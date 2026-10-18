using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Candela.Dmx.Input;
using Candela.Protocols.Networking;

namespace Candela.Protocols.ArtNet;

/// <summary>
/// Sends Art-Net packets (ArtDmx, ArtTimeCode, ArtPoll, ArtSync) from its own UDP socket — the transmit half used by
/// tests and by the built-in <see cref="ArtNetTestConsole"/>. Sequence numbers are kept per port-address (1..255).
/// Thread-safe; packets are built in one reused buffer.
/// </summary>
public sealed class ArtNetSender : IDisposable
{
    private readonly Socket _socket;
    private readonly byte[] _buffer = new byte[ArtDmxPacket.HeaderLength + ArtNetProtocol.MaxDmxLength];
    private readonly byte[] _receiveBuffer = new byte[UdpChannel.MaxDatagram];
    private readonly byte[] _sequences = new byte[ArtNetProtocol.MaxPortAddress + 1];
    private readonly object _lock = new();
    private bool _disposed;

    /// <summary>Opens a broadcast-capable UDP socket.</summary>
    /// <param name="localAddress">Local address to send from (default: any).</param>
    /// <param name="localPort">Local port (default 0 = any free port; real consoles send from 6454).</param>
    public ArtNetSender(IPAddress? localAddress = null, int localPort = 0)
    {
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _socket.EnableBroadcast = true;
            _socket.Bind(new IPEndPoint(localAddress ?? IPAddress.Any, localPort));
        }
        catch
        {
            _socket.Dispose();
            throw;
        }
    }

    /// <summary>The local endpoint packets are sent from (replies to ArtPoll come back here).</summary>
    public IPEndPoint LocalEndPoint => (IPEndPoint)_socket.LocalEndPoint!;

    /// <summary>When true (default) ArtDmx carries an incrementing sequence 1..255 per port-address; false sends 0.</summary>
    public bool UseSequence { get; set; } = true;

    /// <summary>Sends one universe of DMX as ArtDmx.</summary>
    /// <param name="target">Node address (unicast) or a broadcast address, port 6454 normally.</param>
    /// <param name="portAddress">15-bit Art-Net port-address (0 = "0:0:0").</param>
    /// <param name="slots">1..512 DMX slots.</param>
    /// <param name="physical">Physical input port reported to the receiver.</param>
    public void SendDmx(IPEndPoint target, int portAddress, ReadOnlySpan<byte> slots, byte physical = 0)
    {
        ArgumentNullException.ThrowIfNull(target);
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            byte sequence = 0;
            if (UseSequence && portAddress is >= 0 and <= ArtNetProtocol.MaxPortAddress)
            {
                sequence = (byte)(_sequences[portAddress] % 255 + 1);
                _sequences[portAddress] = sequence;
            }
            int n = ArtDmxPacket.Write(_buffer, portAddress, slots, sequence, physical);
            _socket.SendTo(_buffer.AsSpan(0, n), SocketFlags.None, target);
        }
    }

    /// <summary>Sends ArtDmx with an explicit sequence number (tests of the out-of-order rule).</summary>
    public void SendDmx(IPEndPoint target, int portAddress, ReadOnlySpan<byte> slots, byte sequence, byte physical)
    {
        ArgumentNullException.ThrowIfNull(target);
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            int n = ArtDmxPacket.Write(_buffer, portAddress, slots, sequence, physical);
            _socket.SendTo(_buffer.AsSpan(0, n), SocketFlags.None, target);
        }
    }

    /// <summary>Sends an ArtTimeCode.</summary>
    public void SendTimeCode(IPEndPoint target, Timecode timecode, byte streamId = 0)
    {
        ArgumentNullException.ThrowIfNull(target);
        var packet = ArtTimeCodePacket.From(timecode, streamId);
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            int n = packet.Write(_buffer);
            _socket.SendTo(_buffer.AsSpan(0, n), SocketFlags.None, target);
        }
    }

    /// <summary>Sends an ArtPoll (default: reply-on-change flag, no targeting).</summary>
    public void SendPoll(IPEndPoint target, ArtPollPacket? poll = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        var packet = poll ?? new ArtPollPacket(ArtPollPacket.FlagReplyOnChange, 0, ArtNetProtocol.MaxPortAddress, 0);
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            int n = packet.Write(_buffer);
            _socket.SendTo(_buffer.AsSpan(0, n), SocketFlags.None, target);
        }
    }

    /// <summary>Sends an ArtSync (releases the frames sent since the previous ArtSync at the receivers).</summary>
    public void SendSync(IPEndPoint target)
    {
        ArgumentNullException.ThrowIfNull(target);
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            int n = ArtSyncPacket.Write(_buffer);
            _socket.SendTo(_buffer.AsSpan(0, n), SocketFlags.None, target);
        }
    }

    /// <summary>Sends an arbitrary datagram (tests).</summary>
    public void SendRaw(IPEndPoint target, ReadOnlySpan<byte> datagram)
    {
        ArgumentNullException.ThrowIfNull(target);
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _socket.SendTo(datagram, SocketFlags.None, target);
        }
    }

    /// <summary>
    /// Waits up to <paramref name="timeout"/> for ArtPollReply packets on this sender's socket (replies to
    /// <see cref="SendPoll"/>) and returns those that arrived. Other datagrams are skipped.
    /// </summary>
    public IReadOnlyList<ArtPollReplyPacket> ReceivePollReplies(TimeSpan timeout, int stopAfter = int.MaxValue)
    {
        var replies = new List<ArtPollReplyPacket>();
        var sw = Stopwatch.StartNew();
        EndPoint from = new IPEndPoint(IPAddress.Any, 0);
        while (sw.Elapsed < timeout && replies.Count < stopAfter)
        {
            int remainingUs = (int)Math.Max(1000, (timeout - sw.Elapsed).TotalMicroseconds);
            if (!_socket.Poll(remainingUs, SelectMode.SelectRead)) break;
            int n;
            try
            {
                n = _socket.ReceiveFrom(_receiveBuffer, ref from);
            }
            catch (SocketException)
            {
                continue;
            }
            if (ArtPollReplyPacket.TryParse(_receiveBuffer.AsSpan(0, n), out var reply, out _)) replies.Add(reply!);
        }
        return replies;
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
}
