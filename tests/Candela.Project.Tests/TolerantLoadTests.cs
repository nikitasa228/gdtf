using System.Text.Json.Nodes;
using Candela.Assets;
using Candela.Core.Diagnostics;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;

namespace Candela.Project.Tests;

public sealed class TolerantLoadTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly ProjectData _project = TestData.RichProject();
    private readonly string _path;

    public TolerantLoadTests()
    {
        _path = _dir.File("rich.candela");
        ProjectFile.Save(_path, _project, new ProjectSaveOptions { GdtfProvider = TestData.LibraryGdtf });
    }

    public void Dispose() => _dir.Dispose();

    private AssetEntry Image => _project.Assets.Entries.Single(a => a.Kind == AssetKind.Image);

    private AssetEntry Model => _project.Assets.Entries.Single(a => a.Kind == AssetKind.Model);

    [Fact]
    public void Corrupted_asset_entry_becomes_an_issue_and_the_rest_loads()
    {
        var damaged = $"assets/{Image.Id:D}/plan.png";
        TestData.RewriteArchive(_path, (name, data) =>
        {
            if (name != damaged) return data;
            var copy = (byte[])data.Clone();
            copy[copy.Length / 2] ^= 0xFF;
            return copy;
        });

        var loaded = ProjectFile.Load(_path);
        var issue = Assert.Single(loaded.LoadIssues);
        Assert.Equal(ProjectIssueCodes.AssetCorrupt, issue.Code);
        Assert.Equal(IssueSeverity.Error, issue.Severity);
        Assert.Contains("plan.png", issue.Message);
        var poster = loaded.Scene.Query<ImageComponent>().Single();
        Assert.Equal(new[] { poster.Entity.Id }, issue.Entities);

        // Everything else is intact.
        Assert.False(loaded.Assets.Contains(Image.Id));
        Assert.Equal(_project.Assets.Count - 1, loaded.Assets.Count);
        Assert.Equal(TestData.SceneJson(_project.Scene), TestData.SceneJson(loaded.Scene));
        Assert.Equal(2, loaded.GdtfFiles.Count);
        Assert.Equal(3, loaded.Settings.Count);
        Assert.NotNull(loaded.Thumbnail);
    }

    [Fact]
    public void Damaged_compressed_data_is_reported_per_entry()
    {
        // Replace the image by bytes whose deflate stream cannot be decoded: write the archive by hand.
        var damaged = $"assets/{Image.Id:D}/plan.png";
        var bytes = File.ReadAllBytes(_path);
        var entries = new List<(string, byte[])>();
        using (var zip = System.IO.Compression.ZipFile.OpenRead(_path))
        {
            foreach (var e in zip.Entries)
            {
                using var s = e.Open();
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                entries.Add((e.FullName, e.FullName == damaged ? new byte[4096] : ms.ToArray()));
            }
        }
        // A deflated run of zeros compresses well; corrupt its compressed bytes in place.
        var archive = TestData.Zip(entries.ToArray());
        int at = IndexOf(archive, System.Text.Encoding.UTF8.GetBytes(damaged)) + damaged.Length;
        for (int i = at + 2; i < at + 12; i++) archive[i] = 0xFF;
        File.WriteAllBytes(_path, archive);
        _ = bytes;

        var loaded = ProjectFile.Load(_path);
        var issue = Assert.Single(loaded.LoadIssues);
        Assert.Equal(ProjectIssueCodes.AssetCorrupt, issue.Code);
        Assert.Equal(_project.Assets.Count - 1, loaded.Assets.Count);
        Assert.Equal(_project.Scene.Count, loaded.Scene.Count);
    }

    [Fact]
    public void Missing_asset_file_and_damaged_companion_are_reported()
    {
        TestData.RewriteArchive(_path, (name, data) =>
            name == $"assets/{Image.Id:D}/plan.png" ? null :
            name == $"assets/{Model.Id:D}/tex/wood.png" ? new byte[] { 1, 2, 3 } : data);

        var loaded = ProjectFile.Load(_path);
        Assert.Contains(loaded.LoadIssues, i => i.Code == ProjectIssueCodes.AssetMissing && i.Message.Contains("plan.png"));
        var companion = Assert.Single(loaded.LoadIssues, i => i.Code == ProjectIssueCodes.AssetCompanionMissing);
        Assert.Contains("tex/wood.png", companion.Message);
        Assert.Equal(2, companion.Entities.Count); // the mesh and the truss use the model
        var model = loaded.Assets.Find(Model.Id);
        Assert.NotNull(model);
        Assert.Equal(new[] { "chair.mtl" }, model.CompanionFiles.Keys);
    }

    [Fact]
    public void Damaged_asset_index_is_recovered_from_the_folders()
    {
        TestData.RewriteArchive(_path, (name, data) => name == "assets/index.json" ? TestData.Utf8("{ \"assets\": [ { \"id\": ") : data);

        var loaded = ProjectFile.Load(_path);
        var issue = Assert.Single(loaded.LoadIssues);
        Assert.Equal(ProjectIssueCodes.AssetIndexInvalid, issue.Code);
        Assert.Equal(_project.Assets.Count, loaded.Assets.Count);
        foreach (var a in _project.Assets.Entries)
        {
            var b = loaded.Assets.Find(a.Id);
            Assert.NotNull(b);
            Assert.Equal(a.Data, b.Data);
            Assert.Equal(a.OriginalFileName, b.OriginalFileName);
        }
        Assert.Equal(AssetKind.Model, loaded.Assets.Find(Model.Id)!.Kind);
        Assert.Equal(2, loaded.Assets.Find(Model.Id)!.CompanionFiles.Count);
        Assert.Equal(AssetKind.Gdtf, loaded.Assets.Entries.Single(a => a.OriginalFileName == TestData.GdtfA).Kind);
    }

    [Fact]
    public void Invalid_asset_records_are_skipped_individually()
    {
        TestData.RewriteArchive(_path, (name, data) =>
        {
            if (name != "assets/index.json") return data;
            var index = JsonNode.Parse(data)!.AsObject();
            var assets = index["assets"]!.AsArray();
            assets.Add(JsonNode.Parse("""{"id":"not-a-guid","kind":"Model"}"""));
            assets.Add(JsonNode.Parse("""{"id":"0f8fad5b-d9cb-469f-a165-70867728950e","kind":"Hologram","fileName":"x.holo","entry":"assets/x"}"""));
            assets.Add(JsonNode.Parse("42"));
            return TestData.Utf8(index.ToJsonString());
        });
        var loaded = ProjectFile.Load(_path);
        Assert.Equal(2, loaded.LoadIssues.Count(i => i.Code == ProjectIssueCodes.AssetRecordInvalid));
        // The unknown "Hologram" kind would be kept verbatim, but its file is not in the archive.
        Assert.Single(loaded.LoadIssues, i => i.Code == ProjectIssueCodes.AssetMissing && i.Message.Contains("Hologram"));
        Assert.Empty(loaded.KeptAssetIds);
        Assert.Equal(_project.Assets.Count, loaded.Assets.Count);
    }

    [Fact]
    public void Corrupted_gdtf_entry_is_reported_and_the_other_gdtf_loads()
    {
        TestData.RewriteArchive(_path, (name, data) => name == "fixtures/" + TestData.GdtfB ? TestData.Utf8("not a zip") : data);
        var loaded = ProjectFile.Load(_path);
        var corrupt = Assert.Single(loaded.LoadIssues, i => i.Code == ProjectIssueCodes.GdtfCorrupt);
        Assert.Equal(IssueSeverity.Error, corrupt.Severity);
        var spot = loaded.Scene.All().Single(e => e.Name == "Spot 1");
        Assert.Equal(new[] { spot.Id }, corrupt.Entities);
        Assert.Contains(loaded.LoadIssues, i => i.Code == ProjectIssueCodes.GdtfNotEmbedded && i.Entities.Contains(spot.Id));
        Assert.Equal(new[] { TestData.GdtfA }, loaded.GdtfFiles.Keys);
    }

    [Fact]
    public void Missing_fixture_index_is_recovered_from_the_folder()
    {
        TestData.RewriteArchive(_path, (name, data) => name == "fixtures/index.json" ? null : data);
        var loaded = ProjectFile.Load(_path);
        Assert.Single(loaded.LoadIssues, i => i.Code == ProjectIssueCodes.GdtfIndexInvalid);
        Assert.Equal(new[] { TestData.GdtfA, TestData.GdtfB }, loaded.GdtfFiles.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Damaged_settings_and_thumbnail_fall_back_to_defaults()
    {
        TestData.RewriteArchive(_path, (name, data) => name switch
        {
            "settings.json" => TestData.Utf8("[1, 2"),
            "thumbnail.png" => TestData.Utf8("GIF89a"),
            _ => data,
        });
        var loaded = ProjectFile.Load(_path);
        Assert.Contains(loaded.LoadIssues, i => i.Code == ProjectIssueCodes.SettingsInvalid);
        Assert.Contains(loaded.LoadIssues, i => i.Code == ProjectIssueCodes.ThumbnailInvalid);
        Assert.Empty(loaded.Settings);
        Assert.Null(loaded.Thumbnail);
        Assert.Equal(_project.Assets.Count, loaded.Assets.Count);
    }

    [Fact]
    public void Damaged_scene_elements_are_dropped_one_by_one()
    {
        var spot = _project.Scene.All().Single(e => e.Name == "Spot 1");
        var console = _project.Scene.All().Single(e => e.Name == "Console");
        var atmosphere = _project.Scene.All().Single(e => e.Name == "Atmosphere");
        TestData.RewriteArchive(_path, (name, data) =>
        {
            if (name != "scene.json") return data;
            var scene = JsonNode.Parse(data)!.AsObject();
            var entities = scene["entities"]!.AsArray();
            foreach (var e in entities.Select(n => n!.AsObject()))
            {
                var id = Guid.Parse(e["id"]!.GetValue<string>());
                if (id == spot.Id) e["components"]!.AsArray()[0]!["addresses"] = "not a list";
                if (id == console.Id)
                {
                    e["components"]!.AsArray().Add(JsonNode.Parse("""{"$type":"laserProjector","power":5}"""));
                    e["components"]!.AsArray().Add(JsonNode.Parse("""{"$type":17,"model":"no type"}"""));
                }
                if (id == atmosphere.Id) e["transform"] = JsonNode.Parse("""{"position":[1,"NaN",3]}""");
            }
            entities.Add(JsonNode.Parse("17"));
            entities.Add(JsonNode.Parse($$"""{"id":"{{console.Id}}","name":"Duplicate"}"""));
            entities.Add(JsonNode.Parse($$"""{"id":"{{Guid.NewGuid()}}","name":"Orphan","parent":"{{Guid.NewGuid()}}"}"""));
            var a = Guid.NewGuid();
            var b = Guid.NewGuid();
            entities.Add(JsonNode.Parse($$"""{"id":"{{a}}","name":"Cycle A","parent":"{{b}}"}"""));
            entities.Add(JsonNode.Parse($$"""{"id":"{{b}}","name":"Cycle B","parent":"{{a}}"}"""));
            entities.Add(JsonNode.Parse($$"""{"id":"{{Guid.NewGuid()}}","name":"Nulls","components":[{"$type":"fixture","gdtfSpec":null,"addresses":null,"manualAttributes":null},{"width":3.5,"$type":"wall"}]}"""));
            return TestData.Utf8(scene.ToJsonString());
        });

        var loaded = ProjectFile.Load(_path);
        var codes = loaded.LoadIssues.Select(i => i.Code).ToList();
        Assert.Contains(ProjectIssueCodes.SceneComponentInvalid, codes);
        Assert.Contains(ProjectIssueCodes.SceneComponentUnknown, codes);
        Assert.Contains(ProjectIssueCodes.SceneTransformInvalid, codes);
        Assert.Contains(ProjectIssueCodes.SceneEntityInvalid, codes);
        Assert.Contains(ProjectIssueCodes.SceneIdInvalid, codes);
        Assert.Contains(ProjectIssueCodes.SceneParentMissing, codes);
        Assert.Contains(ProjectIssueCodes.SceneParentCycle, codes);
        Assert.Contains(ProjectIssueCodes.SceneValueRepaired, codes);

        Assert.Equal(_project.Scene.Count + 5, loaded.Scene.Count);
        // One unreadable value costs that value, not the component: the patch is reset, the rest of the fixture is kept.
        var spotFixture = loaded.Scene.Find(spot.Id)!.Get<FixtureComponent>()!;
        Assert.Empty(spotFixture.Addresses);
        Assert.Equal(TestData.GdtfB, spotFixture.GdtfSpec);
        Assert.Equal(101, spotFixture.FixtureId);
        Assert.Equal(spot.Get<FixtureComponent>()!.MountedOn, spotFixture.MountedOn);
        Assert.Contains(loaded.LoadIssues, i => i.Code == ProjectIssueCodes.SceneValueRepaired && i.Message.Contains("$.addresses") &&
                                                i.Entities.SequenceEqual(new[] { spot.Id }));
        Assert.Single(loaded.Scene.Find(console.Id)!.Components);
        Assert.Equal(Core.Math.Transform.Identity, loaded.Scene.Find(atmosphere.Id)!.Local);
        Assert.Contains(loaded.Scene.Roots, e => e.Name == "Orphan");
        Assert.Contains(loaded.Scene.Roots, e => e.Name is "Cycle A" or "Cycle B");
        var nulls = loaded.Scene.All().Single(e => e.Name == "Nulls").Get<FixtureComponent>()!;
        Assert.Equal("", nulls.GdtfSpec);
        Assert.Empty(nulls.Addresses);
        Assert.Empty(nulls.ManualAttributes);
        Assert.Equal(3.5f, loaded.Scene.All().Single(e => e.Name == "Nulls").Get<WallComponent>()!.Width);
        Assert.Contains(loaded.Scene.All(), e => e.Name == "Duplicate" && e.Id != console.Id);
    }

    [Fact]
    public void Thousands_of_broken_elements_produce_a_capped_issue_list()
    {
        TestData.RewriteArchive(_path, (name, data) =>
        {
            if (name != "scene.json") return data;
            var scene = JsonNode.Parse(data)!.AsObject();
            var entities = scene["entities"]!.AsArray();
            for (int i = 0; i < 3000; i++) entities.Add(JsonNode.Parse("null"));
            return TestData.Utf8(scene.ToJsonString());
        });
        var loaded = ProjectFile.Load(_path, new ProjectLoadOptions { MaxIssuesPerCode = 20 });
        Assert.Equal(20, loaded.LoadIssues.Count(i => i.Code == ProjectIssueCodes.SceneEntityInvalid));
        var more = Assert.Single(loaded.LoadIssues, i => i.Code == ProjectIssueCodes.MoreIssues);
        Assert.Contains("2980", more.Message);
        Assert.Equal(_project.Scene.Count, loaded.Scene.Count);
    }

    [Fact]
    public void Entries_over_the_size_limit_are_reported_not_allocated()
    {
        var loaded = ProjectFile.Load(_path, new ProjectLoadOptions { MaxEntryBytes = 32 * 1024 });
        Assert.Contains(loaded.LoadIssues, i => i.Code == ProjectIssueCodes.GdtfCorrupt && i.Message.Contains("too large"));
        Assert.Equal(_project.Scene.Count, loaded.Scene.Count);
    }

    [Fact]
    public void Missing_manifest_is_tolerated()
    {
        TestData.RewriteArchive(_path, (name, data) => name == "manifest.json" ? null : data);
        var loaded = ProjectFile.Load(_path);
        Assert.Single(loaded.LoadIssues, i => i.Code == ProjectIssueCodes.ManifestMissing);
        Assert.Equal(TestData.SceneJson(_project.Scene), TestData.SceneJson(loaded.Scene));
        Assert.Throws<ProjectFormatException>(() => ProjectFile.ReadManifest(_path));
        Assert.False(ProjectFile.IsProjectFile(_path));
    }

    [Fact]
    public void Invalid_manifest_fields_fall_back_to_defaults()
    {
        TestData.RewriteArchive(_path, (name, data) =>
        {
            if (name != "manifest.json") return data;
            var m = JsonNode.Parse(data)!.AsObject();
            m["createdUtc"] = 12;
            m["entityCount"] = "many";
            return TestData.Utf8(m.ToJsonString());
        });
        var loaded = ProjectFile.Load(_path);
        var issue = Assert.Single(loaded.LoadIssues);
        Assert.Equal(ProjectIssueCodes.ManifestInvalid, issue.Code);
        Assert.Contains("createdUtc", issue.Message);
        Assert.Contains("entityCount", issue.Message);
    }

    [Fact]
    public void Unreadable_files_are_refused_with_a_clear_message()
    {
        var notZip = _dir.File("text.candela");
        File.WriteAllText(notZip, "hello");
        Assert.Contains("not a zip", Assert.Throws<ProjectFormatException>(() => ProjectFile.Load(notZip)).Message);

        var truncated = _dir.File("truncated.candela");
        var bytes = File.ReadAllBytes(_path);
        File.WriteAllBytes(truncated, bytes[..(bytes.Length / 2)]);
        Assert.Throws<ProjectFormatException>(() => ProjectFile.Load(truncated));

        var noScene = _dir.File("noscene.candela");
        File.Copy(_path, noScene);
        TestData.RewriteArchive(noScene, (name, data) => name == "scene.json" ? null : data);
        Assert.Contains("scene", Assert.Throws<ProjectFormatException>(() => ProjectFile.Load(noScene)).Message);

        var badScene = _dir.File("badscene.candela");
        File.Copy(_path, badScene);
        TestData.RewriteArchive(badScene, (name, data) => name == "scene.json" ? TestData.Utf8("{\"entities\": [") : data);
        Assert.Contains("scene.json", Assert.Throws<ProjectFormatException>(() => ProjectFile.Load(badScene)).Message);

        var otherFormat = _dir.File("other.candela");
        File.WriteAllBytes(otherFormat, TestData.Zip(("manifest.json", TestData.Utf8("""{"format":"something-else","formatVersion":1}""")),
            ("scene.json", TestData.Utf8("{}"))));
        Assert.Contains("something-else", Assert.Throws<ProjectFormatException>(() => ProjectFile.Load(otherFormat)).Message);

        var empty = _dir.File("empty.candela");
        File.WriteAllBytes(empty, TestData.Zip(("readme.txt", TestData.Utf8("hi"))));
        Assert.Contains("not a Candela project", Assert.Throws<ProjectFormatException>(() => ProjectFile.Load(empty)).Message);

        Assert.Throws<FileNotFoundException>(() => ProjectFile.Load(_dir.File("nope.candela")));
        Assert.Throws<FileNotFoundException>(() => ProjectFile.Load(Path.Combine(_dir.Path, "no-folder", "nope.candela")));
    }

    [Fact]
    public void Minimal_hand_written_archive_loads()
    {
        var path = _dir.File("minimal.candela");
        File.WriteAllBytes(path, TestData.Zip(
            ("manifest.json", TestData.Utf8("""{"format":"candela-project","formatVersion":1}""")),
            ("scene.json", TestData.Utf8("""{"name":"Tiny","entities":[{"id":"8a7d2b4e-1c0f-4f7c-9a55-2b0c1f0e9d11","name":"Box","components":[{"$type":"wall"}]}]}"""))));
        var loaded = ProjectFile.Load(path);
        Assert.Equal("Tiny", loaded.Scene.Name);
        Assert.Single(loaded.Scene.Query<WallComponent>());
        Assert.Contains(loaded.LoadIssues, i => i.Code == ProjectIssueCodes.SettingsInvalid && i.Severity == IssueSeverity.Note);
        Assert.DoesNotContain(loaded.LoadIssues, i => i.Severity >= IssueSeverity.Warning);
        Assert.Equal(loaded.Scene.Id, loaded.Id);
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle)) return i;
        return -1;
    }
}
