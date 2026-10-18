using System.Net;
using System.Net.Sockets;

namespace Candela.Protocols.Networking;

/// <summary>Handles one received datagram. The span is only valid during the call.</summary>
internal delegate void DatagramHandler(ReadOnlySpan<byte> datagram, IPv4Endpoint sender);

/// <summary>
/// A bound IPv4 UDP socket with a dedicated receive thread. The loop is allocation-free in the steady state
/// (<see cref="Socket.ReceiveFrom(Span{byte}, SocketFlags, SocketAddress)"/> into a reused buffer and address), never
/// lets a handler exception kill the thread, and stops promptly when disposed.
/// </summary>
internal sealed class UdpChannel : IDisposable
{
    /// <summary>Largest UDP payload.</summary>
    public const int MaxDatagram = 65535;

    private const int SioUdpConnReset = unchecked((int)0x9800000C);
    private const int PollMicroseconds = 250_000;
    private const int LinuxMembershipsPerSocket = 20;

    private readonly Socket _socket;
    private readonly byte[] _buffer = new byte[MaxDatagram];
    private readonly SocketAddress _from = new(AddressFamily.InterNetwork);
    private readonly List<Socket> _membershipSockets = new();
    private readonly object _membershipLock = new();
    private readonly object _sendLock = new();
    private Thread? _thread;
    private volatile bool _closing;

    private UdpChannel(Socket socket) => _socket = socket;

    /// <summary>The local address and port actually bound (port is resolved when 0 was requested).</summary>
    public IPEndPoint LocalEndPoint => (IPEndPoint)_socket.LocalEndPoint!;

    /// <summary>The managed thread id of the receive loop (0 before <see cref="Start"/>).</summary>
    public int ReceiveThreadId => _thread?.ManagedThreadId ?? 0;

    /// <summary>
    /// Opens a socket with SO_REUSEADDR (so Candela can share a port with other lighting software on the same PC) and
    /// binds it. Throws <see cref="ProtocolStartException"/> with a user-facing message on failure.
    /// </summary>
    /// <param name="protocolName">"Art-Net", "sACN", … for messages.</param>
    /// <param name="bindAddress">Local address (Any or an interface address).</param>
    /// <param name="port">UDP port (0 = any free port).</param>
    /// <param name="enableBroadcast">Allow sending to broadcast addresses.</param>
    public static UdpChannel Open(string protocolName, IPAddress bindAddress, int port, bool enableBroadcast)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            if (enableBroadcast) socket.EnableBroadcast = true;
            if (OperatingSystem.IsWindows())
            {
                // Without this, an ICMP "port unreachable" for a reply we sent makes the next receive throw.
                socket.IOControl(SioUdpConnReset, new byte[4], null);
            }
            TrySetReceiveBuffer(socket, 1 << 20);
            socket.Bind(new IPEndPoint(bindAddress, port));
            return new UdpChannel(socket);
        }
        catch (SocketException e)
        {
            socket.Dispose();
            throw new ProtocolStartException(DescribeBindError(protocolName, bindAddress, port, e), e);
        }
        catch (Exception)
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The address receivers bind to. Linux and macOS deliver broadcast and multicast datagrams only to sockets bound
    /// to the wildcard address, so there the selected interface is used for replies and multicast membership while the
    /// socket binds to 0.0.0.0. Windows delivers them to interface-bound sockets, so it binds to the interface.
    /// </summary>
    public static IPAddress BindAddressFor(NetworkInterfaceInfo? selected) =>
        selected is not null && OperatingSystem.IsWindows() ? selected.Address : IPAddress.Any;

    /// <summary>Starts the receive thread. <paramref name="handler"/> runs on that thread; exceptions it throws are passed
    /// to <paramref name="onHandlerError"/> and the loop continues. <paramref name="onFault"/> is called once if the
    /// socket fails while not being closed.</summary>
    public void Start(string threadName, DatagramHandler handler, Action<Exception> onHandlerError, Action<Exception> onFault)
    {
        if (_thread is not null) throw new InvalidOperationException("The receive loop is already running.");
        _thread = new Thread(() => Loop(handler, onHandlerError, onFault))
        {
            IsBackground = true,
            Name = threadName,
        };
        _thread.Start();
    }

    /// <summary>
    /// Joins a multicast group on <paramref name="localInterface"/> (Any = the OS default route). On Linux, where each
    /// socket can hold only 20 memberships by default, further groups are joined through extra membership sockets;
    /// Linux delivers multicast for every joined group to wildcard-bound sockets of the port. Returns false with a reason
    /// instead of throwing.
    /// </summary>
    public bool TryJoinMulticast(IPAddress group, IPAddress localInterface, out string? error)
    {
        error = null;
        var option = new MulticastOption(group, localInterface);
        // Joins can come from the start-up thread and from the thread pool (late sACN synchronisation universes) while
        // Dispose runs: membership sockets are created and closed under one lock.
        lock (_membershipLock)
        {
            if (_closing)
            {
                error = "socket closed";
                return false;
            }
            try
            {
                _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, option);
                return true;
            }
            catch (SocketException e) when (OperatingSystem.IsLinux() && e.SocketErrorCode == SocketError.NoBufferSpaceAvailable)
            {
                return TryJoinThroughHelper(option, out error);
            }
            catch (SocketException e)
            {
                error = $"{e.SocketErrorCode}: {e.Message}";
                return false;
            }
            catch (ObjectDisposedException)
            {
                error = "socket closed";
                return false;
            }
        }
    }

    /// <summary>
    /// Joins <paramref name="group"/> on every address of <paramref name="localInterfaces"/> (one membership each), so
    /// a receiver listening on "all interfaces" gets multicast from the lighting network even when the operating
    /// system's default multicast route points at another adapter (Wi-Fi, office LAN) or does not exist (an isolated
    /// 2.x.x.x network without gateway). Returns true when at least one interface joined; <paramref name="error"/> is the
    /// first failure otherwise.
    /// </summary>
    public bool TryJoinMulticast(IPAddress group, IReadOnlyList<IPAddress> localInterfaces, out string? error)
    {
        ArgumentNullException.ThrowIfNull(localInterfaces);
        error = null;
        bool joined = false;
        string? firstError = null;
        foreach (var local in localInterfaces)
        {
            if (TryJoinMulticast(group, local, out var e)) joined = true;
            else firstError ??= localInterfaces.Count > 1 ? $"{local}: {e}" : e;
        }
        if (!joined) error = firstError ?? "no network interface to join on";
        return joined;
    }

    /// <summary>Sends a datagram; returns false (instead of throwing) when the network refuses it.</summary>
    public bool TrySend(ReadOnlySpan<byte> datagram, EndPoint destination)
    {
        try
        {
            lock (_sendLock) _socket.SendTo(datagram, SocketFlags.None, destination);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>Closes the socket(s) and waits briefly for the receive thread (never from the thread itself).</summary>
    public void Dispose()
    {
        lock (_membershipLock)
        {
            _closing = true;
            _socket.Dispose();
            foreach (var s in _membershipSockets) s.Dispose();
            _membershipSockets.Clear();
        }
        var t = _thread;
        if (t is not null && t.ManagedThreadId != Environment.CurrentManagedThreadId) t.Join(TimeSpan.FromSeconds(2));
    }

    private void Loop(DatagramHandler handler, Action<Exception> onHandlerError, Action<Exception> onFault)
    {
        while (!_closing)
        {
            int length;
            try
            {
                if (!_socket.Poll(PollMicroseconds, SelectMode.SelectRead)) continue;
                if (_closing) break;
                length = _socket.ReceiveFrom(_buffer.AsSpan(), SocketFlags.None, _from);
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException e) when (e.SocketErrorCode is SocketError.ConnectionReset or SocketError.MessageSize
                                                 or SocketError.NetworkReset or SocketError.Interrupted
                                                 or SocketError.ConnectionRefused or SocketError.HostUnreachable
                                                 or SocketError.NetworkUnreachable)
            {
                continue;
            }
            catch (SocketException e)
            {
                if (!_closing) onFault(e);
                break;
            }

            if (!IPv4Endpoint.TryFrom(_from, out var sender)) continue;
            try
            {
                handler(_buffer.AsSpan(0, length), sender);
            }
            catch (Exception e)
            {
                // One bad datagram must never stop reception.
                onHandlerError(e);
            }
        }
    }

    private bool TryJoinThroughHelper(MulticastOption option, out string? error)
    {
        error = null;
        foreach (var helper in _membershipSockets)
        {
            try
            {
                helper.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, option);
                return true;
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.NoBufferSpaceAvailable)
            {
            }
            catch (SocketException e)
            {
                error = $"{e.SocketErrorCode}: {e.Message}";
                return false;
            }
        }
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            socket.Bind(new IPEndPoint(IPAddress.Any, 0));
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, option);
            _membershipSockets.Add(socket);
            return true;
        }
        catch (SocketException e)
        {
            socket.Dispose();
            error = $"{e.SocketErrorCode}: {e.Message} (more than {LinuxMembershipsPerSocket} groups per socket)";
            return false;
        }
    }

    private static void TrySetReceiveBuffer(Socket socket, int bytes)
    {
        try
        {
            socket.ReceiveBufferSize = bytes;
        }
        catch (SocketException)
        {
            // The OS keeps its default; reception still works, bursts may drop sooner.
        }
    }

    private static string DescribeBindError(string protocol, IPAddress address, int port, SocketException e) => e.SocketErrorCode switch
    {
        SocketError.AddressAlreadyInUse or SocketError.AccessDenied =>
            $"{protocol}: UDP port {port} is already in use by another program on this computer (another visualiser, " +
            "console offline software or node software). Close it, or select a specific network interface.",
        SocketError.AddressNotAvailable =>
            $"{protocol}: the address {address} is not assigned to any network interface of this computer.",
        _ => $"{protocol}: cannot open UDP {address}:{port} ({e.SocketErrorCode}: {e.Message}).",
    };
}
