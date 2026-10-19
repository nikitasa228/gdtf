using System.Numerics;
using Candela.Core.Math;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;
using Candela.Electrical;
using Candela.Rendering;

namespace Candela.Engine.Tests;

/// <summary>
/// Follow-ups of the cabling work: the engine's network solution follows anchors that move, and what the cables,
/// devices and the work light look like.
/// </summary>
public sealed class CablingFollowUpTests
{
    /// <summary>A console, a truss with two moving heads hung on it (children) daisy-chained from DMX A, and a floor.</summary>
    private static (StageEngine Engine, Entity Truss, Entity Desk, Entity[] Heads, Entity[] Cables) TrussRig(string name)
    {
        var engine = TestEnvironment.CreateEngine(name);
        var scene = engine.Scene;
        var desk = SceneBuilder.Console(scene, new Vector3(0, -10, 0));
        var truss = new Entity("Truss") { Local = new Transform(new Vector3(0, 0, 6)) };
        truss.Add(new TrussComponent { Length = 8f });
        scene.Add(truss);
        var heads = new Entity[2];
        for (int i = 0; i < heads.Length; i++)
        {
            heads[i] = SceneBuilder.Fixture(scene, SceneBuilder.Naja, null, new Vector3(-2 + 4 * i, 0, 0), universe: 1, address: 1 + 40 * i,
                name: $"Head {i + 1}", fixtureId: 101 + i);
            scene.Reparent(heads[i], truss, keepWorldTransform: false);
        }
        var cables = new[]
        {
            SceneBuilder.Cable(scene, desk, "DMX A", heads[0], "XLR IN"),
            SceneBuilder.Cable(scene, heads[0], "XLR OUT", heads[1], "XLR IN"),
        };
        return (engine, truss, desk, heads, cables);
    }

    [Fact]
    public void The_network_solution_follows_a_moved_anchor_and_renews_only_the_cables_that_moved()
    {
        var (engine, truss, _, heads, cables) = TrussRig("anchor-move");
        using var _ = engine;
        engine.Tick(1.0 / 30);
        var before = engine.NetworkSolution;
        Assert.True(before.IsConsoleDriven(heads[1].Id));
        float firstLink = before.FindCable(cables[0].Id)!.LengthM;
        float secondLink = before.FindCable(cables[1].Id)!.LengthM;

        // something without cables moves: the same solution
        var lamp = SceneBuilder.Fixture(engine.Scene, SceneBuilder.Naja, null, new Vector3(5, 5, 0), name: "Lamp");
        var spare = SceneBuilder.Fixture(engine.Scene, SceneBuilder.Naja, null, new Vector3(9, 5, 0), name: "Spare");
        var lampFree = SceneBuilder.Cable(engine.Scene, lamp, "XLR OUT", spare, "XLR IN");
        engine.Tick(1.0 / 30);
        var withLamp = engine.NetworkSolution; // the lamp was added: solved again
        var truss2 = new Entity("Empty truss") { Local = new Transform(new Vector3(0, 8, 6)) };
        truss2.Add(new TrussComponent { Length = 4f });
        engine.Scene.Add(truss2);
        withLamp = engine.NetworkSolution;
        truss2.Local = new Transform(new Vector3(0, 9, 6));
        engine.Tick(1.0 / 30);
        Assert.Same(withLamp, engine.NetworkSolution);

        // the truss is flown out 3 m: the cable from the desk to the first head grows, the link between the heads does not
        truss.Local = new Transform(new Vector3(0, 0, 9));
        engine.Tick(1.0 / 30);
        var after = engine.NetworkSolution;
        Assert.NotSame(withLamp, after);
        Assert.Equal(engine.Scene.Version, after.SceneVersion);
        var fresh = new NetworkSolver(engine.ElectricalProfiles).Solve(engine.Scene);
        Assert.Equal(fresh.FindCable(cables[0].Id)!.LengthM, after.FindCable(cables[0].Id)!.LengthM, 4);
        Assert.True(after.FindCable(cables[0].Id)!.LengthM > firstLink + 1f);
        Assert.Equal(secondLink, after.FindCable(cables[1].Id)!.LengthM, 4);
        Assert.Same(withLamp.FindCable(lampFree.Id), after.FindCable(lampFree.Id)); // a cable that did not move is not measured again
        Assert.Equal(fresh.DataLines[0].Feeds.Select(f => f.RunLengthM), after.DataLines[0].Feeds.Select(f => f.RunLengthM));
        Assert.True(after.IsConsoleDriven(heads[0].Id) && after.IsConsoleDriven(heads[1].Id));

        // moving the last head renews only the cable plugged into it; the solver's cache is the engine's solution
        heads[1].Local = new Transform(new Vector3(3, 0, 0));
        var moved = engine.NetworkSolution;
        Assert.Same(after.FindCable(cables[0].Id), moved.FindCable(cables[0].Id));
        Assert.Equal(new NetworkSolver(engine.ElectricalProfiles).Solve(engine.Scene).FindCable(cables[1].Id)!.LengthM,
            moved.FindCable(cables[1].Id)!.LengthM, 4);
        Assert.Same(moved, engine.Network.GetOrSolve(engine.Scene));

        // renaming a device solves again (labels and messages are part of the solution)
        heads[0].Name = "Spot left";
        Assert.Contains(engine.NetworkSolution.Devices.Values, d => d.Name == "Spot left");
    }

    [Fact]
    public void Without_consoles_the_solution_also_follows_moved_anchors()
    {
        using var engine = TestEnvironment.CreateEngine("anchor-noconsole");
        var scene = engine.Scene;
        var a = SceneBuilder.Fixture(scene, SceneBuilder.Naja, null, new Vector3(0, 0, 6), name: "A");
        var b = SceneBuilder.Fixture(scene, SceneBuilder.Naja, null, new Vector3(4, 0, 6), name: "B");
        var cable = SceneBuilder.Cable(scene, a, "XLR OUT", b, "XLR IN");
        Assert.False(engine.HasConsoles);
        Assert.Equal(4f, engine.NetworkSolution.FindCable(cable.Id)!.Geometry.RoutedLengthM, 3);
        b.Local = new Transform(new Vector3(10, 0, 6));
        Assert.Equal(10f, engine.NetworkSolution.FindCable(cable.Id)!.Geometry.RoutedLengthM, 3);
        engine.Tick(1.0 / 30);
        Assert.Equal(10f, engine.NetworkSolution.FindCable(cable.Id)!.Geometry.RoutedLengthM, 3);
    }
    /// <summary>A cable through free nodes at <paramref name="points"/> (in order), as the cable tool draws one on the ground.</summary>
    private static Entity FreeCable(Scene scene, string name, float slack, params Vector3[] points)
    {
        var entity = new Entity(name);
        var cable = new CableComponent { CableTypeId = "dmx-xlr5", SlackFactor = slack };
        foreach (var p in points) cable.Nodes.Add(new CableNode { Position = p });
        for (int i = 0; i + 1 < cable.Nodes.Count; i++) cable.Segments.Add(new CableSegment(cable.Nodes[i].Id, cable.Nodes[i + 1].Id));
        entity.Add(cable);
        scene.Add(entity);
        return entity;
    }

    /// <summary>The world-space vertices of the tube RenderSync drew for <paramref name="cable"/>.</summary>
    private static Vector3[] TubeVertices(RecordingRenderWorld world, RenderSync sync, Entity cable)
    {
        uint pick = sync.PickIdOf(cable.Id);
        Assert.NotEqual(0u, pick);
        var result = new List<Vector3>();
        foreach (var instance in world.Instances.Values)
        {
            if (instance.PickId != pick) continue;
            foreach (var p in world.Meshes[instance.Mesh.Value].Positions) result.Add(Vector3.Transform(p, instance.World));
        }
        Assert.NotEmpty(result);
        return result.ToArray();
    }

    [Fact]
    public void Cables_lying_on_a_floor_do_not_sag_below_it_and_keep_their_lengths()
    {
        using var engine = TestEnvironment.CreateEngine("cable-floor");
        var scene = engine.Scene;
        var floor = SceneBuilder.Floor(scene, 30f); // top face at z = 0
        var riser = new Entity("Riser") { Local = new Transform(new Vector3(8, 0, 0.6f)) };
        riser.Add(new WallComponent { Kind = WallKind.Floor, Width = 4f, Height = 4f, Thickness = 0.6f });
        scene.Add(riser);
        var truss = new Entity("Truss") { Local = new Transform(new Vector3(0, 6, 6)) };
        truss.Add(new TrussComponent { Length = 8f });
        scene.Add(truss);

        // free nodes 2 cm above the floor (where the cable tool puts them), lots of slack so a hanging span would sag ~1 m
        const float slack = 1.2f;
        var floorRun = FreeCable(scene, "Floor run", slack, new(-10, -6, 0.02f), new(0, -6, 0.02f), new(0, 4, 0.02f), new(5, 4, 0.02f));
        // a run on the riser, its nodes fixed to it
        var onRiser = new Entity("On riser");
        var riserCable = new CableComponent { CableTypeId = "powercon-true1", SlackFactor = slack };
        var r1 = new CableNode { Anchor = new CableAnchor(riser.Id, new Vector3(-1.5f, -1.5f, 0.02f)) };
        var r2 = new CableNode { Anchor = new CableAnchor(riser.Id, new Vector3(1.5f, 1.5f, 0.02f)) };
        var r3 = new CableNode { Position = new Vector3(8, 6, 0.02f) }; // down to the floor beside the riser
        riserCable.Nodes.AddRange([r1, r2, r3]);
        riserCable.Segments.AddRange([new(r1.Id, r2.Id), new(r2.Id, r3.Id)]);
        onRiser.Add(riserCable);
        scene.Add(onRiser);
        // a drop from the truss to the floor: it hangs, but never below the floor
        var drop = new Entity("Drop");
        var dropCable = new CableComponent { CableTypeId = "dmx-xlr5", SlackFactor = slack };
        var d1 = new CableNode { Anchor = new CableAnchor(truss.Id, new Vector3(-3, 0, 0)) };
        var d2 = new CableNode { Anchor = new CableAnchor(truss.Id, new Vector3(3, 0, 0)) };
        var d3 = new CableNode { Position = new Vector3(3, 10, 0.02f) };
        dropCable.Nodes.AddRange([d1, d2, d3]);
        dropCable.Segments.AddRange([new(d1.Id, d2.Id), new(d2.Id, d3.Id)]);
        drop.Add(dropCable);
        scene.Add(drop);

        var world = new RecordingRenderWorld();
        var sync = engine.AttachRenderWorld(world, loadFixtureModelsInBackground: false);
        engine.Tick(1.0 / 30);
        Assert.Empty(world.Violations);

        var run = TubeVertices(world, sync, floorRun);
        Assert.All(run, p => Assert.True(p.Z >= -1e-4f, $"floor run vertex at z = {p.Z}"));
        Assert.All(run, p => Assert.True(p.Z <= 0.02f + 0.0041f, $"floor run vertex at z = {p.Z}: the run lies flat")); // no hump either

        var risen = TubeVertices(world, sync, onRiser);
        Assert.All(risen, p => Assert.True(p.Z >= -1e-4f, $"riser cable vertex at z = {p.Z}"));
        Assert.All(risen.Where(p => p.X is > 6.01f and < 9.99f && p.Y is > -1.99f and < 1.99f),
            p => Assert.True(p.Z >= 0.6f - 1e-4f, $"riser cable vertex at z = {p.Z} inside the riser"));
        Assert.All(risen.Where(p => p.Y < 1.4f), p => Assert.InRange(p.Z, 0.62f - 0.0061f, 0.62f + 0.0061f)); // fixed along the riser: straight

        var hanging = TubeVertices(world, sync, drop);
        Assert.All(hanging, p => Assert.True(p.Z >= -1e-4f, $"drop vertex at z = {p.Z}"));
        Assert.All(hanging.Where(p => p.Y < 6.01f && p.Z > 5f), p => Assert.InRange(p.Z, 6f - 0.0041f, 6f + 0.0041f)); // along the truss
        Assert.Contains(hanging, p => p.Y > 7f && p.Z < 0.02f + 0.0041f); // the free span reaches the floor and lies on it

        // stored lengths are routed length × the cable's slack, whatever the drawing does
        var solution = engine.NetworkSolution;
        Assert.Equal(25f * slack, solution.FindCable(floorRun.Id)!.LengthM, 3);
        foreach (var cable in new[] { floorRun, onRiser, drop })
        {
            var geometry = CableGeometry.Compute(scene, cable, cable.Get<CableComponent>()!);
            Assert.Equal(geometry.RoutedLengthM * slack, solution.FindCable(cable.Id)!.LengthM, 3);
        }

        // without anything to rest on the same run sags through where the floor was: the support is what keeps it up
        scene.Remove(floor);
        sync.CableSupports.GroundHeight = null;
        engine.Tick(1.0 / 30);
        Assert.True(TubeVertices(world, sync, floorRun).Min(p => p.Z) < -0.5f);
        sync.CableSupports.GroundHeight = 0f;
        engine.Tick(1.0 / 30);
        Assert.True(TubeVertices(world, sync, floorRun).Min(p => p.Z) >= -1e-4f);

        // lowering the riser redraws the cables on it: the anchored span follows, the span to the floor still rests on it
        riser.Local = new Transform(new Vector3(8, 0, 0.3f));
        engine.Tick(1.0 / 30);
        risen = TubeVertices(world, sync, onRiser);
        Assert.All(risen.Where(p => p.X is > 6.01f and < 9.99f && p.Y is > -1.99f and < 1.99f),
            p => Assert.True(p.Z >= 0.3f - 1e-4f, $"riser cable vertex at z = {p.Z} inside the lowered riser"));
        Assert.Empty(world.Violations);
    }

    [Fact]
    public void A_free_run_left_on_a_lowered_floor_drapes_onto_it()
    {
        using var engine = TestEnvironment.CreateEngine("cable-floor-moved");
        var scene = engine.Scene;
        var deck = new Entity("Deck") { Local = new Transform(new Vector3(0, 0, 1f)) };
        deck.Add(new WallComponent { Kind = WallKind.Floor, Width = 10f, Height = 10f, Thickness = 0.2f });
        scene.Add(deck);
        var cable = FreeCable(scene, "Deck run", 1.1f, new(-4, 0, 1.02f), new(4, 0, 1.02f));
        var world = new RecordingRenderWorld();
        var sync = engine.AttachRenderWorld(world, loadFixtureModelsInBackground: false);
        engine.Tick(1.0 / 30);
        Assert.All(TubeVertices(world, sync, cable), p => Assert.InRange(p.Z, 1.02f - 0.0041f, 1.02f + 0.0041f)); // flat on the deck

        // the deck is lowered by 40 cm under the cable's free nodes: the span now hangs and lies on the deck, not through it
        deck.Local = new Transform(new Vector3(0, 0, 0.6f));
        engine.Tick(1.0 / 30);
        var tube = TubeVertices(world, sync, cable);
        Assert.All(tube, p => Assert.True(p.Z >= 0.6f - 1e-4f, $"vertex at z = {p.Z} below the deck"));
        Assert.Contains(tube, p => MathF.Abs(p.X) < 1f && p.Z < 0.6f + 0.0081f);
        Assert.Empty(world.Violations);
    }
}
