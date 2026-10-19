using System.Numerics;
using Candela.App.Editing;
using Candela.App.Panels;
using Candela.App.Ui;
using Candela.Core.Math;
using Hexa.NET.ImGui;

namespace Candela.App.Viewport;

/// <summary>Library items dragged into the viewport: drop target, surface under the cursor and the ghost preview.</summary>
internal sealed partial class ViewportPanel
{
    private SurfaceHit? _dropHit;
    private Vector2 _dropHitMouse = new(float.NaN);
    private readonly Vector2[] _ghostCorners = new Vector2[8];
    private readonly bool[] _ghostVisible = new bool[8];
    private readonly Vector2[] _ring = new Vector2[33];

    /// <summary>The surface point the dragged item would land on (last frame), or null when nothing is dragged over this view.</summary>
    internal SurfaceHit? DropHit => _dropHit;

    /// <summary>
    /// While a library item is dragged over the image: accepts the drop, finds the surface under the cursor (renderer
    /// pick of the last frame, ground plane fallback) and draws where the item will go; a release places it.
    /// </summary>
    private void HandleLibraryDrop(ImDrawListPtr drawList)
    {
        var item = _host.DraggedLibraryItem;
        if (item is null || _imageSize.X <= 0)
        {
            _dropHit = null;
            _dropHitMouse = new Vector2(float.NaN);
            return;
        }

        var rect = new ImRect(_imageMin, _imageMin + _imageSize);
        uint id = ImGui.GetID("##viewport-drop");
        if (!ImGuiP.BeginDragDropTargetCustom(rect, id))
        {
            _dropHit = null;
            return;
        }

        var payload = ImGui.AcceptDragDropPayload(LibraryPanel.PayloadType, ImGuiDragDropFlags.AcceptBeforeDelivery | ImGuiDragDropFlags.AcceptNoDrawDefaultRect);
        if (!payload.IsNull)
        {
            var mouse = ImGui.GetIO().MousePos;
            // One read of the pick buffer per mouse position (the read waits for the last frame on the GPU).
            if (_dropHit is null || Vector2.DistanceSquared(mouse, _dropHitMouse) > 0.25f)
            {
                _dropHit = SurfaceAt(mouse, _ctx.Gpu.Renderer);
                _dropHitMouse = mouse;
            }

            var hit = _dropHit.Value;
            DrawGhost(drawList, item, hit, mouse);
            if (payload.IsDelivery())
            {
                _host.SetActiveViewport(this);
                _host.PlaceLibraryItem(item, hit, mouse);
                _dropHit = null;
            }
        }

        ImGui.EndDragDropTarget();
    }

    private void DrawGhost(ImDrawListPtr drawList, LibraryItem item, SurfaceHit hit, Vector2 mouse)
    {
        var style = ImGui.GetStyle();
        float scale = style.FontScaleMain * style.FontScaleDpi;
        var accent = Theme.Accent;
        Transform placement;
        Aabb box;
        if (item.Fixture is { } entry)
        {
            placement = Placement.ForFixture(hit);
            box = _host.FixtureInfoOf(entry)?.RestBounds ?? FixtureInfoCache.DefaultBounds;
        }
        else if (item.Model is { Kind: ModelItemKind.Image })
        {
            placement = Placement.ForImage(hit);
            // An image lands as a 4 m wide sheet; its height follows the picture, shown here as 4:3.
            box = new Aabb(new Vector3(-2f, -0.001f, -1.5f), new Vector3(2f, 0.001f, 1.5f));
        }
        else
        {
            placement = Placement.ForModel(hit);
            box = new Aabb(new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, 0.5f, 1f));
        }

        // Footprint ring on the surface, then the item's box.
        var n = hit.Normal;
        var t1 = Vector3.Normalize(MathF.Abs(n.Z) < 0.9f ? Vector3.Cross(n, Vector3.UnitZ) : Vector3.Cross(n, Vector3.UnitX));
        var t2 = Vector3.Cross(n, t1);
        float radius = MathF.Max(0.15f, 0.5f * MathF.Max(box.Size.X, box.Size.Y));
        int ringCount = 0;
        for (int i = 0; i < _ring.Length; i++)
        {
            float a = i * MathF.Tau / (_ring.Length - 1);
            var p = hit.Position + (t1 * MathF.Cos(a) + t2 * MathF.Sin(a)) * radius + n * 0.005f;
            if (!Camera.WorldToScreen(p, _imageSize, out var s))
            {
                ringCount = 0;
                break;
            }

            _ring[ringCount++] = s + _imageMin;
        }

        if (ringCount > 2)
        {
            for (int i = 0; i + 1 < ringCount; i++)
                drawList.AddLine(_ring[i], _ring[i + 1], Theme.U32(Theme.WithAlpha(accent, 0.55f)), 1.5f * scale);
        }

        var matrix = placement.ToMatrix();
        for (int i = 0; i < 8; i++)
        {
            var c = new Vector3((i & 1) == 0 ? box.Min.X : box.Max.X, (i & 2) == 0 ? box.Min.Y : box.Max.Y, (i & 4) == 0 ? box.Min.Z : box.Max.Z);
            _ghostVisible[i] = Camera.WorldToScreen(Vector3.Transform(c, matrix), _imageSize, out var s);
            _ghostCorners[i] = s + _imageMin;
        }

        uint edge = Theme.U32(Theme.WithAlpha(accent, 0.95f));
        for (int i = 0; i < 8; i++)
        {
            for (int bit = 1; bit <= 4; bit <<= 1)
            {
                int j = i | bit;
                if (j == i || !_ghostVisible[i] || !_ghostVisible[j])
                    continue;
                drawList.AddLine(_ghostCorners[i], _ghostCorners[j], edge, 2f * scale);
            }
        }

        // The point it lands on, and what lands there.
        if (Camera.WorldToScreen(hit.Position, _imageSize, out var point))
            drawList.AddCircleFilled(point + _imageMin, 4f * scale, Theme.U32(accent));
        string label = _host.DropLabel(item);
        var size = ImGui.CalcTextSize(label);
        // Above the cursor: the drag tooltip of the library sits below it.
        var pos = mouse + new Vector2(16f * scale, -size.Y - 14f * scale);
        drawList.AddRectFilled(pos - new Vector2(6f, 3f) * scale, pos + size + new Vector2(6f, 3f) * scale, Theme.U32(Theme.WithAlpha(Theme.OverlayBackground, 0.9f)),
            4f * scale);
        drawList.AddText(pos, Theme.U32(Theme.Text), label);
    }
}
