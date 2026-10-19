using System.Globalization;
using System.Numerics;
using Candela.App.Editing;
using Candela.App.Shell;
using Candela.App.Ui;
using Candela.Core.Editing;
using Candela.Core.Math;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;
using Hexa.NET.ImGui;
using Hexa.NET.ImGuizmo;

namespace Candela.App.Viewport;

/// <summary>
/// The Wall and Floor tools in the viewport (requirement: walls and floors created as resizable rectangles): with the
/// Floor tool a left-button drag draws a rectangle on the surface under the press (a horizontal surface, else the
/// ground), with the Wall tool a drag draws the line a 3 m wall stands on. The handles of a selected wall, floor or
/// ceiling (any tool) resize it while the opposite side stays in place. Each creation or resize is one undo step;
/// the engine draws the result with the shape's built-in material.
/// </summary>
internal sealed unsafe partial class ViewportPanel
{
    private ShapeEdit _shape;
    private readonly Vector2[] _shapePoints = new Vector2[8];
    private string _shapeLabel = "";
    private Vector2 _shapeLabelSize;

    /// <summary>True while a wall/floor is being drawn or resized.</summary>
    internal bool IsEditingShape => _shape.Mode != ShapeMode.None;

    /// <summary>
    /// Starts drawing (Wall / Floor tool) or resizing (a handle under the mouse) at a left-button press; false when the
    /// press is not for a shape (it then selects as usual).
    /// </summary>
    private bool BeginShapeEdit(Vector2 mouse)
    {
        var session = _ctx.Session;
        if (session.Engine is null)
            return false;
        if (HandleUnder(mouse) is { } grab)
        {
            var wall = grab.Entity.Get<WallComponent>()!;
            _shape = new ShapeEdit
            {
                Mode = ShapeMode.Resize,
                Entity = grab.Entity,
                Handle = grab.Handle,
                BeforeLocal = grab.Entity.Local,
                BeforeWidth = wall.Width,
                BeforeHeight = wall.Height,
                Moved = false,
            };
            return true;
        }

        var tool = _ctx.Tools.Tool;
        if (tool is not (EditorTool.Wall or EditorTool.Floor))
            return false;
        // The plane to draw on: a horizontal surface under the press (a stage, a floor), else the ground.
        var hit = SurfaceAt(mouse, _ctx.Gpu.Renderer);
        float height = hit.Kind == SurfaceKind.Geometry && hit.Normal.Z > 0.7f ? hit.Position.Z : 0f;
        var ray = Camera.RayThrough(mouse - _imageMin, _imageSize);
        if (WallShapes.OnPlaneZ(ray, height) is not { } start)
            return true; // looking above the horizon: nothing to draw on, but the press is not a selection either
        start = WallShapes.SnapPoint(start, SnapStep());
        _shape = new ShapeEdit
        {
            Mode = tool == EditorTool.Wall ? ShapeMode.DrawWall : ShapeMode.DrawFloor,
            PlaneZ = height,
            Start = start,
            End = start,
        };
        return true;
    }

    /// <summary>Follows the mouse while a shape is drawn or resized; commits on release.</summary>
    private void UpdateShapeEdit(Vector2 mouse)
    {
        if (_shape.Mode == ShapeMode.None)
            return;
        if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            CommitShapeEdit();
            return;
        }

        var ray = Camera.RayThrough(mouse - _imageMin, _imageSize);
        switch (_shape.Mode)
        {
            case ShapeMode.DrawWall:
            case ShapeMode.DrawFloor:
                if (WallShapes.OnPlaneZ(ray, _shape.PlaneZ) is { } p)
                    _shape.End = WallShapes.SnapPoint(p, SnapStep());
                break;
            case ShapeMode.Resize:
                ResizeTo(ray);
                break;
        }
    }

    private void ResizeTo(Ray worldRay)
    {
        var entity = _shape.Entity!;
        if (entity.Scene is null || entity.Get<WallComponent>() is not { } wall)
        {
            _shape = default;
            return;
        }

        // Work in the shape's own frame as it was when the drag began (the origin moves while resizing).
        var parentWorld = entity.Parent?.WorldMatrix ?? Matrix4x4.Identity;
        var world = _shape.BeforeLocal.ToMatrix() * parentWorld;
        if (!Matrix4x4.Invert(world, out var inverse))
            return;
        var origin = Vector3.Transform(worldRay.Origin, inverse);
        var direction = Vector3.TransformNormal(worldRay.Direction, inverse);
        var localRay = new Ray(origin, direction);
        var before = new WallComponent { Kind = wall.Kind, Width = _shape.BeforeWidth, Height = _shape.BeforeHeight };
        var handlePosition = WallShapes.HandlePosition(before, _shape.Handle);
        Vector3? target = _shape.Handle.IsCorner
            ? WallShapes.OnPlaneZ(localRay, handlePosition.Z)
            : WallShapes.ClosestOnAxis(handlePosition, WallShapes.HandleAxis(_shape.Handle), localRay);
        if (target is not { } t)
            return;
        var (width, height, shift) = WallShapes.Resize(before, _shape.Handle, t, SnapStep());
        wall.Width = width;
        wall.Height = height;
        wall.NotifyChanged();
        entity.Local = WallShapes.ShiftOrigin(_shape.BeforeLocal, shift);
        _shape.Moved = true;
    }

    private void CommitShapeEdit()
    {
        var shape = _shape;
        _shape = default;
        var session = _ctx.Session;
        switch (shape.Mode)
        {
            case ShapeMode.Resize when shape.Moved && shape.Entity is { Scene: not null } entity && entity.Get<WallComponent>() is { } wall:
                {
                    // The drag changed the shape live; as one undo step: back to the start, then the final size.
                    float width = wall.Width, height = wall.Height;
                    var local = entity.Local;
                    wall.Width = shape.BeforeWidth;
                    wall.Height = shape.BeforeHeight;
                    wall.NotifyChanged();
                    entity.Local = shape.BeforeLocal;
                    using (session.Undo.Group($"Resize {entity.Name}"))
                    {
                        session.Undo.Execute(ModifyComponentCommand.Create(wall, w =>
                        {
                            w.Width = width;
                            w.Height = height;
                        }, "Resize"));
                        session.Undo.Execute(new SetTransformCommand(new[] { entity }, new[] { local }, "Resize"));
                    }

                    session.Undo.BreakMerge();
                    break;
                }

            case ShapeMode.DrawFloor:
                if (WallShapes.FloorFromCorners(shape.Start, shape.End) is { } floor)
                    AddShape(WallKind.Floor, floor.Placement, floor.Width, floor.Length, WallShapes.DefaultFloorThickness);
                break;
            case ShapeMode.DrawWall:
                if (WallShapes.WallFromLine(shape.Start, shape.End, Camera.Eye) is { } line)
                    AddShape(WallKind.Wall, line.Placement, line.Width, WallShapes.DefaultWallHeight, WallShapes.DefaultWallThickness);
                break;
        }
    }

    private void AddShape(WallKind kind, Transform placement, float width, float height, float thickness)
    {
        var session = _ctx.Session;
        int number = 1;
        foreach (var (_, w) in session.Scene.Query<WallComponent>())
            if (w.Kind == kind)
                number++;
        string name = $"{Loc.Get(kind == WallKind.Floor ? Str.FloorName : Str.WallName)} {number.ToString(CultureInfo.InvariantCulture)}";
        var entity = new Entity(name) { Local = placement };
        entity.Add(new WallComponent
        {
            Kind = kind,
            Width = width,
            Height = height,
            Thickness = thickness,
            MaterialId = WallShapes.DefaultMaterial(kind),
        });
        session.Undo.Execute(new AddEntityCommand(session.Scene, entity));
        session.Selection.Set(entity.Id);
    }

    private void CancelShapeEdit()
    {
        if (_shape.Mode == ShapeMode.Resize && _shape.Entity is { Scene: not null } entity && entity.Get<WallComponent>() is { } wall && _shape.Moved)
        {
            wall.Width = _shape.BeforeWidth;
            wall.Height = _shape.BeforeHeight;
            wall.NotifyChanged();
            entity.Local = _shape.BeforeLocal;
        }

        _shape = default;
    }

    private float SnapStep()
    {
        bool snap = _ctx.Tools.Snap ^ ImGui.GetIO().KeyCtrl;
        float grid = _ctx.Session.Scene.Settings.GridSpacing > 0 ? _ctx.Session.Scene.Settings.GridSpacing : 1f;
        return snap ? grid * 0.5f : 0f;
    }

    /// <summary>The resize handle of the selected wall/floor under <paramref name="mouse"/>, or null.</summary>
    private (Entity Entity, ShapeHandle Handle)? HandleUnder(Vector2 mouse)
    {
        if (SelectedShape() is not { } entity)
            return null;
        var wall = entity.Get<WallComponent>()!;
        var world = entity.WorldMatrix;
        float radius = HandleRadius() * 1.6f;
        (Entity, ShapeHandle)? best = null;
        float bestDistance = radius * radius;
        foreach (var handle in WallShapes.HandlesOf(wall.Kind))
        {
            if (!Camera.WorldToScreen(Vector3.Transform(WallShapes.HandlePosition(wall, handle), world), _imageSize, out var s))
                continue;
            float d = Vector2.DistanceSquared(s + _imageMin, mouse);
            if (d <= bestDistance)
            {
                bestDistance = d;
                best = (entity, handle);
            }
        }

        return best;
    }

    /// <summary>The selected wall, floor or ceiling that may be resized (single editable selection), or null.</summary>
    private Entity? SelectedShape()
    {
        var session = _ctx.Session;
        if (session.Selection.Count != 1 || session.Selection.Primary is not { } id || session.Scene.Find(id) is not { } entity)
            return null;
        return entity.Get<WallComponent>() is not null && SelectionController.IsEditable(session.Scene, entity) ? entity : null;
    }

    private float HandleRadius()
    {
        var style = ImGui.GetStyle();
        return 5f * style.FontScaleMain * style.FontScaleDpi;
    }

    /// <summary>Draws the shape being drawn (outline and size) and the handles of a selected wall or floor.</summary>
    private void DrawShapeEdit(ImDrawListPtr drawList, Vector2 mouse, bool hovered)
    {
        var style = ImGui.GetStyle();
        float scale = style.FontScaleMain * style.FontScaleDpi;
        var accent = Theme.Accent;
        if (_shape.Mode is ShapeMode.DrawFloor or ShapeMode.DrawWall)
        {
            DrawDrawingPreview(drawList, scale, mouse);
            return;
        }

        if (_ctx.Tools.Tool is EditorTool.Wall or EditorTool.Floor && _shape.Mode == ShapeMode.None)
            DrawToolHint(drawList, scale, Loc.Get(_ctx.Tools.Tool == EditorTool.Wall ? Str.ToolWallHint : Str.ToolFloorHint));

        if (SelectedShape() is not { } entity || _gizmoVisible && ImGuizmo.IsUsing())
            return;
        var wall = entity.Get<WallComponent>()!;
        var world = entity.WorldMatrix;
        var hot = _shape.Mode == ShapeMode.Resize ? _shape.Handle : hovered ? HandleUnder(mouse)?.Handle : null;
        float r = HandleRadius();
        foreach (var handle in WallShapes.HandlesOf(wall.Kind))
        {
            if (!Camera.WorldToScreen(Vector3.Transform(WallShapes.HandlePosition(wall, handle), world), _imageSize, out var s))
                continue;
            var p = s + _imageMin;
            bool isHot = hot == handle;
            float size = isHot ? r * 1.35f : r;
            drawList.AddRectFilled(p - new Vector2(size), p + new Vector2(size), Theme.U32(isHot ? accent : Theme.Rgb(0xF4F4F5)), 2f * scale);
            drawList.AddRect(p - new Vector2(size), p + new Vector2(size), Theme.U32(Theme.Rgb(0x16171A)), 2f * scale, ImDrawFlags.None, 1.5f * scale);
        }

        if (_shape.Mode == ShapeMode.Resize)
            DrawSizeLabel(drawList, mouse, scale, wall.Width, wall.Height);
    }

    private void DrawDrawingPreview(ImDrawListPtr drawList, float scale, Vector2 mouse)
    {
        var a = _shape.Start;
        var b = _shape.End;
        int count;
        float width, height;
        if (_shape.Mode == ShapeMode.DrawFloor)
        {
            _corners[0] = a;
            _corners[1] = new Vector3(b.X, a.Y, a.Z);
            _corners[2] = new Vector3(b.X, b.Y, a.Z);
            _corners[3] = new Vector3(a.X, b.Y, a.Z);
            count = 4;
            width = MathF.Abs(b.X - a.X);
            height = MathF.Abs(b.Y - a.Y);
        }
        else
        {
            var up = new Vector3(0f, 0f, WallShapes.DefaultWallHeight);
            _corners[0] = a;
            _corners[1] = b with { Z = a.Z };
            _corners[2] = _corners[1] + up;
            _corners[3] = a + up;
            count = 4;
            width = new Vector2(b.X - a.X, b.Y - a.Y).Length();
            height = WallShapes.DefaultWallHeight;
        }

        for (int i = 0; i < count; i++)
        {
            if (!Camera.WorldToScreen(_corners[i], _imageSize, out var s))
                return;
            _shapePoints[i] = s + _imageMin;
        }

        var fill = Theme.U32(Theme.WithAlpha(Theme.Accent, 0.18f));
        drawList.AddQuadFilled(_shapePoints[0], _shapePoints[1], _shapePoints[2], _shapePoints[3], fill);
        var edge = Theme.U32(Theme.WithAlpha(Theme.Accent, 0.95f));
        for (int i = 0; i < count; i++)
            drawList.AddLine(_shapePoints[i], _shapePoints[(i + 1) % count], edge, 2f * scale);
        DrawSizeLabel(drawList, mouse, scale, width, height);
    }

    /// <summary>The tool's one-line help, centred under the toolbar (wrapped in narrow views).</summary>
    private void DrawToolHint(ImDrawListPtr drawList, float scale, string hint)
    {
        float wrap = MathF.Max(120f * scale, _imageSize.X - 40f * scale);
        var size = ImGui.CalcTextSize(hint, false, wrap);
        var pos = new Vector2(_imageMin.X + (_imageSize.X - size.X) * 0.5f, MathF.Max(_toolbarBottom, _imageMin.Y) + 10f * scale);
        drawList.AddRectFilled(pos - new Vector2(10f, 5f) * scale, pos + size + new Vector2(10f, 5f) * scale,
            Theme.U32(Theme.WithAlpha(Theme.OverlayBackground, 0.85f)), 5f * scale);
        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), pos, Theme.U32(Theme.WithAlpha(Theme.Text, 0.9f)), hint, wrap);
    }

    private readonly Vector3[] _corners = new Vector3[4];
    private float _labelWidth = float.NaN, _labelHeight = float.NaN;

    private void DrawSizeLabel(ImDrawListPtr drawList, Vector2 mouse, float scale, float width, float height)
    {
        // Rebuilt only when the size changes by a centimetre (no string per frame while the mouse rests).
        if (MathF.Abs(width - _labelWidth) >= 0.005f || MathF.Abs(height - _labelHeight) >= 0.005f || _shapeLabel.Length == 0)
        {
            _labelWidth = width;
            _labelHeight = height;
            _shapeLabel = Loc.Format(Str.WallSize, width.ToString("0.00", CultureInfo.InvariantCulture), height.ToString("0.00", CultureInfo.InvariantCulture));
            _shapeLabelSize = ImGui.CalcTextSize(_shapeLabel);
        }

        var pos = mouse + new Vector2(14f, -_shapeLabelSize.Y - 10f) * scale;
        drawList.AddRectFilled(pos - new Vector2(6f, 3f) * scale, pos + _shapeLabelSize + new Vector2(6f, 3f) * scale,
            Theme.U32(Theme.WithAlpha(Theme.OverlayBackground, 0.9f)), 4f * scale);
        drawList.AddText(pos, Theme.U32(Theme.Text), _shapeLabel);
    }

    private enum ShapeMode
    {
        None,
        DrawFloor,
        DrawWall,
        Resize,
    }

    private struct ShapeEdit
    {
        public ShapeMode Mode;
        public float PlaneZ;
        public Vector3 Start;
        public Vector3 End;
        public Entity? Entity;
        public ShapeHandle Handle;
        public Transform BeforeLocal;
        public float BeforeWidth;
        public float BeforeHeight;
        public bool Moved;
    }
}
