using System.Numerics;
using Candela.Core.SceneGraph.Components;

namespace Candela.Engine.Tests;

/// <summary>Importing an MVR into the open scene (merge), twice, with undo, while drawn.</summary>
public sealed class MvrImportReviewTests
{
    [Fact]
    public void An_mvr_merges_into_the_open_scene_twice_with_undo_while_drawn()
    {
        using var engine = TestEnvironment.CreateEngine("mvr-merge");
        var own = SceneBuilder.Fixture(engine.Scene, SceneBuilder.MacViper, "Basic", new Vector3(0, -12, 6));
        var world = new RecordingRenderWorld();
        var sync = engine.AttachRenderWorld(world, loadFixtureModelsInBackground: false);
        engine.Tick(0);

        var first = engine.Import(TestEnvironment.Fokina);
        Assert.True(first.Success, first.Summary);
        var second = engine.Import(TestEnvironment.Fokina);
        Assert.True(second.Success, second.Summary);
        Assert.Equal(1 + 2 * 91, engine.Scene.Query<FixtureComponent>().Count());
        // the second copy got its own ids
        var ids = engine.Scene.All().Select(e => e.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.True(engine.IsModified);

        engine.Tick(1.0 / 30);
        Assert.Equal(1 + 2 * 91, engine.Dmx.FixtureCount);
        Assert.Equal(1 + 2 * 91, engine.Poses.Count);
        int instances = sync.InstanceCount;
        Assert.Equal(instances, world.Instances.Count);

        engine.Undo.Undo();
        engine.Undo.Undo();
        engine.Tick(1.0 / 30);
        Assert.Single(engine.Scene.Query<FixtureComponent>());
        Assert.Equal(1, engine.Dmx.FixtureCount);
        Assert.Equal(sync.InstancesOf(own.Id).Count, world.Instances.Count);

        engine.Undo.Redo();
        engine.Tick(1.0 / 30);
        Assert.Equal(1 + 91, engine.Dmx.FixtureCount);
        Assert.Empty(world.Violations);
    }
}
