using Candela.Core.Math;
using Candela.Core.SceneGraph;
using Candela.Core.Serialization;
using SceneModel = Candela.Core.SceneGraph.Scene;

namespace Candela.Core.Editing;

/// <summary>Adds an entity (with its subtree) to the scene.</summary>
public sealed class AddEntityCommand(SceneModel scene, Entity entity, Entity? parent = null) : IUndoableCommand
{
    public string Description => $"Add {entity.Name}";
    public Entity Entity => entity;

    public void Do() => scene.Add(entity, parent);
    public void Undo() => scene.Remove(entity);
}

/// <summary>Removes an entity and restores it at the same place on undo.</summary>
public sealed class RemoveEntityCommand(SceneModel scene, Entity entity) : IUndoableCommand
{
    private Entity? _parent;
    private int _index;

    public string Description => $"Delete {entity.Name}";

    public void Do() => (_parent, _index) = scene.Remove(entity);
    public void Undo() => scene.Add(entity, _parent, _index);
}

/// <summary>Moves entities under a new parent keeping their world placement.</summary>
public sealed class ReparentCommand(SceneModel scene, Entity entity, Entity? newParent) : IUndoableCommand
{
    private Entity? _oldParent;
    private int _oldIndex;
    private Transform _oldLocal;

    public string Description => $"Move {entity.Name} into {newParent?.Name ?? "scene root"}";

    public void Do()
    {
        _oldParent = entity.Parent;
        _oldIndex = _oldParent is null ? scene.Roots.ToList().IndexOf(entity) : _oldParent.Children.ToList().IndexOf(entity);
        _oldLocal = entity.Local;
        scene.Reparent(entity, newParent);
    }

    public void Undo()
    {
        scene.Reparent(entity, _oldParent, keepWorldTransform: false, index: _oldIndex);
        entity.Local = _oldLocal;
    }
}

/// <summary>
/// Sets local transforms of one or more entities. Consecutive commands for the same set of entities merge, so a gizmo
/// drag is a single undo step (call <see cref="UndoStack.BreakMerge"/> when the drag ends).
/// </summary>
public sealed class SetTransformCommand : IUndoableCommand
{
    private readonly Entity[] _entities;
    private readonly Transform[] _before;
    private Transform[] _after;

    public SetTransformCommand(IReadOnlyList<Entity> entities, IReadOnlyList<Transform> newLocals, string description = "Move")
    {
        if (entities.Count != newLocals.Count) throw new ArgumentException("One transform per entity is required.");
        _entities = entities.ToArray();
        _before = _entities.Select(e => e.Local).ToArray();
        _after = newLocals.ToArray();
        Description = description;
    }

    public string Description { get; }

    public void Do()
    {
        for (int i = 0; i < _entities.Length; i++) _entities[i].Local = _after[i];
    }

    public void Undo()
    {
        for (int i = 0; i < _entities.Length; i++) _entities[i].Local = _before[i];
    }

    public bool TryMerge(IUndoableCommand next)
    {
        if (next is not SetTransformCommand t || t.Description != Description || !t._entities.SequenceEqual(_entities)) return false;
        _after = t._after;
        return true;
    }
}

/// <summary>Generic property change through a getter/setter pair (names, visibility, numeric fields…).</summary>
public sealed class SetValueCommand<T>(string description, Func<T> get, Action<T> set, T value, object? mergeKey = null)
    : IUndoableCommand
{
    private T _before = get();
    private T _after = value;

    public string Description { get; } = description;
    public object? MergeKey { get; } = mergeKey;

    public void Do() => set(_after);
    public void Undo() => set(_before);

    public bool TryMerge(IUndoableCommand next)
    {
        if (MergeKey is null || next is not SetValueCommand<T> s || !Equals(s.MergeKey, MergeKey)) return false;
        _after = s._after;
        return true;
    }
}

/// <summary>Attaches a component to an entity.</summary>
public sealed class AddComponentCommand(Entity entity, Component component) : IUndoableCommand
{
    public string Description => $"Add {component.GetType().Name.Replace("Component", "")} to {entity.Name}";

    public void Do() => entity.Add(component);
    public void Undo() => entity.Remove(component);
}

/// <summary>Detaches a component from an entity.</summary>
public sealed class RemoveComponentCommand(Entity entity, Component component) : IUndoableCommand
{
    public string Description => $"Remove {component.GetType().Name.Replace("Component", "")} from {entity.Name}";

    public void Do() => entity.Remove(component);
    public void Undo() => entity.Add(component);
}

/// <summary>
/// Edits a component in place by snapshotting its data before and after the edit (JSON). Works for any component
/// without writing a dedicated command: <c>undo.Execute(ModifyComponentCommand.Create(c, x =&gt; x.Width = 5))</c>.
/// </summary>
public sealed class ModifyComponentCommand<T> : IUndoableCommand where T : Component
{
    private readonly T _component;
    private readonly string _before;
    private string _after;

    private ModifyComponentCommand(T component, string before, string after, string description)
    {
        _component = component;
        _before = before;
        _after = after;
        Description = description;
    }

    public string Description { get; }

    /// <summary>Applies <paramref name="edit"/> once to capture the after-state, then reverts so <see cref="Do"/> applies it.</summary>
    public static ModifyComponentCommand<T> Create(T component, Action<T> edit, string? description = null)
    {
        var before = CandelaJson.SerializeComponent(component);
        edit(component);
        var after = CandelaJson.SerializeComponent(component);
        CandelaJson.PopulateComponent(component, before);
        return new ModifyComponentCommand<T>(component, before, after,
            description ?? $"Edit {component.GetType().Name.Replace("Component", "")}");
    }

    public void Do()
    {
        CandelaJson.PopulateComponent(_component, _after);
        _component.NotifyChanged();
    }

    public void Undo()
    {
        CandelaJson.PopulateComponent(_component, _before);
        _component.NotifyChanged();
    }

    public bool TryMerge(IUndoableCommand next)
    {
        if (next is not ModifyComponentCommand<T> m || !ReferenceEquals(m._component, _component) || m.Description != Description) return false;
        _after = m._after;
        return true;
    }
}

public static class ModifyComponentCommand
{
    public static ModifyComponentCommand<T> Create<T>(T component, Action<T> edit, string? description = null) where T : Component =>
        ModifyComponentCommand<T>.Create(component, edit, description);
}
