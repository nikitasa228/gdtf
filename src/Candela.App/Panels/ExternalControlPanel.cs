using System.Globalization;
using System.Numerics;
using System.Text;
using Candela.App.Docking;
using Candela.App.Files;
using Candela.App.Session;
using Candela.App.Shell;
using Candela.App.Ui;
using Candela.Dmx.Input;
using Candela.Engine;
using Candela.Protocols;
using Candela.Protocols.ArtNet;
using Candela.Protocols.Networking;
using Candela.Protocols.Osc;
using Candela.Protocols.Psn;
using Hexa.NET.ImGui;

namespace Candela.App.Panels;

/// <summary>
/// The external-control panel (requirement "Окно внешней связи с пультом"): every one of the 15 protocols with its
/// honest support level, enable switches and per-protocol settings (network interface, Art-Net universe offset, ports,
/// EntTec device), live status (state, message, packets per second, errors and warnings), the senders on the network,
/// a universe activity grid (click a universe to see its 512 slot values and which fixtures use them), the last
/// timecode received (ArtTimeCode), the merge mode (HTP/LTP), a button that sends the Art-Net test show to this
/// computer (verify reception without a console) and the DMX recorder (record, stop, play, seek by time or timecode,
/// save, load).
/// </summary>
internal sealed unsafe class ExternalControlPanel : Panel
{
    private static readonly string[] Numbers = BuildNumbers(513);
    private readonly StagePanelContext _stage;
    private readonly byte[] _target = new byte[64];
    private readonly byte[] _seek = new byte[32];
    private readonly byte[] _enttecPort = new byte[128];
    private int _selected;
    private IReadOnlyList<ProtocolStatus>? _rowsOf;
    private string[] _rowStatus = Array.Empty<string>();
    private string[] _rowRate = Array.Empty<string>();
    private IReadOnlyList<NetworkInterfaceInfo>? _interfaces;
    private IReadOnlyList<string>? _serialPorts;
    private string _timecodeText = "--:--:--:--";
    private string _timecodeDetail = "";
    private DateTime _timecodeShown;
    private double _lastTimecodeRefresh = double.NegativeInfinity;
    private string _language = "";
    private string _slotTooltipCache = "";
    private ProtocolStatus? _countersOf;
    private int _portEdit;
    private string _countersText = "";
    private string _countersLanguage = "";
    private int _slotTooltipKey = -1;

    /// <summary>Creates the panel for the editor.</summary>
    public ExternalControlPanel(WorkspaceContext context)
        : this(StagePanelContext.From(context ?? throw new ArgumentNullException(nameof(context))))
    {
    }

    /// <summary>Creates the panel around a stage context (tests).</summary>
    internal ExternalControlPanel(StagePanelContext stage)
        : base("ExternalControl", StageIcons.Radio)
    {
        _stage = stage ?? throw new ArgumentNullException(nameof(stage));
    }

    /// <inheritdoc/>
    public override string Title => StageLoc.Get(StageStr.PanelExternalControl);

    /// <inheritdoc/>
    public override Vector2 DefaultSize => new(760, 420);

    /// <summary>Index of the protocol whose details are shown (into <see cref="ProtocolRegistry.All"/>).</summary>
    internal int SelectedProtocol
    {
        get => _selected;
        set => _selected = Math.Clamp(value, 0, ProtocolRegistry.All.Count - 1);
    }

    /// <summary>A tab to bring to the front at the next draw (0 protocols, 1 universes, 2 sources, 3 recorder; tests and scripts).</summary>
    internal int? ShowTab { get; set; }

    /// <inheritdoc/>
    public override void Draw()
    {
        var engine = _stage.Engine;
        if (engine is null)
        {
            PropertiesPanel.EmptyState(StageIcons.Radio, StageLoc.Get(StageStr.ExtNoEngine));
            return;
        }

        var model = _stage.Tools.External;
        HeaderBar(engine, model);
        if (model.Message is { } message)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, model.MessageIsError ? Theme.Error : Theme.Success);
            ImGui.PushTextWrapPos(0f);
            ImGui.TextUnformatted(message);
            ImGui.PopTextWrapPos();
            ImGui.PopStyleColor();
            if (ImGui.IsItemClicked())
                model.ClearMessage();
        }

        if (!ImGui.BeginTabBar("##external-tabs"))
            return;
        int? show = ShowTab;
        ShowTab = null;
        if (ImGui.BeginTabItem(StageLoc.With(Icons.Network, StageStr.ExtTabProtocols), TabFlags(show, 0)))
        {
            Protocols(engine, model);
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem(StageLoc.With(Icons.LayoutGrid, StageStr.ExtTabUniverses), TabFlags(show, 1)))
        {
            Universes(engine, model);
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem(StageLoc.With(StageIcons.Router, StageStr.ExtTabSources), TabFlags(show, 2)))
        {
            Sources(engine, model);
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem(StageLoc.With(StageIcons.Timer, StageStr.ExtTabRecorder), TabFlags(show, 3)))
        {
            Recorder(engine, model);
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

    private static ImGuiTabItemFlags TabFlags(int? show, int tab) => show == tab ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;

    private void HeaderBar(StageEngine engine, ExternalControl model)
    {
        RefreshTimecode(model);
        var style = ImGui.GetStyle();
        float right = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X;
        ImGui.PushFont(ImGui.GetFont(), ImGui.GetStyle().FontSizeBase * 1.35f);
        ImGui.TextColored(model.LastTimecode is null ? Theme.TextMuted : Theme.Accent, _timecodeText);
        ImGui.PopFont();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(StageLoc.Get(StageStr.ExtTimecodeHint));
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled(_timecodeDetail);

        // Right side: merge mode and the test pattern.
        float testWidth = ImGui.CalcTextSize(StageLoc.Get(StageStr.ExtTestPatternStop)).X + ImGui.GetFrameHeight() + 2f * style.FramePadding.X;
        float mergeWidth = 150f * style.FontScaleMain;
        float x = right - testWidth - mergeWidth - style.ItemSpacing.X;
        if (x > ImGui.GetCursorPosX() + 20f)
            ImGui.SameLine(x);
        ImGui.SetNextItemWidth(mergeWidth);
        int merge = (int)engine.Input.MergeMode;
        string mergeLabel = merge == (int)DmxMergeMode.Htp ? StageLoc.Get(StageStr.ExtMergeHtp) : StageLoc.Get(StageStr.ExtMergeLtp);
        if (ImGui.BeginCombo("##merge", mergeLabel))
        {
            if (ImGui.Selectable(StageLoc.Get(StageStr.ExtMergeHtp), merge == 0))
                model.SetMergeMode(engine, DmxMergeMode.Htp);
            if (ImGui.Selectable(StageLoc.Get(StageStr.ExtMergeLtp), merge == 1))
                model.SetMergeMode(engine, DmxMergeMode.Ltp);
            ImGui.EndCombo();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(StageLoc.Get(StageStr.ExtMergeHint));
        ImGui.SameLine();
        bool running = model.TestPatternRunning;
        if (running)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, Theme.WithAlpha(Theme.Accent, 0.75f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Theme.Accent);
        }

        if (ImGui.Button(running ? StageLoc.With(StageIcons.Stop, StageStr.ExtTestPatternStop) : StageLoc.With(StageIcons.Send, StageStr.ExtTestPattern),
                new Vector2(testWidth, 0f)))
        {
            if (running)
                model.StopTestPattern();
            else
                model.StartTestPattern(engine, _stage.Settings);
        }

        if (running)
            ImGui.PopStyleColor(2);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(StageLoc.Format(StageStr.ExtTestPatternHint, model.Settings.TestTarget, model.TestFramesSent));
    }

    private void RefreshTimecode(ExternalControl model)
    {
        double now = ImGui.GetTime();
        bool languageChanged = !string.Equals(_language, Loc.Language, StringComparison.Ordinal);
        if (!languageChanged && now - _lastTimecodeRefresh < 0.04)
            return;
        _lastTimecodeRefresh = now;
        _language = Loc.Language;
        if (model.LastTimecode is not { } tc)
        {
            _timecodeText = "--:--:--:--";
            _timecodeDetail = StageLoc.Get(StageStr.ExtNoTimecode);
            return;
        }

        if (tc.ReceivedUtc == _timecodeShown && !languageChanged && (DateTime.UtcNow - tc.ReceivedUtc).TotalSeconds < 1.0)
            return;
        _timecodeShown = tc.ReceivedUtc;
        _timecodeText = tc.Timecode.ToString();
        double ago = Math.Max(0, (DateTime.UtcNow - tc.ReceivedUtc).TotalSeconds);
        string rate = tc.Timecode.Type switch
        {
            TimecodeType.Film24 => "24 fps",
            TimecodeType.Ebu25 => "25 fps EBU",
            TimecodeType.Df2997 => "29.97 fps DF",
            _ => "30 fps SMPTE",
        };
        _timecodeDetail = ago < 1.0 ? StageLoc.Format(StageStr.ExtTimecodeLive, rate) : StageLoc.Format(StageStr.ExtTimecodeStale, rate, ago);
    }

    private void Protocols(StageEngine engine, ExternalControl model)
    {
        RefreshRows(model);
        var avail = ImGui.GetContentRegionAvail();
        bool sideBySide = avail.X > 640f * ImGui.GetStyle().FontScaleMain;
        float listWidth = sideBySide ? avail.X * 0.48f : avail.X;
        float listHeight = sideBySide ? 0f : MathF.Max(avail.Y * 0.5f, 120f);
        if (ImGui.BeginChild("##protocol-list", new Vector2(listWidth, listHeight)))
        {
            const ImGuiTableFlags flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp |
                                          ImGuiTableFlags.ScrollY;
            if (ImGui.BeginTable("##protocols", 4, flags))
            {
                ImGui.TableSetupScrollFreeze(0, 1);
                ImGui.TableSetupColumn(StageLoc.Get(StageStr.ExtColumnOn), ImGuiTableColumnFlags.WidthFixed);
                ImGui.TableSetupColumn(StageLoc.Get(StageStr.ExtColumnProtocol), ImGuiTableColumnFlags.WidthStretch, 1.2f);
                ImGui.TableSetupColumn(StageLoc.Get(StageStr.ExtColumnSupport), ImGuiTableColumnFlags.WidthStretch, 1f);
                ImGui.TableSetupColumn(StageLoc.Get(StageStr.ExtColumnStatus), ImGuiTableColumnFlags.WidthStretch, 1.1f);
                ImGui.TableHeadersRow();
                var all = ProtocolRegistry.All;
                for (int i = 0; i < all.Count; i++)
                {
                    var d = all[i];
                    var status = i < model.Statuses.Count ? model.Statuses[i] : null;
                    ImGui.TableNextRow();
                    ImGui.PushID(i);
                    ImGui.TableNextColumn();
                    bool enabled = model.IsEnabled(d.Id);
                    ImGui.BeginDisabled(!d.CanStart);
                    if (ImGui.Checkbox("##on", ref enabled))
                        model.SetEnabled(engine, _stage.Settings, d.Id, enabled);
                    ImGui.EndDisabled();
                    if (!d.CanStart && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                        ImGui.SetTooltip(d.SupportReason);
                    ImGui.TableNextColumn();
                    StateDot(status?.State ?? ProtocolState.Stopped, d.CanStart);
                    ImGui.SameLine();
                    if (ImGui.Selectable(d.DisplayName, _selected == i, ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowOverlap))
                        _selected = i;
                    ImGui.TableNextColumn();
                    SupportBadge(d.SupportLevel);
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(d.SupportReason);
                    ImGui.TableNextColumn();
                    if (i < _rowStatus.Length)
                    {
                        ImGui.TextUnformatted(_rowStatus[i]);
                        if (_rowRate[i].Length > 0)
                        {
                            ImGui.SameLine();
                            ImGui.TextDisabled(_rowRate[i]);
                        }
                    }

                    ImGui.PopID();
                }

                ImGui.EndTable();
            }
        }

        ImGui.EndChild();
        if (sideBySide)
            ImGui.SameLine();
        if (ImGui.BeginChild("##protocol-details", Vector2.Zero, ImGuiChildFlags.AlwaysUseWindowPadding))
            Details(engine, model, ProtocolRegistry.All[Math.Clamp(_selected, 0, ProtocolRegistry.All.Count - 1)]);
        ImGui.EndChild();
    }

    private void RefreshRows(ExternalControl model)
    {
        var statuses = model.Statuses;
        if (ReferenceEquals(statuses, _rowsOf) && string.Equals(_language, Loc.Language, StringComparison.Ordinal))
            return;
        _rowsOf = statuses;
        _rowStatus = new string[statuses.Count];
        _rowRate = new string[statuses.Count];
        for (int i = 0; i < statuses.Count; i++)
        {
            var s = statuses[i];
            _rowStatus[i] = StateText(s.State);
            double rate = 0;
            foreach (var u in s.Universes)
                rate += u.PacketsPerSecond;
            _rowRate[i] = s.State is ProtocolState.Running or ProtocolState.Warning
                ? string.Create(CultureInfo.InvariantCulture, $"{rate:0} {StageLoc.Get(StageStr.ExtPacketsPerSecond)} · U{s.Universes.Count}")
                : "";
        }
    }

    private void Details(StageEngine engine, ExternalControl model, ProtocolDescriptor d)
    {
        ImGui.PushFont(ImGui.GetFont(), ImGui.GetStyle().FontSizeBase * 1.1f);
        ImGui.TextUnformatted(d.DisplayName);
        ImGui.PopFont();
        ImGui.SameLine();
        SupportBadge(d.SupportLevel);
        ImGui.PushTextWrapPos(0f);
        ImGui.TextDisabled(StageLoc.ProtocolDescription(d));
        ImGui.PopTextWrapPos();
        if (d.SupportLevel is not ProtocolSupportLevel.Implemented)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, d.CanStart ? Theme.Note : Theme.Warning);
            ImGui.PushTextWrapPos(0f);
            ImGui.TextUnformatted(StageLoc.ProtocolSupportReason(d));
            ImGui.PopTextWrapPos();
            ImGui.PopStyleColor();
        }

        int index = IndexOf(d.Id);
        var status = index >= 0 && index < model.Statuses.Count ? model.Statuses[index] : null;
        if (status is not null && status.State != ProtocolState.Stopped)
        {
            ImGui.SeparatorText(StageLoc.Get(StageStr.ExtStatus));
            StateDot(status.State, d.CanStart);
            ImGui.SameLine();
            ImGui.PushTextWrapPos(0f);
            ImGui.TextUnformatted(status.Message);
            ImGui.PopTextWrapPos();
            ImGui.TextDisabled(CountersText(status));
            if (status.LastError is { } error)
                ImGui.TextColored(Theme.Error, error);
            foreach (var warning in status.Warnings)
                ImGui.TextColored(Theme.Warning, warning);
            if (status.LastBadPacket is { } bad)
                ImGui.TextDisabled(bad);
        }

        if (!d.CanStart)
            return;
        ImGui.SeparatorText(StageLoc.Get(StageStr.ExtSettings));
        if (!PropertyGrid.Begin("##protocol-settings", 130f))
            return;
        bool changed = false;
        var settings = model.Settings;
        switch (d.Id)
        {
            case ProtocolIds.ArtNet:
                changed |= InterfaceRow();
                changed |= OffsetRow();
                if (PortRow(StageStr.ExtPort, settings.ArtNetPort, out int artNetPort))
                {
                    settings.ArtNetPort = artNetPort;
                    changed = true;
                }

                if (CheckRow(StageStr.ExtArtSync, settings.ArtNetSync, out bool sync))
                {
                    settings.ArtNetSync = sync;
                    changed = true;
                }

                if (CheckRow(StageStr.ExtBroadcastReply, settings.ArtNetBroadcastPollReply, out bool broadcast))
                {
                    settings.ArtNetBroadcastPollReply = broadcast;
                    changed = true;
                }

                if (CheckRow(StageStr.ExtAcceptUnsubscribed, settings.AcceptUnsubscribedUniverses, out bool acceptArtNet))
                {
                    settings.AcceptUnsubscribedUniverses = acceptArtNet;
                    changed = true;
                }

                ArtNetNodes(engine);
                break;
            case ProtocolIds.Sacn:
                changed |= InterfaceRow();
                if (PortRow(StageStr.ExtPort, settings.SacnPort, out int sacnPort))
                {
                    settings.SacnPort = sacnPort;
                    changed = true;
                }

                if (CheckRow(StageStr.ExtJoinMulticast, settings.SacnJoinMulticast, out bool multicast))
                {
                    settings.SacnJoinMulticast = multicast;
                    changed = true;
                }

                if (CheckRow(StageStr.ExtAcceptPreview, settings.SacnAcceptPreviewData, out bool preview))
                {
                    settings.SacnAcceptPreviewData = preview;
                    changed = true;
                }

                if (CheckRow(StageStr.ExtAcceptUnsubscribed, settings.AcceptUnsubscribedUniverses, out bool acceptSacn))
                {
                    settings.AcceptUnsubscribedUniverses = acceptSacn;
                    changed = true;
                }

                break;
            case ProtocolIds.Osc:
                changed |= InterfaceRow();
                if (PortRow(StageStr.ExtPort, settings.OscPort, out int oscPort))
                {
                    settings.OscPort = oscPort;
                    changed = true;
                }

                PropertyGrid.Row(StageLoc.Get(StageStr.ExtOscPatterns));
                ImGui.TextDisabled(string.Join("  ", ProtocolOptions.DefaultOscPatterns));
                break;
            case ProtocolIds.EtcEos:
                changed |= InterfaceRow();
                if (PortRow(StageStr.ExtPort, settings.EosOscPort, out int eosPort))
                {
                    settings.EosOscPort = eosPort;
                    changed = true;
                }

                EosState(engine);
                break;
            case ProtocolIds.PosiStageNet:
                changed |= InterfaceRow();
                if (PortRow(StageStr.ExtPort, settings.PsnPort, out int psnPort))
                {
                    settings.PsnPort = psnPort;
                    changed = true;
                }

                PropertyGrid.Row(StageLoc.Get(StageStr.ExtTrackers));
                ImGui.TextUnformatted((engine.Protocols.Get<PsnReceiver>()?.Trackers.Count ?? 0).ToString(CultureInfo.InvariantCulture));
                break;
            case ProtocolIds.EnttecDmxUsbPro:
                changed |= SerialPortRow(settings);
                PropertyGrid.Row(StageLoc.Get(StageStr.ExtUniverse));
                int universe = settings.EnttecUniverse;
                if (ImGui.InputInt("##enttec-universe", ref universe) && universe is >= 1 and <= 63999)
                {
                    settings.EnttecUniverse = universe;
                    changed = true;
                }

                break;
        }

        PropertyGrid.End();
        if (changed)
            model.ApplySettings(engine, _stage.Settings);
    }

    private string CountersText(ProtocolStatus status)
    {
        if (ReferenceEquals(status, _countersOf) && string.Equals(_countersLanguage, Loc.Language, StringComparison.Ordinal))
            return _countersText;
        _countersOf = status;
        _countersLanguage = Loc.Language;
        return _countersText = string.Create(CultureInfo.InvariantCulture,
            $"{StageLoc.Get(StageStr.ExtPackets)}: {status.Packets} · {StageLoc.Get(StageStr.ExtBadPackets)}: {status.BadPackets} · {StageLoc.Get(StageStr.ExtIgnoredPackets)}: {status.IgnoredPackets}");
    }

    private bool InterfaceRow()
    {
        PropertyGrid.Row(StageLoc.Get(StageStr.ExtInterface));
        string current = _stage.Settings.NetworkInterface ?? "";
        string preview = current.Length == 0 ? StageLoc.Get(StageStr.ExtAllInterfaces) : current;
        bool changed = false;
        if (ImGui.BeginCombo("##interface", preview))
        {
            if (ImGui.IsWindowAppearing())
                _interfaces = ProtocolManager.GetNetworkInterfaces();
            if (ImGui.Selectable(StageLoc.Get(StageStr.ExtAllInterfaces), current.Length == 0) && current.Length != 0)
            {
                _stage.Settings.NetworkInterface = "";
                changed = true;
            }

            foreach (var nic in _interfaces ?? Array.Empty<NetworkInterfaceInfo>())
            {
                string address = nic.Address.ToString();
                string label = $"{nic.Name} — {address}{(nic.IsUp ? "" : " (down)")}";
                if (ImGui.Selectable(label, current == address || current == nic.Name) && current != address)
                {
                    _stage.Settings.NetworkInterface = address;
                    changed = true;
                }
            }

            ImGui.EndCombo();
        }

        if (changed)
            _stage.SettingsChanged?.Invoke();
        return changed;
    }

    private bool OffsetRow()
    {
        PropertyGrid.Row(StageLoc.Get(StageStr.ExtOffset));
        int offset = _stage.Settings.ArtNetUniverseOffset == 0 ? 0 : 1;
        bool changed = false;
        if (ImGui.BeginCombo("##offset", Loc.Get(offset == 0 ? Str.SettingsArtNetOffset0 : Str.SettingsArtNetOffset1)))
        {
            if (ImGui.Selectable(Loc.Get(Str.SettingsArtNetOffset1), offset == 1) && offset != 1)
            {
                _stage.Settings.ArtNetUniverseOffset = 1;
                changed = true;
            }

            if (ImGui.Selectable(Loc.Get(Str.SettingsArtNetOffset0), offset == 0) && offset != 0)
            {
                _stage.Settings.ArtNetUniverseOffset = 0;
                changed = true;
            }

            ImGui.EndCombo();
        }

        if (changed)
            _stage.SettingsChanged?.Invoke();
        return changed;
    }

    /// <summary>A UDP port field; true once the user finished typing a valid new port (applied on leaving the field).</summary>
    private bool PortRow(StageStr label, int port, out int value)
    {
        PropertyGrid.Row(StageLoc.Get(label));
        int shown = port;
        if (ImGui.InputInt("##port", ref shown, 0, 0))
            _portEdit = shown;
        if (ImGui.IsItemActivated())
            _portEdit = port;
        bool done = ImGui.IsItemDeactivatedAfterEdit();
        value = _portEdit;
        return done && value is > 0 and <= 65535 && value != port;
    }

    private static bool CheckRow(StageStr label, bool current, out bool value)
    {
        PropertyGrid.Row(StageLoc.Get(label));
        value = current;
        ImGui.PushID((int)label);
        bool changed = ImGui.Checkbox("##check", ref value);
        ImGui.PopID();
        return changed;
    }

    private bool SerialPortRow(ExternalControlSettings settings)
    {
        PropertyGrid.Row(StageLoc.Get(StageStr.ExtSerialPort));
        bool changed = false;
        string preview = settings.EnttecSerialPort.Length == 0 ? StageLoc.Get(StageStr.ExtNoSerialPort) : settings.EnttecSerialPort;
        if (ImGui.BeginCombo("##serial", preview))
        {
            if (ImGui.IsWindowAppearing())
                _serialPorts = ProtocolManager.GetSerialPorts();
            if (_serialPorts is not { Count: > 0 })
                ImGui.TextDisabled(StageLoc.Get(StageStr.ExtNoSerialPorts));
            foreach (string port in _serialPorts ?? Array.Empty<string>())
            {
                if (ImGui.Selectable(port, port == settings.EnttecSerialPort) && port != settings.EnttecSerialPort)
                {
                    settings.EnttecSerialPort = port;
                    changed = true;
                }
            }

            ImGui.EndCombo();
        }

        return changed;
    }

    private static void ArtNetNodes(StageEngine engine)
    {
        var receiver = engine.Protocols.Get<ArtNetReceiver>();
        if (receiver is null || !receiver.IsRunning)
            return;
        var nodes = receiver.Nodes;
        PropertyGrid.Row(StageLoc.Get(StageStr.ExtNodes));
        if (nodes.Count == 0)
        {
            ImGui.TextDisabled("—");
            return;
        }

        foreach (var node in nodes)
            ImGui.TextUnformatted($"{node.Reply.ShortName} ({node.Sender})");
    }

    private static void EosState(StageEngine engine)
    {
        var eos = engine.Protocols.Get<EtcEosReceiver>();
        if (eos is null || !eos.IsRunning)
            return;
        var state = eos.State;
        PropertyGrid.Row(StageLoc.Get(StageStr.ExtEosShow));
        ImGui.TextUnformatted(state.ShowName ?? "—");
        PropertyGrid.Row(StageLoc.Get(StageStr.ExtEosActiveCue));
        ImGui.TextUnformatted(state.ActiveCueText ?? "—");
        PropertyGrid.Row(StageLoc.Get(StageStr.ExtEosPendingCue));
        ImGui.TextUnformatted(state.PendingCueText ?? "—");
    }

    private void Universes(StageEngine engine, ExternalControl model)
    {
        var style = ImGui.GetStyle();
        float cell = MathF.Max(ImGui.GetFrameHeight() * 1.35f, ImGui.CalcTextSize("512").X + 2f * style.FramePadding.X);
        int columns = Math.Clamp((int)((ImGui.GetContentRegionAvail().X + style.ItemSpacing.X) / (cell + 2f)), 4, 32);
        int count = model.GridUniverses;
        var dl = ImGui.GetWindowDrawList();
        ImGui.TextDisabled(StageLoc.Get(StageStr.ExtUniversesHint));
        for (int u = 1; u <= count; u++)
        {
            if ((u - 1) % columns != 0)
                ImGui.SameLine(0f, 2f);
            var p = ImGui.GetCursorScreenPos();
            bool active = model.IsActive(u), patched = model.IsPatched(u), selected = model.SelectedUniverse == u;
            ImGui.PushID(u);
            if (ImGui.InvisibleButton("##u", new Vector2(cell, cell * 0.8f)))
                model.SelectedUniverse = selected ? 0 : u;
            bool hovered = ImGui.IsItemHovered();
            ImGui.PopID();
            double rate = model.RateOf(u);
            var fill = active ? Vector4.Lerp(Theme.WithAlpha(Theme.Success, 0.45f), Theme.Success, (float)Math.Clamp(rate / 44.0, 0, 1))
                : patched ? Theme.Rgb(0x33363E) : Theme.Rgb(0x26292F);
            dl.AddRectFilled(p, p + new Vector2(cell, cell * 0.8f), Theme.U32(fill), 3f);
            dl.AddRect(p, p + new Vector2(cell, cell * 0.8f), Theme.U32(patched ? Theme.WithAlpha(Theme.Accent, active ? 0.9f : 0.6f) : Theme.Rgb(0x34373F)), 3f);
            if (selected || hovered)
                dl.AddRect(p - new Vector2(1f), p + new Vector2(cell, cell * 0.8f) + new Vector2(1f), Theme.U32(selected ? Theme.Accent : Theme.Text), 3f, ImDrawFlags.None, 2f);
            string number = Numbers[u];
            var size = ImGui.CalcTextSize(number);
            dl.AddText(p + (new Vector2(cell, cell * 0.8f) - size) * 0.5f, Theme.U32(active ? new Vector4(0f, 0f, 0f, 0.9f) : Theme.TextMuted), number);
            if (hovered)
                ImGui.SetTooltip(StageLoc.Format(StageStr.ExtUniverseTooltip, u, rate, active ? StageLoc.Get(StageStr.ExtLive) : "—",
                    patched ? StageLoc.Get(StageStr.ExtPatched) : StageLoc.Get(StageStr.ExtNotPatched)));
        }

        if (model.SelectedUniverse > 0)
            Slots(engine, model);
    }

    private void Slots(StageEngine engine, ExternalControl model)
    {
        int universe = model.SelectedUniverse;
        var slots = model.ReadSelectedUniverse(engine, out bool live);
        ImGui.Spacing();
        ImGui.TextUnformatted(StageLoc.Format(StageStr.ExtSlotsTitle, universe));
        ImGui.SameLine();
        ImGui.TextDisabled(live ? StageLoc.Get(StageStr.ExtLive) : StageLoc.Get(StageStr.ExtNoData));
        var style = ImGui.GetStyle();
        float cell = MathF.Max(ImGui.CalcTextSize("255").X + 6f, ImGui.GetTextLineHeight() * 1.6f);
        int columns = Math.Clamp((int)(ImGui.GetContentRegionAvail().X / cell), 8, 32);
        columns = columns >= 32 ? 32 : columns >= 16 ? 16 : 8;
        float rowHeight = ImGui.GetTextLineHeight() + 4f;
        if (!ImGui.BeginChild("##slots", new Vector2(0f, MathF.Min(ImGui.GetContentRegionAvail().Y, (512 / columns + 1) * rowHeight)), ImGuiChildFlags.Borders))
        {
            ImGui.EndChild();
            return;
        }

        var dl = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        var mouse = ImGui.GetIO().MousePos;
        int hoveredSlot = -1;
        for (int i = 0; i < 512; i++)
        {
            int row = i / columns, col = i % columns;
            var p = origin + new Vector2(col * cell, row * rowHeight);
            float v = slots[i] / 255f;
            dl.AddRectFilled(p, p + new Vector2(cell - 2f, rowHeight - 2f), Theme.U32(new Vector4(0.9f * v + 0.08f, 0.55f * v + 0.08f, 0.1f * v + 0.09f, 1f)), 2f);
            string text = Numbers[slots[i]];
            var size = ImGui.CalcTextSize(text);
            dl.AddText(p + new Vector2((cell - 2f - size.X) * 0.5f, 1f), Theme.U32(v > 0.55f ? new Vector4(0f, 0f, 0f, 1f) : Theme.Text), text);
            if (mouse.X >= p.X && mouse.X < p.X + cell && mouse.Y >= p.Y && mouse.Y < p.Y + rowHeight)
                hoveredSlot = i;
        }

        ImGui.Dummy(new Vector2(columns * cell, (511 / columns + 1) * rowHeight));
        if (hoveredSlot >= 0 && ImGui.IsWindowHovered())
            ImGui.SetTooltip(SlotTooltip(model, universe, hoveredSlot + 1, slots[hoveredSlot]));
        ImGui.EndChild();
        _ = style;
    }

    private string SlotTooltip(ExternalControl model, int universe, int slot, byte value)
    {
        int key = (universe << 20) | (slot << 8) | value;
        if (key == _slotTooltipKey)
            return _slotTooltipCache;
        _slotTooltipKey = key;
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"{universe}.{slot:D3} = {value} ({value * 100 / 255}%)");
        if (model.Patch is { } patch)
        {
            foreach (var entry in patch.At(universe, slot))
                sb.Append('\n').Append(entry.Label);
        }

        return _slotTooltipCache = sb.ToString();
    }

    private static void Sources(StageEngine engine, ExternalControl model)
    {
        var sources = model.Sources;
        if (sources.Count == 0)
        {
            ImGui.TextDisabled(StageLoc.Get(StageStr.ExtNoSources));
            return;
        }

        const ImGuiTableFlags flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.ScrollY;
        if (!ImGui.BeginTable("##sources", 6, flags))
            return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn(StageLoc.Get(StageStr.ExtColumnProtocol));
        ImGui.TableSetupColumn(StageLoc.Get(StageStr.ExtColumnName));
        ImGui.TableSetupColumn(StageLoc.Get(StageStr.ExtColumnAddress));
        ImGui.TableSetupColumn(StageLoc.Get(StageStr.ExtColumnUniverses));
        ImGui.TableSetupColumn(StageLoc.Get(StageStr.ExtColumnPriority));
        ImGui.TableSetupColumn(StageLoc.Get(StageStr.ExtColumnPackets));
        ImGui.TableHeadersRow();
        foreach (var s in sources)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(s.Protocol);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(s.Name.Length > 0 ? s.Name : "—");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(s.Address);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(s.Universes);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(Numbers[Math.Clamp(s.Priority, 0, 512)]);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(s.Packets.ToString(CultureInfo.InvariantCulture));
        }

        ImGui.EndTable();
        _ = engine;
    }

    private void Recorder(StageEngine engine, ExternalControl model)
    {
        var recorder = engine.Recorder;
        var style = ImGui.GetStyle();
        if (recorder.IsRecording)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, Theme.WithAlpha(Theme.Error, 0.8f));
            if (ImGui.Button(StageLoc.With(StageIcons.Stop, StageStr.ExtRecordStop)))
                model.StopRecording(engine);
            ImGui.PopStyleColor();
            ImGui.SameLine();
            ImGui.TextColored(Theme.Error, string.Create(CultureInfo.InvariantCulture,
                $"● {StageLoc.Get(StageStr.ExtRecordingNow)} {recorder.Duration:0.0} s · {recorder.FrameCount}"));
        }
        else
        {
            if (ImGui.Button(StageLoc.With(StageIcons.Record, StageStr.ExtRecord)))
                model.StartRecording(engine);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(StageLoc.Get(StageStr.ExtRecordHint));
        }

        ImGui.SameLine(0f, style.ItemSpacing.X * 2f);
        ImGui.BeginDisabled(_stage.Tools.Browser is null || recorder.IsRecording);
        if (ImGui.Button(StageLoc.With(Icons.FolderOpen, StageStr.ExtLoadRecording)) && _stage.Tools.Browser is { } browser)
        {
            browser.Show(FileBrowserMode.Open, StageLoc.Get(StageStr.ExtLoadRecording), RecordingFilters(), null, null,
                path => model.LoadRecording(path));
        }

        ImGui.SameLine();
        ImGui.BeginDisabled(model.Recording is null);
        if (ImGui.Button(StageLoc.With(Icons.Save, StageStr.ExtSaveRecording)) && _stage.Tools.Browser is { } saver)
        {
            saver.Show(FileBrowserMode.Save, StageLoc.Get(StageStr.ExtSaveRecording), RecordingFilters(), null,
                (model.Recording?.Name ?? "recording") + Engine.DmxRecording.Extension, path => model.SaveRecording(path));
        }

        ImGui.EndDisabled();
        ImGui.EndDisabled();

        var recording = model.Recording;
        if (recording is null)
        {
            ImGui.Spacing();
            ImGui.PushTextWrapPos(0f);
            ImGui.TextDisabled(StageLoc.Get(StageStr.ExtNoRecording));
            ImGui.PopTextWrapPos();
            return;
        }

        ImGui.SeparatorText(recording.Name);
        var player = engine.Player;
        bool playing = player is not null && ReferenceEquals(player.Recording, recording) && !player.IsAtEnd;
        if (ImGui.Button(playing ? StageLoc.With(StageIcons.Stop, StageStr.ExtStopPlayback) : StageLoc.With(StageIcons.Play, StageStr.ExtPlay)))
        {
            if (playing)
                ExternalControl.StopPlayback(engine);
            else
                model.Play(engine, player is not null && ReferenceEquals(player.Recording, recording) && !player.IsAtEnd ? player.Position : 0);
        }

        ImGui.SameLine();
        if (ImGui.Button(StageIcons.SkipBack + "##rewind"))
            model.Seek(engine, 0);
        ImGui.SameLine();
        float position = (float)(player is not null && ReferenceEquals(player.Recording, recording) ? player.Position : 0);
        ImGui.SetNextItemWidth(MathF.Max(80f, ImGui.GetContentRegionAvail().X * 0.5f));
        if (ImGui.SliderFloat("##position", ref position, 0f, (float)Math.Max(recording.Duration, 0.001), "%.2f s"))
            model.Seek(engine, position);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(110f * style.FontScaleMain);
        bool go;
        fixed (byte* seek = _seek)
            go = ImGui.InputTextWithHint("##seek-tc", "00:00:00:00", seek, (nuint)_seek.Length, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        go |= ImGui.Button(StageLoc.Get(StageStr.ExtSeekTimecode));
        if (go)
        {
            var type = recording.FirstTimecode?.Type ?? TimecodeType.Ebu25;
            if (ExternalControl.TryParseTimecode(Text(_seek), type, out var tc))
                model.SeekTimecode(engine, tc);
        }

        ImGui.TextDisabled(string.Create(CultureInfo.InvariantCulture,
            $"{recording.Frames.Count} · {recording.Duration:0.0} s · {StageLoc.Get(StageStr.ExtColumnUniverses)} {ExternalControl.UniverseRanges(recording.Universes)}{(recording.FirstTimecode is { } first ? " · TC " + first : "")}"));
        if (model.RecordingPath is { } file)
            ImGui.TextDisabled(file);
    }

    private static IReadOnlyList<FileFilter> RecordingFilters() =>
        new[] { new FileFilter(StageLoc.Get(StageStr.ExtRecordingFilter), "*" + Engine.DmxRecording.Extension) };

    private static void StateDot(ProtocolState state, bool canStart)
    {
        var color = state switch
        {
            ProtocolState.Running => Theme.Success,
            ProtocolState.Warning => Theme.Warning,
            ProtocolState.Error => Theme.Error,
            ProtocolState.Unavailable => Theme.TextMuted,
            _ => canStart ? Theme.WithAlpha(Theme.TextMuted, 0.6f) : Theme.WithAlpha(Theme.TextMuted, 0.3f),
        };
        var p = ImGui.GetCursorScreenPos();
        float h = ImGui.GetTextLineHeight();
        ImGui.GetWindowDrawList().AddCircleFilled(p + new Vector2(h * 0.4f, h * 0.55f), h * 0.28f, Theme.U32(color), 12);
        ImGui.Dummy(new Vector2(h * 0.8f, h));
    }

    private static void SupportBadge(ProtocolSupportLevel level)
    {
        var (text, color) = level switch
        {
            ProtocolSupportLevel.Implemented => (StageLoc.Get(StageStr.SupportImplemented), Theme.Success),
            ProtocolSupportLevel.Partial => (StageLoc.Get(StageStr.SupportPartial), Theme.Note),
            ProtocolSupportLevel.Planned => (StageLoc.Get(StageStr.SupportPlanned), Theme.TextMuted),
            _ => (StageLoc.Get(StageStr.SupportVendorSdk), Theme.Warning),
        };
        ImGui.TextColored(color, text);
    }

    private static string StateText(ProtocolState state) => StageLoc.Get(state switch
    {
        ProtocolState.Running => StageStr.StateRunning,
        ProtocolState.Warning => StageStr.StateWarning,
        ProtocolState.Error => StageStr.StateError,
        ProtocolState.Unavailable => StageStr.StateUnavailable,
        _ => StageStr.StateStopped,
    });

    private static int IndexOf(string id)
    {
        var all = ProtocolRegistry.All;
        for (int i = 0; i < all.Count; i++)
        {
            if (all[i].Id == id)
                return i;
        }

        return -1;
    }

    private static string[] BuildNumbers(int count)
    {
        var numbers = new string[count];
        for (int i = 0; i < count; i++)
            numbers[i] = i.ToString(CultureInfo.InvariantCulture);
        return numbers;
    }

    private static string Text(byte[] buffer)
    {
        int length = Array.IndexOf(buffer, (byte)0);
        return Encoding.UTF8.GetString(buffer, 0, length < 0 ? buffer.Length : length);
    }
}
