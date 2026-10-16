using System.Numerics;
using Candela.Core.Math;

namespace Candela.Core.SceneGraph;

/// <summary>
/// A node of the scene tree: an id, a name, a local transform and components. Fixtures, walls, cables, trusses,
/// consoles, imported models and groups are all entities with different components.
/// </summary>
public sealed class Entity
{
    private readonly List<Entity> _children = new();
    private readonly List<Component> _components = new();
    private Transform _local = Transform.Identity;
    private Matrix4x4 _world = Matrix4x4.Identity;
    private bool _worldDirty = true;
    private string _name;

    public Entity(string name, Guid? id = null)
    {
        _name = name;
        Id = id ?? Guid.NewGuid();
    }

    /// <summary>Stable identity (MVR uuids are kept on import).</summary>
    public Guid Id { get; }

    public string Name
    {
        get => _name;
        set
        {
            if (_name == value) return;
            _name = value;
            Scene?.Raise(new SceneChange(SceneChangeKind.Renamed, this));
        }
    }

    public Guid LayerId { get; set; }

    /// <summary>Hidden entities are not rendered and not picked, but still simulated and analysed.</summary>
    public bool Visible { get; set; } = true;

    /// <summary>Locked entities cannot be selected or moved in the viewport.</summary>
    public bool Locked { get; set; }

    public Scene? Scene { get; internal set; }
    public Entity? Parent { get; private set; }
    public IReadOnlyList<Entity> Children => _children;
    public IReadOnlyList<Component> Components => _components;

    /// <summary>Transform relative to the parent (metres, Z up).</summary>
    public Transform Local
    {
        get => _local;
        set
        {
            if (_local == value) return;
            _local = value;
            MarkWorldDirty();
            Scene?.Raise(new SceneChange(SceneChangeKind.TransformChanged, this));
        }
    }

    /// <summary>Local-to-world matrix (cached, recomputed lazily after any ancestor moves).</summary>
    public Matrix4x4 WorldMatrix
    {
        get
        {
            if (_worldDirty)
            {
                var local = _local.ToMatrix();
                _world = Parent is null ? local : local * Parent.WorldMatrix;
                _worldDirty = false;
            }
            return _world;
        }
    }

    public Vector3 WorldPosition => WorldMatrix.Translation;

    /// <summary>World transform; setting it computes the matching local transform under the current parent.</summary>
    public Transform World
    {
        get => Transform.FromMatrix(WorldMatrix);
        set
        {
            var m = value.ToMatrix();
            if (Parent is not null && Matrix4x4.Invert(Parent.WorldMatrix, out var inv)) m *= inv;
            Local = Transform.FromMatrix(m);
        }
    }

    /// <summary>True when this entity or any ancestor is hidden.</summary>
    public bool IsVisibleInHierarchy => Visible && (Parent?.IsVisibleInHierarchy ?? true);

    public T Add<T>(T component) where T : Component
    {
        if (component.Owner is not null) throw new InvalidOperationException("Component is already attached to an entity.");
        component.Owner = this;
        _components.Add(component);
        Scene?.Raise(new SceneChange(SceneChangeKind.ComponentAdded, this, component));
        return component;
    }

    public bool Remove(Component component)
    {
        if (!_components.Remove(component)) return false;
        component.Owner = null;
        Scene?.Raise(new SceneChange(SceneChangeKind.ComponentRemoved, this, component));
        return true;
    }

    public T? Get<T>() where T : Component
    {
        foreach (var c in _components)
            if (c is T t) return t;
        return null;
    }

    public bool Has<T>() where T : Component => Get<T>() is not null;

    public IEnumerable<T> GetAll<T>() where T : Component => _components.OfType<T>();

    /// <summary>Depth-first: this entity, then all descendants.</summary>
    public IEnumerable<Entity> SelfAndDescendants()
    {
        yield return this;
        foreach (var c in _children)
            foreach (var d in c.SelfAndDescendants())
                yield return d;
    }

    public bool IsAncestorOf(Entity other)
    {
        for (var p = other.Parent; p is not null; p = p.Parent)
            if (p == this) return true;
        return false;
    }

    internal void AttachChild(Entity child, int index)
    {
        if (index < 0 || index > _children.Count) index = _children.Count;
        _children.Insert(index, child);
        child.Parent = this;
        child.MarkWorldDirty();
    }

    internal int DetachChild(Entity child)
    {
        int i = _children.IndexOf(child);
        if (i >= 0) _children.RemoveAt(i);
        child.Parent = null;
        child.MarkWorldDirty();
        return i;
    }

    internal void ClearParentForRoot() => Parent = null;

    internal void NotifyComponentChanged(Component c) =>
        Scene?.Raise(new SceneChange(SceneChangeKind.ComponentChanged, this, c));

    private void MarkWorldDirty()
    {
        if (_worldDirty && _children.Count == 0) return;
        _worldDirty = true;
        foreach (var c in _children) c.MarkWorldDirty();
    }

    public override string ToString() => $"{Name} ({Id})";
}
