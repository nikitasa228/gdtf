using System.Numerics;
using Candela.Core.Math;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;

namespace Candela.Electrical.Tests;

/// <summary>
/// <see cref="NetworkSolver.UpdateGeometry"/>: when entities that cable nodes are anchored to move, only the cables that
/// move with them are measured again, and the result equals a fresh solve of the moved scene — cables, data lines with
/// their run lengths, power, and the issues in the same order.
/// </summary>
public class GeometryUpdateTests
{
    /// <summary>
    /// A console whose DMX A runs along four moving heads hung on a truss (children of the truss, cables anchored to the
    /// heads), a network console with an Ethernet cable to a head standing on the floor, and a distro feeding two AECO 20
    /// on the truss through TRUE1 links.
    /// </summary>
    private sealed record Rig(SceneKit Kit, Entity Truss, Entity Desk, List<Entity> Heads, List<Entity> DmxCables, Entity Floor, Entity Ethernet,
        List<Entity> PowerCables);

    private static Rig Build(float consoleDistance = 12f, float floorHeadDistance = 20f)
    {
        var kit = new SceneKit();
        var truss = kit.Truss("Truss", new Vector3(0, 0, 6), 12f);
        var desk = kit.Console("Desk", new Vector3(0, -consoleDistance, 0), SceneKit.DmxOutput("DMX A", 1));
        var net = kit.Console("Net desk", new Vector3(0, -consoleDistance, 0), SceneKit.EthernetOutput("Net 1", 1, 4));
        var heads = Enumerable.Range(0, 4)
            .Select(i => kit.Fixture(TestData.Naja, $"Head {i + 1}", 101 + i, new Vector3(-4.5f + i * 3f, 0, 0), universe: 1, address: 1 + i * 40, parent: truss))
            .ToList();
        var dmx = kit.Chain("dmx-xlr5", "D", (desk, "DMX A"), heads, "XLR IN", "XLR OUT");
        var floor = kit.Fixture(TestData.Naja, "Floor head", 201, new Vector3(0, floorHeadDistance - consoleDistance, 0), universe: 2, address: 1);
        var ethernet = kit.Cable("ethernet", "E1", (net, "Net 1"), (floor, "ETH IN"));
        var distro = kit.Source("Distro", new Vector3(6, -2, 0), outlets: SceneKit.Outlet("Outlet 1"));
        var aecos = Enumerable.Range(0, 2)
            .Select(i => kit.Fixture(TestData.Aeco20, $"AECO {i + 1}", 301 + i, new Vector3(-1f + i * 2f, 0.5f, 0), parent: truss))
            .ToList();
        var power = kit.Chain("powercon-true1", "P", (distro, "Outlet 1"), aecos, "Power In", "Power Out");
        return new Rig(kit, truss, desk, heads, dmx, floor, ethernet, power);
    }

    private static void Move(Entity e, Vector3 by) => e.Local = e.Local with { Position = e.Local.Position + by };

    /// <summary>The cables the move of <paramref name="moved"/> carries: anchored in its subtree, or carried by it.</summary>
    private static List<Guid> CablesMovedWith(Scene scene, Entity moved)
    {
        var ids = moved.SelfAndDescendants().Select(e => e.Id).ToHashSet();
        return scene.Query<CableComponent>()
            .Where(c => ids.Contains(c.Entity.Id) || c.Component.Nodes.Any(n => n.Anchor is { } a && ids.Contains(a.EntityId)))
            .Select(c => c.Entity.Id)
            .ToList();
    }

    /// <summary>Asserts that two solutions describe the same network: cables, data, power and issues (in order).</summary>
    private static void AssertSameNetwork(NetworkSolution expected, NetworkSolution actual)
    {
        Assert.Equal(expected.Cables.Select(c => c.EntityId), actual.Cables.Select(c => c.EntityId));
        for (int i = 0; i < expected.Cables.Count; i++)
        {
            var e = expected.Cables[i];
            var a = actual.Cables[i];
            Assert.Equal(e.LengthM, a.LengthM, 4);
            Assert.Equal(e.Geometry.RoutedLengthM, a.Geometry.RoutedLengthM, 4);
            Assert.Equal(e.WeightKg, a.WeightKg, 4);
            Assert.Equal(e.Geometry.Nodes.Select(n => n.WorldPosition), a.Geometry.Nodes.Select(n => n.WorldPosition));
            Assert.Equal(e.Endpoints.Select(p => (p.EntityId, p.PortName, p.IsUsable)), a.Endpoints.Select(p => (p.EntityId, p.PortName, p.IsUsable)));
            Assert.Equal((e.UpstreamEndpointIndex, e.PeakCurrentA, e.LoadW), (a.UpstreamEndpointIndex, a.PeakCurrentA, a.LoadW));
        }

        Assert.Equal(expected.DataLines.Count, actual.DataLines.Count);
        for (int i = 0; i < expected.DataLines.Count; i++)
        {
            var e = expected.DataLines[i].Feeds;
            var a = actual.DataLines[i].Feeds;
            Assert.Equal(e.Select(f => (f.DeviceEntityId, f.InputPort, f.ChainIndex, f.FirstUniverse)), a.Select(f => (f.DeviceEntityId, f.InputPort, f.ChainIndex, f.FirstUniverse)));
            Assert.Equal(e.Select(f => f.RunLengthM), a.Select(f => f.RunLengthM));
            Assert.Equal(e.Select(f => string.Join(",", f.CablePath)), a.Select(f => string.Join(",", f.CablePath)));
        }
        Assert.Equal(expected.ConsoleDrivenFixtures.OrderBy(g => g), actual.ConsoleDrivenFixtures.OrderBy(g => g));
        Assert.Equal(expected.PowerCircuits.Select(c => (c.SourceEntityId, c.Outlet.Name, c.LoadW, c.CurrentA)),
            actual.PowerCircuits.Select(c => (c.SourceEntityId, c.Outlet.Name, c.LoadW, c.CurrentA)));
        Assert.Equal(expected.PowerFeeds.Keys.OrderBy(g => g), actual.PowerFeeds.Keys.OrderBy(g => g));
        Assert.Equal(expected.Issues.Select(i => (i.Severity, i.Code, i.Message, string.Join(",", i.Entities))),
            actual.Issues.Select(i => (i.Severity, i.Code, i.Message, string.Join(",", i.Entities))));
    }

    [Fact]
    public void Moving_a_truss_renews_the_cables_anchored_to_it_like_a_fresh_solve()
    {
        var rig = Build();
        var scene = rig.Kit.Scene;
        var before = rig.Kit.Solve();
        var oldLengths = before.Cables.Select(c => c.LengthM).ToList();
        var oldRuns = before.DataLines.SelectMany(l => l.Feeds).Select(f => f.RunLengthM).ToList();

        Move(rig.Truss, new Vector3(0, 8, 2));
        var moved = CablesMovedWith(scene, rig.Truss);
        Assert.Equal(rig.DmxCables.Count + rig.PowerCables.Count, moved.Count); // the heads, links and their cables, not the Ethernet run
        var updated = rig.Kit.Solver.UpdateGeometry(before, scene, moved);
        Assert.Same(updated, rig.Kit.Solver.GetOrSolve(scene)); // the solver's cache follows the update

        AssertSameNetwork(rig.Kit.Solver.Solve(scene), updated);
        Assert.Equal(scene.Version, updated.SceneVersion);
        // the DMX run from the console grew with the truss; the first link is longer, links between heads did not change
        var line = updated.DataLines.Single(l => l.Output.Name == "DMX A");
        Assert.True(updated.FindCable(rig.DmxCables[0].Id)!.LengthM > before.FindCable(rig.DmxCables[0].Id)!.LengthM + 5f);
        Assert.Equal(before.FindCable(rig.DmxCables[2].Id)!.LengthM, updated.FindCable(rig.DmxCables[2].Id)!.LengthM, 4);
        Assert.True(line.Feeds[^1].RunLengthM > before.DataLines.Single(l => l.Output.Name == "DMX A").Feeds[^1].RunLengthM + 5f);

        // the old solution still describes the old positions
        Assert.Equal(oldLengths, before.Cables.Select(c => c.LengthM));
        Assert.Equal(oldRuns, before.DataLines.SelectMany(l => l.Feeds).Select(f => f.RunLengthM));
    }

    [Fact]
    public void Only_the_cables_that_moved_are_measured_again()
    {
        var rig = Build();
        var scene = rig.Kit.Scene;
        var before = rig.Kit.Solve();

        // a power-only move: the data network is taken over as it is
        Move(rig.Truss, new Vector3(1, 0, 0));
        var powerOnly = rig.Kit.Solver.UpdateGeometry(before, scene, rig.PowerCables.Select(c => c.Id));
        foreach (var cable in before.Cables)
        {
            bool moved = rig.PowerCables.Any(p => p.Id == cable.EntityId);
            Assert.Equal(!moved, ReferenceEquals(cable, powerOnly.FindCable(cable.EntityId)));
        }
        Assert.Same(before.DataLines, powerOnly.DataLines);
        Assert.Same(before.PowerSources, powerOnly.PowerSources);
        Assert.Same(before.Devices[rig.Desk.Id], powerOnly.Devices[rig.Desk.Id]);

        // a data move runs the data walk again over the new lengths; the untouched Ethernet cable is the same object
        var both = rig.Kit.Solver.UpdateGeometry(powerOnly, scene, CablesMovedWith(scene, rig.Truss));
        Assert.Same(powerOnly.FindCable(rig.Ethernet.Id), both.FindCable(rig.Ethernet.Id));
        Assert.NotSame(powerOnly.DataLines, both.DataLines);
        AssertSameNetwork(rig.Kit.Solver.Solve(scene), both);

        // ids of entities that carry no cable are ignored: nothing to renew
        Assert.Same(both, rig.Kit.Solver.UpdateGeometry(both, scene, [rig.Truss.Id, rig.Heads[0].Id, Guid.NewGuid()]));
    }

    [Fact]
    public void A_dmx_line_moved_past_its_maximum_run_is_reported_and_moving_back_clears_it()
    {
        var rig = Build();
        var scene = rig.Kit.Scene;
        var solution = rig.Kit.Solve();
        solution.Issues.None(IssueCodes.DataRunTooLong);

        Move(rig.Truss, new Vector3(0, 320, 0));
        solution = rig.Kit.Solver.UpdateGeometry(solution, scene, CablesMovedWith(scene, rig.Truss));
        var issue = solution.Issues.Single(IssueCodes.DataRunTooLong);
        Assert.Contains("DMX line", issue.Message);
        AssertSameNetwork(rig.Kit.Solver.Solve(scene), solution);

        Move(rig.Truss, new Vector3(0, -320, 0));
        solution = rig.Kit.Solver.UpdateGeometry(solution, scene, CablesMovedWith(scene, rig.Truss));
        solution.Issues.None(IssueCodes.DataRunTooLong);
        AssertSameNetwork(rig.Kit.Solver.Solve(scene), solution);
    }

    [Fact]
    public void An_ethernet_link_stretched_past_100_m_is_reported()
    {
        var rig = Build(consoleDistance: 12f, floorHeadDistance: 80f);
        var scene = rig.Kit.Scene;
        var solution = rig.Kit.Solve();
        solution.Issues.None(IssueCodes.DataRunTooLong);

        Move(rig.Floor, new Vector3(0, 40, 0));
        var moved = CablesMovedWith(scene, rig.Floor);
        Assert.Equal([rig.Ethernet.Id], moved);
        solution = rig.Kit.Solver.UpdateGeometry(solution, scene, moved);
        Assert.Contains("Ethernet cable 'E1'", solution.Issues.Single(IssueCodes.DataRunTooLong).Message);
        AssertSameNetwork(rig.Kit.Solver.Solve(scene), solution);
    }

    [Fact]
    public void A_moved_cable_entity_carries_its_free_nodes()
    {
        var rig = Build();
        var scene = rig.Kit.Scene;
        // a free node in the middle of the Ethernet run (a cable tie on the floor)
        var cable = rig.Ethernet.Get<CableComponent>()!;
        var tie = new CableNode { Position = new Vector3(5, -6, 0) };
        cable.Nodes.Add(tie);
        cable.Segments.Clear();
        cable.Segments.Add(new CableSegment(cable.Nodes[0].Id, tie.Id));
        cable.Segments.Add(new CableSegment(tie.Id, cable.Nodes[1].Id));
        cable.NotifyChanged();
        var solution = rig.Kit.Solve();

        rig.Ethernet.Local = new Transform(new Vector3(10, 0, 0));
        solution = rig.Kit.Solver.UpdateGeometry(solution, scene, CablesMovedWith(scene, rig.Ethernet));
        Assert.True(solution.FindCable(rig.Ethernet.Id)!.Geometry.TryGetPosition(tie.Id, out var p));
        Assert.Equal(new Vector3(15, -6, 0), p);
        AssertSameNetwork(rig.Kit.Solver.Solve(scene), solution);
    }

    [Fact]
    public void Falls_back_to_a_full_solve_when_the_cabling_itself_changed()
    {
        var rig = Build();
        var scene = rig.Kit.Scene;
        var solution = rig.Kit.Solve();

        // a cable the solution does not know
        var extra = rig.Kit.Cable("dmx-xlr5", "D5", (rig.Heads[^1], "XLR OUT"), (rig.Floor, "XLR IN"));
        var updated = rig.Kit.Solver.UpdateGeometry(solution, scene, [extra.Id]);
        Assert.NotNull(updated.FindCable(extra.Id));
        AssertSameNetwork(rig.Kit.Solver.Solve(scene), updated);

        // a cable whose component was replaced
        var replacement = new CableComponent { CableTypeId = "dmx-xlr3", Label = "D1" };
        var old = rig.DmxCables[0].Get<CableComponent>()!;
        replacement.Nodes.AddRange(old.Nodes);
        replacement.Segments.AddRange(old.Segments);
        replacement.Endpoints.AddRange(old.Endpoints);
        rig.DmxCables[0].Remove(old);
        rig.DmxCables[0].Add(replacement);
        updated = rig.Kit.Solver.UpdateGeometry(updated, scene, [rig.DmxCables[0].Id]);
        Assert.Equal("dmx-xlr3", updated.FindCable(rig.DmxCables[0].Id)!.TypeId);
        AssertSameNetwork(rig.Kit.Solver.Solve(scene), updated);

        // a solution of another solver, or of another scene
        var other = new NetworkSolver(TestData.Profiles);
        AssertSameNetwork(rig.Kit.Solver.Solve(scene), other.UpdateGeometry(updated, scene, [rig.DmxCables[1].Id]));
        var elsewhere = new SceneKit().Solve();
        Assert.Equal(scene.Id, rig.Kit.Solver.UpdateGeometry(elsewhere, scene, [rig.DmxCables[1].Id]).SceneId);
    }

    [Fact]
    public void Renewing_a_large_rig_after_a_truss_move_touches_only_its_cables()
    {
        // 400 heads on 40 trusses, each truss with a DMX line of ten heads; one truss moves
        var kit = new SceneKit();
        var desk = kit.Console("Desk", new Vector3(0, -15, 0), Enumerable.Range(0, 40).Select(i => SceneKit.DmxOutput($"DMX {i + 1}", i + 1)).ToArray());
        var trusses = new List<Entity>();
        for (int t = 0; t < 40; t++)
        {
            var truss = kit.Truss($"Truss {t + 1}", new Vector3(0, t * 1.5f, 6), 15f);
            trusses.Add(truss);
            var heads = Enumerable.Range(0, 10)
                .Select(i => kit.Fixture(TestData.Naja, $"Head {t + 1}.{i + 1}", 1000 + t * 10 + i, new Vector3(-7f + i * 1.5f, 0, 0), t + 1, 1 + i * 40, parent: truss))
                .ToList();
            kit.Chain("dmx-xlr5", $"T{t + 1}-", (desk, $"DMX {t + 1}"), heads, "XLR IN", "XLR OUT");
        }
        var solution = kit.Solve();
        Move(trusses[17], new Vector3(0, 0, 1));
        var moved = CablesMovedWith(kit.Scene, trusses[17]);
        Assert.Equal(10, moved.Count);
        var updated = kit.Solver.UpdateGeometry(solution, kit.Scene, moved);
        Assert.Equal(400 - 10, solution.Cables.Count(c => ReferenceEquals(c, updated.FindCable(c.EntityId))));
        AssertSameNetwork(kit.Solver.Solve(kit.Scene), updated);
    }
}
