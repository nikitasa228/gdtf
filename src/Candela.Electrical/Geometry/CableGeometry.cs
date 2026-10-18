using System.Numerics;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;

namespace Candela.Electrical;

/// <summary>Kind of a problem found in a cable's node/segment data.</summary>
public enum CableGeometryProblemKind
{
    /// <summary>A node is anchored to an entity that no longer exists; its stored position is used.</summary>
    AnchorMissing,

    /// <summary>A segment refers to a node id that the cable does not have; the segment is ignored.</summary>
    SegmentNodeMissing,

    /// <summary>A node position is NaN or infinite; segments touching it are ignored.</summary>
    InvalidPosition,

    /// <summary>Segments close a loop (a cable is a tree); lengths are measured along the first route found.</summary>
    NotATree,

    /// <summary>Two nodes share an id; the second one is ignored.</summary>
    DuplicateNode,
}

/// <summary>A problem in a cable's geometry data.</summary>
/// <param name="Kind">What is wrong.</param>
/// <param name="NodeId">Node concerned (empty when not node-specific).</param>
/// <param name="Message">Explanation for the issues panel.</param>
public readonly record struct CableGeometryProblem(CableGeometryProblemKind Kind, Guid NodeId, string Message);

/// <summary>One node of a cable in world space.</summary>
/// <param name="Id">Node id.</param>
/// <param name="WorldPosition">Position in metres (world, Z up).</param>
/// <param name="AnchorEntityId">Entity the node follows, if any.</param>
/// <param name="AnchorResolved">False when the anchor entity is missing (the stored position was used).</param>
public readonly record struct CableNodePosition(Guid Id, Vector3 WorldPosition, Guid? AnchorEntityId, bool AnchorResolved);

/// <summary>One straight run of a cable in world space.</summary>
/// <param name="FromNode">First node id.</param>
/// <param name="ToNode">Second node id.</param>
/// <param name="From">World position of the first node.</param>
/// <param name="To">World position of the second node.</param>
/// <param name="LengthM">Straight length in metres (without slack).</param>
public readonly record struct CableSegmentGeometry(Guid FromNode, Guid ToNode, Vector3 From, Vector3 To, float LengthM);

/// <summary>
/// World-space geometry of one <see cref="CableComponent"/>: node positions (anchored nodes follow their entity at the
/// anchor's local offset; other nodes are in the cable entity's local space), segment lengths, the routed length and
/// the cut length (routed × <see cref="CableComponent.SlackFactor"/>), and lengths of the route between any two
/// nodes along the cable tree. Invalid data (missing nodes, NaN positions, loops) is reported in
/// <see cref="Problems"/> and skipped, never thrown.
/// </summary>
public sealed class CableGeometry
{
    private readonly Dictionary<Guid, int> _index;
    private readonly CableNodePosition[] _nodes;
    private readonly CableSegmentGeometry[] _segments;
    private readonly int[] _adjStart;
    private readonly (int Node, float Length)[] _adj;

    private CableGeometry(Guid cableEntityId, Dictionary<Guid, int> index, CableNodePosition[] nodes, CableSegmentGeometry[] segments,
        int[] adjStart, (int, float)[] adj, float slack, List<CableGeometryProblem> problems)
    {
        CableEntityId = cableEntityId;
        _index = index;
        _nodes = nodes;
        _segments = segments;
        _adjStart = adjStart;
        _adj = adj;
        SlackFactor = slack;
        Problems = problems;
        float routed = 0;
        foreach (var s in segments) routed += s.LengthM;
        RoutedLengthM = routed;
    }

    /// <summary>The entity that carries the cable component.</summary>
    public Guid CableEntityId { get; }

    /// <summary>Nodes in the cable's node order (duplicates removed).</summary>
    public IReadOnlyList<CableNodePosition> Nodes => _nodes;

    /// <summary>Valid segments.</summary>
    public IReadOnlyList<CableSegmentGeometry> Segments => _segments;

    /// <summary>Sum of the straight segment lengths in metres.</summary>
    public float RoutedLengthM { get; }

    /// <summary>Slack factor applied (the component's value, at least 1).</summary>
    public float SlackFactor { get; }

    /// <summary>Cable length to cut in metres: routed length × slack factor.</summary>
    public float LengthM => RoutedLengthM * SlackFactor;

    /// <summary>Problems found in the data.</summary>
    public IReadOnlyList<CableGeometryProblem> Problems { get; }

    /// <summary>True when the cable has a node with this id.</summary>
    public bool HasNode(Guid nodeId) => _index.ContainsKey(nodeId);

    /// <summary>World position of a node.</summary>
    public bool TryGetPosition(Guid nodeId, out Vector3 position)
    {
        if (_index.TryGetValue(nodeId, out int i))
        {
            position = _nodes[i].WorldPosition;
            return true;
        }
        position = default;
        return false;
    }

    /// <summary>
    /// Length in metres of the cable between two nodes along the tree, including slack; null when either node is
    /// missing or the nodes are not connected.
    /// </summary>
    public float? PathLengthM(Guid fromNode, Guid toNode)
    {
        if (!_index.TryGetValue(fromNode, out int a) || !_index.TryGetValue(toNode, out int b)) return null;
        if (a == b) return 0f;
        var (dist, _) = Search(a, b);
        return float.IsFinite(dist[b]) ? dist[b] * SlackFactor : null;
    }

    /// <summary>Node ids along the tree from one node to another (both included); empty when not connected.</summary>
    public IReadOnlyList<Guid> Path(Guid fromNode, Guid toNode)
    {
        if (!_index.TryGetValue(fromNode, out int a) || !_index.TryGetValue(toNode, out int b)) return Array.Empty<Guid>();
        if (a == b) return [fromNode];
        var (dist, parent) = Search(a, b);
        if (!float.IsFinite(dist[b])) return Array.Empty<Guid>();
        var path = new List<Guid>();
        for (int n = b; n >= 0; n = parent[n]) path.Add(_nodes[n].Id);
        path.Reverse();
        return path;
    }

    /// <summary>
    /// Routed distances (without slack) from one node to every node, in <see cref="Nodes"/> order; unreachable nodes
    /// are +∞. Null when the node is missing.
    /// </summary>
    public float[]? DistancesFrom(Guid nodeId)
    {
        if (!_index.TryGetValue(nodeId, out int a)) return null;
        return Search(a, -1).Dist;
    }

    /// <summary>Index of a node in <see cref="Nodes"/>, or −1.</summary>
    public int IndexOf(Guid nodeId) => _index.TryGetValue(nodeId, out int i) ? i : -1;

    private (float[] Dist, int[] Parent) Search(int from, int stopAt)
    {
        var dist = new float[_nodes.Length];
        var parent = new int[_nodes.Length];
        Array.Fill(dist, float.PositiveInfinity);
        Array.Fill(parent, -1);
        dist[from] = 0;
        var queue = new Queue<int>();
        queue.Enqueue(from);
        while (queue.Count > 0)
        {
            int n = queue.Dequeue();
            if (n == stopAt) break;
            for (int k = _adjStart[n]; k < _adjStart[n + 1]; k++)
            {
                var (m, len) = _adj[k];
                if (float.IsFinite(dist[m])) continue;
                dist[m] = dist[n] + len;
                parent[m] = n;
                queue.Enqueue(m);
            }
        }
        return (dist, parent);
    }

    // ------------------------------------------------------------------ construction

    /// <summary>Computes the geometry of a cable component carried by <paramref name="cableEntity"/>.</summary>
    /// <param name="scene">Scene used to resolve anchors (null: anchors are treated as missing).</param>
    /// <param name="cableEntity">Entity that carries the component (its world matrix places unanchored nodes).</param>
    /// <param name="cable">The cable.</param>
    public static CableGeometry Compute(Scene? scene, Entity cableEntity, CableComponent cable)
    {
        ArgumentNullException.ThrowIfNull(cableEntity);
        ArgumentNullException.ThrowIfNull(cable);
        var problems = new List<CableGeometryProblem>();
        var cableWorld = cableEntity.WorldMatrix;
        var index = new Dictionary<Guid, int>(cable.Nodes.Count);
        var nodes = new List<CableNodePosition>(cable.Nodes.Count);
        var valid = new List<bool>(cable.Nodes.Count);
        foreach (var node in cable.Nodes)
        {
            if (node is null) continue;
            if (index.ContainsKey(node.Id))
            {
                problems.Add(new(CableGeometryProblemKind.DuplicateNode, node.Id, $"node {node.Id} appears twice"));
                continue;
            }
            var pos = NodeWorldPosition(scene, cableWorld, node, out bool resolved);
            if (node.Anchor is { } anchor && !resolved)
                problems.Add(new(CableGeometryProblemKind.AnchorMissing, node.Id,
                    $"a node is anchored to entity {anchor.EntityId}, which no longer exists; its stored position is used"));
            bool finite = float.IsFinite(pos.X) && float.IsFinite(pos.Y) && float.IsFinite(pos.Z);
            if (!finite)
                problems.Add(new(CableGeometryProblemKind.InvalidPosition, node.Id, "a node has an invalid (NaN or infinite) position"));
            index[node.Id] = nodes.Count;
            nodes.Add(new CableNodePosition(node.Id, pos, node.Anchor?.EntityId, resolved));
            valid.Add(finite);
        }

        var segments = new List<CableSegmentGeometry>(cable.Segments.Count);
        var pairs = new List<(int A, int B, float L)>(cable.Segments.Count);
        var unionFind = new int[nodes.Count];
        for (int i = 0; i < unionFind.Length; i++) unionFind[i] = i;
        bool loopReported = false;
        foreach (var s in cable.Segments)
        {
            if (!index.TryGetValue(s.FromNode, out int a) || !index.TryGetValue(s.ToNode, out int b))
            {
                problems.Add(new(CableGeometryProblemKind.SegmentNodeMissing, index.ContainsKey(s.FromNode) ? s.ToNode : s.FromNode,
                    "a segment refers to a node the cable does not have; it is ignored"));
                continue;
            }
            if (a == b || !valid[a] || !valid[b]) continue;
            int ra = Find(unionFind, a), rb = Find(unionFind, b);
            if (ra == rb)
            {
                if (!loopReported)
                    problems.Add(new(CableGeometryProblemKind.NotATree, s.FromNode, "segments form a loop; a cable should be a tree"));
                loopReported = true;
                continue;
            }
            unionFind[ra] = rb;
            float len = Vector3.Distance(nodes[a].WorldPosition, nodes[b].WorldPosition);
            segments.Add(new CableSegmentGeometry(s.FromNode, s.ToNode, nodes[a].WorldPosition, nodes[b].WorldPosition, len));
            pairs.Add((a, b, len));
        }

        // Compressed adjacency (both directions).
        var adjStart = new int[nodes.Count + 1];
        foreach (var (a, b, _) in pairs)
        {
            adjStart[a + 1]++;
            adjStart[b + 1]++;
        }
        for (int i = 0; i < nodes.Count; i++) adjStart[i + 1] += adjStart[i];
        var adj = new (int, float)[pairs.Count * 2];
        var fill = (int[])adjStart.Clone();
        foreach (var (a, b, l) in pairs)
        {
            adj[fill[a]++] = (b, l);
            adj[fill[b]++] = (a, l);
        }

        float slack = float.IsFinite(cable.SlackFactor) && cable.SlackFactor >= 1f ? cable.SlackFactor : 1f;
        return new CableGeometry(cableEntity.Id, index, nodes.ToArray(), segments.ToArray(), adjStart, adj, slack, problems);
    }

    /// <summary>
    /// World position of one node: an anchored node follows its entity (anchor local offset × entity world matrix);
    /// otherwise, or when the anchor entity is missing, the node's position × the cable entity's world matrix.
    /// </summary>
    public static Vector3 NodeWorldPosition(Scene? scene, Matrix4x4 cableWorld, CableNode node, out bool anchorResolved)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (node.Anchor is { } anchor)
        {
            var target = scene?.Find(anchor.EntityId);
            if (target is not null)
            {
                anchorResolved = true;
                return Vector3.Transform(anchor.LocalOffset, target.WorldMatrix);
            }
            anchorResolved = false;
        }
        else
        {
            anchorResolved = true;
        }
        return Vector3.Transform(node.Position, cableWorld);
    }

    /// <summary>
    /// Writes the world positions of the cable's nodes (in node order) into <paramref name="destination"/> without
    /// allocating — for drawing cables every frame. Returns the number of positions written (at most the span length).
    /// </summary>
    public static int WriteNodeWorldPositions(Scene? scene, Entity cableEntity, CableComponent cable, Span<Vector3> destination)
    {
        ArgumentNullException.ThrowIfNull(cableEntity);
        ArgumentNullException.ThrowIfNull(cable);
        var world = cableEntity.WorldMatrix;
        int n = Math.Min(destination.Length, cable.Nodes.Count);
        for (int i = 0; i < n; i++)
        {
            var node = cable.Nodes[i];
            destination[i] = node is null ? default : NodeWorldPosition(scene, world, node, out _);
        }
        return n;
    }

    private static int Find(int[] parent, int i)
    {
        while (parent[i] != i)
        {
            parent[i] = parent[parent[i]];
            i = parent[i];
        }
        return i;
    }
}
