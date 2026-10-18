using Candela.Core.Settings;

namespace Candela.Protocols;

/// <summary>
/// Everything the external-control panel lets the user configure for the protocol receivers. Immutable; build a new
/// instance with <c>with</c> and pass it to <see cref="ProtocolManager.Apply"/> (or to a protocol's
/// <see cref="IExternalProtocol.Start"/>). Universe numbers are Candela's internal 1-based universes.
/// </summary>
public sealed record ProtocolOptions
{
    /// <summary>The universes announced/joined when nothing else is configured: internal universes 1–4.</summary>
    public static IReadOnlyList<int> DefaultUniverses { get; } = Array.AsReadOnly(new[] { 1, 2, 3, 4 });

    /// <summary>The OSC address patterns used when nothing else is configured.</summary>
    public static IReadOnlyList<string> DefaultOscPatterns { get; } = Array.AsReadOnly(new[] { "/dmx/{universe}/{channel}", "/dmx/{universe}" });

    /// <summary>
    /// Network interface for every network protocol: an IPv4 address ("192.168.1.20"), an adapter name or id
    /// ("eth0", "Ethernet 2"), or empty / "0.0.0.0" for all interfaces. An interface that does not exist is reported
    /// as an error status. Mirrors <see cref="AppSettings.NetworkInterface"/>.
    /// </summary>
    public string NetworkInterface { get; init; } = "";

    /// <summary>
    /// Internal universes Candela wants: announced in ArtPollReply (so consoles that unicast only to subscribed nodes
    /// send them) and joined as sACN multicast groups. The engine sets this to the universes used by the patch.
    /// </summary>
    public IReadOnlyList<int> SubscribedUniverses { get; init; } = DefaultUniverses;

    /// <summary>
    /// When true (default) data for universes outside <see cref="SubscribedUniverses"/> is still accepted (Art-Net
    /// broadcast, sACN unicast); when false it is counted as ignored.
    /// </summary>
    public bool AcceptUnsubscribedUniverses { get; init; } = true;

    /// <summary>Internal universe = Art-Net 15-bit port-address + this offset. 1 (default) matches grandMA, Eos, Onyx
    /// and most consoles ("Art-Net 0:0:0" is universe 1). Mirrors <see cref="AppSettings.ArtNetUniverseOffset"/>.</summary>
    public int ArtNetUniverseOffset { get; init; } = 1;

    /// <summary>Art-Net UDP port (6454 on real networks; another value only for tests). 0 = any free port.</summary>
    public int ArtNetPort { get; init; } = 6454;

    /// <summary>Also broadcast ArtPollReply (Art-Net 3 controllers listen for broadcast replies). Art-Net 4 replies
    /// are always unicast to the poller.</summary>
    public bool ArtNetBroadcastPollReply { get; init; }

    /// <summary>Honour ArtSync: once a controller sends ArtSync, its ArtDmx frames are held and released together on
    /// the next ArtSync (falls back to immediate output when ArtSync stops for 4 s).</summary>
    public bool ArtNetSync { get; init; } = true;

    /// <summary>Art-Net node short name (max 17 characters) shown in console node lists.</summary>
    public string NodeShortName { get; init; } = "Candela";

    /// <summary>Art-Net node long name (max 63 characters).</summary>
    public string NodeLongName { get; init; } = "Candela stage-lighting visualiser";

    /// <summary>Art-Net OEM code in ArtPollReply. 0xFFFF = no registered OEM code.</summary>
    public ushort ArtNetOemCode { get; init; } = 0xFFFF;

    /// <summary>ESTA manufacturer code in ArtPollReply. 0x7FF0 lies in ESTA's range reserved for prototypes.</summary>
    public ushort EstaManufacturerCode { get; init; } = 0x7FF0;

    /// <summary>sACN (E1.31) UDP port (5568 on real networks). 0 = any free port.</summary>
    public int SacnPort { get; init; } = 5568;

    /// <summary>Accept sACN packets flagged as preview data (normally ignored: they are meant for visualisers only when
    /// the console's blind/preview output is wanted).</summary>
    public bool SacnAcceptPreviewData { get; init; }

    /// <summary>Join the sACN multicast groups of <see cref="SubscribedUniverses"/> (and the discovery universe).
    /// Unicast sACN is accepted either way.</summary>
    public bool SacnJoinMulticast { get; init; } = true;

    /// <summary>UDP port of the generic OSC receiver.</summary>
    public int OscPort { get; init; } = 8000;

    /// <summary>
    /// OSC → DMX address patterns, e.g. "/dmx/{universe}/{channel}" (int 0..255 or float 0..1) or
    /// "/fader/{channel} universe=2 scale=percent". See <see cref="Osc.OscDmxPattern"/> for the syntax.
    /// </summary>
    public IReadOnlyList<string> OscPatterns { get; init; } = DefaultOscPatterns;

    /// <summary>UDP port on which Candela listens for ETC Eos OSC output ("OSC UDP TX port" in the Eos shell).</summary>
    public int EosOscPort { get; init; } = 8001;

    /// <summary>PosiStageNet multicast group.</summary>
    public string PsnMulticastGroup { get; init; } = "236.10.10.10";

    /// <summary>PosiStageNet UDP port.</summary>
    public int PsnPort { get; init; } = 56565;

    /// <summary>Serial port of the EntTec DMX USB Pro ("COM3", "/dev/ttyUSB0").</summary>
    public string EnttecSerialPort { get; init; } = "";

    /// <summary>Internal universe that receives the DMX input of the EntTec widget.</summary>
    public int EnttecUniverse { get; init; } = 1;

    /// <summary>Takes the network interface and Art-Net offset from the user's program settings.</summary>
    public static ProtocolOptions FromSettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new ProtocolOptions
        {
            NetworkInterface = settings.NetworkInterface ?? "",
            ArtNetUniverseOffset = settings.ArtNetUniverseOffset,
        };
    }

    /// <summary>Throws <see cref="ProtocolStartException"/> when <paramref name="port"/> is not a UDP port number.</summary>
    internal static void ValidatePort(int port, string what)
    {
        if (port is < 0 or > 65535)
            throw new ProtocolStartException($"{what} {port} is not a valid UDP port (0–65535).");
    }
}
