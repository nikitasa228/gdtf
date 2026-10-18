using System.Numerics;
using Candela.Assets;
using Candela.Core.Math;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;
using Candela.Project;
using Candela.Rendering;

namespace Candela.Engine.Tests;

/// <summary>Saving and opening <c>.candela</c> projects through the engine, merge import, autosave.</summary>
public sealed class ProjectTests
{
    private static (StageEngine Engine, Entity Head, Entity Wall, Entity Cable, Entity Model, string Obj) BuildShow(string name)
    {
        var engine = TestEnvironment.CreateEngine(name);
        var scene = engine.Scene;
        var console = SceneBuilder.Console(scene, new Vector3(0, -6, 0));
        var head = SceneBuilder.Fixture(scene, SceneBuilder.Naja, null, new Vector3(-2, 0, 5), 1, 1, "Head 1", fixtureId: 101);
        SceneBuilder.Fixture(scene, SceneBuilder.MacViper, "Basic", new Vector3(2, 0, 5), 1, 101, "Spot 1", fixtureId: 201);
        var wall = new Entity("Back wall") { Local = new Transform(new Vector3(0, 6, 0)) };
        wall.Add(new WallComponent { Kind = WallKind.Wall, Width = 12f, Height = 6f, MaterialId = MaterialIds.MoltonBlack });
        scene.Add(wall);
        SceneBuilder.Floor(scene, 14f, MaterialIds.WoodStage);
        var cable = SceneBuilder.Cable(scene, console, "DMX A", head, "XLR IN");
        string obj = SceneBuilder.WriteCubeObj(TestEnvironment.NewTempDirectory(name + "-model"), "riser");
        var imported = engine.Import(obj, new EngineImportOptions { Placement = new Transform(new Vector3(0, 2, 0)) });
        Assert.True(imported.Success, imported.Summary);
        var model = scene.Find(imported.Entities[0])!;
        head.Get<FixtureComponent>()!.ManualAttributes["Dimmer"] = 0.5f;
        engine.Settings.SimulateNoise = false;
        engine.Settings.IdleNoiseShare = 0.4f;
        engine.View.View.Overlay = OverlayMode.Lux;
        engine.View.Camera = new SavedCamera { Eye = new Vector3(1, -12, 4), Target = new Vector3(0, 0, 1.5f), Ev100 = 9f };
        return (engine, head, wall, cable, model, obj);
    }

    [Fact]
    public void Save_and_open_keep_fixtures_walls_cables_models_and_engine_settings()
    {
        var (engine, head, wall, cable, model, _) = BuildShow("roundtrip");
        string path = Path.Combine(TestEnvironment.NewTempDirectory("roundtrip-file"), "show.candela");
        using (engine)
        {
            Assert.False(engine.Save().Success); // never saved: needs a file name
            var saved = engine.SaveAs(path);
            Assert.True(saved.Success, saved.Summary);
            Assert.Equal(Path.GetFullPath(path), engine.ProjectPath);
            Assert.False(engine.IsModified);
            Assert.DoesNotContain(saved.Issues, i => i.Severity == Core.Diagnostics.IssueSeverity.Error);
        }

        using var reopened = TestEnvironment.CreateEngine("roundtrip-open");
        var opened = reopened.Open(path);
        Assert.True(opened.Success, opened.Summary);
        Assert.Equal(Path.GetFullPath(path), reopened.ProjectPath);
        var scene = reopened.Scene;

        var head2 = scene.Find(head.Id)!;
        var fixture = head2.Get<FixtureComponent>()!;
        Assert.Equal(SceneBuilder.Naja, fixture.GdtfSpec);
        Assert.Equal(101, fixture.FixtureId);
        Assert.Equal(new DmxAddress(1, 1), Assert.Single(fixture.Addresses));
        Assert.Equal(0.5f, fixture.ManualAttributes["Dimmer"]);
        Assert.Equal(new Vector3(-2, 0, 5), head2.Local.Position);
        Assert.Equal(2, scene.Query<FixtureComponent>().Count());

        var wall2 = scene.Find(wall.Id)!.Get<WallComponent>()!;
        Assert.Equal(12f, wall2.Width);
        Assert.Equal(MaterialIds.MoltonBlack, wall2.MaterialId);

        var cable2 = scene.Find(cable.Id)!.Get<CableComponent>()!;
        Assert.Equal(2, cable2.Endpoints.Count);
        Assert.Contains(cable2.Endpoints, e => e.EntityId == head.Id && e.PortName == "XLR IN");
        Assert.All(cable2.Nodes, n => Assert.NotNull(n.Anchor));

        var mesh = scene.Find(model.Id)!.Get<MeshComponent>()!;
        var asset = reopened.Assets.Find(mesh.ModelAssetId);
        Assert.NotNull(asset);
        Assert.Contains("tex.png", asset!.CompanionFiles.Keys);
        Assert.True(reopened.Models.TryGet(asset.Id, out var decoded, out var error), error);
        Assert.Equal(12, decoded.TriangleCount);

        // the engine and view sections came back
        Assert.False(reopened.Settings.SimulateNoise);
        Assert.Equal(0.4f, reopened.Settings.IdleNoiseShare);
        Assert.Equal(OverlayMode.Lux, reopened.View.View.Overlay);
        Assert.Equal(new Vector3(1, -12, 4), reopened.View.Camera!.Eye);
        Assert.Equal(9f, reopened.View.Camera.Ev100);

        // the project is alive: the cabled fixture is console-driven, the scene renders
        reopened.Tick(0);
        Assert.True(reopened.HasConsoles);
        Assert.Equal(2, reopened.Dmx.FixtureCount);
        var world = new RecordingRenderWorld();
        reopened.AttachRenderWorld(world).LoadFixtureModelsInBackground = false;
        reopened.Tick(1.0 / 30);
        Assert.Empty(world.Violations);
        Assert.True(world.Instances.Count > 4);
        Assert.NotEmpty(world.Lines);
    }

    [Fact]
    public void A_project_opens_without_the_library_files_from_its_gdtf_copies()
    {
        var (engine, head, _, _, _, _) = BuildShow("copies");
        string path = Path.Combine(TestEnvironment.NewTempDirectory("copies-file"), "show.candela");
        using (engine) Assert.True(engine.SaveAs(path).Success);

        // an engine whose library is empty still evaluates the fixtures from the copies kept in the project
        using var bare = new StageEngine(new StageEngineOptions
        {
            Library = new Gdtf.FixtureLibrary(),
            CacheDirectory = TestEnvironment.NewTempDirectory("copies-cache"),
        });
        var opened = bare.Open(path);
        Assert.True(opened.Success, opened.Summary);
        bare.Tick(0);
        Assert.NotNull(bare.Dmx.GetState(head.Id));
        Assert.Equal(2, bare.Dmx.FixtureCount);
        Assert.NotNull(bare.Library.FindByFileName(SceneBuilder.Naja));
    }

    [Fact]
    public void Importing_a_project_merges_it_under_a_group_and_can_be_undone()
    {
        var (engine, _, _, _, _, _) = BuildShow("merge-source");
        string path = Path.Combine(TestEnvironment.NewTempDirectory("merge-file"), "rig.candela");
        using (engine) Assert.True(engine.SaveAs(path).Success);

        using var target = TestEnvironment.CreateEngine("merge-target");
        SceneBuilder.Fixture(target.Scene, SceneBuilder.MacViper, "Basic", new Vector3(0, 0, 6), 2, 1);
        int before = target.Scene.Count;
        var result = target.Import(path);
        Assert.True(result.Success, result.Summary);
        var group = target.Scene.Find(Assert.Single(result.Entities))!;
        Assert.True(group.Has<GroupComponent>());
        Assert.Equal(3, target.Scene.Query<FixtureComponent>().Count());
        Assert.True(target.Assets.Count >= 1);
        target.Tick(0);
        Assert.Equal(3, target.Dmx.FixtureCount);

        target.Undo.Undo();
        Assert.Equal(before, target.Scene.Count);
        target.Undo.Redo();
        Assert.NotNull(target.Scene.Find(group.Id));
    }

    [Fact]
    public async Task Autosave_writes_a_recoverable_copy_of_a_changed_project()
    {
        string dir = TestEnvironment.NewTempDirectory("autosave");
        using var engine = TestEnvironment.CreateEngine("autosave-engine");
        engine.EnableAutosave(new AutosaveOptions { Directory = dir, Interval = TimeSpan.FromMilliseconds(20) });
        Assert.NotNull(engine.Autosave);
        SceneBuilder.Fixture(engine.Scene, SceneBuilder.MacViper, "Basic", new Vector3(0, 0, 6), 1, 1);
        for (int i = 0; i < 50 && engine.Autosave!.LastSavedUtc is null; i++)
        {
            Thread.Sleep(30);
            engine.Tick(0.03);
            await engine.Autosave.FlushAsync();
        }
        engine.Tick(0);
        Assert.NotNull(engine.Autosave!.LastSavedUtc);
        Assert.True(File.Exists(engine.Autosave.FilePath));

        // a crash (no clean close) leaves a recoverable session; recovering brings the scene back
        engine.DisableAutosave(completeSession: false);
        var session = Assert.Single(AutosaveService.FindRecoverable(dir));
        using var recovered = TestEnvironment.CreateEngine("autosave-recover");
        var result = recovered.Recover(session);
        Assert.True(result.Success, result.Summary);
        Assert.Single(recovered.Scene.Query<FixtureComponent>());
        Assert.True(recovered.IsModified);
        session.Delete();
        Assert.Empty(AutosaveService.FindRecoverable(dir));
    }

    [Fact]
    public void Opening_damaged_or_foreign_files_fails_without_losing_the_open_scene()
    {
        using var engine = TestEnvironment.CreateEngine("damaged");
        SceneBuilder.Fixture(engine.Scene, SceneBuilder.MacViper, "Basic", new Vector3(0, 0, 6));
        string dir = TestEnvironment.NewTempDirectory("damaged-files");
        string notZip = Path.Combine(dir, "broken.candela");
        File.WriteAllText(notZip, "not a project");
        var result = engine.Open(notZip);
        Assert.False(result.Success);
        Assert.Equal(EngineIssueCodes.OpenFailed, Assert.Single(result.Issues).Code);
        Assert.Single(engine.Scene.Query<FixtureComponent>());

        Assert.False(engine.Open(Path.Combine(dir, "missing.candela")).Success);
        string txt = Path.Combine(dir, "notes.txt");
        File.WriteAllText(txt, "x");
        Assert.False(engine.Open(txt).Success);
        Assert.False(engine.Import(txt).Success);
        Assert.False(engine.SaveAs(Path.Combine(dir, "no-such-folder", "\0bad.candela")).Success);

        var fresh = engine.New("Next show");
        Assert.True(fresh.Success);
        Assert.Equal("Next show", engine.Scene.Name);
        Assert.Equal(0, engine.Scene.Count);
        Assert.Equal(0, engine.Assets.Count);
    }
}
