using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;

namespace Candela.Engine;

/// <summary>
/// Which cables move when an entity moves: every entity a cable node is anchored to (<see cref="CableAnchor"/>) and
/// every entity that carries a cable (its free nodes are in its local space) map to those cables. Moving an entity
/// moves its whole subtree, so <see cref="CollectMoved"/> looks up the moved entity and its descendants. Built lazily
/// from the scene after <see cref="Invalidate"/> (cables, anchors or entities were added, removed or edited); lookups
/// are dictionary hits that do not allocate. Main thread only, like the scene.
/// </summary>
public sealed class CableAnchorIndex
{
    private readonly Dictionary<Guid, List<Guid>> _cablesByEntity = new();
    private bool _valid;

    /// <summary>Number of entities that move cables (anchors and cable entities), after the last build.</summary>
    public int Count => _cablesByEntity.Count;

    /// <summary>True when the index describes the scene it was last built from.</summary>
    public bool IsValid => _valid;

    /// <summary>The cables changed (added, removed, re-anchored) or entities left the scene: rebuild on the next use.</summary>
    public void Invalidate() => _valid = false;

    /// <summary>Builds the index from <paramref name="scene"/> when it was invalidated (linear in the cables' nodes).</summary>
    public void EnsureBuilt(Scene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (_valid) return;
        _valid = true;
        foreach (var list in _cablesByEntity.Values) list.Clear();
        _cablesByEntity.Clear();
        foreach (var (entity, cable) in scene.Query<CableComponent>())
        {
            Add(entity.Id, entity.Id); // a cable entity moved as a whole moves its free nodes
            if (cable.Nodes is null) continue;
            foreach (var node in cable.Nodes)
                if (node?.Anchor is { } anchor && anchor.EntityId != Guid.Empty) Add(anchor.EntityId, entity.Id);
        }
    }

    /// <summary>
    /// The cables whose geometry follows <paramref name="entityId"/> (anchored to it, or carried by it); false when none.
    /// </summary>
    public bool TryGetCables(Guid entityId, out IReadOnlyList<Guid> cables)
    {
        if (_cablesByEntity.TryGetValue(entityId, out var list))
        {
            cables = list;
            return true;
        }
        cables = Array.Empty<Guid>();
        return false;
    }

    /// <summary>
    /// Adds to <paramref name="cables"/> every cable that moves with <paramref name="moved"/>: the cables anchored to it
    /// or to one of its descendants, and the cables they carry. Returns the number of cables added. Call
    /// <see cref="EnsureBuilt"/> first.
    /// </summary>
    public int CollectMoved(Entity moved, ISet<Guid> cables)
    {
        ArgumentNullException.ThrowIfNull(moved);
        ArgumentNullException.ThrowIfNull(cables);
        if (_cablesByEntity.Count == 0) return 0;
        int added = 0;
        foreach (var e in moved.SelfAndDescendants())
        {
            if (!_cablesByEntity.TryGetValue(e.Id, out var list)) continue;
            foreach (var cable in list)
                if (cables.Add(cable)) added++;
        }
        return added;
    }

    private void Add(Guid entityId, Guid cableId)
    {
        if (!_cablesByEntity.TryGetValue(entityId, out var list)) _cablesByEntity[entityId] = list = new List<Guid>(1);
        if (!list.Contains(cableId)) list.Add(cableId);
    }
}
