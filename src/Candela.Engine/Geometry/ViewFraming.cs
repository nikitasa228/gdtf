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
    public static RenderCamera Frame(Aabb bounds, ViewPreset preset, float fovYDegrees = 45f, float aspect = 16f / 9f, float margin = 1.06f) =>
        Frame(bounds, Direction(preset), fovYDegrees, aspect, margin);

    /// <summary>
    /// A perspective camera looking at the centre of <paramref name="bounds"/> from <paramref name="towardsCamera"/> (unit
    /// vector from the target to the eye), just far enough that the whole box is in the image.
    /// </summary>
    public static RenderCamera Frame(Aabb bounds, Vector3 towardsCamera, float fovYDegrees, float aspect, float margin = 1.06f)
    {
        if (bounds.IsEmpty || !IsFinite(bounds.Min) || !IsFinite(bounds.Max)) bounds = new Aabb(new Vector3(-5f, -5f, 0f), new Vector3(5f, 5f, 3f));
        float fovY = Math.Clamp(float.IsFinite(fovYDegrees) ? fovYDegrees : 45f, 5f, 120f) * MathF.PI / 180f;
        aspect = float.IsFinite(aspect) && aspect > 0.05f ? aspect : 16f / 9f;
        var centre = bounds.Center;
        float length = towardsCamera.Length();
        var forward = length > 1e-6f && float.IsFinite(length) ? -towardsCamera / length : -Direction(ViewPreset.Auto); // camera looks along forward
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
            // the corner is at depth D + z in front of the camera and must satisfy |x| ≤ (D + z)·tan(fov/2)
            distance = MathF.Max(distance, MathF.Abs(x) / tanX - z);
            distance = MathF.Max(distance, MathF.Abs(y) / tanY - z);
            distance = MathF.Max(distance, 0.05f - z);
        }
        distance *= margin;
        var eye = centre - forward * distance;
        var camera = RenderCamera.LookAt(eye, centre, fovYDegrees);
        float extent = bounds.Size.Length();
        camera.Near = MathF.Max(0.05f, Math.Min(distance - extent, distance * 0.05f));
        camera.Far = MathF.Max(100f, distance + 2f * extent);
        return camera;
    }

    /// <summary>
    /// The tightest framing of a set of points (e.g. <see cref="SceneBounds.CollectContentCorners"/>) seen from
    /// <paramref name="towardsCamera"/> (unit vector from the content towards the eye): the camera keeps that viewing
    /// direction and is moved sideways and along it until every point is inside the image with
    /// <paramref name="margin"/> (1 = touching the edges) and the content is centred. A box's corners frame the empty
    /// volume of the box as well; the points of the content fill the picture. No finite point: the default box.
    /// </summary>
    public static RenderCamera Frame(ReadOnlySpan<Vector3> points, Vector3 towardsCamera, float fovYDegrees, float aspect, float margin = 1.06f)
    {
        float fovDegrees = Math.Clamp(float.IsFinite(fovYDegrees) ? fovYDegrees : 45f, 5f, 120f);
        float fovY = fovDegrees * MathF.PI / 180f;
        aspect = float.IsFinite(aspect) && aspect > 0.05f ? aspect : 16f / 9f;
        float m = float.IsFinite(margin) && margin >= 1f ? margin : 1.06f;
        float length = towardsCamera.Length();
        var forward = length > 1e-6f && float.IsFinite(length) ? -towardsCamera / length : -Direction(ViewPreset.Auto);
        var worldUp = MathF.Abs(forward.Z) > 0.999f ? Vector3.UnitY : Vector3.UnitZ;
        var right = Vector3.Normalize(Vector3.Cross(forward, worldUp));
        var up = Vector3.Cross(right, forward);
        float tanY = MathF.Tan(fovY / 2f) / m, tanX = MathF.Tan(fovY / 2f) * aspect / m;

        // In camera axes (x right, y up, z forward) a point is inside when |x − ex| ≤ tan·(z − ez). The four side
        // planes each touch their outermost point: A = max(x − tanX·z), B = max(−x − tanX·z) give the horizontal fit
        // ez = −(A + B) / (2·tanX), ex = (A − B) / 2; likewise vertically. The eye takes the farther of the two.
        float a = float.NegativeInfinity, b = float.NegativeInfinity, c = float.NegativeInfinity, d = float.NegativeInfinity;
        float zMin = float.PositiveInfinity, zMax = float.NegativeInfinity, zSum = 0f;
        int n = 0;
        foreach (var p in points)
        {
            if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z)) continue;
            float x = Vector3.Dot(p, right), y = Vector3.Dot(p, up), z = Vector3.Dot(p, forward);
            a = MathF.Max(a, x - tanX * z);
            b = MathF.Max(b, -x - tanX * z);
            c = MathF.Max(c, y - tanY * z);
            d = MathF.Max(d, -y - tanY * z);
            zMin = MathF.Min(zMin, z);
            zMax = MathF.Max(zMax, z);
            zSum += z;
            n++;
        }
        if (n == 0) return Frame(Aabb.Empty, -forward, fovDegrees, aspect, m);

        float ex = (a - b) / 2f, ey = (c - d) / 2f;
        float ez = MathF.Min(-(a + b) / (2f * tanX), -(c + d) / (2f * tanY));
        // everything in front of the camera, and the eye at least a little away from the content
        float extent = MathF.Max(zMax - zMin, 0.1f);
        ez = MathF.Min(ez, zMin - MathF.Max(0.1f, 0.05f * extent));
        var eye = right * ex + up * ey + forward * ez;
        float focus = MathF.Max(zSum / n - ez, 0.1f);
        var camera = RenderCamera.LookAt(eye, eye + forward * focus, fovDegrees);
        float near = zMin - ez;
        camera.Near = MathF.Max(0.05f, near * 0.5f);
        camera.Far = MathF.Max(100f, (zMax - ez) * 2f);
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
