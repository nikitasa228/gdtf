using System.Numerics;
using Candela.Core.Editing;
using Candela.Core.Math;
using Candela.Core.SceneGraph;
using SceneModel = Candela.Core.SceneGraph.Scene;

namespace Candela.App.Editing;

/// <summary>
/// One drag of the move / rotate gizmo: the gizmo manipulates the primary entity's world matrix (the pivot), and every
/// entity the selection moves (<see cref="SelectionController.MovableRoots"/>) follows rigidly — its world matrix is
/// multiplied by the pivot's change since the drag began, so the arrangement turns and moves as one block around the
/// primary object. Each update is a <see cref="SetTransformCommand"/> on the same entity list, so the commands of one
/// drag merge into a single undo step; a new drag never merges into the previous one.
/// </summary>
internal sealed class GizmoDrag
{
    private readonly List<Entity> _entities = new();
    private Matrix4x4[] _startWorld = Array.Empty<Matrix4x4>();
    private Transform[] _startLocal = Array.Empty<Transform>();
    private Matrix4x4 _pivotStart;

    /// <summary>True between <see cref="Begin"/> and <see cref="End"/>.</summary>
    public bool IsActive { get; private set; }

    /// <summary>The entities moved by this drag.</summary>
    public IReadOnlyList<Entity> Entities => _entities;

    /// <summary>Starts a drag of the selection around <paramref name="primary"/> (its current world matrix is the pivot).</summary>
    public void Begin(SceneModel scene, Selection selection, Entity primary, UndoStack undo)
    {
        ArgumentNullException.ThrowIfNull(primary);
        ArgumentNullException.ThrowIfNull(undo);
        _entities.Clear();
        _entities.AddRange(SelectionController.MovableRoots(scene, selection));
        _startWorld = new Matrix4x4[_entities.Count];
        _startLocal = new Transform[_entities.Count];
        for (int i = 0; i < _entities.Count; i++)
        {
            _startWorld[i] = _entities[i].WorldMatrix;
            _startLocal[i] = _entities[i].Local;
        }

        _pivotStart = primary.WorldMatrix;
        IsActive = true;
        undo.BreakMerge();
    }

    /// <summary>
    /// Applies the pivot's current world matrix <paramref name="pivotNow"/> (from the gizmo) to the dragged entities as
    /// one undoable command named <paramref name="description"/> (merged with the drag's earlier commands).
    /// </summary>
    public void Update(Matrix4x4 pivotNow, UndoStack undo, string description)
    {
        ArgumentNullException.ThrowIfNull(undo);
        if (!IsActive || _entities.Count == 0 || !IsFinite(pivotNow))
            return;
        var delta = TransformMath.Delta(_pivotStart, pivotNow);
        var locals = TransformMath.ApplyDelta(_entities, _startWorld, _startLocal, delta);
        undo.Execute(new SetTransformCommand(_entities, locals, description));
    }

    /// <summary>Ends the drag (the next edit starts a new undo step).</summary>
    public void End(UndoStack undo)
    {
        ArgumentNullException.ThrowIfNull(undo);
        if (!IsActive)
            return;
        IsActive = false;
        _entities.Clear();
        undo.BreakMerge();
    }

    private static bool IsFinite(Matrix4x4 m) =>
        float.IsFinite(m.M11) && float.IsFinite(m.M12) && float.IsFinite(m.M13) && float.IsFinite(m.M14) &&
        float.IsFinite(m.M21) && float.IsFinite(m.M22) && float.IsFinite(m.M23) && float.IsFinite(m.M24) &&
        float.IsFinite(m.M31) && float.IsFinite(m.M32) && float.IsFinite(m.M33) && float.IsFinite(m.M34) &&
        float.IsFinite(m.M41) && float.IsFinite(m.M42) && float.IsFinite(m.M43) && float.IsFinite(m.M44);
}
