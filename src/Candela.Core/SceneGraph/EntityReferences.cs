using System.Numerics;

namespace Candela.Core.SceneGraph;

/// <summary>
/// Marks a member of a component (or of a type nested in one) that holds the <see cref="Entity.Id"/> of another entity:
/// a <see cref="Guid"/>, a <see cref="Nullable{Guid}"/> or a list of them. When entities are copied
/// (<see cref="EntityCloner"/>) these references are rewritten by the component's
/// <see cref="Component.RemapEntityReferences"/>. Every <see cref="Guid"/> member of a component must carry exactly one
/// of <see cref="EntityReferenceAttribute"/>, <see cref="AssetReferenceAttribute"/> or <see cref="LocalIdAttribute"/>;
/// a unit test enforces it, so a new reference cannot be forgotten by the copy code.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, Inherited = true)]
public sealed class EntityReferenceAttribute : Attribute
{
}

/// <summary>
/// Marks a <see cref="Guid"/> member that names an asset of the project (imported model, image), not an entity. Copies
/// inside a scene keep it; a merge import maps it with the project's asset table.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, Inherited = true)]
public sealed class AssetReferenceAttribute : Attribute
{
}

/// <summary>
/// Marks a <see cref="Guid"/> member that is an id local to its component (e.g. a cable node id) or a reference to such
/// an id inside the same component. It is never mapped through the entity id map; a copy gets fresh local ids, rewritten
/// consistently within the component, when <see cref="EntityReferenceRemapper.RegenerateLocalIds"/> is set.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, Inherited = true)]
public sealed class LocalIdAttribute : Attribute
{
}

/// <summary>What happens to a reference to an entity that is not part of a copy (not a key of the id map).</summary>
public enum ExternalReferenceHandling
{
    /// <summary>
    /// The reference is kept unchanged: duplicating inside the same scene, the copy of a fixture still hangs on the
    /// original truss.
    /// </summary>
    Keep,

    /// <summary>
    /// The reference is removed (a nullable id becomes null, a list entry or a cable endpoint is dropped, a cable node
    /// anchor is released at its current place): pasting into another scene or merging a project, where the old ids mean
    /// nothing.
    /// </summary>
    Clear,

    /// <summary>
    /// The reference is kept when <see cref="EntityReferenceRemapper.IsResolvable"/> says the entity exists where the
    /// copy goes (e.g. pasting into the scene the copy was taken from, whose entities may have been deleted since), and
    /// removed otherwise.
    /// </summary>
    KeepIfResolvable,
}

/// <summary>An entity reference that was removed by an <see cref="EntityReferenceRemapper"/>.</summary>
/// <param name="Component">The component that held it (null when the remapper was used outside <see cref="EntityReferenceRemapper.Apply(Component)"/>).</param>
/// <param name="EntityId">The id that was referenced.</param>
public readonly record struct ClearedEntityReference(Component? Component, Guid EntityId);

/// <summary>
/// Rewrites the entity references held by components when entities are copied (duplicate, copy/paste, merge import):
/// ids that are keys of <see cref="IdMap"/> become their copy's id; other ids are kept or removed according to
/// <see cref="ExternalReferences"/>. <see cref="Guid.Empty"/> means "no reference" and is never changed.
/// Components implement the rewrite in <see cref="Component.RemapEntityReferences"/> with the primitives of this class;
/// callers use <see cref="Apply(Component)"/>. Counters and <see cref="Cleared"/> report what happened.
/// One instance serves one copy operation on the main thread.
/// </summary>
public sealed class EntityReferenceRemapper
{
    private readonly IReadOnlyDictionary<Guid, Guid> _map;
    private readonly List<ClearedEntityReference> _cleared = new();
    private Dictionary<Guid, Guid>? _reverse;
    private Component? _current;

    /// <summary>Creates a remapper for one copy operation.</summary>
    /// <param name="idMap">Original entity id → id of its copy, for every copied entity.</param>
    /// <param name="externalReferences">What to do with references to entities that are not keys of <paramref name="idMap"/>.</param>
    public EntityReferenceRemapper(IReadOnlyDictionary<Guid, Guid> idMap, ExternalReferenceHandling externalReferences = ExternalReferenceHandling.Keep)
    {
        ArgumentNullException.ThrowIfNull(idMap);
        _map = idMap;
        ExternalReferences = externalReferences;
    }

    /// <summary>Original entity id → id of its copy.</summary>
    public IReadOnlyDictionary<Guid, Guid> IdMap => _map;

    /// <summary>What happens to references to entities outside <see cref="IdMap"/>.</summary>
    public ExternalReferenceHandling ExternalReferences { get; }

    /// <summary>
    /// For <see cref="ExternalReferenceHandling.KeepIfResolvable"/>: true when an id outside the map names an entity that
    /// exists where the copies will live. Without it, such references are removed.
    /// </summary>
    public Func<Guid, bool>? IsResolvable { get; init; }

    /// <summary>
    /// Resolves an original id (before the copy) to the entity it named, so a component can keep its geometry when a
    /// reference is removed — a cable node anchored to an entity that is not copied stays where it was. Optional.
    /// </summary>
    public Func<Guid, Entity?>? ResolveOriginal { get; init; }

    /// <summary>
    /// When true (the default), ids local to a component (<see cref="LocalIdAttribute"/>, e.g. cable node ids) are
    /// regenerated, consistently within the component, so a copy never shares them with its original.
    /// </summary>
    public bool RegenerateLocalIds { get; init; } = true;

    /// <summary>Number of references rewritten to the id of a copy.</summary>
    public int Remapped { get; private set; }

    /// <summary>Number of references to entities outside the map that were kept.</summary>
    public int Kept { get; private set; }

    /// <summary>References to entities outside the map that were removed, in order.</summary>
    public IReadOnlyList<ClearedEntityReference> Cleared => _cleared;

    /// <summary>Rewrites every entity reference held by <paramref name="component"/>.</summary>
    public void Apply(Component component)
    {
        ArgumentNullException.ThrowIfNull(component);
        var previous = _current;
        _current = component;
        try
        {
            component.RemapEntityReferences(this);
        }
        finally
        {
            _current = previous;
        }
    }

    /// <summary>Rewrites the entity references of every component of <paramref name="entity"/> (not of its children).</summary>
    public void Apply(Entity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        foreach (var c in entity.Components) Apply(c);
    }

    /// <summary>
    /// Maps one referenced id. Returns true with the id to store (the copy's id, or the same id when kept or
    /// <see cref="Guid.Empty"/>), or false when the reference must be removed (then <paramref name="result"/> is
    /// <see cref="Guid.Empty"/> and the removal is recorded in <see cref="Cleared"/>).
    /// </summary>
    public bool TryRemap(Guid id, out Guid result)
    {
        if (id == Guid.Empty)
        {
            result = id;
            return true;
        }
        if (_map.TryGetValue(id, out result))
        {
            Remapped++;
            return true;
        }
        bool keep = ExternalReferences switch
        {
            ExternalReferenceHandling.Keep => true,
            ExternalReferenceHandling.KeepIfResolvable => IsResolvable?.Invoke(id) == true,
            _ => false,
        };
        if (keep)
        {
            result = id;
            Kept++;
            return true;
        }
        result = Guid.Empty;
        _cleared.Add(new ClearedEntityReference(_current, id));
        return false;
    }

    /// <summary>Maps an optional reference: null stays null, a removed reference becomes null.</summary>
    public Guid? Remap(Guid? id) => id is { } value ? (TryRemap(value, out var mapped) ? mapped : null) : null;

    /// <summary>Maps a list of references in place; removed references are taken out of the list (null list: no-op).</summary>
    public void RemapAll(List<Guid>? ids)
    {
        if (ids is null) return;
        int write = 0;
        for (int read = 0; read < ids.Count; read++)
            if (TryRemap(ids[read], out var mapped)) ids[write++] = mapped;
        if (write < ids.Count) ids.RemoveRange(write, ids.Count - write);
    }

    /// <summary>
    /// The original id of a copy (the key whose value is <paramref name="id"/>), or <paramref name="id"/> itself when it is
    /// not the id of a copy.
    /// </summary>
    public Guid OriginalIdOf(Guid id)
    {
        if (_reverse is null)
        {
            _reverse = new Dictionary<Guid, Guid>(_map.Count);
            foreach (var (original, copy) in _map) _reverse.TryAdd(copy, original);
        }
        return _reverse.TryGetValue(id, out var o) ? o : id;
    }

    /// <summary>World matrix of the entity an original id named, through <see cref="ResolveOriginal"/>.</summary>
    public bool TryGetOriginalWorldMatrix(Guid originalId, out Matrix4x4 world)
    {
        if (ResolveOriginal?.Invoke(originalId) is { } e)
        {
            world = e.WorldMatrix;
            return true;
        }
        world = Matrix4x4.Identity;
        return false;
    }
}
