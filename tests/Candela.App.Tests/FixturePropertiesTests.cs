using System.Numerics;
using Candela.App.Panels;
using Candela.App.Session;
using Candela.App.Ui;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;
using Candela.Dmx;
using Candela.Electrical;
using Candela.Engine;
using Hexa.NET.ImGui;

namespace Candela.App.Tests;

/// <summary>
/// The fixture part of the properties panel on a headless stage engine with real GDTF files: manual controls of the
/// DMX mode in physical units, live state, the console/DMX lock, multi-selection editing, the patch, manufacturer data
/// and the connectors with the cables plugged into them.
/// </summary>
[Collection(UiStateCollection.Name)]
public sealed class FixturePropertiesTests
{
    [Fact]
    public void Controls_cover_the_mode_in_physical_units_and_follow_manual_values()
    {
        Loc.SetLanguage("en");
        using var dir = new TempDir();
        using var engine = StageTestSupport.CreateEngine(dir);
        var head = StageTestSupport.AddFixture(engine, "Spot 1", 101, new Vector3(0, 0, 6));
        engine.Tick(0.02);
        var controls = new FixtureControls();
        controls.Refresh(engine, head);

        Assert.NotNull(controls.Profile);
        Assert.Equal(ControlLock.Manual, controls.Lock);
        // Every modelled attribute of the mode has a control, grouped; the dimmer leads the intensity group.
        var dimmer = controls.Find("Dimmer");
        Assert.NotNull(dimmer);
        Assert.Equal(ControlGroup.Intensity, dimmer!.Group);
        Assert.Same(dimmer, controls.Controls.First(c => c.Group == ControlGroup.Intensity));
        Assert.Equal("Intensity", dimmer.Label);
        Assert.Equal(100f, dimmer.DisplayScale);
        var pan = controls.Find("Pan");
        Assert.NotNull(pan);
        Assert.Equal(Gdtf.PhysicalUnit.Angle, pan!.Unit);
        Assert.True(pan.Max - pan.Min >= 360f, $"pan range {pan.Min}…{pan.Max}");
        Assert.Contains("°", pan.Format, StringComparison.Ordinal);
        Assert.NotNull(controls.Find("Tilt"));
        Assert.NotNull(controls.Find("Zoom"));
        var colour = controls.Controls.FirstOrDefault(c => c.Class.Kind == AttributeKind.WheelSelect && c.Class.Wheel == WheelFamily.Color);
        Assert.NotNull(colour);
        Assert.NotNull(colour!.Choices);
        Assert.True(colour.Choices!.Length > 3);
        Assert.Equal(colour.Choices.Length, colour.ChoiceValues!.Length);
        // Groups never interleave.
        var groups = controls.Controls.Select(c => c.Group).ToList();
        Assert.Equal(groups.OrderBy(g => g).ToList(), groups);

        // A manual value is an undo step and drives the fixture.
        var fixture = head.Get<FixtureComponent>()!;
        engine.Undo.Execute(new ManualAttributeCommand(new[] { fixture }, "Dimmer", 0.75f, "dim"));
        engine.Tick(0.02);
        controls.Refresh(engine, head);
        Assert.Equal(0.75f, dimmer.Manual);
        Assert.Equal(0.75f, dimmer.Live, 2);
        Assert.True(controls.Live.Valid);
        Assert.True(controls.Live.PeakCandela > 1000f, $"{controls.Live.PeakCandela} cd");
        Assert.True(controls.Live.Intensity > 0.5f);
        Assert.True(float.IsFinite(controls.Live.Cct));
        engine.Undo.Undo();
        engine.Tick(0.02);
        controls.Refresh(engine, head);
        Assert.Null(dimmer.Manual);
        Assert.False(fixture.ManualAttributes.ContainsKey("Dimmer"));
    }

    [Fact]
    public void Manual_attribute_command_merges_a_drag_and_restores_absent_values()
    {
        var a = new FixtureComponent();
        var b = new FixtureComponent();
        b.ManualAttributes["Pan"] = 10f;
        var undo = new Core.Editing.UndoStack();
        var targets = new[] { a, b };
        undo.Execute(new ManualAttributeCommand(targets, "Pan", 20f, "pan"));
        undo.Execute(new ManualAttributeCommand(targets, "Pan", 30f, "pan"));
        undo.Execute(new ManualAttributeCommand(targets, "Pan", 40f, "pan"));
        Assert.Equal(40f, a.ManualAttributes["Pan"]);
        Assert.Equal(40f, b.ManualAttributes["Pan"]);
        undo.Undo();
        Assert.False(a.ManualAttributes.ContainsKey("Pan"));
        Assert.Equal(10f, b.ManualAttributes["Pan"]);
        Assert.False(undo.CanUndo);
        undo.Redo();
        Assert.Equal(40f, a.ManualAttributes["Pan"]);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ManualAttributeCommand(targets, "Pan", float.NaN, "nan"));
        // A component without a dictionary (damaged file) gets one.
        var broken = new FixtureComponent { ManualAttributes = null! };
        new ManualAttributeCommand(new[] { broken }, "Dimmer", 1f, "x").Do();
        Assert.Equal(1f, broken.ManualAttributes["Dimmer"]);
    }

    [Fact]
    public void Live_dmx_locks_the_controls_and_names_the_universe()
    {
        Loc.SetLanguage("en");
        using var dir = new TempDir();
        using var engine = StageTestSupport.CreateEngine(dir);
        var head = StageTestSupport.AddFixture(engine, "Spot 1", 1, Vector3.Zero, universe: 3, address: 1);
        var tools = StageTestSupport.CreateTools();
        var stage = StagePanelContext.ForEngine(engine, tools);
        var view = new FixtureInspectorView(stage);
        engine.Selection.Set(head.Id);
        var slots = new byte[512];
        slots[0] = 255;
        Assert.True(StageTestSupport.TickUntil(engine, () =>
        {
            engine.Input.Submit(new Dmx.Input.DmxSourceId("test", "console"), 3, slots);
            return engine.Dmx.IsConsoleDriven(head.Id);
        }, TimeSpan.FromSeconds(5)));
        view.Controls.Refresh(engine, head);
        Assert.Equal(ControlLock.LiveDmx, view.Controls.Lock);

        // A locked fixture is not changed by its manual controls.
        view.SetManual(engine, head, view.Controls.Find("Dimmer")!, 0.3f);
        Assert.False(head.Get<FixtureComponent>()!.ManualAttributes.ContainsKey("Dimmer"));
        Assert.False(engine.Undo.CanUndo);

        // Drawing the panel shows the lock reason with the universe.
        using var ui = new HeadlessImGui(new Vector2(1200, 900));
        DrawSection(ui, stage, head);
        Assert.Contains("universe 3", StageLoc.Format(StageStr.LockLiveDmx, "3"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_fixture_cabled_to_a_console_is_console_driven_and_its_port_shows_the_cable()
    {
        Loc.SetLanguage("en");
        using var dir = new TempDir();
        using var engine = StageTestSupport.CreateEngine(dir);
        var head = StageTestSupport.AddFixture(engine, "Spot 1", 1, new Vector3(1, 0, 5));
        var console = StageTestSupport.AddConsole(engine, "Desk", "DMX A", 1);
        var ports = engine.Network.Ports.GetPorts(head);
        var dmxIn = ports.First(p => p.Role is PortRole.DataIn && p.Signal == SignalKind.Dmx512);
        var cable = StageTestSupport.AddCable(engine, CableCatalog.Ids.DmxXlr5, "DMX-1", (console, "DMX A"), (head, dmxIn.Name));
        engine.Tick(0.02);
        Assert.True(engine.Dmx.IsConsoleDriven(head.Id));

        var tools = StageTestSupport.CreateTools();
        var stage = StagePanelContext.ForEngine(engine, tools);
        var controls = new FixtureControls();
        controls.Refresh(engine, head);
        Assert.Equal(ControlLock.Console, controls.Lock);

        var facts = DeviceFacts.Build(engine, head);
        var row = facts.Ports.Single(p => p.Port.Name == dmxIn.Name);
        Assert.Equal(cable.Id, row.CableId);
        Assert.Contains("DMX-1", row.CableText, StringComparison.Ordinal);
        Assert.Contains("m", row.CableText, StringComparison.Ordinal);
        Assert.Equal(1f, row.CableColor.W);
        Assert.True(facts.Ports.Where(p => p.Port.Name != dmxIn.Name).All(p => p.CableId is null));

        // The console's own port shows the same cable; clicking it asks for the link view (or selects the cable).
        var consoleFacts = DeviceFacts.Build(engine, console);
        Assert.Equal(cable.Id, consoleFacts.Ports.Single().CableId);
        var view = new DeviceFactsView(stage);
        Guid? requested = null;
        tools.CableLinkRequested += id => requested = id;
        view.OpenCable(cable.Id);
        Assert.Equal(cable.Id, requested);
        var unhandled = new DeviceFactsView(StagePanelContext.ForEngine(engine, StageTestSupport.CreateTools()));
        unhandled.OpenCable(cable.Id);
        Assert.Equal(cable.Id, engine.Selection.Primary);
    }

    [Fact]
    public void Multi_selection_sets_every_selected_fixture_with_the_attribute_in_one_step()
    {
        Loc.SetLanguage("en");
        using var dir = new TempDir();
        using var engine = StageTestSupport.CreateEngine(dir);
        var a = StageTestSupport.AddFixture(engine, "A", 1, new Vector3(-1, 0, 5), address: 1);
        var b = StageTestSupport.AddFixture(engine, "B", 2, new Vector3(1, 0, 5), address: 101);
        var wall = StageTestSupport.AddWall(engine, "Back wall", 8, 4);
        engine.Tick(0.02);
        var stage = StagePanelContext.ForEngine(engine, StageTestSupport.CreateTools());
        engine.Selection.Set(new[] { wall.Id, b.Id, a.Id });
        var view = new FixtureInspectorView(stage);
        view.Controls.Refresh(engine, a);
        view.SetManual(engine, a, view.Controls.Find("Tilt")!, 45f);
        view.SetManual(engine, a, view.Controls.Find("Tilt")!, 50f);
        Assert.Equal(50f, a.Get<FixtureComponent>()!.ManualAttributes["Tilt"]);
        Assert.Equal(50f, b.Get<FixtureComponent>()!.ManualAttributes["Tilt"]);
        engine.Undo.Undo();
        Assert.False(engine.Undo.CanUndo, "a drag of one control on two fixtures is one step");
        Assert.False(b.Get<FixtureComponent>()!.ManualAttributes.ContainsKey("Tilt"));
    }

    [Fact]
    public void Patch_and_mode_edits_are_undoable_and_clamped()
    {
        using var dir = new TempDir();
        using var engine = StageTestSupport.CreateEngine(dir);
        var head = StageTestSupport.AddFixture(engine, "Spot", 7, Vector3.Zero, universe: 1, address: 1);
        var fixture = head.Get<FixtureComponent>()!;
        var view = new FixtureInspectorView(StagePanelContext.ForEngine(engine, StageTestSupport.CreateTools()));
        Assert.True(view.PatchTo(head, fixture, 0, 1, 2, 101));
        Assert.Equal(new DmxAddress(2, 101, 1), fixture.Addresses[0]);
        Assert.False(view.PatchTo(head, fixture, 0, 1, 2, 101), "an unchanged address records nothing");
        Assert.True(view.PatchTo(head, fixture, 0, 1, -5, 9999));
        Assert.Equal(new DmxAddress(1, 512, 1), fixture.Addresses[0]);
        engine.Undo.Undo();
        engine.Undo.Undo();
        Assert.Equal(new DmxAddress(1, 1, 1), fixture.Addresses[0]);
        engine.Tick(0.02);
        Assert.Equal(1, engine.Dmx.BuildPatch().Find(head.Id)!.Ranges[0].Start);
    }

    [Fact]
    public void Manufacturer_data_comes_from_gdtf_and_specs_with_illustrated_connectors()
    {
        Loc.SetLanguage("en");
        using var dir = new TempDir();
        using var engine = StageTestSupport.CreateEngine(dir);
        var head = StageTestSupport.AddFixture(engine, "Spot", 7, Vector3.Zero);
        engine.Tick(0.02);
        var facts = DeviceFacts.Build(engine, head);
        Assert.Contains("Martin", facts.Title, StringComparison.Ordinal);
        Assert.Contains("Viper", facts.Title, StringComparison.Ordinal);
        Assert.Contains("channels", facts.Subtitle, StringComparison.Ordinal);
        var labels = facts.Rows.Select(r => r.Label).ToHashSet();
        foreach (var expected in new[]
                 {
                     StageStr.FactManufacturer, StageStr.FactModel, StageStr.FactCategory, StageStr.FactWeight, StageStr.FactPower,
                     StageStr.FactIntensity, StageStr.FactBeamAngle, StageStr.FactPanTiltRange,
                 })
            Assert.Contains(expected, labels);
        Assert.Contains("kg", facts.Rows.Single(r => r.Label == StageStr.FactWeight).Value, StringComparison.Ordinal);
        Assert.Contains("W", facts.Rows.Single(r => r.Label == StageStr.FactPower).Value, StringComparison.Ordinal);
        Assert.Contains("cd", facts.Rows.Single(r => r.Label == StageStr.FactIntensity).Value, StringComparison.Ordinal);
        Assert.NotEmpty(facts.Ports);
        Assert.Contains(facts.Ports, p => p.Port.Role == PortRole.PowerIn);
        Assert.Contains(facts.Ports, p => p.Port.IsDataInput);
        Assert.All(facts.Ports, p => Assert.True(ConnectorIcons.HasDrawing(p.Port.Connector.IllustrationKey), p.Port.Connector.IllustrationKey));

        // Russian labels.
        Loc.SetLanguage("ru");
        try
        {
            var ru = DeviceFacts.Build(engine, head);
            Assert.Contains("каналов", ru.Subtitle, StringComparison.Ordinal);
        }
        finally
        {
            Loc.SetLanguage("en");
        }
    }

    [Fact]
    public void Every_connector_of_the_catalogue_has_its_own_illustration_and_draws_without_errors()
    {
        var keys = ConnectorCatalog.All.Select(c => c.IllustrationKey).Distinct().ToList();
        Assert.All(keys, k => Assert.True(ConnectorIcons.HasDrawing(k), k));
        using var ui = new HeadlessImGui(new Vector2(800, 600));
        ui.Frame(() =>
        {
            ImGui.Begin("icons");
            var dl = ImGui.GetWindowDrawList();
            int before = dl.VtxBuffer.Size;
            float x = 0;
            foreach (var key in keys.Append("connector-unknown").Append("not-a-key"))
            {
                ConnectorIcons.Draw(dl, new Vector2(x, 10), 40f, key);
                x += 44f;
            }

            Assert.True(dl.VtxBuffer.Size - before > keys.Count * 20, "every connector draws geometry");
            ImGui.End();
        });
    }

    [Fact]
    public void Unresolved_fixture_shows_why_and_has_no_controls()
    {
        Loc.SetLanguage("en");
        using var dir = new TempDir();
        using var engine = StageTestSupport.CreateEngine(dir);
        var ghost = StageTestSupport.AddFixture(engine, "Ghost", 9, Vector3.Zero, spec: "Nobody@Nothing@v0.gdtf");
        engine.Tick(0.02);
        var controls = new FixtureControls();
        controls.Refresh(engine, ghost);
        Assert.Equal(ControlLock.Unresolved, controls.Lock);
        Assert.Empty(controls.Controls);
        Assert.False(string.IsNullOrEmpty(controls.ResolutionError));
        var facts = DeviceFacts.Build(engine, ghost);
        Assert.NotNull(facts.Note);
        using var ui = new HeadlessImGui(new Vector2(1000, 900));
        DrawSection(ui, StagePanelContext.ForEngine(engine, StageTestSupport.CreateTools()), ghost);
    }

    [Fact]
    public void Properties_sections_draw_for_fixtures_walls_consoles_and_without_engine_in_both_languages()
    {
        using var dir = new TempDir();
        using var engine = StageTestSupport.CreateEngine(dir);
        var head = StageTestSupport.AddFixture(engine, "Spot", 7, Vector3.Zero);
        var wall = StageTestSupport.AddWall(engine, "Wall", 6, 3);
        var console = StageTestSupport.AddConsole(engine, "Desk", "DMX A", 1);
        engine.Tick(0.02);
        var stage = StagePanelContext.ForEngine(engine, StageTestSupport.CreateTools());
        using var ui = new HeadlessImGui(new Vector2(1000, 1600));
        foreach (string language in new[] { "en", "ru" })
        {
            Loc.SetLanguage(language);
            foreach (var e in new[] { head, wall, console })
            {
                engine.Selection.Set(e.Id);
                DrawSection(ui, stage, e);
            }
        }

        Loc.SetLanguage("en");
        // Without an engine the fixture shows a hint and its patch; walls are still editable.
        var noEngine = new StagePanelContext(() => null, () => engine.Scene, () => engine.Selection, () => engine.Undo,
            StageTestSupport.CreateTools(), new Core.Settings.AppSettings());
        DrawSection(ui, noEngine, head);
        DrawSection(ui, noEngine, wall);
    }

    /// <summary>Draws the stage properties section for an entity for a few frames inside a window.</summary>
    internal static void DrawSection(HeadlessImGui ui, StagePanelContext stage, Entity entity)
    {
        var section = new StagePropertiesSection(stage);
        for (int i = 0; i < 3; i++)
        {
            ui.Frame(() =>
            {
                ImGui.SetNextWindowSize(new Vector2(420, 1500));
                ImGui.Begin("props-host");
                section.Draw(entity, 92f);
                ImGui.End();
            });
        }
    }
}
