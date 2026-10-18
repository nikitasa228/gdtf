using System.Numerics;
using Candela.Core.Math;
using Candela.Rendering;

namespace Candela.Engine;

/// <summary>Standard camera directions for framing a scene.</summary>
public enum ViewPreset
{
    /// <summary>Three-quarter view from the audience side (front left, elevated), the default for overviews.</summary>
    Auto,

    /// <summary>From the audience (−Y) looking upstage (+Y), slightly elevated.</summary>
    Front,

    /// <summary>Straight down (plan view).</summary>
    Top,

    /// <summary>From stage right (+X) looking across the stage (−X), slightly elevated.</summary>
    Side,
}

/// <summary>Cameras that frame a box (the scene's bounds) from a preset direction, for "frame all", the CLI and thumbnails.</summary>
public static class ViewFraming
{
    /// <summary>
    /// A perspective camera looking at the centre of <paramref name="bounds"/> from <paramref name="preset"/>'s direction,
    /// just far enough that all eight corners of the box are inside the image (with a small margin) for the given vertical
    /// field of view and aspect ratio (width / height).
    /// </summary>
    public static RenderCamera Frame(Aabb bounds, ViewPreset preset, float fovYDegrees = 45f, float aspect = 16f / 9f, float margin = 1.06f)
    {
        if (bounds.IsEmpty || !IsFinite(bounds.Min) || !IsFinite(bounds.Max)) bounds = new Aabb(new Vector3(-5f, -5f, 0f), new Vector3(5f, 5f, 3f));
        float fovY = Math.Clamp(float.IsFinite(fovYDegrees) ? fovYDegrees : 45f, 5f, 120f) * MathF.PI / 180f;
        aspect = float.IsFinite(aspect) && aspect > 0.05f ? aspect : 16f / 9f;
        var centre = bounds.Center;
        var forward = -Direction(preset); // camera looks along forward
        var worldUp = MathF.Abs(forward.Z) > 0.999f ? Vector3.UnitY : Vector3.UnitZ;
        var right = Vector3.Normalize(Vector3.Cross(forward, worldUp));
        var up = Vector3.Cross(right, forward);
        float tanY = MathF.Tan(fovY / 2f), tanX = tanY * aspect;

        float distance = 0.1f;
        for (int i = 0; i < 8; i++)
        {
            var corner = new Vector3((i & 1) == 0 ? bounds.Min.X : bounds.Max.X, (i & 2) == 0 ? bounds.Min.Y : bounds.Max.Y, (i & 4) == 0 ? bounds.Min.Z : bounds.Max.Z);
            var c = corner - centre;
            float x = Vector3.Dot(c, right), y = Vector3.Dot(c, up), z = Vector3.Dot(c, forward);
            distance = MathF.Max(distance, z + MathF.Abs(x) / tanX);
            distance = MathF.Max(distance, z + MathF.Abs(y) / tanY);
        }
        distance *= margin;
        var eye = centre - forward * distance;
        var camera = RenderCamera.LookAt(eye, centre, fovYDegrees);
        float extent = bounds.Size.Length();
        camera.Near = MathF.Max(0.05f, Math.Min(distance - extent, distance * 0.05f));
        camera.Far = MathF.Max(100f, distance + 2f * extent);
        return camera;
    }

    /// <summary>Unit vector from the target towards the camera for a preset.</summary>
    public static Vector3 Direction(ViewPreset preset) => preset switch
    {
        ViewPreset.Front => Vector3.Normalize(new Vector3(0f, -1f, 0.14f)),
        ViewPreset.Top => Vector3.UnitZ,
        ViewPreset.Side => Vector3.Normalize(new Vector3(1f, 0f, 0.14f)),
        _ => Vector3.Normalize(new Vector3(-0.42f, -1f, 0.55f)),
    };

    private static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
