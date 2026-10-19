using System.Numerics;
using Candela.App.Editing;
using Candela.App.Session;
using Candela.App.Shell;
using Candela.App.Ui;
using Candela.Core.Math;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;
using Candela.Electrical;
using Hexa.NET.ImGui;
using Candela.Rendering;
using Hexa.NET.ImGuizmo;

namespace Candela.App.Viewport;

/// <summary>
/// Cables in the viewport: the Cable tool (requirement "Кабели": the cable type's icon follows the cursor; clicks on
/// walls, trusses or the floor fix route points, a click on a device plugs the cable in — with the port picker next to
/// the cursor when several ports fit —, a click on the middle of a cable branches it; right-click or Esc finishes), the
/// hover feedback (the device's ports coloured by whether the cable fits, the split point on a cable, the cable's
/// length and weight so far), and the nodes of a selected cable, which are selected, dragged and moved with the Move
/// gizmo like objects (every move one undo step). Cables are also picked by their drawn line, so a thin cable is easy
/// to click.
/// </summary>
internal sealed unsafe partial class ViewportPanel
{
    private const string PortPickerId = "##cable-port-picker";
    private const string CableTypesPopupId = "##cable-types";
    private CableTarget _cableTarget;
    private Vector2 _cableTargetMouse = new(float.NaN);
    private long _cableTargetVersion = -1;
    private Vector2 _cablePress = new(float.NaN);
    private Vector2 _rightPress = new(float.NaN);
    private bool _openPicker;
    private Vector2 _pickerAt;
    private Vector3[] _nodeWorld = new Vector3[64];
    private readonly Dictionary<Guid, int> _nodeIndex = new();
    private readonly Vector2[] _boxCorners = new Vector2[8];
    private readonly bool[] _boxVisible = new bool[8];
    private NodeDrag _nodeDrag;
    private Matrix4x4 _nodeGizmoMatrix;
    private IReadOnlyList<PortCandidate>? _hoverPorts;
    private (Guid Device, long Version, int Draft, string Type) _hoverPortsKey;
    private string _cableLabel = "";
    private float _cableLabelLength = float.NaN;
    private readonly CableToolLabels _cableLabels = new();
    private readonly List<Guid> _devices = new();
    private long _devicesVersion = -1;
    private Engine.StageEngine? _devicesEngine;

    /// <summary>The cable tool's target under the mouse (last frame; tests).</summary>
    internal CableTarget CableTarget => _cableTarget;

    /// <summary>True while a cable node is dragged with the mouse or the gizmo.</summary>
    internal bool IsDraggingNode => _nodeDrag.Active;

    private CableTool? Cables => _ctx.Tools.Cables;

    // ---- input ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// A left press with the Cable tool: remembered, and acted on at the release if the mouse did not move (a drag is a
    /// navigation in some styles). Returns true when the press belongs to the tool.
    /// </summary>
    private bool BeginCableClick(Vector2 mouse)
    {
        if (_ctx.Tools.Tool != EditorTool.Cable || Cables is null || _ctx.Session.Engine is null)
            return false;
        _cablePress = mouse;
        return true;
    }

    /// <summary>Per-frame cable input of this viewport: the click at release, the right-click that finishes, the hover target.</summary>
    private void UpdateCableInput(bool hovered, bool overOverlay, Vector2 mouse)
    {
        var tool = Cables;
        if (_ctx.Tools.Tool != EditorTool.Cable || tool is null)
        {
            _cablePress = new Vector2(float.NaN);
            _rightPress = new Vector2(float.NaN);
            _cableTarget = default;
            return;
        }

        tool.Validate();
        if (hovered && !overOverlay)
            UpdateCableTarget(mouse);
        else if (!hovered)
            _cableTarget = default;

        if (!float.IsNaN(_cablePress.X) && !ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            var press = _cablePress;
            _cablePress = new Vector2(float.NaN);
            if (Vector2.Distance(press, mouse) <= ClickSlop())
            {
                var target = ResolveCableTarget(press);
                _host.SetActiveViewport(this);
                if (tool.Click(target) == CableClickOutcome.PickPort)
                {
                    _openPicker = true;
                    _pickerAt = mouse;
                }

                _cableTargetVersion = -1; // the scene changed: resolve again
            }
        }

        // Right-click (press and release in place) finishes the cable; a right drag stays a navigation.
        if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
            _rightPress = mouse;
        if (!float.IsNaN(_rightPress.X) && ImGui.IsMouseReleased(ImGuiMouseButton.Right))
        {
            if (Vector2.Distance(_rightPress, mouse) <= ClickSlop() && tool.IsDrawing)
            {
                tool.Finish();
                _cableTargetVersion = -1;
            }

            _rightPress = new Vector2(float.NaN);
        }
    }

    private float ClickSlop()
    {
        var style = ImGui.GetStyle();
        return 5f * style.FontScaleMain * style.FontScaleDpi;
    }

    private void UpdateCableTarget(Vector2 mouse)
    {
        long version = _ctx.Session.Scene.Version;
        if (Vector2.DistanceSquared(mouse, _cableTargetMouse) < 0.25f && version == _cableTargetVersion)
            return;
        _cableTargetMouse = mouse;
        _cableTargetVersion = version;
        _cableTarget = ResolveCableTarget(mouse);
    }

    /// <summary>
    /// What the cable tool would act on at <paramref name="mouse"/>, in this order: a free cable node (not one plugged
    /// into a device: clicking a device must plug into it) within a few pixels, a device under the cursor, the middle of
    /// a cable segment within a few pixels of its drawn line (so thin cables are easy to hit and win over the wall or
    /// floor behind them), a surface anchored to the object under the cursor, or the ground.
    /// </summary>
    internal CableTarget ResolveCableTarget(Vector2 mouse)
    {
        var session = _ctx.Session;
        if (session.Engine is not { } engine || !ContainsScreenPoint(mouse))
            return default;
        if (CableUnder(mouse, includeSegments: false, skipPlugged: true) is { } node)
            return node;
        var hit = SurfaceAt(mouse, _ctx.Gpu.Renderer);
        Entity? entity = hit.Kind == SurfaceKind.Geometry && hit.PickId != 0 && session.Content is EngineViewportContent content
            ? content.EntityOfPick(hit.PickId)
            : null;
        if (entity is not null && entity.Get<CableComponent>() is null && DeviceOf(engine, entity) is { } device)
            return CableTarget.Device(device.Id, hit.Position);
        // A ray through a gap of a device (between a yoke and its head, under a slim batten) still means the device,
        // not the floor or the background far behind it.
        var ray = Camera.RayThrough(mouse - _imageMin, _imageSize);
        if ((entity is null || entity.Get<CableComponent>() is null) &&
            DeviceAlongRay(engine, ray, Vector3.Distance(ray.Origin, hit.Position)) is var (near, entry))
            return CableTarget.Device(near, entry);
        if (CableUnder(mouse, includeSegments: true, skipPlugged: true) is { } onCable)
            return onCable;
        if (entity is not null)
        {
            if (entity.Get<CableComponent>() is not null)
                return CableUnder(mouse, includeSegments: true, radiusScale: 3f, skipPlugged: true) ?? CableTarget.Surface(hit.Position, hit.Normal, Guid.Empty);
            return CableTarget.Surface(hit.Position, hit.Normal, entity.Id);
        }

        return CableTarget.Surface(hit.Position, hit.Normal, Guid.Empty);
    }

    /// <summary>
    /// The device whose bounding box the ray enters first, nearer than <paramref name="limit"/> metres (the surface the
    /// ray hits), with the entry point; null when there is none. The device list is rebuilt when the scene changes.
    /// </summary>
    private (Guid Device, Vector3 Entry)? DeviceAlongRay(Engine.StageEngine engine, Ray ray, float limit)
    {
        var session = _ctx.Session;
        if (session.Content is not { } content || !float.IsFinite(ray.Direction.X))
            return null;
        var scene = session.Scene;
        if (_devicesVersion != scene.Version || !ReferenceEquals(_devicesEngine, engine))
        {
            _devicesVersion = scene.Version;
            _devicesEngine = engine;
            _devices.Clear();
            foreach (var e in scene.All())
            {
                if (e.IsVisibleInHierarchy && CablePorts.IsDevice(engine, e))
                    _devices.Add(e.Id);
            }
        }

        Guid best = Guid.Empty;
        float bestT = float.IsFinite(limit) ? limit : float.MaxValue;
        foreach (var id in _devices)
        {
            if (!content.TryGetBounds(id, out var box) || box.IsEmpty || box.Contains(ray.Origin))
                continue;
            if (box.Intersect(ray) is { } t && t < bestT)
            {
                bestT = t;
                best = id;
            }
        }

        return best == Guid.Empty ? null : (best, ray.At(bestT));
    }

    /// <summary>The device an entity belongs to (itself, or the fixture a picked part belongs to), or null.</summary>
    private static Entity? DeviceOf(Engine.StageEngine engine, Entity entity)
    {
        for (var e = entity; e is not null; e = e.Parent)
        {
            if (CablePorts.IsDevice(engine, e))
                return e;
        }

        return null;
    }

    /// <summary>
    /// The cable node within a few pixels of <paramref name="mouse"/>, else (with <paramref name="includeSegments"/>) the
    /// closest point on a cable segment within a few pixels — the world point is where the mouse ray passes the segment.
    /// </summary>
    internal CableTarget? CableUnder(Vector2 mouse, bool includeSegments, float radiusScale = 1f, bool skipPlugged = false)
    {
        var scene = _ctx.Session.Scene;
        var style = ImGui.GetStyle();
        float scale = style.FontScaleMain * style.FontScaleDpi;
        float nodeRadius = 9f * scale * radiusScale, lineRadius = 6f * scale * radiusScale;
        CableTarget? best = null;
        float bestNode = nodeRadius * nodeRadius, bestLine = lineRadius * lineRadius;
        bool nodeFound = false;
        var ray = Camera.RayThrough(mouse - _imageMin, _imageSize);
        foreach (var (entity, cable) in scene.Query<CableComponent>())
        {
            if (!entity.IsVisibleInHierarchy || cable.Nodes is not { Count: > 0 } nodes)
                continue;
            if (_nodeWorld.Length < nodes.Count)
                _nodeWorld = new Vector3[Math.Max(nodes.Count, _nodeWorld.Length * 2)];
            int n = CableGeometry.WriteNodeWorldPositions(scene, entity, cable, _nodeWorld);
            _nodeIndex.Clear();
            for (int i = 0; i < n; i++)
            {
                if (nodes[i] is not { } node)
                    continue;
                _nodeIndex.TryAdd(node.Id, i);
                if (skipPlugged && IsPlugged(cable, node.Id))
                    continue;
                if (!Camera.WorldToScreen(_nodeWorld[i], _imageSize, out var s))
                    continue;
                float d = Vector2.DistanceSquared(s + _imageMin, mouse);
                if (d < bestNode)
                {
                    bestNode = d;
                    nodeFound = true;
                    best = CableTarget.Node(entity.Id, node.Id, _nodeWorld[i]);
                }
            }

            if (!includeSegments || nodeFound)
                continue;
            foreach (var segment in cable.Segments)
            {
                if (!_nodeIndex.TryGetValue(segment.FromNode, out int a) || !_nodeIndex.TryGetValue(segment.ToNode, out int b))
                    continue;
                var p0 = _nodeWorld[a];
                var p1 = _nodeWorld[b];
                if (!Camera.WorldToScreen(p0, _imageSize, out var s0) || !Camera.WorldToScreen(p1, _imageSize, out var s1))
                    continue;
                float d = DistanceToSegmentSquared(mouse, s0 + _imageMin, s1 + _imageMin, out _);
                if (d >= bestLine)
                    continue;
                bestLine = d;
                best = CableTarget.Segment(entity.Id, segment.FromNode, segment.ToNode, ClosestOnSegment(ray, p0, p1));
            }
        }

        return best;
    }

    private static bool IsPlugged(CableComponent cable, Guid node)
    {
        foreach (var e in cable.Endpoints)
        {
            if (e is not null && e.NodeId == node)
                return true;
        }

        return false;
    }

    /// <summary>Squared screen distance from <paramref name="p"/> to the segment a–b, and the parameter of the closest point.</summary>
    internal static float DistanceToSegmentSquared(Vector2 p, Vector2 a, Vector2 b, out float t)
    {
        var ab = b - a;
        float len = ab.LengthSquared();
        t = len > 1e-9f ? Math.Clamp(Vector2.Dot(p - a, ab) / len, 0f, 1f) : 0f;
        return Vector2.DistanceSquared(p, a + ab * t);
    }

    /// <summary>The point of the segment a–b closest to the ray (world space).</summary>
    internal static Vector3 ClosestOnSegment(Ray ray, Vector3 a, Vector3 b)
    {
        var u = b - a;
        var v = ray.Direction;
        var w = a - ray.Origin;
        float uu = Vector3.Dot(u, u), uv = Vector3.Dot(u, v), vv = Vector3.Dot(v, v), uw = Vector3.Dot(u, w), vw = Vector3.Dot(v, w);
        float denom = uu * vv - uv * uv;
        float s = MathF.Abs(denom) > 1e-12f ? (uv * vw - vv * uw) / denom : 0f;
        s = float.IsFinite(s) ? Math.Clamp(s, 0f, 1f) : 0f;
        return a + u * s;
    }

    // ---- drawing ----------------------------------------------------------------------------------------------------

    /// <summary>The Cable tool's drawing: the cable being drawn with its rubber band, the target under the mouse, the cursor icon, the port picker.</summary>
    private void DrawCableTool(ImDrawListPtr drawList, Vector2 mouse, bool hovered)
    {
        var tool = Cables;
        if (_ctx.Tools.Tool != EditorTool.Cable || tool is null)
            return;
        var style = ImGui.GetStyle();
        float scale = style.FontScaleMain * style.FontScaleDpi;
        var type = tool.Draft?.Type ?? tool.Type;
        var (r, g, b) = type?.ColorSrgb ?? (0.9f, 0.9f, 0.9f);
        var color = new Vector4(r, g, b, 1f);
        _cableLabels.Refresh(tool, type);

        if (tool.Draft is { } draft)
            DrawDraft(drawList, draft, color, scale, mouse, hovered);
        if (hovered)
            DrawCableTarget(drawList, tool, type, scale);
        DrawTypeChip(tool, scale);
        if (_shape.Mode == ShapeMode.None)
            DrawToolHint(drawList, scale, tool.IsDrawing ? _cableLabels.DrawingHint : _cableLabels.Hint);
        if (hovered && type is not null && ContainsScreenPoint(mouse))
            DrawCursorBadge(ImGui.GetForegroundDrawList(), mouse, type, color, scale);
        DrawPortPicker(tool, scale);
    }

    private void DrawDraft(ImDrawListPtr drawList, CableDraft draft, Vector4 color, float scale, Vector2 mouse, bool hovered)
    {
        uint line = Theme.U32(color);
        uint shadow = Theme.U32(new Vector4(0f, 0f, 0f, 0.55f));
        Vector2 previous = default;
        bool havePrevious = false;
        foreach (var p in draft.Points)
        {
            if (!Camera.WorldToScreen(p.World, _imageSize, out var s))
            {
                havePrevious = false;
                continue;
            }

            s += _imageMin;
            if (havePrevious)
            {
                drawList.AddLine(previous, s, shadow, 5f * scale);
                drawList.AddLine(previous, s, line, 3f * scale);
            }

            drawList.AddCircleFilled(s, 4f * scale, p.Port is not null ? Theme.U32(Theme.Accent) : line);
            drawList.AddCircle(s, 4f * scale, shadow, 12, 1.2f * scale);
            previous = s;
            havePrevious = true;
        }

        // The rubber band to where the next click would go, and the length so far.
        Vector3? next = hovered && _cableTarget.Kind != CableTargetKind.None ? _cableTarget.Position : null;
        if (havePrevious && next is { } n && Camera.WorldToScreen(n, _imageSize, out var e))
            DashedLine(drawList, previous, e + _imageMin, Theme.U32(Theme.WithAlpha(color, 0.9f)), 2f * scale, 8f * scale);
        float length = draft.RoutedLength(next) * 1.05f;
        if (!float.IsFinite(_cableLabelLength) || MathF.Abs(length - _cableLabelLength) >= 0.05f || _cableLabel.Length == 0)
        {
            _cableLabelLength = length;
            _cableLabel = CableLoc.Format(CableStr.ToolCableLength, CableTool.Metres(length), CableTool.Kilograms(length * draft.Type.WeightKgPerM));
        }

        var size = ImGui.CalcTextSize(_cableLabel);
        // Below the cable icon that follows the cursor.
        var pos = mouse + new Vector2(14f, 46f) * scale;
        drawList.AddRectFilled(pos - new Vector2(6f, 3f) * scale, pos + size + new Vector2(6f, 3f) * scale,
            Theme.U32(Theme.WithAlpha(Theme.OverlayBackground, 0.9f)), 4f * scale);
        drawList.AddText(pos, Theme.U32(Theme.Text), _cableLabel);
    }

    private void DrawCableTarget(ImDrawListPtr drawList, CableTool tool, CableType? type, float scale)
    {
        var target = _cableTarget;
        var accent = Theme.Accent;
        switch (target.Kind)
        {
            case CableTargetKind.Device:
                DrawDeviceTarget(drawList, tool, type, target.EntityId, scale);
                break;
            case CableTargetKind.CableNode:
            case CableTargetKind.CableSegment:
                if (Camera.WorldToScreen(target.Position, _imageSize, out var s))
                {
                    s += _imageMin;
                    float r = 6f * scale;
                    if (target.Kind == CableTargetKind.CableNode)
                    {
                        drawList.AddCircle(s, r * 1.4f, Theme.U32(accent), 20, 2.5f * scale);
                    }
                    else
                    {
                        drawList.AddQuadFilled(s + new Vector2(0, -r), s + new Vector2(r, 0), s + new Vector2(0, r), s + new Vector2(-r, 0), Theme.U32(accent));
                        drawList.AddQuad(s + new Vector2(0, -r), s + new Vector2(r, 0), s + new Vector2(0, r), s + new Vector2(-r, 0), Theme.U32(Theme.Rgb(0x16171A)), 1.5f * scale);
                    }

                    string label = tool.IsDrawing ? _cableLabels.Join : target.Kind == CableTargetKind.CableNode ? _cableLabels.Continue : _cableLabels.Branch;
                    var size = ImGui.CalcTextSize(label);
                    var pos = s + new Vector2(12f, -size.Y - 10f) * scale;
                    drawList.AddRectFilled(pos - new Vector2(5f, 2f) * scale, pos + size + new Vector2(5f, 2f) * scale, Theme.U32(Theme.WithAlpha(Theme.OverlayBackground, 0.9f)), 4f * scale);
                    drawList.AddText(pos, Theme.U32(Theme.Text), label);
                }

                break;
            case CableTargetKind.Surface:
                if (Camera.WorldToScreen(target.Position, _imageSize, out var p))
                {
                    p += _imageMin;
                    float r = 5f * scale;
                    uint c = Theme.U32(Theme.WithAlpha(target.EntityId != Guid.Empty ? accent : Theme.Text, 0.9f));
                    drawList.AddLine(p - new Vector2(r, 0), p + new Vector2(r, 0), c, 1.5f * scale);
                    drawList.AddLine(p - new Vector2(0, r), p + new Vector2(0, r), c, 1.5f * scale);
                    if (target.EntityId != Guid.Empty)
                        drawList.AddCircle(p, r * 1.2f, c, 16, 1.2f * scale);
                }

                break;
        }
    }

    /// <summary>The hovered device: its box, and every port as a dot (green fits, amber adapter, grey in use, red does not fit).</summary>
    private void DrawDeviceTarget(ImDrawListPtr drawList, CableTool tool, CableType? type, Guid deviceId, float scale)
    {
        var session = _ctx.Session;
        if (session.Engine is not { } engine || session.Scene.Find(deviceId) is not { } device || type is null)
            return;
        if (session.Content is { } content && content.TryGetBounds(deviceId, out var bounds) && !bounds.IsEmpty)
            DrawBox(drawList, bounds, Theme.U32(Theme.WithAlpha(Theme.Accent, 0.9f)), 1.5f * scale);

        var key = (deviceId, session.Scene.Version, tool.Draft is { } d ? d.GetHashCode() : 0, type.Id);
        if (_hoverPorts is null || key != _hoverPortsKey)
        {
            _hoverPortsKey = key;
            _hoverPorts = CablePorts.Candidates(engine, device, type, tool.DraftSide(), tool.Draft?.StartPortRef);
        }

        int count = engine.Network.Ports.GetPorts(device).Count;
        // Usable ports come first in the list: drawn last, so they stay on top where dots overlap.
        for (int i = _hoverPorts.Count - 1; i >= 0; i--)
        {
            var c = _hoverPorts[i];
            var world = CablePorts.WorldPosition(device, c.Port, c.Index, count);
            if (!Camera.WorldToScreen(world, _imageSize, out var s))
                continue;
            s += _imageMin;
            // Green: fits; amber: fits with an adapter; grey: in use or the wrong direction; red: does not fit.
            var fill = c.Usable
                ? c.Check.Match == ConnectorMatch.NeedsAdapter ? Theme.Warning : Theme.Rgb(0x34D399)
                : c.Occupant is not null || c.WrongDirection ? Theme.Rgb(0x8E9196) : Theme.Error;
            drawList.AddCircleFilled(s, 4.5f * scale, Theme.U32(fill), 14);
            drawList.AddCircle(s, 4.5f * scale, Theme.U32(Theme.Rgb(0x16171A)), 14, 1.2f * scale);
        }
    }

    private void DrawBox(ImDrawListPtr drawList, Aabb box, uint color, float thickness)
    {
        for (int i = 0; i < 8; i++)
        {
            var c = new Vector3((i & 1) == 0 ? box.Min.X : box.Max.X, (i & 2) == 0 ? box.Min.Y : box.Max.Y, (i & 4) == 0 ? box.Min.Z : box.Max.Z);
            _boxVisible[i] = Camera.WorldToScreen(c, _imageSize, out var s);
            _boxCorners[i] = s + _imageMin;
        }

        for (int i = 0; i < 8; i++)
        {
            for (int bit = 1; bit <= 4; bit <<= 1)
            {
                int j = i | bit;
                if (j != i && _boxVisible[i] && _boxVisible[j])
                    drawList.AddLine(_boxCorners[i], _boxCorners[j], color, thickness);
            }
        }
    }

    /// <summary>The cable's icon next to the cursor: the connector of its source end in a badge of the cable's colour.</summary>
    private static void DrawCursorBadge(ImDrawListPtr drawList, Vector2 mouse, CableType type, Vector4 color, float scale)
    {
        float side = 26f * scale;
        var min = mouse + new Vector2(14f, 14f) * scale;
        drawList.AddRectFilled(min, min + new Vector2(side), Theme.U32(Theme.WithAlpha(Theme.OverlayBackground, 0.92f)), 5f * scale);
        drawList.AddRect(min, min + new Vector2(side), Theme.U32(color), 5f * scale, ImDrawFlags.None, 2f * scale);
        ConnectorIcons.Draw(drawList, min + new Vector2(3f * scale), side - 6f * scale, type.ConnectorA.IllustrationKey);
    }

    /// <summary>The chosen cable type under the toolbar; a click opens the list of every type.</summary>
    private void DrawTypeChip(CableTool tool, float scale)
    {
        var type = tool.Type;
        ImGui.SetCursorScreenPos(new Vector2(_imageMin.X + 6f * scale, MathF.Max(_toolbarBottom, _imageMin.Y) + 6f * scale));
        ImGui.PushStyleColor(ImGuiCol.Button, Theme.WithAlpha(Theme.OverlayBackground, ToolbarAlpha));
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(8, 4) * scale);
        string label = _cableLabels.Chip;
        var start = ImGui.GetCursorScreenPos();
        float swatch = ImGui.GetTextLineHeight();
        if (ImGui.Button(label))
            ImGui.OpenPopup(CableTypesPopupId);
        if (ImGui.IsItemHovered())
            SafeText.Tooltip(CableLoc.Get(CableStr.ToolCableType));
        if (type is not null)
        {
            var (r, g, b) = type.ColorSrgb;
            var p = start + new Vector2(8f * scale, (ImGui.GetItemRectSize().Y - swatch) * 0.5f);
            ImGui.GetWindowDrawList().AddRectFilled(p + new Vector2(0f, swatch * 0.3f), p + new Vector2(swatch * 1.4f, swatch * 0.7f), Theme.U32(new Vector4(r, g, b, 1f)), 2f);
        }

        ImGui.PopStyleVar();
        ImGui.PopStyleColor();
        if (ImGui.BeginPopup(CableTypesPopupId))
        {
            if (CableTypeMenu.Draw(tool.Catalog, tool.CableTypeId, scale) is { } chosen)
            {
                if (tool.IsDrawing && !string.Equals(chosen, tool.CableTypeId, StringComparison.OrdinalIgnoreCase))
                    tool.Finish();
                tool.CableTypeId = chosen;
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }
    }

    private void DrawPortPicker(CableTool tool, float scale)
    {
        if (_openPicker)
        {
            _openPicker = false;
            if (tool.Pick is not null)
            {
                ImGui.SetNextWindowPos(_pickerAt + new Vector2(14f, 14f) * scale, ImGuiCond.Always);
                ImGui.OpenPopup(PortPickerId);
            }
        }

        if (!ImGui.BeginPopup(PortPickerId, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings))
        {
            // Closed by a click outside or Escape: the cable keeps going without this device.
            if (tool.Pick is not null && !ImGui.IsPopupOpen(PortPickerId) && !_openPicker)
                tool.CancelPick();
            return;
        }

        if (tool.Pick is not { } pick || (tool.Draft?.Type ?? tool.Type) is not { } type)
        {
            ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            return;
        }

        if (PortPicker.Draw(pick, type, scale) is { } chosen)
        {
            ImGui.CloseCurrentPopup();
            tool.ChoosePort(chosen);
            _cableTargetVersion = -1;
        }

        ImGui.EndPopup();
    }

    private static void DashedLine(ImDrawListPtr drawList, Vector2 a, Vector2 b, uint color, float thickness, float dash)
    {
        float length = Vector2.Distance(a, b);
        if (length < 1f || !float.IsFinite(length))
            return;
        var dir = (b - a) / length;
        for (float t = 0f; t < length; t += dash * 2f)
            drawList.AddLine(a + dir * t, a + dir * MathF.Min(t + dash, length), color, thickness);
    }

    // ---- cable nodes of a selected cable ------------------------------------------------------------------------

    /// <summary>The selected cable whose nodes are shown (single selection), or null.</summary>
    private (Entity Entity, CableComponent Cable)? SelectedCable()
    {
        var session = _ctx.Session;
        if (session.Selection.Count != 1 || session.Selection.Primary is not { } id || session.Scene.Find(id) is not { } entity ||
            entity.Get<CableComponent>() is not { } cable || !SelectionController.IsEditable(session.Scene, entity))
            return null;
        return (entity, cable);
    }

    /// <summary>Keeps the selected node consistent with the selection (a node of another cable is forgotten).</summary>
    private void ValidateNodeSelection()
    {
        if (_ctx.Tools.CableNode is not { } node)
            return;
        if (SelectedCable() is not var (entity, cable) || entity.Id != node.CableId || cable.FindNode(node.NodeId) is null)
            _ctx.Tools.CableNode = null;
    }

    /// <summary>A left press on a node handle of the selected cable selects the node and starts dragging it.</summary>
    private bool BeginNodeDrag(Vector2 mouse)
    {
        if (_ctx.Tools.Tool == EditorTool.Cable || SelectedCable() is not var (entity, cable))
            return false;
        if (CableUnder(mouse, includeSegments: false) is not { Kind: CableTargetKind.CableNode } hit || hit.EntityId != entity.Id ||
            cable.FindNode(hit.NodeId) is not { } node)
            return false;
        _ctx.Tools.CableNode = new CableNodeRef(entity.Id, node.Id);
        var world = CableNodeEdits.WorldPosition(_ctx.Session.Scene, entity, node);
        var ray = Camera.RayThrough(mouse - _imageMin, _imageSize);
        _nodeDrag = new NodeDrag
        {
            Active = true,
            Node = new CableNodeRef(entity.Id, node.Id),
            Before = CableNodeEdits.Capture(node),
            PlanePoint = world,
            PlaneNormal = -Camera.Forward,
            Grab = world - (PlaneHit(ray, world, -Camera.Forward) ?? world),
            Press = mouse,
        };
        return true;
    }

    /// <summary>Follows the mouse while a node is dragged; commits on release (one undo step).</summary>
    private void UpdateNodeDrag(Vector2 mouse)
    {
        if (!_nodeDrag.Active)
            return;
        var session = _ctx.Session;
        if (CableNodeEdits.Resolve(session.Scene, _nodeDrag.Node) is not var (entity, cable, node))
        {
            _nodeDrag = default;
            return;
        }

        if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            var drag = _nodeDrag;
            _nodeDrag = default;
            if (drag.Moved)
                CableNodeEdits.CommitMove(session.Undo, cable, node, drag.Before);
            return;
        }

        if (!_nodeDrag.Moved && Vector2.Distance(mouse, _nodeDrag.Press) < ClickSlop())
            return;
        var ray = Camera.RayThrough(mouse - _imageMin, _imageSize);
        if (PlaneHit(ray, _nodeDrag.PlanePoint, _nodeDrag.PlaneNormal) is not { } p)
            return;
        var world = SnapWorld(p + _nodeDrag.Grab);
        CableNodeEdits.SetWorldPosition(session.Scene, entity, cable, node, world);
        _nodeDrag.Moved = true;
    }

    private void CancelNodeDrag()
    {
        if (!_nodeDrag.Active)
            return;
        if (_nodeDrag.Moved && CableNodeEdits.Resolve(_ctx.Session.Scene, _nodeDrag.Node) is var (_, cable, node))
        {
            node.Position = _nodeDrag.Before.Position;
            node.Anchor = _nodeDrag.Before.Anchor;
            cable.NotifyChanged();
        }

        _nodeDrag = default;
    }

    private Vector3 SnapWorld(Vector3 p)
    {
        float step = SnapStep();
        if (step <= 0f)
            return p;
        return new Vector3(MathF.Round(p.X / step) * step, MathF.Round(p.Y / step) * step, MathF.Round(p.Z / step) * step);
    }

    private static Vector3? PlaneHit(Ray ray, Vector3 point, Vector3 normal)
    {
        float denom = Vector3.Dot(ray.Direction, normal);
        if (MathF.Abs(denom) < 1e-6f)
            return null;
        float t = Vector3.Dot(point - ray.Origin, normal) / denom;
        return t > 0f && float.IsFinite(t) ? ray.At(t) : null;
    }

    /// <summary>Draws the nodes of the selected cable as handles (the selected node in the accent colour).</summary>
    private void DrawNodeHandles(ImDrawListPtr drawList, Vector2 mouse, bool hovered)
    {
        if (_ctx.Tools.Tool == EditorTool.Cable || SelectedCable() is not var (entity, cable))
            return;
        var style = ImGui.GetStyle();
        float scale = style.FontScaleMain * style.FontScaleDpi;
        var scene = _ctx.Session.Scene;
        var selected = _ctx.Tools.CableNode;
        Guid? hot = hovered && !_nodeDrag.Active && CableUnder(mouse, includeSegments: false) is { Kind: CableTargetKind.CableNode } h && h.EntityId == entity.Id
            ? h.NodeId
            : null;
        float r = 4.5f * scale;
        foreach (var node in cable.Nodes)
        {
            if (node is null)
                continue;
            var world = CableNodeEdits.WorldPosition(scene, entity, node);
            if (!Camera.WorldToScreen(world, _imageSize, out var s))
                continue;
            s += _imageMin;
            bool isSelected = selected is { } sel && sel.NodeId == node.Id;
            bool isHot = hot == node.Id;
            float size = isHot || isSelected ? r * 1.35f : r;
            uint fill = Theme.U32(isSelected ? Theme.Accent : isHot ? Theme.WithAlpha(Theme.Accent, 0.7f) : Theme.Rgb(0xF4F4F5));
            drawList.AddRectFilled(s - new Vector2(size), s + new Vector2(size), fill, 2f * scale);
            drawList.AddRect(s - new Vector2(size), s + new Vector2(size), Theme.U32(Theme.Rgb(0x16171A)), 2f * scale, ImDrawFlags.None, 1.3f * scale);
        }

        if (!_drag.IsActive && _ctx.Tools.CableNode is null && !_nodeDrag.Active && _shape.Mode == ShapeMode.None)
            DrawToolHint(drawList, scale, _cableLabels.NodeHint);
    }

    /// <summary>
    /// The Move gizmo of the selected cable node (instead of the cable's): drags move the node live; the whole drag is one
    /// undo step. Returns true when the node gizmo is shown.
    /// </summary>
    private bool DrawNodeGizmo(ImDrawListPtr drawList)
    {
        var session = _ctx.Session;
        if (_ctx.Tools.Tool != EditorTool.Move || _ctx.Tools.CableNode is not { } reference ||
            CableNodeEdits.Resolve(session.Scene, reference) is not var (entity, cable, node))
        {
            EndNodeGizmo();
            return false;
        }

        ImGuizmo.SetDrawlist(drawList);
        ImGuizmo.SetRect(_imageMin.X, _imageMin.Y, _imageSize.X, _imageSize.Y);
        ImGuizmo.SetOrthographic(Camera.Projection == ProjectionKind.Orthographic);
        ImGuizmo.SetID(Number);
        var view = Camera.ViewMatrix;
        var projection = Camera.ProjectionMatrix(_imageSize.X / MathF.Max(_imageSize.Y, 1f));
        if (!_nodeDrag.Active)
            _nodeGizmoMatrix = Matrix4x4.CreateTranslation(CableNodeEdits.WorldPosition(session.Scene, entity, node));
        bool snap = _ctx.Tools.Snap ^ ImGui.GetIO().KeyCtrl;
        float grid = session.Scene.Settings.GridSpacing > 0 ? session.Scene.Settings.GridSpacing : 1f;
        var snapValues = new Vector3(grid * 0.5f);
        var matrix = _nodeGizmoMatrix;
        bool changed = ImGuizmo.Manipulate((float*)&view, (float*)&projection, ImGuizmoOperation.Translate, ImGuizmoMode.World, (float*)&matrix, null,
            snap ? (float*)&snapValues : null);
        if (ImGuizmo.IsUsing())
        {
            if (!_nodeDrag.Active)
                _nodeDrag = new NodeDrag { Active = true, Gizmo = true, Node = reference, Before = CableNodeEdits.Capture(node) };
            if (changed)
            {
                _nodeGizmoMatrix = matrix;
                CableNodeEdits.SetWorldPosition(session.Scene, entity, cable, node, matrix.Translation);
                _nodeDrag.Moved = true;
            }
        }
        else
        {
            EndNodeGizmo();
        }

        return true;
    }

    private void EndNodeGizmo()
    {
        if (!_nodeDrag.Active || !_nodeDrag.Gizmo)
            return;
        var drag = _nodeDrag;
        _nodeDrag = default;
        if (drag.Moved && CableNodeEdits.Resolve(_ctx.Session.Scene, drag.Node) is var (_, cable, node))
            CableNodeEdits.CommitMove(_ctx.Session.Undo, cable, node, drag.Before);
    }

    /// <summary>
    /// A selection click on a cable's drawn line selects the cable (the tube is only millimetres thick); returns true when
    /// a cable was hit.
    /// </summary>
    private bool SelectCableUnder(Vector2 mouse, bool shift)
    {
        if (CableUnder(mouse, includeSegments: true) is not { } hit)
            return false;
        var session = _ctx.Session;
        if (session.Scene.Find(hit.EntityId) is not { } cable || !SelectionController.IsSelectable(session.Scene, cable))
            return false;
        SelectionController.Click(session.Scene, session.Selection, cable.Id, shift);
        if (!shift)
            _ctx.Tools.Stage.RequestCableLink(cable.Id);
        return true;
    }

    private struct NodeDrag
    {
        public bool Active;
        public bool Gizmo;
        public bool Moved;
        public CableNodeRef Node;
        public (Vector3 Position, CableAnchor? Anchor) Before;
        public Vector3 PlanePoint;
        public Vector3 PlaneNormal;
        public Vector3 Grab;
        public Vector2 Press;
    }

    /// <summary>Texts of the cable tool, rebuilt when the language or the cable type changes (no per-frame strings).</summary>
    private sealed class CableToolLabels
    {
        private string _language = "";
        private string _type = "";

        public string Hint { get; private set; } = "";
        public string DrawingHint { get; private set; } = "";
        public string Chip { get; private set; } = "";
        public string Branch { get; private set; } = "";
        public string Continue { get; private set; } = "";
        public string Join { get; private set; } = "";
        public string NodeHint { get; private set; } = "";

        public void Refresh(CableTool? tool, CableType? type)
        {
            string id = type?.Id ?? "";
            if (_language == Loc.Language && _type == id && Hint.Length > 0)
                return;
            _language = Loc.Language;
            _type = id;
            string name = type is null ? (tool?.CableTypeId ?? "") : CableLoc.CableName(type);
            Hint = CableLoc.Format(CableStr.ToolCableHint, name);
            DrawingHint = CableLoc.Get(CableStr.ToolCableDrawingHint);
            // Room for the colour swatch drawn over the start of the button.
            Chip = "  " + Icons.Cable + "  " + name + "  " + Icons.ChevronDown + "##cable-type";
            Branch = CableLoc.Get(CableStr.ToolCableBranchHere);
            Continue = CableLoc.Get(CableStr.ToolCableContinueHere);
            Join = CableLoc.Get(CableStr.ToolCableJoinHere);
            NodeHint = CableLoc.Get(CableStr.NodeHint);
        }
    }
}
