using System.Numerics;
using Candela.Core.Math;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;

namespace Candela.Electrical.Tests;

public class CableGeometryTests
{
    [Fact]
    public void Cable_length_follows_a_moved_truss_through_anchored_nodes()
    {
        var kit = new SceneKit();
        var desk = kit.Console("Desk", new Vector3(0, 0, 0), SceneKit.DmxOutput("DMX A", 1));
        var truss = kit.Truss("Truss 1", new Vector3(0, 0, 6), 6f);
        var head = kit.Fixture(TestData.Naja, "Head", 1, new Vector3(4, 0, 5.6f), 1, 1, mountedOn: truss.Id);

        // Desk → up to the truss (anchored at 0 m along it) → along the truss (anchored at 4 m) → down to the head.
        var cableEntity = new Entity("D1");
        var cable = new CableComponent { CableTypeId = "dmx-xlr5", Label = "D1", SlackFactor = 1.1f };
        var n0 = new CableNode { Anchor = new CableAnchor(desk.Id, Vector3.Zero) };
        var n1 = new CableNode { Anchor = new CableAnchor(truss.Id, Vector3.Zero) };
        var n2 = new CableNode { Anchor = new CableAnchor(truss.Id, new Vector3(4, 0, 0)) };
        var n3 = new CableNode { Anchor = new CableAnchor(head.Id, Vector3.Zero) };
        cable.Nodes.AddRange([n0, n1, n2, n3]);
        cable.Segments.AddRange([new(n0.Id, n1.Id), new(n1.Id, n2.Id), new(n2.Id, n3.Id)]);
        cable.Endpoints.Add(new CableEndpoint { NodeId = n0.Id, EntityId = desk.Id, PortName = "DMX A" });
        cable.Endpoints.Add(new CableEndpoint { NodeId = n3.Id, EntityId = head.Id, PortName = "XLR IN" });
        cableEntity.Add(cable);
        kit.Scene.Add(cableEntity);

        var s = kit.Solve();
        var info = s.FindCable(cableEntity.Id)!;
        Assert.Equal(6f + 4f + 0.4f, info.Geometry.RoutedLengthM, 4);
        Assert.Equal(10.4f * 1.1f, info.LengthM, 4);
        Assert.Equal(10.4f * 1.1f * 0.055f, info.WeightKg, 4);
        Assert.Equal(10.4f * 1.1f, s.DataFeedOf(head.Id)!.RunLengthM, 4);

        // Raise the truss by 2 m: the riser grows; the run along the truss does not. (The head is not a child of the
        // truss, so it stays where it is and the drop grows too.)
        truss.Local = new Transform(new Vector3(0, 0, 8));
        var moved = kit.Solver.GetOrSolve(kit.Scene).FindCable(cableEntity.Id)!;
        Assert.Equal(8f + 4f + 2.4f, moved.Geometry.RoutedLengthM, 4);

        // Turn the truss 90° about Z: the second anchor swings from +X to +Y.
        truss.Local = new Transform(new Vector3(0, 0, 8), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2), Vector3.One);
        var geometry = CableGeometry.Compute(kit.Scene, cableEntity, cable);
        Assert.True(geometry.TryGetPosition(n2.Id, out var p2));
        Assert.True(Vector3.Distance(new Vector3(0, 4, 8), p2) < 1e-4f);

        var schedule = CableSchedule.Build(kit.Solve());
        var row = schedule.Rows.Single();
        Assert.Equal("D1", row.Label);
        Assert.Equal("dmx-xlr5", row.TypeId);
        Assert.Equal(desk.Id, row.From!.EntityId);
        Assert.Equal("XLR IN", row.To.Single().PortName);
        Assert.Equal(row.LengthM, schedule.TotalLengthM);
        Assert.Equal(1, schedule.Totals.Single().Count);
    }

    [Fact]
    public void Branched_cables_measure_paths_along_the_tree()
    {
        var e = new Entity("Cable");
        var c = new CableComponent { SlackFactor = 1f };
        var a = new CableNode { Position = new Vector3(0, 0, 0) };
        var hub = new CableNode { Position = new Vector3(3, 0, 0) };
        var b = new CableNode { Position = new Vector3(3, 4, 0) };
        var d = new CableNode { Position = new Vector3(3, 0, 2) };
        c.Nodes.AddRange([a, hub, b, d]);
        c.Segments.AddRange([new(a.Id, hub.Id), new(hub.Id, b.Id), new(hub.Id, d.Id)]);
        e.Add(c);
        var g = CableGeometry.Compute(null, e, c);
        Assert.Equal(9f, g.RoutedLengthM, 5);
        Assert.Equal(7f, g.PathLengthM(a.Id, b.Id)!.Value, 5);
        Assert.Equal(6f, g.PathLengthM(b.Id, d.Id)!.Value, 5);
        Assert.Equal([b.Id, hub.Id, d.Id], g.Path(b.Id, d.Id));
        Assert.Null(g.PathLengthM(a.Id, Guid.NewGuid()));
        Assert.Empty(g.Problems);
    }

    [Fact]
    public void Broken_cable_data_is_reported_not_thrown()
    {
        var scene = new Scene();
        var e = new Entity("Cable");
        var c = new CableComponent { SlackFactor = float.NaN };
        var a = new CableNode { Position = new Vector3(0, 0, 0) };
        var b = new CableNode { Position = new Vector3(1, 0, 0) };
        var d = new CableNode { Position = new Vector3(1, 1, 0) };
        var nan = new CableNode { Position = new Vector3(float.NaN, 0, 0) };
        var orphan = new CableNode { Anchor = new CableAnchor(Guid.NewGuid(), Vector3.Zero), Position = new Vector3(5, 0, 0) };
        c.Nodes.AddRange([a, b, d, nan, orphan, new CableNode { Position = Vector3.One }]);
        c.Nodes.Add(new CableNode()); // duplicate below
        var duplicate = c.Nodes[^1];
        c.Nodes.Add(new CableNode { Position = Vector3.One });
        c.Nodes[^1].Id = duplicate.Id;
        c.Segments.AddRange([new(a.Id, b.Id), new(b.Id, d.Id), new(d.Id, a.Id), new(a.Id, nan.Id), new(a.Id, Guid.NewGuid()), new(a.Id, orphan.Id)]);
        e.Add(c);
        scene.Add(e);

        var g = CableGeometry.Compute(scene, e, c);
        var kinds = g.Problems.Select(p => p.Kind).ToHashSet();
        Assert.Contains(CableGeometryProblemKind.AnchorMissing, kinds);
        Assert.Contains(CableGeometryProblemKind.InvalidPosition, kinds);
        Assert.Contains(CableGeometryProblemKind.NotATree, kinds);
        Assert.Contains(CableGeometryProblemKind.SegmentNodeMissing, kinds);
        Assert.Contains(CableGeometryProblemKind.DuplicateNode, kinds);
        Assert.Equal(1f, g.SlackFactor);
        Assert.True(float.IsFinite(g.LengthM));
        Assert.Equal(1f + 1f + 5f, g.RoutedLengthM, 5); // a–b, b–d, a–orphan (stored position); the loop and NaN are skipped
    }

    [Fact]
    public void Writing_node_positions_for_drawing_does_not_allocate()
    {
        var scene = new Scene();
        var anchor = scene.Add(new Entity("Truss") { Local = new Transform(new Vector3(0, 0, 6)) });
        var e = new Entity("Cable") { Local = new Transform(new Vector3(1, 0, 0)) };
        var c = new CableComponent();
        for (int i = 0; i < 16; i++)
            c.Nodes.Add(i % 2 == 0 ? new CableNode { Position = new Vector3(i, 0, 0) } : new CableNode { Anchor = new CableAnchor(anchor.Id, new Vector3(i, 0, 0)) });
        e.Add(c);
        scene.Add(e);
        Span<Vector3> buffer = stackalloc Vector3[16];
        CableGeometry.WriteNodeWorldPositions(scene, e, c, buffer); // warm-up
        long before = GC.GetAllocatedBytesForCurrentThread();
        int n = 0;
        for (int k = 0; k < 100; k++) n = CableGeometry.WriteNodeWorldPositions(scene, e, c, buffer);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(16, n);
        Assert.Equal(0, allocated);
        Assert.Equal(new Vector3(1, 0, 0), buffer[0]);
        Assert.Equal(new Vector3(1, 0, 6), buffer[1]);
    }
}
