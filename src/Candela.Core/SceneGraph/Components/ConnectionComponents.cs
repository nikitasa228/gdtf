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

    /// <summary>Points of the cable (a tree through <see cref="Segments"/>).</summary>
    public List<CableNode> Nodes { get; set; } = new();

    /// <summary>Straight runs between nodes of this cable.</summary>
    public List<CableSegment> Segments { get; set; } = new();

    /// <summary>Nodes plugged into device ports.</summary>
    public List<CableEndpoint> Endpoints { get; set; } = new();

    /// <summary>Extra length for sag and service loops, as a factor of the routed length (1.05 = +5 %).</summary>
    public float SlackFactor { get; set; } = 1.05f;

    /// <summary>The node with <paramref name="id"/>, or null.</summary>
    public CableNode? FindNode(Guid id) => Nodes.Find(n => n?.Id == id);

    /// <summary>
    /// Anchors and endpoints follow the remapper. A removed anchor releases its node where it is now (its world position
    /// expressed in the cable's space, when the original entities can be resolved); a removed endpoint leaves the node
    /// unplugged. With <see cref="EntityReferenceRemapper.RegenerateLocalIds"/>, node ids are renewed and segments and
    /// endpoints follow them; references to node ids the cable does not have are left as they are.
    /// </summary>
    protected internal override void RemapReferences(EntityReferenceRemapper remapper)
    {
        Dictionary<Guid, Guid>? nodeIds = null;
        if (remapper.RegenerateLocalIds && Nodes is { Count: > 0 })
        {
            nodeIds = new Dictionary<Guid, Guid>(Nodes.Count);
            foreach (var node in Nodes)
            {
                if (node is null) continue;
                var fresh = Guid.NewGuid();
                nodeIds.TryAdd(node.Id, fresh); // a repeated id keeps pointing at its first node, as in CableGeometry
                node.Id = fresh;
            }
        }

        if (Nodes is not null)
        {
            Matrix4x4? toCable = null;
            bool toCableResolved = false;
            foreach (var node in Nodes)
            {
                if (node?.Anchor is not { } anchor) continue;
                if (remapper.TryRemap(anchor.EntityId, out var entityId))
                {
                    if (entityId != anchor.EntityId) node.Anchor = anchor with { EntityId = entityId };
                    continue;
                }
                if (remapper.TryGetOriginalWorldMatrix(anchor.EntityId, out var anchorWorld))
                {
                    if (!toCableResolved)
                    {
                        toCable = OriginalWorldToCable(remapper);
                        toCableResolved = true;
                    }
                    if (toCable is { } m) node.Position = Vector3.Transform(Vector3.Transform(anchor.LocalOffset, anchorWorld), m);
                }
                node.Anchor = null;
            }
        }

        if (nodeIds is not null && Segments is not null)
        {
            for (int i = 0; i < Segments.Count; i++)
            {
                var s = Segments[i];
                Segments[i] = new CableSegment(nodeIds.GetValueOrDefault(s.FromNode, s.FromNode), nodeIds.GetValueOrDefault(s.ToNode, s.ToNode));
            }
        }

        if (Endpoints is not null)
        {
            int write = 0;
            for (int read = 0; read < Endpoints.Count; read++)
            {
                var ep = Endpoints[read];
                if (ep is not null)
                {
                    if (nodeIds is not null && nodeIds.TryGetValue(ep.NodeId, out var node)) ep.NodeId = node;
                    if (!remapper.TryRemap(ep.EntityId, out var device)) continue; // the device is not there: the end hangs free
                    ep.EntityId = device;
                }
                Endpoints[write++] = ep!;
            }
            if (write < Endpoints.Count) Endpoints.RemoveRange(write, Endpoints.Count - write);
        }
    }

    /// <summary>World → this cable's local space of the original cable entity, when it can be resolved.</summary>
    private Matrix4x4? OriginalWorldToCable(EntityReferenceRemapper remapper)
    {
        if (Owner is not { } owner) return null;
        if (!remapper.TryGetOriginalWorldMatrix(remapper.OriginalIdOf(owner.Id), out var cableWorld)) return null;
        return Matrix4x4.Invert(cableWorld, out var inverse) ? inverse : null;
    }
}

/// <summary>A point of a cable.</summary>
public sealed class CableNode
{
    /// <summary>Id of the node, unique within its cable.</summary>
    [LocalId]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Position in the cable entity's local space (metres). Ignored while <see cref="Anchor"/> is set (then it is the
    /// fallback used when the anchor entity is missing).
    /// </summary>
    public Vector3 Position { get; set; }

    /// <summary>When set, the node follows this entity at the given local offset.</summary>
    public CableAnchor? Anchor { get; set; }
}

/// <summary>Attachment of a cable node to an entity.</summary>
/// <param name="EntityId">The entity the node follows.</param>
/// <param name="LocalOffset">Offset in that entity's local space (metres).</param>
public readonly record struct CableAnchor([property: EntityReference] Guid EntityId, Vector3 LocalOffset);

/// <summary>A straight run between two nodes of the same cable.</summary>
/// <param name="FromNode">Id of the first node (<see cref="CableNode.Id"/>).</param>
/// <param name="ToNode">Id of the second node.</param>
public readonly record struct CableSegment([property: LocalId] Guid FromNode, [property: LocalId] Guid ToNode);

/// <summary>A cable node plugged into a port of a device (port names come from GDTF wiring objects / the catalogue).</summary>
public sealed class CableEndpoint
{
    /// <summary>The plugged node (<see cref="CableNode.Id"/> of the same cable).</summary>
    [LocalId]
    public Guid NodeId { get; set; }

    /// <summary>The device entity the node is plugged into.</summary>
    [EntityReference]
    public Guid EntityId { get; set; }

    /// <summary>Port name on the device.</summary>
    public string PortName { get; set; } = "";
}

/// <summary>
/// A lighting console in the scene. External protocol input (Art-Net, sACN, …) enters the scene through a console's
/// outputs; fixtures cabled to a console output are console-driven and their manual controls are locked.
/// </summary>
public sealed class ConsoleComponent : Component
{
    /// <summary>Console model shown in the UI and reports (e.g. "grandMA3 light").</summary>
    public string Model { get; set; } = "Generic console";

    /// <summary>Protocol whose input feeds this console ("artnet", "sacn", …); null = none (offline programming).</summary>
    public string? ProtocolId { get; set; } = "artnet";

    /// <summary>Physical outputs (DMX ports, network ports) and the universes they carry.</summary>
    public List<ConsoleOutput> Outputs { get; set; } = new()
    {
        new ConsoleOutput { PortName = "DMX A", FirstUniverse = 1 },
        new ConsoleOutput { PortName = "DMX B", FirstUniverse = 2 },
        new ConsoleOutput { PortName = "DMX C", FirstUniverse = 3 },
        new ConsoleOutput { PortName = "DMX D", FirstUniverse = 4 },
    };
}

/// <summary>One output port of a <see cref="ConsoleComponent"/>.</summary>
public sealed class ConsoleOutput
{
    /// <summary>Port name cables plug into (<see cref="CableEndpoint.PortName"/>), e.g. "DMX A".</summary>
    public string PortName { get; set; } = "";

    /// <summary>"DMX512" for XLR outputs, "Ethernet" for network ports carrying several universes.</summary>
    public string Signal { get; set; } = "DMX512";

    /// <summary>First universe carried by the port (1-based, as shown on consoles).</summary>
    public int FirstUniverse { get; set; } = 1;

    /// <summary>Number of consecutive universes carried (1 for an XLR DMX port).</summary>
    public int UniverseCount { get; set; } = 1;
}

/// <summary>Where power comes from: a wall outlet, a distro or a generator.</summary>
public sealed class PowerSourceComponent : Component
{
    /// <summary>Nominal phase-to-neutral voltage in volts.</summary>
    public float VoltageV { get; set; } = 230f;

    /// <summary>Mains frequency in hertz.</summary>
    public float FrequencyHz { get; set; } = 50f;

    /// <summary>Number of phases supplied (1 or 3).</summary>
    public int Phases { get; set; } = 1;

    /// <summary>The outlets cables plug into.</summary>
    public List<PowerOutlet> Outlets { get; set; } = new()
    {
        new PowerOutlet { PortName = "Outlet 1", Connector = "Schuko", BreakerA = 16f },
    };
}

/// <summary>One outlet of a <see cref="PowerSourceComponent"/>.</summary>
public sealed class PowerOutlet
{
    /// <summary>Port name cables plug into (<see cref="CableEndpoint.PortName"/>), e.g. "Outlet 1".</summary>
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
    /// <summary>Extent of the box in metres along the entity's local X, Y and Z, centred on the entity.</summary>
    public Vector3 Size { get; set; } = new(20f, 20f, 10f);

    /// <summary>Simulation cell size in metres (smaller = finer and slower).</summary>
    public float CellSize { get; set; } = 0.25f;

    /// <summary>Fraction of smoke lost per second (evaporation / ventilation).</summary>
    public float Dissipation { get; set; } = 0.01f;

    /// <summary>Scattering coefficient in 1/m at smoke density 1.</summary>
    public float ScatteringAtFullDensity { get; set; } = 0.5f;

    /// <summary>Constant airflow in m/s (ventilation drafts).</summary>
    public Vector3 Airflow { get; set; }

    /// <summary>False pauses the simulation of this volume (only the base haze remains).</summary>
    public bool Enabled { get; set; } = true;
}
