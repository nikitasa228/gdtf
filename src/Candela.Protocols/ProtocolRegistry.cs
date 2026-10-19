using Candela.Dmx.Input;
using Candela.Protocols.ArtNet;
using Candela.Protocols.Enttec;
using Candela.Protocols.Osc;
using Candela.Protocols.Psn;
using Candela.Protocols.Sacn;

namespace Candela.Protocols;

/// <summary>Stable ids of the external-control protocols (used in settings files and <see cref="ProtocolManager"/>).</summary>
public static class ProtocolIds
{
    /// <summary>Art-Net 4 (Artistic Licence), UDP 6454.</summary>
    public const string ArtNet = "artnet";

    /// <summary>BlackTrax real-time tracking (RTTrPM).</summary>
    public const string BlackTrax = "blacktrax";

    /// <summary>Controller Interface Transport Protocol (visualiser/media-server link).</summary>
    public const string Citp = "citp";

    /// <summary>Compulite Vector visualiser link.</summary>
    public const string CompuliteVc = "compulite-vc";

    /// <summary>ETC Eos family OSC output.</summary>
    public const string EtcEos = "etc-eos";

    /// <summary>EntTec DMX USB Pro widget (serial over USB).</summary>
    public const string EnttecDmxUsbPro = "enttec-usb-pro";

    /// <summary>Kinesys motion-control data.</summary>
    public const string Kinesys = "kinesys";

    /// <summary>LaserAnimation Sollinger Lasergraph DSP.</summary>
    public const string LasergraphDsp = "lasergraph-dsp";

    /// <summary>MA Lighting MA-Net2 (grandMA2).</summary>
    public const string MaNet2 = "ma-net2";

    /// <summary>MA Lighting MA-Net3 (grandMA3).</summary>
    public const string MaNet3 = "ma-net3";

    /// <summary>NewTek/Vizrt Network Device Interface video.</summary>
    public const string Ndi = "ndi";

    /// <summary>Open Sound Control over UDP.</summary>
    public const string Osc = "osc";

    /// <summary>Pangolin laser software (Beyond / LD2000).</summary>
    public const string PangolinLd = "pangolin-ld";

    /// <summary>PosiStageNet 2.0 position tracking.</summary>
    public const string PosiStageNet = "psn";

    /// <summary>Streaming ACN, ANSI E1.31-2018, UDP 5568.</summary>
    public const string Sacn = "sacn";
}

/// <summary>
/// Every protocol the external-control panel lists, in the order of the requirements:
/// ArtNet / BlackTrax / CITP / Compulite VC / ETC Eos / EntTec DMX USB Pro / Kinesys / LasergraphDSP / MA-Net2 /
/// MA-Net3 / NDI / OSC / PangolinLD / PosiStageNet / sACN — each with an honest support level.
/// </summary>
public static class ProtocolRegistry
{
    /// <summary>All 15 protocols, in display order.</summary>
    public static IReadOnlyList<ProtocolDescriptor> All { get; } = new ProtocolDescriptor[]
    {
        new(ProtocolIds.ArtNet, "Art-Net",
            "Art-Net 4 over UDP port 6454: ArtDmx (15-bit port-address), ArtPoll/ArtPollReply discovery so consoles list " +
            "Candela as a node, ArtSync and ArtTimeCode.",
            ProtocolSupportLevel.Implemented,
            "Receiver implemented: DMX from any Art-Net console (grandMA2/3, ETC Eos, Onyx, ChamSys, …), node discovery, " +
            "synchronous output and timecode."),
        new(ProtocolIds.BlackTrax, "BlackTrax",
            "BlackTrax real-time motion tracking (RTTrPM — Real-Time Tracking Protocol Motion, UDP).",
            ProtocolSupportLevel.Planned,
            "Planned: an RTTrPM receiver for tracked performer positions. Today, BlackTrax-driven fixtures can be " +
            "visualised through the console's Art-Net/sACN output, and PosiStageNet trackers are received."),
        new(ProtocolIds.Citp, "CITP",
            "Controller Interface Transport Protocol (PINF/SDMX/MSEX/CAEX): patch exchange, media thumbnails and DMX " +
            "between consoles, media servers and visualisers.",
            ProtocolSupportLevel.Planned,
            "Planned: CITP peer announcement, patch exchange (CAEX) and media-server thumbnails. DMX from the console " +
            "already arrives over Art-Net or sACN."),
        new(ProtocolIds.CompuliteVc, "Compulite VC",
            "Compulite Vector console visualiser connection.",
            ProtocolSupportLevel.Planned,
            "Planned: the Vector visualiser link is not publicly documented. Vector consoles output Art-Net and sACN, " +
            "which Candela receives today."),
        new(ProtocolIds.EtcEos, "ETC Eos",
            "ETC Eos family consoles (Eos, Ion, Gio, Element, Nomad) via their OSC output (UDP, default port 8001 here).",
            ProtocolSupportLevel.Partial,
            "Partial (via OSC): cue/show events from Eos OSC output (/eos/out/…) are received and exposed; levels come " +
            "from the console's Art-Net or sACN output. A native Eos patch/parameter sync is planned."),
        new(ProtocolIds.EnttecDmxUsbPro, "EntTec DMX USB Pro",
            "EntTec DMX USB Pro (and compatible) widget on a USB serial port: DMX512 input from a console's DMX cable.",
            ProtocolSupportLevel.Implemented,
            "Implemented: widget message framing (labels 3, 5, 6, 8, 9, 10), DMX input to one universe, parameters " +
            "and serial number."),
        new(ProtocolIds.Kinesys, "Kinesys",
            "Kinesys automation (K2/Vector) motion-control data: hoist and track positions for moving trusses and scenery.",
            ProtocolSupportLevel.Planned,
            "Planned: needs Kinesys' data-protocol documentation. PosiStageNet is received today for systems that " +
            "can output tracked positions."),
        new(ProtocolIds.LasergraphDsp, "LasergraphDSP",
            "LaserAnimation Sollinger Lasergraph DSP laser controllers.",
            ProtocolSupportLevel.Planned,
            "Planned: native laser frame/beam data. Lasers controlled over Art-Net/sACN/DMX are visualised today."),
        new(ProtocolIds.MaNet2, "MA-Net2",
            "MA Lighting's grandMA2 session network.",
            ProtocolSupportLevel.RequiresVendorSdk,
            "MA-Net2 is proprietary and needs MA Lighting's licensed SDK. Set the grandMA2 to output Art-Net or sACN " +
            "(Setup → Network Protocols) — Candela receives both."),
        new(ProtocolIds.MaNet3, "MA-Net3",
            "MA Lighting's grandMA3 session network.",
            ProtocolSupportLevel.RequiresVendorSdk,
            "MA-Net3 is proprietary and needs MA Lighting's licensed SDK. Set the grandMA3 to output Art-Net or sACN " +
            "(Menu → DMX Protocols) — Candela receives both."),
        new(ProtocolIds.Ndi, "NDI",
            "NDI (Network Device Interface) video streams, e.g. content for LED screens and projectors.",
            ProtocolSupportLevel.RequiresVendorSdk,
            "Needs the NDI SDK (proprietary licence and native runtime that cannot be bundled); planned as an optional " +
            "video-texture input."),
        new(ProtocolIds.Osc, "OSC",
            "Open Sound Control over UDP (default port 8000): messages and bundles, mapped to DMX with address patterns " +
            "such as /dmx/{universe}/{channel}.",
            ProtocolSupportLevel.Implemented,
            "Implemented: OSC 1.0/1.1 messages and bundles (i, f, s, b, h, d, t, T, F, N, I, …), configurable DMX " +
            "mapping, raw messages exposed for console integrations."),
        new(ProtocolIds.PangolinLd, "PangolinLD",
            "Pangolin laser software (Beyond, LD2000) laser show data.",
            ProtocolSupportLevel.Planned,
            "Planned: native laser frames. Pangolin-driven lasers controlled over Art-Net/DMX are visualised today."),
        new(ProtocolIds.PosiStageNet, "PosiStageNet",
            "PosiStageNet 2.0 position tracking (UDP multicast 236.10.10.10:56565): tracker positions, speed, " +
            "orientation, acceleration and target positions.",
            ProtocolSupportLevel.Implemented,
            "Implemented: info and data packets are received into a tracker table (for follow-spot and tracking " +
            "features)."),
        new(ProtocolIds.Sacn, "sACN",
            "Streaming ACN (ANSI E1.31-2018) over UDP port 5568: multicast 239.255.x.y per universe and unicast, with " +
            "source priority, sequence checking, stream termination, synchronisation and universe discovery.",
            ProtocolSupportLevel.Implemented,
            "Receiver implemented: DMX from any sACN console, priorities merged in the DMX input hub."),
    };

    /// <summary>The descriptor with <paramref name="id"/>, or null.</summary>
    public static ProtocolDescriptor? Find(string id)
    {
        foreach (var d in All)
            if (string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase)) return d;
        return null;
    }

    /// <summary>The descriptor with <paramref name="id"/>; throws <see cref="KeyNotFoundException"/> when unknown.</summary>
    public static ProtocolDescriptor Get(string id) =>
        Find(id) ?? throw new KeyNotFoundException($"Unknown external-control protocol '{id}'.");

    /// <summary>
    /// Creates the protocol with <paramref name="id"/>. Planned/SDK-only protocols become a <see cref="PlannedProtocol"/>
    /// that reports <see cref="ProtocolState.Unavailable"/> when started.
    /// </summary>
    /// <param name="id">Protocol id (see <see cref="ProtocolIds"/>).</param>
    /// <param name="hub">Hub that receives the DMX.</param>
    /// <param name="enttecTransportFactory">Opens the EntTec serial transport for a port name (tests pass a fake;
    /// default <see cref="SerialPortEnttecTransport"/>).</param>
    public static IExternalProtocol Create(string id, DmxInputHub hub, Func<string, IEnttecTransport>? enttecTransportFactory = null)
    {
        ArgumentNullException.ThrowIfNull(hub);
        var d = Get(id);
        return d.Id switch
        {
            ProtocolIds.ArtNet => new ArtNetReceiver(hub),
            ProtocolIds.Sacn => new SacnReceiver(hub),
            ProtocolIds.Osc => new OscReceiver(hub),
            ProtocolIds.EtcEos => new EtcEosReceiver(),
            ProtocolIds.PosiStageNet => new PsnReceiver(),
            ProtocolIds.EnttecDmxUsbPro => new EnttecDmxUsbPro(hub, enttecTransportFactory),
            _ => new PlannedProtocol(d),
        };
    }

    /// <summary>Creates all 15 protocols in display order.</summary>
    public static IReadOnlyList<IExternalProtocol> CreateAll(DmxInputHub hub, Func<string, IEnttecTransport>? enttecTransportFactory = null)
    {
        ArgumentNullException.ThrowIfNull(hub);
        var list = new IExternalProtocol[All.Count];
        for (int i = 0; i < list.Length; i++) list[i] = Create(All[i].Id, hub, enttecTransportFactory);
        return list;
    }
}

/// <summary>
/// A protocol that is listed but cannot run yet (planned or vendor-SDK-only). Starting it reports
/// <see cref="ProtocolState.Unavailable"/> with the support reason; it never throws.
/// </summary>
public sealed class PlannedProtocol : ExternalProtocolBase
{
    /// <summary>Creates the placeholder for <paramref name="descriptor"/>.</summary>
    public PlannedProtocol(ProtocolDescriptor descriptor)
        : base(descriptor)
    {
    }

    /// <inheritdoc />
    protected override string OnStart(ProtocolOptions options) =>
        throw new ProtocolStartException(ProtocolText.Create(ProtocolMessages.Unavailable, DisplayName, SupportReason), ProtocolErrorCode.Unavailable);

    /// <inheritdoc />
    protected override void OnStop()
    {
    }
}
