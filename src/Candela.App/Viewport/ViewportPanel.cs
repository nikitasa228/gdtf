using System.Globalization;
using System.Numerics;
using Candela.App.Docking;
using Candela.App.Editing;
using Candela.App.Shell;
using Candela.App.Ui;
using Candela.Core.Editing;
using Candela.Core.Math;
using Candela.Core.SceneGraph;
using Candela.Rendering;
using Candela.Rendering.Vulkan;
using Hexa.NET.ImGui;
using Hexa.NET.ImGuizmo;

namespace Candela.App.Viewport;

/// <summary>What the workspace provides to its viewports.</summary>
internal interface IViewportHost
{
    /// <summary>Makes <paramref name="viewport"/> the target of View-menu commands and shortcuts.</summary>
    void SetActiveViewport(ViewportPanel viewport);

    /// <summary>True for the active viewport.</summary>
    bool IsActive(ViewportPanel viewport);

    /// <summary>Stops shortcuts while flying with W/A/S/D.</summary>
    void SuspendShortcuts(bool suspended);
}

/// <summary>
/// A 3D viewport panel (requirement): the world rendered into the panel's own render target (sized to the panel in
/// framebuffer pixels) and shown as an ImGui image; camera navigation (orbit, pan, zoom, fly with W/A/S/D + mouse,
/// frame selection, focus on double-click) following the navigation style setting; perspective / orthographic;
/// Wireframe / Colorless / Full shading; overlay selector (None / Lux / Noise / Cables); grid; FPS readout; the corner
/// view cube that snaps to the six orthographic views; click / Shift+click / box selection through the renderer's pick
/// buffer; and ImGuizmo move / rotate gizmos with snapping, every drag one undo step.
/// </summary>
internal sealed unsafe class ViewportPanel : Panel
{
    private const float ToolbarAlpha = 0.82f;
    private readonly WorkspaceContext _ctx;
    private readonly IViewportHost _host;
    private readonly RenderCamera _renderCamera = new();
    private readonly ViewCubeFace[] _cubeFaces = new ViewCubeFace[ViewCube.FaceCount];
    private readonly ViewCubeArrow[] _cubeArrows = new ViewCubeArrow[ViewCube.ArrowCount];
    private readonly List<PickRequest> _picks = new();
    private readonly List<Entity> _dragEntities = new();
    private SelectionController.ProjectToScreen? _boxProjector;
    private Matrix4x4[] _dragStartWorld = Array.Empty<Matrix4x4>();
    private Transform[] _dragStartLocal = Array.Empty<Transform>();
    private Matrix4x4 _pivotStart;
    private Matrix4x4 _gizmoMatrix;
    private bool _gizmoDragging;
    private bool _gizmoVisible;
    private IRenderTarget? _target;
    private ImTextureID _texture;
    private int _registeredVersion = -1;
    private Vector2 _imageMin;
    private Vector2 _imageSize;
    private float _framebufferScale = 1f;
    private bool _visibleThisFrame;
    private NavigationOperation _navigation;
    private ImGuiMouseButton _navigationButton;
    private bool _selecting;
    private bool _selectShift;
    private Vector2 _pressPosition;
    private string _statsText = "";
    private double _statsUpdatedAt = -1;
    private string _title = "";
    private string _titleLanguage = "";
    private ViewDirection? _hoveredFace;
    private readonly ToolbarLabels _labels = new();
    private float _toolbarBottom;

    /// <summary>Creates viewport number <paramref name="number"/> (1 … 16).</summary>
    public ViewportPanel(int number, WorkspaceContext context, IViewportHost host)
        : base("Viewport" + number.ToString(CultureInfo.InvariantCulture), Icons.Video)
    {
        Number = number;
        _ctx = context ?? throw new ArgumentNullException(nameof(context));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        View.ShowGrid = true;
    }

    /// <summary>Viewport number.</summary>
    public int Number { get; }

    /// <summary>The camera.</summary>
    public ViewportCamera Camera { get; } = new();

    /// <summary>Shading, overlay, grid and selection-outline options of this view.</summary>
    public ViewOptions View { get; } = new();

    /// <summary>FPS / frame time readout.</summary>
    public bool ShowStats { get; set; } = true;

    /// <summary>The corner view cube.</summary>
    public bool ShowViewCube { get; set; } = true;

    /// <summary>The render target (null until the panel was first shown).</summary>
    internal IRenderTarget? Target => _target;

    /// <summary>Screen rectangle of the image (last frame), for scripts and tests.</summary>
    internal (Vector2 Min, Vector2 Size) ImageRect => (_imageMin, _imageSize);

    /// <inheritdoc/>
    public override string Title
    {
        get
        {
            if (_titleLanguage != Loc.Language || _title.Length == 0)
            {
                _titleLanguage = Loc.Language;
                _title = Number == 1 ? Loc.Get(Str.PanelViewport) : $"{Loc.Get(Str.PanelViewport)} {Number}";
            }

            return _title;
        }
    }

    /// <inheritdoc/>
    public override bool EdgeToEdge => true;

    /// <inheritdoc/>
    public override bool CanClose => Number != 1;

    /// <inheritdoc/>
    public override Vector2 DefaultSize => new(720, 480);

    /// <summary>Layout state of this viewport.</summary>
    public ViewportLayout ToLayout() => new()
    {
        Id = Id,
        View = Camera.SnappedView?.ToString() ?? "Perspective",
        Projection = Camera.Projection.ToString(),
        Shading = View.Shading.ToString(),
        Overlay = View.Overlay.ToString(),
        Grid = View.ShowGrid,
        Stats = ShowStats,
    };

    /// <summary>Applies a layout state (unknown values keep the current setting).</summary>
    public void ApplyLayout(ViewportLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        Configure(layout.View, layout.Projection, layout.Shading, layout.Overlay, layout.Grid);
        ShowStats = layout.Stats;
    }

    /// <summary>Sets view / projection / shading / overlay by name (empty or unknown strings keep the current value).</summary>
    public void Configure(string view, string projection, string shading, string overlay, bool grid)
    {
        if (Enum.TryParse(view, true, out ViewDirection dir) && Enum.IsDefined(dir))
            Camera.SnapTo(dir);
        else if (string.Equals(view, "Perspective", StringComparison.OrdinalIgnoreCase) && Camera.Projection == ProjectionKind.Orthographic)
            Camera.ToggleProjection();
        if (Enum.TryParse(projection, true, out ProjectionKind proj) && Enum.IsDefined(proj) && proj != Camera.Projection)
            Camera.ToggleProjection();
        if (Enum.TryParse(shading, true, out ViewShading s) && Enum.IsDefined(s))
            View.Shading = s;
        if (Enum.TryParse(overlay, true, out OverlayMode o) && Enum.IsDefined(o))
            View.Overlay = o;
        View.ShowGrid = grid;
    }

    /// <summary>Frames the selection (or the whole scene when nothing is selected).</summary>
    public void FrameSelection()
    {
        var content = _ctx.Session.Content;
        if (content is null)
            return;
        var bounds = Aabb.Empty;
        foreach (var id in _ctx.Session.Selection.Ids)
            if (content.TryGetBounds(id, out var b))
                bounds.Include(b);
        if (bounds.IsEmpty)
            bounds = content.SceneBounds;
        Camera.Frame(bounds, _imageSize.Y > 0 ? _imageSize.X / _imageSize.Y : 16f / 9f);
    }

    /// <summary>Performs the pick queries recorded last frame (blocking reads of the last rendered frame; outside any GPU frame).</summary>
    public void ProcessPicks(IRenderer renderer)
    {
        if (_picks.Count == 0)
            return;
        var session = _ctx.Session;
        foreach (var request in _picks)
        {
            PickResult? hit = _target is null ? null : renderer.Pick(_target, request.X, request.Y);
            Guid? entity = hit is { } h ? session.Content?.EntityForPick(h.PickId) : null;
            switch (request.Kind)
            {
                case PickKind.Select:
                    SelectionController.Click(session.Scene, session.Selection, entity, request.Shift);
                    break;
                case PickKind.Focus:
                    if (hit is { } focus)
                        Camera.FocusOn(focus.WorldPosition);
                    break;
            }
        }

        _picks.Clear();
    }

    /// <summary>Renders the view into its target (inside the GPU frame, after the UI was built).</summary>
    public void Render(IRenderer renderer, float deltaTime)
    {
        if (!_visibleThisFrame || _target is null || !IsOpen)
            return;
        _visibleThisFrame = false;
        Camera.UpdateRenderCamera(_renderCamera);
        View.GridSpacing = _ctx.Session.Scene.Settings.GridSpacing > 0 ? _ctx.Session.Scene.Settings.GridSpacing : 1f;
        renderer.Render(_target, _renderCamera, View, _ctx.Tools.Preview, deltaTime);
    }

    /// <summary>Queues a selection click at viewport pixel coordinates (scripts and tests).</summary>
    internal void QueuePick(Vector2 pixel, bool shift, bool focus = false) =>
        _picks.Add(new PickRequest(focus ? PickKind.Focus : PickKind.Select, (int)(pixel.X * _framebufferScale), (int)(pixel.Y * _framebufferScale), shift));

    /// <inheritdoc/>
    public override void OnClosed() => ReleaseTarget();

    /// <summary>Releases the render target and its ImGui texture (panel closed, workspace disposed).</summary>
    public void ReleaseTarget()
    {
        if (_texture != new ImTextureID(0UL))
            _ctx.ImGui.RemoveTextureDeferred(_texture);
        _texture = new ImTextureID(0UL);
        _registeredVersion = -1;
        if (_target is not null)
            _ctx.Gpu.Context.DeferDispose(_target);
        _target = null;
    }

    /// <inheritdoc/>
    public override void Draw()
    {
        var avail = ImGui.GetContentRegionAvail();
        if (avail.X < 8 || avail.Y < 8)
            return;
        _imageMin = ImGui.GetCursorScreenPos();
        _imageSize = avail;
        _framebufferScale = ImGui.GetWindowViewport().FramebufferScale.X is > 0 and var fb ? fb : 1f;
        EnsureTarget(avail);
        _visibleThisFrame = true;

        var drawList = ImGui.GetWindowDrawList();
        var max = _imageMin + avail;
        if (_texture != new ImTextureID(0UL))
            drawList.AddImage(new ImTextureRef(null, _texture), _imageMin, max);
        else
            drawList.AddRectFilled(_imageMin, max, Theme.U32(Theme.WorkspaceBackground));
        if (_host.IsActive(this))
            drawList.AddRect(_imageMin, max, Theme.U32(Theme.WithAlpha(Theme.Accent, 0.35f)));

        // The gizmo runs before the input surface: ImGuizmo only starts a drag when no ImGui item is hovered or active,
        // so the surface is left out while the mouse is over (or dragging) a gizmo handle.
        DrawGizmo(drawList);
        bool gizmoHot = _gizmoVisible && (ImGuizmo.IsOver() || ImGuizmo.IsUsing());
        ImGui.SetCursorScreenPos(_imageMin);
        bool hovered;
        if (gizmoHot)
        {
            ImGui.Dummy(avail);
            hovered = ImGui.IsWindowHovered();
        }
        else
        {
            ImGui.SetNextItemAllowOverlap();
            ImGui.InvisibleButton("##viewport-input", avail,
                ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight | ImGuiButtonFlags.MouseButtonMiddle);
            hovered = ImGui.IsItemHovered();
        }

        bool overOverlay = DrawToolbar();
        overOverlay |= DrawViewCube(drawList, hovered);
        DrawStats(drawList);
        HandleInput(hovered, overOverlay);
        DrawBoxSelection(drawList);
    }

    private void EnsureTarget(Vector2 size)
    {
        int w = Math.Max(1, (int)MathF.Round(size.X * _framebufferScale));
        int h = Math.Max(1, (int)MathF.Round(size.Y * _framebufferScale));
        if (_target is null)
            _target = _ctx.Gpu.Renderer.CreateTarget(w, h);
        else if (_target.Width != w || _target.Height != h)
            _target.Resize(w, h);

        if (_target is VulkanRenderTarget vt && vt.DisplayVersion != _registeredVersion)
        {
            if (_texture != new ImTextureID(0UL))
                _ctx.ImGui.RemoveTextureDeferred(_texture);
            _texture = _ctx.ImGui.AddTexture(vt.DisplaySampler, vt.DisplayImageView, vt.DisplayImageLayout);
            _registeredVersion = vt.DisplayVersion;
        }
    }

    private void HandleInput(bool hovered, bool overOverlay)
    {
        var io = ImGui.GetIO();
        var mouse = io.MousePos;
        var settings = _ctx.Settings.Settings;
        bool gizmoBusy = _gizmoVisible && (ImGuizmo.IsUsing() || ImGuizmo.IsOver());
        if (hovered && (ImGui.IsMouseClicked(ImGuiMouseButton.Left) || ImGui.IsMouseClicked(ImGuiMouseButton.Right) ||
                        ImGui.IsMouseClicked(ImGuiMouseButton.Middle) || io.MouseWheel != 0))
            _host.SetActiveViewport(this);

        // Wheel zoom towards the point under the cursor (on the plane through the target).
        if (hovered && !overOverlay && io.MouseWheel != 0 && _navigation == NavigationOperation.None)
        {
            var ray = Camera.RayThrough(mouse - _imageMin, _imageSize);
            var plane = Camera.Target;
            float denom = Vector3.Dot(ray.Direction, Camera.Forward);
            Vector3? towards = MathF.Abs(denom) > 1e-5f ? ray.At(Vector3.Dot(plane - ray.Origin, Camera.Forward) / denom) : null;
            Camera.Zoom(io.MouseWheel, settings.ZoomSpeed, towards);
        }

        var mods = NavigationModifiers.None;
        if (io.KeyShift)
            mods |= NavigationModifiers.Shift;
        if (io.KeyCtrl)
            mods |= NavigationModifiers.Ctrl;
        if (io.KeyAlt)
            mods |= NavigationModifiers.Alt;

        // Start a navigation or a selection.
        if (hovered && !overOverlay && _navigation == NavigationOperation.None && !_selecting)
        {
            foreach (var (button, navButton) in Buttons)
            {
                if (!ImGui.IsMouseClicked(button))
                    continue;
                var op = NavigationStyles.Resolve(settings.NavigationStyle, navButton, mods);
                if (op != NavigationOperation.None)
                {
                    _navigation = op;
                    _navigationButton = button;
                    if (op == NavigationOperation.Fly)
                        _host.SuspendShortcuts(true);
                }
                else if (button == ImGuiMouseButton.Left && !gizmoBusy)
                {
                    _selecting = true;
                    _selectShift = io.KeyShift;
                    _pressPosition = mouse;
                }

                break;
            }

            if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) && !gizmoBusy)
            {
                _picks.Add(new PickRequest(PickKind.Focus, ToPixelX(mouse), ToPixelY(mouse), false));
                _selecting = false;
            }
        }

        if (_navigation != NavigationOperation.None)
        {
            if (!ImGui.IsMouseDown(_navigationButton))
            {
                if (_navigation == NavigationOperation.Fly)
                    _host.SuspendShortcuts(false);
                _navigation = NavigationOperation.None;
            }
            else
            {
                Navigate(io, settings.ZoomSpeed);
            }
        }

        if (_selecting && !ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            _selecting = false;
            if (SelectionController.IsBoxDrag(_pressPosition, mouse))
                BoxSelect(_pressPosition, mouse, _selectShift);
            else
                _picks.Add(new PickRequest(PickKind.Select, ToPixelX(_pressPosition), ToPixelY(_pressPosition), _selectShift));
        }
    }

    private void Navigate(ImGuiIOPtr io, float zoomSpeed)
    {
        var d = io.MouseDelta;
        switch (_navigation)
        {
            case NavigationOperation.Orbit:
                Camera.Orbit(d.X, d.Y);
                break;
            case NavigationOperation.Pan:
                Camera.Pan(d.X, d.Y, _imageSize.Y);
                break;
            case NavigationOperation.Zoom:
                Camera.ZoomDrag(d.Y, zoomSpeed);
                break;
            case NavigationOperation.Fly:
                Camera.Look(d.X, d.Y);
                var move = Vector3.Zero;
                if (ImGui.IsKeyDown(ImGuiKey.W))
                    move.Y += 1;
                if (ImGui.IsKeyDown(ImGuiKey.S))
                    move.Y -= 1;
                if (ImGui.IsKeyDown(ImGuiKey.D))
                    move.X += 1;
                if (ImGui.IsKeyDown(ImGuiKey.A))
                    move.X -= 1;
                if (ImGui.IsKeyDown(ImGuiKey.E))
                    move.Z += 1;
                if (ImGui.IsKeyDown(ImGuiKey.Q))
                    move.Z -= 1;
                Camera.Fly(move, io.DeltaTime, io.KeyShift ? 3f : 1f);
                break;
        }
    }

    private void BoxSelect(Vector2 a, Vector2 b, bool shift)
    {
        var (min, max) = SelectionController.Rect(a, b);
        var session = _ctx.Session;
        var content = session.Content;
        var inside = new List<Guid>();
        _boxProjector ??= ProjectToScreen;
        foreach (var e in session.Scene.All())
        {
            // Objects with geometry must lie entirely inside the box; objects without (lights, groups) by their origin.
            var bounds = content is not null && content.TryGetBounds(e.Id, out var b0) ? b0 : new Aabb(e.WorldPosition, e.WorldPosition);
            if (SelectionController.Encloses(bounds, min, max, _boxProjector))
                inside.Add(e.Id);
        }

        SelectionController.Box(session.Scene, session.Selection, inside, shift);
    }

    private bool ProjectToScreen(Vector3 world, out Vector2 screen)
    {
        bool ok = Camera.WorldToScreen(world, _imageSize, out var p);
        screen = p + _imageMin;
        return ok;
    }

    private void DrawBoxSelection(ImDrawListPtr drawList)
    {
        if (!_selecting)
            return;
        var mouse = ImGui.GetIO().MousePos;
        if (!SelectionController.IsBoxDrag(_pressPosition, mouse))
            return;
        var (min, max) = SelectionController.Rect(_pressPosition, mouse);
        drawList.AddRectFilled(min, max, Theme.U32(Theme.WithAlpha(Theme.Accent, 0.10f)));
        drawList.AddRect(min, max, Theme.U32(Theme.WithAlpha(Theme.Accent, 0.85f)));
    }

    private void DrawGizmo(ImDrawListPtr drawList)
    {
        var session = _ctx.Session;
        var tools = _ctx.Tools;
        if (tools.Tool == EditorTool.Select || session.Selection.Primary is not { } primaryId || session.Scene.Find(primaryId) is not { } primary ||
            primary.Locked)
        {
            _gizmoVisible = false;
            EndGizmoDrag();
            return;
        }

        _gizmoVisible = true;

        ImGuizmo.SetDrawlist(drawList);
        ImGuizmo.SetRect(_imageMin.X, _imageMin.Y, _imageSize.X, _imageSize.Y);
        ImGuizmo.SetOrthographic(Camera.Projection == ProjectionKind.Orthographic);
        ImGuizmo.SetID(Number);
        var view = Camera.ViewMatrix;
        var projection = Camera.ProjectionMatrix(_imageSize.X / MathF.Max(_imageSize.Y, 1f));
        if (!_gizmoDragging)
            _gizmoMatrix = primary.WorldMatrix;
        var operation = tools.Tool == EditorTool.Move ? ImGuizmoOperation.Translate : ImGuizmoOperation.Rotate;
        var mode = tools.LocalSpace ? ImGuizmoMode.Local : ImGuizmoMode.World;
        bool snap = tools.Snap ^ ImGui.GetIO().KeyCtrl;
        float grid = session.Scene.Settings.GridSpacing > 0 ? session.Scene.Settings.GridSpacing : 1f;
        var snapValues = operation == ImGuizmoOperation.Translate ? new Vector3(grid * 0.5f) : new Vector3(tools.AngleStep);
        var matrix = _gizmoMatrix;
        bool changed = ImGuizmo.Manipulate((float*)&view, (float*)&projection, operation, mode, (float*)&matrix, null,
            snap ? (float*)&snapValues : null);

        if (ImGuizmo.IsUsing())
        {
            if (!_gizmoDragging)
                BeginGizmoDrag(primary);
            if (changed)
            {
                _gizmoMatrix = matrix;
                var delta = TransformMath.Delta(_pivotStart, matrix);
                var locals = TransformMath.ApplyDelta(_dragEntities, _dragStartWorld, _dragStartLocal, delta);
                session.Undo.Execute(new SetTransformCommand(_dragEntities, locals, operation == ImGuizmoOperation.Translate ? "Move" : "Rotate"));
            }
        }
        else
        {
            EndGizmoDrag();
        }
    }

    private void BeginGizmoDrag(Entity primary)
    {
        var session = _ctx.Session;
        _dragEntities.Clear();
        _dragEntities.AddRange(SelectionController.TopLevel(session.Scene, session.Selection).Where(e => !e.Locked));
        if (!_dragEntities.Contains(primary) && !primary.Locked)
            _dragEntities.Add(primary);
        _dragStartWorld = _dragEntities.Select(e => e.WorldMatrix).ToArray();
        _dragStartLocal = _dragEntities.Select(e => e.Local).ToArray();
        _pivotStart = primary.WorldMatrix;
        _gizmoDragging = true;
        // A new drag never merges into the previous one.
        session.Undo.BreakMerge();
    }

    private void EndGizmoDrag()
    {
        if (!_gizmoDragging)
            return;
        _gizmoDragging = false;
        _dragEntities.Clear();
        _ctx.Session.Undo.BreakMerge();
    }

    /// <summary>Draws the toolbar; returns true when the mouse is over it.</summary>
    private bool DrawToolbar()
    {
        var style = ImGui.GetStyle();
        float scale = style.FontScaleMain * style.FontScaleDpi;
        var pad = new Vector2(6, 6) * scale;
        _labels.Refresh();
        bool compact = _imageSize.X < 760f * scale;
        ImGui.SetCursorScreenPos(_imageMin + pad);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(6, 3) * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(4, 4) * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 4 * scale);
        ImGui.PushStyleColor(ImGuiCol.Button, Theme.WithAlpha(Theme.OverlayBackground, ToolbarAlpha));
        ImGui.PushStyleColor(ImGuiCol.FrameBg, Theme.WithAlpha(Theme.OverlayBackground, ToolbarAlpha));
        ImGui.BeginGroup();
        var tools = _ctx.Tools;
        if (ToolButton(ToolbarLabels.SelectId, Str.ToolSelect, tools.Tool == EditorTool.Select))
            tools.Tool = EditorTool.Select;
        ImGui.SameLine();
        if (ToolButton(ToolbarLabels.MoveId, Str.ToolMove, tools.Tool == EditorTool.Move))
            tools.Tool = EditorTool.Move;
        ImGui.SameLine();
        if (ToolButton(ToolbarLabels.RotateId, Str.ToolRotate, tools.Tool == EditorTool.Rotate))
            tools.Tool = EditorTool.Rotate;
        ImGui.SameLine();
        if (ToolButton(tools.LocalSpace ? ToolbarLabels.LocalId : ToolbarLabels.WorldId, tools.LocalSpace ? Str.ToolSpaceLocal : Str.ToolSpaceWorld, false))
            tools.LocalSpace = !tools.LocalSpace;
        ImGui.SameLine();
        if (ToolButton(ToolbarLabels.SnapId, Str.ToolSnapping, tools.Snap))
            tools.Snap = !tools.Snap;
        ImGui.SameLine(0, 10 * scale);

        int shading = (int)View.Shading;
        if (ImGui.BeginCombo("##shading", compact ? _labels.ShadingIcon[shading] : _labels.ShadingFull[shading], ImGuiComboFlags.WidthFitPreview))
        {
            for (int i = 0; i < ShadingModes.Length; i++)
                if (ImGui.Selectable(_labels.ShadingFull[i], shading == i))
                    View.Shading = ShadingModes[i];
            ImGui.EndCombo();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(_labels.ShadingTooltip);
        ImGui.SameLine();
        bool persp = Camera.Projection == ProjectionKind.Perspective;
        if (ImGui.Button(compact ? ToolbarLabels.ProjectionIconId : persp ? _labels.Perspective : _labels.Orthographic))
            Camera.ToggleProjection();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(persp ? _labels.PerspectiveTooltip : _labels.OrthographicTooltip);
        ImGui.SameLine();
        int overlay = (int)View.Overlay;
        if (ImGui.BeginCombo("##overlay", compact ? _labels.OverlayIcon[overlay] : _labels.OverlayFull[overlay], ImGuiComboFlags.WidthFitPreview))
        {
            for (int i = 0; i < OverlayModes.Length; i++)
                if (ImGui.Selectable(_labels.OverlayFull[i], overlay == i))
                    View.Overlay = OverlayModes[i];
            ImGui.EndCombo();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(_labels.OverlayTooltip);
        ImGui.SameLine();
        if (ToolButton(ToolbarLabels.GridId, Str.ViewportGrid, View.ShowGrid))
            View.ShowGrid = !View.ShowGrid;
        ImGui.SameLine();
        if (ToolButton(ToolbarLabels.StatsId, Str.ViewportStats, ShowStats))
            ShowStats = !ShowStats;
        ImGui.EndGroup();
        _toolbarBottom = ImGui.GetItemRectMax().Y;
        bool hovered = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem) || ImGui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId);
        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar(3);
        return hovered;
    }

    private static bool ToolButton(string id, Str tooltip, bool active)
    {
        if (active)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, Theme.WithAlpha(Theme.Accent, 0.55f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Theme.WithAlpha(Theme.Accent, 0.7f));
        }

        bool clicked = ImGui.Button(id);
        if (active)
            ImGui.PopStyleColor(2);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Loc.Get(tooltip));
        return clicked;
    }

    /// <summary>Draws the view cube; returns true when the mouse is over it.</summary>
    private bool DrawViewCube(ImDrawListPtr drawList, bool viewportHovered)
    {
        if (!ShowViewCube)
            return false;
        var style = ImGui.GetStyle();
        float scale = style.FontScaleMain * style.FontScaleDpi;
        float size = 84f * scale;
        float radius = size * 0.6f;
        var center = new Vector2(_imageMin.X + _imageSize.X - radius - 10f * scale, MathF.Max(_toolbarBottom, _imageMin.Y) + radius + 8f * scale);
        if (_imageSize.X < size * 2.4f || _imageSize.Y < size * 2.2f)
            return false;
        ViewCube.Project(Camera.Right, Camera.Up, Camera.Forward, center, size, _cubeFaces);
        var mouse = ImGui.GetIO().MousePos;
        bool inArea = Vector2.Distance(mouse, center) < radius;
        _hoveredFace = viewportHovered && inArea ? ViewCube.HitTest(_cubeFaces, mouse) : null;

        // Looking straight at one face, the cube shows only that face: arrows around it lead to the four neighbours.
        bool axisAligned = ViewCube.IsAxisAligned(Camera.Forward);
        int hoveredArrow = -1;
        if (axisAligned)
        {
            ViewCube.Arrows(Camera.Right, Camera.Up, center, radius - 2f * scale, 9f * scale, _cubeArrows);
            if (viewportHovered && inArea && _hoveredFace is null)
                hoveredArrow = ViewCube.HitTestArrow(_cubeArrows, mouse, 11f * scale);
        }

        drawList.AddCircleFilled(center, radius, Theme.U32(Theme.WithAlpha(Theme.OverlayBackground, inArea ? 0.8f : 0.5f)));
        var font = ImGui.GetFont();
        float baseFontSize = ImGui.GetFontSize();
        foreach (var face in _cubeFaces)
        {
            if (face.Facing <= 0.02f)
                continue;
            bool hot = _hoveredFace == face.View;
            bool current = Camera.SnappedView == face.View;
            var fill = hot ? Theme.WithAlpha(Theme.Accent, 0.9f) : current ? Theme.Rgb(0x6B4A2A, 0.95f) : Theme.Rgb(0x3A3D44, 0.95f);
            drawList.AddQuadFilled(face.C0, face.C1, face.C2, face.C3, Theme.U32(fill));
            drawList.AddQuad(face.C0, face.C1, face.C2, face.C3, Theme.U32(Theme.Rgb(0x16171A, 0.95f)), 1.2f * scale);
            if (face.Facing > 0.35f)
            {
                // The label fills at most 85 % of the face's projected width (long Russian names on oblique faces).
                string label = FaceLabel(face.View);
                var fullSize = ImGui.CalcTextSize(label);
                float faceWidth = MathF.Min(Vector2.Distance(face.C0, face.C1), Vector2.Distance(face.C1, face.C2));
                float fontSize = MathF.Min(baseFontSize * 0.8f, baseFontSize * faceWidth * 0.85f / MathF.Max(fullSize.X, 1f));
                var textSize = fullSize * (fontSize / baseFontSize);
                var alpha = Math.Clamp((face.Facing - 0.35f) / 0.4f, 0f, 1f);
                drawList.AddText(font, fontSize, face.Center - textSize * 0.5f, Theme.U32(Theme.WithAlpha(Theme.Text, alpha)), label);
            }
        }

        if (axisAligned)
        {
            for (int i = 0; i < ViewCube.ArrowCount; i++)
            {
                var arrow = _cubeArrows[i];
                var color = i == hoveredArrow ? Theme.Accent : Theme.WithAlpha(Theme.TextMuted, inArea ? 0.95f : 0.6f);
                drawList.AddTriangleFilled(arrow.Tip, arrow.Left, arrow.Right, Theme.U32(color));
            }
        }

        string caption = Camera.SnappedView is { } snapped
            ? _labels.SnappedCaption(snapped, Camera.Projection == ProjectionKind.Perspective)
            : Camera.Projection == ProjectionKind.Perspective ? _labels.PerspectiveShort : _labels.OrthographicShort;
        var captionSize = ImGui.CalcTextSize(caption);
        drawList.AddText(new Vector2(center.X - captionSize.X * 0.5f, center.Y + radius + 4f * scale), Theme.U32(Theme.TextMuted), caption);

        ViewDirection? target = _hoveredFace ?? (hoveredArrow >= 0 ? _cubeArrows[hoveredArrow].View : null);
        if (target is { } view && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            Camera.SnapTo(view);
            _host.SetActiveViewport(this);
        }

        if (target is { } tip)
            ImGui.SetTooltip(FaceLabel(tip));
        return inArea;
    }

    private void DrawAxisTriad(ImDrawListPtr drawList, Vector2 origin, float length)
    {
        DrawAxis(drawList, origin, Vector3.UnitX, Theme.AxisX, "X", length);
        DrawAxis(drawList, origin, Vector3.UnitY, Theme.AxisY, "Y", length);
        DrawAxis(drawList, origin, Vector3.UnitZ, Theme.AxisZ, "Z", length);
    }

    private void DrawAxis(ImDrawListPtr drawList, Vector2 origin, Vector3 axis, Vector4 color, string label, float length)
    {
        var dir = new Vector2(Vector3.Dot(axis, Camera.Right), -Vector3.Dot(axis, Camera.Up));
        var end = origin + dir * length;
        drawList.AddLine(origin, end, Theme.U32(color), 2f);
        if (dir.LengthSquared() > 0.04f)
        {
            var size = ImGui.CalcTextSize(label);
            drawList.AddText(end + dir * (size.X * 0.7f) - size * 0.5f, Theme.U32(color), label);
        }
    }

    private void DrawStats(ImDrawListPtr drawList)
    {
        var style = ImGui.GetStyle();
        float scale = style.FontScaleMain * style.FontScaleDpi;
        var pad = new Vector2(8, 6) * scale;
        double now = _ctx.Timer.Elapsed;
        float lineHeight = ImGui.GetTextLineHeight();
        float bottom = _imageMin.Y + _imageSize.Y - pad.Y;
        if (ShowStats)
        {
            if (now - _statsUpdatedAt > 0.25 || _statsText.Length == 0)
            {
                _statsUpdatedAt = now;
                var t = _target;
                _statsText = string.Create(CultureInfo.InvariantCulture,
                    $"{Loc.Format(Str.ViewportFps, _ctx.Timer.FramesPerSecond)}  ·  {Loc.Format(Str.ViewportFrameTime, _ctx.Timer.FrameMilliseconds)}  ·  {t?.Width ?? 0} × {t?.Height ?? 0}");
            }

            var size = ImGui.CalcTextSize(_statsText);
            var pos = new Vector2(_imageMin.X + pad.X, bottom - size.Y);
            drawList.AddRectFilled(pos - new Vector2(6, 3) * scale, pos + size + new Vector2(6, 3) * scale,
                Theme.U32(Theme.WithAlpha(Theme.OverlayBackground, 0.7f)), 4 * scale);
            drawList.AddText(pos, Theme.U32(Theme.TextMuted), _statsText);
            bottom = pos.Y - 6f * scale;
        }

        // Axis triad in the lower-left corner (world X red, Y green, Z blue).
        float axisLength = 18f * scale;
        DrawAxisTriad(drawList, new Vector2(_imageMin.X + pad.X + axisLength + 6f * scale, bottom - axisLength - lineHeight * 0.3f), axisLength);

        if (_ctx.Session.Content is { Caption.Length: > 0 } content && _imageSize.X > 520f * scale)
        {
            var size = ImGui.CalcTextSize(content.Caption);
            var pos = new Vector2(_imageMin.X + _imageSize.X - size.X - pad.X, _imageMin.Y + _imageSize.Y - size.Y - pad.Y);
            drawList.AddText(pos, Theme.U32(Theme.WithAlpha(Theme.TextMuted, 0.8f)), content.Caption);
        }
    }

    /// <summary>Toolbar and overlay texts, rebuilt only when the language changes (no per-frame string building).</summary>
    private sealed class ToolbarLabels
    {
        public const string SelectId = Icons.Pointer + "##select";
        public const string MoveId = Icons.Move + "##move";
        public const string RotateId = Icons.Rotate3D + "##rotate";
        public const string LocalId = Icons.Box + "##space";
        public const string WorldId = Icons.Axis3D + "##space";
        public const string SnapId = Icons.Magnet + "##snap";
        public const string GridId = Icons.Grid + "##grid";
        public const string StatsId = Icons.Activity + "##stats";
        public const string ProjectionIconId = Icons.Camera + "##projection";
        private string _language = "";
        private string[] _snappedPersp = Array.Empty<string>();
        private string[] _snappedOrtho = Array.Empty<string>();

        public string[] ShadingFull { get; private set; } = Array.Empty<string>();
        public string[] ShadingIcon { get; } = ShadingModes.Select(s => ViewportPanel.ShadingIcon(s)).ToArray();
        public string[] OverlayFull { get; private set; } = Array.Empty<string>();
        public string[] OverlayIcon { get; } = OverlayModes.Select(o => ViewportPanel.OverlayIcon(o)).ToArray();
        public string Perspective { get; private set; } = "";
        public string Orthographic { get; private set; } = "";
        public string PerspectiveShort { get; private set; } = "";
        public string OrthographicShort { get; private set; } = "";
        public string PerspectiveTooltip { get; private set; } = "";
        public string OrthographicTooltip { get; private set; } = "";
        public string ShadingTooltip { get; private set; } = "";
        public string OverlayTooltip { get; private set; } = "";

        public void Refresh()
        {
            if (_language == Loc.Language)
                return;
            _language = Loc.Language;
            ShadingFull = ShadingModes.Select(s => $"{ViewportPanel.ShadingIcon(s)}  {ShadingLabel(s)}").ToArray();
            OverlayFull = OverlayModes.Select(o => $"{ViewportPanel.OverlayIcon(o)}  {OverlayLabel(o)}").ToArray();
            PerspectiveShort = Loc.Get(Str.ViewPerspectiveShort);
            OrthographicShort = Loc.Get(Str.ViewOrthoShort);
            Perspective = $"{Icons.Camera}  {PerspectiveShort}##projection";
            Orthographic = $"{Icons.Camera}  {OrthographicShort}##projection";
            PerspectiveTooltip = $"{Loc.Get(Str.ViewProjection)}: {Loc.Get(Str.ProjectionPerspective)}";
            OrthographicTooltip = $"{Loc.Get(Str.ViewProjection)}: {Loc.Get(Str.ProjectionOrthographic)}";
            ShadingTooltip = Loc.Get(Str.ViewShading);
            OverlayTooltip = Loc.Get(Str.ViewOverlay);
            _snappedPersp = ViewCube.AllViews.Select(v => $"{FaceLabel(v)} · {PerspectiveShort}").ToArray();
            _snappedOrtho = ViewCube.AllViews.Select(v => $"{FaceLabel(v)} · {OrthographicShort}").ToArray();
        }

        public string SnappedCaption(ViewDirection view, bool perspective) => (perspective ? _snappedPersp : _snappedOrtho)[(int)view];
    }

    private int ToPixelX(Vector2 screen) => (int)MathF.Floor((screen.X - _imageMin.X) * _framebufferScale);

    private int ToPixelY(Vector2 screen) => (int)MathF.Floor((screen.Y - _imageMin.Y) * _framebufferScale);

    /// <summary>Localised view-cube face / view name.</summary>
    public static string FaceLabel(ViewDirection view) => view switch
    {
        ViewDirection.Top => Loc.Get(Str.ViewTop),
        ViewDirection.Bottom => Loc.Get(Str.ViewBottom),
        ViewDirection.Front => Loc.Get(Str.ViewFront),
        ViewDirection.Back => Loc.Get(Str.ViewBack),
        ViewDirection.Left => Loc.Get(Str.ViewLeft),
        _ => Loc.Get(Str.ViewRight),
    };

    /// <summary>Localised shading name.</summary>
    public static string ShadingLabel(ViewShading s) => s switch
    {
        ViewShading.Wireframe => Loc.Get(Str.ShadingWireframe),
        ViewShading.Colorless => Loc.Get(Str.ShadingColorless),
        _ => Loc.Get(Str.ShadingFull),
    };

    /// <summary>Icon of a shading mode.</summary>
    public static string ShadingIcon(ViewShading s) => s switch
    {
        ViewShading.Wireframe => Icons.Frame,
        ViewShading.Colorless => Icons.Box,
        _ => Icons.Sparkles,
    };

    /// <summary>Localised overlay name.</summary>
    public static string OverlayLabel(OverlayMode o) => o switch
    {
        OverlayMode.Lux => Loc.Get(Str.OverlayLux),
        OverlayMode.Noise => Loc.Get(Str.OverlayNoise),
        OverlayMode.Cables => Loc.Get(Str.OverlayCables),
        _ => Loc.Get(Str.OverlayNone),
    };

    /// <summary>Icon of an overlay.</summary>
    public static string OverlayIcon(OverlayMode o) => o switch
    {
        OverlayMode.Lux => Icons.Sun,
        OverlayMode.Noise => Icons.AudioLines,
        OverlayMode.Cables => Icons.Cable,
        _ => Icons.Layers,
    };

    /// <summary>All shading modes in menu order.</summary>
    public static readonly ViewShading[] ShadingModes = { ViewShading.Wireframe, ViewShading.Colorless, ViewShading.Full };

    /// <summary>All overlays in menu order.</summary>
    public static readonly OverlayMode[] OverlayModes = { OverlayMode.None, OverlayMode.Lux, OverlayMode.Noise, OverlayMode.Cables };

    private static readonly (ImGuiMouseButton, NavigationButton)[] Buttons =
    {
        (ImGuiMouseButton.Left, NavigationButton.Left),
        (ImGuiMouseButton.Right, NavigationButton.Right),
        (ImGuiMouseButton.Middle, NavigationButton.Middle),
    };

    private enum PickKind
    {
        Select,
        Focus,
    }

    private readonly record struct PickRequest(PickKind Kind, int X, int Y, bool Shift);
}
