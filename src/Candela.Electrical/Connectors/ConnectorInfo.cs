namespace Candela.Electrical;

/// <summary>Broad kind of a connector: what it can physically carry.</summary>
public enum ConnectorDomain
{
    /// <summary>Not recognised: no compatibility checks are made against it.</summary>
    Unknown,

    /// <summary>Single-circuit mains or DC power connector.</summary>
    Power,

    /// <summary>Multicore power connector carrying several circuits (Socapex, Harting).</summary>
    Multicore,

    /// <summary>DMX512 connector (XLR).</summary>
    Dmx,

    /// <summary>Network connector (RJ45, etherCON).</summary>
    Ethernet,

    /// <summary>Combined power and data connector (manufacturer specific).</summary>
    Hybrid,

    /// <summary>Video, audio or service connector.</summary>
    Other,

    /// <summary>
    /// A termination that accepts anything of the right signal: a fixed cable with an unspecified plug, a terminal
    /// block, loose ends. Never reported as a connector mismatch.
    /// </summary>
    Any,
}

/// <summary>Colour keying of Neutrik powerCON (legacy, 20 A): blue feeds a device, grey takes power out of it.</summary>
public enum PowerconKeying
{
    /// <summary>Not a keyed powerCON, or the colour is not known.</summary>
    None,

    /// <summary>Blue: power input (NAC3MPA chassis inlet, NAC3FCA cable connector).</summary>
    Blue,

    /// <summary>Grey: power output (NAC3MPB chassis outlet, NAC3FCB cable connector).</summary>
    Grey,
}

/// <summary>
/// One connector of the connector vocabulary shared by GDTF WiringObject <c>ConnectorType</c> names, the
/// <c>_specs</c> fields <c>powerConnectorIn</c>/<c>powerConnectorOut</c>/<c>dataConnectors</c>, power source outlets
/// and the cable catalogue. <see cref="Id"/> is the canonical name (the <c>_specs</c> spelling when one exists).
/// </summary>
public sealed record ConnectorInfo
{
    /// <summary>Canonical name, e.g. "powerCON TRUE1". For unrecognised connectors the name as written.</summary>
    public required string Id { get; init; }

    /// <summary>Name for the UI and messages.</summary>
    public required string DisplayName { get; init; }

    /// <summary>
    /// Mating family: connectors of one family plug into each other without an adapter (TRUE1 and TRUE1 TOP;
    /// RJ45 and etherCON; the three powerCON entries, subject to <see cref="Keying"/>).
    /// </summary>
    public required string Family { get; init; }

    /// <summary>What the connector carries.</summary>
    public required ConnectorDomain Domain { get; init; }

    /// <summary>Current rating per circuit in amperes (null for data connectors or when unknown).</summary>
    public float? RatedCurrentA { get; init; }

    /// <summary>Number of contacts (0 when not meaningful).</summary>
    public int Pins { get; init; }

    /// <summary>Independent power circuits carried (6 for Socapex/Harting multicores, otherwise 1).</summary>
    public int Circuits { get; init; } = 1;

    /// <summary>Phases of a power connector (3 for 5-pin CEE, otherwise 1).</summary>
    public int Phases { get; init; } = 1;

    /// <summary>powerCON colour keying.</summary>
    public PowerconKeying Keying { get; init; }

    /// <summary>True for a power connector that delivers DC (DC jacks, XLR4 DC, DIN3).</summary>
    public bool IsDc { get; init; }

    /// <summary>
    /// Not a pluggable port (internal battery): ports with this connector are not created.
    /// </summary>
    public bool IsInternal { get; init; }

    /// <summary>Key of the connector illustration shown in the properties panel ("connector-true1", …).</summary>
    public string IllustrationKey { get; init; } = "connector-unknown";

    /// <summary>Other spellings that resolve to this connector (GDTF names, part numbers, colloquial names).</summary>
    public IReadOnlyList<string> Aliases { get; init; } = Array.Empty<string>();

    /// <summary>True when the connector is part of the catalogue (false for names that were not recognised).</summary>
    public bool IsKnown => Domain != ConnectorDomain.Unknown;

    /// <summary>True for terminations that accept any plug of the right signal (see <see cref="ConnectorDomain.Any"/>).</summary>
    public bool IsWildcard => Domain == ConnectorDomain.Any || Family == ConnectorCatalog.GenericXlrFamily;

    /// <inheritdoc />
    public override string ToString() => DisplayName;
}
