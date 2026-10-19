using System.Numerics;
using Candela.Core.Editing;
using Candela.Core.Math;
using Candela.Core.SceneGraph;
using SceneModel = Candela.Core.SceneGraph.Scene;

namespace Candela.App.Editing;

/// <summary>A detached copy of an entity subtree (clipboard content): world placement, flags, component copies, children.</summary>
internal sealed class EntitySnapshot
{
    /// <summary>Entity name.</summary>
    public required string Name { get; init; }

    /// <summary>World transform at copy time (paste keeps the world placement).</summary>
    public required Transform World { get; init; }

    /// <summary>Local transform relative to the parent snapshot (children).</summary>
    public required Transform Local { get; init; }

    /// <summary>Layer id.</summary>
    public Guid Layer { get; init; }

    /// <summary>Visible flag.</summary>
    public bool Visible { get; init; } = true;

    /// <summary>Locked flag.</summary>
    public bool Locked { get; init; }

    /// <summary>Deep copies of the components (cloned again for every paste).</summary>
    public required IReadOnlyList<Component> Components { get; init; }

    /// <summary>Child snapshots.</summary>
    public required IReadOnlyList<EntitySnapshot> Children { get; init; }

    /// <summary>Captures an entity and its subtree.</summary>
    public static EntitySnapshot Capture(Entity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return new EntitySnapshot
        {
            Name = entity.Name,
            World = entity.World,
            Local = entity.Local,
            Layer = entity.LayerId,
            Visible = entity.Visible,
            Locked = entity.Locked,
            Components = entity.Components.Select(c => c.Clone()).ToList(),
            Children = entity.Children.Select(Capture).ToList(),
        };
    }

    /// <summary>Number of entities in this subtree.</summary>
    public int Count => 1 + Children.Sum(c => c.Count);
}

/// <summary>
/// Edit menu operations on the selection, every one an undoable step (grouped, so one Ctrl+Z reverts it): Delete,
/// Duplicate (in place, the copies become the selection), Copy / Cut / Paste through an editor clipboard, Select All.
/// Copies get new ids; references inside components keep pointing at the original entities.
/// </summary>
internal sealed class EditOperations
{
    private readonly List<EntitySnapshot> _clipboard = new();

    /// <summary>True when the clipboard holds entities.</summary>
    public bool CanPaste => _clipboard.Count > 0;

    /// <summary>Number of top-level entities on the clipboard.</summary>
    public int ClipboardCount => _clipboard.Count;

    /// <summary>Deletes the selected entities (children go with their parents). Returns the number deleted.</summary>
    public static int Delete(SceneModel scene, Selection selection, UndoStack undo)
    {
        ArgumentNullException.ThrowIfNull(undo);
        var targets = SelectionController.TopLevel(scene, selection);
        if (targets.Count == 0)
            return 0;
        using (undo.Group(targets.Count == 1 ? $"Delete {targets[0].Name}" : $"Delete {targets.Count} objects"))
        {
            foreach (var e in targets)
                undo.Execute(new RemoveEntityCommand(scene, e));
        }

        selection.Clear();
        return targets.Count;
    }

    /// <summary>Copies the selected entities to the clipboard. Returns the number copied.</summary>
    public int Copy(SceneModel scene, Selection selection)
    {
        var targets = SelectionController.TopLevel(scene, selection);
        if (targets.Count == 0)
            return 0;
        _clipboard.Clear();
        foreach (var e in targets)
            _clipboard.Add(EntitySnapshot.Capture(e));
        return targets.Count;
    }

    /// <summary>Copies, then deletes the selection.</summary>
    public int Cut(SceneModel scene, Selection selection, UndoStack undo)
    {
        int n = Copy(scene, selection);
        if (n > 0)
            Delete(scene, selection, undo);
        return n;
    }

    /// <summary>
    /// Adds copies of the clipboard at the scene root, at their original world placement plus <paramref name="offset"/>
    /// (m), and selects them. Returns the new top-level ids.
    /// </summary>
    public List<Guid> Paste(SceneModel scene, Selection selection, UndoStack undo, Vector3 offset = default)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(undo);
        var created = new List<Guid>();
        if (_clipboard.Count == 0)
            return created;
        using (undo.Group(_clipboard.Count == 1 ? $"Paste {_clipboard[0].Name}" : $"Paste {_clipboard.Count} objects"))
        {
            foreach (var snapshot in _clipboard)
                created.Add(Instantiate(scene, undo, snapshot, null, offset));
        }

        selection.Set(created);
        return created;
    }

    /// <summary>Duplicates the selection in place (the copies become the selection). Returns the new ids.</summary>
    public static List<Guid> Duplicate(SceneModel scene, Selection selection, UndoStack undo)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(undo);
        var targets = SelectionController.TopLevel(scene, selection);
        var created = new List<Guid>();
        if (targets.Count == 0)
            return created;
        using (undo.Group(targets.Count == 1 ? $"Duplicate {targets[0].Name}" : $"Duplicate {targets.Count} objects"))
        {
            foreach (var e in targets)
                created.Add(Instantiate(scene, undo, EntitySnapshot.Capture(e), e.Parent, Vector3.Zero, keepLocal: true));
        }

        selection.Set(created);
        return created;
    }

    private static Guid Instantiate(SceneModel scene, UndoStack undo, EntitySnapshot snapshot, Entity? parent, Vector3 offset, bool keepLocal = false)
    {
        var entity = Build(snapshot, scene);
        if (keepLocal)
        {
            entity.Local = snapshot.Local;
        }
        else
        {
            var world = snapshot.World;
            world.Position += offset;
            entity.Local = world; // root: local = world
        }

        if (parent is not null && parent.Scene != scene)
            parent = null;
        undo.Execute(new AddEntityCommand(scene, entity, parent));
        AddChildren(scene, undo, snapshot, entity);
        return entity.Id;
    }

    private static void AddChildren(SceneModel scene, UndoStack undo, EntitySnapshot snapshot, Entity parent)
    {
        foreach (var childSnapshot in snapshot.Children)
        {
            var child = Build(childSnapshot, scene);
            child.Local = childSnapshot.Local;
            undo.Execute(new AddEntityCommand(scene, child, parent));
            AddChildren(scene, undo, childSnapshot, child);
        }
    }

    private static Entity Build(EntitySnapshot snapshot, SceneModel scene)
    {
        var entity = new Entity(snapshot.Name)
        {
            LayerId = scene.FindLayer(snapshot.Layer) is not null ? snapshot.Layer : scene.DefaultLayer.Id,
            Visible = snapshot.Visible,
            Locked = snapshot.Locked,
        };
        foreach (var c in snapshot.Components)
            entity.Add(c.Clone());
        return entity;
    }
}
