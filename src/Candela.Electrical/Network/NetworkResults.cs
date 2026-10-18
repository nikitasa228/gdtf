using Candela.Core.Diagnostics;

namespace Candela.Electrical;

/// <summary>What kind of device an entity is for the network solver.</summary>
public enum DeviceKind
{
    /// <summary>A GDTF fixture (lights, hazers, dimmer packs, PSUs …).</summary>
    Fixture,

    /// <summary>A lighting console (data source).</summary>
    Console,

    /// <summary>A power source: wall outlet, distro, generator.</summary>
    PowerSource,
}

/// <summary>A device as the solver saw it.</summary>
public sealed record DeviceInfo
{
    /// <summary>Entity id.</summary>
    public required Guid EntityId { get; init; }

    /// <summary>Entity name.</summary>
    public required string Name { get; init; }

    /// <summary>Kind of device.</summary>
    public required DeviceKind Kind { get; init; }

    /// <summary>Console fixture id (MVR FixtureID) of a fixture, 0 otherwise.</summary>
    public int FixtureId { get; init; }

    /// <summary>Fixture profile (null for consoles, sources and unknown fixture types).</summary>
    public FixtureProfile? Profile { get; init; }

    /// <summary>The device's ports.</summary>
    public required IReadOnlyList<Port> Ports { get; init; }

    /// <summary>Label for messages: "#12 'Spot 12'" for fixtures with an id, "'Name'" otherwise.</summary>
    public string Label => Kind == DeviceKind.Fixture && FixtureId > 0 ? $"#{FixtureId} '{Name}'" : $"'{Name}'";

    /// <summary>Port by name (case-insensitive), or null.</summary>
    public Port? FindPort(string? name) =>
        name is null ? null : Ports.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>One plugged end of a cable, resolved against the device's ports.</summary>
public sealed record CableEndpointInfo
{
    /// <summary>Index in <c>CableComponent.Endpoints</c>.</summary>
    public required int Index { get; init; }

    /// <summary>Cable node the plug sits on.</summary>
    public required Guid NodeId { get; init; }

    /// <summary>Device entity.</summary>
    public required Guid EntityId { get; init; }

    /// <summary>Port name as stored.</summary>
    public required string PortName { get; init; }

    /// <summary>Device name (null when the entity no longer exists).</summary>
    public string? EntityName { get; init; }

    /// <summary>The resolved port (null when the device or port does not exist).</summary>
    public Port? Port { get; init; }

    /// <summary>Connector/signal check of the cable against the port (null when either is unknown).</summary>
    public ConnectorCheck? Check { get; init; }

    /// <summary>True when the endpoint's node exists in the cable.</summary>
    public bool NodeExists { get; init; }

    /// <summary>True when power/data can flow through this end (port and node exist, not an incompatible plug).</summary>
    public bool IsUsable => Port is not null && NodeExists && Check is not { Match: ConnectorMatch.Incompatible };

    /// <summary>The port reference.</summary>
    public PortRef PortRef => new(EntityId, PortName);
}

/// <summary>A cable as solved: type, geometry, lengths, weight and resolved endpoints.</summary>
public sealed class CableInfo
{
    internal CableInfo(Guid entityId, string name, string? label, string typeId, CableType? type, CableGeometry geometry,
        IReadOnlyList<CableEndpointInfo> endpoints)
    {
        EntityId = entityId;
        Name = name;
        Label = label;
        TypeId = typeId;
        Type = type;
        Geometry = geometry;
        Endpoints = endpoints;
    }

    /// <summary>Cable entity id.</summary>
    public Guid EntityId { get; }

    /// <summary>Entity name.</summary>
    public string Name { get; }

    /// <summary>Printed label ("U1-03"), or null.</summary>
    public string? Label { get; }

    /// <summary>Label for messages and tables: the label when set, else the entity name.</summary>
    public string DisplayLabel => string.IsNullOrWhiteSpace(Label) ? Name : Label!;

    /// <summary>Cable type id as stored.</summary>
    public string TypeId { get; }

    /// <summary>Catalogue type, or null when the id is unknown.</summary>
    public CableType? Type { get; }

    /// <summary>World geometry.</summary>
    public CableGeometry Geometry { get; }

    /// <summary>Cut length in metres (routed × slack).</summary>
    public float LengthM => Geometry.LengthM;

    /// <summary>Weight in kg (length × kg/m of the type; 0 for unknown types).</summary>
    public float WeightKg => Type is null ? 0f : LengthM * Type.WeightKgPerM;

    /// <summary>Plugged ends in endpoint order.</summary>
    public IReadOnlyList<CableEndpointInfo> Endpoints { get; }

    /// <summary>Endpoint on the supply side of a power cable (an outlet or link output), when determined.</summary>
    public int? UpstreamEndpointIndex { get; internal set; }

    /// <summary>Highest current in A on any circuit of a power cable (null for data cables / unpowered cables).</summary>
    public double? PeakCurrentA { get; internal set; }

    /// <summary>Power in W through the cable (null for data cables / unpowered cables).</summary>
    public double? LoadW { get; internal set; }

    /// <inheritdoc />
    public override string ToString() => $"{DisplayLabel} ({TypeId}, {LengthM:0.0} m)";
}

/// <summary>How one device input receives lighting data from a console output.</summary>
public sealed record DataFeed
{
    /// <summary>Device that receives the data.</summary>
    public required Guid DeviceEntityId { get; init; }

    /// <summary>Its input port.</summary>
    public required string InputPort { get; init; }

    /// <summary>Console the data comes from.</summary>
    public required Guid ConsoleEntityId { get; init; }

    /// <summary>Console output port.</summary>
    public required string ConsolePort { get; init; }

    /// <summary>DMX512 or Ethernet.</summary>
    public required SignalKind Signal { get; init; }

    /// <summary>First universe on the line (the only one for DMX).</summary>
    public required int FirstUniverse { get; init; }

    /// <summary>Universes on the line (1 for DMX).</summary>
    public required int UniverseCount { get; init; }

    /// <summary>1-based position from the console: 1 = first device after the console.</summary>
    public required int ChainIndex { get; init; }

    /// <summary>Device the data arrives from (null = straight from the console).</summary>
    public Guid? UpstreamEntityId { get; init; }

    /// <summary>Port the data leaves the upstream device (or console) through.</summary>
    public required string UpstreamPort { get; init; }

    /// <summary>Cable from the upstream device to this input.</summary>
    public required Guid CableEntityId { get; init; }

    /// <summary>All cables from the console to this input, in order.</summary>
    public required IReadOnlyList<Guid> CablePath { get; init; }

    /// <summary>
    /// DMX: cable length in metres since the start of the electrical DMX segment (console output or splitter);
    /// Ethernet: length of the last link.
    /// </summary>
    public required float RunLengthM { get; init; }

    /// <summary>True when <see cref="FirstUniverse"/> … + <see cref="UniverseCount"/> − 1 contains <paramref name="universe"/>.</summary>
    public bool Carries(int universe) => universe >= FirstUniverse && universe < FirstUniverse + Math.Max(1, UniverseCount);
}

/// <summary>Everything one console output drives, in cabling order (depth first).</summary>
public sealed record DataLine
{
    /// <summary>Console entity.</summary>
    public required Guid ConsoleEntityId { get; init; }

    /// <summary>Console name.</summary>
    public required string ConsoleName { get; init; }

    /// <summary>The output port.</summary>
    public required Port Output { get; init; }

    /// <summary>DMX512 or Ethernet.</summary>
    public SignalKind Signal => Output.Signal;

    /// <summary>First universe sent.</summary>
    public int FirstUniverse => Output.Universe ?? 1;

    /// <summary>Universes sent.</summary>
    public int UniverseCount => Math.Max(1, Output.UniverseCount);

    /// <summary>Devices reached, depth first from the console (a plain daisy chain is in chain order).</summary>
    public required IReadOnlyList<DataFeed> Feeds { get; init; }
}

/// <summary>How a device receives power.</summary>
public sealed record PowerFeed
{
    /// <summary>Powered device.</summary>
    public required Guid DeviceEntityId { get; init; }

    /// <summary>Its power input port.</summary>
    public required string InputPort { get; init; }

    /// <summary>Nearest power source (the distro's outlet for devices behind a distro).</summary>
    public required Guid SourceEntityId { get; init; }

    /// <summary>Outlet of that source.</summary>
    public required string OutletPort { get; init; }

    /// <summary>Circuit on the outlet (1; 1..6 on multicore outlets).</summary>
    public required int Circuit { get; init; }

    /// <summary>Phase (1..3) the device's load is on.</summary>
    public required int Phase { get; init; }

    /// <summary>Supply voltage at the device in V.</summary>
    public required float VoltageV { get; init; }

    /// <summary>Supply frequency in Hz (0 for DC).</summary>
    public required float FrequencyHz { get; init; }

    /// <summary>True when the device is supplied with DC (behind a DC power supply or a DC source).</summary>
    public bool IsDc { get; init; }

    /// <summary>True when the device hangs on a dimmed distribution output.</summary>
    public bool Dimmed { get; init; }

    /// <summary>Device the power arrives from (null = straight from the outlet).</summary>
    public Guid? UpstreamEntityId { get; init; }

    /// <summary>Port the power leaves the upstream device (or source) through.</summary>
    public required string UpstreamPort { get; init; }

    /// <summary>Cable from upstream to this device.</summary>
    public required Guid CableEntityId { get; init; }

    /// <summary>Devices between the outlet and this one (0 = plugged into the outlet).</summary>
    public required int Depth { get; init; }

    /// <summary>The device's own draw in W.</summary>
    public double OwnW { get; init; }

    /// <summary>Draw at the device's inlet in W: own draw plus everything linked or distributed behind it (with conversion losses).</summary>
    public double TotalW { get; init; }

    /// <summary>Current through the device's inlet in A.</summary>
    public double InputCurrentA { get; init; }
}

/// <summary>Load of one circuit of one outlet of a power source.</summary>
public sealed record PowerCircuit
{
    /// <summary>Source entity.</summary>
    public required Guid SourceEntityId { get; init; }

    /// <summary>Source name.</summary>
    public required string SourceName { get; init; }

    /// <summary>The outlet port.</summary>
    public required Port Outlet { get; init; }

    /// <summary>Circuit number on the outlet (1; 1..6 on multicore outlets).</summary>
    public required int Circuit { get; init; }

    /// <summary>Phase of the circuit (1..3); 0 for a three-phase outlet.</summary>
    public required int Phase { get; init; }

    /// <summary>Supply voltage (phase to neutral) in V.</summary>
    public required float VoltageV { get; init; }

    /// <summary>Supply frequency in Hz (0 = DC).</summary>
    public required float FrequencyHz { get; init; }

    /// <summary>Breaker rating of the circuit in A, or null.</summary>
    public float? BreakerA => Outlet.BreakerA;

    /// <summary>Active power in W.</summary>
    public required double LoadW { get; init; }

    /// <summary>Apparent power in VA (sum over phases).</summary>
    public required double ApparentPowerVA { get; init; }

    /// <summary>Current per phase L1, L2, L3 in A.</summary>
    public required IReadOnlyList<double> PhaseCurrentsA { get; init; }

    /// <summary>Highest phase current in A (the one the breaker sees).</summary>
    public double CurrentA => PhaseCurrentsA.Count == 0 ? 0 : PhaseCurrentsA.Max();

    /// <summary>Current as a percentage of the breaker rating, or null without a breaker.</summary>
    public double? PercentOfBreaker => BreakerA is { } b && b > 0 ? 100.0 * CurrentA / b : null;

    /// <summary>True when the current exceeds the breaker.</summary>
    public bool IsOverloaded => BreakerA is { } b && b > 0 && CurrentA > b;

    /// <summary>Devices on the circuit (not behind a distro fed from it), depth first.</summary>
    public required IReadOnlyList<PowerFeed> Feeds { get; init; }
}

/// <summary>Totals of one power source.</summary>
public sealed record PowerSourceLoad
{
    /// <summary>Source entity.</summary>
    public required Guid SourceEntityId { get; init; }

    /// <summary>Source name.</summary>
    public required string Name { get; init; }

    /// <summary>Phase voltage in V.</summary>
    public required float VoltageV { get; init; }

    /// <summary>Frequency in Hz (0 = DC).</summary>
    public required float FrequencyHz { get; init; }

    /// <summary>Number of phases.</summary>
    public required int Phases { get; init; }

    /// <summary>True when the source is a distro fed from another source (through its Mains In).</summary>
    public required bool IsFed { get; init; }

    /// <summary>True when the source has power (always for unfed roots; for fed distros: their feed is powered).</summary>
    public required bool IsPowered { get; init; }

    /// <summary>Total active power in W.</summary>
    public required double TotalW { get; init; }

    /// <summary>Current per phase L1, L2, L3 in A.</summary>
    public required IReadOnlyList<double> PhaseCurrentsA { get; init; }

    /// <summary>Circuits in outlet order.</summary>
    public required IReadOnlyList<PowerCircuit> Circuits { get; init; }
}

/// <summary>
/// Result of <see cref="NetworkSolver.Solve"/>: devices and their ports, cables, data lines (what each console output
/// drives), console-driven fixtures, power circuits and feeds, and the issues found. Immutable snapshot of the scene
/// at <see cref="SceneVersion"/>.
/// </summary>
public sealed class NetworkSolution
{
    private readonly Dictionary<Guid, CableInfo> _cablesById;
    private readonly Dictionary<PortRef, (CableInfo Cable, int Endpoint)> _plugged;
    private readonly HashSet<Guid> _consoleDriven;

    internal NetworkSolution(Guid sceneId, long sceneVersion, IReadOnlyDictionary<Guid, DeviceInfo> devices, IReadOnlyList<CableInfo> cables,
        Dictionary<PortRef, (CableInfo, int)> plugged, IReadOnlyList<DataLine> dataLines, IReadOnlyDictionary<Guid, IReadOnlyList<DataFeed>> dataFeeds,
        HashSet<Guid> consoleDriven, IReadOnlyList<PowerSourceLoad> sources, IReadOnlyDictionary<Guid, PowerFeed> powerFeeds,
        IReadOnlyList<Guid> unpowered, IReadOnlyList<Issue> issues)
    {
        SceneId = sceneId;
        SceneVersion = sceneVersion;
        Devices = devices;
        Cables = cables;
        _cablesById = cables.ToDictionary(c => c.EntityId);
        _plugged = plugged;
        DataLines = dataLines;
        DataFeedsByDevice = dataFeeds;
        _consoleDriven = consoleDriven;
        PowerSources = sources;
        PowerCircuits = sources.SelectMany(s => s.Circuits).ToList();
        PowerFeeds = powerFeeds;
        UnpoweredDevices = unpowered;
        Issues = issues;
    }

    /// <summary>Id of the solved scene.</summary>
    public Guid SceneId { get; }

    /// <summary><c>Scene.Version</c> at solve time.</summary>
    public long SceneVersion { get; }

    /// <summary>Fixtures, consoles and power sources by entity id.</summary>
    public IReadOnlyDictionary<Guid, DeviceInfo> Devices { get; }

    /// <summary>Every cable in scene order.</summary>
    public IReadOnlyList<CableInfo> Cables { get; }

    /// <summary>One line per console output, in scene and output order.</summary>
    public IReadOnlyList<DataLine> DataLines { get; }

    /// <summary>Data feeds per device (usually one).</summary>
    public IReadOnlyDictionary<Guid, IReadOnlyList<DataFeed>> DataFeedsByDevice { get; }

    /// <summary>Fixtures connected to a console output by data cables: their manual controls are locked.</summary>
    public IReadOnlySet<Guid> ConsoleDrivenFixtures => _consoleDriven;

    /// <summary>Power sources with their circuits.</summary>
    public IReadOnlyList<PowerSourceLoad> PowerSources { get; }

    /// <summary>All circuits of all sources.</summary>
    public IReadOnlyList<PowerCircuit> PowerCircuits { get; }

    /// <summary>How each powered device is fed.</summary>
    public IReadOnlyDictionary<Guid, PowerFeed> PowerFeeds { get; }

    /// <summary>Devices that need power and have none (fixtures and fed distros), in scene order.</summary>
    public IReadOnlyList<Guid> UnpoweredDevices { get; }

    /// <summary>Issues of the power and data networks and the cables.</summary>
    public IReadOnlyList<Issue> Issues { get; }

    /// <summary>True when the fixture is driven by a console (data path from a console output).</summary>
    public bool IsConsoleDriven(Guid fixtureEntityId) => _consoleDriven.Contains(fixtureEntityId);

    /// <summary>Cable by entity id, or null.</summary>
    public CableInfo? FindCable(Guid cableEntityId) => _cablesById.GetValueOrDefault(cableEntityId);

    /// <summary>The cable plugged into a port (and which endpoint), or null.</summary>
    public (CableInfo Cable, CableEndpointInfo Endpoint)? CableAt(Guid entityId, string portName)
    {
        if (portName is null || !_plugged.TryGetValue(new PortRef(entityId, portName), out var hit)) return null;
        return (hit.Cable, hit.Cable.Endpoints.First(e => e.Index == hit.Endpoint));
    }

    /// <summary>First data feed of a device, or null.</summary>
    public DataFeed? DataFeedOf(Guid deviceEntityId) =>
        DataFeedsByDevice.TryGetValue(deviceEntityId, out var list) && list.Count > 0 ? list[0] : null;

    /// <summary>Power feed of a device, or null.</summary>
    public PowerFeed? PowerFeedOf(Guid deviceEntityId) => PowerFeeds.GetValueOrDefault(deviceEntityId);
}
