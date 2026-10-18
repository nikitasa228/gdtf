namespace Candela.Electrical;

/// <summary>
/// What a cable or a device port carries. Cable types use <see cref="Power"/>, <see cref="Dmx512"/>,
/// <see cref="Ethernet"/> and <see cref="Multicore"/>; ports can also be <see cref="Hybrid"/> (one connector carrying
/// power and data, e.g. Martin P3/PDE or Astera Datalink) or <see cref="Other"/> (video, audio, USB service ports).
/// </summary>
public enum SignalKind
{
    /// <summary>Mains or DC power on a single circuit.</summary>
    Power,

    /// <summary>DMX512 (RDM) on XLR or RJ45: one universe per line.</summary>
    Dmx512,

    /// <summary>Ethernet carrying Art-Net / sACN (any number of universes), or a proprietary network protocol.</summary>
    Ethernet,

    /// <summary>A multicore power cable carrying several independent circuits (Socapex, Harting).</summary>
    Multicore,

    /// <summary>A combined power + data connector (only on device ports).</summary>
    Hybrid,

    /// <summary>Anything that is neither power nor lighting data (video, audio, USB, clock).</summary>
    Other,
}

/// <summary>Which way a port carries its signal.</summary>
public enum PortDirection
{
    /// <summary>Receives power or data (mains inlet, DMX in).</summary>
    Input,

    /// <summary>Sends power or data (outlet, link/thru, console output).</summary>
    Output,

    /// <summary>Either way (Ethernet ports of devices with a built-in switch).</summary>
    Bidirectional,
}

/// <summary>What a port is for inside its device; the network solver routes power and data with it.</summary>
public enum PortRole
{
    /// <summary>A port the solver does not route through (video, USB, …).</summary>
    Other,

    /// <summary>Mains/DC inlet of a device.</summary>
    PowerIn,

    /// <summary>Power link (thru) output: the same circuit as the inlet, passed on to the next device.</summary>
    PowerLink,

    /// <summary>
    /// Output of a power-distribution device (dimmer channel, PSU output, P3 PowerPort output): the loads behind it are
    /// counted as their own devices and limited by the device's per-channel and total ratings.
    /// </summary>
    PowerDistribution,

    /// <summary>Outlet of a power source (wall outlet, distro, generator): the root of a power circuit.</summary>
    PowerOutlet,

    /// <summary>Data input of a device.</summary>
    DataIn,

    /// <summary>Data output (DMX thru, Ethernet out) of a device.</summary>
    DataOut,

    /// <summary>Bidirectional data port (switched Ethernet).</summary>
    DataInOut,

    /// <summary>A lighting console output: the root of a data line.</summary>
    ConsoleOutput,
}

/// <summary>Where the description of a port came from.</summary>
public enum PortOrigin
{
    /// <summary>A GDTF 1.2 WiringObject geometry.</summary>
    GdtfWiringObject,

    /// <summary>A GDTF 1.0/1.1 <c>PhysicalDescriptions/Connectors/Connector</c>.</summary>
    GdtfConnector,

    /// <summary>Researched connector fields of <c>_specs</c> (powerConnectorIn/Out, dataConnectors, controlSignal).</summary>
    Specs,

    /// <summary>A <c>ConsoleComponent</c> output.</summary>
    Console,

    /// <summary>A <c>PowerSourceComponent</c> outlet or its mains input.</summary>
    PowerSource,
}

/// <summary>Helpers on <see cref="SignalKind"/>.</summary>
public static class SignalKinds
{
    /// <summary>True for signals that deliver power (single circuit, multicore or hybrid).</summary>
    public static bool CarriesPower(this SignalKind signal) => signal is SignalKind.Power or SignalKind.Multicore or SignalKind.Hybrid;

    /// <summary>True for signals that carry lighting data (DMX512, Ethernet or hybrid).</summary>
    public static bool CarriesData(this SignalKind signal) => signal is SignalKind.Dmx512 or SignalKind.Ethernet or SignalKind.Hybrid;

    /// <summary>
    /// Whether a cable with signal <paramref name="cable"/> may be plugged into a port with signal <paramref name="port"/>:
    /// equal signals, a hybrid port with any power or data cable, and multicore cables with single power circuits
    /// (through a breakout) are accepted; everything else is the wrong cable.
    /// </summary>
    public static bool IsCompatible(SignalKind cable, SignalKind port) =>
        cable == port ||
        (port == SignalKind.Hybrid && cable is SignalKind.Power or SignalKind.Dmx512 or SignalKind.Ethernet) ||
        (cable, port) is (SignalKind.Multicore, SignalKind.Power) or (SignalKind.Power, SignalKind.Multicore);

    /// <summary>Human-readable name for messages ("DMX512", "power", …).</summary>
    public static string DisplayName(this SignalKind signal) => signal switch
    {
        SignalKind.Power => "power",
        SignalKind.Dmx512 => "DMX512",
        SignalKind.Ethernet => "Ethernet",
        SignalKind.Multicore => "multicore power",
        SignalKind.Hybrid => "power/data",
        _ => "other signal",
    };
}
