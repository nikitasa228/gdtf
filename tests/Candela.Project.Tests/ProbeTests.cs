using System.Numerics;
using System.Text.Json;
using Candela.Assets;
using Candela.Core.Diagnostics;
using Candela.Core.Math;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;

namespace Candela.Project.Tests;

public sealed class ProbeTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly ManualClock _clock = new();

    public void Dispose() => _dir.Dispose();

    private string AutosaveDir => Path.Combine(_dir.Path, "autosave");

    private AutosaveOptions Options(int keep = 3) => new() { Directory = AutosaveDir, Interval = TimeSpan.FromMinutes(1), KeepCount = keep, TimeProvider = _clock };

    [Fact]
    public async Task P1_new_session_does_not_destroy_unrecovered_crash_files()
    {
        var project = new ProjectData(new Scene("Crashy"));
        project.Scene.Add(new Entity("Unsaved work"));
        using (var crashed = new AutosaveService(project.Id, () => ProjectSnapshot.Capture(project), Options()))
        {
            await crashed.SaveNowAsync();
        }
        var crashBytes = File.ReadAllBytes(Path.Combine(AutosaveDir, project.Id.ToString("D") + ".candela"));

        // The app opens the same project again (original file, without the unsaved work) before recovery was decided.
        var reopened = new ProjectData(new Scene("Crashy")) { Id = project.Id };
        var second = new AutosaveService(reopened.Id, () => ProjectSnapshot.Capture(reopened), Options(keep: 1));
        await second.SaveNowAsync();
        await second.SaveNowAsync();
        second.CompleteSession();

        var session = Assert.Single(AutosaveService.FindRecoverable(AutosaveDir));
        Assert.Equal(crashBytes, File.ReadAllBytes(session.Files[0]));
    }

    [Fact]
    public void P2_lone_surrogate_and_control_chars_in_names_save()
    {
        var project = new ProjectData(new Scene("Bad \uD800 name\0"));
        project.Scene.Add(new Entity("x\uDC00y\u0001"));
        project.Settings.SetJson("weird", "{\"a\":\"\\ud800\"}");
        var path = _dir.File("s.candela");
        ProjectFile.Save(path, project);
        var loaded = ProjectFile.Load(path);
        Assert.Equal(TestData.SceneJson(project.Scene), TestData.SceneJson(loaded.Scene));
    }

    [Fact]
    public void P3_gdtf_names_with_separators_and_case_variants_round_trip()
    {
        var project = new ProjectData(new Scene("Names"));
        void Add(string spec)
        {
            var e = new Entity(spec);
            e.Add(new FixtureComponent { GdtfSpec = spec });
            project.Scene.Add(e);
        }
        Add("Robe/Robin:T1.gdtf");
        Add(TestData.GdtfA);
        Add(TestData.GdtfA.ToUpperInvariant());
        Add("noext");
        byte[]? Provider(string name) => name switch
        {
            "Robe/Robin:T1.gdtf" => TestData.LibraryGdtf(TestData.GdtfB),
            "noext" => TestData.LibraryGdtf(TestData.GdtfB),
            _ when name == TestData.GdtfA => TestData.LibraryGdtf(TestData.GdtfA),
            _ when name == TestData.GdtfA.ToUpperInvariant() => TestData.LibraryGdtf(TestData.GdtfB),
            _ => null,
        };
        var path = _dir.File("n.candela");
        var result = ProjectFile.Save(path, project, new ProjectSaveOptions { GdtfProvider = Provider });
        Assert.Empty(result.Issues);
        var loaded = ProjectFile.Load(path);
        Assert.Empty(loaded.LoadIssues);
        foreach (var spec in project.UsedGdtfSpecs()) Assert.Equal(Provider(spec), loaded.GdtfFiles[spec]);
        // Saved again without the library: identical copies.
        var path2 = _dir.File("n2.candela");
        ProjectFile.Save(path2, loaded);
        var again = ProjectFile.Load(path2);
        foreach (var spec in project.UsedGdtfSpecs()) Assert.Equal(Provider(spec), again.GdtfFiles[spec]);
    }

    [Fact]
    public void P4_non_finite_component_values_round_trip()
    {
        var project = new ProjectData(new Scene("NaN"));
        var e = new Entity("f");
        e.Add(new FixtureComponent { ManualAttributes = { ["Pan"] = float.NaN, ["Tilt"] = float.PositiveInfinity }, FocusPoint = new Vector3(float.NaN, 1, 2) });
        e.Add(new WallComponent { Width = float.NegativeInfinity });
        project.Scene.Add(e);
        var path = _dir.File("nan.candela");
        ProjectFile.Save(path, project);
        var loaded = ProjectFile.Load(path);
        Assert.Equal(TestData.SceneJson(project.Scene), TestData.SceneJson(loaded.Scene));
    }

    [Fact]
    public void P5_merge_repoints_case_variant_gdtf_references_on_rename()
    {
        var target = new ProjectData(new Scene("T"));
        target.GdtfFiles[TestData.GdtfA] = TestData.LibraryGdtf(TestData.GdtfA)!;
        var source = new ProjectData(new Scene("S"));
        source.GdtfFiles[TestData.GdtfA] = TestData.LibraryGdtf(TestData.GdtfB)!;
        var f = new Entity("f");
        f.Add(new FixtureComponent { GdtfSpec = TestData.GdtfA.ToLowerInvariant() });
        source.Scene.Add(f);
        Assert.NotNull(source.FindGdtf(TestData.GdtfA.ToLowerInvariant()));
        var result = ProjectMerge.Import(target, source);
        var spec = target.Scene.Find(result.EntityIds[f.Id])!.Get<FixtureComponent>()!.GdtfSpec;
        Assert.Equal(TestData.LibraryGdtf(TestData.GdtfB), target.FindGdtf(spec));
    }

    [Fact]
    public void P6_many_entities_round_trip_and_merge_quickly()
    {
        var project = new ProjectData(new Scene("Big"));
        var parent = new Entity("root");
        project.Scene.Add(parent);
        Entity last = parent;
        for (int i = 0; i < 20000; i++)
        {
            var e = new Entity("e" + i);
            e.Add(new FixtureComponent { GdtfSpec = "x.gdtf", MountedOn = last.Id });
            project.Scene.Add(e, i % 100 == 0 ? parent : last);
            if (i % 100 == 0) last = e;
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var path = _dir.File("big.candela");
        ProjectFile.Save(path, project);
        var loaded = ProjectFile.Load(path);
        var r = ProjectMerge.Import(loaded, loaded);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), sw.Elapsed.ToString());
        Assert.Equal(project.Scene.Count * 2 + 1, loaded.Scene.Count);
        _ = r;
    }

    [Fact]
    public void P7_deep_tree_round_trips()
    {
        var project = new ProjectData(new Scene("Deep"));
        Entity? parent = null;
        for (int i = 0; i < 3000; i++)
        {
            var e = new Entity("d" + i);
            project.Scene.Add(e, parent);
            parent = e;
        }
        var path = _dir.File("deep.candela");
        ProjectFile.Save(path, project);
        var loaded = ProjectFile.Load(path);
        Assert.Equal(TestData.SceneJson(project.Scene), TestData.SceneJson(loaded.Scene));
        ProjectMerge.Import(loaded, loaded);
    }

    [Fact]
    public void P8_unknown_component_is_kept_on_resave()
    {
        var project = new ProjectData(new Scene("Future"));
        var e = new Entity("Laser");
        project.Scene.Add(e);
        var path = _dir.File("f.candela");
        ProjectFile.Save(path, project);
        TestData.RewriteArchive(path, (name, data) =>
        {
            if (name != "scene.json") return data;
            var text = System.Text.Encoding.UTF8.GetString(data);
            text = text.Replace("\"components\": []", "\"components\": [ { \"$type\": \"laser\", \"power\": 5 } ]");
            return TestData.Utf8(text);
        });
        var loaded = ProjectFile.Load(path);
        Assert.Contains(loaded.LoadIssues, i => i.Code == ProjectIssueCodes.SceneComponentUnknown);
        var path2 = _dir.File("f2.candela");
        ProjectFile.Save(path2, loaded);
        var scene = System.Text.Encoding.UTF8.GetString(TestData.ReadEntry(path2, "scene.json"));
        Assert.Contains("laser", scene);
    }

    [Fact]
    public void P9_save_into_readonly_target_keeps_file()
    {
        var project = new ProjectData(new Scene("ro"));
        var path = _dir.File("ro.candela");
        ProjectFile.Save(path, project);
        var before = File.ReadAllBytes(path);
        var bad = Path.Combine(path, "child.candela"); // the 'folder' is a file
        Assert.ThrowsAny<IOException>(() => ProjectFile.Save(bad, project));
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void P10_load_of_project_saved_with_asset_whose_name_is_index_json()
    {
        var project = new ProjectData(new Scene("idx"));
        project.Assets.Add(AssetKind.Image, "index.json", TestData.Png());
        project.Assets.Add(AssetKind.Image, "../../manifest.json", TestData.Png(seed: 3));
        project.Assets.Add(AssetKind.Model, "m.obj", TestData.Utf8(TestData.ObjText), new Dictionary<string, byte[]>
        {
            ["../index.json"] = TestData.Utf8("a"),
            ["Index.JSON"] = TestData.Utf8("b"),
            ["m.obj"] = TestData.Utf8("c"),
        });
        var path = _dir.File("i.candela");
        ProjectFile.Save(path, project);
        var loaded = ProjectFile.Load(path);
        Assert.Empty(loaded.LoadIssues);
        foreach (var a in project.Assets.Entries)
        {
            var b = loaded.Assets.Find(a.Id)!;
            Assert.Equal(a.Data, b.Data);
            Assert.Equal(a.CompanionFiles.Count, b.CompanionFiles.Count);
            foreach (var (k, v) in a.CompanionFiles) Assert.Equal(v, b.CompanionFiles[k]);
        }
    }

    [Fact]
    public void P11_settings_with_odd_section_names_round_trip()
    {
        var project = new ProjectData(new Scene("s"));
        project.Settings.SetJson("$type", "1");
        project.Settings.SetJson("a.b/c", "[1,2,{\"x\":null}]");
        project.Settings.SetJson("Ünïcödé", "\"\\u0000\"");
        project.Settings.Set<object?>("nullish", null);
        var path = _dir.File("s.candela");
        ProjectFile.Save(path, project);
        var loaded = ProjectFile.Load(path);
        Assert.Equal(project.Settings.Keys.Order(), loaded.Settings.Keys.Order());
        foreach (var k in project.Settings.Keys) Assert.Equal(project.Settings.GetJson(k), loaded.Settings.GetJson(k));
    }

    [Fact]
    public void P12_merge_with_camera_target_and_placement_identity_is_noop()
    {
        var p = TestData.RichProject();
        var r = ProjectMerge.Import(p, p, new ProjectMergeOptions { Placement = Transform.Identity });
        Assert.NotNull(r);
    }

    [Fact]
    public void P13_corrupt_component_keeps_others()
    {
        var project = new ProjectData(new Scene("c"));
        var e = new Entity("x");
        e.Add(new WallComponent { Width = 7 });
        e.Add(new FixtureComponent { GdtfSpec = "a.gdtf", FixtureId = 3 });
        project.Scene.Add(e);
        var path = _dir.File("c.candela");
        ProjectFile.Save(path, project);
        TestData.RewriteArchive(path, (name, data) =>
        {
            if (name != "scene.json") return data;
            var text = System.Text.Encoding.UTF8.GetString(data).Replace("\"fixtureId\": 3", "\"fixtureId\": \"three\"");
            return TestData.Utf8(text);
        });
        var loaded = ProjectFile.Load(path);
        Assert.Single(loaded.Scene.All().Single().Components);
    }
}
