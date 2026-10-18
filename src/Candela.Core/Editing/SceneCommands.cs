using Candela.Core.Math;
using Candela.Core.SceneGraph;
using Candela.Core.Serialization;
using SceneModel = Candela.Core.SceneGraph.Scene;

namespace Candela.Core.Editing;

/// <summary>
/// Adds an entity (with its subtree) to the scene. The description ("Add &lt;name&gt;") is fixed when the command is
/// created.
/// </summary>
/// <param name="scene">Scene to add to.</param>
/// <param name="entity">A detached entity (the root of the subtree to add).</param>
/// <param name="parent">Parent in the scene, or null for a root.</param>
/// <param name="index">Sibling position (-1 = last).</param>
public sealed class AddEntityCommand(SceneModel scene, Entity entity, Entity? parent = null, int index = -1) : IUndoableCommand
{
    /// <inheritdoc/>
    public string Description { get; } = $"Add {entity.Name}";

    /// <summary>The entity added by <see cref="Do"/>.</summary>
    public Entity Entity => entity;

    /// <inheritdoc/>
    public void Do() => scene.Add(entity, parent, index);

    /// <inheritdoc/>
    public void Undo() => scene.Remove(entity);
}

/// <summary>
/// Adds several detached subtrees (the roots of a paste, a duplicate or a merge import) as one undo step, each under its
/// own parent. The description is fixed when the command is created.
/// </summary>
public sealed class AddEntitiesCommand : IUndoableCommand
{
    private readonly SceneModel _scene;
    private readonly (Entity Entity, Entity? Parent, int Index)[] _items;

    /// <summary>Adds <paramref name="entities"/> under <paramref name="parent"/> (null = scene roots), in order, after the existing children.</summary>
    /// <param name="scene">Scene to add to.</param>
    /// <param name="entities">Detached subtree roots.</param>
    /// <param name="parent">Common parent in the scene, or null for roots.</param>
    /// <param name="description">Undo text; default "Add &lt;name&gt;" or "Add &lt;n&gt; objects".</param>
    public AddEntitiesCommand(SceneModel scene, IEnumerable<Entity> entities, Entity? parent = null, string? description = null)
        : this(scene, entities.Select(e => (e, parent, -1)), description)
    {
    }

    /// <summary>Adds each entity under its own parent at its own sibling position (-1 = last), in order.</summary>
    /// <param name="scene">Scene to add to.</param>
    /// <param name="items">Detached subtree roots with their parent (null = root) and sibling position.</param>
    /// <param name="description">Undo text; default "Add &lt;name&gt;" or "Add &lt;n&gt; objects".</param>
    public AddEntitiesCommand(SceneModel scene, IEnumerable<(Entity Entity, Entity? Parent, int Index)> items, string? description = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(items);
        _scene = scene;
        _items = items.ToArray();
        foreach (var (e, _, _) in _items) ArgumentNullException.ThrowIfNull(e, nameof(items));
        Entities = Array.AsReadOnly(_items.Select(i => i.Entity).ToArray());
        Description = description ?? (_items.Length == 1 ? $"Add {_items[0].Entity.Name}" : $"Add {_items.Length} objects");
    }

    /// <inheritdoc/>
    public string Description { get; }

    /// <summary>The subtree roots added by <see cref="Do"/>, in order.</summary>
    public IReadOnlyList<Entity> Entities { get; }

    /// <inheritdoc/>
    public void Do()
    {
        foreach (var (e, parent, index) in _items) _scene.Add(e, parent, index);
    }

    /// <inheritdoc/>
    public void Undo()
    {
        for (int i = _items.Length - 1; i >= 0; i--)
            if (_items[i].Entity.Scene == _scene) _scene.Remove(_items[i].Entity);
    }
}

/// <summary>
/// Duplicates entities in place as one undo step: each copy (with its subtree, new ids) is added under its original's
/// parent, after the existing children. References among the copied entities point at the copies (a duplicated truss
/// with its fixtures keeps them mounted on the copy); references to other entities are kept (the copy of a fixture still
/// hangs on the original truss). The copies are made when the command is created; redo re-adds the same copies.
/// </summary>
public sealed class DuplicateEntitiesCommand : IUndoableCommand
{
    private readonly SceneModel _scene;
    private readonly Entity?[] _parents;

    /// <summary>Copies <paramref name="entities"/> (entities of <paramref name="scene"/>; one listed with its ancestor is copied once).</summary>
    /// <exception cref="ArgumentException">An entity is not in <paramref name="scene"/>.</exception>
    public DuplicateEntitiesCommand(SceneModel scene, IEnumerable<Entity> entities)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(entities);
        var list = entities.Where(e => e is not null).ToList();
        foreach (var e in list)
            if (e.Scene != scene) throw new ArgumentException($"{e} is not in the scene.", nameof(entities));
        _scene = scene;
        Result = EntityCloner.Clone(list, new EntityCloneOptions { TargetScene = scene, ExternalReferences = ExternalReferenceHandling.Keep });
        _parents = Result.Originals.Select(o => o.Parent).ToArray();
        Description = Result.Originals.Count == 1 ? $"Duplicate {Result.Originals[0].Name}" : $"Duplicate {Result.Originals.Count} objects";
    }

    /// <inheritdoc/>
    public string Description { get; }

    /// <summary>The copies and what happened to their references.</summary>
    public EntityCloneResult Result { get; }

    /// <summary>The copied subtree roots (select them after <see cref="Do"/>).</summary>
    public IReadOnlyList<Entity> Copies => Result.Roots;

    /// <inheritdoc/>
    public void Do()
    {
        for (int i = 0; i < Result.Roots.Count; i++)
        {
            var parent = _parents[i];
            _scene.Add(Result.Roots[i], parent is not null && parent.Scene == _scene ? parent : null);
        }
    }

    /// <inheritdoc/>
    public void Undo()
    {
        for (int i = Result.Roots.Count - 1; i >= 0; i--)
            if (Result.Roots[i].Scene == _scene) _scene.Remove(Result.Roots[i]);
    }
}

/// <summary>Removes an entity and restores it at the same place on undo. The description is fixed when the command is created.</summary>
/// <param name="scene">Scene to remove from.</param>
/// <param name="entity">Entity (with its subtree) to remove.</param>
public sealed class RemoveEntityCommand(SceneModel scene, Entity entity) : IUndoableCommand
{
    private Entity? _parent;
    private int _index;

    /// <inheritdoc/>
    public string Description { get; } = $"Delete {entity.Name}";

    /// <inheritdoc/>
    public void Do() => (_parent, _index) = scene.Remove(entity);

    /// <inheritdoc/>
    public void Undo() => scene.Add(entity, _parent, _index);
}

/// <summary>
/// Moves an entity under a new parent keeping its world placement; undo restores the old parent, sibling position and
/// local transform. The description is fixed when the command is created.
/// </summary>
/// <param name="scene">Scene the entity is in.</param>
/// <param name="entity">Entity to move.</param>
/// <param name="newParent">New parent, or null for the scene root.</param>
public sealed class ReparentCommand(SceneModel scene, Entity entity, Entity? newParent) : IUndoableCommand
{
    private Entity? _oldParent;
    private int _oldIndex;
    private Transform _oldLocal;

    /// <inheritdoc/>
    public string Description { get; } = $"Move {entity.Name} into {newParent?.Name ?? "scene root"}";

    /// <inheritdoc/>
    public void Do()
    {
        _oldParent = entity.Parent;
        _oldIndex = IndexOf(_oldParent is null ? scene.Roots : _oldParent.Children, entity);
        _oldLocal = entity.Local;
        scene.Reparent(entity, newParent);
    }

    /// <inheritdoc/>
    public void Undo()
    {
        scene.Reparent(entity, _oldParent, keepWorldTransform: false, index: _oldIndex);
        entity.Local = _oldLocal;
    }

    private static int IndexOf(IReadOnlyList<Entity> list, Entity entity)
    {
        for (int i = 0; i < list.Count; i++)
            if (ReferenceEquals(list[i], entity)) return i;
        return -1;
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

    /// <summary>Captures the current local transforms of <paramref name="entities"/> as the undo state.</summary>
    /// <param name="entities">Entities to move.</param>
    /// <param name="newLocals">New local transform per entity (metres, Z up).</param>
    /// <param name="description">Undo text.</param>
    public SetTransformCommand(IReadOnlyList<Entity> entities, IReadOnlyList<Transform> newLocals, string description = "Move")
    {
        if (entities.Count != newLocals.Count) throw new ArgumentException("One transform per entity is required.");
        _entities = entities.ToArray();
        _before = _entities.Select(e => e.Local).ToArray();
        _after = newLocals.ToArray();
        Description = description;
    }

    /// <inheritdoc/>
    public string Description { get; }

    /// <inheritdoc/>
    public void Do()
    {
        for (int i = 0; i < _entities.Length; i++) _entities[i].Local = _after[i];
    }

    /// <inheritdoc/>
    public void Undo()
    {
        for (int i = 0; i < _entities.Length; i++) _entities[i].Local = _before[i];
    }

    /// <inheritdoc/>
    public bool TryMerge(IUndoableCommand next)
    {
        if (next is not SetTransformCommand t || t.Description != Description || !t._entities.SequenceEqual(_entities)) return false;
        _after = t._after;
        return true;
    }
}

/// <summary>
/// Generic property change through a getter/setter pair (names, visibility, locks, layers, numeric fields…). Entity,
/// layer and scene-settings setters raise their own scene changes, so undo and redo are seen by every view. For a
/// property of a component use <see cref="SetValueCommand.ForComponent"/>, which also raises
/// <see cref="SceneChangeKind.ComponentChanged"/>.
/// </summary>
/// <param name="description">Undo text (stored as given).</param>
/// <param name="get">Reads the current value (called once, when the command is created).</param>
/// <param name="set">Writes a value (on do, undo and redo).</param>
/// <param name="value">The new value.</param>
/// <param name="mergeKey">When not null, consecutive commands with an equal key merge into one undo step (slider drags).</param>
public sealed class SetValueCommand<T>(string description, Func<T> get, Action<T> set, T value, object? mergeKey = null)
    : IUndoableCommand
{
    private T _before = get();
    private T _after = value;

    /// <inheritdoc/>
    public string Description { get; } = description;

    /// <summary>Commands with equal non-null keys merge.</summary>
    public object? MergeKey { get; } = mergeKey;

    /// <inheritdoc/>
    public void Do() => set(_after);

    /// <inheritdoc/>
    public void Undo() => set(_before);

    /// <inheritdoc/>
    public bool TryMerge(IUndoableCommand next)
    {
        if (MergeKey is null || next is not SetValueCommand<T> s || !Equals(s.MergeKey, MergeKey)) return false;
        _after = s._after;
        return true;
    }
}

/// <summary>Factories for <see cref="SetValueCommand{T}"/>.</summary>
public static class SetValueCommand
{
    /// <summary>
    /// A property change of <paramref name="component"/> that raises <see cref="SceneChangeKind.ComponentChanged"/> after
    /// every write (do, undo, redo) — plain component setters are silent.
    /// </summary>
    /// <param name="component">The component the setter writes to.</param>
    /// <param name="description">Undo text.</param>
    /// <param name="get">Reads the current value.</param>
    /// <param name="set">Writes a value into the component.</param>
    /// <param name="value">The new value.</param>
    /// <param name="mergeKey">Optional merge key (see <see cref="SetValueCommand{T}"/>).</param>
    public static SetValueCommand<T> ForComponent<T>(Component component, string description, Func<T> get, Action<T> set, T value,
        object? mergeKey = null)
    {
        ArgumentNullException.ThrowIfNull(component);
        ArgumentNullException.ThrowIfNull(set);
        return new SetValueCommand<T>(description, get, v =>
        {
            set(v);
            component.NotifyChanged();
        }, value, mergeKey);
    }
}

/// <summary>Attaches a component to an entity. The description is fixed when the command is created.</summary>
/// <param name="entity">Entity to attach to.</param>
/// <param name="component">A detached component.</param>
public sealed class AddComponentCommand(Entity entity, Component component) : IUndoableCommand
{
    /// <inheritdoc/>
    public string Description { get; } = $"Add {CommandText.ComponentName(component)} to {entity.Name}";

    /// <inheritdoc/>
    public void Do() => entity.Add(component);

    /// <inheritdoc/>
    public void Undo() => entity.Remove(component);
}

/// <summary>Detaches a component from an entity. The description is fixed when the command is created.</summary>
/// <param name="entity">Entity the component is attached to.</param>
/// <param name="component">The component to detach.</param>
public sealed class RemoveComponentCommand(Entity entity, Component component) : IUndoableCommand
{
    /// <inheritdoc/>
    public string Description { get; } = $"Remove {CommandText.ComponentName(component)} from {entity.Name}";

    /// <inheritdoc/>
    public void Do() => entity.Remove(component);

    /// <inheritdoc/>
    public void Undo() => entity.Add(component);
}

/// <summary>
/// Edits a component in place by snapshotting its data before and after the edit (JSON, nulls included so an edit to
/// null is undone and redone exactly). Works for any component without writing a dedicated command:
/// <c>undo.Execute(ModifyComponentCommand.Create(c, x =&gt; x.Width = 5))</c>.
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

    /// <inheritdoc/>
    public string Description { get; }

    /// <summary>Applies <paramref name="edit"/> once to capture the after-state, then reverts so <see cref="Do"/> applies it.</summary>
    public static ModifyComponentCommand<T> Create(T component, Action<T> edit, string? description = null)
    {
        ArgumentNullException.ThrowIfNull(component);
        ArgumentNullException.ThrowIfNull(edit);
        var before = CandelaJson.SnapshotComponent(component);
        edit(component);
        var after = CandelaJson.SnapshotComponent(component);
        CandelaJson.PopulateComponent(component, before);
        return new ModifyComponentCommand<T>(component, before, after, description ?? $"Edit {CommandText.ComponentName(component)}");
    }

    /// <inheritdoc/>
    public void Do()
    {
        CandelaJson.PopulateComponent(_component, _after);
        _component.NotifyChanged();
    }

    /// <inheritdoc/>
    public void Undo()
    {
        CandelaJson.PopulateComponent(_component, _before);
        _component.NotifyChanged();
    }

    /// <inheritdoc/>
    public bool TryMerge(IUndoableCommand next)
    {
        if (next is not ModifyComponentCommand<T> m || !ReferenceEquals(m._component, _component) || m.Description != Description) return false;
        _after = m._after;
        return true;
    }
}

/// <summary>Factory for <see cref="ModifyComponentCommand{T}"/> with type inference.</summary>
public static class ModifyComponentCommand
{
    /// <inheritdoc cref="ModifyComponentCommand{T}.Create"/>
    public static ModifyComponentCommand<T> Create<T>(T component, Action<T> edit, string? description = null) where T : Component =>
        ModifyComponentCommand<T>.Create(component, edit, description);
}

/// <summary>Shared wording of command descriptions.</summary>
internal static class CommandText
{
    /// <summary>"FixtureComponent" → "Fixture".</summary>
    public static string ComponentName(Component component) => component.GetType().Name.Replace("Component", "");
}
