using System.Numerics;
using Candela.Assets;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;
using Candela.Rendering;

namespace Candela.Engine.Tests;

/// <summary>Render sync under changes that replace what it drew: assets, fixture types, the library, the project.</summary>
public sealed class RenderSyncReviewTests
{
    private const double Frame = 1.0 / 30.0;

    private static string WriteStl(string folder, float size)
    {
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "plate.stl");
        string s = size.ToString(System.Globalization.CultureInfo.InvariantCulture);
        File.WriteAllText(path, $"solid plate\nfacet normal 0 0 1\nouter loop\nvertex 0 0 0\nvertex {s} 0 0\nvertex 0 {s} 0\nendloop\nendfacet\nendsolid plate\n");
        return path;
    }

    [Fact]
    public void Changing_a_model_assets_import_options_redraws_it_with_the_new_model()
    {
        using var engine = TestEnvironment.CreateEngine("asset-change");
        var imported = engine.Import(WriteStl(TestEnvironment.NewTempDirectory("asset-change-model"), 2f),
            new EngineImportOptions { Model = ImportOptions.Default with { UnitScale = 1f, GuessUnits = false, UpAxis = SourceUpAxis.Z } });
        Assert.True(imported.Success, imported.Summary);
        var entity = engine.Scene.Find(imported.Entities[0])!;
        var assetId = entity.Get<MeshComponent>()!.ModelAssetId;
        var world = new RecordingRenderWorld();
        var sync = engine.AttachRenderWorld(world, loadFixtureModelsInBackground: false);
        engine.Tick(0);
        var before = world.Meshes[world.Instances[sync.InstancesOf(entity.Id).Single().Handle.Value].Mesh.Value];
        float extentBefore = before.Positions.Max(p => p.X);

        string path = Path.Combine(TestEnvironment.NewTempDirectory("asset-change-file"), "show.candela");
        Assert.True(engine.SaveAs(path).Success);
        Assert.False(engine.IsModified);
        engine.Assets.SetImportOptions(assetId, ImportOptions.Default with { UnitScale = 0.5f, GuessUnits = false, UpAxis = SourceUpAxis.Z });
        Assert.True(engine.IsModified, "changing an asset's import options is an unsaved change");
        engine.Tick(Frame);
        var after = world.Meshes[world.Instances[sync.InstancesOf(entity.Id).Single().Handle.Value].Mesh.Value];
        Assert.Equal(extentBefore * 0.5f, after.Positions.Max(p => p.X), 3);
        Assert.Empty(world.Violations);
    }

    [Fact]
    public void Changing_a_fixtures_gdtf_or_mode_redraws_it_as_the_new_type()
    {
        using var engine = TestEnvironment.CreateEngine("type-change");
        var head = SceneBuilder.Fixture(engine.Scene, SceneBuilder.MacViper, "Basic", new Vector3(0, 0, 6));
        var other = SceneBuilder.Fixture(engine.Scene, SceneBuilder.MacViper, "Basic", new Vector3(2, 0, 6));
        StageLooks.ApplyOpenWhite(engine, new OpenWhiteOptions { Undoable = false });
        var world = new RecordingRenderWorld();
        var sync = engine.AttachRenderWorld(world, loadFixtureModelsInBackground: false);
        engine.Tick(0);
        engine.Tick(Frame);
        var viperMeshes = sync.InstancesOf(other.Id).Select(i => world.Instances[i.Handle.Value].Mesh).ToHashSet();
        Assert.Subset(viperMeshes, sync.InstancesOf(head.Id).Select(i => world.Instances[i.Handle.Value].Mesh).ToHashSet());

        var fixture = head.Get<FixtureComponent>()!;
        fixture.GdtfSpec = SceneBuilder.Naja;
        fixture.GdtfMode = null;
        fixture.NotifyChanged();
        engine.Tick(Frame);
        engine.Tick(Frame);
        Assert.Equal("Naja", engine.Dmx.GetState(head.Id)!.Profile.FixtureType.Name);
        // the head draws the Naja's meshes now, the other fixture still the MAC Viper's
        Assert.NotEmpty(sync.InstancesOf(head.Id));
        Assert.DoesNotContain(sync.InstancesOf(head.Id), i => viperMeshes.Contains(world.Instances[i.Handle.Value].Mesh));
        Assert.Subset(viperMeshes, sync.InstancesOf(other.Id).Select(i => world.Instances[i.Handle.Value].Mesh).ToHashSet());
        Assert.Equal(sync.InstanceCount, world.Instances.Count);
        Assert.Equal(engine.Lights.Length, world.Lights.Length);
        Assert.Empty(world.Violations);
    }

    [Fact]
    public void A_fixture_whose_gdtf_arrives_later_is_drawn_once_it_is_imported()
    {
        // an engine without the MAC Viper in its library: the fixture is unknown until its GDTF is imported
        using var engine = new StageEngine(new StageEngineOptions
        {
            Library = new Gdtf.FixtureLibrary(),
            CacheDirectory = TestEnvironment.NewTempDirectory("late-gdtf-cache"),
        });
        var head = SceneBuilder.Fixture(engine.Scene, SceneBuilder.MacViper, "Basic", new Vector3(0, 0, 6));
        var world = new RecordingRenderWorld();
        var sync = engine.AttachRenderWorld(world, loadFixtureModelsInBackground: false);
        engine.Tick(0);
        Assert.Empty(sync.InstancesOf(head.Id));

        var imported = engine.Import(Path.Combine(TestEnvironment.Root, SceneBuilder.MacViper));
        Assert.True(imported.Success, imported.Summary);
        StageLooks.ApplyOpenWhite(engine, new OpenWhiteOptions { Undoable = false });
        engine.Tick(Frame);
        engine.Tick(Frame);
        Assert.NotEmpty(sync.InstancesOf(head.Id));
        Assert.Single(world.Lights);
        Assert.Empty(world.Violations);
    }

    [Fact]
    public void Reloading_the_library_while_drawn_rebuilds_every_fixture_without_dangling_resources()
    {
        using var engine = new StageEngine(new StageEngineOptions
        {
            Library = Gdtf.FixtureLibrary.Scan(new[] { CopyGdtfs("reload-lib", SceneBuilder.MacViper) }),
            CacheDirectory = TestEnvironment.NewTempDirectory("reload-cache"),
        });
        var a = SceneBuilder.Fixture(engine.Scene, SceneBuilder.MacViper, "Basic", new Vector3(0, 0, 6));
        var b = SceneBuilder.Fixture(engine.Scene, SceneBuilder.MacViper, "Basic", new Vector3(2, 0, 6));
        StageLooks.ApplyOpenWhite(engine, new OpenWhiteOptions { Undoable = false });
        var fa = a.Get<FixtureComponent>()!;
        fa.ManualAttributes["Gobo1"] = 3f;
        fa.NotifyChanged();
        var world = new RecordingRenderWorld();
        var sync = engine.AttachRenderWorld(world, loadFixtureModelsInBackground: false);
        for (int i = 0; i < 60; i++) engine.Tick(Frame);
        int parts = sync.InstancesOf(a.Id).Count;
        int textures = world.Textures.Count;
        Assert.True(textures > 0);

        // adding a GDTF recompiles every profile (new profile objects for the same types)
        Assert.True(engine.Import(Path.Combine(TestEnvironment.Root, SceneBuilder.Naja)).Success);
        for (int i = 0; i < 3; i++) engine.Tick(Frame);
        Assert.Equal(parts, sync.InstancesOf(a.Id).Count);
        Assert.Equal(parts, sync.InstancesOf(b.Id).Count);
        Assert.Equal(2, world.Lights.Length);
        Assert.Contains(world.Lights, l => l.Gobo.IsValid);
        Assert.Empty(world.Violations);

        // the replaced fixture type's meshes and gobos do not pile up in the renderer
        int meshes = world.Meshes.Count;
        Assert.True(engine.Import(Path.Combine(TestEnvironment.Root, SceneBuilder.EntourHaze)).Success);
        for (int i = 0; i < 3; i++) engine.Tick(Frame);
        Assert.Equal(meshes, world.Meshes.Count);
        Assert.True(world.Textures.Count <= textures, $"{world.Textures.Count} textures after a reload, {textures} before");
        Assert.Empty(world.Violations);
    }

    [Fact]
    public void Opening_another_scene_while_attached_replaces_everything_drawn()
    {
        using var engine = TestEnvironment.CreateEngine("reopen-attached");
        var world = new RecordingRenderWorld();
        var sync = engine.AttachRenderWorld(world, loadFixtureModelsInBackground: false);
        Assert.True(engine.Open(TestEnvironment.Fokina).Success);
        StageLooks.ApplyOpenWhite(engine, new OpenWhiteOptions { Undoable = false });
        engine.Tick(0);
        engine.Tick(Frame);
        Assert.True(world.Instances.Count > 1000);
        engine.Selection.Set(engine.Scene.Roots[0].Id);

        engine.New("Empty");
        engine.Tick(Frame);
        Assert.Empty(world.Instances);
        Assert.Empty(world.Lights);
        Assert.Empty(world.Lines);

        var head = SceneBuilder.Fixture(engine.Scene, SceneBuilder.MacViper, "Basic", new Vector3(0, 0, 6));
        engine.Tick(Frame);
        Assert.NotEmpty(sync.InstancesOf(head.Id));
        Assert.Equal(sync.InstanceCount, world.Instances.Count);
        Assert.Empty(world.Violations);
    }

    private static string CopyGdtfs(string name, params string[] files)
    {
        string dir = TestEnvironment.NewTempDirectory(name);
        foreach (var f in files) File.Copy(Path.Combine(TestEnvironment.Root, f), Path.Combine(dir, f));
        return dir;
    }
}
