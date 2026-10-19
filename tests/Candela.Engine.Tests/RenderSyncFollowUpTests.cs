using System.Numerics;
using Candela.Core.Editing;
using Candela.Core.Math;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;
using Candela.Rendering;

namespace Candela.Engine.Tests;

/// <summary>
/// Render binding follow-ups: flags derived from scene change kinds (not every frame), per-entity world bounds, the
/// work light and the outline of a selected group's content.
/// </summary>
public sealed class RenderSyncFollowUpTests
{
    private const double Frame = 1.0 / 30.0;
    private const InstanceFlags EntityFlags = InstanceFlags.Selected | InstanceFlags.Hidden | InstanceFlags.CableHighlight;

    /// <summary>The flags every instance must have, derived from the scene directly (the per-frame rule the binding replaced).</summary>
    private static void AssertFlagsMatchScene(StageEngine engine, RecordingRenderWorld world, RenderSync sync, bool outlineDescendants = false)
    {
        var scene = engine.Scene;
        var layers = scene.Layers.ToDictionary(l => l.Id, l => l.Visible);
        var cableEnds = new HashSet<Guid>();
        foreach (var (entity, cable) in scene.Query<CableComponent>())
        {
            cableEnds.Add(entity.Id);
            foreach (var end in cable.Endpoints)
                if (end is not null && end.EntityId != Guid.Empty) cableEnds.Add(end.EntityId);
        }
        foreach (var instance in world.Instances.Values)
        {
            Assert.True(sync.TryGetEntity(instance.PickId, out var id));
            var e = scene.Find(id)!;
            var expected = InstanceFlags.None;
            bool selected = engine.Selection.Contains(id);
            if (outlineDescendants)
                for (var p = e.Parent; p is not null && !selected; p = p.Parent) selected = engine.Selection.Contains(p.Id);
            if (selected) expected |= InstanceFlags.Selected;
            if (!e.IsVisibleInHierarchy || (layers.TryGetValue(e.LayerId, out bool visible) && !visible)) expected |= InstanceFlags.Hidden;
            if (cableEnds.Contains(id)) expected |= InstanceFlags.CableHighlight;
            Assert.True(expected == (instance.Flags & EntityFlags), $"{e.Name}: expected {expected}, drawn {instance.Flags & EntityFlags}");
        }
    }

    private static Entity Wall(Scene scene, string name, Vector3 position, Entity? parent = null)
    {
        var wall = new Entity(name) { Local = new Transform(position) };
        wall.Add(new WallComponent { Kind = WallKind.Wall, Width = 2f, Height = 3f, Thickness = 0.2f });
        scene.Add(wall, parent);
        return wall;
    }

    [Fact]
    public void Flags_follow_visibility_layer_selection_reparent_and_cable_changes_like_a_per_frame_scan()
    {
        using var engine = TestEnvironment.CreateEngine("flags-events");
        var scene = engine.Scene;
        var groupA = new Entity("Group A");
        var groupB = new Entity("Group B") { Local = new Transform(new Vector3(0, 4, 0)) };
        scene.Add(groupA);
        scene.Add(groupB);
        var walls = new List<Entity>();
        for (int i = 0; i < 6; i++) walls.Add(Wall(scene, $"Wall {i}", new Vector3(i * 2.5f, 0, 0), i % 2 == 0 ? groupA : groupB));
        var head = SceneBuilder.Fixture(scene, SceneBuilder.MacViper, "Basic", new Vector3(0, 0, 6));
        scene.Reparent(head, groupB);
        var console = SceneBuilder.Console(scene, new Vector3(0, -8, 0));
        var layerA = new Layer { Name = "A" };
        var layerB = new Layer { Name = "B" };
        scene.Layers.Add(layerA);
        scene.Layers.Add(layerB);
        var world = new RecordingRenderWorld();
        var sync = engine.AttachRenderWorld(world, loadFixtureModelsInBackground: false);
        engine.Tick(0);
        AssertFlagsMatchScene(engine, world, sync);

        var all = new List<Entity>(walls) { head, console, groupA, groupB };
        var random = new Random(1234);
        Entity? cable = null;
        for (int step = 0; step < 120; step++)
        {
            var target = all[random.Next(all.Count)];
            switch (random.Next(9))
            {
                case 0:
                    target.Visible = !target.Visible;
                    break;
                case 1:
                    target.LayerId = random.Next(3) switch { 0 => layerA.Id, 1 => layerB.Id, _ => Guid.Empty };
                    break;
                case 2:
                    (random.Next(2) == 0 ? layerA : layerB).Visible ^= true;
                    break;
                case 3:
                    engine.Selection.Set(all.Where(_ => random.Next(4) == 0).Select(e => e.Id));
                    break;
                case 4 when target != groupA && target != groupB:
                    scene.Reparent(target, random.Next(3) switch { 0 => groupA, 1 => groupB, _ => null });
                    break;
                case 5:
                    target.Locked = !target.Locked;
                    break;
                case 6:
                    if (cable is null) cable = SceneBuilder.Cable(scene, console, "DMX A", head, "XLR IN");
                    else
                    {
                        scene.Remove(cable);
                        cable = null;
                    }
                    break;
                case 7:
                    if (scene.Layers.Contains(layerB)) scene.Layers.Remove(layerB);
                    else scene.Layers.Add(layerB);
                    break;
                default:
                    target.Local = new Transform(target.Local.Position + new Vector3(0.1f, 0, 0));
                    break;
            }
            engine.Tick(Frame);
            AssertFlagsMatchScene(engine, world, sync);
        }
        Assert.Empty(world.Violations);
    }

    [Fact]
    public void Steady_frames_send_no_flag_updates_for_static_content()
    {
        using var engine = TestEnvironment.CreateEngine("flags-steady");
        for (int i = 0; i < 50; i++) Wall(engine.Scene, $"Wall {i}", new Vector3(i, 0, 0));
        var world = new RecordingRenderWorld();
        engine.AttachRenderWorld(world, loadFixtureModelsInBackground: false);
        engine.Tick(0);
        int updates = world.InstanceUpdates;
        for (int i = 0; i < 10; i++) engine.Tick(Frame);
        Assert.Equal(updates, world.InstanceUpdates);
        engine.Scene.Find(engine.Scene.Roots[3].Id)!.Visible = false;
        engine.Tick(Frame);
        Assert.Equal(updates + 1, world.InstanceUpdates); // only the wall that was hidden
    }

    [Fact]
    public void Selected_groups_outline_their_content_when_asked()
    {
        using var engine = TestEnvironment.CreateEngine("flags-outline");
        var group = new Entity("Group");
        engine.Scene.Add(group);
        var inner = new Entity("Inner");
        engine.Scene.Add(inner, group);
        var wall = Wall(engine.Scene, "Wall", Vector3.Zero, inner);
        var other = Wall(engine.Scene, "Other", new Vector3(5, 0, 0));
        var world = new RecordingRenderWorld();
        var sync = engine.AttachRenderWorld(world, loadFixtureModelsInBackground: false);
        engine.Selection.Set(group.Id);
        engine.Tick(0);
        Assert.Equal(InstanceFlags.None, sync.InstancesOf(wall.Id).Single().Flags & InstanceFlags.Selected);

        sync.OutlineSelectedDescendants = true;
        engine.Tick(Frame);
        Assert.Equal(InstanceFlags.Selected, sync.InstancesOf(wall.Id).Single().Flags & InstanceFlags.Selected);
        Assert.Equal(InstanceFlags.None, sync.InstancesOf(other.Id).Single().Flags & InstanceFlags.Selected);
        AssertFlagsMatchScene(engine, world, sync, outlineDescendants: true);

        engine.Scene.Reparent(other, inner); // moved into the selected group
        engine.Tick(Frame);
        AssertFlagsMatchScene(engine, world, sync, outlineDescendants: true);
        engine.Selection.Clear();
        engine.Tick(Frame);
        AssertFlagsMatchScene(engine, world, sync, outlineDescendants: true);
        Assert.Equal(InstanceFlags.None, sync.InstancesOf(wall.Id).Single().Flags & InstanceFlags.Selected);
    }

    [Fact]
    public void World_bounds_follow_what_is_drawn_per_entity_and_pick_id()
    {
        using var engine = TestEnvironment.CreateEngine("bounds");
        var wall = Wall(engine.Scene, "Wall", new Vector3(3, 2, 0));
        var head = SceneBuilder.Fixture(engine.Scene, SceneBuilder.MacViper, "Basic", new Vector3(-2, 0, 5));
        var group = new Entity("Group");
        engine.Scene.Add(group);
        var world = new RecordingRenderWorld();
        var sync = engine.AttachRenderWorld(world, loadFixtureModelsInBackground: false);
        engine.Tick(0);
        engine.Tick(Frame);

        Assert.True(sync.TryGetWorldBounds(wall.Id, out var wallBounds));
        var expected = Aabb.Empty;
        foreach (var info in sync.InstancesOf(wall.Id))
        {
            var mesh = world.Meshes[world.Instances[info.Handle.Value].Mesh.Value];
            expected.Include(mesh.ComputeBounds().Transform(world.Instances[info.Handle.Value].World));
            Assert.Equal(info.WorldBounds.Min, mesh.ComputeBounds().Transform(info.World).Min);
        }
        Assert.Equal(expected.Min, wallBounds.Min);
        Assert.Equal(expected.Max, wallBounds.Max);
        Assert.True(wallBounds.Contains(new Vector3(3, 2, 1)));
        Assert.True(sync.TryGetWorldBounds(sync.PickIdOf(wall.Id), out var byPick));
        Assert.Equal(wallBounds.Min, byPick.Min);

        Assert.True(sync.TryGetWorldBounds(head.Id, out var headBounds));
        Assert.True(headBounds.Size.Z > 0.2f && headBounds.Size.Z < 1.5f, $"MAC Viper body: {headBounds.Size}");
        Assert.True(Vector3.Distance(headBounds.Center, new Vector3(-2, 0, 5)) < 1f);
        Assert.False(sync.TryGetWorldBounds(group.Id, out _)); // nothing drawn for a group
        Assert.False(sync.TryGetWorldBounds(Guid.NewGuid(), out _));
        Assert.False(sync.TryGetWorldBounds(0u, out _));

        var all = sync.VisibleWorldBounds();
        Assert.True(all.Contains(wallBounds.Min) && all.Contains(wallBounds.Max) && all.Contains(headBounds.Center));
        var corners = new List<Vector3>();
        sync.CollectVisibleCorners(corners);
        Assert.Equal(8 * world.Instances.Count, corners.Count);

        // bounds follow moves; hidden entities only count when asked
        wall.Local = new Transform(new Vector3(13, 2, 0));
        wall.Visible = false;
        engine.Tick(Frame);
        Assert.True(sync.TryGetWorldBounds(wall.Id, out var moved));
        Assert.Equal(wallBounds.Min.X + 10f, moved.Min.X, 3);
        Assert.False(sync.TryGetWorldBounds(wall.Id, out _, includeHidden: false));
        var bounds = Aabb.Empty;
        Assert.False(sync.IncludeWorldBounds(sync.PickIdOf(wall.Id), ref bounds, includeHidden: false));
        Assert.True(sync.IncludeWorldBounds(sync.PickIdOf(head.Id), ref bounds));
        Assert.False(sync.VisibleWorldBounds().Contains(moved.Center));
    }

    [Fact]
    public void Work_light_lights_a_dark_scene_in_auto_and_never_enters_the_stage_light()
    {
        using var engine = TestEnvironment.CreateEngine("work-light");
        SceneBuilder.Floor(engine.Scene, 12f);
        var head = SceneBuilder.Fixture(engine.Scene, SceneBuilder.MacViper, "Basic", new Vector3(0, 0, 6));
        var world = new RecordingRenderWorld();
        var sync = engine.AttachRenderWorld(world, loadFixtureModelsInBackground: false);
        engine.Tick(0);

        // off by default: renders never get it unless asked
        Assert.Equal(WorkLightMode.Off, sync.WorkLight);
        Assert.Empty(world.Lights);
        Assert.False(sync.IsWorkLightOn);
        Assert.Equal(WorkLightMode.Auto, engine.View.WorkLight); // the editor's preview setting

        sync.WorkLight = WorkLightMode.Auto;
        engine.Tick(Frame);
        Assert.True(sync.IsWorkLightOn);
        Assert.Equal(WorkLightRig.LightCount, world.Lights.Length);
        Assert.Equal(0, engine.Lights.Length);
        Assert.Equal(0.0, engine.LightMeter.HorizontalIlluminanceAt(Vector3.Zero), 6);
        Assert.All(world.Lights, l => Assert.Equal(Guid.Empty, l.FixtureId));
        Assert.All(world.Lights, l => Assert.Equal(Vector3.One, l.Color));
        var key = world.Lights[0];
        Assert.True(key.CastShadows);
        Assert.Equal(1, world.Lights.Count(l => l.CastShadows));
        var centre = sync.VisibleWorldBounds().Center;
        Assert.Equal(WorkLightRig.KeyIlluminance, key.PeakCandela / Vector3.DistanceSquared(key.Position, centre), 2);
        Assert.True(Vector3.Dot(Vector3.Normalize(centre - key.Position), key.Direction) > 0.999f);
        Assert.True(key.Position.Y < centre.Y && key.Position.Z > centre.Z, "the key comes from the audience side, above");

        // deterministic: the same scene gives the same rig
        var first = world.Lights.ToArray();
        engine.Tick(Frame);
        Assert.Equal(first, world.Lights);

        // a lit fixture: Auto switches the work light off; On keeps it on top of the stage light
        StageLooks.ApplyOpenWhite(engine, new OpenWhiteOptions { Undoable = false });
        for (int i = 0; i < 3; i++) engine.Tick(Frame);
        Assert.True(engine.Lights.Length > 0);
        Assert.False(sync.IsWorkLightOn);
        Assert.Equal(engine.Lights.Length, world.Lights.Length);
        sync.WorkLight = WorkLightMode.On;
        engine.Tick(Frame);
        Assert.Equal(engine.Lights.Length + WorkLightRig.LightCount, world.Lights.Length);
        Assert.Equal(engine.Lights.ToArray(), world.Lights.Take(engine.Lights.Length));

        // the lux overlay measures stage light only
        engine.View.View.Overlay = OverlayMode.Lux;
        engine.Tick(Frame);
        Assert.False(sync.IsWorkLightOn);
        Assert.Equal(engine.Lights.Length, world.Lights.Length);
        engine.View.View.Overlay = OverlayMode.None;
        sync.WorkLight = WorkLightMode.Off;
        engine.Tick(Frame);
        Assert.Equal(engine.Lights.Length, world.Lights.Length);
        _ = head;
    }

    [Fact]
    public void Work_light_rig_covers_the_content_and_handles_empty_bounds()
    {
        var lights = new SpotLight[WorkLightRig.LightCount];
        WorkLightRig.Write(Aabb.Empty, lights);
        Assert.All(lights, l => Assert.True(l.PeakCandela > 0 && float.IsFinite(l.Position.X) && l.Iris == 1f));
        var bounds = new Aabb(new Vector3(-10, -6, 0), new Vector3(10, 6, 8));
        WorkLightRig.Write(bounds, lights);
        // every face direction has one ambient light looking at it
        foreach (var axis in new[] { Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ })
            Assert.Contains(lights.Skip(1), l => Vector3.Dot(l.Direction, -axis) > 0.999f);
        // the key's beam (50 %) cone holds the content's corners
        var key = lights[0];
        for (int i = 0; i < 8; i++)
        {
            var corner = new Vector3((i & 1) == 0 ? bounds.Min.X : bounds.Max.X, (i & 2) == 0 ? bounds.Min.Y : bounds.Max.Y, (i & 4) == 0 ? bounds.Min.Z : bounds.Max.Z);
            float angle = MathF.Acos(Vector3.Dot(Vector3.Normalize(corner - key.Position), key.Direction));
            Assert.True(angle <= key.BeamAngle / 2f + 1e-4f, $"corner {corner} at {angle * 180 / MathF.PI:0.0}°");
        }
        Assert.Throws<ArgumentException>(() => WorkLightRig.Write(bounds, new SpotLight[3]));
        Assert.False(WorkLightRig.IsNeeded(WorkLightMode.Off, ReadOnlySpan<SpotLight>.Empty));
        Assert.True(WorkLightRig.IsNeeded(WorkLightMode.Auto, new[] { new SpotLight { PeakCandela = 0.2f } }));
        Assert.False(WorkLightRig.IsNeeded(WorkLightMode.Auto, new[] { new SpotLight { PeakCandela = 50f } }));
    }
}
