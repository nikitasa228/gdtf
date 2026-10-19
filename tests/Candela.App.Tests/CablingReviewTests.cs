using System.Globalization;
using System.Numerics;
using Candela.App.Editing;
using Candela.App.Panels;
using Candela.App.Session;
using Candela.App.Ui;
using Candela.Core.Math;
using Candela.Core.SceneGraph.Components;
using Candela.Electrical;

namespace Candela.App.Tests;

/// <summary>
/// Regression tests of the cabling review: the panels show cable lengths of the scene as it is after a device that
/// cables are plugged into moved; renaming a console output or an outlet keeps its cables plugged (one undo step);
/// a branching cable point is not deleted; an unknown cable type is named in the tool's message; the cable link
/// window brings the Properties panel into view after selecting a device and keeps room for branch arcs only when a
/// branch needs it; the port picker opens towards the inside of the window and is pushed back inside when it hangs
/// over an edge; a three-phase supply feeds a distro whose single-phase loads are spread over L1–L3.
/// </summary>
[Collection(UiStateCollection.Name)]
public sealed class CablingReviewTests
{
    [Fact]
    public void Panels_show_cable_lengths_of_the_scene_as_it_is_after_a_plugged_device_moved()
    {
        Loc.SetLanguage("en");
        using var dir = new TempDir();
        using var s = new CablingScene(dir);
        var tool = s.Tool;
        tool.CableTypeId = CableCatalog.Ids.DmxXlr5;
        tool.Click(CablingScene.DeviceAt(s.Console));
        tool.ChoosePort("DMX A");
        Assert.Equal(CableClickOutcome.Connected, tool.Click(CablingScene.DeviceAt(s.Spots[0])));
        tool.Cancel();
        var (cableEntity, _) = Assert.Single(s.Cables());
        var view = new ConnectionInspectorView(s.Stage);
        view.Refresh(s.Engine, cableEntity);
        string lengthBefore = view.CableInfo!.Length;
        float before = CablingNetwork.Current(s.Engine).FindCable(cableEntity.Id)!.LengthM;
        float linkBefore = LinkLength(CableLinkModel.Build(s.Engine, cableEntity.Id)!);

        // The fixture moves 10 m along the truss: the plug follows it, and so does the cable's length everywhere.
        s.Spots[0].Local = new Transform(new Vector3(-12f, 0f, 5f));
        s.Engine.Tick(0.02);
        float after = CablingNetwork.Current(s.Engine).FindCable(cableEntity.Id)!.LengthM;
        Assert.True(after - before > 5f, $"length {before} m → {after} m");
        Assert.True(LinkLength(CableLinkModel.Build(s.Engine, cableEntity.Id)!) - linkBefore > 5f);
        view.Refresh(s.Engine, cableEntity);
        Assert.NotEqual(lengthBefore, view.CableInfo!.Length);
        // The console still drives the fixture; no new solve while nothing changes.
        Assert.True(CablingNetwork.Current(s.Engine).IsConsoleDriven(s.Spots[0].Id));
        Assert.Same(CablingNetwork.Current(s.Engine), CablingNetwork.Current(s.Engine));
    }

    [Fact]
    public void Renaming_a_console_output_or_an_outlet_keeps_its_cables_plugged_as_one_undo_step()
    {
        Loc.SetLanguage("en");
        using var dir = new TempDir();
        using var s = new CablingScene(dir);
        var tool = s.Tool;
        tool.CableTypeId = CableCatalog.Ids.DmxXlr5;
        tool.Click(CablingScene.DeviceAt(s.Console));
        tool.ChoosePort("DMX A");
        tool.Click(CablingScene.DeviceAt(s.Spots[0]));
        tool.Cancel();
        tool.CableTypeId = CableCatalog.Ids.PowerconTrue1;
        tool.Click(CablingScene.DeviceAt(s.Distro));
        tool.ChoosePort("TRUE1 2");
        tool.Click(CablingScene.Ground(5f, -2f));
        Assert.True(tool.Finish());
        var dmx = s.Cables()[0].Cable;
        var power = s.Cables()[1].Cable;
        var console = s.Console.Get<ConsoleComponent>()!;
        var source = s.Distro.Get<PowerSourceComponent>()!;
        var view = new ConnectionInspectorView(s.Stage);

        Assert.True(view.RenameConsoleOutput(s.Console, console, 0, "  Desk A "));
        Assert.Equal("Desk A", console.Outputs[0].PortName);
        Assert.Contains(dmx.Endpoints, e => e.EntityId == s.Console.Id && e.PortName == "Desk A");
        var feed = CablingNetwork.Current(s.Engine).DataFeedOf(s.Spots[0].Id);
        Assert.Equal("Desk A", feed!.ConsolePort);
        Assert.Equal("Rename console output", s.Engine.Undo.UndoDescription);
        // Another output's name, an empty name or the same name: nothing happens.
        Assert.False(view.RenameConsoleOutput(s.Console, console, 0, "dmx b"));
        Assert.False(view.RenameConsoleOutput(s.Console, console, 0, "   "));
        Assert.False(view.RenameConsoleOutput(s.Console, console, 0, "Desk A"));

        Assert.True(view.RenameOutlet(s.Distro, source, 1, "Truss L"));
        Assert.Contains(power.Endpoints, e => e.EntityId == s.Distro.Id && e.PortName == "Truss L");
        Assert.True(CablingNetwork.Current(s.Engine).FindCable(s.Cables()[1].Entity.Id)!.Endpoints.Single(e => e.EntityId == s.Distro.Id).Port is not null);

        // Each rename is one undo step that takes the plugs back with the name.
        s.Engine.Undo.Undo();
        Assert.Equal("TRUE1 2", source.Outlets[1].PortName);
        Assert.Contains(power.Endpoints, e => e.EntityId == s.Distro.Id && e.PortName == "TRUE1 2");
        s.Engine.Undo.Undo();
        Assert.Equal("DMX A", console.Outputs[0].PortName);
        Assert.Contains(dmx.Endpoints, e => e.EntityId == s.Console.Id && e.PortName == "DMX A");
        Assert.True(CablingNetwork.Current(s.Engine).IsConsoleDriven(s.Spots[0].Id));
        s.Engine.Undo.Redo();
        Assert.Contains(dmx.Endpoints, e => e.EntityId == s.Console.Id && e.PortName == "Desk A");
    }

    [Fact]
    public void A_branching_point_is_kept_and_an_unknown_cable_type_is_named()
    {
        Loc.SetLanguage("en");
        using var dir = new TempDir();
        using var s = new CablingScene(dir);
        var tool = s.Tool;
        tool.CableTypeId = CableCatalog.Ids.DmxXlr5;
        tool.Click(CablingScene.Ground(0f, -2f));
        tool.Click(CablingScene.Ground(2f, -2f));
        Assert.True(tool.Finish());
        var (entity, cable) = Assert.Single(s.Cables());
        tool.Click(CableTarget.Segment(entity.Id, cable.Segments[0].FromNode, cable.Segments[0].ToNode, new Vector3(1f, -2f, 0.02f)));
        tool.Click(CablingScene.Ground(1f, -4f));
        Assert.True(tool.Finish());
        var split = cable.Nodes.Single(n => cable.Segments.Count(sg => sg.FromNode == n.Id || sg.ToNode == n.Id) == 3);
        string? step = s.Engine.Undo.UndoDescription;
        Assert.False(CableNodeEdits.Delete(s.Engine.Scene, s.Engine.Undo, new CableNodeRef(entity.Id, split.Id)));
        Assert.Equal(4, cable.Nodes.Count);
        Assert.Equal(step, s.Engine.Undo.UndoDescription);
        Assert.False(string.IsNullOrWhiteSpace(CableLoc.Get(CableStr.NodeBranchKept)));

        tool.CableTypeId = "no-such-cable";
        Assert.Equal(CableClickOutcome.Rejected, tool.Click(CablingScene.Ground(0f, 0f)));
        Assert.Contains("'no-such-cable'", tool.LastMessage, StringComparison.Ordinal);
        Assert.Contains("catalogue", tool.LastMessage, StringComparison.Ordinal);
        Assert.False(tool.IsDrawing);
    }

    [Fact]
    public void Selecting_a_device_in_the_cable_link_window_brings_the_properties_into_view()
    {
        Loc.SetLanguage("en");
        using var dir = new TempDir();
        using var s = new CablingScene(dir);
        var tool = s.Tool;
        tool.CableTypeId = CableCatalog.Ids.DmxXlr5;
        tool.Click(CablingScene.DeviceAt(s.Console));
        tool.ChoosePort("DMX A");
        tool.Click(CablingScene.DeviceAt(s.Spots[0]));
        tool.Cancel();
        int shown = 0;
        var panel = new CableLinksPanel(s.Stage, null, () => shown++);
        panel.Show(s.Cables()[0].Entity.Id);
        panel.Refresh();
        Assert.Equal(new[] { "Console 1", "Spot 1" }, panel.Model!.Cards.Select(c => c.Name));
        panel.SelectCard(1);
        Assert.Equal(s.Spots[0].Id, s.Engine.Selection.Primary);
        Assert.Equal(1, shown);
        panel.SelectCard(7); // no such card: nothing happens
        Assert.Equal(1, shown);
    }

    [Theory]
    [InlineData(100f, 100f, 114f, 114f, 0f, 0f)]
    [InlineData(1500f, 100f, 1486f, 114f, 1f, 0f)]
    [InlineData(100f, 850f, 114f, 836f, 0f, 1f)]
    [InlineData(1500f, 850f, 1486f, 836f, 1f, 1f)]
    public void The_port_picker_opens_towards_the_inside_of_the_window(float x, float y, float px, float py, float pivotX, float pivotY)
    {
        var (position, pivot) = PopupPlacement.AtPoint(new Vector2(x, y), 14f, Vector2.Zero, new Vector2(1600f, 900f));
        Assert.Equal(new Vector2(px, py), position);
        Assert.Equal(new Vector2(pivotX, pivotY), pivot);
    }

    [Fact]
    public void An_open_port_picker_hanging_over_an_edge_is_pushed_back_inside_the_window()
    {
        var area = (Pos: new Vector2(0f, 20f), Size: new Vector2(1600f, 860f));
        // A 7-port picker at 150 % opened under a click low in the window: it would reach below the window.
        Assert.Equal(new Vector2(500f, 300f), PopupPlacement.Inside(new Vector2(500f, 380f), new Vector2(520f, 580f), area.Pos, area.Size));
        // Over the right and the top edge.
        Assert.Equal(new Vector2(1080f, 20f), PopupPlacement.Inside(new Vector2(1300f, -40f), new Vector2(520f, 300f), area.Pos, area.Size));
        // Inside: unchanged; larger than the window: its top left corner stays in it; not finite: the corner.
        Assert.Equal(new Vector2(200f, 200f), PopupPlacement.Inside(new Vector2(200f, 200f), new Vector2(300f, 300f), area.Pos, area.Size));
        Assert.Equal(new Vector2(0f, 20f), PopupPlacement.Inside(new Vector2(50f, 60f), new Vector2(2000f, 1000f), area.Pos, area.Size));
        Assert.Equal(area.Pos, PopupPlacement.Inside(new Vector2(float.NaN, 0f), new Vector2(10f, 10f), area.Pos, area.Size));
    }

    [Fact]
    public void A_plain_chain_needs_no_room_for_branch_arcs_in_the_link_window()
    {
        Loc.SetLanguage("en");
        using var dir = new TempDir();
        using var s = new CablingScene(dir);
        var tool = s.Tool;
        tool.CableTypeId = CableCatalog.Ids.DmxXlr5;
        tool.Click(CablingScene.DeviceAt(s.Console));
        tool.ChoosePort("DMX A");
        tool.Click(CablingScene.Ground(-2f, -2f));
        tool.Click(CablingScene.DeviceAt(s.Spots[0]));
        tool.Click(CablingScene.DeviceAt(s.Spots[1]));
        tool.Cancel();
        var first = s.Cables()[0];
        Assert.False(CableLinkModel.Build(s.Engine, first.Entity.Id)!.HasBranches);
        // A branch from the floor run to the third fixture: its cable skips a card, drawn as an arc.
        var segment = first.Cable.Segments[1];
        tool.Click(CableTarget.Segment(first.Entity.Id, segment.FromNode, segment.ToNode, new Vector3(-2f, -1f, 2.5f)));
        Assert.Equal(CableClickOutcome.Connected, tool.Click(CablingScene.DeviceAt(s.Spots[2])));
        tool.Cancel();
        Assert.True(CableLinkModel.Build(s.Engine, first.Entity.Id)!.HasBranches);
    }

    [Fact]
    public void A_three_phase_supply_feeds_a_distro_whose_loads_spread_over_the_phases()
    {
        Loc.SetLanguage("en");
        using var dir = new TempDir();
        using var s = new CablingScene(dir);
        var mains = s.Add("Mains 1", DeviceCatalog.Find("mains-cee32-3p")!.CreateComponent(), new Vector3(8f, 4f, 0f));
        var distro = s.Add("Distro 9", DeviceCatalog.Find("distro-32-schuko")!.CreateComponent(), new Vector3(8f, 0f, 0f));
        var tool = s.Tool;
        tool.CableTypeId = CableCatalog.Ids.Cee32ThreePhase;
        Assert.Equal(CableClickOutcome.PickPort, tool.Click(CablingScene.DeviceAt(mains)));
        Assert.Equal("CEE 32 A 3~", tool.Pick!.Usable.First().Port.Name); // the outlet before the supply's own inlet
        tool.ChooseUsable(0);
        Assert.Equal(CableClickOutcome.Connected, tool.Click(CablingScene.DeviceAt(distro)));
        tool.Cancel();
        tool.CableTypeId = CableCatalog.Ids.Schuko;
        for (int k = 0; k < 3; k++)
        {
            Assert.Equal(CableClickOutcome.PickPort, tool.Click(CablingScene.DeviceAt(distro)));
            tool.ChoosePort($"Schuko {k + 1}");
            Assert.Equal(CableClickOutcome.Connected, tool.Click(CablingScene.DeviceAt(s.Spots[k])));
            tool.Cancel();
        }

        var solution = CablingNetwork.Current(s.Engine);
        var supply = solution.PowerSources.Single(p => p.SourceEntityId == mains.Id);
        var fed = solution.PowerSources.Single(p => p.SourceEntityId == distro.Id);
        Assert.True(fed.IsFed && fed.IsPowered);
        Assert.Equal(3 * 1225.0, supply.TotalW, 1);
        // Schuko 1–3 are on L1, L2 and L3: one MAC Viper on each phase.
        Assert.All(supply.PhaseCurrentsA, a => Assert.InRange(a, 5.0, 6.0));
        s.Engine.RefreshIssues();
        Assert.DoesNotContain(s.Engine.Issues.Current, i => i.Code is IssueCodes.PowerPhaseMismatch or IssueCodes.PowerUnpowered or IssueCodes.PowerBreakerOverload);
        var texts = new ConnectionInspectorView(s.Stage);
        texts.Refresh(s.Engine, mains);
        Assert.Contains("3675 W", texts.PowerInfo!.Total, StringComparison.Ordinal);
    }

    private static float LinkLength(CableLinkModel model) =>
        float.Parse(model.Links[0].Length.Split(' ')[0], CultureInfo.InvariantCulture);
}
