using System.Text.Json.Nodes;
using Candela.Core.Diagnostics;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;

namespace Candela.Project.Tests;

public sealed class VersionTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private string SaveSimple(string name = "v.candela")
    {
        var project = new ProjectData(new Scene("Versions"));
        var wall = new Entity("Wall");
        wall.Add(new WallComponent { Width = 6f });
        project.Scene.Add(wall);
        project.Settings.SetJson("view", """{"mode":"Full"}""");
        var path = _dir.File(name);
        ProjectFile.Save(path, project, new ProjectSaveOptions { AppVersion = "1.0.0" });
        return path;
    }

    private static void SetManifest(string path, Action<JsonObject> edit) =>
        TestData.RewriteArchive(path, (name, data) =>
        {
            if (name != "manifest.json") return data;
            var m = JsonNode.Parse(data)!.AsObject();
            edit(m);
            return TestData.Utf8(m.ToJsonString());
        });

    [Fact]
    public void Future_format_version_is_refused_with_a_clear_message()
    {
        var path = SaveSimple();
        SetManifest(path, m =>
        {
            m["formatVersion"] = 7;
            m["appVersion"] = "9.4.0";
        });

        var e = Assert.Throws<ProjectVersionException>(() => ProjectFile.Load(path));
        Assert.True(e.IsNewer);
        Assert.Equal(7, e.FileFormatVersion);
        Assert.Equal(ProjectFile.FormatVersion, e.SupportedFormatVersion);
        Assert.Equal("9.4.0", e.WrittenBy);
        Assert.Contains("Candela 9.4.0", e.Message);
        Assert.Contains("format 7", e.Message);
        Assert.Contains("update Candela", e.Message);
        Assert.IsAssignableFrom<ProjectFormatException>(e);

        // The manifest can still be shown in an Open dialog.
        Assert.Equal(7, ProjectFile.ReadManifest(path).FormatVersion);
    }

    [Fact]
    public void Future_scene_format_inside_a_current_archive_is_refused()
    {
        var path = SaveSimple();
        TestData.RewriteArchive(path, (name, data) =>
        {
            if (name != "scene.json") return data;
            var s = JsonNode.Parse(data)!.AsObject();
            s["formatVersion"] = 3;
            return TestData.Utf8(s.ToJsonString());
        });
        var e = Assert.Throws<ProjectVersionException>(() => ProjectFile.Load(path));
        Assert.True(e.IsNewer);
        Assert.Contains("newer version", e.Message);
    }

    [Fact]
    public void Older_format_without_a_migration_is_refused()
    {
        var path = SaveSimple();
        SetManifest(path, m => m["formatVersion"] = 0);
        var e = Assert.Throws<ProjectVersionException>(() => ProjectFile.Load(path));
        Assert.False(e.IsNewer);
        Assert.Contains("format version 0", e.Message);
    }

    [Fact]
    public void Older_format_is_converted_by_the_registered_migrations()
    {
        // A hypothetical format 0: the scene was called "stage.json", walls stored "w" instead of "width", and the
        // settings were a list of {name, value} pairs.
        var path = SaveSimple();
        TestData.RewriteArchive(path, (name, data) =>
        {
            switch (name)
            {
                case "manifest.json":
                    var m = JsonNode.Parse(data)!.AsObject();
                    m["formatVersion"] = 0;
                    return TestData.Utf8(m.ToJsonString());
                case "scene.json":
                    return null;
                case "settings.json":
                    return TestData.Utf8("""[{"name":"view","value":{"mode":"Colorless"}}]""");
                default:
                    return data;
            }
        }, ("stage.json", OldScene(path)));

        var registry = ProjectMigrationRegistry.CreateBuiltIn().Register(0, "scene file renamed, wall width key, settings as object", ctx =>
        {
            Assert.Equal(0, ctx.FromVersion);
            Assert.Null(ctx.Scene);
            Assert.Contains("stage.json", ctx.EntryNames);
            var scene = JsonNode.Parse(ctx.ReadEntry("stage.json")!)!.AsObject();
            foreach (var e in scene["entities"]!.AsArray())
            {
                foreach (var c in e!["components"]!.AsArray())
                {
                    if (c!["w"] is { } w)
                    {
                        c.AsObject().Remove("w");
                        c["width"] = w.GetValue<float>();
                    }
                }
            }
            ctx.Scene = scene;
            ctx.ReplaceEntry("stage.json", null);
            var settings = new JsonObject();
            foreach (var item in ctx.Settings!.AsArray()) settings[item!["name"]!.GetValue<string>()] = item["value"]!.DeepClone();
            ctx.Settings = settings;
            ctx.Note("Walls were converted to the new size fields.");
        });
        Assert.True(registry.CanMigrate(0));
        Assert.False(ProjectMigrationRegistry.Default.CanMigrate(0));

        var loaded = ProjectFile.Load(path, new ProjectLoadOptions { Migrations = registry });
        Assert.Equal(6f, loaded.Scene.Query<WallComponent>().Single().Component.Width);
        Assert.Equal("""{"mode":"Colorless"}""", loaded.Settings.GetJson("view"));
        var notes = loaded.LoadIssues.Where(i => i.Code == ProjectIssueCodes.Migrated).ToList();
        Assert.Equal(2, notes.Count);
        Assert.All(notes, n => Assert.Equal(IssueSeverity.Note, n.Severity));
        Assert.Contains(notes, n => n.Message.Contains("0 → 1"));
        Assert.Contains(notes, n => n.Message.Contains("Walls were converted"));
        Assert.Equal(0, loaded.Manifest!.FormatVersion);

        // Saving writes the current format.
        var resaved = _dir.File("resaved.candela");
        Assert.Equal(ProjectFile.FormatVersion, ProjectFile.Save(resaved, loaded).Manifest.FormatVersion);
        Assert.DoesNotContain("stage.json", TestData.EntryNames(resaved));
    }

    [Fact]
    public void Migration_that_throws_is_reported_as_a_format_error()
    {
        var path = SaveSimple();
        SetManifest(path, m => m["formatVersion"] = 0);
        var registry = ProjectMigrationRegistry.CreateBuiltIn().Register(0, "broken", _ => throw new InvalidOperationException("bad data"));
        var e = Assert.Throws<ProjectFormatException>(() => ProjectFile.Load(path, new ProjectLoadOptions { Migrations = registry }));
        Assert.Contains("bad data", e.Message);
    }

    [Fact]
    public void Registry_rules()
    {
        Assert.Throws<InvalidOperationException>(() => ProjectMigrationRegistry.Default.Register(0, "x", _ => { }));
        var r = new ProjectMigrationRegistry();
        Assert.Throws<ArgumentOutOfRangeException>(() => r.Register(ProjectFile.FormatVersion, "x", _ => { }));
        Assert.Throws<ArgumentOutOfRangeException>(() => r.Register(-1, "x", _ => { }));
        r.Register(0, "first", _ => { });
        Assert.Throws<InvalidOperationException>(() => r.Register(0, "again", _ => { }));
        Assert.Equal(new[] { (0, "first") }, r.Steps);
        Assert.True(r.CanMigrate(ProjectFile.FormatVersion));
        Assert.False(r.CanMigrate(ProjectFile.FormatVersion + 1));
    }

    private static byte[] OldScene(string path)
    {
        var scene = JsonNode.Parse(TestData.ReadEntry(path, "scene.json"))!.AsObject();
        foreach (var e in scene["entities"]!.AsArray())
        {
            foreach (var c in e!["components"]!.AsArray())
            {
                if (c!["width"] is not { } w) continue;
                c.AsObject().Remove("width");
                c["w"] = w.GetValue<float>();
            }
        }
        return TestData.Utf8(scene.ToJsonString());
    }
}
