using System.Net;
using Candela.Protocols.Networking;

namespace Candela.Protocols.Osc;

/// <summary>
/// Shared OSC-over-UDP plumbing: binds the port, parses messages and bundles, keeps a log of recent messages and raises
/// <see cref="MessageReceived"/> for every message so console integrations (ETC Eos, show control) can use the raw data.
/// </summary>
public abstract class OscReceiverBase : ExternalProtocolBase
{
    /// <summary>How many messages <see cref="RecentMessages"/> keeps.</summary>
    public const int RecentCapacity = 128;

    private readonly object _recentLock = new();
    private readonly OscReceivedMessage?[] _recent = new OscReceivedMessage?[RecentCapacity];
    private readonly List<OscMessage> _parsed = new();
    private readonly object _parseLock = new();
    private readonly Dictionary<IPv4Endpoint, string> _senderText = new();
    private int _recentNext;
    private int _recentCount;
    private volatile UdpChannel? _channel;

    /// <summary>Creates the receiver.</summary>
    protected OscReceiverBase(ProtocolDescriptor descriptor, TimeProvider? timeProvider)
        : base(descriptor, timeProvider)
    {
    }

    /// <summary>Raised on the receive thread for every message (bundle contents in order).</summary>
    public event EventHandler<OscMessageEventArgs>? MessageReceived;

    /// <summary>The local UDP endpoint while running, else null.</summary>
    public IPEndPoint? LocalEndPoint => _channel?.LocalEndPoint;

    /// <summary>The most recent messages, oldest first (at most <see cref="RecentCapacity"/>).</summary>
    public IReadOnlyList<OscReceivedMessage> RecentMessages
    {
        get
        {
            lock (_recentLock)
            {
                var list = new List<OscReceivedMessage>(_recentCount);
                int start = (_recentNext - _recentCount + RecentCapacity) % RecentCapacity;
                for (int i = 0; i < _recentCount; i++) list.Add(_recent[(start + i) % RecentCapacity]!);
                return list;
            }
        }
    }

    /// <summary>"OSC", "ETC Eos OSC" — used in messages.</summary>
    protected abstract string ProtocolLabel { get; }

    /// <summary>The UDP port to listen on, from the options.</summary>
    protected abstract int PortFrom(ProtocolOptions options);

    /// <summary>Called after the socket is open and before reception starts (validate options, set up state).</summary>
    protected abstract void OnConfigure(ProtocolOptions options);

    /// <summary>Called after the socket has been closed.</summary>
    protected abstract void OnClosed();

    /// <summary>Handles the messages of one datagram (called on the receive thread, serialised).</summary>
    protected abstract void OnMessages(IReadOnlyList<OscMessage> messages, IPv4Endpoint sender, string senderText, DateTime nowUtc);

    /// <inheritdoc />
    protected override string OnStart(ProtocolOptions options)
    {
        int port = PortFrom(options);
        ProtocolOptions.ValidatePort(port, $"{ProtocolLabel} port");
        var interfaces = NetworkInterfaceCatalog.Enumerate();
        var selected = NetworkInterfaceCatalog.Resolve(options.NetworkInterface, interfaces);
        OnConfigure(options);
        lock (_recentLock)
        {
            _recentCount = 0;
            _recentNext = 0;
            Array.Clear(_recent);
        }
        var channel = UdpChannel.Open(ProtocolLabel, UdpChannel.BindAddressFor(selected), port, enableBroadcast: false);
        _channel = channel;
        channel.Start($"Candela {ProtocolLabel} receiver", ProcessPacket,
            _ => CountBadPacket($"internal error while processing an {ProtocolLabel} packet", default),
            e => ReportFault($"{ProtocolLabel} socket failed: {e.Message}"));
        var where = selected is null ? "all interfaces" : $"{selected.Name} {selected.Address}";
        return $"Listening on UDP {channel.LocalEndPoint.Port} ({where})";
    }

    /// <inheritdoc />
    protected override void OnStop()
    {
        var channel = _channel;
        _channel = null;
        channel?.Dispose();
        OnClosed();
    }

    /// <summary>Processes one datagram as if received from <paramref name="sender"/> (receive thread; tests). Never
    /// throws for malformed input.</summary>
    internal void ProcessPacket(ReadOnlySpan<byte> packet, IPv4Endpoint sender)
    {
        var now = UtcNow;
        OscMessage[]? snapshot = null;
        string senderText;
        lock (_parseLock)
        {
            _parsed.Clear();
            if (!OscCodec.TryParse(packet, _parsed, out var error))
            {
                CountBadPacket(error!, sender);
                return;
            }
            CountPacket(now);
            if (!_senderText.TryGetValue(sender, out senderText!))
            {
                if (_senderText.Count > 1024) _senderText.Clear();
                senderText = sender.ToString();
                _senderText[sender] = senderText;
            }
            OnMessages(_parsed, sender, senderText, now);
            if (MessageReceived is not null) snapshot = _parsed.ToArray();
            Remember(_parsed, senderText, now);
        }
        if (snapshot is null) return;
        var handler = MessageReceived;
        foreach (var m in snapshot) handler?.Invoke(this, new OscMessageEventArgs(new OscReceivedMessage(m, senderText, now)));
    }

    private void Remember(List<OscMessage> messages, string sender, DateTime now)
    {
        lock (_recentLock)
        {
            foreach (var m in messages)
            {
                _recent[_recentNext] = new OscReceivedMessage(m, sender, now);
                _recentNext = (_recentNext + 1) % RecentCapacity;
                if (_recentCount < RecentCapacity) _recentCount++;
            }
        }
    }
}
