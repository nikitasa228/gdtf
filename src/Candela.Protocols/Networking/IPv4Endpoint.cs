using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace Candela.Protocols.Networking;

/// <summary>
/// An IPv4 address and UDP port as plain integers, so receive loops can identify senders without allocating
/// <see cref="IPEndPoint"/> objects per packet.
/// </summary>
/// <param name="Address">The IPv4 address with the first octet in the most significant byte (192.168.1.2 = 0xC0A80102).</param>
/// <param name="Port">UDP port, 0–65535.</param>
public readonly record struct IPv4Endpoint(uint Address, int Port)
{
    /// <summary>True for 127.0.0.0/8.</summary>
    public bool IsLoopback => Address >> 24 == 127;

    /// <summary>The address as dotted decimal ("192.168.1.2"); allocates.</summary>
    public string AddressString => $"{Address >> 24}.{(Address >> 16) & 0xFF}.{(Address >> 8) & 0xFF}.{Address & 0xFF}";

    /// <summary>Converts an <see cref="IPAddress"/> (must be IPv4) to the integer form.</summary>
    public static uint ToUInt32(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
            else throw new ArgumentException("Only IPv4 addresses are supported.", nameof(address));
        }
        Span<byte> bytes = stackalloc byte[4];
        address.TryWriteBytes(bytes, out _);
        return BinaryPrimitives.ReadUInt32BigEndian(bytes);
    }

    /// <summary>Converts the integer form back to an <see cref="IPAddress"/>.</summary>
    public static IPAddress ToIPAddress(uint address)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, address);
        return new IPAddress(bytes);
    }

    /// <summary>Creates the endpoint from an <see cref="IPEndPoint"/>.</summary>
    public static IPv4Endpoint From(IPEndPoint endPoint)
    {
        ArgumentNullException.ThrowIfNull(endPoint);
        return new IPv4Endpoint(ToUInt32(endPoint.Address), endPoint.Port);
    }

    /// <summary>Reads an IPv4 sender from a native socket address filled by <c>Socket.ReceiveFrom</c>. Returns false
    /// for other address families. Allocation-free.</summary>
    public static bool TryFrom(SocketAddress socketAddress, out IPv4Endpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(socketAddress);
        endpoint = default;
        if (socketAddress.Family != AddressFamily.InterNetwork || socketAddress.Size < 8) return false;
        // sockaddr_in on Windows, Linux and the BSDs: port (big-endian) at offset 2, address at offset 4.
        var bytes = socketAddress.Buffer.Span;
        endpoint = new IPv4Endpoint(BinaryPrimitives.ReadUInt32BigEndian(bytes[4..]), BinaryPrimitives.ReadUInt16BigEndian(bytes[2..]));
        return true;
    }

    /// <summary>The address as an <see cref="IPAddress"/>; allocates.</summary>
    public IPAddress ToIPAddress() => ToIPAddress(Address);

    /// <summary>The endpoint as an <see cref="IPEndPoint"/>; allocates.</summary>
    public IPEndPoint ToIPEndPoint() => new(ToIPAddress(Address), Port);

    /// <summary>"a.b.c.d:port".</summary>
    public override string ToString() => $"{AddressString}:{Port}";
}
