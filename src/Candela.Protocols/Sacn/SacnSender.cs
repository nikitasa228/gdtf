using System.Net;
using System.Net.Sockets;

namespace Candela.Protocols.Sacn;

/// <summary>
/// Sends E1.31 packets from one source (CID + name): data with per-universe sequence numbers, stream termination,
/// synchronisation and universe discovery — to the universe's multicast group or a unicast target. Used by tests and
/// test tools. Thread-safe; sending data does not allocate after the first packet of a universe.
/// </summary>
public sealed class SacnSender : IDisposable
{
    private readonly Socket _socket;
    private readonly byte[] _buffer = new byte[SacnProtocol.FullDataPacketLength];
    private readonly Dictionary<int, byte> _sequences = new();
    private readonly Dictionary<int, IPEndPoint> _groups = new();
    private readonly byte[] _nameField;
    private readonly object _lock = new();
    private byte _syncSequence;
    private bool _disposed;

    /// <summary>Creates the source.</summary>
    /// <param name="cid">Component identifier (keep it stable for a console/process).</param>
    /// <param name="sourceName">Name shown by receivers (max 63 UTF-8 bytes).</param>
    /// <param name="localAddress">Interface to send from (also used as the multicast interface); default any.</param>
    /// <param name="destinationPort">Port of the receivers (5568 normally).</param>
    public SacnSender(Guid cid, string sourceName, IPAddress? localAddress = null, int destinationPort = SacnProtocol.DefaultPort)
    {
        Cid = cid;
        SourceName = sourceName ?? throw new ArgumentNullException(nameof(sourceName));
        DestinationPort = destinationPort;
        _nameField = SacnProtocol.EncodeName(sourceName);
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            _socket.Bind(new IPEndPoint(localAddress ?? IPAddress.Any, 0));
            _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 16);
            if (localAddress is not null && !localAddress.Equals(IPAddress.Any))
                _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, localAddress.GetAddressBytes());
        }
        catch
        {
            _socket.Dispose();
            throw;
        }
    }

    /// <summary>Source CID.</summary>
    public Guid Cid { get; }

    /// <summary>Source name.</summary>
    public string SourceName { get; }

    /// <summary>Destination UDP port.</summary>
    public int DestinationPort { get; }

    /// <summary>Default priority of data packets (0..200).</summary>
    public byte Priority { get; set; } = SacnProtocol.DefaultPriority;

    /// <summary>Sends one universe of DMX data.</summary>
    /// <param name="universe">Universe 1..63999.</param>
    /// <param name="slots">Up to 512 slots.</param>
    /// <param name="target">Unicast receiver, or null for the universe's multicast group.</param>
    /// <param name="priority">Priority for this packet (default <see cref="Priority"/>).</param>
    /// <param name="options">Options bits (<see cref="SacnProtocol.OptionPreview"/>, …).</param>
    /// <param name="syncAddress">Synchronisation universe, 0 = none.</param>
    /// <param name="sequence">Explicit sequence number (tests); null = next in this universe's sequence.</param>
    /// <param name="startCode">DMX start code (0 = dimmer data).</param>
    public void SendData(int universe, ReadOnlySpan<byte> slots, IPEndPoint? target = null, byte? priority = null, byte options = 0,
        int syncAddress = 0, byte? sequence = null, byte startCode = 0)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            byte seq = sequence ?? NextSequence(universe);
            if (sequence is not null) _sequences[universe] = seq;
            int n = SacnPacket.WriteData(_buffer, Cid, _nameField, priority ?? Priority, syncAddress, seq, options, universe, slots, startCode);
            _socket.SendTo(_buffer.AsSpan(0, n), SocketFlags.None, target ?? Multicast(universe));
        }
    }

    /// <summary>Announces the end of a universe's stream: three packets with Stream_Terminated (E1.31 §6.7.1).</summary>
    public void SendTermination(int universe, IPEndPoint? target = null)
    {
        for (int i = 0; i < 3; i++)
            SendData(universe, ReadOnlySpan<byte>.Empty, target, options: SacnProtocol.OptionStreamTerminated);
    }

    /// <summary>Sends a synchronisation packet for <paramref name="syncAddress"/>.</summary>
    public void SendSync(int syncAddress, IPEndPoint? target = null)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            int n = SacnPacket.WriteSync(_buffer, Cid, ++_syncSequence, syncAddress);
            _socket.SendTo(_buffer.AsSpan(0, n), SocketFlags.None, target ?? Multicast(syncAddress));
        }
    }

    /// <summary>Sends universe-discovery pages listing <paramref name="universes"/> (512 per page).</summary>
    public void SendDiscovery(IReadOnlyCollection<int> universes, IPEndPoint? target = null)
    {
        ArgumentNullException.ThrowIfNull(universes);
        var sorted = universes.Distinct().Order().ToArray();
        int pages = Math.Max(1, (sorted.Length + 511) / 512);
        var buffer = new byte[120 + 2 * 512];
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            for (int page = 0; page < pages; page++)
            {
                var slice = sorted.AsSpan(Math.Min(page * 512, sorted.Length), Math.Min(512, Math.Max(0, sorted.Length - page * 512)));
                int n = SacnPacket.WriteDiscovery(buffer, Cid, SourceName, page, pages - 1, slice);
                _socket.SendTo(buffer.AsSpan(0, n), SocketFlags.None, target ?? Multicast(SacnProtocol.DiscoveryUniverse));
            }
        }
    }

    /// <summary>Closes the socket.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _socket.Dispose();
        }
    }

    private byte NextSequence(int universe)
    {
        _sequences.TryGetValue(universe, out var s);
        s = unchecked((byte)(s + 1));
        _sequences[universe] = s;
        return s;
    }

    private IPEndPoint Multicast(int universe)
    {
        if (!_groups.TryGetValue(universe, out var endPoint))
        {
            endPoint = new IPEndPoint(SacnProtocol.MulticastGroup(universe), DestinationPort);
            _groups[universe] = endPoint;
        }
        return endPoint;
    }
}
