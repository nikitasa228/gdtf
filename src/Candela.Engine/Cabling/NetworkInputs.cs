using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;

namespace Candela.Engine;

/// <summary>
/// What a network solution was solved from, as far as an in-place edit of a fixture or cable component can change it:
/// a fixture's GDTF, mode, patch and fixture id, and a cable's type, label, nodes (ids and anchor entities), segments and
/// plugs. With it the engine tells an edit that changes the cabling from one that does not — a manual attribute or colour
/// of a fixture, set on every frame of a slider drag — and from one that only moves cable nodes (a node dragged along,
/// its anchor offset, the slack): only the first needs a new solve, the last renews the geometry of that one cable.
/// </summary>
/// <remarks>
/// Recorded after every full solve (linear in the fixtures and the cables' nodes, like the solve); the comparisons do
/// not allocate. A component that was not recorded (added since) never matches. Main thread only, like the scene.
/// </remarks>
internal sealed class NetworkInputs
{
    private readonly Dictionary<FixtureComponent, FixtureKey> _fixtures = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<CableComponent, CableShape> _cables = new(ReferenceEqualityComparer.Instance);

    /// <summary>Number of recorded fixtures and cables.</summary>
    public int Count => _fixtures.Count + _cables.Count;

    /// <summary>Forgets everything (no solution, or the scene was replaced).</summary>
    public void Clear()
    {
        _fixtures.Clear();
        _cables.Clear();
    }

    /// <summary>Records the scene's fixtures and cables as they are now (call right after a full solve of it).</summary>
    public void Record(Scene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        Clear();
        foreach (var (_, fixture) in scene.Query<FixtureComponent>()) _fixtures[fixture] = FixtureKey.Of(fixture);
        foreach (var (_, cable) in scene.Query<CableComponent>()) _cables[cable] = CableShape.Of(cable);
    }

    /// <summary>True when nothing of <paramref name="fixture"/> the cabling depends on changed since it was recorded.</summary>
    public bool IsUnchanged(FixtureComponent fixture) => _fixtures.TryGetValue(fixture, out var key) && key.Matches(fixture);

    /// <summary>
    /// True when <paramref name="cable"/> is still made of the same nodes, anchored to the same entities, joined by the
    /// same segments and plugged into the same ports as when it was recorded: at most its geometry changed.
    /// </summary>
    public bool HasSameStructure(CableComponent cable) => _cables.TryGetValue(cable, out var shape) && shape.Matches(cable);

    /// <summary>The network-relevant data of a fixture: GDTF file and mode, patch, fixture id (labels).</summary>
    private sealed class FixtureKey
    {
        private string? _spec;
        private string? _mode;
        private int _fixtureId;
        private DmxAddress[] _addresses = Array.Empty<DmxAddress>();

        public static FixtureKey Of(FixtureComponent f) => new()
        {
            _spec = f.GdtfSpec,
            _mode = f.GdtfMode,
            _fixtureId = f.FixtureId,
            _addresses = f.Addresses is { Count: > 0 } a ? a.ToArray() : Array.Empty<DmxAddress>(),
        };

        public bool Matches(FixtureComponent f)
        {
            if (!string.Equals(_spec, f.GdtfSpec, StringComparison.Ordinal) || !string.Equals(_mode, f.GdtfMode, StringComparison.Ordinal) ||
                _fixtureId != f.FixtureId)
                return false;
            var now = f.Addresses;
            int count = now?.Count ?? 0;
            if (count != _addresses.Length) return false;
            for (int i = 0; i < count; i++)
                if (!now![i].Equals(_addresses[i])) return false;
            return true;
        }
    }

    /// <summary>The structure of a cable: type, label, node ids with their anchor entities, segments and plugs.</summary>
    private sealed class CableShape
    {
        private string? _type;
        private string? _label;
        private Guid[] _nodes = Array.Empty<Guid>();
        private Guid?[] _anchors = Array.Empty<Guid?>();
        private CableSegment[] _segments = Array.Empty<CableSegment>();
        private (Guid Node, Guid Entity, string? Port)[] _endpoints = Array.Empty<(Guid, Guid, string?)>();

        public static CableShape Of(CableComponent c)
        {
            int nodes = c.Nodes?.Count ?? 0, endpoints = c.Endpoints?.Count ?? 0;
            var shape = new CableShape
            {
                _type = c.CableTypeId,
                _label = c.Label,
                _nodes = new Guid[nodes],
                _anchors = new Guid?[nodes],
                _segments = c.Segments is { Count: > 0 } s ? s.ToArray() : Array.Empty<CableSegment>(),
                _endpoints = new (Guid, Guid, string?)[endpoints],
            };
            for (int i = 0; i < nodes; i++)
            {
                var node = c.Nodes![i];
                shape._nodes[i] = node?.Id ?? Guid.Empty;
                shape._anchors[i] = node?.Anchor?.EntityId;
            }
            for (int i = 0; i < endpoints; i++)
            {
                var ep = c.Endpoints![i];
                shape._endpoints[i] = ep is null ? (Guid.Empty, Guid.Empty, null) : (ep.NodeId, ep.EntityId, ep.PortName);
            }
            return shape;
        }

        public bool Matches(CableComponent c)
        {
            if (!string.Equals(_type, c.CableTypeId, StringComparison.Ordinal) || !string.Equals(_label, c.Label, StringComparison.Ordinal))
                return false;
            var nodes = c.Nodes;
            if ((nodes?.Count ?? 0) != _nodes.Length) return false;
            for (int i = 0; i < _nodes.Length; i++)
            {
                var node = nodes![i];
                if ((node?.Id ?? Guid.Empty) != _nodes[i] || node?.Anchor?.EntityId != _anchors[i]) return false;
            }
            var segments = c.Segments;
            if ((segments?.Count ?? 0) != _segments.Length) return false;
            for (int i = 0; i < _segments.Length; i++)
                if (segments![i] != _segments[i]) return false;
            var endpoints = c.Endpoints;
            if ((endpoints?.Count ?? 0) != _endpoints.Length) return false;
            for (int i = 0; i < _endpoints.Length; i++)
            {
                var ep = endpoints![i];
                var (node, entity, port) = _endpoints[i];
                if (ep is null ? port is not null || node != Guid.Empty || entity != Guid.Empty
                        : ep.NodeId != node || ep.EntityId != entity || !string.Equals(ep.PortName, port, StringComparison.Ordinal))
                    return false;
            }
            return true;
        }
    }
}
