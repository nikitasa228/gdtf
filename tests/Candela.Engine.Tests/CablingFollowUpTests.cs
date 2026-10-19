using System.Numerics;
using Candela.Core.Math;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;
using Candela.Electrical;

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
}
