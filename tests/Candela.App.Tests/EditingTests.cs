using System.Numerics;
using Candela.App.Editing;
using Candela.Core.Editing;
using Candela.Core.Math;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;
using SceneModel = Candela.Core.SceneGraph.Scene;

namespace Candela.App.Tests;

public sealed class SelectionControllerTests
{
    private readonly SceneModel _scene = new("test");
    private readonly Selection _selection = new();
    private readonly Entity _a;
    private readonly Entity _b;
    private readonly Entity _c;

    public SelectionControllerTests()
    {
        _a = _scene.Add(new Entity("A"));
        _b = _scene.Add(new Entity("B"));
        _c = _scene.Add(new Entity("C"));
    }

    [Fact]
    public void Click_selects_one_and_background_clears()
    {
        SelectionController.Click(_scene, _selection, _a.Id, shift: false);
        Assert.Equal(new[] { _a.Id }, _selection.Ids);
        SelectionController.Click(_scene, _selection, _b.Id, shift: false);
        Assert.Equal(new[] { _b.Id }, _selection.Ids);
        SelectionController.Click(_scene, _selection, null, shift: false);
        Assert.True(_selection.IsEmpty);
    }

    [Fact]
    public void Shift_click_adds_and_removes()
    {
        SelectionController.Click(_scene, _selection, _a.Id, shift: false);
        SelectionController.Click(_scene, _selection, _b.Id, shift: true);
        Assert.Equal(new[] { _a.Id, _b.Id }, _selection.Ids);
        Assert.Equal(_b.Id, _selection.Primary);
        SelectionController.Click(_scene, _selection, _a.Id, shift: true);
        Assert.Equal(new[] { _b.Id }, _selection.Ids);
        // Shift-click on the background keeps the selection.
        SelectionController.Click(_scene, _selection, null, shift: true);
        Assert.Equal(new[] { _b.Id }, _selection.Ids);
    }

    [Fact]
    public void Hidden_locked_and_unknown_entities_are_not_selected()
    {
        _a.Visible = false;
        _b.Locked = true;
        SelectionController.Click(_scene, _selection, _a.Id, shift: false);
        Assert.True(_selection.IsEmpty);
        SelectionController.Click(_scene, _selection, _b.Id, shift: true);
        Assert.True(_selection.IsEmpty);
        SelectionController.Click(_scene, _selection, Guid.NewGuid(), shift: false);
        Assert.True(_selection.IsEmpty);
        SelectionController.Box(_scene, _selection, new[] { _a.Id, _b.Id, _c.Id }, shift: false);
        Assert.Equal(new[] { _c.Id }, _selection.Ids);
    }

    [Fact]
    public void Entities_on_hidden_or_locked_layers_are_not_selectable()
    {
        var hidden = new Layer { Name = "hidden", Visible = false };
        var locked = new Layer { Name = "locked", Locked = true };
        _scene.Layers.Add(hidden);
        _scene.Layers.Add(locked);
        _a.LayerId = hidden.Id;
        _b.LayerId = locked.Id;
        Assert.False(SelectionController.IsSelectable(_scene, _a));
        Assert.False(SelectionController.IsSelectable(_scene, _b));
        Assert.True(SelectionController.IsSelectable(_scene, _c));
    }

    [Fact]
    public void Children_of_hidden_parents_are_not_selectable()
    {
        var child = _scene.Add(new Entity("child"), _a);
        _a.Visible = false;
        Assert.False(SelectionController.IsSelectable(_scene, child));
    }

    [Fact]
    public void Box_replaces_or_adds_with_shift()
    {
        SelectionController.Click(_scene, _selection, _a.Id, shift: false);
        SelectionController.Box(_scene, _selection, new[] { _b.Id, _c.Id }, shift: false);
        Assert.Equal(new[] { _b.Id, _c.Id }, _selection.Ids);
        SelectionController.Box(_scene, _selection, new[] { _a.Id, _b.Id }, shift: true);
        Assert.Equal(3, _selection.Count);
        SelectionController.Box(_scene, _selection, Array.Empty<Guid>(), shift: false);
        Assert.True(_selection.IsEmpty);
    }

    [Fact]
    public void Select_all_takes_selectable_roots_only()
    {
        _scene.Add(new Entity("child"), _a);
        _c.Locked = true;
        SelectionController.SelectAll(_scene, _selection);
        Assert.Equal(new[] { _a.Id, _b.Id }, _selection.Ids);
    }

    [Fact]
    public void Top_level_drops_entities_whose_ancestor_is_selected()
    {
        var child = _scene.Add(new Entity("child"), _a);
        var grandChild = _scene.Add(new Entity("grandchild"), child);
        _selection.Set(new[] { grandChild.Id, _a.Id, _b.Id });
        var top = SelectionController.TopLevel(_scene, _selection);
        Assert.Equal(new[] { _a, _b }, top);
    }

    [Fact]
    public void Box_drag_threshold_and_rectangle()
    {
        Assert.False(SelectionController.IsBoxDrag(new Vector2(10, 10), new Vector2(12, 12)));
        Assert.True(SelectionController.IsBoxDrag(new Vector2(10, 10), new Vector2(14, 10)));
        var (min, max) = SelectionController.Rect(new Vector2(50, 5), new Vector2(10, 40));
        Assert.Equal(new Vector2(10, 5), min);
        Assert.Equal(new Vector2(50, 40), max);
    }
}

public sealed class EditOperationsTests
{
    private readonly SceneModel _scene = new("test");
    private readonly Selection _selection = new();
    private readonly UndoStack _undo = new();

    private Entity AddBox(string name, Vector3 position, Entity? parent = null)
    {
        var e = new Entity(name) { Local = new Transform(position) };
        e.Add(new MeshComponent { ModelAssetId = Guid.NewGuid(), CastShadow = false });
        return _scene.Add(e, parent);
    }

    [Fact]
    public void Delete_removes_the_selection_with_children_in_one_undo_step()
    {
        var a = AddBox("A", Vector3.Zero);
        var child = AddBox("child", Vector3.One, a);
        var b = AddBox("B", Vector3.UnitX);
        _selection.Set(new[] { a.Id, child.Id, b.Id });
        Assert.Equal(2, EditOperations.Delete(_scene, _selection, _undo));
        Assert.Equal(0, _scene.Count);
        Assert.True(_selection.IsEmpty);
        Assert.Equal("Delete 2 objects", _undo.UndoDescription);
        _undo.Undo();
        Assert.Equal(3, _scene.Count);
        Assert.Same(a, child.Parent);
        Assert.False(_undo.CanUndo);
    }

    [Fact]
    public void Duplicate_copies_in_place_with_new_ids_and_selects_the_copies()
    {
        var a = AddBox("A", new Vector3(1, 2, 3));
        AddBox("child", new Vector3(0, 0, 1), a);
        _selection.Set(a.Id);
        var ids = EditOperations.Duplicate(_scene, _selection, _undo);
        var copy = Assert.Single(ids);
        Assert.NotEqual(a.Id, copy);
        Assert.Equal(new[] { copy }, _selection.Ids);
        var duplicate = _scene.Find(copy)!;
        Assert.Equal("A", duplicate.Name);
        Assert.Equal(a.Local.Position, duplicate.Local.Position);
        Assert.Single(duplicate.Children);
        Assert.NotSame(a.Get<MeshComponent>(), duplicate.Get<MeshComponent>());
        Assert.Equal(a.Get<MeshComponent>()!.ModelAssetId, duplicate.Get<MeshComponent>()!.ModelAssetId);
        Assert.Equal(4, _scene.Count);
        _undo.Undo();
        Assert.Equal(2, _scene.Count);
    }

    [Fact]
    public void Copy_paste_keeps_world_placement_plus_offset()
    {
        var parent = AddBox("parent", new Vector3(10, 0, 0));
        var a = AddBox("A", new Vector3(1, 0, 0), parent); // world (11, 0, 0)
        var ops = new EditOperations();
        _selection.Set(a.Id);
        Assert.Equal(1, ops.Copy(_scene, _selection));
        Assert.True(ops.CanPaste);
        var pasted = ops.Paste(_scene, _selection, _undo, new Vector3(0, 0.5f, 0));
        var copy = _scene.Find(Assert.Single(pasted))!;
        Assert.Null(copy.Parent); // pasted at the root
        Assert.True(Vector3.Distance(new Vector3(11, 0.5f, 0), copy.WorldPosition) < 1e-4f);
        // Pasting twice gives independent copies.
        var again = ops.Paste(_scene, _selection, _undo);
        Assert.NotEqual(pasted[0], again[0]);
        Assert.Equal(4, _scene.Count);
    }

    [Fact]
    public void Cut_then_paste_restores_the_objects()
    {
        var a = AddBox("A", new Vector3(2, 2, 0));
        var ops = new EditOperations();
        _selection.Set(a.Id);
        Assert.Equal(1, ops.Cut(_scene, _selection, _undo));
        Assert.Equal(0, _scene.Count);
        ops.Paste(_scene, _selection, _undo);
        Assert.Equal(1, _scene.Count);
        Assert.Equal(new Vector3(2, 2, 0), _scene.Roots[0].WorldPosition);
    }

    [Fact]
    public void Operations_on_an_empty_selection_do_nothing()
    {
        AddBox("A", Vector3.Zero);
        var ops = new EditOperations();
        Assert.Equal(0, EditOperations.Delete(_scene, _selection, _undo));
        Assert.Empty(EditOperations.Duplicate(_scene, _selection, _undo));
        Assert.Equal(0, ops.Copy(_scene, _selection));
        Assert.Empty(ops.Paste(_scene, _selection, _undo));
        Assert.False(_undo.CanUndo);
    }
}

public sealed class TransformMathTests
{
    private static void Near(Vector3 expected, Vector3 actual, float tolerance = 1e-4f) =>
        Assert.True(Vector3.Distance(expected, actual) <= tolerance, $"expected {expected}, got {actual}");

    [Fact]
    public void Delta_moves_other_objects_rigidly_with_the_pivot()
    {
        var pivotStart = Matrix4x4.CreateRotationZ(0.3f) * Matrix4x4.CreateTranslation(1, 2, 0);
        var pivotNow = Matrix4x4.CreateRotationZ(0.3f + MathF.PI / 2) * Matrix4x4.CreateTranslation(1, 2, 0);
        var delta = TransformMath.Delta(pivotStart, pivotNow);
        // A point fixed relative to the pivot keeps its pivot-relative position.
        var otherStart = Matrix4x4.CreateTranslation(3, 2, 0);
        var otherNow = otherStart * delta;
        var relStart = Vector3.Transform(otherStart.Translation, Matrix4x4.Invert(pivotStart, out var i1) ? i1 : default);
        var relNow = Vector3.Transform(otherNow.Translation, Matrix4x4.Invert(pivotNow, out var i2) ? i2 : default);
        Near(relStart, relNow);
        Assert.Equal(Matrix4x4.Identity, TransformMath.Delta(Matrix4x4.Identity, Matrix4x4.Identity));
    }

    [Fact]
    public void Apply_delta_handles_parents_and_keeps_scale()
    {
        var scene = new SceneModel();
        var parent = scene.Add(new Entity("parent") { Local = new Transform(new Vector3(5, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.5f), Vector3.One) });
        var child = scene.Add(new Entity("child") { Local = new Transform(new Vector3(1, 0, 0), Quaternion.Identity, new Vector3(2, 2, 2)) }, parent);
        var entities = new[] { child };
        var startWorld = new[] { child.WorldMatrix };
        var startLocal = new[] { child.Local };
        var delta = Matrix4x4.CreateTranslation(0, 0, 1.5f);
        var result = TransformMath.ApplyDelta(entities, startWorld, startLocal, delta);
        child.Local = result[0];
        Near(startWorld[0].Translation + new Vector3(0, 0, 1.5f), child.WorldPosition);
        Assert.Equal(new Vector3(2, 2, 2), child.Local.Scale);
    }

    [Fact]
    public void Gizmo_drag_frames_merge_into_one_undo_step()
    {
        var scene = new SceneModel();
        var undo = new UndoStack();
        var a = scene.Add(new Entity("A") { Local = new Transform(new Vector3(0, 0, 0)) });
        var b = scene.Add(new Entity("B") { Local = new Transform(new Vector3(2, 0, 0)) });
        var entities = new[] { a, b };
        var startWorld = entities.Select(e => e.WorldMatrix).ToArray();
        var startLocal = entities.Select(e => e.Local).ToArray();
        var pivot = a.WorldMatrix;
        // Twenty drag frames, each one command as the viewport issues them.
        for (int frame = 1; frame <= 20; frame++)
        {
            var now = pivot * Matrix4x4.CreateTranslation(0.1f * frame, 0, 0);
            var locals = TransformMath.ApplyDelta(entities, startWorld, startLocal, TransformMath.Delta(pivot, now));
            undo.Execute(new SetTransformCommand(entities, locals, "Move"));
        }

        undo.BreakMerge(); // the drag ended
        Near(new Vector3(2, 0, 0), a.WorldPosition);
        Near(new Vector3(4, 0, 0), b.WorldPosition);
        // A second drag is a separate step.
        undo.Execute(new SetTransformCommand(new[] { a }, new[] { new Transform(new Vector3(9, 9, 9)) }, "Move"));
        undo.Undo();
        Near(new Vector3(2, 0, 0), a.WorldPosition);
        undo.Undo();
        Near(Vector3.Zero, a.WorldPosition);
        Near(new Vector3(2, 0, 0), b.WorldPosition);
        Assert.False(undo.CanUndo);
    }

    [Fact]
    public void Snapping()
    {
        Assert.Equal(new Vector3(0.5f, -1f, 2.5f), TransformMath.SnapPosition(new Vector3(0.6f, -0.9f, 2.4f), 0.5f));
        Assert.Equal(new Vector3(0.6f, 1f, 2f), TransformMath.SnapPosition(new Vector3(0.6f, 1f, 2f), 0f));
        Assert.Equal(new Vector3(0.6f, 1f, 2f), TransformMath.SnapPosition(new Vector3(0.6f, 1f, 2f), float.NaN));
        Assert.Equal(45f, TransformMath.SnapAngle(40f, 15f));
        Assert.Equal(-30f, TransformMath.SnapAngle(-37f, 15f));
        Assert.Equal(37f, TransformMath.SnapAngle(37f, 0f));
    }

    [Theory]
    [InlineData(0f, 0f, 0f)]
    [InlineData(30f, 0f, 0f)]
    [InlineData(0f, -45f, 0f)]
    [InlineData(0f, 0f, 120f)]
    [InlineData(10f, 20f, 30f)]
    [InlineData(-80f, 60f, -170f)]
    public void Euler_degrees_round_trip(float x, float y, float z)
    {
        var q = TransformMath.FromEulerDegrees(new Vector3(x, y, z));
        var back = TransformMath.ToEulerDegrees(q);
        Near(new Vector3(x, y, z), back, 0.01f);
    }

    [Fact]
    public void Euler_x_then_z_order()
    {
        // X 90° then Z 90°: the local Y axis first goes to +Z, then stays at +Z; the local X axis goes to +Y.
        var q = TransformMath.FromEulerDegrees(new Vector3(90f, 0f, 90f));
        Near(Vector3.UnitZ, Vector3.Transform(Vector3.UnitY, q));
        Near(Vector3.UnitY, Vector3.Transform(Vector3.UnitX, q));
    }

    [Fact]
    public void Gimbal_lock_still_round_trips_the_rotation()
    {
        var q = TransformMath.FromEulerDegrees(new Vector3(25f, 90f, 40f));
        var back = TransformMath.FromEulerDegrees(TransformMath.ToEulerDegrees(q));
        Assert.True(MathF.Abs(Quaternion.Dot(q, back)) > 0.9999f);
    }
}
