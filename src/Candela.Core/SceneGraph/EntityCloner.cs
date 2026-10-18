using System.Text.Json;
using Candela.Core.Diagnostics;

namespace Candela.Core.SceneGraph;

/// <summary>Options of <see cref="EntityCloner.Clone(IEnumerable{Entity}, EntityCloneOptions?)"/>.</summary>
public sealed class EntityCloneOptions
{
    /// <summary>
    /// What happens to references to entities that are not copied: <see cref="ExternalReferenceHandling.Keep"/> (default,
    /// duplicate inside the same scene), <see cref="ExternalReferenceHandling.Clear"/> (paste into another scene, merge
    /// import) or <see cref="ExternalReferenceHandling.KeepIfResolvable"/> (keep when <see cref="TargetScene"/> has the
    /// entity).
    /// </summary>
    public ExternalReferenceHandling ExternalReferences { get; init; } = ExternalReferenceHandling.Keep;

    /// <summary>
    /// The scene the copies will be added to, when known. Copies never get an id it already uses; layer ids it does not
    /// have are left empty (so <see cref="Scene.Add"/> puts the copy on its parent's or the default layer);
    /// <see cref="ExternalReferenceHandling.KeepIfResolvable"/> keeps references to entities it contains.
    /// </summary>
    public Scene? TargetScene { get; init; }

    /// <summary>
    /// Chooses the id of the copy of an entity (e.g. keep the original id when it is free, or a deterministic id). Default:
    /// a new random id. An id that is empty, already given to another copy, or used in <see cref="TargetScene"/> is
    /// replaced by a new random id.
    /// </summary>
    public Func<Entity, Guid>? IdFactory { get; init; }

    /// <summary>Maps the layer id of an original to the layer id of its copy (merge import layer table). Default: same id.</summary>
    public Func<Guid, Guid>? LayerMap { get; init; }

    /// <summary>
    /// True: the local transform of each copied root is the original's world transform, so a copy added at the scene
    /// root lands where the original is (copy/paste, merge into the root). False (default): local transforms are copied as
    /// they are (duplicate under the same parent).
    /// </summary>
    public bool RootsKeepWorldTransform { get; init; }

    /// <summary>Renew ids local to components (cable node ids) so copies never share them (default true).</summary>
    public bool RegenerateLocalIds { get; init; } = true;

    /// <summary>
    /// Original asset id → asset id where the copies go (merge import of another project's assets). Null (default):
    /// asset ids are kept, as for copies inside one project. See <see cref="EntityReferenceRemapper.AssetIdMap"/>.
    /// </summary>
    public IReadOnlyDictionary<Guid, Guid>? AssetIdMap { get; init; }

    /// <summary>
    /// Called for every copied component after its references were rewritten, with the original component and its copy
    /// (attached to the copy entity, which is still detached; <see cref="EntityCloneResult.IdMap"/> is complete by then).
    /// Use it for what Core does not know about: asset texture keys inside material ids, GDTF file names, console fixture
    /// ids, world-space points moved by a placement. An exception leaves that component out of the copy and is reported in
    /// <see cref="EntityCloneResult.Issues"/>; the copy goes on.
    /// </summary>
    public Action<Component, Component>? ComponentCopied { get; init; }
}

/// <summary>The detached copies made by <see cref="EntityCloner"/> and what happened to the references they hold.</summary>
public sealed class EntityCloneResult
{
    private readonly Dictionary<Guid, Entity> _copyByOriginalId;

    internal EntityCloneResult(IReadOnlyList<Entity> originals, IReadOnlyList<Entity> roots, Dictionary<Guid, Guid> idMap,
        Dictionary<Guid, Entity> copyByOriginalId, EntityReferenceRemapper remapper, IReadOnlyList<Issue> issues)
    {
        Originals = originals;
        Roots = roots;
        IdMap = idMap;
        _copyByOriginalId = copyByOriginalId;
        ReferencesRemapped = remapper.Remapped;
        ReferencesKept = remapper.Kept;
        AssetReferencesRemapped = remapper.AssetsRemapped;
        ClearedReferences = remapper.Cleared;
        Issues = issues;
    }

    /// <summary>
    /// The entities that were copied as subtree roots, in input order (duplicates and entities whose ancestor was also
    /// given are left out: they are copied as part of that ancestor's subtree).
    /// </summary>
    public IReadOnlyList<Entity> Originals { get; }

    /// <summary>The detached copies of <see cref="Originals"/> (same order), each with its copied subtree. Add them to a scene.</summary>
    public IReadOnlyList<Entity> Roots { get; }

    /// <summary>Original id → copy id, for every copied entity (all levels).</summary>
    public IReadOnlyDictionary<Guid, Guid> IdMap { get; }

    /// <summary>Number of copied entities (all levels).</summary>
    public int Count => IdMap.Count;

    /// <summary>References rewritten to point at a copy (e.g. a copied fixture mounted on a copied truss).</summary>
    public int ReferencesRemapped { get; }

    /// <summary>References to entities that were not copied and were kept.</summary>
    public int ReferencesKept { get; }

    /// <summary>Asset references rewritten through <see cref="EntityCloneOptions.AssetIdMap"/>.</summary>
    public int AssetReferencesRemapped { get; }

    /// <summary>References to entities that were not copied and were removed.</summary>
    public IReadOnlyList<ClearedEntityReference> ClearedReferences { get; }

    /// <summary>Problems met while copying (a component that could not be copied, references removed). Never fatal.</summary>
    public IReadOnlyList<Issue> Issues { get; }

    /// <summary>The copy of the entity with <paramref name="originalId"/> (any level), or null when it was not copied.</summary>
    public Entity? FindCopy(Guid originalId) => _copyByOriginalId.GetValueOrDefault(originalId);
}

/// <summary>
/// Deep-copies entity subtrees (entities, flags, transforms and components) with new ids and rewrites the entity
/// references inside the copied components (<see cref="Component.RemapReferences"/>): references among the copied
/// entities point at the copies; references to other entities are kept or removed (<see cref="EntityCloneOptions.ExternalReferences"/>).
/// The copies are detached: add <see cref="EntityCloneResult.Roots"/> to a scene (e.g. with
/// <see cref="Editing.AddEntitiesCommand"/>).
/// </summary>
/// <remarks>
/// Typical uses:
/// <list type="bullet">
/// <item>Duplicate: <c>Clone(selection, new() { TargetScene = scene })</c>, add each copy under its original's parent
/// (<see cref="Editing.DuplicateEntitiesCommand"/> does this).</item>
/// <item>Copy: <c>Clone(selection, new() { RootsKeepWorldTransform = true })</c> into the clipboard; paste:
/// <c>Clone(clipboard.Roots, new() { TargetScene = scene, ExternalReferences = ExternalReferenceHandling.KeepIfResolvable })</c>
/// and add the copies at the root — every paste gets fresh ids.</item>
/// <item>Merge import: <c>Clone(source.Roots, new() { TargetScene = target, ExternalReferences = ExternalReferenceHandling.Clear, LayerMap = … })</c>.</item>
/// </list>
/// World-space points held by components (focus points, camera targets) are copied unchanged.
/// </remarks>
public static class EntityCloner
{
    /// <summary>Issue code: a component could not be copied and was left out of the copy.</summary>
    public const string ComponentNotCopiedCode = "scene.copy-component-failed";

    /// <summary>Issue code: references to entities that were not copied were removed.</summary>
    public const string ReferencesRemovedCode = "scene.copy-references-removed";

    /// <summary>Copies one entity with its subtree.</summary>
    public static EntityCloneResult Clone(Entity entity, EntityCloneOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return Clone(new[] { entity }, options);
    }

    /// <summary>
    /// Copies the given entities with their subtrees. Entities may belong to a scene or be detached (a clipboard);
    /// an entity listed together with one of its ancestors is copied once, inside the ancestor's copy. Null entries
    /// are ignored.
    /// </summary>
    public static EntityCloneResult Clone(IEnumerable<Entity> entities, EntityCloneOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(entities);
        options ??= new EntityCloneOptions();
        var target = options.TargetScene;

        var roots = TopLevel(entities);
        var idMap = new Dictionary<Guid, Guid>();
        var originalById = new Dictionary<Guid, Entity>();
        var copyByOriginalId = new Dictionary<Guid, Entity>();
        var copies = new List<(Entity Original, Entity Copy)>();
        var copyOf = new Dictionary<Entity, Entity>(ReferenceEqualityComparer.Instance);
        var usedIds = new HashSet<Guid>();
        var sourceOf = new Dictionary<Component, Component>(ReferenceEqualityComparer.Instance);
        var issues = new List<Issue>();
        var rootCopies = new List<Entity>(roots.Count);

        foreach (var root in roots)
        {
            foreach (var original in root.SelfAndDescendants())
            {
                var id = options.IdFactory?.Invoke(original) ?? Guid.NewGuid();
                while (id == Guid.Empty || usedIds.Contains(id) || target?.Find(id) is not null) id = Guid.NewGuid();
                usedIds.Add(id);

                var layer = options.LayerMap is { } mapLayer ? mapLayer(original.LayerId) : original.LayerId;
                if (target is not null && layer != Guid.Empty && target.FindLayer(layer) is null) layer = Guid.Empty;

                bool isRoot = ReferenceEquals(original, root);
                var copy = new Entity(original.Name, id)
                {
                    LayerId = layer,
                    Visible = original.Visible,
                    Locked = original.Locked,
                    UnknownProperties = original.UnknownProperties is { } unknown ? new Dictionary<string, JsonElement>(unknown) : null,
                };
                copy.Local = isRoot && options.RootsKeepWorldTransform ? original.World : original.Local;
                if (isRoot) rootCopies.Add(copy);
                else copyOf[original.Parent!].AttachChild(copy, -1);
                copyOf[original] = copy;
                copies.Add((original, copy));
                if (idMap.TryAdd(original.Id, id)) // a repeated id (malformed detached tree): references go to the first copy
                {
                    originalById[original.Id] = original;
                    copyByOriginalId[original.Id] = copy;
                }

                foreach (var component in original.Components)
                {
                    try
                    {
                        var clone = component.Clone();
                        copy.Add(clone);
                        sourceOf[clone] = component;
                    }
                    catch (Exception e)
                    {
                        issues.Add(new Issue(IssueSeverity.Warning, IssueCategory.Scene, ComponentNotCopiedCode,
                            $"The {component.GetType().Name} of '{original.Name}' could not be copied and was left out: {e.Message}", id));
                    }
                }
            }
        }

        var sources = new List<Scene>(1);
        foreach (var r in roots)
            if (r.Scene is { } s && !sources.Contains(s)) sources.Add(s);
        var remapper = new EntityReferenceRemapper(idMap, options.ExternalReferences)
        {
            IsResolvable = target is null ? null : id => target.Find(id) is not null,
            ResolveOriginal = id => ResolveOriginal(id, originalById, sources, target),
            RegenerateLocalIds = options.RegenerateLocalIds,
            AssetIdMap = options.AssetIdMap,
        };
        foreach (var (original, copy) in copies)
        {
            if (copy.Components.Count == 0) continue;
            foreach (var component in copy.Components.ToArray()) // components may be left out on failure
            {
                try
                {
                    remapper.Apply(component);
                }
                catch (Exception e)
                {
                    // A half-remapped component could still point at the originals: leave it out rather than keep it wrong.
                    copy.Remove(component);
                    issues.Add(new Issue(IssueSeverity.Warning, IssueCategory.Scene, ComponentNotCopiedCode,
                        $"The references of the {component.GetType().Name} of '{original.Name}' could not be updated; the component was left out of the copy: {e.Message}",
                        copy.Id));
                    continue;
                }
                if (options.ComponentCopied is not { } copied) continue;
                try
                {
                    copied(sourceOf[component], component);
                }
                catch (Exception e)
                {
                    copy.Remove(component);
                    issues.Add(new Issue(IssueSeverity.Warning, IssueCategory.Scene, ComponentNotCopiedCode,
                        $"The {component.GetType().Name} of '{original.Name}' could not be completed and was left out of the copy: {e.Message}",
                        copy.Id));
                }
            }
        }

        if (remapper.Cleared.Count > 0)
        {
            var owners = new List<Guid>();
            var seen = new HashSet<Guid>();
            foreach (var cleared in remapper.Cleared)
                if (cleared.Component?.Owner is { } owner && seen.Add(owner.Id)) owners.Add(owner.Id);
            issues.Add(new Issue(IssueSeverity.Note, IssueCategory.Scene, ReferencesRemovedCode,
                $"{remapper.Cleared.Count} reference(s) to objects that were not copied were removed (mounts, carried structures, cable anchors and ends).",
                owners.ToArray()));
        }

        return new EntityCloneResult(roots, rootCopies, idMap, copyByOriginalId, remapper, issues);
    }

    /// <summary>Distinct non-null entities whose ancestors are not in the set, in input order.</summary>
    private static List<Entity> TopLevel(IEnumerable<Entity> entities)
    {
        var set = new HashSet<Entity>(ReferenceEqualityComparer.Instance);
        var ordered = new List<Entity>();
        foreach (var e in entities)
            if (e is not null && set.Add(e)) ordered.Add(e);
        if (ordered.Count < 2) return ordered;
        var top = new List<Entity>(ordered.Count);
        foreach (var e in ordered)
        {
            bool covered = false;
            for (var p = e.Parent; p is not null && !covered; p = p.Parent) covered = set.Contains(p);
            if (!covered) top.Add(e);
        }
        return top;
    }

    private static Entity? ResolveOriginal(Guid id, Dictionary<Guid, Entity> originals, List<Scene> sources, Scene? target)
    {
        if (originals.TryGetValue(id, out var e)) return e;
        foreach (var s in sources)
            if (s.Find(id) is { } found) return found;
        return target?.Find(id);
    }
}
