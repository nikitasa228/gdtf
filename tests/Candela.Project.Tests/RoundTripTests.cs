using System.Text.Json;
using Candela.Assets;
using Candela.Core.Diagnostics;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;

namespace Candela.Project.Tests;

public sealed class RoundTripTests
{
    [Fact]
    public void Full_round_trip_keeps_scene_assets_gdtfs_settings_and_thumbnail()
    {
        using var dir = new TempDir();
        var original = TestData.RichProject();
        var path = dir.File("show.candela");

        var saved = ProjectFile.Save(path, original, new ProjectSaveOptions { GdtfProvider = TestData.LibraryGdtf, AppVersion = "1.2.3" });
        Assert.Empty(saved.Issues);
        Assert.Equal(new[] { TestData.GdtfB, TestData.GdtfA }, saved.StoredGdtfs);
        Assert.Empty(saved.MissingGdtfs);
        Assert.Equal(Path.GetFullPath(path), original.FilePath);
        Assert.Equal(saved.BytesWritten, new FileInfo(path).Length);

        var loaded = ProjectFile.Load(path);
        Assert.Empty(loaded.LoadIssues);
        Assert.Equal(TestData.SceneJson(original.Scene), TestData.SceneJson(loaded.Scene));
        Assert.Equal(original.Id, loaded.Id);
        Assert.Equal(Path.GetFullPath(path), loaded.FilePath);
        Assert.Equal(original.Thumbnail, loaded.Thumbnail);

        // Every component type of Core is in the scene and survived.
        var types = loaded.Scene.All().SelectMany(e => e.Components).Select(c => c.GetType()).ToHashSet();
        Assert.Equal(ComponentTypes.All.ToHashSet(), types);

        // Assets: same ids, kinds, names, bytes, companions and import options.
        Assert.Equal(original.Assets.Count, loaded.Assets.Count);
        foreach (var a in original.Assets.Entries)
        {
            var b = loaded.Assets.Find(a.Id);
            Assert.NotNull(b);
            Assert.Equal(a.Kind, b.Kind);
            Assert.Equal(a.OriginalFileName, b.OriginalFileName);
            Assert.Equal(a.Data, b.Data);
            Assert.Equal(a.ContentHash, b.ContentHash);
            Assert.Equal(a.SourcePath, b.SourcePath);
            Assert.Equal(a.ImportOptions, b.ImportOptions);
            Assert.Equal(a.CompanionFiles.Keys.Order(), b.CompanionFiles.Keys.Order());
            foreach (var (key, bytes) in a.CompanionFiles) Assert.Equal(bytes, b.CompanionFiles[key]);
        }
        // The model decodes from the loaded bytes, including its texture companion.
        var chair = loaded.Scene.Query<MeshComponent>().Single().Component;
        Assert.True(loaded.Assets.TryGetModel(chair.ModelAssetId, out var model, out var error), error);
        Assert.NotEmpty(model.Model.Parts);
        Assert.Empty(model.MissingFiles);

        // Two GDTF copies with the exact library bytes.
        Assert.Equal(2, loaded.GdtfFiles.Count);
        Assert.Equal(TestData.LibraryGdtf(TestData.GdtfA), loaded.GdtfFiles[TestData.GdtfA]);
        Assert.Equal(TestData.LibraryGdtf(TestData.GdtfB), loaded.GdtfFiles[TestData.GdtfB]);

        // Settings sections: raw JSON identical, typed access works.
        Assert.Equal(original.Settings.Keys.Order(), loaded.Settings.Keys.Order());
        foreach (var key in original.Settings.Keys) Assert.Equal(original.Settings.GetJson(key), loaded.Settings.GetJson(key));
        Assert.True(loaded.Settings.TryGet<TestData.ViewSection>("view", out var view));
        Assert.Equal(7.5f, view.Exposure);
        Assert.Equal("Full", view.Mode);

        // Manifest.
        var manifest = loaded.Manifest!;
        Assert.Equal("candela-project", manifest.Format);
        Assert.Equal(1, manifest.FormatVersion);
        Assert.Equal("1.2.3", manifest.AppVersion);
        Assert.Equal("Rich stage", manifest.SceneName);
        Assert.Equal(original.Scene.Count, manifest.EntityCount);
        Assert.Equal(2, manifest.FixtureCount);
        Assert.Equal(4, manifest.AssetCount);
        Assert.Equal(2, manifest.GdtfCount);
        Assert.True(manifest.HasThumbnail);
        Assert.Equal(original.Id, manifest.ProjectId);
        Assert.Equal(manifest, ProjectFile.ReadManifest(path));
        Assert.Equal(original.Thumbnail, ProjectFile.ReadThumbnail(path));
        Assert.True(ProjectFile.IsProjectFile(path));
    }

    [Fact]
    public void Saving_a_loaded_project_again_produces_the_same_content()
    {
        using var dir = new TempDir();
        var clock = new ManualClock();
        var options = new ProjectSaveOptions { GdtfProvider = TestData.LibraryGdtf, TimeProvider = clock };
        var first = dir.File("a.candela");
        ProjectFile.Save(first, TestData.RichProject(), options);

        // Without the library: the project's own GDTF copies are written back.
        var loaded = ProjectFile.Load(first);
        var second = dir.File("b.candela");
        var result = ProjectFile.Save(second, loaded, new ProjectSaveOptions { TimeProvider = clock });
        Assert.Empty(result.Issues);
        Assert.Equal(TestData.EntryNames(first), TestData.EntryNames(second));
        foreach (var name in TestData.EntryNames(first))
            Assert.Equal(TestData.ReadEntry(first, name), TestData.ReadEntry(second, name));
    }

    [Fact]
    public void Archive_layout_matches_the_documented_format()
    {
        using var dir = new TempDir();
        var project = TestData.RichProject();
        var path = dir.File("layout.candela");
        ProjectFile.Save(path, project, new ProjectSaveOptions { GdtfProvider = TestData.LibraryGdtf });

        var names = TestData.EntryNames(path);
        Assert.Contains("manifest.json", names);
        Assert.Contains("scene.json", names);
        Assert.Contains("settings.json", names);
        Assert.Contains("assets/index.json", names);
        Assert.Contains("fixtures/index.json", names);
        Assert.Contains("thumbnail.png", names);
        Assert.Contains("fixtures/" + TestData.GdtfA, names);
        Assert.Contains("fixtures/" + TestData.GdtfB, names);
        var chair = project.Assets.Entries.Single(a => a.Kind == AssetKind.Model);
        Assert.Contains($"assets/{chair.Id:D}/chair.obj", names);
        Assert.Contains($"assets/{chair.Id:D}/chair.mtl", names);
        Assert.Contains($"assets/{chair.Id:D}/tex/wood.png", names);

        using var manifest = JsonDocument.Parse(TestData.ReadEntry(path, "manifest.json"));
        Assert.Equal("candela-project", manifest.RootElement.GetProperty("format").GetString());
        Assert.Equal(1, manifest.RootElement.GetProperty("formatVersion").GetInt32());
        foreach (var field in new[] { "appVersion", "createdUtc", "modifiedUtc", "sceneName", "entityCount", "assetCount", "projectId" })
            Assert.True(manifest.RootElement.TryGetProperty(field, out _), field);

        using var settings = JsonDocument.Parse(TestData.ReadEntry(path, "settings.json"));
        Assert.Equal(JsonValueKind.Object, settings.RootElement.ValueKind);
        Assert.Equal(2000, settings.RootElement.GetProperty("luxOverlay").GetProperty("max").GetInt32());
    }

    [Fact]
    public void Missing_gdtf_is_reported_on_save_and_on_load_but_does_not_fail()
    {
        using var dir = new TempDir();
        var project = new ProjectData(new Scene("Missing"));
        var fixture = new Entity("Unknown fixture");
        fixture.Add(new FixtureComponent { GdtfSpec = "Nobody@Nothing.gdtf" });
        project.Scene.Add(fixture);
        var path = dir.File("missing.candela");

        var result = ProjectFile.Save(path, project, new ProjectSaveOptions { GdtfProvider = TestData.LibraryGdtf });
        var issue = Assert.Single(result.Issues);
        Assert.Equal(ProjectIssueCodes.SaveGdtfUnavailable, issue.Code);
        Assert.Equal(new[] { fixture.Id }, issue.Entities);
        Assert.Equal(new[] { "Nobody@Nothing.gdtf" }, result.MissingGdtfs);

        var loaded = ProjectFile.Load(path);
        var note = Assert.Single(loaded.LoadIssues);
        Assert.Equal(ProjectIssueCodes.GdtfNotEmbedded, note.Code);
        Assert.Equal(IssueSeverity.Note, note.Severity);
        Assert.Equal(new[] { fixture.Id }, note.Entities);
    }

    [Fact]
    public void Invalid_gdtf_from_the_provider_and_dangling_assets_are_reported()
    {
        using var dir = new TempDir();
        var project = new ProjectData(new Scene("Bad"));
        var fixture = new Entity("Broken fixture");
        fixture.Add(new FixtureComponent { GdtfSpec = "Broken.gdtf" });
        project.Scene.Add(fixture);
        var mesh = new Entity("Lost model");
        mesh.Add(new MeshComponent { ModelAssetId = Guid.NewGuid() });
        project.Scene.Add(mesh);
        project.Thumbnail = TestData.Utf8("not a png");

        var result = ProjectFile.Save(dir.File("bad.candela"), project, new ProjectSaveOptions { GdtfProvider = _ => TestData.Utf8("garbage") });
        Assert.Contains(result.Issues, i => i.Code == ProjectIssueCodes.SaveGdtfInvalid && i.Entities.Contains(fixture.Id));
        Assert.Contains(result.Issues, i => i.Code == ProjectIssueCodes.SaveAssetMissing && i.Entities.Contains(mesh.Id));
        Assert.Contains(result.Issues, i => i.Code == ProjectIssueCodes.SaveThumbnailInvalid);
        Assert.False(result.Manifest.HasThumbnail);
        Assert.Equal(0, result.Manifest.GdtfCount);
    }

    [Fact]
    public void Snapshot_can_be_written_on_another_thread_while_the_scene_changes()
    {
        using var dir = new TempDir();
        var project = TestData.RichProject();
        var expected = TestData.SceneJson(project.Scene);
        var snapshot = ProjectSnapshot.Capture(project, new ProjectSaveOptions { GdtfProvider = TestData.LibraryGdtf });
        project.Scene.Add(new Entity("Added after the capture"));
        project.Settings.SetJson("view", "{}");

        var path = dir.File("background.candela");
        var result = Task.Run(() => ProjectFile.Save(path, snapshot)).GetAwaiter().GetResult();
        Assert.Empty(result.Issues);
        var loaded = ProjectFile.Load(path);
        Assert.Equal(expected, TestData.SceneJson(loaded.Scene));
        Assert.True(loaded.Settings.TryGet<TestData.ViewSection>("view", out var view));
        Assert.Equal("Full", view.Mode);
        Assert.Null(project.Manifest);
    }

    [Fact]
    public void Stream_save_and_load_round_trip()
    {
        var project = TestData.RichProject();
        using var ms = new MemoryStream();
        ProjectFile.Save(ms, project, new ProjectSaveOptions { GdtfProvider = TestData.LibraryGdtf });
        ms.Position = 0;
        var loaded = ProjectFile.Load(ms);
        Assert.Equal(TestData.SceneJson(project.Scene), TestData.SceneJson(loaded.Scene));
        Assert.Null(loaded.FilePath);
        Assert.Equal(2, loaded.GdtfFiles.Count);
    }

    [Fact]
    public void Odd_file_names_are_stored_safely_and_keep_their_original_names()
    {
        using var dir = new TempDir();
        var project = new ProjectData(new Scene("Names"));
        var a = project.Assets.Add(AssetKind.Image, "CON.png", TestData.Png());
        var b = project.Assets.Add(AssetKind.Image, "a:b*c?.png", TestData.Png(seed: 9));
        var c = project.Assets.Add(AssetKind.Image, "Ünïcødé плакат.png", TestData.Png(seed: 33));
        foreach (var id in new[] { a.Id, b.Id, c.Id })
        {
            var e = new Entity("img");
            e.Add(new ImageComponent { ImageAssetId = id });
            project.Scene.Add(e);
        }
        var path = dir.File("names.candela");
        ProjectFile.Save(path, project);
        var names = TestData.EntryNames(path);
        Assert.Contains($"assets/{a.Id:D}/_CON.png", names);
        Assert.Contains($"assets/{b.Id:D}/a_b_c_.png", names);
        Assert.Contains($"assets/{c.Id:D}/Ünïcødé плакат.png", names);

        var loaded = ProjectFile.Load(path);
        Assert.Empty(loaded.LoadIssues);
        Assert.Equal("CON.png", loaded.Assets.Find(a.Id)!.OriginalFileName);
        Assert.Equal("a:b*c?.png", loaded.Assets.Find(b.Id)!.OriginalFileName);
        Assert.Equal("Ünïcødé плакат.png", loaded.Assets.Find(c.Id)!.OriginalFileName);
    }

    [Fact]
    public void Source_paths_can_be_left_out_of_shared_projects()
    {
        using var dir = new TempDir();
        var path = dir.File("private.candela");
        ProjectFile.Save(path, TestData.RichProject(), new ProjectSaveOptions { IncludeSourcePaths = false });
        var loaded = ProjectFile.Load(path);
        Assert.All(loaded.Assets.Entries, a => Assert.Null(a.SourcePath));
        Assert.DoesNotContain("/home/designer", System.Text.Encoding.UTF8.GetString(TestData.ReadEntry(path, "assets/index.json")));
    }

    [Fact]
    public void Settings_sections_are_independent_json_documents()
    {
        var settings = new ProjectSettings();
        string? changed = "";
        settings.Changed += name => changed = name;
        settings.Set("render", new { samples = 256, denoise = true });
        Assert.Equal("render", changed);
        Assert.Equal("""{"samples":256,"denoise":true}""", settings.GetJson("render"));
        Assert.False(settings.TryGet<TestData.ViewSection>("missing", out _));
        Assert.Equal(3, settings.GetOrDefault("absent", 3));
        settings.SetJson("broken-type", "[1,2,3]");
        Assert.False(settings.TryGet<TestData.ViewSection>("broken-type", out _));
        Assert.ThrowsAny<JsonException>(() => settings.SetJson("bad", "{not json"));
        Assert.Throws<ArgumentException>(() => settings.SetJson(" ", "{}"));
        Assert.True(settings.Remove("render"));
        Assert.False(settings.Remove("render"));
        settings.Clear();
        Assert.Null(changed);
        Assert.Empty(settings);
    }
}
