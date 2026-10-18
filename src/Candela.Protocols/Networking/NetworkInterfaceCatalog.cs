using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Candela.Protocols.Networking;

/// <summary>One IPv4 address of a network adapter, for the network-interface choice in the settings UI.</summary>
/// <param name="Id">Operating-system id of the adapter.</param>
/// <param name="Name">Adapter name ("eth0", "Ethernet 2").</param>
/// <param name="Description">Adapter description (driver/vendor text).</param>
/// <param name="Address">IPv4 address.</param>
/// <param name="SubnetMask">IPv4 subnet mask.</param>
/// <param name="Broadcast">Directed broadcast address (address | ~mask), used for Art-Net broadcast replies.</param>
/// <param name="IsUp">True when the adapter is operational.</param>
/// <param name="IsLoopback">True for the loopback adapter.</param>
/// <param name="MacAddress">Hardware address (6 bytes for Ethernet/Wi-Fi), empty when unknown.</param>
public sealed record NetworkInterfaceInfo(
    string Id,
    string Name,
    string Description,
    IPAddress Address,
    IPAddress SubnetMask,
    IPAddress Broadcast,
    bool IsUp,
    bool IsLoopback,
    IReadOnlyList<byte> MacAddress)
{
    /// <summary>The address in integer form.</summary>
    public uint AddressValue => IPv4Endpoint.ToUInt32(Address);

    /// <summary>The mask in integer form.</summary>
    public uint MaskValue => IPv4Endpoint.ToUInt32(SubnetMask);

    /// <summary>True when <paramref name="address"/> lies in this interface's subnet.</summary>
    public bool Contains(uint address) => (address & MaskValue) == (AddressValue & MaskValue);

    /// <summary>"eth0 — 192.168.1.20/24".</summary>
    public override string ToString() => $"{Name} — {Address}/{PrefixLength(MaskValue)}";

    private static int PrefixLength(uint mask) => System.Numerics.BitOperations.PopCount(mask);
}

/// <summary>Lists the IPv4 network interfaces and resolves the user's interface choice.</summary>
public static class NetworkInterfaceCatalog
{
    /// <summary>
    /// Every IPv4 address of every adapter (up adapters first, loopback last). Never throws: on platforms where the
    /// adapter list is unavailable an empty list is returned.
    /// </summary>
    public static IReadOnlyList<NetworkInterfaceInfo> Enumerate()
    {
        var list = new List<NetworkInterfaceInfo>();
        NetworkInterface[] adapters;
        try
        {
            adapters = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            return list;
        }
        catch (PlatformNotSupportedException)
        {
            return list;
        }

        foreach (var adapter in adapters)
        {
            try
            {
                var props = adapter.GetIPProperties();
                bool loopback = adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback;
                bool up = adapter.OperationalStatus is OperationalStatus.Up or OperationalStatus.Unknown;
                byte[] mac;
                try
                {
                    mac = adapter.GetPhysicalAddress().GetAddressBytes();
                }
                catch (NetworkInformationException)
                {
                    mac = Array.Empty<byte>();
                }
                foreach (var unicast in props.UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var mask = SafeMask(unicast, loopback);
                    uint ip = IPv4Endpoint.ToUInt32(unicast.Address);
                    uint m = IPv4Endpoint.ToUInt32(mask);
                    list.Add(new NetworkInterfaceInfo(
                        adapter.Id,
                        adapter.Name,
                        adapter.Description,
                        unicast.Address,
                        mask,
                        IPv4Endpoint.ToIPAddress(ip | ~m),
                        up,
                        loopback || unicast.Address.Equals(IPAddress.Loopback),
                        mac));
                }
            }
            catch (NetworkInformationException)
            {
                // One broken adapter must not hide the others.
            }
            catch (PlatformNotSupportedException)
            {
            }
        }
        list.Sort(static (a, b) =>
        {
            int c = b.IsUp.CompareTo(a.IsUp);
            if (c != 0) return c;
            c = a.IsLoopback.CompareTo(b.IsLoopback);
            return c != 0 ? c : string.CompareOrdinal(a.Name, b.Name);
        });
        return list;
    }

    /// <summary>True when <paramref name="selector"/> means "all interfaces" (empty, whitespace, "0.0.0.0", "any", "*").</summary>
    public static bool IsAny(string? selector)
    {
        if (string.IsNullOrWhiteSpace(selector)) return true;
        var s = selector.Trim();
        return s == "0.0.0.0" || s == "*" || s.Equals("any", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Finds the interface the user selected: an IPv4 address, or an adapter name, id or description
    /// (case-insensitive). Returns false when nothing matches. "All interfaces" selectors return true with a null
    /// <paramref name="match"/>.
    /// </summary>
    public static bool TryResolve(string? selector, IReadOnlyList<NetworkInterfaceInfo> interfaces, out NetworkInterfaceInfo? match)
    {
        ArgumentNullException.ThrowIfNull(interfaces);
        match = null;
        if (IsAny(selector)) return true;
        var s = selector!.Trim();
        if (IPAddress.TryParse(s, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork)
        {
            foreach (var i in interfaces)
                if (i.Address.Equals(ip))
                {
                    match = i;
                    return true;
                }
            if (IPAddress.IsLoopback(ip))
            {
                // Loopback always exists even when the adapter list does not show it (containers).
                match = new NetworkInterfaceInfo("lo", "loopback", "Loopback", ip, IPAddress.Parse("255.0.0.0"),
                    IPAddress.Parse("127.255.255.255"), true, true, Array.Empty<byte>());
                return true;
            }
            return false;
        }
        foreach (var i in interfaces)
            if (string.Equals(i.Name, s, StringComparison.OrdinalIgnoreCase) || string.Equals(i.Id, s, StringComparison.OrdinalIgnoreCase))
            {
                match = i;
                return true;
            }
        foreach (var i in interfaces)
            if (string.Equals(i.Description, s, StringComparison.OrdinalIgnoreCase))
            {
                match = i;
                return true;
            }
        return false;
    }

    /// <summary>Resolves the selector or throws <see cref="ProtocolStartException"/> listing what exists.</summary>
    internal static NetworkInterfaceInfo? Resolve(string? selector, IReadOnlyList<NetworkInterfaceInfo> interfaces)
    {
        if (TryResolve(selector, interfaces, out var match)) return match;
        var available = interfaces.Count == 0 ? "none found" : string.Join(", ", interfaces.Select(i => $"{i.Name} {i.Address}"));
        throw new ProtocolStartException(
            $"Network interface '{selector}' was not found (cable unplugged or adapter disabled?). Available: {available}.");
    }

    /// <summary>
    /// The local address Candela should announce to a peer: the selected interface, else the interface whose subnet
    /// contains <paramref name="peer"/>, else loopback for loopback peers, else the first up non-loopback address.
    /// </summary>
    internal static NetworkInterfaceInfo? LocalFor(uint peer, NetworkInterfaceInfo? selected, IReadOnlyList<NetworkInterfaceInfo> interfaces)
    {
        if (selected is not null) return selected;
        if (peer >> 24 == 127)
        {
            foreach (var i in interfaces)
                if (i.IsLoopback) return i;
            return null;
        }
        foreach (var i in interfaces)
            if (i.IsUp && !i.IsLoopback && i.Contains(peer)) return i;
        foreach (var i in interfaces)
            if (i.IsUp && !i.IsLoopback) return i;
        return null;
    }

    private static IPAddress SafeMask(UnicastIPAddressInformation unicast, bool loopback)
    {
        try
        {
            var mask = unicast.IPv4Mask;
            if (mask is not null && !mask.Equals(IPAddress.Any)) return mask;
        }
        catch (PlatformNotSupportedException)
        {
        }
        catch (NotImplementedException)
        {
        }
        int prefix = unicast.PrefixLength is > 0 and <= 32 ? unicast.PrefixLength : loopback ? 8 : 24;
        uint m = prefix == 0 ? 0 : uint.MaxValue << (32 - prefix);
        return IPv4Endpoint.ToIPAddress(m);
    }
}
