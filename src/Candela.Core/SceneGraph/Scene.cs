using System.Text.Json;

namespace Candela.Core.SceneGraph;

/// <summary>
/// The edited document: a forest of entities, layers and scene-wide settings. Every mutation — structure, transforms,
/// names, visibility, locks, layers, components, settings — raises <see cref="Changed"/> and bumps
/// <see cref="Version"/>, which renderers, analysers and the project's modified flag use to update incrementally.
/// Lookups that run per frame (<see cref="Find"/>, <see cref="FindLayer"/>, <see cref="IsEntityVisible"/>,
/// <see cref="IsEntityLocked"/>) are dictionary lookups that never allocate.
/// The scene is owned by the main thread; background work must not touch it.
/// </summary>
public sealed class Scene
{
    private readonly List<Entity> _roots = new();
    private readonly Dictionary<Guid, Entity> _byId = new();
    private SceneSettings _settings;
    private string _name;

    /// <summary>Creates an empty scene with one layer named "Default".</summary>
    public Scene(string name = "Untitled")
    {
        _name = name ?? "";
        Layers = new LayerCollection(this);
        Layers.AddSilently(new Layer { Name = "Default" });
        _settings = new SceneSettings { Owner = this };
    }

    /// <summary>Stable identity of the document (kept in the project file).</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Display name; changing it raises <see cref="SceneChangeKind.SceneRenamed"/>. Null is stored as "".</summary>
    public string Name
    {
        get => _name;
        set
        {
            value ??= "";
            if (_name == value) return;
            _name = value;
            Raise(new SceneChange(SceneChangeKind.SceneRenamed, null));
        }
    }

    /// <summary>
    /// Layers in display order; the first one is <see cref="DefaultLayer"/>. Adding, removing or editing a layer raises
    /// <see cref="SceneChangeKind.LayersChanged"/>.
    /// </summary>
    public LayerCollection Layers { get; }

    /// <summary>
    /// Scene-wide settings. Changing one of their values, or assigning another object (null assigns defaults), raises
    /// <see cref="SceneChangeKind.SettingsChanged"/>. A settings object belongs to one scene at a time: assigning the
    /// settings of another scene throws <see cref="InvalidOperationException"/> (assign a copy, e.g.
    /// <see cref="Serialization.CandelaJson.DeepClone{T}"/>).
    /// </summary>
    public SceneSettings Settings
    {
        get => _settings;
        set
        {
            value ??= new SceneSettings();
            if (ReferenceEquals(_settings, value)) return;
            if (value.Owner is { } other && !ReferenceEquals(other, this) && ReferenceEquals(other._settings, value))
                throw new InvalidOperationException($"These settings belong to the scene '{other.Name}'; assign a copy.");
            if (ReferenceEquals(_settings.Owner, this)) _settings.Owner = null;
            _settings = value;
            value.Owner = this;
            NotifySettingsChanged();
        }
    }

    /// <summary>
    /// Top-level JSON properties of the scene file that this version does not know (written by a newer version of
    /// Candela), kept so saving the scene again does not drop them. Opaque: never interpreted, and changing them raises
    /// no scene change. Null when there are none.
    /// </summary>
    public Dictionary<string, JsonElement>? UnknownProperties { get; set; }

    /// <summary>Top-level entities in order.</summary>
    public IReadOnlyList<Entity> Roots => _roots;

    /// <summary>Number of entities in the scene (all levels).</summary>
    public int Count => _byId.Count;

    /// <summary>Incremented on every change (never decreases, also not on undo).</summary>
    public long Version { get; private set; }

    /// <summary>Raised after every change, on the thread that made it (the main thread).</summary>
    public event Action<SceneChange>? Changed;

    /// <summary>
    /// The layer new entities go to when they name none: the first layer. When the layer list was emptied, a new layer
    /// named "Default" is created on demand, so a scene always has one.
    /// </summary>
    public Layer DefaultLayer
    {
        get
        {
            if (Layers.Count == 0) Layers.Add(new Layer { Name = "Default" });
            return Layers[0];
        }
    }

    /// <summary>
    /// Adds a detached entity (with its existing subtree) under <paramref name="parent"/> or as a root, at
    /// <paramref name="index"/> among its siblings (-1 = last). Entities without a layer (<see cref="Guid.Empty"/>) take
    /// the layer of their parent, roots the <see cref="DefaultLayer"/>. Raises <see cref="SceneChangeKind.Added"/> once for
    /// the subtree root. Ids are checked before anything changes, so a duplicate id leaves the scene untouched.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The entity belongs to a scene or has a parent, the parent is not in this scene, or an id of the subtree is already
    /// used (in the scene or twice in the subtree).
    /// </exception>
    public Entity Add(Entity entity, Entity? parent = null, int index = -1)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (entity.Scene is not null) throw new InvalidOperationException($"{entity} already belongs to a scene.");
        if (entity.Parent is not null)
            throw new InvalidOperationException($"{entity} is a child of {entity.Parent}; only the root of a detached subtree can be added.");
        if (parent is not null && parent.Scene != this) throw new InvalidOperationException("Parent is not in this scene.");

        HashSet<Guid>? subtreeIds = entity.Children.Count > 0 ? new HashSet<Guid>() : null;
        foreach (var e in entity.SelfAndDescendants())
        {
            if (_byId.ContainsKey(e.Id) || (subtreeIds is not null && !subtreeIds.Add(e.Id)))
                throw new InvalidOperationException($"Duplicate entity id {e.Id}.");
        }

        if (entity.LayerId == Guid.Empty)
            entity.LayerId = parent is not null && parent.LayerId != Guid.Empty ? parent.LayerId : DefaultLayer.Id;
        if (subtreeIds is not null)
        {
            foreach (var e in entity.SelfAndDescendants())
                if (e.LayerId == Guid.Empty && e.Parent is { } p) e.LayerId = p.LayerId; // parents come first
        }

        if (parent is null)
        {
            if (index < 0 || index > _roots.Count) index = _roots.Count;
            _roots.Insert(index, entity);
            entity.ClearParentForRoot();
        }
        else
        {
            parent.AttachChild(entity, index);
        }
        foreach (var e in entity.SelfAndDescendants())
        {
            _byId.Add(e.Id, e);
            e.Scene = this;
        }
        Raise(new SceneChange(SceneChangeKind.Added, entity));
        return entity;
    }

    /// <summary>
    /// Removes an entity and its subtree (the subtree stays intact and detached, ready to be added again by undo).
    /// Returns the former parent and sibling index.
    /// </summary>
    public (Entity? Parent, int Index) Remove(Entity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (entity.Scene != this) throw new InvalidOperationException($"{entity} is not in this scene.");
        var parent = entity.Parent;
        int index;
        if (parent is null)
        {
            index = _roots.IndexOf(entity);
            _roots.RemoveAt(index);
        }
        else
        {
            index = parent.DetachChild(entity);
        }
        foreach (var e in entity.SelfAndDescendants())
        {
            _byId.Remove(e.Id);
            e.Scene = null;
        }
        Raise(new SceneChange(SceneChangeKind.Removed, entity) { FormerParent = parent });
        return (parent, index);
    }

    /// <summary>
    /// Moves an entity under another parent (null = root) at <paramref name="index"/> (-1 = last), optionally keeping its
    /// world transform. Raises <see cref="SceneChangeKind.Reparented"/>.
    /// </summary>
    public void Reparent(Entity entity, Entity? newParent, bool keepWorldTransform = true, int index = -1)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (entity.Scene != this) throw new InvalidOperationException($"{entity} is not in this scene.");
        if (newParent is not null && newParent.Scene != this) throw new InvalidOperationException("The new parent is not in this scene.");
        if (newParent == entity || (newParent is not null && entity.IsAncestorOf(newParent)))
            throw new InvalidOperationException("Cannot parent an entity under itself or its descendant.");
        var world = entity.World;
        var oldParent = entity.Parent;
        if (oldParent is null) _roots.Remove(entity); else oldParent.DetachChild(entity);
        if (newParent is null)
        {
            if (index < 0 || index > _roots.Count) index = _roots.Count;
            _roots.Insert(index, entity);
            entity.ClearParentForRoot();
        }
        else
        {
            newParent.AttachChild(entity, index);
        }
        if (keepWorldTransform) entity.World = world;
        Raise(new SceneChange(SceneChangeKind.Reparented, entity) { FormerParent = oldParent });
    }

    /// <summary>The entity with <paramref name="id"/>, or null. O(1), allocation-free.</summary>
    public Entity? Find(Guid id) => _byId.GetValueOrDefault(id);

    /// <summary>All entities, depth-first in tree order (parents before their children).</summary>
    public IEnumerable<Entity> All() => Entity.Traverse(null, _roots);

    /// <summary>All entities that carry a component of type <typeparamref name="T"/>, in tree order.</summary>
    public IEnumerable<(Entity Entity, T Component)> Query<T>() where T : Component
    {
        foreach (var e in All())
        {
            var components = e.Components;
            for (int i = 0; i < components.Count; i++)
                if (components[i] is T t) yield return (e, t);
        }
    }

    /// <summary>The layer with <paramref name="id"/>, or null. O(1), allocation-free (safe to call per frame).</summary>
    public Layer? FindLayer(Guid id) => Layers.Find(id);

    /// <summary>
    /// True when <paramref name="entity"/> is drawn: it and all its ancestors are <see cref="Entity.Visible"/> and its layer
    /// is visible (an unknown layer counts as visible). Allocation-free.
    /// </summary>
    public bool IsEntityVisible(Entity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return entity.IsVisibleInHierarchy && Layers.Find(entity.LayerId) is not { Visible: false };
    }

    /// <summary>
    /// True when <paramref name="entity"/> must not be selected or moved in the viewport: it is <see cref="Entity.Locked"/>
    /// or its layer is locked. Allocation-free.
    /// </summary>
    public bool IsEntityLocked(Entity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return entity.Locked || Layers.Find(entity.LayerId) is { Locked: true };
    }

    /// <summary>
    /// For changes to scene-level data the scene cannot see by itself (e.g. after editing a settings object in place
    /// through reflection). Raises <see cref="SceneChangeKind.SettingsChanged"/>.
    /// </summary>
    public void NotifySettingsChanged() => Raise(new SceneChange(SceneChangeKind.SettingsChanged, null));

    internal void Raise(SceneChange change)
    {
        Version++;
        Changed?.Invoke(change);
    }
}
