namespace Candela.Core.SceneGraph;

/// <summary>
/// The edited document: a forest of entities, layers and scene-wide settings. All mutations raise
/// <see cref="Changed"/> and bump <see cref="Version"/>, which renderers and analysers use to update incrementally.
/// The scene is owned by the main thread; background work must not touch it.
/// </summary>
public sealed class Scene
{
    private readonly List<Entity> _roots = new();
    private readonly Dictionary<Guid, Entity> _byId = new();

    public Scene(string name = "Untitled")
    {
        Name = name;
        Layers.Add(new Layer { Name = "Default" });
    }

    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; set; }
    public List<Layer> Layers { get; } = new();
    public SceneSettings Settings { get; set; } = new();

    public IReadOnlyList<Entity> Roots => _roots;
    public int Count => _byId.Count;

    /// <summary>Incremented on every change.</summary>
    public long Version { get; private set; }

    public event Action<SceneChange>? Changed;

    public Layer DefaultLayer => Layers[0];

    /// <summary>Adds an entity (with its existing subtree) under <paramref name="parent"/> or as a root.</summary>
    public Entity Add(Entity entity, Entity? parent = null, int index = -1)
    {
        if (entity.Scene is not null) throw new InvalidOperationException($"{entity} already belongs to a scene.");
        if (parent is not null && parent.Scene != this) throw new InvalidOperationException("Parent is not in this scene.");
        if (entity.LayerId == Guid.Empty) entity.LayerId = parent?.LayerId ?? DefaultLayer.Id;
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
            if (!_byId.TryAdd(e.Id, e)) throw new InvalidOperationException($"Duplicate entity id {e.Id}.");
            e.Scene = this;
        }
        Raise(new SceneChange(SceneChangeKind.Added, entity));
        return entity;
    }

    /// <summary>Removes an entity and its subtree. Returns the former parent and sibling index (for undo).</summary>
    public (Entity? Parent, int Index) Remove(Entity entity)
    {
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

    /// <summary>Moves an entity under another parent (null = root), optionally keeping its world transform.</summary>
    public void Reparent(Entity entity, Entity? newParent, bool keepWorldTransform = true, int index = -1)
    {
        if (entity.Scene != this) throw new InvalidOperationException($"{entity} is not in this scene.");
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

    public Entity? Find(Guid id) => _byId.GetValueOrDefault(id);

    /// <summary>All entities, depth-first in tree order.</summary>
    public IEnumerable<Entity> All()
    {
        foreach (var r in _roots)
            foreach (var e in r.SelfAndDescendants())
                yield return e;
    }

    /// <summary>All entities that carry a component of type <typeparamref name="T"/>.</summary>
    public IEnumerable<(Entity Entity, T Component)> Query<T>() where T : Component
    {
        foreach (var e in All())
            foreach (var c in e.Components)
                if (c is T t) yield return (e, t);
    }

    public Layer? FindLayer(Guid id) => Layers.FirstOrDefault(l => l.Id == id);

    internal void Raise(SceneChange change)
    {
        Version++;
        Changed?.Invoke(change);
    }

    /// <summary>For changes to scene-level data (settings, layers).</summary>
    public void NotifySettingsChanged() => Raise(new SceneChange(SceneChangeKind.SettingsChanged, null));
}

public sealed class Layer
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; set; } = "Layer";
    public bool Visible { get; set; } = true;
    public bool Locked { get; set; }
}

public enum SceneChangeKind
{
    Added,
    Removed,
    Reparented,
    Renamed,
    TransformChanged,
    ComponentAdded,
    ComponentRemoved,
    ComponentChanged,
    SettingsChanged,
}

/// <summary>What changed. <see cref="Entity"/> is null only for scene-level changes.</summary>
public sealed record SceneChange(SceneChangeKind Kind, Entity? Entity, Component? Component = null)
{
    public Entity? FormerParent { get; init; }
}

/// <summary>Scene-wide settings stored in the project.</summary>
public sealed class SceneSettings
{
    /// <summary>Display units in the UI (the engine itself is always metric).</summary>
    public UnitSystem DisplayUnits { get; set; } = UnitSystem.Metric;

    /// <summary>Uniform haze everywhere, as volume scattering coefficient in 1/m (0 = clean air).</summary>
    public float BaseHazeScattering { get; set; } = 0.02f;

    /// <summary>Henyey–Greenstein anisotropy of haze particles (0 = isotropic, ~0.6–0.85 for stage haze).</summary>
    public float HazeAnisotropy { get; set; } = 0.7f;

    /// <summary>Luminance of the surroundings in cd/m² (a dark venue is close to 0).</summary>
    public float AmbientLuminance { get; set; } = 0.05f;

    /// <summary>Viewport grid spacing in metres.</summary>
    public float GridSpacing { get; set; } = 1f;
}

public enum UnitSystem
{
    Metric,
    Imperial,
}
