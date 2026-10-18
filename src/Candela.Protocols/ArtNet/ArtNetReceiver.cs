using System.Net;
using Candela.Dmx.Input;
using Candela.Protocols.Networking;

namespace Candela.Protocols.ArtNet;

/// <summary>Another Art-Net device seen on the network (from its ArtPollReply), for the external-control panel.</summary>
/// <param name="Sender">Address the reply came from.</param>
/// <param name="Reply">The parsed reply.</param>
/// <param name="LastSeenUtc">When the reply arrived.</param>
public sealed record ArtNetNode(string Sender, ArtPollReplyPacket Reply, DateTime LastSeenUtc);

/// <summary>
/// Art-Net 4 receiver. Listens on UDP 6454 (SO_REUSEADDR, all interfaces or the selected one), feeds ArtDmx into the
/// <see cref="DmxInputHub"/> (internal universe = port-address + <see cref="ProtocolOptions.ArtNetUniverseOffset"/>,
/// one hub source per sender IP + physical port), answers ArtPoll with ArtPollReply (one per four subscribed
/// universes) so consoles list Candela as a node, honours ArtSync and forwards ArtTimeCode to the hub. Every other
/// opcode is counted as ignored. The receive path does not allocate once each sender/universe has been seen.
/// </summary>
public sealed class ArtNetReceiver : ExternalProtocolBase
{
    /// <summary>Protocol name used in <see cref="DmxSourceId.Protocol"/>.</summary>
    public const string SourceProtocol = "artnet";

    /// <summary>After this long without ArtSync from a controller, its ArtDmx is output immediately again.</summary>
    public static readonly TimeSpan SyncTimeout = TimeSpan.FromSeconds(4);

    /// <summary>An output port shows "data present" in ArtPollReply when data arrived within this time.</summary>
    private static readonly TimeSpan DataPresentWindow = TimeSpan.FromSeconds(4);

    private static readonly TimeSpan NodeTimeout = TimeSpan.FromSeconds(30);

    private readonly DmxInputHub _hub;
    private readonly object _gate = new();
    private readonly Dictionary<ulong, Stream> _streams = new();
    private readonly List<Stream> _pending = new();
    private readonly Dictionary<uint, long> _syncTicks = new();
    private readonly Dictionary<uint, string> _addressText = new();
    private readonly Dictionary<(uint Ip, byte Bind), ArtNetNode> _nodes = new();
    private readonly byte[] _replyBuffer = new byte[ArtPollReplyPacket.Length];
    private volatile Runtime? _runtime;
    private int _replyCounter;

    /// <summary>Creates the receiver feeding <paramref name="hub"/>.</summary>
    public ArtNetReceiver(DmxInputHub hub, TimeProvider? timeProvider = null)
        : base(ProtocolRegistry.Get(ProtocolIds.ArtNet), timeProvider)
    {
        _hub = hub ?? throw new ArgumentNullException(nameof(hub));
    }

    /// <summary>The local UDP endpoint while running (port resolved when 0 was requested), else null.</summary>
    public IPEndPoint? LocalEndPoint => _runtime?.Channel.LocalEndPoint;

    /// <summary>Port-addresses announced in ArtPollReply, grouped per bind (BindIndex = position + 1).</summary>
    public IReadOnlyList<IReadOnlyList<int>> AnnouncedPortAddresses =>
        _runtime?.Binds.Select(b => (IReadOnlyList<int>)b.PortAddresses).ToArray() ?? Array.Empty<IReadOnlyList<int>>();

    /// <summary>Other Art-Net devices (consoles, nodes) that sent ArtPollReply within the last 30 s.</summary>
    public IReadOnlyList<ArtNetNode> Nodes
    {
        get
        {
            var cutoff = UtcNow - NodeTimeout;
            lock (_gate)
            {
                return _nodes.Values.Where(n => n.LastSeenUtc >= cutoff)
                    .OrderBy(n => n.Sender, StringComparer.Ordinal).ThenBy(n => n.Reply.BindIndex).ToArray();
            }
        }
    }

    /// <summary>Raised on the receive thread for every valid ArtTimeCode (after the hub has been updated).</summary>
    public event Action<ArtTimeCodePacket>? TimeCodeReceived;

    /// <inheritdoc />
    protected override string OnStart(ProtocolOptions options)
    {
        ProtocolOptions.ValidatePort(options.ArtNetPort, "Art-Net port");
        var interfaces = NetworkInterfaceCatalog.Enumerate();
        var selected = NetworkInterfaceCatalog.Resolve(options.NetworkInterface, interfaces);
        var subscribed = new HashSet<int>(options.SubscribedUniverses.Where(u => u >= 1));
        var binds = BuildBinds(subscribed, options.ArtNetUniverseOffset, out var bindWarning);
        if (bindWarning is not null) AddWarning(bindWarning);

        var channel = UdpChannel.Open("Art-Net", UdpChannel.BindAddressFor(selected), options.ArtNetPort, enableBroadcast: true);
        var rt = new Runtime(channel, options, selected, interfaces, subscribed, binds);
        lock (_gate)
        {
            _streams.Clear();
            _pending.Clear();
            _syncTicks.Clear();
            _nodes.Clear();
            _runtime = rt;
        }
        channel.Start("Candela Art-Net receiver", ProcessPacket, OnHandlerError, e => ReportFault($"Art-Net socket failed: {e.Message}"));
        var where = selected is null ? "all interfaces" : $"{selected.Name} {selected.Address}";
        return $"Listening on UDP {channel.LocalEndPoint.Port} ({where}); announcing {binds.Sum(b => b.PortAddresses.Length)} " +
               $"universe(s) as node '{options.NodeShortName}'";
    }

    /// <inheritdoc />
    protected internal override string OptionsSignature(ProtocolOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Signature(options.NetworkInterface, options.SubscribedUniverses, options.AcceptUnsubscribedUniverses,
            options.ArtNetUniverseOffset, options.ArtNetPort, options.ArtNetBroadcastPollReply, options.ArtNetSync,
            options.NodeShortName, options.NodeLongName, options.ArtNetOemCode, options.EstaManufacturerCode);
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
            _syncTicks.Clear();
        }
        rt?.Channel.Dispose();
        foreach (var s in streams) _hub.Terminate(s.Source, s.Universe);
    }

    /// <summary>
    /// Processes one datagram as if it had arrived from <paramref name="sender"/>. Used by the receive thread; tests call
    /// it directly to feed crafted or fuzzed packets. Never throws for malformed input.
    /// </summary>
    internal void ProcessPacket(ReadOnlySpan<byte> packet, IPv4Endpoint sender)
    {
        var rt = _runtime;
        if (rt is null) return;
        var now = UtcNow;
        if (!ArtNetProtocol.TryReadOpCode(packet, out var op))
        {
            CountBadPacket("not an Art-Net packet", sender);
            return;
        }
        switch (op)
        {
            case ArtNetOpCode.Dmx:
                HandleDmx(rt, packet, sender, now);
                break;
            case ArtNetOpCode.Sync:
                HandleSync(rt, packet, sender, now);
                break;
            case ArtNetOpCode.Poll:
                HandlePoll(rt, packet, sender, now);
                break;
            case ArtNetOpCode.TimeCode:
                HandleTimeCode(packet, sender, now);
                break;
            case ArtNetOpCode.PollReply:
                HandlePollReply(rt, packet, sender, now);
                break;
            default:
                CountIgnoredPacket();
                break;
        }
    }

    private void HandleDmx(Runtime rt, ReadOnlySpan<byte> packet, IPv4Endpoint sender, DateTime now)
    {
        if (!ArtDmxPacket.TryParse(packet, out var dmx, out var error))
        {
            CountBadPacket(error!, sender);
            return;
        }
        int universe = dmx.PortAddress + rt.Options.ArtNetUniverseOffset;
        if (universe < 1 || (!rt.Options.AcceptUnsubscribedUniverses && !rt.Subscribed.Contains(universe)))
        {
            CountIgnoredPacket();
            return;
        }
        string label;
        lock (_gate)
        {
            if (!ReferenceEquals(rt, _runtime)) return;
            ulong key = ((ulong)sender.Address << 24) | ((ulong)dmx.Physical << 16) | (uint)dmx.PortAddress;
            if (!_streams.TryGetValue(key, out var stream))
            {
                var ip = AddressText(sender.Address);
                stream = new Stream(new DmxSourceId(SourceProtocol, $"{ip}/{dmx.Physical}"), universe, sender.Address, ip, NodeName(sender.Address));
                _streams[key] = stream;
            }
            label = stream.Label;
            if (dmx.Sequence != 0 && stream.LastSequence != 0)
            {
                // Art-Net sequence rule (as in E1.31): a frame up to 19 steps behind the last one is late — drop it.
                int delta = (sbyte)(byte)(dmx.Sequence - stream.LastSequence);
                if (delta is < 0 and > -20)
                {
                    CountIgnoredPacket();
                    CountUniversePacket(universe, now, label, sequenceError: true);
                    return;
                }
            }
            stream.LastSequence = dmx.Sequence;

            if (rt.Options.ArtNetSync && _syncTicks.TryGetValue(sender.Address, out var syncTicks)
                                      && now.Ticks - syncTicks <= SyncTimeout.Ticks)
            {
                dmx.Data.CopyTo(stream.Pending);
                stream.PendingLength = dmx.Data.Length;
                if (!stream.HasPending)
                {
                    stream.HasPending = true;
                    _pending.Add(stream);
                }
            }
            else
            {
                if (stream.HasPending) DropPending(stream);
                _hub.Submit(stream.Source, universe, dmx.Data, DmxInputHub.DefaultPriority, stream.Name);
            }
        }
        CountPacket(now);
        CountUniversePacket(universe, now, label);
    }

    private void HandleSync(Runtime rt, ReadOnlySpan<byte> packet, IPv4Endpoint sender, DateTime now)
    {
        if (!ArtSyncPacket.TryParse(packet, out var error))
        {
            CountBadPacket(error!, sender);
            return;
        }
        if (!rt.Options.ArtNetSync)
        {
            CountIgnoredPacket();
            return;
        }
        lock (_gate)
        {
            if (!ReferenceEquals(rt, _runtime)) return;
            _syncTicks[sender.Address] = now.Ticks;
            // ArtSync from one controller releases only that controller's frames (Art-Net 4: ArtSync from another IP
            // than the ArtDmx sender is ignored).
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                var s = _pending[i];
                if (s.SenderAddress != sender.Address) continue;
                _hub.Submit(s.Source, s.Universe, s.Pending.AsSpan(0, s.PendingLength), DmxInputHub.DefaultPriority, s.Name);
                s.HasPending = false;
                _pending.RemoveAt(i);
            }
        }
        CountPacket(now);
    }

    private void HandleTimeCode(ReadOnlySpan<byte> packet, IPv4Endpoint sender, DateTime now)
    {
        if (!ArtTimeCodePacket.TryParse(packet, out var tc, out var error))
        {
            CountBadPacket(error!, sender);
            return;
        }
        CountPacket(now);
        _hub.SubmitTimecode(tc.ToTimecode());
        TimeCodeReceived?.Invoke(tc);
    }

    private void HandlePoll(Runtime rt, ReadOnlySpan<byte> packet, IPv4Endpoint sender, DateTime now)
    {
        if (!ArtPollPacket.TryParse(packet, out var poll, out var error))
        {
            CountBadPacket(error!, sender);
            return;
        }
        CountPacket(now);
        var local = NetworkInterfaceCatalog.LocalFor(sender.Address, rt.Selected, rt.Interfaces);
        var localIp = local?.Address ?? (sender.IsLoopback ? IPAddress.Loopback : IPAddress.Any);
        IReadOnlyList<byte> mac = local?.MacAddress is { Count: 6 } m ? m : new byte[6];
        int port = rt.Channel.LocalEndPoint.Port;
        var replyTo = new IPEndPoint(sender.ToIPAddress(), port);
        var replyToSource = sender.Port != port ? sender.ToIPEndPoint() : null;
        var broadcast = rt.Options.ArtNetBroadcastPollReply
            ? new IPEndPoint(local?.Broadcast ?? IPAddress.Broadcast, port)
            : null;
        var merging = MergingUniverses();

        for (int b = 0; b < rt.Binds.Count; b++)
        {
            var bind = rt.Binds[b];
            bool targeted = false;
            foreach (var pa in bind.PortAddresses) targeted |= poll.Targets(pa);
            if (!targeted) continue;

            var reply = BuildReply(rt, bind, (byte)(b + 1), localIp, mac, merging, now);
            int length;
            lock (_replyBuffer)
            {
                length = reply.Write(_replyBuffer);
                var datagram = _replyBuffer.AsSpan(0, length);
                rt.Channel.TrySend(datagram, replyTo);
                if (replyToSource is not null) rt.Channel.TrySend(datagram, replyToSource);
                if (broadcast is not null) rt.Channel.TrySend(datagram, broadcast);
            }
        }
    }

    private void HandlePollReply(Runtime rt, ReadOnlySpan<byte> packet, IPv4Endpoint sender, DateTime now)
    {
        if (!ArtPollReplyPacket.TryParse(packet, out var reply, out var error))
        {
            CountBadPacket(error!, sender);
            return;
        }
        if (rt.IsOwnReply(reply!, sender))
        {
            // Our own (broadcast or looped-back) reply: not another node.
            CountIgnoredPacket();
            return;
        }
        CountPacket(now);
        lock (_gate)
        {
            if (_nodes.Count > 4096) _nodes.Clear();
            _nodes[(sender.Address, reply!.BindIndex)] = new ArtNetNode(AddressText(sender.Address), reply, now);
            if (reply.ShortName.Length > 0)
            {
                foreach (var s in _streams.Values)
                    if (s.SenderAddress == sender.Address) s.Name = reply.ShortName;
            }
        }
    }

    private ArtPollReplyPacket BuildReply(Runtime rt, Bind bind, byte bindIndex, IPAddress localIp, IReadOnlyList<byte> mac,
        HashSet<int> merging, DateTime now)
    {
        var ports = new ArtPollReplyPort[bind.PortAddresses.Length];
        int receiving = 0;
        for (int i = 0; i < ports.Length; i++)
        {
            int pa = bind.PortAddresses[i];
            int universe = pa + rt.Options.ArtNetUniverseOffset;
            byte good = 0;
            if (LastPacketOf(universe) is { } last && now - last <= DataPresentWindow)
            {
                good |= 0x80;
                receiving++;
            }
            if (merging.Contains(universe)) good |= 0x08;
            if (_hub.MergeMode == DmxMergeMode.Ltp) good |= 0x02;
            ports[i] = new ArtPollReplyPort(ArtPollReplyPort.DmxOutputFromArtNet, 0x00, good, 0xC0, 0, (byte)(pa & 0x0F));
        }
        int counter = Interlocked.Increment(ref _replyCounter) % 10000;
        return new ArtPollReplyPacket
        {
            IpAddress = localIp,
            Port = ArtNetProtocol.DefaultPort,
            FirmwareVersion = 1,
            NetSwitch = (byte)((bind.PortAddresses[0] >> 8) & 0x7F),
            SubSwitch = (byte)((bind.PortAddresses[0] >> 4) & 0x0F),
            Oem = rt.Options.ArtNetOemCode,
            Status1 = 0xD0, // indicators normal, port-addresses set by "front panel" (Candela's settings)
            EstaManufacturer = rt.Options.EstaManufacturerCode,
            ShortName = rt.Options.NodeShortName,
            LongName = rt.Options.NodeLongName,
            NodeReport = $"#0001 [{counter:D4}] Candela OK, {receiving} of {ports.Length} port(s) receiving",
            NumPorts = ports.Length,
            Ports = ports,
            AcnPriority = DmxInputHub.DefaultPriority,
            Style = ArtPollReplyPacket.StyleNode,
            MacAddress = mac,
            BindIp = localIp,
            BindIndex = bindIndex,
            Status2 = 0x08, // 15-bit port-addresses (Art-Net 3/4)
            RefreshRate = 44,
        };
    }

    private HashSet<int> MergingUniverses()
    {
        var counts = new Dictionary<int, int>();
        foreach (var s in _hub.Sources())
            counts[s.Universe] = counts.TryGetValue(s.Universe, out var c) ? c + 1 : 1;
        var set = new HashSet<int>();
        foreach (var (u, c) in counts)
            if (c > 1) set.Add(u);
        return set;
    }

    private void DropPending(Stream stream)
    {
        stream.HasPending = false;
        _pending.Remove(stream);
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

    private string? NodeName(uint address)
    {
        foreach (var n in _nodes.Values)
            if (IPv4Endpoint.ToUInt32(n.Reply.IpAddress) == address && n.Reply.ShortName.Length > 0) return n.Reply.ShortName;
        return null;
    }

    private void OnHandlerError(Exception e) => CountBadPacket("internal error while processing an Art-Net packet", default);

    /// <summary>Groups port-addresses into ArtPollReply binds: up to four per bind, sharing Net and SubNet.</summary>
    internal static List<Bind> BuildBinds(IReadOnlyCollection<int> subscribedUniverses, int offset, out string? warning)
    {
        warning = null;
        var addresses = subscribedUniverses.Select(u => u - offset)
            .Where(pa => pa is >= 0 and <= ArtNetProtocol.MaxPortAddress)
            .Distinct().Order().ToList();
        if (addresses.Count < subscribedUniverses.Count)
            warning = $"{subscribedUniverses.Count - addresses.Count} subscribed universe(s) cannot be expressed as an Art-Net " +
                      $"port-address with offset {offset} and are not announced.";
        if (addresses.Count == 0)
        {
            addresses.Add(Math.Clamp(1 - offset, 0, ArtNetProtocol.MaxPortAddress));
        }
        var binds = new List<Bind>();
        var current = new List<int>(4);
        foreach (var pa in addresses)
        {
            if (current.Count == 4 || (current.Count > 0 && current[0] >> 4 != pa >> 4))
            {
                binds.Add(new Bind(current.ToArray()));
                current.Clear();
            }
            current.Add(pa);
        }
        if (current.Count > 0) binds.Add(new Bind(current.ToArray()));
        if (binds.Count > 255)
        {
            warning = $"Only the first 1020 of {addresses.Count} subscribed universes fit into 255 ArtPollReply binds.";
            binds.RemoveRange(255, binds.Count - 255);
        }
        return binds;
    }

    internal sealed record Bind(int[] PortAddresses);

    private sealed class Runtime
    {
        public Runtime(UdpChannel channel, ProtocolOptions options, NetworkInterfaceInfo? selected,
            IReadOnlyList<NetworkInterfaceInfo> interfaces, HashSet<int> subscribed, List<Bind> binds)
        {
            Channel = channel;
            Options = options;
            Selected = selected;
            Interfaces = interfaces;
            Subscribed = subscribed;
            Binds = binds;
        }

        public UdpChannel Channel { get; }
        public ProtocolOptions Options { get; }
        public NetworkInterfaceInfo? Selected { get; }
        public IReadOnlyList<NetworkInterfaceInfo> Interfaces { get; }
        public HashSet<int> Subscribed { get; }
        public List<Bind> Binds { get; }

        /// <summary>True when a reply came from one of this computer's addresses and carries our node name.</summary>
        public bool IsOwnReply(ArtPollReplyPacket reply, IPv4Endpoint sender)
        {
            if (reply.ShortName != Options.NodeShortName || reply.LongName != Options.NodeLongName) return false;
            if (sender.IsLoopback) return true;
            foreach (var i in Interfaces)
                if (i.AddressValue == sender.Address) return true;
            return false;
        }
    }

    /// <summary>State of one (sender IP, physical port, port-address) stream.</summary>
    private sealed class Stream
    {
        public Stream(DmxSourceId source, int universe, uint senderAddress, string label, string? name)
        {
            Source = source;
            Universe = universe;
            SenderAddress = senderAddress;
            Label = label;
            Name = name;
        }

        public DmxSourceId Source { get; }
        public int Universe { get; }
        public uint SenderAddress { get; }
        public string Label { get; }
        public string? Name { get; set; }
        public byte LastSequence { get; set; }
        public byte[] Pending { get; } = new byte[ArtNetProtocol.MaxDmxLength];
        public int PendingLength { get; set; }
        public bool HasPending { get; set; }
    }
}
