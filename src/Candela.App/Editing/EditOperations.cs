using System.Numerics;
using Candela.Core.Editing;
using Candela.Core.SceneGraph;
using SceneModel = Candela.Core.SceneGraph.Scene;

namespace Candela.App.Editing;

/// <summary>
/// Edit menu operations on the selection, each one undoable step: Delete, Duplicate (Core's
/// <see cref="DuplicateEntitiesCommand"/>: copies next to their originals, references among the copies remapped — a
/// duplicated truss keeps its fixtures mounted on the copy — and references to other entities kept), Copy / Cut / Paste
/// through an editor clipboard of detached copies made by Core's <see cref="EntityCloner"/> (world placement kept;
/// every paste gets fresh ids; on paste a reference stays only when the scene still has its target), Select All.
/// Children selected together with their parent follow the parent once.
/// </summary>
internal sealed class EditOperations
{
    private IReadOnlyList<Entity> _clipboard = Array.Empty<Entity>();

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

    /// <summary>Copies the selected entities (with their subtrees) to the clipboard. Returns the number copied.</summary>
    public int Copy(SceneModel scene, Selection selection)
    {
        var targets = SelectionController.TopLevel(scene, selection);
        if (targets.Count == 0)
            return 0;
        // Detached copies taken now: later edits of the originals do not change what is pasted. Nested entities become
        // roots that keep their world placement.
        _clipboard = EntityCloner.Clone(targets, new EntityCloneOptions { RootsKeepWorldTransform = true }).Roots;
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
    /// Adds copies of the clipboard at the scene root, at their copied world placement plus <paramref name="offset"/>
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
        var copies = EntityCloner.Clone(_clipboard, new EntityCloneOptions
        {
            TargetScene = scene,
            ExternalReferences = ExternalReferenceHandling.KeepIfResolvable,
        });
        if (copies.Roots.Count == 0)
            return created;
        foreach (var root in copies.Roots)
        {
            var local = root.Local;
            local.Position += offset;
            root.Local = local;
        }

        undo.Execute(new AddEntitiesCommand(scene, copies.Roots,
            description: copies.Roots.Count == 1 ? $"Paste {copies.Roots[0].Name}" : $"Paste {copies.Roots.Count} objects"));
        foreach (var root in copies.Roots)
            created.Add(root.Id);
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
        var command = new DuplicateEntitiesCommand(scene, targets);
        undo.Execute(command);
        foreach (var copy in command.Copies)
            created.Add(copy.Id);
        selection.Set(created);
        return created;
    }
}
