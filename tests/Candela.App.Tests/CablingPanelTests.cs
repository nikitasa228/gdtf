using System.Numerics;
using Candela.App.Editing;
using Candela.App.Input;
using Candela.App.Panels;
using Candela.App.Session;
using Candela.App.Settings;
using Candela.App.Ui;
using Candela.Core.Diagnostics;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;
using Candela.Electrical;
using Candela.Engine;
using Hexa.NET.ImGui;

namespace Candela.App.Tests;

/// <summary>
/// Cabling beyond the tool: consoles lock the fixtures cabled to them (and only those), power cabled from a distro
/// overloads a breaker in the Issues panel, the cable link window selects devices and follows cables, the Devices
/// section places consoles, distros and GDTF devices, cables and devices survive a project save and reopen, copied
/// fixtures are patched to the next free addresses, the cable strings are complete in both languages, and scripts can
/// click world points and entities.
/// </summary>
[Collection(UiStateCollection.Name)]
public sealed class CablingPanelTests
{
    [Fact]
    public void Fixtures_cabled_to_a_console_are_locked_with_the_cable_index_and_a_universe_mismatch_is_an_issue()
    {
        Loc.SetLanguage("en");
        using var dir = new TempDir();
        using var s = new CablingScene(dir);
        var tool = s.Tool;
        tool.CableTypeId = CableCatalog.Ids.DmxXlr5;
        tool.Click(CablingScene.DeviceAt(s.Console));
        tool.ChoosePort("DMX A");
        tool.Click(CablingScene.DeviceAt(s.Spots[0]));
        tool.Click(CablingScene.DeviceAt(s.Spots[1]));
        tool.Finish();
        s.Engine.Tick(0.02);

        var controls = new FixtureControls();
        controls.Refresh(s.Engine, s.Spots[1]);
        Assert.Equal(ControlLock.Console, controls.Lock);
        controls.Refresh(s.Engine, s.Spots[2]);
        Assert.Equal(ControlLock.Manual, controls.Lock); // a console in the scene, but no cable to it
        Assert.True(s.Engine.NetworkSolution.IsConsoleDriven(s.Spots[1].Id));
        Assert.False(s.Engine.NetworkSolution.IsConsoleDriven(s.Spots[2].Id));

        // The properties panel names the console, the output, the cable index and the universe.
        var view = new FixtureInspectorView(s.Stage);
        using (var ui = new HeadlessImGui(new Vector2(900, 1600)))
        {
            for (int i = 0; i < 2; i++)
            {
                ui.Frame(() =>
                {
                    ImGui.SetNextWindowSize(new Vector2(420, 1500));
                    ImGui.Begin("props");
                    view.Draw(s.Spots[1], s.Spots[1].Get<FixtureComponent>()!, 92f);
                    ImGui.End();
                });
            }
        }

        Assert.Equal("Locked: driven by console 'Console 1' (DMX A · cable 2, universe 1).", view.LockText);
        Assert.Equal("'Console 1' DMX A · cable 2 · U1", view.DataText);
        Assert.Equal("not cabled to power", view.PowerText);

        // A console output on another universe: the patch no longer matches what arrives.
        var console = s.Console.Get<ConsoleComponent>()!;
        s.Engine.Undo.Execute(Core.Editing.ModifyComponentCommand.Create(console, c => c.Outputs[0].FirstUniverse = 5, "universe"));
        s.Engine.RefreshIssues();
        var mismatch = s.Engine.Issues.Current.Where(i => i.Code == IssueCodes.DataUniverseMismatch).ToList();
        Assert.Contains(mismatch, i => i.Entities.Contains(s.Spots[0].Id));
        Assert.Equal(IssueSeverity.Error, mismatch[0].Severity);

        // Unplugging (undo of the cable) gives the fixtures back to manual control.
        s.Engine.Undo.Undo();
        s.Engine.Undo.Undo();
        s.Engine.Tick(0.02);
        controls.Refresh(s.Engine, s.Spots[1]);
        Assert.Equal(ControlLock.Manual, controls.Lock);
    }

    [Fact]
    public void Power_cabled_from_a_distro_through_linked_fixtures_overloads_the_breaker_and_the_issue_selects_them()
    {
        Loc.SetLanguage("en");
        using var dir = new TempDir();
        using var s = new CablingScene(dir);
        const string aeco = "ACME@ACME_AECO_20_(AECO_20)@ACME_AECO_20_(AECO_20).gdtf";
        var fixtures = Enumerable.Range(0, 3)
            .Select(i => StageTestSupport.AddFixture(s.Engine, $"AECO {i + 1}", 10 + i, new Vector3(4f + i, 2f, 0.5f), 2, 1 + 20 * i, aeco))
            .ToArray();
        var tool = s.Tool;
        tool.CableTypeId = CableCatalog.Ids.PowerconTrue1;
        // The distro has six TRUE1 outlets: the picker asks.
        Assert.Equal(CableClickOutcome.PickPort, tool.Click(CablingScene.DeviceAt(s.Distro)));
        tool.ChoosePort("TRUE1 1");
        // Each fixture: into its inlet, and the next cable continues from its TRUE1 link.
        foreach (var f in fixtures)
            Assert.Equal(CableClickOutcome.Connected, tool.Click(CablingScene.DeviceAt(f)));
        tool.Finish();
        Assert.Equal(3, s.Cables().Count);

        var solution = s.Engine.NetworkSolution;
        Assert.Equal(fixtures.Select(f => f.Id),
            solution.PowerCircuits.Single(c => c.SourceEntityId == s.Distro.Id && c.Outlet.Name == "TRUE1 1").Feeds.Select(f => f.DeviceEntityId));
        s.Engine.RefreshIssues();
        var overload = s.Engine.Issues.Current.Single(i => i.Code == IssueCodes.PowerBreakerOverload);
        Assert.Equal(IssueSeverity.Error, overload.Severity);
        Assert.Contains("'TRUE1 1' of 'Distro 1'", overload.Message, StringComparison.Ordinal);
        Assert.Contains(s.Distro.Id, overload.Entities);
        Assert.All(fixtures, f => Assert.Contains(f.Id, overload.Entities));

        // The power source section shows the outlet's load against its breaker; the fixture its feed.
        var connections = new ConnectionInspectorView(s.Stage);
        connections.Refresh(s.Engine, s.Distro);
        var (text, overloaded) = connections.PowerInfo!.Loads["TRUE1 1"];
        Assert.True(overloaded);
        Assert.Contains("4740 W", text, StringComparison.Ordinal);
        Assert.Contains("of 16 A", text, StringComparison.Ordinal);
        Assert.Contains("Total 4740 W", connections.PowerInfo.Total, StringComparison.Ordinal);
    }

    [Fact]
    public void Cable_link_window_lists_the_line_selects_a_clicked_device_and_follows_a_cable()
    {
        Loc.SetLanguage("en");
        using var dir = new TempDir();
        using var s = new CablingScene(dir);
        var tool = s.Tool;
        tool.CableTypeId = CableCatalog.Ids.DmxXlr5;
        tool.Click(CablingScene.DeviceAt(s.Console));
        tool.ChoosePort("DMX C");
        foreach (var spot in s.Spots)
            tool.Click(CablingScene.DeviceAt(spot));
        tool.Finish();
        var cables = s.Cables();
        Assert.Equal(3, cables.Count);

        var panel = new CableLinksPanel(s.Stage);
        // Selecting a cable elsewhere shows it.
        s.Engine.Selection.Set(cables[1].Entity.Id);
        using var ui = new HeadlessImGui(new Vector2(1200, 600));
        for (int i = 0; i < 2; i++)
        {
            ui.Frame(() =>
            {
                ImGui.SetNextWindowSize(new Vector2(1100, 300));
                ImGui.Begin("links");
                panel.Draw();
                ImGui.End();
            });
        }

        var model = panel.Model!;
        Assert.Equal(cables[1].Entity.Id, model.CableId);
        Assert.Equal(new[] { "Console 1", "Spot 1", "Spot 2", "Spot 3" }, model.Cards.Select(c => c.Name));
        Assert.Equal("DMX C", model.Cards[0].Detail);
        Assert.Equal("#2 · 1.035", model.Cards[2].Detail);
        Assert.Equal("in: DMX IN", model.Cards[2].InPort);
        Assert.Equal(new[] { false, true, false }, model.Links.Select(l => l.Selected));
        Assert.Equal("DMX C · cable 3", model.Links[2].Index);
        Assert.True(model.Cards[1].OnCable && model.Cards[2].OnCable && !model.Cards[3].OnCable);

        // A click on a device card selects the device: the properties panel shows it.
        panel.SelectCard(2);
        Assert.Equal(s.Spots[1].Id, s.Engine.Selection.Primary);
        // The window keeps the cable while a device is selected, and follows a clicked cable.
        panel.Refresh();
        Assert.Equal(cables[1].Entity.Id, panel.Cable);
        panel.FollowLink(2);
        panel.Refresh();
        Assert.Equal(cables[2].Entity.Id, panel.Model!.CableId);
        Assert.True(panel.Model.Links[2].Selected);

        // Russian: the same strip in the UI language.
        Loc.SetLanguage("ru");
        try
        {
            panel.Refresh();
            Assert.Equal("DMX C · кабель 3", panel.Model!.Links[2].Index);
            Assert.Contains("юниверс", panel.Model.Source, StringComparison.Ordinal);
        }
        finally
        {
            Loc.SetLanguage("en");
        }

        // A cable that is gone says so.
        s.Engine.Undo.Undo();
        panel.Refresh();
        Assert.Null(panel.Model);
    }

    [Fact]
    public void Devices_of_every_section_are_placed_with_ports_names_and_a_patch_for_dimmers()
    {
        Loc.SetLanguage("en");
        using var dir = new TempDir();
        using var s = new CablingScene(dir);
        var groups = DeviceCatalog.BuiltIn.Select(d => d.Group).Distinct().ToList();
        Assert.Equal(Enum.GetValues<DeviceGroup>(), groups.OrderBy(g => g));
        var library = DeviceCatalog.LibraryDevices(s.Engine.Library);
        Assert.Contains(library, d => d.GdtfFile == "PRstage@Dimmer_Pack_4@Ver_1_02.gdtf" && d.IconKey == "dimmer");
        Assert.Contains(library, d => d.GdtfFile!.Contains("P3_PowerPort_2000", StringComparison.Ordinal));
        Assert.DoesNotContain(library, d => GenericDevices.IsGeneric(d.GdtfFile));

        foreach (var preset in DeviceCatalog.BuiltIn.Concat(library))
        {
            var ports = preset.Ports(s.Engine.ElectricalProfiles);
            Assert.True(ports.Count > 0, $"{preset.Id} has no ports");
            Assert.All(ports, p => Assert.True(ConnectorIcons.HasDrawing(p.Connector.IllustrationKey), $"{preset.Id}: {p.Connector.IllustrationKey}"));
        }

        var hit = new SurfaceHit(new Vector3(8f, 0f, 0f), Vector3.UnitZ, SurfaceKind.Ground);
        var console = DevicePlacement.Place(s.Stage, DeviceCatalog.Find("console-artnet-16")!, null, hit);
        Assert.Equal("Console 2", console.Name); // "Console 1" exists
        Assert.Equal(new Vector3(8f, 0f, 0f), console.WorldPosition);
        var output = Assert.Single(console.Get<ConsoleComponent>()!.Outputs);
        Assert.Equal(("Ethernet", 1, 16), (output.Signal, output.FirstUniverse, output.UniverseCount));
        Assert.Equal(console.Id, s.Engine.Selection.Primary);

        var dimmerPreset = DeviceCatalog.Find("dimmer-4x10")!;
        var entry = DevicePlacement.EntryOf(s.Engine, dimmerPreset)!;
        var info = new FixtureInfoCache(s.Engine.Library).GetNow(entry, TimeSpan.FromSeconds(10));
        var dimmer = DevicePlacement.Place(s.Stage, dimmerPreset, info, hit);
        var fixture = dimmer.Get<FixtureComponent>()!;
        Assert.Equal("Dimmer 1", dimmer.Name);
        Assert.Equal("4 channels", fixture.GdtfMode);
        Assert.Equal(4, fixture.FixtureId); // after the three spots
        Assert.Equal(new DmxAddress(1, 27, 1), Assert.Single(fixture.Addresses)); // the first gap of four: after Spot 1 (26 channels)
        Assert.Equal(new[] { "Dimmer 1", "Dimmer 2", "Dimmer 3", "Dimmer 4" },
            s.Engine.Network.Ports.GetPorts(dimmer).Where(p => p.Role == PortRole.PowerDistribution).Select(p => p.Name));

        var splitter = DevicePlacement.Place(s.Stage, DeviceCatalog.Find("dmx-splitter-6")!, null, hit);
        Assert.Equal("Splitter 1", splitter.Name);
        Assert.Empty(splitter.Get<FixtureComponent>()!.Addresses);
        var distro = DevicePlacement.Place(s.Stage, DeviceCatalog.Find("distro-32-schuko")!, null, hit);
        Assert.Equal("Distro 2", distro.Name);
        Assert.Equal(new[] { 1, 2, 3, 1, 2, 3 }, distro.Get<PowerSourceComponent>()!.Outlets.Select(o => o.Phase));

        // A splitter behind the console starts new DMX segments; the console drives fixtures behind it.
        var tool = s.Tool;
        tool.CableTypeId = CableCatalog.Ids.DmxXlr5;
        tool.Click(CablingScene.DeviceAt(s.Console));
        tool.ChoosePort("DMX A");
        Assert.Equal(CableClickOutcome.Connected, tool.Click(CablingScene.DeviceAt(splitter)));
        tool.Cancel();
        Assert.Equal(CableClickOutcome.PickPort, tool.Click(CablingScene.DeviceAt(splitter)));
        tool.ChoosePort("DMX Out 3");
        Assert.Equal(CableClickOutcome.Connected, tool.Click(CablingScene.DeviceAt(s.Spots[2])));
        tool.Finish();
        Assert.True(s.Engine.NetworkSolution.IsConsoleDriven(s.Spots[2].Id));

        // Placing is one undo step each.
        int entities = s.Engine.Scene.All().Count();
        s.Engine.Undo.Undo();
        s.Engine.Undo.Undo();
        Assert.Equal(entities - 2, s.Engine.Scene.All().Count());
    }

    [Fact]
    public void Cables_consoles_power_sources_and_generic_devices_survive_save_and_reopen()
    {
        Loc.SetLanguage("en");
        using var dir = new TempDir();
        using var log = TestLog.Create();
        var settings = new SettingsStore(log, dir.File("settings.json"));
        settings.Load();
        using var session = EngineTestSupport.CreateSession(log, settings, dir);
        var engine = session.Engine!;
        var stage = new StagePanelContext(() => engine, () => session.Scene, () => session.Selection, () => session.Undo,
            StageTestSupport.CreateTools(), settings.Settings);
        var hit = new SurfaceHit(Vector3.Zero, Vector3.UnitZ, SurfaceKind.Ground);
        var console = DevicePlacement.Place(stage, DeviceCatalog.Find("console-artnet-16")!, null, hit with { Position = new Vector3(0f, -3f, 0f) });
        var node = DevicePlacement.Place(stage, DeviceCatalog.Find("artnet-node-2")!, null, hit with { Position = new Vector3(3f, 0f, 0f) });
        var mains = DevicePlacement.Place(stage, DeviceCatalog.Find("mains-cee32-3p")!, null, hit with { Position = new Vector3(6f, 0f, 0f) });
        var spot = new Entity("Spot") { Local = new Core.Math.Transform(new Vector3(3f, 2f, 4f)) };
        spot.Add(new FixtureComponent { GdtfSpec = StageTestSupport.Viper, FixtureId = 7, Addresses = { new DmxAddress(2, 1) } });
        session.Undo.Execute(new Core.Editing.AddEntityCommand(session.Scene, spot));

        // Network console → Ethernet into the node (one route point on the floor), DMX B of the node to the spot.
        var tool = new CableTool(stage) { CableTypeId = CableCatalog.Ids.Ethernet };
        Assert.Equal(CableClickOutcome.Started, tool.Click(CablingScene.DeviceAt(console)));
        Assert.Equal(CableClickOutcome.PointAdded, tool.Click(CablingScene.Ground(1.5f, -1.5f)));
        Assert.Equal(CableClickOutcome.Connected, tool.Click(CablingScene.DeviceAt(node)));
        tool.Cancel();
        tool.CableTypeId = CableCatalog.Ids.DmxXlr5;
        Assert.Equal(CableClickOutcome.PickPort, tool.Click(CablingScene.DeviceAt(node)));
        tool.ChoosePort("DMX B");
        Assert.Equal(CableClickOutcome.Connected, tool.Click(CablingScene.DeviceAt(spot)));
        tool.Cancel();
        // Mains: a 32 A 3-phase supply into a new distro.
        var distro = DevicePlacement.Place(stage, DeviceCatalog.Find("distro-32-true1")!, null, hit with { Position = new Vector3(6f, 3f, 0f) });
        tool.CableTypeId = CableCatalog.Ids.Cee32ThreePhase;
        // The supply has its outlet and a mains input: the picker asks.
        Assert.Equal(CableClickOutcome.PickPort, tool.Click(CablingScene.DeviceAt(mains)));
        Assert.Equal(CableClickOutcome.Started, tool.ChoosePort("CEE 32 A 3~"));
        Assert.Equal(CableClickOutcome.Connected, tool.Click(CablingScene.DeviceAt(distro)));
        tool.Cancel();
        Assert.True(engine.NetworkSolution.IsConsoleDriven(spot.Id));

        string path = dir.File("cabled.candela");
        Assert.True(session.SaveAs(path).Succeeded);
        var before = Describe(session.Scene);
        Assert.True(session.New().Succeeded);
        var opened = session.Open(path);
        Assert.True(opened.Succeeded, opened.Message);
        Assert.Equal(before, Describe(session.Scene));
        var reopenedSpot = session.Scene.All().Single(e => e.Name == "Spot");
        Assert.True(session.Engine!.NetworkSolution.IsConsoleDriven(reopenedSpot.Id));
        // The generic node's GDTF travels in the project.
        using (var zip = System.IO.Compression.ZipFile.OpenRead(path))
            Assert.Contains(zip.Entries, e => e.FullName.EndsWith("Candela@Generic_ArtNet_Node_2@v1.gdtf", StringComparison.Ordinal));
        Assert.Contains(before, l => l.StartsWith("Power 1|cable|cee32-3p|2|1|Mains 1:", StringComparison.Ordinal));
    }

    [Fact]
    public void Copied_fixtures_that_overlap_are_patched_to_the_next_free_addresses_in_one_step()
    {
        using var dir = new TempDir();
        using var s = new CablingScene(dir);
        var scene = s.Engine.Scene;
        s.Engine.Selection.Set(s.Spots.Select(e => e.Id));
        var copies = EditOperations.Duplicate(scene, s.Engine.Selection, s.Engine.Undo);
        Assert.Equal(3, copies.Count);
        var overlapping = DuplicatePatch.Overlapping(scene, s.Engine.Profiles, copies);
        Assert.Equal(3, overlapping.Count);
        Assert.Empty(DuplicatePatch.Overlapping(scene, s.Engine.Profiles, s.Spots.Select(e => e.Id).Take(0)));

        Assert.Equal(3, DuplicatePatch.Repatch(scene, s.Engine.Profiles, s.Engine.Undo, overlapping));
        var addresses = copies.Select(id => scene.Find(id)!.Get<FixtureComponent>()!.Addresses.Single()).ToList();
        // The originals end at 1.094 (26 channels each, the gaps between them are too small): the copies follow one
        // after the other.
        Assert.Equal(new[] { new DmxAddress(1, 95), new DmxAddress(1, 121), new DmxAddress(1, 147) }, addresses);
        Assert.Empty(DuplicatePatch.Overlapping(scene, s.Engine.Profiles, copies));
        // One undo step brings the duplicated addresses back.
        s.Engine.Undo.Undo();
        Assert.Equal(new DmxAddress(1, 1), scene.Find(copies[0])!.Get<FixtureComponent>()!.Addresses.Single());

        // The preference is stored in its own file and read back; a damaged file gives the default.
        var prefs = new EditorPreferences { DuplicatePatch = DuplicatePatchMode.NextFree };
        Assert.True(prefs.Save(dir.Path));
        Assert.Equal(DuplicatePatchMode.NextFree, EditorPreferences.Load(dir.Path).DuplicatePatch);
        File.WriteAllText(dir.File(EditorPreferences.FileName), "{ broken");
        Assert.Equal(DuplicatePatchMode.Ask, EditorPreferences.Load(dir.Path).DuplicatePatch);
    }

    [Fact]
    public void Cable_strings_cable_types_and_devices_are_complete_in_both_languages()
    {
        var keys = Enum.GetValues<CableStr>();
        var entries = CableLoc.Entries.ToList();
        Assert.Equal(keys.Length, entries.Select(e => e.Key).Distinct().Count());
        foreach (var (key, en, ru) in entries)
        {
            Assert.False(string.IsNullOrWhiteSpace(en), key.ToString());
            Assert.False(string.IsNullOrWhiteSpace(ru), key.ToString());
            Assert.Equal(Placeholders(en), Placeholders(ru));
        }

        Assert.Equal(CableCatalog.Default.All.Select(t => t.Id).OrderBy(i => i), CableLoc.TranslatedCableIds.OrderBy(i => i));
        Assert.Equal(DeviceCatalog.BuiltIn.Select(d => d.Id).OrderBy(i => i), CableLoc.TranslatedDeviceIds.OrderBy(i => i));
        Loc.SetLanguage("ru");
        try
        {
            Assert.Equal("DMX-кабель XLR 5-pin", CableLoc.CableName(CableCatalog.Default.Find("dmx-xlr5")!));
            Assert.Equal("Устройства", CableLoc.Get(CableStr.LibDevices));
            Assert.Equal("Дистрибьютор", DevicePlacement.ShortName(DeviceCatalog.Find("distro-125")!));
        }
        finally
        {
            Loc.SetLanguage("en");
        }

        static string Placeholders(string text) =>
            string.Join(",", System.Text.RegularExpressions.Regex.Matches(text, @"\{\d+\}").Select(m => m.Value).OrderBy(v => v));
    }

    [Fact]
    public void Scripts_click_world_points_and_entities_resolved_when_they_run()
    {
        var script = InputScript.Parse("""
            { "actions": [
                { "type": "click", "world": [1.5, -2, 0.25] },
                { "type": "move", "entity": "Console 1" },
                { "type": "click", "x": 10, "y": 20 }
            ] }
            """);
        Assert.Equal(new Vector3(1.5f, -2f, 0.25f), script.Actions[0].World);
        Assert.True(script.Actions[0].HasScenePoint);
        Assert.Equal("Console 1", script.Actions[1].Entity);
        Assert.False(script.Actions[2].HasScenePoint);
        Assert.Throws<FormatException>(() => InputScript.Parse("""{ "actions": [ { "type": "click", "world": [1, 2] } ] }"""));
        Assert.Throws<FormatException>(() => InputScript.Parse("""{ "actions": [ { "type": "click", "world": [1, "a", 3] } ] }"""));
    }

    private static List<string> Describe(Core.SceneGraph.Scene scene)
    {
        var lines = new List<string>();
        foreach (var e in scene.All().OrderBy(e => e.Name, StringComparer.Ordinal))
        {
            foreach (var c in e.Components)
            {
                lines.Add(c switch
                {
                    CableComponent cable => $"{e.Name}|cable|{cable.CableTypeId}|{cable.Nodes.Count}|{cable.Segments.Count}|" +
                                            string.Join(";", cable.Endpoints.Select(p => $"{scene.Find(p.EntityId)?.Name}:{p.PortName}")) + "|" +
                                            string.Join(";", cable.Nodes.Select(n => n.Anchor is { } a ? scene.Find(a.EntityId)?.Name : "-")),
                    ConsoleComponent console => $"{e.Name}|console|{console.ProtocolId}|" +
                                                string.Join(";", console.Outputs.Select(o => $"{o.PortName}:{o.Signal}:{o.FirstUniverse}:{o.UniverseCount}")),
                    PowerSourceComponent source => $"{e.Name}|source|{source.Phases}|" +
                                                   string.Join(";", source.Outlets.Select(o => $"{o.PortName}:{o.Connector}:{o.BreakerA}:{o.Phase}")),
                    FixtureComponent fixture => $"{e.Name}|fixture|{fixture.GdtfSpec}|{fixture.GdtfMode}|{string.Join(";", fixture.Addresses)}",
                    _ => $"{e.Name}|{c.GetType().Name}",
                });
            }
        }

        return lines;
    }
}
