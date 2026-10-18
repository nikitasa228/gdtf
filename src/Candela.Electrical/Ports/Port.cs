using System.Numerics;

namespace Candela.Electrical;

/// <summary>
/// A connector on a device that a cable can be plugged into: a fixture's mains inlet or DMX thru, a console output,
/// a power source outlet. <see cref="Name"/> is what <c>CableEndpoint.PortName</c> stores; it is unique per device
/// (compared case-insensitively).
/// </summary>
public sealed record Port
{
    /// <summary>Name shown in the port picker and stored in cable endpoints ("Power In", "XLR IN", "DMX A").</summary>
    public required string Name { get; init; }

    /// <summary>Signal direction.</summary>
    public required PortDirection Direction { get; init; }

    /// <summary>What the port carries.</summary>
    public required SignalKind Signal { get; init; }

    /// <summary>The connector (resolved through <see cref="ConnectorCatalog"/>; unknown names keep their text).</summary>
    public required ConnectorInfo Connector { get; init; }

    /// <summary>Other connectors accepted by the same port (<c>_specs</c> "A / B" alternatives).</summary>
    public IReadOnlyList<ConnectorInfo> AlternativeConnectors { get; init; } = Array.Empty<ConnectorInfo>();

    /// <summary>What the port does inside its device (inlet, link, distribution output, outlet, data in/out …).</summary>
    public required PortRole Role { get; init; }

    /// <summary>Where the description came from.</summary>
    public required PortOrigin Origin { get; init; }

    /// <summary>
    /// Highest current in amperes that may flow through the port (power inlets and outputs): the GDTF fuse current or
    /// MaxPayLoad / Voltage, a distribution channel limit, a breaker rating, otherwise the connector rating
    /// (TRUE1 16 A, powerCON 20 A, …). Null when nothing is known.
    /// </summary>
    public float? MaxThroughCurrentA { get; init; }

    /// <summary>Highest power in W the port may deliver as a source (GDTF <c>MaxPayLoad</c>), or null.</summary>
    public float? MaxPayloadW { get; init; }

    /// <summary>First universe a console output sends (1-based), null for other ports.</summary>
    public int? Universe { get; init; }

    /// <summary>Number of universes a console output sends (1 for a DMX output, several on Ethernet).</summary>
    public int UniverseCount { get; init; }

    /// <summary>GDTF DMX break the port carries (1-based; 1 unless a legacy connector says otherwise).</summary>
    public int DmxBreak { get; init; } = 1;

    /// <summary>Breaker rating of a power source outlet in amperes (per circuit for multicore outlets), or null.</summary>
    public float? BreakerA { get; init; }

    /// <summary>Phase (1..3) of a power source outlet; 0 for every other port.</summary>
    public int Phase { get; init; }

    /// <summary>1-based channel number of a distribution output (dimmer channel, PSU output), 0 otherwise.</summary>
    public int Channel { get; init; }

    /// <summary>Position of the connector in the device's local space in metres (from GDTF geometry), when known.</summary>
    public Vector3? LocalPosition { get; init; }

    /// <summary>Connector name as written in the source data (GDTF / _specs / outlet), when different from the catalogue id.</summary>
    public string? ConnectorText { get; init; }

    /// <summary>Independent circuits of the connector (6 for Socapex/Harting).</summary>
    public int Circuits => Math.Max(1, Connector.Circuits);

    /// <summary>The connector followed by its alternatives.</summary>
    public IEnumerable<ConnectorInfo> AllConnectors()
    {
        yield return Connector;
        foreach (var c in AlternativeConnectors) yield return c;
    }

    /// <summary>True when the port takes power in (inlet, mains feed of a distro).</summary>
    public bool IsPowerInput => Role == PortRole.PowerIn;

    /// <summary>True when the port gives power out (outlet, link, distribution output).</summary>
    public bool IsPowerOutput => Role is PortRole.PowerLink or PortRole.PowerDistribution or PortRole.PowerOutlet;

    /// <summary>True when the port takes lighting data in (data input or bidirectional data port).</summary>
    public bool IsDataInput => Role is PortRole.DataIn or PortRole.DataInOut;

    /// <summary>True when the port sends lighting data on (data output, bidirectional data port, console output).</summary>
    public bool IsDataOutput => Role is PortRole.DataOut or PortRole.DataInOut or PortRole.ConsoleOutput;

    /// <inheritdoc />
    public override string ToString() => $"{Name} ({Direction} {Signal.DisplayName()}, {Connector.DisplayName})";
}

/// <summary>A port of a scene entity: the entity id plus the port name (compared case-insensitively).</summary>
/// <param name="EntityId">Device entity.</param>
/// <param name="PortName">Port name as stored in the cable endpoint.</param>
public readonly record struct PortRef(Guid EntityId, string PortName)
{
    /// <summary>Equality that ignores the case of <see cref="PortName"/>.</summary>
    public static IEqualityComparer<PortRef> Comparer { get; } = new PortRefComparer();

    /// <inheritdoc />
    public override string ToString() => $"{EntityId}:{PortName}";

    private sealed class PortRefComparer : IEqualityComparer<PortRef>
    {
        public bool Equals(PortRef x, PortRef y) =>
            x.EntityId == y.EntityId && string.Equals(x.PortName, y.PortName, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(PortRef obj) =>
            HashCode.Combine(obj.EntityId, StringComparer.OrdinalIgnoreCase.GetHashCode(obj.PortName ?? ""));
    }
}
