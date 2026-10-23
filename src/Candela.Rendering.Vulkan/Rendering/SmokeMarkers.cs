using System.Numerics;

namespace Candela.Rendering.Vulkan;

/// <summary>
/// Line geometry of the smoke overlay's emitter markers (<see cref="SmokeEmitterMarker"/>): per emitter a ring around
/// the nozzle (the outlet of a fan: a larger ring), an arrow along the jet direction whose length, line width and
/// brightness grow with the output, and a four-armed arrowhead that reads from any side. A device that is switched off
/// gets a short grey arrow. The segments are written into the view's <see cref="VulkanRenderTarget.EmitterLines"/>
/// (sRGB colours, widths in pixels) and drawn over everything, so emitters inside a device body or behind a wall stay
/// visible. Building allocates no managed memory once the buffer has grown to the emitter count.
/// </summary>
internal static class SmokeMarkers
{
    /// <summary>Segments of the ring around the nozzle.</summary>
    public const int RingSegments = 12;

    /// <summary>Line segments per emitter: ring, shaft, four arrowhead arms.</summary>
    public const int SegmentsPerMarker = RingSegments + 1 + 4;

    /// <summary>Arrow length at output 0 (and of a device that is off), m.</summary>
    public const float MinArrowLength = 0.4f;

    /// <summary>Arrow length at full output, m.</summary>
    public const float MaxArrowLength = 2.4f;

    /// <summary>Radius of the ring around a nozzle, m (fans: <see cref="FanRingRadius"/>).</summary>
    public const float RingRadius = 0.15f;

    /// <summary>Radius of the ring around a fan outlet, m.</summary>
    public const float FanRingRadius = 0.3f;

    /// <summary>Most emitters marked in one view (more are left out; a stage has a handful).</summary>
    public const int MaxMarkers = 1024;

    /// <summary>Colour of a device that is switched off (sRGB, alpha).</summary>
    private static readonly Vector4 OffColor = new(0.62f, 0.62f, 0.62f, 0.85f);

    /// <summary>Line colour of a running emitter of each kind (sRGB): hazers cyan, fog white, low fog pale blue, fans green, flames orange.</summary>
    public static Vector3 ColorOf(SmokeEmitterMarkerKind kind) => kind switch
    {
        SmokeEmitterMarkerKind.Hazer => new Vector3(0.35f, 0.9f, 1f),
        SmokeEmitterMarkerKind.FogMachine => new Vector3(1f, 1f, 1f),
        SmokeEmitterMarkerKind.LowFog => new Vector3(0.6f, 0.72f, 1f),
        SmokeEmitterMarkerKind.Fan => new Vector3(0.45f, 1f, 0.55f),
        SmokeEmitterMarkerKind.Flame => new Vector3(1f, 0.55f, 0.12f),
        _ => new Vector3(1f, 1f, 1f),
    };

    /// <summary>Arrow length for an output level, m (non-finite levels count as off).</summary>
    public static float ArrowLength(float output) =>
        MinArrowLength + (MaxArrowLength - MinArrowLength) * (float.IsFinite(output) ? Math.Clamp(output, 0f, 1f) : 0f);

    /// <summary>Writes the markers of <paramref name="markers"/> into <paramref name="lines"/> (empty when <paramref name="active"/> is false).</summary>
    public static void Build(IReadOnlyList<SmokeEmitterMarker> markers, SceneBuffer<LineVertex> lines, bool active)
    {
        int count = active ? Math.Min(markers.Count, MaxMarkers) : 0;
        int valid = 0;
        for (int i = 0; i < count; i++)
        {
            if (IsFinite(markers[i].Position))
                valid++;
        }

        lines.Resize(valid * SegmentsPerMarker * 2);
        int v = 0;
        for (int i = 0; i < count; i++)
        {
            var m = markers[i];
            if (IsFinite(m.Position))
                v = Write(m, lines, v);
        }

        if (v > 0)
            lines.MarkDirty(0, v);
    }

    private static int Write(in SmokeEmitterMarker m, SceneBuffer<LineVertex> lines, int v)
    {
        var axis = m.Direction;
        float length = axis.Length();
        axis = float.IsFinite(length) && length > 1e-6f ? axis / length : Vector3.UnitZ;
        float output = float.IsFinite(m.Output) ? Math.Clamp(m.Output, 0f, 1f) : 0f;
        bool on = output > 0f;
        var rgb = ColorOf(m.Kind);
        // Brighter and wider with the output; a device that is off is grey and thin.
        var color = on ? new Vector4(Vector3.Lerp(rgb * 0.7f, rgb, output), 1f) : OffColor;
        float width = on ? 2f + 2.5f * output : 1.5f;

        // Two directions across the axis (any pair: the ring and the arrowhead look the same from every side).
        var helper = MathF.Abs(axis.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitX;
        var u = Vector3.Normalize(Vector3.Cross(axis, helper));
        var w = Vector3.Cross(axis, u);

        float radius = m.Kind == SmokeEmitterMarkerKind.Fan ? FanRingRadius : RingRadius;
        for (int s = 0; s < RingSegments; s++)
        {
            float a0 = 2f * MathF.PI * s / RingSegments, a1 = 2f * MathF.PI * (s + 1) / RingSegments;
            lines[v++] = Vertex(m.Position + (u * MathF.Cos(a0) + w * MathF.Sin(a0)) * radius, color, width);
            lines[v++] = Vertex(m.Position + (u * MathF.Cos(a1) + w * MathF.Sin(a1)) * radius, color, width);
        }

        float arrow = ArrowLength(output);
        var tip = m.Position + axis * arrow;
        lines[v++] = Vertex(m.Position, color, width);
        lines[v++] = Vertex(tip, color, width);
        float head = MathF.Min(0.3f, 0.35f * arrow);
        var back = tip - axis * head;
        Span<Vector3> arms = stackalloc Vector3[] { u, -u, w, -w };
        foreach (var arm in arms)
        {
            lines[v++] = Vertex(tip, color, width);
            lines[v++] = Vertex(back + arm * (0.5f * head), color, width);
        }

        return v;
    }

    private static LineVertex Vertex(Vector3 position, Vector4 color, float width) => new() { Position = position, Color = color, Width = width };

    private static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
