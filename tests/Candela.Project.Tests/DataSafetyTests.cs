using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Candela.Assets;
using Candela.Core.Math;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;

namespace Candela.Project.Tests;

/// <summary>
/// Attempts to lose or corrupt user data through saving and loading: file-system corner cases of the atomic save, text
/// that parses but cannot be written again, hostile nesting, data written by a newer version, and single unreadable values.
/// </summary>
public sealed class DataSafetyTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    // ----- atomic save and the file system -----------------------------------------------------------------------------

    [Fact]
    public void Saving_through_a_symbolic_link_updates_the_file_it_points_to()
    {
        var real = Path.Combine(_dir.Path, "synced");
        Directory.CreateDirectory(real);
        var target = Path.Combine(real, "show.candela");
        ProjectFile.Save(target, new ProjectData(new Scene("v1")));
        var link = _dir.File("link.candela");
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return; // symbolic links need a privilege on some Windows machines
        }

        var project = new ProjectData(new Scene("v2"));
        ProjectFile.Save(link, project);
        Assert.NotNull(new FileInfo(link).LinkTarget); // the link is still a link …
        Assert.Equal("v2", ProjectFile.ReadManifest(target).SceneName); // … and the real file has the new content
        Assert.Equal(Path.GetFullPath(link), project.FilePath);
        Assert.Single(Directory.GetFiles(real));
    }

    [Fact]
    public void Saving_keeps_the_permissions_of_the_file()
    {
        if (OperatingSystem.IsWindows()) return;
        var path = _dir.File("private.candela");
        ProjectFile.Save(path, new ProjectData(new Scene("v1")));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        ProjectFile.Save(path, new ProjectData(new Scene("v2")));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        Assert.Equal("v2", ProjectFile.ReadManifest(path).SceneName);
    }

    [Fact]
    public void A_read_only_project_is_never_overwritten()
    {
        var path = _dir.File("archived.candela");
        ProjectFile.Save(path, new ProjectData(new Scene("v1")));
        var before = File.ReadAllBytes(path);
        var attributes = File.GetAttributes(path);
        File.SetAttributes(path, attributes | FileAttributes.ReadOnly);
        try
        {
            var project = new ProjectData(new Scene("v2"));
            var e = Assert.Throws<UnauthorizedAccessException>(() => ProjectFile.Save(path, project));
            Assert.Contains("read-only", e.Message);
            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.Null(project.FilePath);
            Assert.Single(Directory.GetFiles(_dir.Path));
        }
        finally
        {
            File.SetAttributes(path, attributes);
        }
    }

    [Fact]
    public void Every_file_name_the_file_system_accepts_can_be_saved()
    {
        // 120 Cyrillic letters are 240 bytes in UTF-8: a valid name, but too long once the temporary name decorates it.
        var path = _dir.File(new string('Ф', 120) + ".candela");
        ProjectFile.Save(path, new ProjectData(new Scene("first")));
        ProjectFile.Save(path, new ProjectData(new Scene("second")));
        Assert.Equal("second", ProjectFile.ReadManifest(path).SceneName);
        Assert.Single(Directory.GetFiles(_dir.Path));
    }

    // ----- text that parses but cannot be written again ----------------------------------------------------------------

    [Fact]
    public void Invalid_text_in_the_json_documents_is_repaired_instead_of_failing_the_load()
    {
        var project = TestData.RichProject();
        var path = _dir.File("text.candela");
        ProjectFile.Save(path, project, new ProjectSaveOptions { GdtfProvider = TestData.LibraryGdtf });
        var spot = project.Scene.All().Single(e => e.Name == "Spot 1");
        byte[] invalidUtf8 = { 0xC3, 0x28 };
        TestData.RewriteArchive(path, (name, data) => name switch
        {
            // A lone surrogate escape in a value and in a property name, and bytes that are not UTF-8.
            "manifest.json" => Replace(data, "\"sceneName\": \"Rich stage\"", "\"sceneName\": \"Rich \\ud800stage\""),
            "scene.json" => Concat(Replace(data, "\"name\": \"Spot 1\"", "\"name\": \"Spot \\udc00"), invalidUtf8, "1\""),
            "settings.json" => Replace(data, "\"layout\":", "\"\\ud83d\": 1, \"layout\":"),
            "assets/index.json" => Replace(data, "\"fileName\": \"plan.png\"", "\"fileName\": \"plan\\ud800.png\""),
            _ => data,
        });

        var loaded = ProjectFile.Load(path);
        Assert.Contains(loaded.LoadIssues, i => i.Code == ProjectIssueCodes.TextRepaired && i.Message.Contains("scene.json"));
        Assert.Contains(loaded.LoadIssues, i => i.Code == ProjectIssueCodes.TextRepaired && i.Message.Contains("manifest.json"));
        Assert.Equal("Rich \uFFFDstage", loaded.Manifest!.SceneName);
        Assert.Equal("Spot \uFFFD\uFFFD(1", loaded.Scene.Find(spot.Id)!.Name); // lone surrogate, then C3 without its continuation byte
        Assert.True(loaded.Settings.ContainsKey("\uFFFD"));
        Assert.Contains(loaded.Assets.Entries, a => a.OriginalFileName == "plan\uFFFD.png");
        Assert.Equal(project.Assets.Count, loaded.Assets.Count);

        // The repaired project saves, and the saved file is clean.
        var again = _dir.File("again.candela");
        ProjectFile.Save(again, loaded);
        var reloaded = ProjectFile.Load(again);
        Assert.DoesNotContain(reloaded.LoadIssues, i => i.Code == ProjectIssueCodes.TextRepaired);
        Assert.Equal(TestData.SceneJson(loaded.Scene), TestData.SceneJson(reloaded.Scene));
        Assert.True(ProjectFile.IsProjectFile(path));
        Assert.Equal("Rich \uFFFDstage", ProjectFile.ReadManifest(path).SceneName);
    }

    [Fact]
    public void Settings_sections_with_unpaired_surrogates_never_block_saving()
    {
        var project = new ProjectData(new Scene("Settings"));
        project.Settings.SetJson("layout", """{"title":"\ud800 half","ok":"\ud83d\ude00"}""");
        using (var doc = JsonDocument.Parse("""{"\udfff":["x\udbff"]}"""))
            project.Settings.SetRaw("view", doc.RootElement);
        project.Settings.Set("typed", new Dictionary<string, string> { ["k"] = "a\uD800b" });

        var path = _dir.File("settings.candela");
        ProjectFile.Save(path, project);
        var loaded = ProjectFile.Load(path);
        Assert.Empty(loaded.LoadIssues);
        Assert.Equal("""{"title":"\uFFFD half","ok":"\uD83D\uDE00"}""", loaded.Settings.GetJson("layout"));
        Assert.Equal("""{"\uFFFD":["x\uFFFD"]}""", loaded.Settings.GetJson("view"));
        Assert.Equal("a\uFFFDb", loaded.Settings.GetOrDefault("typed", new Dictionary<string, string>())["k"]);
    }

    // ----- hostile structure -------------------------------------------------------------------------------------------

    [Fact]
    public void A_parent_chain_deeper_than_the_limit_is_cut_and_keeps_world_positions()
    {
        const int depth = 25_000; // deep enough to overflow the stack of Core's recursive tree walks
        var json = new StringBuilder("""{"formatVersion":1,"name":"Deep","entities":[""");
        var ids = new Guid[depth];
        for (int i = 0; i < depth; i++)
        {
            ids[i] = Guid.NewGuid();
            if (i > 0) json.Append(',');
            json.Append($$"""{"id":"{{ids[i]}}","name":"e{{i}}","transform":{"position":[0.01,0,0]}""");
            if (i > 0) json.Append($$""","parent":"{{ids[i - 1]}}" """);
            json.Append('}');
        }
        json.Append("]}");
        var bytes = TestData.Zip(("scene.json", TestData.Utf8(json.ToString())));

        // Loading may run on a background thread with a small stack.
        ProjectData? loaded = null;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                loaded = ProjectFile.Load(new MemoryStream(bytes));
            }
            catch (Exception e)
            {
                failure = e;
            }
        }, 1024 * 1024);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
        Assert.NotNull(loaded);
        Assert.Equal(depth, loaded.Scene.Count);
        Assert.Contains(loaded.LoadIssues, i => i.Code == ProjectIssueCodes.SceneTooDeep);
        Assert.Equal(depth / 1001 + 1, loaded.Scene.Roots.Count);

        // The first entity cut from the chain is a root now, still at its place in the room.
        var cut = loaded.Scene.Find(ids[1001])!;
        Assert.Null(cut.Parent);
        Assert.True(Vector3.Distance(new Vector3(0.01f * 1002, 0, 0), cut.WorldPosition) < 1e-3f, cut.WorldPosition.ToString());
        Assert.Same(cut, loaded.Scene.Find(ids[1002])!.Parent);
    }

    // ----- data written by a newer version -----------------------------------------------------------------------------

    [Fact]
    public void Components_of_unknown_types_survive_opening_and_saving()
    {
        var project = new ProjectData(new Scene("Future"));
        var laser = new Entity("Laser");
        laser.Add(new WallComponent { Width = 2 });
        project.Scene.Add(laser);
        var other = new Entity("Other");
        project.Scene.Add(other);
        var path = _dir.File("future.candela");
        ProjectFile.Save(path, project);
        const string Unknown = """{"$type":"laserProjector","powerW":5,"zones":[{"name":"a","points":[[0,1],[1,0]]}]}""";
        AddComponent(path, laser.Id, Unknown);
        AddComponent(path, other.Id, """{"$type":"pyroCue","delayS":1.5}""");

        var loaded = ProjectFile.Load(path);
        Assert.Equal(2, loaded.LoadIssues.Count(i => i.Code == ProjectIssueCodes.SceneComponentUnknown));
        Assert.Single(loaded.Scene.Find(laser.Id)!.Components); // the wall
        Assert.Equal(Unknown, Assert.Single(loaded.UnknownComponents[laser.Id]).GetRawText());

        // Saved again (after an unrelated edit, and with one entity deleted): the component is written back unchanged.
        loaded.Scene.Find(laser.Id)!.Name = "Laser renamed";
        loaded.Scene.Remove(loaded.Scene.Find(other.Id)!);
        var again = _dir.File("again.candela");
        ProjectFile.Save(again, loaded);
        var scene = JsonNode.Parse(TestData.ReadEntry(again, "scene.json"))!;
        var entity = scene["entities"]!.AsArray().Single(e => e!["id"]!.GetValue<string>() == laser.Id.ToString("D"))!;
        var components = entity["components"]!.AsArray();
        Assert.Equal(2, components.Count);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Unknown), components[1]));
        Assert.DoesNotContain("pyroCue", Encoding.UTF8.GetString(TestData.ReadEntry(again, "scene.json")));

        var reloaded = ProjectFile.Load(again);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Unknown), JsonNode.Parse(Assert.Single(reloaded.UnknownComponents[laser.Id]).GetRawText())));
        Assert.Equal("Laser renamed", reloaded.Scene.Find(laser.Id)!.Name);
    }

    [Fact]
    public void Assets_this_version_cannot_load_are_kept_verbatim()
    {
        var project = TestData.RichProject();
        var path = _dir.File("assets.candela");
        ProjectFile.Save(path, project, new ProjectSaveOptions { GdtfProvider = TestData.LibraryGdtf });
        var image = project.Assets.Entries.Single(a => a.Kind == AssetKind.Image);
        var texture = project.Assets.Entries.Single(a => a.Kind == AssetKind.Texture);
        var notAnImage = TestData.Utf8("a picture format of the future");
        var extra = $"assets/{image.Id:D}/frames/0001.bin";
        TestData.RewriteArchive(path, (name, data) =>
        {
            if (name == $"assets/{texture.Id:D}/brick.png") return notAnImage;
            if (name != "assets/index.json") return data;
            var index = JsonNode.Parse(data)!.AsObject();
            foreach (var record in index["assets"]!.AsArray().Select(n => n!.AsObject()))
            {
                if (record["id"]!.GetValue<string>() == image.Id.ToString("D"))
                {
                    record["kind"] = "Video"; // a kind a newer version added
                    record["frameRate"] = 25;
                }
                if (record["id"]!.GetValue<string>() == texture.Id.ToString("D"))
                {
                    record["size"] = notAnImage.Length; // intact, but not content this version accepts
                    record["sha256"] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(notAnImage)).ToLowerInvariant();
                }
            }
            return TestData.Utf8(index.ToJsonString());
        }, (extra, new byte[] { 1, 2, 3, 4 }));

        var loaded = ProjectFile.Load(path);
        Assert.Equal(2, loaded.LoadIssues.Count(i => i.Code == ProjectIssueCodes.AssetKept));
        var poster = project.Scene.Query<ImageComponent>().Single().Entity;
        Assert.Contains(loaded.LoadIssues, i => i.Code == ProjectIssueCodes.AssetKept && i.Message.Contains("Video") && i.Entities.Contains(poster.Id));
        Assert.Equal(new[] { image.Id, texture.Id }.Order(), loaded.KeptAssetIds.Order());
        Assert.False(loaded.Assets.Contains(image.Id));
        Assert.Equal(project.Assets.Count - 2, loaded.Assets.Count);

        // Saving writes them back exactly: every file of their folders and their records.
        var again = _dir.File("again.candela");
        var result = ProjectFile.Save(again, loaded);
        Assert.DoesNotContain(result.Issues, i => i.Code == ProjectIssueCodes.SaveAssetMissing);
        Assert.Equal(project.Assets.Count, result.Manifest.AssetCount);
        foreach (var name in TestData.EntryNames(path).Where(n => n.StartsWith($"assets/{image.Id:D}/") || n.StartsWith($"assets/{texture.Id:D}/")))
            Assert.Equal(TestData.ReadEntry(path, name), TestData.ReadEntry(again, name));
        Assert.Contains(extra, TestData.EntryNames(again));
        JsonNode RecordOf(string archive) => JsonNode.Parse(TestData.ReadEntry(archive, "assets/index.json"))!["assets"]!.AsArray()
            .Select(n => n!).Single(n => n["id"]!.GetValue<string>() == image.Id.ToString("D"));
        Assert.True(JsonNode.DeepEquals(RecordOf(path), RecordOf(again)));

        var reloaded = ProjectFile.Load(again);
        Assert.Equal(loaded.KeptAssetIds.Order(), reloaded.KeptAssetIds.Order());
        Assert.Equal(loaded.LoadIssues.Select(i => i.Code).Order(), reloaded.LoadIssues.Select(i => i.Code).Order());
    }

    [Fact]
    public void A_damaged_unknown_asset_is_reported_not_kept()
    {
        var project = TestData.RichProject();
        var path = _dir.File("damaged.candela");
        ProjectFile.Save(path, project, new ProjectSaveOptions { GdtfProvider = TestData.LibraryGdtf });
        var image = project.Assets.Entries.Single(a => a.Kind == AssetKind.Image);
        TestData.RewriteArchive(path, (name, data) =>
        {
            if (name == $"assets/{image.Id:D}/plan.png")
            {
                var copy = (byte[])data.Clone();
                copy[^1] ^= 0xFF;
                return copy;
            }
            return name == "assets/index.json" ? Replace(data, "\"kind\": \"Image\"", "\"kind\": \"Video\"") : data;
        });
        var loaded = ProjectFile.Load(path);
        Assert.Empty(loaded.KeptAssetIds);
        Assert.Contains(loaded.LoadIssues, i => i.Code == ProjectIssueCodes.AssetCorrupt && i.Message.Contains("checksum"));
    }

    [Fact]
    public void An_asset_whose_record_lost_its_id_is_recovered_from_its_folder()
    {
        var project = TestData.RichProject();
        var path = _dir.File("record.candela");
        ProjectFile.Save(path, project, new ProjectSaveOptions { GdtfProvider = TestData.LibraryGdtf });
        var image = project.Assets.Entries.Single(a => a.Kind == AssetKind.Image);
        TestData.RewriteArchive(path, (name, data) =>
            name == "assets/index.json" ? Replace(data, $"\"id\": \"{image.Id:D}\"", "\"id\": 12") : data);

        var loaded = ProjectFile.Load(path);
        Assert.Contains(loaded.LoadIssues, i => i.Code == ProjectIssueCodes.AssetRecordInvalid);
        var recovered = Assert.Single(loaded.LoadIssues, i => i.Code == ProjectIssueCodes.AssetIndexInvalid);
        Assert.Contains("plan.png", recovered.Message);
        Assert.Equal(image.Data, loaded.Assets.GetBytes(image.Id));
        Assert.Equal(project.Assets.Count, loaded.Assets.Count);
    }

    [Fact]
    public void A_damaged_library_file_never_replaces_the_copy_kept_in_the_project()
    {
        var project = new ProjectData(new Scene("Library"));
        var fixture = new Entity("Fog");
        fixture.Add(new FixtureComponent { GdtfSpec = TestData.GdtfA });
        project.Scene.Add(fixture);
        project.GdtfFiles[TestData.GdtfA] = TestData.LibraryGdtf(TestData.GdtfA)!;

        var path = _dir.File("library.candela");
        var result = ProjectFile.Save(path, project, new ProjectSaveOptions { GdtfProvider = _ => TestData.Utf8("half a download") });
        Assert.Equal(new[] { TestData.GdtfA }, result.StoredGdtfs);
        var issue = Assert.Single(result.Issues);
        Assert.Equal(ProjectIssueCodes.SaveGdtfInvalid, issue.Code);
        Assert.Contains("copy kept in the project", issue.Message);
        Assert.Equal(TestData.LibraryGdtf(TestData.GdtfA), ProjectFile.Load(path).GdtfFiles[TestData.GdtfA]);
    }

    // ----- single unreadable values ------------------------------------------------------------------------------------

    [Fact]
    public void An_unreadable_value_is_reset_and_the_rest_of_its_object_is_kept()
    {
        var project = TestData.RichProject();
        var path = _dir.File("values.candela");
        ProjectFile.Save(path, project, new ProjectSaveOptions { GdtfProvider = TestData.LibraryGdtf });
        var cableEntity = project.Scene.All().Single(e => e.Name == "DMX run");
        var cable = cableEntity.Get<CableComponent>()!;
        var wall = project.Scene.All().Single(e => e.Name == "Back wall");
        TestData.RewriteArchive(path, (name, data) =>
        {
            if (name != "scene.json") return data;
            var scene = JsonNode.Parse(data)!.AsObject();
            scene["settings"]!["gridSpacing"] = "wide";
            scene["layers"]![1]!["visible"] = "sometimes";
            foreach (var e in scene["entities"]!.AsArray().Select(n => n!.AsObject()))
            {
                var id = Guid.Parse(e["id"]!.GetValue<string>());
                if (id == cableEntity.Id) e["components"]![0]!["nodes"]![2]!["anchor"]!["entityId"] = "not an id";
                if (id == wall.Id) e["components"]![0]!["kind"] = "Curtain";
            }
            return TestData.Utf8(scene.ToJsonString());
        });

        var loaded = ProjectFile.Load(path);
        var loadedCable = loaded.Scene.Find(cableEntity.Id)!.Get<CableComponent>()!;
        Assert.Equal(cable.Nodes.Select(n => n.Id), loadedCable.Nodes.Select(n => n.Id));
        Assert.Equal(cable.Segments, loadedCable.Segments);
        Assert.Equal(3, loadedCable.Endpoints.Count);
        Assert.Equal(Guid.Empty, loadedCable.Nodes[2].Anchor!.Value.EntityId); // only the unreadable id was reset
        Assert.Equal(cable.Nodes[2].Anchor!.Value.LocalOffset, loadedCable.Nodes[2].Anchor!.Value.LocalOffset);
        Assert.Equal(cable.Nodes[0].Anchor, loadedCable.Nodes[0].Anchor);

        var loadedWall = loaded.Scene.Find(wall.Id)!.Get<WallComponent>()!;
        Assert.Equal(WallKind.Wall, loadedWall.Kind);
        Assert.Equal(12f, loadedWall.Width);
        Assert.Equal(MaterialIds.StageFloorBlack, loadedWall.MaterialId);

        Assert.Equal(1f, loaded.Scene.Settings.GridSpacing);
        Assert.Equal(0.035f, loaded.Scene.Settings.BaseHazeScattering);
        Assert.Equal(project.Scene.Layers.Select(l => l.Id), loaded.Scene.Layers.Select(l => l.Id));
        Assert.True(loaded.Scene.Layers[1].Locked);

        var repaired = loaded.LoadIssues.Where(i => i.Code == ProjectIssueCodes.SceneValueRepaired).ToList();
        Assert.Contains(repaired, i => i.Message.Contains("$.nodes[2].anchor.entityId") && i.Entities.SequenceEqual(new[] { cableEntity.Id }));
        Assert.Contains(repaired, i => i.Message.Contains("$.kind") && i.Entities.SequenceEqual(new[] { wall.Id }));
        Assert.Contains(repaired, i => i.Message.Contains("$.visible"));
        Assert.Contains(loaded.LoadIssues, i => i.Code == ProjectIssueCodes.SceneSettingsInvalid && i.Message.Contains("$.gridSpacing"));
        Assert.DoesNotContain(loaded.LoadIssues, i => i.Code == ProjectIssueCodes.SceneComponentInvalid);
    }

    [Fact]
    public void A_null_that_differs_from_the_default_survives_saving_and_merging()
    {
        // ProtocolId = null means "no protocol input" (offline programming); its default is Art-Net.
        var project = new ProjectData(new Scene("Offline"));
        var desk = new Entity("Desk");
        desk.Add(new ConsoleComponent { ProtocolId = null });
        project.Scene.Add(desk);

        var path = _dir.File("offline.candela");
        ProjectFile.Save(path, project);
        Assert.Contains("\"protocolId\": null", Encoding.UTF8.GetString(TestData.ReadEntry(path, "scene.json")));
        Assert.DoesNotContain("\"label\": null", Encoding.UTF8.GetString(TestData.ReadEntry(path, "scene.json"))); // default-null values stay omitted
        var loaded = ProjectFile.Load(path);
        Assert.Null(loaded.Scene.Find(desk.Id)!.Get<ConsoleComponent>()!.ProtocolId);
        Assert.Equal(TestData.SceneJson(project.Scene), TestData.SceneJson(loaded.Scene));

        var merged = ProjectMerge.Import(loaded, loaded);
        Assert.Null(loaded.Scene.Find(merged.EntityIds[desk.Id])!.Get<ConsoleComponent>()!.ProtocolId);

        // A console that does use a protocol keeps it, of course.
        desk.Get<ConsoleComponent>()!.ProtocolId = "sacn";
        ProjectFile.Save(path, project);
        Assert.Equal("sacn", ProjectFile.Load(path).Scene.Find(desk.Id)!.Get<ConsoleComponent>()!.ProtocolId);
    }

    [Fact]
    public void Odd_values_and_names_round_trip()
    {
        var project = new ProjectData(new Scene("Odd \"quotes\" / slashes \\ and ünïcödé 🎭"));
        var e = new Entity("NaN fixture\t\n\0");
        e.Add(new FixtureComponent
        {
            GdtfSpec = "Robe/Robin:T1.gdtf",
            ManualAttributes = { ["Pan"] = float.NaN, ["Tilt"] = float.PositiveInfinity, ["Zoom"] = float.MinValue },
            FocusPoint = new Vector3(float.NaN, float.NegativeInfinity, 2),
        });
        e.Local = new Transform(new Vector3(1e-30f, 3.4e38f, -0f));
        project.Scene.Add(e);
        var upper = new Entity("Upper");
        upper.Add(new FixtureComponent { GdtfSpec = TestData.GdtfA.ToUpperInvariant() });
        project.Scene.Add(upper);
        var exact = new Entity("Exact");
        exact.Add(new FixtureComponent { GdtfSpec = TestData.GdtfA });
        project.Scene.Add(exact);
        project.Settings.SetJson("$type", "1");
        project.Settings.SetJson("a.b/c['d']", """[1,2,{"x":null}]""");
        project.Settings.Set<object?>("nothing", null);

        byte[]? Provider(string name) => name == TestData.GdtfA ? TestData.LibraryGdtf(TestData.GdtfA) : TestData.LibraryGdtf(TestData.GdtfB);
        var path = _dir.File("odd.candela");
        var result = ProjectFile.Save(path, project, new ProjectSaveOptions { GdtfProvider = Provider });
        Assert.Empty(result.Issues);
        var loaded = ProjectFile.Load(path);
        Assert.Empty(loaded.LoadIssues);
        Assert.Equal(TestData.SceneJson(project.Scene), TestData.SceneJson(loaded.Scene));
        foreach (var spec in project.UsedGdtfSpecs()) Assert.Equal(Provider(spec), loaded.GdtfFiles[spec]);
        foreach (var key in project.Settings.Keys) Assert.Equal(project.Settings.GetJson(key), loaded.Settings.GetJson(key));

        // Saved again without the library: every copy, under its exact name.
        var again = _dir.File("again.candela");
        ProjectFile.Save(again, loaded);
        var reloaded = ProjectFile.Load(again);
        foreach (var spec in project.UsedGdtfSpecs()) Assert.Equal(Provider(spec), reloaded.GdtfFiles[spec]);
    }

    private static void AddComponent(string path, Guid entityId, string componentJson) =>
        TestData.RewriteArchive(path, (name, data) =>
        {
            if (name != "scene.json") return data;
            var scene = JsonNode.Parse(data)!.AsObject();
            var entity = scene["entities"]!.AsArray().Single(e => e!["id"]!.GetValue<string>() == entityId.ToString("D"))!;
            entity["components"]!.AsArray().Add(JsonNode.Parse(componentJson));
            return TestData.Utf8(scene.ToJsonString());
        });

    private static byte[] Replace(byte[] data, string find, string replace)
    {
        var text = Encoding.UTF8.GetString(data);
        Assert.Contains(find, text);
        return Encoding.UTF8.GetBytes(text.Replace(find, replace));
    }

    private static byte[] Concat(byte[] data, byte[] raw, string tail)
    {
        // Appends raw bytes and a tail right after the first occurrence of the (already replaced) unterminated name.
        var text = Encoding.UTF8.GetString(data);
        int at = text.IndexOf("\"Spot \\udc00", StringComparison.Ordinal) + "\"Spot \\udc00".Length;
        var head = Encoding.UTF8.GetBytes(text[..at]);
        var rest = Encoding.UTF8.GetBytes(tail + text[at..]);
        return [.. head, .. raw, .. rest];
    }
}
