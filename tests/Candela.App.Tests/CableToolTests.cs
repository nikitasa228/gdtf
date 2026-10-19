using System.Numerics;
using Candela.App.Editing;
using Candela.App.Session;
using Candela.App.Ui;
using Candela.Core.Diagnostics;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;
using Candela.Electrical;

namespace Candela.App.Tests;

/// <summary>
/// The cable tool driven by scripted input (the clicks the viewport resolves under the mouse): routing over the floor,
/// a wall and a truss, plugging into ports with the port picker, daisy chains that continue from the thru port,
/// branching from the middle of a segment (a Y-split the solver reports), joining a cable, finishing with a free end,
/// rejected clicks, undo; cable nodes moved and deleted with undo.
/// </summary>
[Collection(UiStateCollection.Name)]
public sealed class CableToolTests
{
    [Fact]
    public void A_dmx_cable_routed_over_floor_and_wall_plugs_into_the_console_and_a_fixture_and_the_chain_continues()
    {
        Loc.SetLanguage("en");
        using var dir = new TempDir();
        using var s = new CablingScene(dir);
        var tool = s.Tool;
        tool.CableTypeId = CableCatalog.Ids.DmxXlr5;

        // The console has four free DMX outputs: the port picker opens and lists them, outputs first.
        Assert.Equal(CableClickOutcome.PickPort, tool.Click(CablingScene.DeviceAt(s.Console)));
        var pick = Assert.IsType<PortPick>(tool.Pick);
        Assert.Equal(new[] { "DMX A", "DMX B", "DMX C", "DMX D" }, pick.Usable.Select(c => c.Port.Name));
        Assert.Equal(CableClickOutcome.Started, tool.ChooseUsable(0));
        Assert.Null(tool.Pick);
        var draft = Assert.IsType<CableDraft>(tool.Draft);
        Assert.Equal("DMX A", draft.StartPort!.Name);

        Assert.Equal(CableClickOutcome.PointAdded, tool.Click(CablingScene.Ground(0f, -1f)));
        Assert.Equal(CableClickOutcome.PointAdded, tool.Click(s.OnWall(-2f, 2f)));
        Assert.Equal(CableClickOutcome.PointAdded, tool.Click(s.OnTruss(-2.5f)));
        Assert.Equal(4, tool.Draft!.Points.Count);
        Assert.True(tool.Draft.RoutedLength() > 10f);

        // The fixture has one free DMX input for a cable that starts at an output: no picker.
        Assert.Equal(CableClickOutcome.Connected, tool.Click(CablingScene.DeviceAt(s.Spots[0])));
        var (cableEntity, cable) = Assert.Single(s.Cables());
        Assert.Equal("DMX 1", cableEntity.Name);
        Assert.Equal(CableCatalog.Ids.DmxXlr5, cable.CableTypeId);
        Assert.Equal(5, cable.Nodes.Count);
        Assert.Equal(4, cable.Segments.Count);
        Assert.Equal(new[] { ("Console 1", "DMX A"), ("Spot 1", "DMX IN") },
            cable.Endpoints.Select(e => (s.Engine.Scene.Find(e.EntityId)!.Name, e.PortName)));
        // Route points are fixed to what was clicked; the ground point is free; the plugs follow their devices.
        Assert.Equal(s.Console.Id, cable.Nodes[0].Anchor!.Value.EntityId);
        Assert.Null(cable.Nodes[1].Anchor);
        Assert.Equal(s.Wall.Id, cable.Nodes[2].Anchor!.Value.EntityId);
        Assert.Equal(s.Truss.Id, cable.Nodes[3].Anchor!.Value.EntityId);
        Assert.Equal(s.Spots[0].Id, cable.Nodes[4].Anchor!.Value.EntityId);
        // The wall point sits on the wall (lifted 2 cm off it), in the wall's own space.
        var wallPoint = Vector3.Transform(cable.Nodes[2].Anchor!.Value.LocalOffset, s.Wall.WorldMatrix);
        Assert.Equal(new Vector3(-2f, 3.93f, 2f), wallPoint, new Vector3Comparer(1e-4f));
        Assert.Equal("Add DMX 1", s.Engine.Undo.UndoDescription);

        // A daisy chain goes on from the fixture's DMX thru.
        draft = Assert.IsType<CableDraft>(tool.Draft);
        Assert.Equal("DMX THRU", draft.StartPort!.Name);
        Assert.Equal(CableClickOutcome.PointAdded, tool.Click(s.OnTruss(-1f)));
        Assert.Equal(CableClickOutcome.Connected, tool.Click(CablingScene.DeviceAt(s.Spots[1])));
        Assert.Equal(2, s.Cables().Count);
        // Esc: the cable started at the second fixture's thru has a single point and is dropped.
        Assert.False(tool.Finish());
        Assert.Null(tool.Draft);
        Assert.Equal(2, s.Cables().Count);

        // The engine sees the line: both fixtures are console-driven, in chain order, with their cable index.
        var solution = s.Engine.NetworkSolution;
        Assert.True(solution.IsConsoleDriven(s.Spots[0].Id));
        Assert.True(solution.IsConsoleDriven(s.Spots[1].Id));
        Assert.False(solution.IsConsoleDriven(s.Spots[2].Id));
        Assert.Equal("DMX A · cable 2", CableLinkModel.FeedIndex(solution, s.Spots[1].Id));
        var model = CableLinkModel.Build(s.Engine, s.Cables()[1].Entity.Id)!;
        Assert.Equal(new[] { "Console 1", "Spot 1", "Spot 2" }, model.Cards.Select(c => c.Name));
        Assert.Equal(new[] { "DMX A · cable 1", "DMX A · cable 2" }, model.Links.Select(l => l.Index));
        Assert.True(model.Links[1].Selected);
        Assert.Contains("'Console 1' DMX A · universe 1", model.Source, StringComparison.Ordinal);

        // Each cable is one undo step.
        s.Engine.Undo.Undo();
        Assert.Single(s.Cables());
        s.Engine.Undo.Undo();
        Assert.Empty(s.Cables());
        s.Engine.Undo.Redo();
        Assert.Single(s.Cables());
    }

    [Fact]
    public void Clicking_the_middle_of_a_segment_branches_the_cable_and_the_solver_reports_the_y_split()
    {
        Loc.SetLanguage("en");
        using var dir = new TempDir();
        using var s = new CablingScene(dir);
        var tool = s.Tool;
        tool.CableTypeId = CableCatalog.Ids.DmxXlr5;
        tool.Click(CablingScene.DeviceAt(s.Console));
        tool.ChoosePort("DMX B");
        tool.Click(CablingScene.Ground(0f, -1f));
        tool.Click(CablingScene.Ground(-2f, -1f));
        tool.Click(CablingScene.DeviceAt(s.Spots[0]));
        tool.Finish();
        var (cableEntity, cable) = Assert.Single(s.Cables());
        var from = cable.Segments[1].FromNode;
        var to = cable.Segments[1].ToNode;

        // The middle of the floor run: a new node splits the segment and a branch starts there with the cable's type.
        tool.CableTypeId = CableCatalog.Ids.Schuko;
        Assert.Equal(CableClickOutcome.Started, tool.Click(CableTarget.Segment(cableEntity.Id, from, to, new Vector3(-1f, -1f, 0f))));
        Assert.Equal(CableCatalog.Ids.DmxXlr5, tool.CableTypeId);
        Assert.Equal(cableEntity.Id, tool.Draft!.ExistingCable);
        tool.Click(CablingScene.Ground(2f, -1f));
        Assert.Equal(CableClickOutcome.Connected, tool.Click(CablingScene.DeviceAt(s.Spots[2])));
        Assert.Single(s.Cables());
        Assert.Equal(7, cable.Nodes.Count);
        Assert.Equal(6, cable.Segments.Count);
        Assert.Equal(3, cable.Endpoints.Count);
        Assert.Equal("Branch cable", s.Engine.Undo.UndoDescription);
        var geometry = CableGeometry.Compute(s.Engine.Scene, cableEntity, cable);
        Assert.Empty(geometry.Problems);
        // The split node lies on the old segment and has three segments.
        var split = cable.Nodes.Single(n => cable.Segments.Count(sg => sg.FromNode == n.Id || sg.ToNode == n.Id) == 3);
        Assert.Equal(new Vector3(-1f, -1f, 0f), split.Position, new Vector3Comparer(1e-4f));

        // Three plugged ends on a DMX cable: a Y-split without a splitter, reported by the solver.
        s.Engine.RefreshIssues();
        Assert.Contains(s.Engine.Issues.Current, i => i.Code == IssueCodes.DataYSplit);
        Assert.True(s.Engine.NetworkSolution.IsConsoleDriven(s.Spots[2].Id));

        // One undo step takes the branch away again.
        s.Engine.Undo.Undo();
        Assert.Equal(4, cable.Nodes.Count);
        Assert.Equal(2, cable.Endpoints.Count);
    }

    [Fact]
    public void Port_picker_lists_every_port_with_the_reason_it_does_not_fit_and_a_choice_connects()
    {
        Loc.SetLanguage("en");
        using var dir = new TempDir();
        using var s = new CablingScene(dir);
        var tool = s.Tool;
        tool.CableTypeId = CableCatalog.Ids.DmxXlr5;
        // Started from a free end on the floor, a fixture offers its input and its thru: the picker asks.
        tool.Click(CablingScene.Ground(-2f, -2f));
        Assert.Equal(CableClickOutcome.PickPort, tool.Click(CablingScene.DeviceAt(s.Spots[0])));
        var pick = tool.Pick!;
        Assert.Equal("DMX IN", pick.Candidates[0].Port.Name); // inputs first for a cable coming from a free end
        Assert.Equal(new[] { "DMX IN", "DMX THRU" }, pick.Usable.Select(c => c.Port.Name));
        var power = pick.Candidates.Single(c => c.Port.Name == "Power IN");
        Assert.False(power.Usable);
        Assert.Contains("power port", PortPicker.Status(power)!.Value.Text + PortPicker.Status(power)!.Value.Tooltip, StringComparison.Ordinal);
        Assert.Equal(CableClickOutcome.Connected, tool.ChoosePort("DMX THRU"));
        var (_, cable) = Assert.Single(s.Cables());
        Assert.Equal("DMX THRU", cable.Endpoints.Single().PortName);
        // Plugged into the thru, nothing continues from it.
        Assert.Null(tool.Draft);

        // With the thru in use only the input is left: the cable plugs into it without asking.
        tool.Click(CablingScene.Ground(-2f, -3f));
        Assert.Equal(CableClickOutcome.Connected, tool.Click(CablingScene.DeviceAt(s.Spots[0])));
        Assert.Equal("DMX IN", s.Cables()[1].Cable.Endpoints.Single().PortName);
        tool.Cancel();

        // Ports in use are shown with the cable in them and cannot be chosen.
        tool.Click(CablingScene.Ground(-2f, -4f));
        Assert.Equal(CableClickOutcome.PickPort, tool.Click(CablingScene.DeviceAt(s.Spots[0])));
        Assert.Empty(tool.Pick!.Usable);
        Assert.Equal("DMX 2", tool.Pick.Candidates.Single(c => c.Port.Name == "DMX IN").Occupant!.DisplayLabel);
        var used = tool.Pick!.Candidates.Single(c => c.Port.Name == "DMX THRU");
        Assert.False(used.Usable);
        Assert.Equal("DMX 1", used.Occupant!.DisplayLabel);
        Assert.Contains("DMX 1", PortPicker.Status(used)!.Value.Text, StringComparison.Ordinal);
        Assert.Equal(CableClickOutcome.Rejected, tool.ChoosePort("DMX THRU"));

        // A power cable on a console: nothing fits, the picker explains it and the status says why.
        tool.Cancel();
        tool.CableTypeId = CableCatalog.Ids.PowerconTrue1;
        Assert.Equal(CableClickOutcome.PickPort, tool.Click(CablingScene.DeviceAt(s.Console)));
        Assert.Empty(tool.Pick!.Usable);
        Assert.Contains("has no free port", tool.LastMessage, StringComparison.Ordinal);
        Assert.Contains("cannot be plugged into", tool.LastMessage, StringComparison.Ordinal);
        tool.CancelPick();
        Assert.Null(tool.Pick);
    }

    [Fact]
    public void A_cable_finished_with_a_free_end_a_join_into_another_cable_and_removing_points()
    {
        Loc.SetLanguage("en");
        using var dir = new TempDir();
        using var s = new CablingScene(dir);
        var tool = s.Tool;
        tool.CableTypeId = CableCatalog.Ids.DmxXlr3;
        // A start alone is dropped by Esc / right-click.
        tool.Click(CablingScene.Ground(0f, 0f));
        Assert.False(tool.Finish());
        Assert.Empty(s.Cables());

        // Backspace removes the last point, never the start.
        tool.Click(CablingScene.Ground(0f, 0f));
        tool.Click(CablingScene.Ground(1f, 0f));
        tool.Click(CablingScene.Ground(2f, 0f));
        Assert.True(tool.RemoveLastPoint());
        Assert.True(tool.RemoveLastPoint());
        Assert.False(tool.RemoveLastPoint());
        tool.Click(CablingScene.Ground(3f, 0f));
        Assert.True(tool.Finish());
        var (first, cable) = Assert.Single(s.Cables());
        Assert.Equal(2, cable.Nodes.Count);
        Assert.Empty(cable.Endpoints);
        var model = CableLinkModel.Build(s.Engine, first.Id)!;
        Assert.False(model.Fed);
        Assert.Equal(2, model.FreeEnds);

        // A new cable of the same type that ends on the first cable's end node joins it.
        tool.Click(CablingScene.Ground(3f, 3f));
        var end = cable.Nodes[1];
        Assert.Equal(CableClickOutcome.Committed, tool.Click(CableTarget.Node(first.Id, end.Id, new Vector3(3f, 0f, 0f))));
        Assert.Single(s.Cables());
        Assert.Equal(3, cable.Nodes.Count);
        Assert.Equal("Join cable", s.Engine.Undo.UndoDescription);

        // A cable of another type is not joined: the click is a route point next to it.
        tool.CableTypeId = CableCatalog.Ids.Schuko;
        tool.Click(CablingScene.Ground(-3f, 0f));
        Assert.Equal(CableClickOutcome.PointAdded, tool.Click(CableTarget.Node(first.Id, end.Id, new Vector3(3f, 0f, 0f))));
        Assert.True(tool.Finish());
        Assert.Equal(2, s.Cables().Count);
        Assert.Equal("Power 1", s.Cables()[1].Entity.Name);
    }

    [Fact]
    public void Cable_nodes_move_on_three_axes_and_are_deleted_with_undo()
    {
        Loc.SetLanguage("en");
        using var dir = new TempDir();
        using var s = new CablingScene(dir);
        var tool = s.Tool;
        tool.CableTypeId = CableCatalog.Ids.DmxXlr5;
        tool.Click(CablingScene.Ground(0f, -2f));
        tool.Click(s.OnWall(1f, 2f));
        tool.Click(CablingScene.Ground(2f, -2f));
        tool.Click(CablingScene.Ground(4f, -2f));
        tool.Finish();
        var (entity, cable) = Assert.Single(s.Cables());
        var scene = s.Engine.Scene;
        var undo = s.Engine.Undo;

        // A free node moves in the cable's space, an anchored node keeps its anchor and moves within its entity.
        var free = new CableNodeRef(entity.Id, cable.Nodes[2].Id);
        Assert.True(CableNodeEdits.Move(scene, undo, free, new Vector3(2.5f, -1.5f, 0.75f)));
        Assert.Equal(new Vector3(2.5f, -1.5f, 0.75f), cable.Nodes[2].Position);
        Assert.Equal("Move cable point", undo.UndoDescription);
        var onWall = new CableNodeRef(entity.Id, cable.Nodes[1].Id);
        Assert.True(CableNodeEdits.Move(scene, undo, onWall, new Vector3(1f, 3.93f, 3f)));
        Assert.Equal(s.Wall.Id, cable.Nodes[1].Anchor!.Value.EntityId);
        Assert.Equal(new Vector3(1f, 3.93f, 3f), CableNodeEdits.WorldPosition(scene, entity, cable.Nodes[1]), new Vector3Comparer(1e-4f));
        // The wall moves: the node follows it.
        s.Wall.Local = new Core.Math.Transform(new Vector3(0f, 5f, 0f));
        Assert.Equal(new Vector3(1f, 4.93f, 3f), CableNodeEdits.WorldPosition(scene, entity, cable.Nodes[1]), new Vector3Comparer(1e-4f));

        undo.Undo();
        Assert.Equal(new Vector3(1f, 4.93f, 2f), CableNodeEdits.WorldPosition(scene, entity, cable.Nodes[1]), new Vector3Comparer(1e-4f));
        undo.Undo();
        Assert.Equal(new Vector3(2f, -2f, 0.02f), cable.Nodes[2].Position, new Vector3Comparer(1e-4f));

        // A drag of several steps is one undo step (the moves merge until the merge is broken).
        CableNodeEdits.Move(scene, undo, free, new Vector3(2f, -2f, 0.5f), breakMerge: false);
        CableNodeEdits.Move(scene, undo, free, new Vector3(2f, -2f, 1f), breakMerge: false);
        undo.BreakMerge();
        undo.Undo();
        Assert.Equal(new Vector3(2f, -2f, 0.02f), cable.Nodes[2].Position, new Vector3Comparer(1e-4f));

        // Deleting a route point joins its neighbours; deleting an end removes its segment.
        int nodes = cable.Nodes.Count;
        Assert.True(CableNodeEdits.Delete(scene, undo, onWall));
        Assert.Equal(nodes - 1, cable.Nodes.Count);
        Assert.Equal(nodes - 2, cable.Segments.Count);
        Assert.Empty(CableGeometry.Compute(scene, entity, cable).Problems);
        undo.Undo();
        Assert.Equal(nodes, cable.Nodes.Count);
        Assert.True(CableNodeEdits.Delete(scene, undo, new CableNodeRef(entity.Id, cable.Nodes[3].Id)));
        Assert.Equal(3, cable.Nodes.Count);
        Assert.Equal(2, cable.Segments.Count);
        // A cable left with one node goes away as a whole.
        Assert.True(CableNodeEdits.Delete(scene, undo, new CableNodeRef(entity.Id, cable.Nodes[2].Id)));
        Assert.True(CableNodeEdits.Delete(scene, undo, new CableNodeRef(entity.Id, cable.Nodes[1].Id)));
        Assert.Empty(s.Cables());
        undo.Undo();
        Assert.Single(s.Cables());
        Assert.False(CableNodeEdits.Delete(scene, undo, new CableNodeRef(Guid.NewGuid(), Guid.NewGuid())));
    }

    [Fact]
    public void Undo_of_the_branched_cable_cancels_the_branch_being_drawn()
    {
        Loc.SetLanguage("en");
        using var dir = new TempDir();
        using var s = new CablingScene(dir);
        var tool = s.Tool;
        tool.CableTypeId = CableCatalog.Ids.DmxXlr5;
        tool.Click(CablingScene.Ground(0f, -2f));
        tool.Click(CablingScene.Ground(2f, -2f));
        tool.Finish();
        var (entity, cable) = Assert.Single(s.Cables());
        tool.Click(CableTarget.Segment(entity.Id, cable.Segments[0].FromNode, cable.Segments[0].ToNode, new Vector3(1f, -2f, 0.02f)));
        Assert.True(tool.IsDrawing);
        s.Engine.Undo.Undo(); // the cable is gone
        tool.Validate();
        Assert.False(tool.IsDrawing);
        Assert.False(tool.Finish());
        Assert.Empty(s.Cables());
    }

    /// <summary>Component-wise vector comparison with a tolerance.</summary>
    private sealed class Vector3Comparer(float tolerance) : IEqualityComparer<Vector3>
    {
        public bool Equals(Vector3 x, Vector3 y) => MathF.Abs(x.X - y.X) <= tolerance && MathF.Abs(x.Y - y.Y) <= tolerance && MathF.Abs(x.Z - y.Z) <= tolerance;

        public int GetHashCode(Vector3 obj) => 0;
    }
}
