using System.Net;
using System.Numerics;
using Candela.App.Panels;
using Candela.App.Session;
using Candela.App.Ui;
using Candela.Core.Settings;
using Candela.Dmx.Input;
using Candela.Engine;
using Candela.Protocols;
using Candela.Protocols.ArtNet;
using Hexa.NET.ImGui;

namespace Candela.App.Tests;

/// <summary>
/// The external-control panel's model with real sockets on the loopback interface: the acceptance path (Art-Net DMX
/// and ArtTimeCode from a "console", fixtures follow), the test console, the recorder and settings robustness.
/// </summary>
[Collection(UiStateCollection.Name)]
public sealed class ExternalControlTests
{
    [Fact]
    public void Art_net_from_a_console_drives_the_fixtures_and_shows_universes_sources_and_timecode()
    {
        Loc.SetLanguage("en");
        using var dir = new TempDir();
        using var engine = StageTestSupport.CreateEngine(dir);
        var head = StageTestSupport.AddFixture(engine, "Spot 1", 1, new Vector3(0, 0, 6), universe: 2, address: 1);
        engine.Tick(0.02);
        using var tools = StageTestSupport.CreateTools();
        var model = tools.External;
        model.CanSave = false;
        int port = StageTestSupport.FreeUdpPort();
        model.Settings.ArtNetPort = port;
        var app = new AppSettings { NetworkInterface = "", ArtNetUniverseOffset = 1 };
        model.Bind(engine, app);
        model.SetEnabled(engine, app, ProtocolIds.ArtNet, true);
        Assert.True(model.IsEnabled(ProtocolIds.ArtNet));
        model.Poll(engine);
        var status = model.Statuses[0];
        Assert.Equal(ProtocolIds.ArtNet, status.ProtocolId);
        Assert.True(status.State is ProtocolState.Running or ProtocolState.Warning, status.Message + " " + status.LastError);

        // A console sends universe 2 (port-address 1 with offset 1) at full and timecode.
        using var sender = new ArtNetSender(IPAddress.Loopback);
        var target = new IPEndPoint(IPAddress.Loopback, port);
        var slots = new byte[512];
        var profile = engine.Dmx.GetState(head.Id)!.Profile;
        int dimmer = profile.FindChannels("Dimmer")[0];
        int dimmerSlot = profile.Channels[dimmer].Offsets[0] - 1;
        slots[dimmerSlot] = 255;
        int shutter = profile.FindChannels("Shutter1").FirstOrDefault(-1);
        Assert.True(StageTestSupport.TickUntil(engine, () =>
        {
            sender.SendDmx(target, 1, slots);
            sender.SendTimeCode(target, new Timecode(1, 2, 3, 4, TimecodeType.Ebu25));
            return engine.Dmx.IsConsoleDriven(head.Id) && model.LastTimecode is not null;
        }, TimeSpan.FromSeconds(10)), "the fixture follows the console");
        _ = shutter;
        model.Poll(engine);
        Assert.True(model.IsActive(2));
        Assert.True(model.IsPatched(2));
        Assert.False(model.IsActive(1));
        Assert.Contains(model.Sources, s => s.Protocol == "Art-Net" && s.Address == "127.0.0.1" && s.Universes == "2");
        Assert.Equal(new Timecode(1, 2, 3, 4, TimecodeType.Ebu25), model.LastTimecode!.Value.Timecode);
        model.SelectedUniverse = 2;
        var read = model.ReadSelectedUniverse(engine, out bool live);
        Assert.True(live);
        Assert.Equal(255, read[dimmerSlot]);

        var controls = new FixtureControls();
        controls.Refresh(engine, head);
        Assert.Equal(ControlLock.LiveDmx, controls.Lock);
        Assert.Equal(1f, controls.Find("Dimmer")!.Live, 2);

        // The panel draws every tab with live data, in both languages.
        using var ui = new HeadlessImGui(new Vector2(1400, 900));
        var panel = new ExternalControlPanel(StagePanelContext.ForEngine(engine, tools, app));
        DrawPanel(ui, panel);
        Loc.SetLanguage("ru");
        DrawPanel(ui, panel);
        Loc.SetLanguage("en");

        // Stopping withdraws the universes.
        model.SetEnabled(engine, app, ProtocolIds.ArtNet, false);
        Assert.False(engine.Input.ActiveUniverses().Contains(2));
    }

    [Fact]
    public void The_test_console_drives_this_computer_over_art_net_without_a_real_console()
    {
        using var dir = new TempDir();
        using var engine = StageTestSupport.CreateEngine(dir);
        var head = StageTestSupport.AddFixture(engine, "Spot 1", 1, new Vector3(0, 0, 6), universe: 1, address: 1);
        engine.Tick(0.02);
        using var model = new ExternalControl { CanSave = false };
        model.Settings.ArtNetPort = StageTestSupport.FreeUdpPort();
        var app = new AppSettings();
        model.Bind(engine, app);
        Assert.True(model.StartTestPattern(engine, app), model.Message);
        Assert.True(model.IsEnabled(ProtocolIds.ArtNet), "sending to this computer starts the Art-Net receiver");
        Assert.True(model.TestPatternRunning);
        Assert.True(StageTestSupport.TickUntil(engine, () => engine.Dmx.IsConsoleDriven(head.Id) && model.LastTimecode is not null,
            TimeSpan.FromSeconds(10)), model.TestPatternError);
        Assert.True(model.TestFramesSent > 0);
        model.StopTestPattern();
        Assert.False(model.TestPatternRunning);

        model.Settings.TestTarget = "not-an-address";
        Assert.False(model.StartTestPattern(engine, app));
        Assert.True(model.MessageIsError);
    }

    [Fact]
    public void Planned_protocols_cannot_be_enabled_and_say_why()
    {
        using var dir = new TempDir();
        using var engine = StageTestSupport.CreateEngine(dir);
        using var model = new ExternalControl { CanSave = false };
        var app = new AppSettings();
        model.Bind(engine, app);
        model.SetEnabled(engine, app, ProtocolIds.MaNet3, true);
        Assert.False(model.IsEnabled(ProtocolIds.MaNet3));
        Assert.True(model.MessageIsError);
        Assert.Contains("MA-Net3", model.Message, StringComparison.Ordinal);
        Assert.Equal(15, ProtocolRegistry.All.Count);
        Assert.Equal(15, model.Statuses.Count);
    }

    [Fact]
    public void Recorder_records_saves_loads_plays_and_seeks_by_timecode()
    {
        Loc.SetLanguage("en");
        using var dir = new TempDir();
        using var engine = StageTestSupport.CreateEngine(dir);
        var head = StageTestSupport.AddFixture(engine, "Spot 1", 1, Vector3.Zero, universe: 1, address: 1);
        engine.Tick(0.02);
        using var model = new ExternalControl { CanSave = false };
        model.Bind(engine, new AppSettings());
        var source = new DmxSourceId("test", "console");
        var slots = new byte[512];
        model.StartRecording(engine);
        Assert.True(engine.Recorder.IsRecording);
        for (int i = 0; i < 30; i++)
        {
            slots[0] = (byte)(i * 8);
            engine.Input.Submit(source, 1, slots);
            engine.Input.SubmitTimecode(new Timecode(0, 0, 1, i % 25, TimecodeType.Ebu25));
            engine.Tick(1.0 / 25);
        }

        model.StopRecording(engine);
        Assert.NotNull(model.Recording);
        Assert.True(model.Recording!.Frames.Count >= 20);
        string path = dir.File("show");
        Assert.True(model.SaveRecording(path));
        Assert.True(File.Exists(path + DmxRecording.Extension));
        Assert.True(model.LoadRecording(path + DmxRecording.Extension));
        Assert.False(model.MessageIsError);
        Assert.Equal(path + DmxRecording.Extension, model.RecordingPath);

        engine.Input.Terminate(source, 1);
        model.Play(engine, 0);
        Assert.NotNull(engine.Player);
        Assert.True(model.SeekTimecode(engine, new Timecode(0, 0, 1, 10, TimecodeType.Ebu25)));
        engine.Tick(0.001);
        Assert.False(model.SeekTimecode(engine, new Timecode(5, 0, 0, 0, TimecodeType.Ebu25)));
        Assert.True(model.MessageIsError);
        ExternalControl.StopPlayback(engine);
        Assert.Null(engine.Player);

        // Damaged files are refused with a message, never an exception.
        File.WriteAllText(dir.File("bad.cdmx"), "not a recording");
        Assert.False(model.LoadRecording(dir.File("bad.cdmx")));
        Assert.False(model.LoadRecording(dir.File("missing.cdmx")));
        _ = head;
    }

    [Fact]
    public void Settings_are_sanitised_and_survive_a_damaged_file()
    {
        using var dir = new TempDir();
        var bad = new ExternalControlSettings
        {
            Enabled = new List<string> { "artnet", "artnet", "nonsense", null! },
            ArtNetPort = -4,
            SacnPort = 70000,
            EnttecUniverse = 0,
            TestTarget = " ",
            MergeMode = (DmxMergeMode)42,
        }.Sanitized();
        Assert.Equal(new[] { "artnet" }, bad.Enabled);
        Assert.Equal(6454, bad.ArtNetPort);
        Assert.Equal(5568, bad.SacnPort);
        Assert.Equal(1, bad.EnttecUniverse);
        Assert.Equal("127.0.0.1", bad.TestTarget);
        Assert.Equal(DmxMergeMode.Htp, bad.MergeMode);

        var model = new ExternalControl();
        model.Load(dir.Path);
        model.Settings.ArtNetPort = 7000;
        model.Settings.Enabled.Add(ProtocolIds.Sacn);
        Assert.True(model.Save());
        var again = new ExternalControl();
        again.Load(dir.Path);
        Assert.Equal(7000, again.Settings.ArtNetPort);
        Assert.Contains(ProtocolIds.Sacn, again.Settings.Enabled);

        File.WriteAllText(dir.File(ExternalControlSettings.FileName), "{ broken");
        var damaged = new ExternalControl();
        damaged.Load(dir.Path);
        Assert.Equal(6454, damaged.Settings.ArtNetPort);
        Assert.True(damaged.MessageIsError);

        var options = again.Settings.ToOptions(new AppSettings { ArtNetUniverseOffset = 0, NetworkInterface = "eth9" });
        Assert.Equal(7000, options.ArtNetPort);
        Assert.Equal(0, options.ArtNetUniverseOffset);
        Assert.Equal("eth9", options.NetworkInterface);
    }

    [Theory]
    [InlineData("01:02:03:04", TimecodeType.Ebu25, true, 1, 2, 3, 4)]
    [InlineData("00:00:10;12", TimecodeType.Smpte30, true, 0, 0, 10, 12)]
    [InlineData("00:00:10:25", TimecodeType.Ebu25, false, 0, 0, 0, 0)]
    [InlineData("24:00:00:00", TimecodeType.Ebu25, false, 0, 0, 0, 0)]
    [InlineData("1:2:3", TimecodeType.Ebu25, false, 0, 0, 0, 0)]
    [InlineData("aa:bb:cc:dd", TimecodeType.Ebu25, false, 0, 0, 0, 0)]
    [InlineData("", TimecodeType.Ebu25, false, 0, 0, 0, 0)]
    public void Timecodes_parse_strictly(string text, TimecodeType type, bool ok, int h, int m, int s, int f)
    {
        Assert.Equal(ok, ExternalControl.TryParseTimecode(text, type, out var tc));
        if (ok)
            Assert.Equal((h, m, s, f), (tc.Hours, tc.Minutes, tc.Seconds, tc.Frames));
    }

    [Fact]
    public void Universe_ranges_and_sources_are_grouped_for_display()
    {
        Assert.Equal("1–4, 7, 9–10", ExternalControl.UniverseRanges(new[] { 4, 2, 3, 1, 7, 9, 10, 7 }));
        Assert.Equal("", ExternalControl.UniverseRanges(Array.Empty<int>()));
        var now = new DateTime(2026, 1, 1, 0, 0, 10, DateTimeKind.Utc);
        var id = new DmxSourceId("artnet", "2.0.0.1/0");
        var rows = ExternalControl.GroupSources(new[]
        {
            new DmxSourceInfo(id, 1, 100, now.AddSeconds(-1), 40, "grandMA3"),
            new DmxSourceInfo(id, 2, 100, now, 41, null),
            new DmxSourceInfo(new DmxSourceId("sacn", "2.0.0.2", Guid.NewGuid()), 5, 120, now, 7, "Eos"),
        }, now);
        Assert.Equal(2, rows.Count);
        var art = rows.Single(r => r.Protocol == "Art-Net");
        Assert.Equal("grandMA3", art.Name);
        Assert.Equal("2.0.0.1", art.Address);
        Assert.Equal("1–2", art.Universes);
        Assert.Equal(81, art.Packets);
        Assert.Equal(120, rows.Single(r => r.Protocol == "sACN").Priority);
    }

    [Fact]
    public void Panel_shows_an_empty_state_without_an_engine()
    {
        Loc.SetLanguage("en");
        var tools = StageTestSupport.CreateTools();
        var scene = new Core.SceneGraph.Scene("x");
        var stage = new StagePanelContext(() => null, () => scene, () => new Core.Editing.Selection(), () => new Core.Editing.UndoStack(), tools,
            new AppSettings());
        using var ui = new HeadlessImGui(new Vector2(800, 600));
        DrawPanel(ui, new ExternalControlPanel(stage));
    }

    private static void DrawPanel(HeadlessImGui ui, ExternalControlPanel panel)
    {
        for (int tab = 0; tab < 4; tab++)
        {
            panel.ShowTab = tab;
            for (int i = 0; i < 3; i++)
            {
                ui.Frame(() =>
                {
                    ImGui.SetNextWindowSize(new Vector2(1200, 700));
                    ImGui.Begin("external-host");
                    panel.Draw();
                    ImGui.End();
                });
            }

            panel.SelectedProtocol = tab * 4;
        }
    }
}
