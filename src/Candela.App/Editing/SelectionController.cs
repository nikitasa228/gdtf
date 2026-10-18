using System.Numerics;
using Candela.Core.Editing;
using Candela.Core.Math;
using Candela.Core.SceneGraph;
using SceneModel = Candela.Core.SceneGraph.Scene;

namespace Candela.App.Editing;

/// <summary>
/// Selection rules of the viewports over Core's <see cref="Selection"/>: click selects one object (or clears on the
/// background), Shift+click adds or removes (requirement), a drag box selects what it encloses (Shift adds to the
/// selection; only objects lying entirely inside the box count, so a box drawn around props on a stage does not also
/// grab the floor or the walls behind them), Ctrl+A selects everything selectable, Escape clears. Hidden and locked entities (or entities on hidden or
/// locked layers) are never selected.
/// </summary>
internal static class SelectionController
{
    /// <summary>Projects a world point (m) to screen pixels; false when the point cannot be shown (behind the camera).</summary>
    public delegate bool ProjectToScreen(Vector3 world, out Vector2 screen);

    /// <summary>Whether an entity may be selected from the viewport.</summary>
    public static bool IsSelectable(SceneModel scene, Entity entity)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(entity);
        if (entity.Locked || !entity.IsVisibleInHierarchy)
            return false;
        var layer = SceneLookup.LayerOf(scene, entity.LayerId);
        return layer is null || (layer.Visible && !layer.Locked);
    }

    /// <summary>
    /// Whether an entity may be moved or rotated: not locked itself and not on a locked layer (a locked entity can still
    /// be selected from the issues panel, but the gizmo and the transform fields leave it alone).
    /// </summary>
    public static bool IsEditable(SceneModel scene, Entity entity)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(entity);
        if (entity.Locked)
            return false;
        var layer = SceneLookup.LayerOf(scene, entity.LayerId);
        return layer is null || !layer.Locked;
    }

    /// <summary>
    /// The entities a transform of the selection moves, in selection order: every selected editable entity that is still
    /// in the scene and has no editable selected ancestor (that ancestor already carries it along). A child selected
    /// together with its parent is therefore moved once, not twice; a selected child of a locked parent moves alone.
    /// </summary>
    public static List<Entity> MovableRoots(SceneModel scene, Selection selection)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(selection);
        var candidates = new List<Entity>();
        foreach (var id in selection.Ids)
            if (scene.Find(id) is { } e && IsEditable(scene, e))
                candidates.Add(e);
        var result = new List<Entity>(candidates.Count);
        foreach (var e in candidates)
        {
            bool carried = false;
            for (var p = e.Parent; p is not null && !carried; p = p.Parent)
                carried = selection.Contains(p.Id) && IsEditable(scene, p);
            if (!carried)
                result.Add(e);
        }

        return result;
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

    /// <summary>
    /// True when the box <paramref name="bounds"/> lies entirely inside the screen rectangle (<paramref name="min"/>,
    /// <paramref name="max"/>): all eight corners project in front of the camera and into the rectangle.
    /// <paramref name="project"/> maps a world point to screen pixels (false when it is behind the camera). Empty bounds
    /// are never enclosed.
    /// </summary>
    public static bool Encloses(Aabb bounds, Vector2 min, Vector2 max, ProjectToScreen project)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (bounds.IsEmpty)
            return false;
        for (int i = 0; i < 8; i++)
        {
            var corner = new Vector3((i & 1) == 0 ? bounds.Min.X : bounds.Max.X, (i & 2) == 0 ? bounds.Min.Y : bounds.Max.Y,
                (i & 4) == 0 ? bounds.Min.Z : bounds.Max.Z);
            if (!project(corner, out var p) || p.X < min.X || p.X > max.X || p.Y < min.Y || p.Y > max.Y)
                return false;
        }

        return true;
    }

    /// <summary>Half size (m) of the box framed around an entity without geometry (a light, a group, a cable point).</summary>
    public const float PointFrameHalfSize = 0.5f;

    /// <summary>
    /// What Frame Selection (F) and the issues panel frame: the union of the geometry bounds of <paramref name="ids"/>
    /// (from <paramref name="boundsOf"/>), with entities that have no geometry contributing a
    /// <see cref="PointFrameHalfSize"/> box around their origin — so selecting a light or a group frames it rather than
    /// the whole stage. Empty when none of the ids is in the scene.
    /// </summary>
    public static Aabb FramingBounds(SceneModel scene, IReadOnlyList<Guid> ids, BoundsLookup boundsOf)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(boundsOf);
        var bounds = Aabb.Empty;
        for (int i = 0; i < ids.Count; i++)
        {
            if (boundsOf(ids[i], out var b) && !b.IsEmpty)
            {
                bounds.Include(b);
            }
            else if (scene.Find(ids[i]) is { } e)
            {
                var p = e.WorldPosition;
                bounds.Include(new Aabb(p - new Vector3(PointFrameHalfSize), p + new Vector3(PointFrameHalfSize)));
            }
        }

        return bounds;
    }

    /// <summary>World bounds of an entity's geometry; false when it has none.</summary>
    public delegate bool BoundsLookup(Guid entityId, out Aabb bounds);

    /// <summary>Normalised screen rectangle of a drag from <paramref name="a"/> to <paramref name="b"/>.</summary>
    public static (Vector2 Min, Vector2 Max) Rect(Vector2 a, Vector2 b) => (Vector2.Min(a, b), Vector2.Max(a, b));

    /// <summary>True when the drag is long enough to be a box selection rather than a click (pixels).</summary>
    public static bool IsBoxDrag(Vector2 a, Vector2 b, float threshold = 4f) => Vector2.Distance(a, b) >= threshold;
}
