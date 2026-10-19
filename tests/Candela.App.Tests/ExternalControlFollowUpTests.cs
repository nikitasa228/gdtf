using System.Numerics;
using System.Text.RegularExpressions;
using Candela.App.Panels;
using Candela.App.Session;
using Candela.App.Ui;
using Candela.Core.Settings;
using Candela.Dmx.Input;
using Candela.Engine;
using Candela.Protocols;
using Hexa.NET.ImGui;

namespace Candela.App.Tests;

/// <summary>
/// The External Control panel over the protocols' structured status: every status key has a Russian template with the
/// same placeholders, every error code a hint, the details show the bound endpoint, interface and universe count, a
/// real port conflict reads in the UI language, and the recorder tells how it captures (packet rate, format v2).
/// </summary>
[Collection(UiStateCollection.Name)]
public sealed partial class ExternalControlFollowUpTests : IDisposable
{
    public ExternalControlFollowUpTests() => Loc.SetLanguage("en");

    public void Dispose() => Loc.SetLanguage("en");

    [GeneratedRegex(@"\{(\d+)(?:[,:][^}]*)?\}")]
    private static partial Regex Placeholder();

    private static SortedSet<int> Placeholders(string template) =>
        new(Placeholder().Matches(template).Select(m => int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)));

    [Fact]
    public void Every_protocol_message_key_has_a_russian_template_with_the_same_placeholders()
    {
        var russian = ProtocolTexts.RussianTemplates;
        Assert.Equal(ProtocolMessages.All.Keys.Order(StringComparer.Ordinal), russian.Keys.Order(StringComparer.Ordinal));
        foreach (var (key, english) in ProtocolMessages.All)
        {
            string ru = russian[key];
            Assert.True(Placeholders(english).SetEquals(Placeholders(ru)), $"{key}: '{english}' vs '{ru}'");
            Assert.False(string.Equals(english, ru, StringComparison.Ordinal) && key is not (ProtocolMessages.Literal), $"{key} is not translated");
            // Every template formats with as many arguments as its placeholders ask for.
            var args = Enumerable.Range(0, Placeholders(english).DefaultIfEmpty(-1).Max() + 1).Select(i => "a" + i).ToArray();
            Assert.NotNull(ProtocolTexts.Fill(ru, args));
        }
    }

    [Fact]
    public void Every_error_code_has_a_hint_in_both_languages()
    {
        foreach (var code in Enum.GetValues<ProtocolErrorCode>())
        {
            if (code == ProtocolErrorCode.None)
            {
                Assert.Equal("", ProtocolTexts.ErrorHint(code));
                continue;
            }

            var (en, ru) = ProtocolTexts.ErrorHints[code];
            Assert.False(string.IsNullOrWhiteSpace(en), code.ToString());
            Assert.False(string.IsNullOrWhiteSpace(ru), code.ToString());
            Assert.Equal(en, ProtocolTexts.ErrorHint(code));
            Loc.SetLanguage("ru");
            Assert.Equal(ru, ProtocolTexts.ErrorHint(code));
            Loc.SetLanguage("en");
        }
    }

    [Fact]
    public void Texts_are_localised_from_key_and_arguments_and_fall_back_to_english()
    {
        var text = ProtocolText.Create(ProtocolMessages.ArtNetListening, 6454, "eth0", "192.168.1.20", 4, "Candela 100%");
        Assert.Equal("Listening on UDP 6454 (eth0 192.168.1.20); announcing 4 universe(s) as node 'Candela 100%'", ProtocolTexts.Localize(text));
        Loc.SetLanguage("ru");
        Assert.Equal("Приём на UDP 6454 (eth0 192.168.1.20); объявлено юниверсов: 4, узел «Candela 100%»", ProtocolTexts.Localize(text));
        // A literal (no template), an unknown key and a key whose arguments do not fit fall back to the English text.
        Assert.Equal("raw message", ProtocolTexts.Localize(ProtocolText.Literal("raw message")));
        Assert.Equal("English", ProtocolTexts.Localize(new ProtocolText("future.key", new[] { "x" }, "English")));
        Assert.Equal("English", ProtocolTexts.Localize(new ProtocolText(ProtocolMessages.BindFailed, new[] { "x" }, "English")));
        // "Not available" gives the support reason in the UI language when the protocol is known.
        var planned = ProtocolRegistry.All.First(p => !p.CanStart);
        var unavailable = ProtocolText.Create(ProtocolMessages.Unavailable, planned.DisplayName, planned.SupportReason);
        Assert.Equal($"{planned.DisplayName} недоступен: {StageLoc.ProtocolSupportReason(planned)}", ProtocolTexts.Localize(unavailable, planned));
    }

    [Fact]
    public void The_status_details_show_endpoint_interface_universes_and_a_hint_for_an_error()
    {
        var running = new ProtocolStatus(ProtocolIds.ArtNet, ProtocolState.Running, "Listening …", null, Array.Empty<string>(), 10, 0, 0, null,
            "bad header from 10.0.0.5 (100% %n)", Array.Empty<UniverseStatistics>())
        {
            Text = ProtocolText.Create(ProtocolMessages.ArtNetListeningAll, 6454, 4, "Candela"),
            BoundEndpoint = "0.0.0.0:6454",
            NetworkInterface = "eth0 192.168.1.20",
            UniverseCount = 4,
            WarningTexts = new[] { ProtocolText.Create(ProtocolMessages.ArtNetStreamTableFull, 1024) },
        } with
        { Warnings = new[] { "More than 1024 Art-Net streams (sender, port, universe) are active; new ones are ignored." } };
        var artNet = ProtocolRegistry.Find(ProtocolIds.ArtNet);
        var view = ExternalControlPanel.StatusView.Build(running, artNet);
        Assert.Equal("Listening on UDP 6454 (all interfaces); announcing 4 universe(s) as node 'Candela'", view.Message);
        Assert.Equal("Receiving on 0.0.0.0:6454 · Interface: eth0 192.168.1.20 · Universes served: 4", view.Facts);
        Assert.Equal("", view.Error);
        Assert.Equal("", view.Hint);
        Assert.Equal(running.Warnings, view.Warnings);
        Assert.Equal("Last malformed packet: bad header from 10.0.0.5 (100% %n)", view.BadPacket);

        Loc.SetLanguage("ru");
        view = ExternalControlPanel.StatusView.Build(running, artNet);
        Assert.Equal("Приём на 0.0.0.0:6454 · Интерфейс: eth0 192.168.1.20 · Обслуживается юниверсов: 4", view.Facts);
        Assert.Equal("Активно более 1024 потоков Art-Net (отправитель, порт, юниверс); новые игнорируются.", Assert.Single(view.Warnings));

        var failed = ProtocolStatus.NotStarted(ProtocolIds.Osc) with { State = ProtocolState.Error, LastError = "OSC: UDP port 8000 is already in use …" };
        failed = failed with
        {
            ErrorText = ProtocolText.Create(ProtocolMessages.PortInUse, "OSC", 8000),
            ErrorCode = ProtocolErrorCode.PortInUse,
        };
        view = ExternalControlPanel.StatusView.Build(failed, ProtocolRegistry.Find(ProtocolIds.Osc));
        Assert.StartsWith("OSC: UDP-порт 8000 уже занят другой программой", view.Error, StringComparison.Ordinal);
        Assert.Equal("Что делать: " + ProtocolTexts.ErrorHints[ProtocolErrorCode.PortInUse].Ru, view.Hint);
        Assert.Equal("", view.Facts); // stopped: no endpoint, no universes
    }

    [Fact]
    public void A_real_port_conflict_is_reported_in_the_ui_language_with_its_hint()
    {
        using var dir = new TempDir();
        using var engine = StageTestSupport.CreateEngine(dir);
        StageTestSupport.AddFixture(engine, "Spot 1", 1, new Vector3(0, 0, 6));
        engine.Tick(0.02);
        using var tools = StageTestSupport.CreateTools();
        var model = tools.External;
        model.CanSave = false;
        int port = StageTestSupport.FreeUdpPort();
        model.Settings.ArtNetPort = port;
        model.Settings.OscPort = port;
        var app = new AppSettings();
        model.Bind(engine, app);
        Loc.SetLanguage("ru");
        model.Settings.Enabled.Add(ProtocolIds.ArtNet);
        model.SetEnabled(engine, app, ProtocolIds.Osc, true);
        model.Poll(engine);

        var failed = model.Statuses.Where(s => s.ErrorCode == ProtocolErrorCode.PortConflict).ToList();
        Assert.NotEmpty(failed);
        var status = failed[0];
        Assert.Equal(ProtocolMessages.PortConflict, status.ErrorText!.Key);
        // The model's message and the details are Russian; the hint says what to do.
        Assert.True(model.MessageIsError);
        Assert.Contains("настроены на один и тот же UDP-порт", model.Message, StringComparison.Ordinal);
        // A message reported in one language follows a later change of the UI language.
        Loc.SetLanguage("en");
        Assert.Contains("are both set to UDP port", model.Message, StringComparison.Ordinal);
        Assert.Same(model.Message, model.Message); // built once per language, not per frame
        Loc.SetLanguage("ru");
        var view = ExternalControlPanel.StatusView.Build(status, ProtocolRegistry.Find(status.ProtocolId));
        Assert.Contains($"UDP-порт {port}", view.Error, StringComparison.Ordinal);
        Assert.Contains(ProtocolTexts.ErrorHints[ProtocolErrorCode.PortConflict].Ru, view.Hint, StringComparison.Ordinal);

        // The panel draws the error details of the failed protocol (and every other tab) without trouble.
        using var ui = new HeadlessImGui(new Vector2(1200, 800));
        var panel = new ExternalControlPanel(StagePanelContext.ForEngine(engine, tools, app))
        {
            SelectedProtocol = ProtocolRegistry.All.ToList().FindIndex(p => p.Id == status.ProtocolId),
            ShowTab = 0,
        };
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

        model.Settings.Enabled.Clear();
        model.ApplySettings(engine, app);
    }

    [Fact]
    public void The_recorder_says_how_a_recording_was_captured()
    {
        using var dir = new TempDir();
        using var engine = StageTestSupport.CreateEngine(dir);
        engine.Recorder.Start(engine.Time, "Take 1");
        engine.Input.Submit(new DmxSourceId("test", "1"), 1, new byte[] { 255, 128 });
        engine.Tick(0.05);
        var recorded = engine.Recorder.Stop();
        Assert.NotNull(recorded);
        Assert.Equal(DmxCaptureMode.Packets, recorded!.Capture);
        Assert.Equal("Captured every change at packet rate · format v2", ExternalControl.DescribeCapture(recorded));

        var handMade = new DmxRecording("Old", new[] { new DmxRecordedFrame(0, null, Array.Empty<DmxUniverseChange>()) });
        Assert.Equal(DmxCaptureMode.EngineFrames, handMade.Capture);
        Loc.SetLanguage("ru");
        Assert.Equal("Записано: раз в кадр движка · формат v2", ExternalControl.DescribeCapture(handMade));
        Assert.Equal(2, DmxRecording.FormatVersion);
    }
}
