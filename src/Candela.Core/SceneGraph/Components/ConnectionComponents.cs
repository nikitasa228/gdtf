using System.Numerics;

namespace Candela.Core.SceneGraph.Components;

/// <summary>
/// A physical cable drawn in the scene. A cable is a tree of nodes: the user routes it point by point, and clicking
/// the middle of a segment adds a node from which a branch continues. Nodes can be anchored to entities (walls,
/// trusses, devices) so they follow them. Endpoints plug nodes into device ports.
/// Node positions are in the cable entity's local space (the cable entity normally sits at the world origin).
/// </summary>
public sealed class CableComponent : Component
{
    /// <summary>Id from the cable catalogue in Candela.Electrical (e.g. "dmx-xlr5", "powercon-true1", "cee16").</summary>
    public string CableTypeId { get; set; } = "";

    /// <summary>Label printed on the cable / shown in the patch ("U1-03").</summary>
    public string? Label { get; set; }

    public List<CableNode> Nodes { get; set; } = new();
    public List<CableSegment> Segments { get; set; } = new();
    public List<CableEndpoint> Endpoints { get; set; } = new();

    /// <summary>Extra length for sag and service loops, as a factor of the routed length (1.05 = +5 %).</summary>
    public float SlackFactor { get; set; } = 1.05f;

    public CableNode? FindNode(Guid id) => Nodes.Find(n => n.Id == id);
}

public sealed class CableNode
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Position in the cable entity's local space (metres). Ignored while <see cref="Anchor"/> is set.</summary>
    public Vector3 Position { get; set; }

    /// <summary>When set, the node follows this entity at the given local offset.</summary>
    public CableAnchor? Anchor { get; set; }
}

public readonly record struct CableAnchor(Guid EntityId, Vector3 LocalOffset);

/// <summary>A straight run between two nodes of the same cable.</summary>
public readonly record struct CableSegment(Guid FromNode, Guid ToNode);

/// <summary>A cable node plugged into a port of a device (port names come from GDTF wiring objects / the catalogue).</summary>
public sealed class CableEndpoint
{
    public Guid NodeId { get; set; }
    public Guid EntityId { get; set; }
    public string PortName { get; set; } = "";
}

/// <summary>
/// A lighting console in the scene. External protocol input (Art-Net, sACN, …) enters the scene through a console's
/// outputs; fixtures cabled to a console output are console-driven and their manual controls are locked.
/// </summary>
public sealed class ConsoleComponent : Component
{
    public string Model { get; set; } = "Generic console";

    /// <summary>Protocol whose input feeds this console ("artnet", "sacn", …); null = none (offline programming).</summary>
    public string? ProtocolId { get; set; } = "artnet";

    public List<ConsoleOutput> Outputs { get; set; } = new()
    {
        new ConsoleOutput { PortName = "DMX A", FirstUniverse = 1 },
        new ConsoleOutput { PortName = "DMX B", FirstUniverse = 2 },
        new ConsoleOutput { PortName = "DMX C", FirstUniverse = 3 },
        new ConsoleOutput { PortName = "DMX D", FirstUniverse = 4 },
    };
}

public sealed class ConsoleOutput
{
    public string PortName { get; set; } = "";

    /// <summary>"DMX512" for XLR outputs, "Ethernet" for network ports carrying several universes.</summary>
    public string Signal { get; set; } = "DMX512";

    public int FirstUniverse { get; set; } = 1;
    public int UniverseCount { get; set; } = 1;
}

/// <summary>Where power comes from: a wall outlet, a distro or a generator.</summary>
public sealed class PowerSourceComponent : Component
{
    public float VoltageV { get; set; } = 230f;
    public float FrequencyHz { get; set; } = 50f;
    public int Phases { get; set; } = 1;

    public List<PowerOutlet> Outlets { get; set; } = new()
    {
        new PowerOutlet { PortName = "Outlet 1", Connector = "Schuko", BreakerA = 16f },
    };
}

public sealed class PowerOutlet
{
    public string PortName { get; set; } = "";

    /// <summary>Connector name from the cable catalogue / _specs vocabulary ("Schuko", "CEE 16A", "powerCON TRUE1", …).</summary>
    public string Connector { get; set; } = "Schuko";

    /// <summary>Circuit breaker rating in amperes.</summary>
    public float BreakerA { get; set; } = 16f;

    /// <summary>Phase the outlet is on (1..3).</summary>
    public int Phase { get; set; } = 1;
}

/// <summary>
/// The box in which smoke/haze is simulated, centred on the entity (size in metres). Outside it only the scene's
/// base haze exists.
/// </summary>
public sealed class AtmosphereVolumeComponent : Component
{
    public Vector3 Size { get; set; } = new(20f, 20f, 10f);

    /// <summary>Simulation cell size in metres (smaller = finer and slower).</summary>
    public float CellSize { get; set; } = 0.25f;

    /// <summary>Fraction of smoke lost per second (evaporation / ventilation).</summary>
    public float Dissipation { get; set; } = 0.01f;

    /// <summary>Scattering coefficient in 1/m at smoke density 1.</summary>
    public float ScatteringAtFullDensity { get; set; } = 0.5f;

    /// <summary>Constant airflow in m/s (ventilation drafts).</summary>
    public Vector3 Airflow { get; set; }

    public bool Enabled { get; set; } = true;
}
