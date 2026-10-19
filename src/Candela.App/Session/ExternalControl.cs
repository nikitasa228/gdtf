using System.Globalization;
using System.Net;
using System.Text.Json;
using Candela.App.Ui;
using Candela.Core.Serialization;
using Candela.Core.Settings;
using Candela.Dmx.Input;
using Candela.Engine;
using Candela.Protocols;
using Candela.Protocols.ArtNet;

namespace Candela.App.Session;

/// <summary>
/// The external-control settings that are not part of <see cref="AppSettings"/> (which keeps the network interface and
/// the Art-Net universe offset): enabled protocols, ports, the EntTec widget, receive switches, the merge mode and
/// the test-pattern target. Stored per user in <c>external-control.json</c> in the config folder (they describe this
/// computer's network, not the show).
/// </summary>
internal sealed class ExternalControlSettings
{
    /// <summary>File name in the config folder.</summary>
    public const string FileName = "external-control.json";

    /// <summary>Ids of the protocols to run (<see cref="ProtocolIds"/>).</summary>
    public List<string> Enabled { get; set; } = new();

    /// <summary>Art-Net UDP port (6454).</summary>
    public int ArtNetPort { get; set; } = 6454;

    /// <summary>Apply ArtSync frames together (Art-Net 4).</summary>
    public bool ArtNetSync { get; set; } = true;

    /// <summary>Also broadcast ArtPollReply (Art-Net 3 controllers).</summary>
    public bool ArtNetBroadcastPollReply { get; set; }

    /// <summary>sACN UDP port (5568).</summary>
    public int SacnPort { get; set; } = 5568;

    /// <summary>Join the multicast group of each subscribed sACN universe.</summary>
    public bool SacnJoinMulticast { get; set; } = true;

    /// <summary>Accept sACN preview data.</summary>
    public bool SacnAcceptPreviewData { get; set; }

    /// <summary>Accept data for universes the patch does not use.</summary>
    public bool AcceptUnsubscribedUniverses { get; set; } = true;

    /// <summary>OSC UDP port (8000).</summary>
    public int OscPort { get; set; } = 8000;

    /// <summary>ETC Eos OSC output port (8001).</summary>
    public int EosOscPort { get; set; } = 8001;

    /// <summary>PosiStageNet UDP port (56565).</summary>
    public int PsnPort { get; set; } = 56565;

    /// <summary>Serial port of the EntTec DMX USB Pro ("COM3", "/dev/ttyUSB0").</summary>
    public string EnttecSerialPort { get; set; } = "";

    /// <summary>Internal universe the EntTec input feeds.</summary>
    public int EnttecUniverse { get; set; } = 1;

    /// <summary>How the hub merges several sources of one universe.</summary>
    public DmxMergeMode MergeMode { get; set; } = DmxMergeMode.Htp;

    /// <summary>Where the Art-Net test pattern is sent (an IPv4 address; 127.0.0.1 drives this computer).</summary>
    public string TestTarget { get; set; } = "127.0.0.1";

    /// <summary>A copy with every value in range (settings read from a file written by hand or another version).</summary>
    public ExternalControlSettings Sanitized()
    {
        static int Port(int value, int fallback) => value is > 0 and <= 65535 ? value : fallback;
        var known = new HashSet<string>(ProtocolRegistry.All.Select(p => p.Id), StringComparer.Ordinal);
        return new ExternalControlSettings
        {
            Enabled = (Enabled ?? new List<string>()).Where(id => id is not null && known.Contains(id)).Distinct(StringComparer.Ordinal).ToList(),
            ArtNetPort = Port(ArtNetPort, 6454),
            ArtNetSync = ArtNetSync,
            ArtNetBroadcastPollReply = ArtNetBroadcastPollReply,
            SacnPort = Port(SacnPort, 5568),
            SacnJoinMulticast = SacnJoinMulticast,
            SacnAcceptPreviewData = SacnAcceptPreviewData,
            AcceptUnsubscribedUniverses = AcceptUnsubscribedUniverses,
            OscPort = Port(OscPort, 8000),
            EosOscPort = Port(EosOscPort, 8001),
            PsnPort = Port(PsnPort, 56565),
            EnttecSerialPort = (EnttecSerialPort ?? "").Trim(),
            EnttecUniverse = Math.Clamp(EnttecUniverse, 1, 63999),
            MergeMode = Enum.IsDefined(MergeMode) ? MergeMode : DmxMergeMode.Htp,
            TestTarget = string.IsNullOrWhiteSpace(TestTarget) ? "127.0.0.1" : TestTarget.Trim(),
        };
    }

    /// <summary>The protocol options for these settings and the user's network settings.</summary>
    public ProtocolOptions ToOptions(AppSettings app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return ProtocolOptions.FromSettings(app) with
        {
            ArtNetPort = ArtNetPort,
            ArtNetSync = ArtNetSync,
            ArtNetBroadcastPollReply = ArtNetBroadcastPollReply,
            SacnPort = SacnPort,
            SacnJoinMulticast = SacnJoinMulticast,
            SacnAcceptPreviewData = SacnAcceptPreviewData,
            AcceptUnsubscribedUniverses = AcceptUnsubscribedUniverses,
            OscPort = OscPort,
            EosOscPort = EosOscPort,
            PsnPort = PsnPort,
            EnttecSerialPort = EnttecSerialPort,
            EnttecUniverse = EnttecUniverse,
        };
    }
}

/// <summary>Where the last timecode came from.</summary>
internal enum TimecodeSource
{
    /// <summary>Not known (no timecode, or neither of the sources below sent it).</summary>
    Unknown,

    /// <summary>ArtTimeCode received by the Art-Net receiver.</summary>
    ArtNet,

    /// <summary>A DMX recording being played back.</summary>
    Recording,
}

/// <summary>One live sender as the external-control panel lists it (grouped over its universes).</summary>
internal sealed record SourceRow(string Protocol, string Name, string Address, string Universes, int Priority, long Packets, double SecondsAgo);

/// <summary>
/// The model of the external-control panel: protocol settings and their application to the engine's protocol manager,
/// status snapshots (polled a few times a second, never per frame), live sources, universe activity, the last
/// timecode, the merge mode, the Art-Net test console and the DMX recorder. Every operation reports problems as a
/// message instead of throwing. Main thread, except the timecode handler (receive threads; guarded by a lock).
/// </summary>
internal sealed class ExternalControl : IDisposable
{
    /// <summary>Seconds between status polls.</summary>
    public const double PollInterval = 0.25;

    /// <summary>Universes shown in the activity grid at least (multiples of 16 beyond).</summary>
    public const int MinimumGridUniverses = 16;

    private readonly object _timecodeLock = new();
    private readonly byte[] _slots = new byte[DmxInputHub.SlotCount];
    private ProtocolStatus[] _statuses;
    private IReadOnlyList<SourceRow> _sources = Array.Empty<SourceRow>();
    private HashSet<int> _activeUniverses = new();
    private HashSet<int> _patchedUniverses = new();
    private Dictionary<int, double> _rates = new();
    private StageEngine? _engine;
    private ArtNetTestConsole? _testConsole;
    private double _lastPoll = double.NegativeInfinity;
    private long _patchVersion = -1;
    private Timecode? _timecode;
    private DateTime _timecodeAt;
    private Timecode? _artNetTimecode;
    private DateTime _artNetTimecodeAt;
    private ArtNetReceiver? _artNetReceiver;
    private IReadOnlyList<string> _nodes = Array.Empty<string>();
    private int _trackerCount;
    private readonly bool[] _patchedSlots = new bool[DmxInputHub.SlotCount];
    private (Candela.Dmx.Patch? Patch, int Universe) _patchedSlotsOf;
    private string? _settingsPath;
    private bool _disposed;

    /// <summary>Creates the model with default settings (call <see cref="Load"/> to read the user's file).</summary>
    public ExternalControl()
    {
        _statuses = ProtocolRegistry.All.Select(p => ProtocolStatus.NotStarted(p.Id)).ToArray();
    }

    /// <summary>The settings (edit, then call <see cref="ApplySettings"/>).</summary>
    public ExternalControlSettings Settings { get; private set; } = new();

    /// <summary>False in headless runs: settings are not written.</summary>
    public bool CanSave { get; set; } = true;

    /// <summary>The last message of an operation (error or confirmation), or null.</summary>
    public string? Message { get; private set; }

    /// <summary>True when <see cref="Message"/> reports a problem.</summary>
    public bool MessageIsError { get; private set; }

    /// <summary>The latest status of each protocol, in <see cref="ProtocolRegistry.All"/> order.</summary>
    public IReadOnlyList<ProtocolStatus> Statuses => _statuses;

    /// <summary>Live senders, grouped by source.</summary>
    public IReadOnlyList<SourceRow> Sources => _sources;

    /// <summary>Universe currently shown in the slot view (0 = none).</summary>
    public int SelectedUniverse { get; set; }

    /// <summary>The recording made or loaded last, or null.</summary>
    public DmxRecording? Recording { get; private set; }

    /// <summary>File the recording was loaded from or saved to, or null.</summary>
    public string? RecordingPath { get; private set; }

    /// <summary>True while the Art-Net test console sends.</summary>
    public bool TestPatternRunning => _testConsole?.IsRunning == true;

    /// <summary>Frames the test console has sent.</summary>
    public long TestFramesSent => _testConsole?.FramesSent ?? 0;

    /// <summary>The last error of the test console, or null.</summary>
    public string? TestPatternError => _testConsole?.LastError;

    /// <summary>The patch of the scene as of the last poll (who uses which slots), or null.</summary>
    public Candela.Dmx.Patch? Patch { get; private set; }

    /// <summary>Number of universes the activity grid shows.</summary>
    public int GridUniverses { get; private set; } = MinimumGridUniverses;

    /// <summary>True when <paramref name="universe"/> receives live data.</summary>
    public bool IsActive(int universe) => _activeUniverses.Contains(universe);

    /// <summary>True when a fixture is patched in <paramref name="universe"/>.</summary>
    public bool IsPatched(int universe) => _patchedUniverses.Contains(universe);

    /// <summary>Measured packets per second of <paramref name="universe"/> over all protocols (0 = none).</summary>
    public double RateOf(int universe) => _rates.TryGetValue(universe, out double r) ? r : 0;

    /// <summary>Is the protocol enabled in the settings?</summary>
    public bool IsEnabled(string id) => Settings.Enabled.Contains(id, StringComparer.Ordinal);

    /// <summary>The last received timecode and when it arrived (UTC), or null.</summary>
    public (Timecode Timecode, DateTime ReceivedUtc)? LastTimecode
    {
        get
        {
            lock (_timecodeLock)
                return _timecode is { } tc ? (tc, _timecodeAt) : null;
        }
    }

    /// <summary>
    /// Where the last timecode came from: Art-Net (the receiver reported the same timecode within half a second) or the
    /// DMX recording that is playing; <see cref="TimecodeSource.Unknown"/> otherwise. Main thread.
    /// </summary>
    public TimecodeSource LastTimecodeSource
    {
        get
        {
            lock (_timecodeLock)
            {
                if (_timecode is not { } tc)
                    return TimecodeSource.Unknown;
                if (_artNetTimecode == tc && Math.Abs((_timecodeAt - _artNetTimecodeAt).TotalSeconds) < 0.5)
                    return TimecodeSource.ArtNet;
            }

            return _engine?.Player is { IsAtEnd: false } ? TimecodeSource.Recording : TimecodeSource.Unknown;
        }
    }

    /// <summary>Art-Net nodes (consoles, gateways) that answered ArtPoll, as of the last poll ("ShortName (address)").</summary>
    public IReadOnlyList<string> ArtNetNodes => _nodes;

    /// <summary>PosiStageNet trackers seen, as of the last poll.</summary>
    public int TrackerCount => _trackerCount;

    /// <summary>
    /// For each of the 512 slots of <paramref name="universe"/>, whether a fixture of the patch uses it (the slot view
    /// highlights them). Rebuilt when the patch or the universe changes; allocation-free otherwise.
    /// </summary>
    public ReadOnlySpan<bool> PatchedSlots(int universe)
    {
        var patch = Patch;
        if (!ReferenceEquals(_patchedSlotsOf.Patch, patch) || _patchedSlotsOf.Universe != universe)
        {
            _patchedSlotsOf = (patch, universe);
            Array.Clear(_patchedSlots);
            if (patch is not null && universe > 0)
            {
                foreach (var entry in patch.Entries)
                {
                    foreach (var range in entry.Ranges)
                    {
                        if (range.Universe != universe)
                            continue;
                        for (int a = Math.Max(range.Start, 1); a <= Math.Min(range.End, DmxInputHub.SlotCount); a++)
                            _patchedSlots[a - 1] = true;
                    }
                }
            }
        }

        return _patchedSlots;
    }

    /// <summary>
    /// Who uses slot <paramref name="slot"/> of <paramref name="universe"/>: one line per fixture with the GDTF channel
    /// at that slot ("Fixture 101 'Spot 1' · Pan (coarse)"). Empty when nobody does.
    /// </summary>
    public static IEnumerable<string> SlotUsers(Candela.Dmx.Patch patch, int universe, int slot)
    {
        ArgumentNullException.ThrowIfNull(patch);
        foreach (var entry in patch.At(universe, slot))
        {
            string? channel = null;
            if (entry.Profile is { } profile)
            {
                foreach (var range in entry.Ranges)
                {
                    if (range.Universe != universe || slot < range.Start || slot > range.End)
                        continue;
                    int offset = slot - range.Start + 1;
                    foreach (var ch in profile.Channels)
                    {
                        if (ch.Break != range.Break)
                            continue;
                        int byteIndex = Array.IndexOf(ch.Instance.Offsets, offset);
                        if (byteIndex < 0)
                            continue;
                        string attribute = string.IsNullOrEmpty(ch.Attribute) ? ch.Name : ch.Attribute;
                        string instance = ch.InstanceName is { Length: > 0 } inst && !string.Equals(inst, ch.Source.GeometryName, StringComparison.Ordinal)
                            ? " @ " + inst
                            : "";
                        channel = ch.Instance.Offsets.Length > 1
                            ? $"{attribute}{instance} ({(byteIndex == 0 ? "coarse" : byteIndex == 1 ? "fine" : "ultra")})"
                            : attribute + instance;
                        break;
                    }

                    if (channel is not null)
                        break;
                }
            }

            yield return channel is null ? entry.Label : entry.Label + " · " + channel;
        }
    }

    /// <summary>
    /// The address the Art-Net test show is sent to. A loopback target (this computer) is sent to the address of the
    /// selected network interface instead when one is selected: a receiver bound to that interface (Windows) does not
    /// see loopback datagrams, and every receiver on the wildcard address sees datagrams to the interface's own address.
    /// Null when the target is not an IPv4 address.
    /// </summary>
    public IPAddress? EffectiveTestTarget(AppSettings app, IReadOnlyList<Protocols.Networking.NetworkInterfaceInfo>? interfaces = null)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (!IPAddress.TryParse(Settings.TestTarget, out var address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return null;
        if (!IPAddress.IsLoopback(address) || string.IsNullOrWhiteSpace(app.NetworkInterface))
            return address;
        interfaces ??= ProtocolManager.GetNetworkInterfaces();
        return Protocols.Networking.NetworkInterfaceCatalog.TryResolve(app.NetworkInterface, interfaces, out var nic) &&
               nic is { IsLoopback: false } && nic.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            ? nic.Address
            : address;
    }

    /// <summary>Reads the settings file of <paramref name="configDir"/> (missing or damaged files give the defaults and a message).</summary>
    public void Load(string configDir)
    {
        _settingsPath = Path.Combine(configDir, ExternalControlSettings.FileName);
        try
        {
            if (File.Exists(_settingsPath))
                Settings = (CandelaJson.Deserialize<ExternalControlSettings>(File.ReadAllText(_settingsPath)) ?? new()).Sanitized();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            Settings = new ExternalControlSettings();
            Report(StageLoc.Format(StageStr.ExtSettingsDamaged, _settingsPath, e.Message), error: true);
        }
    }

    /// <summary>Writes the settings file (atomic replace); false with a message when it cannot be written.</summary>
    public bool Save()
    {
        if (!CanSave || _settingsPath is null)
            return true;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            string tmp = _settingsPath + "." + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + ".tmp";
            File.WriteAllText(tmp, CandelaJson.Serialize(Settings));
            File.Move(tmp, _settingsPath, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Report(StageLoc.Format(StageStr.ExtSettingsNotSaved, e.Message), error: true);
            return false;
        }
    }

    /// <summary>
    /// Connects the model to <paramref name="engine"/> (or detaches it): subscribes to its timecode, applies the merge
    /// mode and starts the enabled protocols. Called when the engine changes; cheap when it did not.
    /// </summary>
    public void Bind(StageEngine? engine, AppSettings app)
    {
        if (ReferenceEquals(engine, _engine))
            return;
        if (_engine is not null)
            _engine.Input.TimecodeReceived -= OnTimecode;
        WatchArtNet(null);
        StopTestPattern();
        _engine = engine;
        _patchVersion = -1;
        if (engine is null)
            return;
        engine.Input.TimecodeReceived += OnTimecode;
        engine.Input.MergeMode = Settings.MergeMode;
        if (Settings.Enabled.Count > 0)
            Apply(engine, app);
    }

    /// <summary>Enables or disables a protocol and applies the change (a planned protocol reports why it cannot start).</summary>
    public void SetEnabled(StageEngine engine, AppSettings app, string id, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(engine);
        var descriptor = ProtocolRegistry.Find(id);
        if (descriptor is null)
            return;
        if (enabled && !descriptor.CanStart)
        {
            Report(StageLoc.Format(StageStr.ExtCannotStart, descriptor.DisplayName, descriptor.SupportReason), error: true);
            return;
        }

        Settings.Enabled.RemoveAll(e => string.Equals(e, id, StringComparison.Ordinal));
        if (enabled)
            Settings.Enabled.Add(id);
        Save();
        Apply(engine, app);
    }

    /// <summary>Applies changed settings (ports, interface, offset, merge mode) to the running protocols and saves them.</summary>
    public void ApplySettings(StageEngine? engine, AppSettings app)
    {
        Settings = Settings.Sanitized();
        Save();
        if (engine is null)
            return;
        engine.Input.MergeMode = Settings.MergeMode;
        Apply(engine, app);
    }

    private void Apply(StageEngine engine, AppSettings app)
    {
        try
        {
            var statuses = engine.StartProtocols(Settings.Enabled, Settings.ToOptions(app));
            foreach (var status in statuses)
            {
                if (status.State == ProtocolState.Error && Settings.Enabled.Contains(status.ProtocolId))
                    Report(StageLoc.Format(StageStr.ExtStartFailed, ProtocolRegistry.Find(status.ProtocolId)?.DisplayName ?? status.ProtocolId,
                        status.LastError ?? status.Message), error: true);
            }
        }
        catch (Exception e) when (e is ProtocolStartException or ArgumentException or InvalidOperationException)
        {
            Report(e.Message, error: true);
        }

        _lastPoll = double.NegativeInfinity;
    }

    /// <summary>Per-frame housekeeping: polls statuses, sources and universe activity a few times a second.</summary>
    public void Update(StageEngine? engine, double now)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (engine is null || !ReferenceEquals(engine, _engine))
            return;
        if (now - _lastPoll < PollInterval)
            return;
        _lastPoll = now;
        Poll(engine);
    }

    /// <summary>Reads statuses, live sources, active and patched universes now (allocates; a few times a second).</summary>
    internal void Poll(StageEngine engine)
    {
        var protocols = engine.Protocols.Protocols;
        var statuses = new ProtocolStatus[ProtocolRegistry.All.Count];
        var rates = new Dictionary<int, double>();
        for (int i = 0; i < statuses.Length; i++)
        {
            string id = ProtocolRegistry.All[i].Id;
            var protocol = engine.Protocols.Find(id);
            statuses[i] = protocol?.Status ?? ProtocolStatus.NotStarted(id);
            foreach (var u in statuses[i].Universes)
                rates[u.Universe] = rates.GetValueOrDefault(u.Universe) + u.PacketsPerSecond;
        }

        _ = protocols;
        WatchArtNet(engine.Protocols.Get<ArtNetReceiver>());
        _nodes = _artNetReceiver is { IsRunning: true } receiver
            ? receiver.Nodes.Select(n => $"{n.Reply.ShortName} ({n.Sender})").ToArray()
            : Array.Empty<string>();
        _trackerCount = engine.Protocols.Get<Candela.Protocols.Psn.PsnReceiver>() is { IsRunning: true } psn ? psn.Trackers.Count : 0;
        _statuses = statuses;
        _rates = rates;
        _activeUniverses = new HashSet<int>(engine.Input.ActiveUniverses());
        _sources = GroupSources(engine.Input.Sources(), DateTime.UtcNow);
        long version = engine.Scene.Version;
        if (version != _patchVersion)
        {
            _patchVersion = version;
            try
            {
                Patch = engine.Dmx.BuildPatch();
                _patchedUniverses = new HashSet<int>(Patch.Universes);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                Patch = null;
                _patchedUniverses = new HashSet<int>();
            }
        }

        int highest = Math.Max(_activeUniverses.Count > 0 ? _activeUniverses.Max() : 0, _patchedUniverses.Count > 0 ? _patchedUniverses.Max() : 0);
        GridUniverses = Math.Clamp((highest + 15) / 16 * 16, MinimumGridUniverses, 512);
    }

    /// <summary>Groups the hub's per-universe source entries by sender.</summary>
    internal static IReadOnlyList<SourceRow> GroupSources(IReadOnlyList<DmxSourceInfo> infos, DateTime nowUtc)
    {
        var rows = new List<SourceRow>();
        foreach (var group in infos.GroupBy(s => s.Source))
        {
            var list = group.OrderBy(s => s.Universe).ToList();
            string name = list.Select(s => s.Name).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? "";
            string endpoint = group.Key.Endpoint;
            int slash = endpoint.IndexOf('/');
            string address = slash > 0 ? endpoint[..slash] : endpoint;
            string protocol = ProtocolRegistry.Find(group.Key.Protocol)?.DisplayName ?? group.Key.Protocol switch
            {
                DmxPlayer.SourceProtocol => StageLoc.Get(StageStr.ExtRecordingSource),
                _ => group.Key.Protocol,
            };
            var newest = list.Max(s => s.LastSeenUtc);
            rows.Add(new SourceRow(protocol, name, address, UniverseRanges(list.Select(s => s.Universe)), list.Max(s => s.Priority),
                list.Sum(s => s.PacketCount), Math.Max(0, (nowUtc - newest).TotalSeconds)));
        }

        rows.Sort((a, b) => string.CompareOrdinal(a.Protocol + a.Address, b.Protocol + b.Address));
        return rows;
    }

    /// <summary>"1–4, 7" for a set of universes.</summary>
    internal static string UniverseRanges(IEnumerable<int> universes)
    {
        var sorted = universes.Distinct().Order().ToList();
        var parts = new List<string>();
        for (int i = 0; i < sorted.Count;)
        {
            int j = i;
            while (j + 1 < sorted.Count && sorted[j + 1] == sorted[j] + 1)
                j++;
            parts.Add(j > i ? $"{sorted[i]}–{sorted[j]}" : sorted[i].ToString(CultureInfo.InvariantCulture));
            i = j + 1;
        }

        return string.Join(", ", parts);
    }

    /// <summary>The merged slots of the selected universe (zeros without a source); allocation-free, per frame.</summary>
    public ReadOnlySpan<byte> ReadSelectedUniverse(StageEngine engine, out bool live)
    {
        live = SelectedUniverse > 0 && engine.Input.TryRead(SelectedUniverse, _slots, out _);
        return _slots;
    }

    /// <summary>Changes the merge mode of the DMX input hub and saves it.</summary>
    public void SetMergeMode(StageEngine? engine, DmxMergeMode mode)
    {
        Settings.MergeMode = mode;
        if (engine is not null)
            engine.Input.MergeMode = mode;
        Save();
    }

    /// <summary>
    /// Starts sending the Art-Net test show (pan/tilt sweep, colour chase, dimmer wave, zoom, with ArtSync and
    /// ArtTimeCode) built from the scene's patch with the fixtures' own GDTF channels (a demo rig when nothing is
    /// patched) to <see cref="ExternalControlSettings.TestTarget"/>. Sending to this computer starts the Art-Net receiver
    /// when it is not running, so the fixtures follow at once.
    /// </summary>
    public bool StartTestPattern(StageEngine engine, AppSettings app)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(app);
        StopTestPattern();
        if (EffectiveTestTarget(app) is not { } address)
        {
            Report(StageLoc.Format(StageStr.ExtTestBadTarget, Settings.TestTarget), error: true);
            return false;
        }

        bool viaInterface = IPAddress.TryParse(Settings.TestTarget, out var configured) && IPAddress.IsLoopback(configured) && !IPAddress.IsLoopback(address);

        try
        {
            var pattern = GdtfTestPatternEncoder.ForScene(engine);
            if (pattern.FixtureCount == 0)
                pattern = ArtNetTestPattern.DemoRig();
            if ((IPAddress.IsLoopback(address) || viaInterface) && !IsEnabled(ProtocolIds.ArtNet))
            {
                Settings.Enabled.Add(ProtocolIds.ArtNet);
                Save();
                Apply(engine, app);
            }

            _testConsole = new ArtNetTestConsole(pattern, new IPEndPoint(address, Settings.ArtNetPort), app.ArtNetUniverseOffset);
            _testConsole.Start();
            string target = viaInterface ? StageLoc.Format(StageStr.ExtTestTargetInterface, address) : Settings.TestTarget;
            Report(StageLoc.Format(StageStr.ExtTestStarted, pattern.FixtureCount, ExternalControl.UniverseRanges(pattern.Universes), target),
                error: false);
            return true;
        }
        catch (Exception e) when (e is System.Net.Sockets.SocketException or InvalidOperationException or ArgumentException or IOException)
        {
            StopTestPattern();
            Report(StageLoc.Format(StageStr.ExtTestFailed, e.Message), error: true);
            return false;
        }
    }

    /// <summary>Stops the test console.</summary>
    public void StopTestPattern()
    {
        _testConsole?.Dispose();
        _testConsole = null;
    }

    /// <summary>Starts recording the merged DMX input.</summary>
    public void StartRecording(StageEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        if (engine.Recorder.IsRecording)
            return;
        string name = StageLoc.Format(StageStr.ExtRecordingName, DateTime.Now);
        engine.Recorder.Start(engine.Time, name);
        Report(StageLoc.Get(StageStr.ExtRecordingStarted), error: false);
    }

    /// <summary>Stops recording; the recording becomes <see cref="Recording"/>.</summary>
    public void StopRecording(StageEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        if (!engine.Recorder.IsRecording)
            return;
        var recording = engine.Recorder.Stop();
        if (recording is null || recording.Frames.Count == 0)
        {
            Report(StageLoc.Get(StageStr.ExtRecordingEmpty), error: true);
            return;
        }

        Recording = recording;
        RecordingPath = null;
        Report(StageLoc.Format(StageStr.ExtRecordingStopped, recording.Frames.Count, recording.Duration), error: false);
    }

    /// <summary>Plays <see cref="Recording"/> into the hub from <paramref name="startSeconds"/>.</summary>
    public void Play(StageEngine engine, double startSeconds = 0)
    {
        ArgumentNullException.ThrowIfNull(engine);
        if (Recording is null)
            return;
        double start = double.IsFinite(startSeconds) ? Math.Clamp(startSeconds, 0, Recording.Duration) : 0;
        engine.Play(Recording, start);
    }

    /// <summary>Stops playback.</summary>
    public static void StopPlayback(StageEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        engine.Play(null);
    }

    /// <summary>Moves playback (or the start of the next playback) to <paramref name="seconds"/> of the recording.</summary>
    public void Seek(StageEngine engine, double seconds)
    {
        ArgumentNullException.ThrowIfNull(engine);
        if (Recording is null || !double.IsFinite(seconds))
            return;
        seconds = Math.Clamp(seconds, 0, Recording.Duration);
        if (engine.Player is { } player && ReferenceEquals(player.Recording, Recording))
            player.Seek(seconds, engine.Time);
        else
            engine.Play(Recording, seconds);
    }

    /// <summary>Moves playback to the moment <paramref name="timecode"/> was received; false (with a message) when the recording never received it.</summary>
    public bool SeekTimecode(StageEngine engine, Timecode timecode)
    {
        ArgumentNullException.ThrowIfNull(engine);
        if (Recording?.FindTimecode(timecode) is not { } seconds)
        {
            Report(StageLoc.Format(StageStr.ExtTimecodeNotFound, timecode), error: true);
            return false;
        }

        Seek(engine, seconds);
        return true;
    }

    /// <summary>Saves <see cref="Recording"/> as a <c>.cdmx</c> file.</summary>
    public bool SaveRecording(string path)
    {
        if (Recording is null)
            return false;
        try
        {
            if (!path.EndsWith(DmxRecording.Extension, StringComparison.OrdinalIgnoreCase))
                path += DmxRecording.Extension;
            Recording.Save(path);
            RecordingPath = path;
            Report(StageLoc.Format(StageStr.ExtRecordingSaved, path), error: false);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            Report(StageLoc.Format(StageStr.ExtRecordingSaveFailed, e.Message), error: true);
            return false;
        }
    }

    /// <summary>Loads a <c>.cdmx</c> recording (a damaged tail keeps the frames before it, with a warning).</summary>
    public bool LoadRecording(string path)
    {
        try
        {
            var recording = DmxRecording.Load(path);
            Recording = recording;
            RecordingPath = path;
            Report(recording.Issues.Count > 0
                ? StageLoc.Format(StageStr.ExtRecordingLoadedDamaged, recording.Frames.Count, recording.Issues[0].Message)
                : StageLoc.Format(StageStr.ExtRecordingLoaded, recording.Frames.Count, recording.Duration), error: recording.Issues.Count > 0);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException or ArgumentException)
        {
            Report(StageLoc.Format(StageStr.ExtRecordingLoadFailed, e.Message), error: true);
            return false;
        }
    }

    /// <summary>Parses "HH:MM:SS:FF" (or ';' before the frames for drop-frame) at the given frame type.</summary>
    public static bool TryParseTimecode(string? text, TimecodeType type, out Timecode timecode)
    {
        timecode = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var parts = text.Trim().Split(':', ';', '.');
        if (parts.Length != 4)
            return false;
        Span<int> v = stackalloc int[4];
        for (int i = 0; i < 4; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out v[i]))
                return false;
        }

        int fps = type switch { TimecodeType.Film24 => 24, TimecodeType.Ebu25 => 25, _ => 30 };
        if (v[0] > 23 || v[1] > 59 || v[2] > 59 || v[3] >= fps)
            return false;
        timecode = new Timecode(v[0], v[1], v[2], v[3], text.Contains(';') ? TimecodeType.Df2997 : type);
        return true;
    }

    /// <summary>Clears the last message.</summary>
    public void ClearMessage() => Message = null;

    private void Report(string message, bool error)
    {
        Message = message;
        MessageIsError = error;
    }

    private void OnTimecode(Timecode timecode)
    {
        lock (_timecodeLock)
        {
            _timecode = timecode;
            _timecodeAt = DateTime.UtcNow;
        }
    }

    /// <summary>Follows the engine's Art-Net receiver (a restart may replace it) to know which timecodes came over Art-Net.</summary>
    private void WatchArtNet(ArtNetReceiver? receiver)
    {
        if (ReferenceEquals(receiver, _artNetReceiver))
            return;
        if (_artNetReceiver is not null)
            _artNetReceiver.TimeCodeReceived -= OnArtNetTimecode;
        _artNetReceiver = receiver;
        if (receiver is not null)
            receiver.TimeCodeReceived += OnArtNetTimecode;
    }

    /// <summary>Receive thread of the Art-Net receiver.</summary>
    private void OnArtNetTimecode(ArtTimeCodePacket packet)
    {
        lock (_timecodeLock)
        {
            _artNetTimecode = packet.ToTimecode();
            _artNetTimecodeAt = DateTime.UtcNow;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        StopTestPattern();
        WatchArtNet(null);
        if (_engine is not null)
            _engine.Input.TimecodeReceived -= OnTimecode;
        _engine = null;
    }
}
