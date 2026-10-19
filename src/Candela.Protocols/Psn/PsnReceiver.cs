using System.Net;
using Candela.Protocols.Networking;

namespace Candela.Protocols.Psn;

/// <summary>One row of the tracker table: the latest data of a tracker plus its names.</summary>
/// <param name="Source">Address of the PSN server.</param>
/// <param name="SystemName">The server's system name (from info packets).</param>
/// <param name="Name">The tracker's name (from info packets), empty until known.</param>
/// <param name="Data">Latest values (merged across packets).</param>
/// <param name="LastUpdateUtc">When data for this tracker last arrived.</param>
public sealed record PsnTrackerInfo(string Source, string SystemName, string Name, PsnTracker Data, DateTime LastUpdateUtc);

/// <summary>
/// PosiStageNet 2.0 receiver: joins multicast 236.10.10.10 on UDP 56565 (join failure is a warning; unicast works),
/// parses info and data packets and keeps a tracker table keyed by server address and tracker id. Trackers silent for
/// <see cref="TrackerTimeout"/> drop out of <see cref="Trackers"/>. Tracking data is for follow-spot and automation
/// features; it does not feed the DMX hub.
/// </summary>
public sealed class PsnReceiver : ExternalProtocolBase
{
    private readonly object _gate = new();
    private readonly Dictionary<(uint Source, int Id), Entry> _trackers = new();
    private readonly Dictionary<uint, string> _systemNames = new();
    private readonly Dictionary<(uint Source, int Id), string> _trackerNames = new();
    private readonly List<PsnTracker> _scratchTrackers = new();
    private readonly List<KeyValuePair<int, string>> _scratchNames = new();
    private volatile UdpChannel? _channel;
    private int _reportedCount = -1;

    /// <summary>Creates the receiver.</summary>
    public PsnReceiver(TimeProvider? timeProvider = null)
        : base(ProtocolRegistry.Get(ProtocolIds.PosiStageNet), timeProvider)
    {
    }

    /// <summary>Trackers older than this are left out of <see cref="Trackers"/>.</summary>
    public TimeSpan TrackerTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>The local UDP endpoint while running, else null.</summary>
    public IPEndPoint? LocalEndPoint => _channel?.LocalEndPoint;

    /// <summary>Raised on the receive thread after each data packet has updated the table.</summary>
    public event EventHandler? TrackersUpdated;

    /// <summary>A snapshot of the live trackers, sorted by source and id.</summary>
    public IReadOnlyList<PsnTrackerInfo> Trackers
    {
        get
        {
            var cutoff = UtcNow - TrackerTimeout;
            lock (_gate)
            {
                var list = new List<PsnTrackerInfo>(_trackers.Count);
                foreach (var ((source, id), e) in _trackers)
                {
                    if (e.LastUpdate < cutoff) continue;
                    _systemNames.TryGetValue(source, out var system);
                    _trackerNames.TryGetValue((source, id), out var name);
                    list.Add(new PsnTrackerInfo(e.SourceText, system ?? "", name ?? "", e.Data, e.LastUpdate));
                }
                list.Sort(static (a, b) =>
                {
                    int c = string.CompareOrdinal(a.Source, b.Source);
                    return c != 0 ? c : a.Data.Id.CompareTo(b.Data.Id);
                });
                return list;
            }
        }
    }

    /// <summary>The latest data of tracker <paramref name="id"/> from any server, if live.</summary>
    public bool TryGetTracker(int id, out PsnTrackerInfo? tracker)
    {
        foreach (var t in Trackers)
            if (t.Data.Id == id)
            {
                tracker = t;
                return true;
            }
        tracker = null;
        return false;
    }

    /// <inheritdoc />
    protected override string OnStart(ProtocolOptions options)
    {
        ProtocolOptions.ValidatePort(options.PsnPort, "PosiStageNet");
        if (!IPAddress.TryParse(options.PsnMulticastGroup, out var group) || !IsMulticast(group))
            throw new ProtocolStartException(ProtocolText.Create(ProtocolMessages.PsnInvalidGroup, options.PsnMulticastGroup), ProtocolErrorCode.InvalidSettings);
        var interfaces = NetworkInterfaceCatalog.Enumerate();
        var selected = NetworkInterfaceCatalog.Resolve(options.NetworkInterface, interfaces);
        lock (_gate)
        {
            _trackers.Clear();
            _systemNames.Clear();
            _trackerNames.Clear();
            _reportedCount = -1;
        }
        var channel = UdpChannel.Open("PosiStageNet", UdpChannel.BindAddressFor(selected), options.PsnPort, enableBroadcast: false);
        _channel = channel;
        bool joined = channel.TryJoinMulticast(group, NetworkInterfaceCatalog.MulticastInterfaces(selected, interfaces), out var error);
        if (!joined)
            AddWarning(ProtocolText.Create(ProtocolMessages.PsnMulticastFailed, group, error));
        channel.Start("Candela PosiStageNet receiver", ProcessPacket, _ => CountBadPacket("internal error while processing a PSN packet", default),
            e => ReportFault(ProtocolText.Create(ProtocolMessages.SocketFailed, "PosiStageNet", e.Message), ProtocolErrorCode.SocketFailed));
        int port = channel.LocalEndPoint.Port;
        var text = (joined, selected) switch
        {
            (true, null) => ProtocolText.Create(ProtocolMessages.PsnListeningAll, port, group),
            (true, { } s) => ProtocolText.Create(ProtocolMessages.PsnListening, port, s.Name, s.Address, group),
            (false, null) => ProtocolText.Create(ProtocolMessages.ListeningAll, port),
            (false, { } s) => ProtocolText.Create(ProtocolMessages.Listening, port, s.Name, s.Address),
        };
        return Running(text, channel.LocalEndPoint.ToString(), selected is null ? null : $"{selected.Name} {selected.Address}", 0);
    }

    /// <inheritdoc />
    protected internal override string OptionsSignature(ProtocolOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Signature(options.NetworkInterface, options.PsnMulticastGroup, options.PsnPort);
    }

    /// <inheritdoc />
    protected override void OnStop()
    {
        var channel = _channel;
        _channel = null;
        channel?.Dispose();
    }

    /// <summary>Processes one datagram as if received from <paramref name="sender"/> (receive thread; tests). Never
    /// throws for malformed input.</summary>
    internal void ProcessPacket(ReadOnlySpan<byte> packet, IPv4Endpoint sender)
    {
        var now = UtcNow;
        bool updated = false;
        int count;
        lock (_gate)
        {
            switch (PsnPacketReader.GetKind(packet))
            {
                case PsnPacketKind.Data:
                    if (!PsnPacketReader.TryReadData(packet, out _, _scratchTrackers, out var dataError))
                    {
                        CountBadPacket(dataError!, sender);
                        return;
                    }
                    foreach (var t in _scratchTrackers)
                    {
                        var key = (sender.Address, t.Id);
                        if (_trackers.TryGetValue(key, out var e))
                        {
                            e.Data = e.Data.Merge(t);
                            e.LastUpdate = now;
                        }
                        else
                        {
                            if (_trackers.Count >= 65536) Prune(now);
                            _trackers[key] = new Entry(sender.AddressString, t, now);
                        }
                    }
                    updated = true;
                    break;
                case PsnPacketKind.Info:
                    if (!PsnPacketReader.TryReadInfo(packet, out _, out var system, _scratchNames, out var infoError))
                    {
                        CountBadPacket(infoError!, sender);
                        return;
                    }
                    if (!_systemNames.TryGetValue(sender.Address, out var oldSystem) || oldSystem != system)
                    {
                        if (_systemNames.Count > 1024) _systemNames.Clear();
                        _systemNames[sender.Address] = system;
                    }
                    foreach (var (id, name) in _scratchNames)
                    {
                        if (_trackerNames.TryGetValue((sender.Address, id), out var old) && old == name) continue;
                        if (_trackerNames.Count >= 65536) _trackerNames.Clear();
                        _trackerNames[(sender.Address, id)] = name;
                    }
                    break;
                default:
                    CountBadPacket("not a PosiStageNet 2 packet", sender);
                    return;
            }
            count = _trackers.Count;
        }
        CountPacket(now);
        if (!updated) return;
        if (count != _reportedCount && _channel?.LocalEndPoint is { } endPoint)
        {
            // Only when the number of trackers changes, so steady 60 Hz data does not allocate.
            _reportedCount = count;
            SetMessage(ProtocolText.Create(ProtocolMessages.PsnTrackers, endPoint.Port, count));
        }
        TrackersUpdated?.Invoke(this, EventArgs.Empty);
    }

    private void Prune(DateTime now)
    {
        var cutoff = now - TrackerTimeout;
        foreach (var key in _trackers.Where(kv => kv.Value.LastUpdate < cutoff).Select(kv => kv.Key).ToArray()) _trackers.Remove(key);
        if (_trackers.Count >= 65536) _trackers.Clear();
    }

    private static bool IsMulticast(IPAddress address)
    {
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        byte first = address.GetAddressBytes()[0];
        return first is >= 224 and <= 239;
    }

    private sealed class Entry
    {
        public Entry(string sourceText, PsnTracker data, DateTime lastUpdate)
        {
            SourceText = sourceText;
            Data = data;
            LastUpdate = lastUpdate;
        }

        public string SourceText { get; }
        public PsnTracker Data { get; set; }
        public DateTime LastUpdate { get; set; }
    }
}
