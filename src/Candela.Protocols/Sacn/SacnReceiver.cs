using System.Net;
using Candela.Dmx.Input;
using Candela.Protocols.Networking;

namespace Candela.Protocols.Sacn;

/// <summary>An sACN source announced through universe discovery (for the external-control panel).</summary>
/// <param name="Cid">Source CID.</param>
/// <param name="Name">Source name.</param>
/// <param name="Address">Sender IPv4 address.</param>
/// <param name="Universes">All universes the source announced (all pages), ascending.</param>
/// <param name="LastSeenUtc">When the last discovery page arrived.</param>
public sealed record SacnDiscoveredSource(Guid Cid, string Name, string Address, IReadOnlyList<int> Universes, DateTime LastSeenUtc);

/// <summary>
/// ANSI E1.31-2018 (sACN) receiver. Listens on UDP 5568, joins the multicast groups 239.255.{u&gt;&gt;8}.{u&amp;0xff} of the
/// subscribed universes on the selected interface, or on every operational adapter when none is selected (join failures
/// are warnings — unicast keeps working), validates
/// root/framing/DMP layers, applies the specification's sequence rule per source and universe, drops preview data
/// unless enabled, withdraws a source from the hub on Stream_Terminated, holds synchronised data until its
/// synchronisation packet, and collects universe-discovery announcements. Priority goes to the hub, which merges
/// sources of equal top priority. Allocation-free per packet once a source/universe is known.
/// </summary>
public sealed class SacnReceiver : ExternalProtocolBase
{
    /// <summary>Protocol name used in <see cref="DmxSourceId.Protocol"/>.</summary>
    public const string SourceProtocol = "sacn";

    /// <summary>Synchronised data is held only while synchronisation packets for its address keep arriving.</summary>
    public static readonly TimeSpan SyncTimeout = TimeSpan.FromSeconds(2.5);

    /// <summary>Most multicast groups joined at start (each group is one IGMP membership on the interface).</summary>
    public const int MaxMulticastJoins = 512;

    private static readonly TimeSpan DiscoveryTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Most (CID, universe) streams tracked at once; idle ones are forgotten first.</summary>
    internal const int MaxStreams = 4096;

    /// <summary>A stream without packets for this long may be forgotten when <see cref="MaxStreams"/> is reached.</summary>
    private static readonly TimeSpan StreamIdleTimeout = TimeSpan.FromSeconds(10);

    private static readonly ProtocolText StreamTableFullWarning = ProtocolText.Create(ProtocolMessages.SacnStreamTableFull, MaxStreams);

    private readonly DmxInputHub _hub;
    private readonly object _gate = new();
    private readonly Dictionary<(Guid Cid, int Universe), Stream> _streams = new();
    private readonly List<Stream> _pending = new();
    private readonly Dictionary<int, long> _syncSeen = new();
    private readonly HashSet<int> _joined = new();
    private readonly Dictionary<uint, string> _addressText = new();
    private readonly Dictionary<Guid, Discovery> _discovered = new();
    private volatile Runtime? _runtime;
    private long _nextPruneTicks;

    /// <summary>Creates the receiver feeding <paramref name="hub"/>.</summary>
    public SacnReceiver(DmxInputHub hub, TimeProvider? timeProvider = null)
        : base(ProtocolRegistry.Get(ProtocolIds.Sacn), timeProvider)
    {
        _hub = hub ?? throw new ArgumentNullException(nameof(hub));
    }

    /// <summary>The local UDP endpoint while running, else null.</summary>
    public IPEndPoint? LocalEndPoint => _runtime?.Channel.LocalEndPoint;

    /// <summary>Universes whose multicast group was joined successfully.</summary>
    public IReadOnlyList<int> JoinedUniverses
    {
        get
        {
            lock (_gate) return _joined.Order().ToArray();
        }
    }

    /// <summary>Sources that announced themselves through universe discovery within the last 30 s.</summary>
    public IReadOnlyList<SacnDiscoveredSource> DiscoveredSources
    {
        get
        {
            var cutoff = UtcNow - DiscoveryTimeout;
            lock (_gate)
            {
                return _discovered.Values.Where(d => d.LastSeen >= cutoff)
                    .Select(d => new SacnDiscoveredSource(d.Cid, d.Name, d.Address,
                        d.Pages.Values.SelectMany(p => p).Distinct().Order().ToArray(), d.LastSeen))
                    .OrderBy(d => d.Name, StringComparer.Ordinal).ToArray();
            }
        }
    }

    /// <inheritdoc />
    protected override string OnStart(ProtocolOptions options)
    {
        ProtocolOptions.ValidatePort(options.SacnPort, "sACN");
        var interfaces = NetworkInterfaceCatalog.Enumerate();
        var selected = NetworkInterfaceCatalog.Resolve(options.NetworkInterface, interfaces);
        var subscribed = new HashSet<int>(options.SubscribedUniverses.Where(u => u is >= SacnProtocol.MinUniverse and <= SacnProtocol.MaxUniverse));
        var channel = UdpChannel.Open("sACN", UdpChannel.BindAddressFor(selected), options.SacnPort, enableBroadcast: false);
        var rt = new Runtime(channel, options, NetworkInterfaceCatalog.MulticastInterfaces(selected, interfaces), subscribed);
        lock (_gate)
        {
            _streams.Clear();
            _pending.Clear();
            _syncSeen.Clear();
            _joined.Clear();
            _discovered.Clear();
            _nextPruneTicks = 0;
            _runtime = rt;
        }

        int joined = 0;
        if (options.SacnJoinMulticast)
        {
            var failures = new List<int>();
            string? firstError = null;
            if (subscribed.Count > MaxMulticastJoins)
                AddWarning(ProtocolText.Create(ProtocolMessages.SacnMulticastLimit, subscribed.Count, MaxMulticastJoins));
            foreach (int u in subscribed.Order().Take(MaxMulticastJoins))
            {
                if (Join(rt, u, out var error)) joined++;
                else
                {
                    failures.Add(u);
                    firstError ??= error;
                }
            }
            Join(rt, SacnProtocol.DiscoveryUniverse, out _);
            if (failures.Count > 0)
                AddWarning(ProtocolText.Create(ProtocolMessages.SacnMulticastFailed, failures.Count,
                    string.Join(", ", failures.Take(8)) + (failures.Count > 8 ? ", …" : ""), firstError));
        }
        channel.Start("Candela sACN receiver", ProcessPacket, _ => CountBadPacket("internal error while processing an sACN packet", default),
            e => ReportFault(ProtocolText.Create(ProtocolMessages.SocketFailed, "sACN", e.Message), ProtocolErrorCode.SocketFailed));
        int port = channel.LocalEndPoint.Port;
        var text = (options.SacnJoinMulticast, selected) switch
        {
            (true, null) => ProtocolText.Create(ProtocolMessages.SacnListeningAll, port, joined, subscribed.Count),
            (true, { } s) => ProtocolText.Create(ProtocolMessages.SacnListening, port, s.Name, s.Address, joined, subscribed.Count),
            (false, null) => ProtocolText.Create(ProtocolMessages.SacnUnicastAll, port),
            (false, { } s) => ProtocolText.Create(ProtocolMessages.SacnUnicast, port, s.Name, s.Address),
        };
        return Running(text, channel.LocalEndPoint.ToString(), selected is null ? null : $"{selected.Name} {selected.Address}", subscribed.Count);
    }

    /// <inheritdoc />
    protected internal override string OptionsSignature(ProtocolOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Signature(options.NetworkInterface, options.SubscribedUniverses, options.AcceptUnsubscribedUniverses,
            options.SacnPort, options.SacnAcceptPreviewData, options.SacnJoinMulticast);
    }

    /// <inheritdoc />
    protected override void OnStop()
    {
        Runtime? rt;
        List<Stream> streams;
        lock (_gate)
        {
            rt = _runtime;
            _runtime = null;
            streams = _streams.Values.ToList();
            _streams.Clear();
            _pending.Clear();
            _joined.Clear();
        }
        rt?.Channel.Dispose();
        foreach (var s in streams) _hub.Terminate(s.Source, s.Universe);
    }

    /// <summary>Processes one datagram as if received from <paramref name="sender"/> (receive thread; tests). Never
    /// throws for malformed input.</summary>
    internal void ProcessPacket(ReadOnlySpan<byte> packet, IPv4Endpoint sender)
    {
        var rt = _runtime;
        if (rt is null) return;
        var now = UtcNow;
        switch (SacnPacket.Classify(packet, out var error))
        {
            case SacnPacketKind.Data:
                HandleData(rt, packet, sender, now);
                break;
            case SacnPacketKind.Sync:
                HandleSync(rt, packet, sender, now);
                break;
            case SacnPacketKind.Discovery:
                HandleDiscovery(packet, sender, now);
                break;
            default:
                CountBadPacket(error ?? "not an E1.31 packet", sender);
                break;
        }
    }

    private void HandleData(Runtime rt, ReadOnlySpan<byte> packet, IPv4Endpoint sender, DateTime now)
    {
        if (!SacnPacket.TryParseData(packet, out var data, out var error))
        {
            CountBadPacket(error!, sender);
            return;
        }
        int universe = data.Universe;
        if ((data.IsPreview && !rt.Options.SacnAcceptPreviewData)
            || (!rt.Options.AcceptUnsubscribedUniverses && !rt.Subscribed.Contains(universe)))
        {
            CountIgnoredPacket();
            return;
        }

        string label;
        bool tableFull = false;
        try
        {
            lock (_gate)
            {
                if (!ReferenceEquals(rt, _runtime)) return;
                var key = (data.Cid, universe);
                _streams.TryGetValue(key, out var stream);
                if (stream is not null)
                {
                    // E1.31-2018 §6.7.2: discard if -20 < (new - last) <= 0 (as signed 8-bit).
                    int delta = (sbyte)(byte)(data.Sequence - stream.LastSequence);
                    if (delta is <= 0 and > -20)
                    {
                        CountIgnoredPacket();
                        CountUniversePacket(universe, now, stream.Label, sequenceError: true);
                        return;
                    }
                }

                if (data.IsStreamTerminated)
                {
                    // Data in a terminating packet is ignored; the source leaves the merge immediately.
                    if (stream is not null)
                    {
                        _hub.Terminate(stream.Source, universe);
                        if (stream.HasPending) _pending.Remove(stream);
                        _streams.Remove(key);
                    }
                    CountPacket(now);
                    return;
                }

                if (stream is null)
                {
                    if (_streams.Count >= MaxStreams && !PruneIdleStreams(now))
                    {
                        // Thousands of live sources: a flood or a misconfigured device. Keep serving the known ones.
                        tableFull = true;
                        CountIgnoredPacket();
                        return;
                    }
                    var ip = AddressText(sender.Address);
                    stream = new Stream(new DmxSourceId(SourceProtocol, ip, data.Cid), universe, ip);
                    _streams[key] = stream;
                }
                stream.LastSequence = data.Sequence;
                stream.LastSeenTicks = now.Ticks;
                stream.UpdateName(data.SourceNameField);
                label = stream.Label;

                if (data.StartCode != 0)
                {
                    // Alternate start codes (0xDD per-address priority, text, RDM) are not dimmer data.
                    CountIgnoredPacket();
                    return;
                }

                int sync = data.SyncAddress;
                if (sync != 0 && _syncSeen.TryGetValue(sync, out var seen) && now.Ticks - seen <= SyncTimeout.Ticks)
                {
                    data.Slots.CopyTo(stream.Pending);
                    stream.PendingLength = data.Slots.Length;
                    stream.PendingPriority = data.Priority;
                    stream.PendingSync = sync;
                    if (!stream.HasPending)
                    {
                        stream.HasPending = true;
                        _pending.Add(stream);
                    }
                }
                else
                {
                    if (stream.HasPending)
                    {
                        stream.HasPending = false;
                        _pending.Remove(stream);
                    }
                    _hub.Submit(stream.Source, universe, data.Slots, data.Priority, stream.Name);
                    if (sync != 0 && rt.Options.SacnJoinMulticast && !_joined.Contains(sync)) JoinLater(rt, sync);
                }
            }
        }
        finally
        {
            // Raised outside the lock: StatusChanged subscribers may read locked state.
            if (tableFull) AddWarning(StreamTableFullWarning);
        }
        CountPacket(now);
        CountUniversePacket(universe, now, label);
    }

    private void HandleSync(Runtime rt, ReadOnlySpan<byte> packet, IPv4Endpoint sender, DateTime now)
    {
        if (!SacnPacket.TryParseSync(packet, out var sync, out var error))
        {
            CountBadPacket(error!, sender);
            return;
        }
        lock (_gate)
        {
            if (!ReferenceEquals(rt, _runtime)) return;
            _syncSeen[sync.SyncAddress] = now.Ticks;
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                var s = _pending[i];
                if (s.PendingSync != sync.SyncAddress) continue;
                _hub.Submit(s.Source, s.Universe, s.Pending.AsSpan(0, s.PendingLength), s.PendingPriority, s.Name);
                s.HasPending = false;
                _pending.RemoveAt(i);
            }
        }
        CountPacket(now);
    }

    private void HandleDiscovery(ReadOnlySpan<byte> packet, IPv4Endpoint sender, DateTime now)
    {
        if (!SacnPacket.TryParseDiscovery(packet, out var discovery, out var error))
        {
            CountBadPacket(error!, sender);
            return;
        }
        CountPacket(now);
        lock (_gate)
        {
            if (!_discovered.TryGetValue(discovery!.Cid, out var d))
            {
                if (_discovered.Count > 1024) _discovered.Clear();
                d = new Discovery(discovery.Cid);
                _discovered[discovery.Cid] = d;
            }
            d.Name = discovery.SourceName;
            d.Address = AddressText(sender.Address);
            d.LastSeen = now;
            if (discovery.Page == 0)
            {
                // A new announcement cycle: forget pages beyond the new last page.
                foreach (var stale in d.Pages.Keys.Where(p => p > discovery.LastPage).ToArray()) d.Pages.Remove(stale);
            }
            d.Pages[discovery.Page] = discovery.Universes;
        }
    }

    /// <summary>Forgets streams idle for <see cref="StreamIdleTimeout"/> (called under <c>_gate</c> when the table is
    /// full; the hub has already expired their sources). Returns true when room was made.</summary>
    private bool PruneIdleStreams(DateTime now)
    {
        // A full table of live streams stays full for a while: scan it at most once a second, not per packet.
        if (now.Ticks < _nextPruneTicks) return false;
        long cutoff = now.Ticks - StreamIdleTimeout.Ticks;
        List<(Guid, int)>? idle = null;
        foreach (var (key, s) in _streams)
            if (s.LastSeenTicks < cutoff) (idle ??= new()).Add(key);
        if (idle is null)
        {
            _nextPruneTicks = now.Ticks + TimeSpan.TicksPerSecond;
            return false;
        }
        foreach (var key in idle)
        {
            var s = _streams[key];
            if (s.HasPending) _pending.Remove(s);
            _streams.Remove(key);
        }
        return true;
    }

    private bool Join(Runtime rt, int universe, out string? error)
    {
        bool ok = rt.Channel.TryJoinMulticast(SacnProtocol.MulticastGroup(universe), rt.JoinInterfaces, out error);
        if (ok)
        {
            lock (_gate) _joined.Add(universe);
        }
        return ok;
    }

    private void JoinLater(Runtime rt, int universe)
    {
        // Called under _gate: remember the attempt so it is made once, and join outside the packet path.
        _joined.Add(universe);
        ThreadPool.QueueUserWorkItem(_ =>
        {
            if (!rt.Channel.TryJoinMulticast(SacnProtocol.MulticastGroup(universe), rt.JoinInterfaces, out var error)
                && ReferenceEquals(rt, _runtime))
                AddWarning(ProtocolText.Create(ProtocolMessages.SacnSyncJoinFailed, universe, error));
        });
    }

    private string AddressText(uint address)
    {
        if (!_addressText.TryGetValue(address, out var text))
        {
            if (_addressText.Count > 4096) _addressText.Clear();
            text = new IPv4Endpoint(address, 0).AddressString;
            _addressText[address] = text;
        }
        return text;
    }

    private sealed class Runtime
    {
        public Runtime(UdpChannel channel, ProtocolOptions options, IReadOnlyList<IPAddress> joinInterfaces, HashSet<int> subscribed)
        {
            Channel = channel;
            Options = options;
            JoinInterfaces = joinInterfaces;
            Subscribed = subscribed;
        }

        public UdpChannel Channel { get; }
        public ProtocolOptions Options { get; }

        /// <summary>Local addresses multicast groups are joined on (the selected interface, or every up adapter).</summary>
        public IReadOnlyList<IPAddress> JoinInterfaces { get; }
        public HashSet<int> Subscribed { get; }
    }

    /// <summary>State of one (CID, universe) stream.</summary>
    private sealed class Stream
    {
        private readonly byte[] _nameField = new byte[64];

        public Stream(DmxSourceId source, int universe, string address)
        {
            Source = source;
            Universe = universe;
            Address = address;
            Label = address;
        }

        public DmxSourceId Source { get; }
        public int Universe { get; }
        public string Address { get; }
        public string Label { get; private set; }
        public string? Name { get; private set; }
        public byte LastSequence { get; set; }
        public long LastSeenTicks { get; set; }
        public byte[] Pending { get; } = new byte[512];
        public int PendingLength { get; set; }
        public int PendingPriority { get; set; }
        public int PendingSync { get; set; }
        public bool HasPending { get; set; }

        /// <summary>Decodes the source name only when the 64-byte field changed (normally once).</summary>
        public void UpdateName(ReadOnlySpan<byte> field)
        {
            if (Name is not null && field.SequenceEqual(_nameField)) return;
            field.CopyTo(_nameField);
            Name = SacnProtocol.ReadName(field);
            Label = Name.Length > 0 ? $"{Name} ({Address})" : Address;
        }
    }

    private sealed class Discovery
    {
        public Discovery(Guid cid) => Cid = cid;

        public Guid Cid { get; }
        public string Name { get; set; } = "";
        public string Address { get; set; } = "";
        public DateTime LastSeen { get; set; }
        public Dictionary<int, IReadOnlyList<int>> Pages { get; } = new();
    }
}
