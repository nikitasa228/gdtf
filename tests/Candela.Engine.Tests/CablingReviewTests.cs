using System.Numerics;
using Candela.Core.Editing;
using Candela.Core.Math;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;
using Candela.Electrical;

namespace Candela.Engine.Tests;

/// <summary>
/// The engine's network solution after moves it has to notice indirectly: anchors deep in a moved subtree, cables
/// carried by a moved parent, undo and redo, and the length checks and issues that depend on the new geometry.
/// </summary>
public sealed class CablingReviewTests
{
    /// <summary>Asserts that every cable of <paramref name="actual"/> has the length and node positions of a fresh solve.</summary>
    private static void AssertCurrent(StageEngine engine, NetworkSolution actual)
    {
        var fresh = new NetworkSolver(engine.ElectricalProfiles).Solve(engine.Scene);
        Assert.Equal(fresh.Cables.Select(c => c.EntityId), actual.Cables.Select(c => c.EntityId));
        foreach (var expected in fresh.Cables)
        {
            var cable = actual.FindCable(expected.EntityId)!;
            Assert.Equal(expected.LengthM, cable.LengthM, 4);
            Assert.Equal(expected.Geometry.Nodes.Select(n => n.WorldPosition), cable.Geometry.Nodes.Select(n => n.WorldPosition));
        }
        Assert.Equal(fresh.Issues.Select(i => (i.Code, i.Message)), actual.Issues.Select(i => (i.Code, i.Message)));
        Assert.Equal(fresh.DataLines.SelectMany(l => l.Feeds).Select(f => f.RunLengthM), actual.DataLines.SelectMany(l => l.Feeds).Select(f => f.RunLengthM));
    }

    [Fact]
    public void Anchors_deep_in_a_moved_subtree_and_cables_carried_by_a_moved_parent_follow_it()
    {
        using var engine = TestEnvironment.CreateEngine("review-subtree");
        var scene = engine.Scene;
        var desk = SceneBuilder.Console(scene, new Vector3(0, -12, 0));
        var truss = scene.Add(new Entity("Truss") { Local = new Transform(new Vector3(0, 0, 6)) });
        truss.Add(new TrussComponent { Length = 8f });
        var clamp = scene.Add(new Entity("Clamp group") { Local = new Transform(new Vector3(1, 0, 0)) });
        scene.Reparent(clamp, truss, keepWorldTransform: false);
        var head = SceneBuilder.Fixture(scene, SceneBuilder.Naja, null, Vector3.Zero, universe: 1, address: 1, name: "Head", fixtureId: 1);
        scene.Reparent(head, clamp, keepWorldTransform: false);
        var feed = SceneBuilder.Cable(scene, desk, "DMX A", head, "XLR IN");

        // a spare cable tied along the truss: a child of the truss with free nodes in its local space
        var spare = new Entity("Spare on truss");
        var spareCable = new CableComponent { CableTypeId = "dmx-xlr5", SlackFactor = 1.05f };
        var n1 = new CableNode { Position = new Vector3(-3, 0, 0) };
        var n2 = new CableNode { Position = new Vector3(3, 0, 0) };
        spareCable.Nodes.AddRange([n1, n2]);
        spareCable.Segments.Add(new CableSegment(n1.Id, n2.Id));
        spare.Add(spareCable);
        scene.Add(spare);
        scene.Reparent(spare, truss, keepWorldTransform: false);

        engine.Tick(1.0 / 30);
        var before = engine.NetworkSolution;
        float feedBefore = before.FindCable(feed.Id)!.LengthM;

        // the truss is flown out and turned: the head (two levels down) and the spare (a child) move with it
        truss.Local = new Transform(new Vector3(0, 2, 9), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2), Vector3.One);
        var after = engine.NetworkSolution;
        Assert.NotSame(before, after);
        Assert.True(after.FindCable(feed.Id)!.LengthM > feedBefore + 1f);
        var spareNodes = after.FindCable(spare.Id)!.Geometry.Nodes;
        Assert.Equal(9f, spareNodes[0].WorldPosition.Z, 4);
        Assert.Equal(-1f, spareNodes[0].WorldPosition.Y, 3); // turned by 90°: the run now lies along Y
        AssertCurrent(engine, after);
    }

    [Fact]
    public void Undo_and_redo_of_a_move_renew_the_solution_and_the_ethernet_length_check_follows_it()
    {
        using var engine = TestEnvironment.CreateEngine("review-undo");
        var scene = engine.Scene;
        var desk = new Entity("Net desk") { Local = new Transform(new Vector3(0, -10, 0)) };
        desk.Add(new ConsoleComponent { Outputs = { new ConsoleOutput { PortName = "Net 1", Signal = "Ethernet", FirstUniverse = 1, UniverseCount = 4 } } });
        scene.Add(desk);
        var head = SceneBuilder.Fixture(scene, SceneBuilder.Naja, null, new Vector3(0, 10, 6), universe: 1, address: 1, name: "Head", fixtureId: 1);
        var link = SceneBuilder.Cable(scene, desk, "Net 1", head, "ETH IN", type: "ethernet");
        engine.Tick(1.0 / 30);
        Assert.DoesNotContain(engine.NetworkSolution.Issues, i => i.Code == IssueCodes.DataRunTooLong);

        // dragged 150 m away with the gizmo (one undoable command): the link is now too long for Ethernet
        engine.Undo.Execute(new SetTransformCommand([head], [new Transform(new Vector3(150, 10, 6))]));
        engine.Tick(1.0 / 30);
        var far = engine.NetworkSolution;
        Assert.Contains(far.Issues, i => i.Code == IssueCodes.DataRunTooLong && i.Entities.Contains(link.Id));
        AssertCurrent(engine, far);
        engine.RefreshIssues();
        Assert.Contains(engine.Issues.Current, i => i.Code == IssueCodes.DataRunTooLong);

        engine.Undo.Undo();
        var back = engine.NetworkSolution;
        Assert.DoesNotContain(back.Issues, i => i.Code == IssueCodes.DataRunTooLong);
        AssertCurrent(engine, back);
        engine.RefreshIssues();
        Assert.DoesNotContain(engine.Issues.Current, i => i.Code == IssueCodes.DataRunTooLong);

        engine.Undo.Redo();
        Assert.Contains(engine.NetworkSolution.Issues, i => i.Code == IssueCodes.DataRunTooLong);
        AssertCurrent(engine, engine.NetworkSolution);
    }

    [Fact]
    public void Edits_the_cabling_does_not_depend_on_keep_the_solution_and_a_dragged_cable_node_renews_only_its_cable()
    {
        using var engine = TestEnvironment.CreateEngine("review-edits");
        var scene = engine.Scene;
        var desk = SceneBuilder.Console(scene, new Vector3(0, -10, 0));
        var a = SceneBuilder.Fixture(scene, SceneBuilder.Naja, null, new Vector3(-2, 0, 6), universe: 1, address: 1, name: "A", fixtureId: 1);
        var b = SceneBuilder.Fixture(scene, SceneBuilder.Naja, null, new Vector3(2, 0, 6), universe: 1, address: 41, name: "B", fixtureId: 2);
        var feed = SceneBuilder.Cable(scene, desk, "DMX A", a, "XLR IN");
        var link = SceneBuilder.Cable(scene, a, "XLR OUT", b, "XLR IN");
        // a route point in the middle of the link, as the cable tool adds one
        var linkCable = link.Get<CableComponent>()!;
        var route = new CableNode { Position = new Vector3(0, 0, 4) };
        linkCable.Nodes.Insert(1, route);
        linkCable.Segments.Clear();
        linkCable.Segments.AddRange([new CableSegment(linkCable.Nodes[0].Id, route.Id), new CableSegment(route.Id, linkCable.Nodes[2].Id)]);
        linkCable.NotifyChanged();
        engine.Tick(1.0 / 30);
        var solved = engine.NetworkSolution;

        // a slider dragged on the fixture: every frame changes a manual attribute, nothing the cabling depends on
        var fixture = a.Get<FixtureComponent>()!;
        for (int frame = 0; frame < 5; frame++)
        {
            fixture.ManualAttributes["Dimmer"] = frame / 4f;
            fixture.NotifyChanged();
            engine.Tick(1.0 / 30);
            Assert.Same(solved, engine.NetworkSolution);
        }

        // the route point dragged down: only the link is measured again
        float before = solved.FindCable(link.Id)!.LengthM;
        for (int frame = 1; frame <= 3; frame++)
        {
            route.Position = new Vector3(0, 0, 4 - frame);
            linkCable.NotifyChanged();
            engine.Tick(1.0 / 30);
            var dragged = engine.NetworkSolution;
            Assert.Same(solved.FindCable(feed.Id), dragged.FindCable(feed.Id));
            AssertCurrent(engine, dragged);
        }
        Assert.True(engine.NetworkSolution.FindCable(link.Id)!.LengthM > before + 1f);

        // the patch changes: solved again (B now sits on universe 2 while its input carries universe 1)
        var patched = engine.NetworkSolution;
        b.Get<FixtureComponent>()!.Addresses[0] = new DmxAddress(2, 41);
        b.Get<FixtureComponent>()!.NotifyChanged();
        var repatched = engine.NetworkSolution;
        Assert.NotSame(patched, repatched);
        Assert.Contains(repatched.Issues, i => i.Code == IssueCodes.DataUniverseMismatch && i.Entities.Contains(b.Id));
        AssertCurrent(engine, repatched);

        // the route point is anchored to another object: solved again, and from then on it follows that object
        var post = scene.Add(new Entity("Post") { Local = new Transform(new Vector3(0, 3, 0)) });
        engine.Tick(1.0 / 30);
        route.Anchor = new CableAnchor(post.Id, new Vector3(0, 0, 2));
        linkCable.NotifyChanged();
        var anchored = engine.NetworkSolution;
        AssertCurrent(engine, anchored);
        post.Local = new Transform(new Vector3(0, 8, 0));
        var followed = engine.NetworkSolution;
        Assert.NotSame(anchored, followed);
        Assert.Equal(new Vector3(0, 8, 2), followed.FindCable(link.Id)!.Geometry.Nodes[1].WorldPosition);
        AssertCurrent(engine, followed);

        // the link is re-plugged into the other input: solved again with the new plug
        linkCable.Endpoints[1].PortName = "XLR IN 3";
        linkCable.NotifyChanged();
        var replugged = engine.NetworkSolution;
        Assert.Equal("XLR IN 3", replugged.FindCable(link.Id)!.Endpoints[1].PortName);
        AssertCurrent(engine, replugged);
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

    [Theory]
    [InlineData(20f)]
    [InlineData(45f)]
    [InlineData(58f)]
    public void A_cable_lying_on_a_ramp_stays_on_its_surface(float degrees)
    {
        using var engine = TestEnvironment.CreateEngine("review-ramp");
        var scene = engine.Scene;
        // a 6 m ramp tilted about X: it rises along +Y, its top face through the origin
        var tilt = Quaternion.CreateFromAxisAngle(Vector3.UnitX, degrees * MathF.PI / 180f);
        var ramp = scene.Add(new Entity("Ramp") { Local = new Transform(new Vector3(0, 0, 3), tilt, Vector3.One) });
        ramp.Add(new WallComponent { Kind = WallKind.Floor, Width = 4f, Height = 6f, Thickness = 0.2f });
        var up = Vector3.Transform(Vector3.UnitZ, tilt);
        var origin = new Vector3(0, 0, 3);
        Vector3 OnRamp(float x, float y) => origin + Vector3.Transform(new Vector3(x, y, 0), tilt) + up * 0.005f;

        // a power cable (6 mm) with slack from the foot of the ramp to its top, plus a route point half way
        var entity = new Entity("Up the ramp");
        var cable = new CableComponent { CableTypeId = "powercon-true1", SlackFactor = 1.3f };
        foreach (var p in new[] { OnRamp(-1, -2.8f), OnRamp(0.5f, 0), OnRamp(1, 2.8f) }) cable.Nodes.Add(new CableNode { Position = p });
        cable.Segments.Add(new CableSegment(cable.Nodes[0].Id, cable.Nodes[1].Id));
        cable.Segments.Add(new CableSegment(cable.Nodes[1].Id, cable.Nodes[2].Id));
        entity.Add(cable);
        scene.Add(entity);

        var world = new RecordingRenderWorld();
        var sync = engine.AttachRenderWorld(world, loadFixtureModelsInBackground: false);
        sync.CableSupports.GroundHeight = null; // only the ramp
        engine.Tick(1.0 / 30);
        var tube = TubeVertices(world, sync, entity);
        // no vertex below the ramp's surface (measured along its normal), and the cable lies on it (no free sag)
        Assert.All(tube, v => Assert.True(Vector3.Dot(v - origin, up) >= -1e-4f, $"vertex {Vector3.Dot(v - origin, up) * 1000:0.00} mm below the ramp"));
        Assert.All(tube, v => Assert.True(Vector3.Dot(v - origin, up) <= 0.04f, $"vertex {Vector3.Dot(v - origin, up) * 1000:0.00} mm above the ramp"));
    }

    /// <summary>The mesh the tube of <paramref name="cable"/> is drawn with.</summary>
    private static int TubeMesh(RecordingRenderWorld world, RenderSync sync, Entity cable)
    {
        uint pick = sync.PickIdOf(cable.Id);
        return world.Instances.Values.Single(i => i.PickId == pick).Mesh.Value;
    }

    [Fact]
    public void A_floor_dragged_or_resized_redraws_only_the_cables_over_it()
    {
        using var engine = TestEnvironment.CreateEngine("review-floor-drag");
        var scene = engine.Scene;
        var deck = scene.Add(new Entity("Deck") { Local = new Transform(new Vector3(0, 0, 1)) });
        deck.Add(new WallComponent { Kind = WallKind.Floor, Width = 4f, Height = 4f, Thickness = 0.2f });
        Entity Run(string name, Vector3 from, Vector3 to)
        {
            var e = new Entity(name);
            var c = new CableComponent { CableTypeId = "dmx-xlr5", SlackFactor = 1.2f };
            c.Nodes.AddRange([new CableNode { Position = from }, new CableNode { Position = to }]);
            c.Segments.Add(new CableSegment(c.Nodes[0].Id, c.Nodes[1].Id));
            e.Add(c);
            scene.Add(e);
            return e;
        }
        var onDeck = Run("On the deck", new Vector3(-1.5f, 0, 1.02f), new Vector3(1.5f, 0, 1.02f));
        var farAway = Run("Far away", new Vector3(20, 0, 0.02f), new Vector3(30, 0, 0.02f));
        var world = new RecordingRenderWorld();
        var sync = engine.AttachRenderWorld(world, loadFixtureModelsInBackground: false);
        engine.Tick(1.0 / 30);
        int deckMesh = TubeMesh(world, sync, onDeck), farMesh = TubeMesh(world, sync, farAway);

        // a few frames of a resize drag: the cable on the deck follows (it now hangs onto the lowered deck), the far one is
        // left alone
        var wall = deck.Get<WallComponent>()!;
        for (int frame = 1; frame <= 3; frame++)
        {
            wall.Width = 4f + frame;
            wall.NotifyChanged();
            deck.Local = new Transform(new Vector3(0, 0, 1f - 0.1f * frame));
            engine.Tick(1.0 / 30);
            Assert.Equal(farMesh, TubeMesh(world, sync, farAway));
            Assert.NotEqual(deckMesh, TubeMesh(world, sync, onDeck));
            deckMesh = TubeMesh(world, sync, onDeck);
            Assert.All(TubeVertices(world, sync, onDeck), v => Assert.True(v.Z >= deck.Local.Position.Z - 1e-4f, $"vertex at {v.Z} under the deck"));
        }

        // the ground changes: every cable is drawn again
        sync.CableSupports.GroundHeight = -0.5f;
        engine.Tick(1.0 / 30);
        Assert.NotEqual(farMesh, TubeMesh(world, sync, farAway));
        Assert.Empty(world.Violations);
    }
}
