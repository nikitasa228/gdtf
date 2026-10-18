using System.Numerics;
using Candela.Core.Editing;
using Candela.Core.SceneGraph;
using SceneModel = Candela.Core.SceneGraph.Scene;

namespace Candela.App.Editing;

/// <summary>
/// Selection rules of the viewports over Core's <see cref="Selection"/>: click selects one object (or clears on the
/// background), Shift+click adds or removes (requirement), a drag box selects what it encloses (Shift adds to the
/// selection), Ctrl+A selects everything selectable, Escape clears. Hidden and locked entities (or entities on hidden or
/// locked layers) are never selected.
/// </summary>
internal static class SelectionController
{
    /// <summary>Whether an entity may be selected from the viewport.</summary>
    public static bool IsSelectable(SceneModel scene, Entity entity)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(entity);
        if (entity.Locked || !entity.IsVisibleInHierarchy)
            return false;
        var layer = scene.FindLayer(entity.LayerId);
        return layer is null || (layer.Visible && !layer.Locked);
    }

    /// <summary>
    /// Applies a click on <paramref name="hit"/> (null = background). Without Shift the selection becomes the hit (or
    /// empty); with Shift the hit is toggled and a background click keeps the selection.
    /// </summary>
    public static void Click(SceneModel scene, Selection selection, Guid? hit, bool shift)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var entity = hit is { } id ? scene.Find(id) : null;
        if (entity is not null && !IsSelectable(scene, entity))
            entity = null;
        if (shift)
        {
            if (entity is not null)
                selection.Toggle(entity.Id);
            return;
        }

        if (entity is null)
            selection.Clear();
        else
            selection.Set(entity.Id);
    }

    /// <summary>
    /// Applies a box selection: <paramref name="inside"/> are the entities whose screen projection lies in the box.
    /// Without Shift they replace the selection; with Shift they are added.
    /// </summary>
    public static void Box(SceneModel scene, Selection selection, IEnumerable<Guid> inside, bool shift)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(inside);
        var ids = new List<Guid>();
        foreach (var id in inside)
            if (scene.Find(id) is { } e && IsSelectable(scene, e))
                ids.Add(id);
        if (shift)
        {
            foreach (var id in ids)
                if (!selection.Contains(id))
                    selection.Add(id);
        }
        else
        {
            selection.Set(ids);
        }
    }

    /// <summary>Selects every selectable top-level entity (children move with their parents).</summary>
    public static void SelectAll(SceneModel scene, Selection selection)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(selection);
        var ids = new List<Guid>();
        foreach (var root in scene.Roots)
            if (IsSelectable(scene, root))
                ids.Add(root.Id);
        selection.Set(ids);
    }

    /// <summary>
    /// The selected entities whose ancestors are not selected (editing a parent already moves/deletes its children),
    /// in selection order.
    /// </summary>
    public static List<Entity> TopLevel(SceneModel scene, Selection selection)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(selection);
        var result = new List<Entity>();
        foreach (var id in selection.Ids)
        {
            if (scene.Find(id) is not { } e)
                continue;
            bool ancestorSelected = false;
            for (var p = e.Parent; p is not null; p = p.Parent)
            {
                if (selection.Contains(p.Id))
                {
                    ancestorSelected = true;
                    break;
                }
            }

            if (!ancestorSelected)
                result.Add(e);
        }

        return result;
    }

    /// <summary>Normalised screen rectangle of a drag from <paramref name="a"/> to <paramref name="b"/>.</summary>
    public static (Vector2 Min, Vector2 Max) Rect(Vector2 a, Vector2 b) => (Vector2.Min(a, b), Vector2.Max(a, b));

    /// <summary>True when the drag is long enough to be a box selection rather than a click (pixels).</summary>
    public static bool IsBoxDrag(Vector2 a, Vector2 b, float threshold = 4f) => Vector2.Distance(a, b) >= threshold;
}
