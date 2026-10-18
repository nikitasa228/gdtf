using System.Numerics;
using Candela.Core.Math;

namespace Candela.Core.SceneGraph;

/// <summary>
/// A node of the scene tree: an id, a name, a local transform and components. Fixtures, walls, cables, trusses,
/// consoles, imported models and groups are all entities with different components. While the entity belongs to a
/// scene, every change of its state (name, transform, layer, visibility, lock, hierarchy, components) raises a
/// <see cref="SceneChange"/> on that scene; a detached entity changes silently.
/// </summary>
public sealed class Entity
{
    private readonly List<Entity> _children = new();
    private readonly List<Component> _components = new();
    private Transform _local = Transform.Identity;
    private Matrix4x4 _world = Matrix4x4.Identity;
    private bool _worldDirty = true;
    private string _name;
    private Guid _layerId;
    private bool _visible = true;
    private bool _locked;

    /// <summary>Creates a detached entity; <paramref name="id"/> defaults to a new random id.</summary>
    public Entity(string name, Guid? id = null)
    {
        _name = name ?? "";
        Id = id ?? Guid.NewGuid();
    }

    /// <summary>Stable identity (MVR uuids are kept on import).</summary>
    public Guid Id { get; }

    /// <summary>Display name; changing it raises <see cref="SceneChangeKind.Renamed"/>. Null is stored as "".</summary>
    public string Name
    {
        get => _name;
        set
        {
            value ??= "";
            if (_name == value) return;
            _name = value;
            Scene?.Raise(new SceneChange(SceneChangeKind.Renamed, this));
        }
    }

    /// <summary>
    /// Id of the <see cref="Layer"/> the entity is on (<see cref="Guid.Empty"/> until it is added to a scene, which assigns
    /// the parent's or the default layer). Changing it raises <see cref="SceneChangeKind.LayerChanged"/>.
    /// </summary>
    public Guid LayerId
    {
        get => _layerId;
        set
        {
            if (_layerId == value) return;
            var former = _layerId;
            _layerId = value;
            Scene?.Raise(new SceneChange(SceneChangeKind.LayerChanged, this) { FormerLayerId = former });
        }
    }

    /// <summary>
    /// Hidden entities (and their descendants) are not rendered and not picked, but still simulated and analysed.
    /// Changing it raises <see cref="SceneChangeKind.VisibilityChanged"/>.
    /// </summary>
    public bool Visible
    {
        get => _visible;
        set
        {
            if (_visible == value) return;
            _visible = value;
            Scene?.Raise(new SceneChange(SceneChangeKind.VisibilityChanged, this));
        }
    }

    /// <summary>
    /// Locked entities cannot be selected or moved in the viewport. Changing it raises
    /// <see cref="SceneChangeKind.LockedChanged"/>.
    /// </summary>
    public bool Locked
    {
        get => _locked;
        set
        {
            if (_locked == value) return;
            _locked = value;
            Scene?.Raise(new SceneChange(SceneChangeKind.LockedChanged, this));
        }
    }

    /// <summary>The scene the entity belongs to, or null while detached.</summary>
    public Scene? Scene { get; internal set; }

    /// <summary>The parent entity, or null for a scene root or a detached subtree root.</summary>
    public Entity? Parent { get; private set; }

    /// <summary>Child entities in order.</summary>
    public IReadOnlyList<Entity> Children => _children;

    /// <summary>Attached components in order.</summary>
    public IReadOnlyList<Component> Components => _components;

    /// <summary>Transform relative to the parent (metres, Z up). Changing it raises <see cref="SceneChangeKind.TransformChanged"/>.</summary>
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

    /// <summary>World position in metres.</summary>
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

    /// <summary>True when this entity and every ancestor are <see cref="Visible"/> (layers are not considered, see
    /// <see cref="SceneGraph.Scene.IsEntityVisible"/>). Allocation-free.</summary>
    public bool IsVisibleInHierarchy
    {
        get
        {
            for (var e = this; e is not null; e = e.Parent)
                if (!e._visible) return false;
            return true;
        }
    }

    /// <summary>Attaches a detached component and raises <see cref="SceneChangeKind.ComponentAdded"/>.</summary>
    public T Add<T>(T component) where T : Component
    {
        ArgumentNullException.ThrowIfNull(component);
        if (component.Owner is not null) throw new InvalidOperationException("Component is already attached to an entity.");
        component.Owner = this;
        _components.Add(component);
        Scene?.Raise(new SceneChange(SceneChangeKind.ComponentAdded, this, component));
        return component;
    }

    /// <summary>Detaches a component and raises <see cref="SceneChangeKind.ComponentRemoved"/>; false when it is not attached here.</summary>
    public bool Remove(Component component)
    {
        if (component is null || !_components.Remove(component)) return false;
        component.Owner = null;
        Scene?.Raise(new SceneChange(SceneChangeKind.ComponentRemoved, this, component));
        return true;
    }

    /// <summary>The first component of type <typeparamref name="T"/>, or null. Allocation-free.</summary>
    public T? Get<T>() where T : Component
    {
        foreach (var c in _components)
            if (c is T t) return t;
        return null;
    }

    /// <summary>True when a component of type <typeparamref name="T"/> is attached.</summary>
    public bool Has<T>() where T : Component => Get<T>() is not null;

    /// <summary>All components of type <typeparamref name="T"/>.</summary>
    public IEnumerable<T> GetAll<T>() where T : Component => _components.OfType<T>();

    /// <summary>
    /// Attaches <paramref name="child"/> (the root of a detached subtree) under this entity at <paramref name="index"/>
    /// (-1 = last). In a scene this is <see cref="SceneGraph.Scene.Add"/> (the subtree joins the scene, Added is raised);
    /// on a detached entity it assembles a detached hierarchy, e.g. a copy that is added to a scene later in one step.
    /// </summary>
    /// <exception cref="InvalidOperationException">The child belongs to a scene, has a parent, or is this entity or one of its ancestors.</exception>
    public Entity AddChild(Entity child, int index = -1)
    {
        ArgumentNullException.ThrowIfNull(child);
        if (Scene is { } scene) return scene.Add(child, this, index);
        if (child.Scene is not null) throw new InvalidOperationException($"{child} belongs to a scene.");
        if (child.Parent is not null) throw new InvalidOperationException($"{child} already has a parent.");
        if (child == this || child.IsAncestorOf(this)) throw new InvalidOperationException("Cannot parent an entity under itself or its descendant.");
        AttachChild(child, index);
        return child;
    }

    /// <summary>Depth-first: this entity, then all descendants in tree order. One small enumerator per call, no recursion.</summary>
    public IEnumerable<Entity> SelfAndDescendants() => Traverse(this, _children);

    /// <summary>True when this entity is a (direct or indirect) parent of <paramref name="other"/>.</summary>
    public bool IsAncestorOf(Entity other)
    {
        ArgumentNullException.ThrowIfNull(other);
        for (var p = other.Parent; p is not null; p = p.Parent)
            if (p == this) return true;
        return false;
    }

    /// <summary>
    /// Pre-order traversal of <paramref name="self"/> (when not null) followed by the subtrees of
    /// <paramref name="top"/>, with an explicit stack (deep trees cannot overflow the call stack).
    /// </summary>
    internal static IEnumerable<Entity> Traverse(Entity? self, List<Entity> top)
    {
        if (self is not null) yield return self;
        Stack<(List<Entity> List, int Next)>? stack = null;
        var list = top;
        int next = 0;
        while (true)
        {
            if (next < list.Count)
            {
                var e = list[next++];
                yield return e;
                if (e._children.Count > 0)
                {
                    (stack ??= new Stack<(List<Entity>, int)>()).Push((list, next));
                    list = e._children;
                    next = 0;
                }
            }
            else if (stack is { Count: > 0 })
            {
                (list, next) = stack.Pop();
            }
            else
            {
                yield break;
            }
        }
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

    /// <inheritdoc/>
    public override string ToString() => $"{Name} ({Id})";
}
