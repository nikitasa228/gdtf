using System.Numerics;
using Candela.Core.Math;

namespace Candela.App.Editing;

/// <summary>What was under the cursor when something was dropped or placed.</summary>
internal enum SurfaceKind
{
    /// <summary>Drawn geometry (renderer pick: floor, wall, truss, model, fixture).</summary>
    Geometry,

    /// <summary>Nothing drawn there: the ground plane (z = 0).</summary>
    Ground,

    /// <summary>Neither (looking at the sky): the plane through the camera's target, facing the camera.</summary>
    ViewPlane,
}

/// <summary>A point on a surface under the cursor (world metres) with the surface normal (unit, towards the viewer).</summary>
/// <param name="Position">World position, m.</param>
/// <param name="Normal">Unit surface normal.</param>
/// <param name="Kind">Where the point came from.</param>
/// <param name="PickId">Renderer pick id of the geometry hit (0 = none).</param>
internal readonly record struct SurfaceHit(Vector3 Position, Vector3 Normal, SurfaceKind Kind, uint PickId = 0)
{
    /// <summary>A point on the ground plane (normal +Z).</summary>
    public static SurfaceHit Ground(Vector3 position) => new(position with { Z = 0f }, Vector3.UnitZ, SurfaceKind.Ground);
}

/// <summary>
/// How things dropped into a viewport are oriented on the surface under the cursor. GDTF fixtures hang by default
/// (base up, beam along local −Z); dropped on a surface they mount with the base against it and the beam pointing away
/// from it: standing on a floor, hanging under a ceiling or truss, sideways on a wall. Models keep their own Z-up
/// orientation and stand on the point. Images lie on the surface (facing out of it), lifted by
/// <see cref="ImageLift"/> so they do not flicker with it.
/// </summary>
internal static class Placement
{
    /// <summary>Distance of an image above the surface it is placed on, m.</summary>
    public const float ImageLift = 0.002f;

    /// <summary>Normals more horizontal than this (|z| below it) count as walls; others as floors or ceilings.</summary>
    public const float WallThreshold = 0.5f;

    /// <summary>World transform of a fixture mounted on the surface.</summary>
    public static Transform ForFixture(SurfaceHit hit)
    {
        var n = SafeNormal(hit.Normal);
        // Base against the surface: local +Z (towards the base) points into the surface.
        return new Transform(hit.Position, FromTo(Vector3.UnitZ, -n), Vector3.One);
    }

    /// <summary>World transform of a 3D model placed on the surface (upright, the model's origin on the point).</summary>
    public static Transform ForModel(SurfaceHit hit) => new(hit.Position, Quaternion.Identity, Vector3.One);

    /// <summary>World transform of an image (its face, local −Y, looking out of the surface).</summary>
    public static Transform ForImage(SurfaceHit hit)
    {
        var n = SafeNormal(hit.Normal);
        return new Transform(hit.Position + n * ImageLift, FromTo(-Vector3.UnitY, n), Vector3.One);
    }

    /// <summary>
    /// The shortest rotation turning unit vector <paramref name="from"/> into <paramref name="to"/>; opposite vectors turn
    /// half way round the X axis (or Y when they lie along X), so a fixture turned over keeps its front-back axis.
    /// </summary>
    public static Quaternion FromTo(Vector3 from, Vector3 to)
    {
        from = SafeNormal(from);
        to = SafeNormal(to);
        float d = Vector3.Dot(from, to);
        if (d > 1f - 1e-6f)
            return Quaternion.Identity;
        if (d < -1f + 1e-6f)
        {
            // The axis perpendicular to `from` closest to X (Y when `from` lies along X).
            var axis = Vector3.UnitX - from * from.X;
            if (axis.LengthSquared() < 1e-4f)
                axis = Vector3.UnitY - from * from.Y;
            return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.PI);
        }

        var c = Vector3.Cross(from, to);
        var q = new Quaternion(c, 1f + d);
        return Quaternion.Normalize(q);
    }

    /// <summary>The ground point under a ray, or null when the ray runs parallel to or away from the ground.</summary>
    public static Vector3? GroundUnder(Vector3 origin, Vector3 direction, float height = 0f)
    {
        if (!Finite(origin) || !Finite(direction) || MathF.Abs(direction.Z) < 1e-6f)
            return null;
        float t = (height - origin.Z) / direction.Z;
        return t > 0f && float.IsFinite(t) ? origin + direction * t : null;
    }

    private static Vector3 SafeNormal(Vector3 v)
    {
        float len = v.Length();
        return float.IsFinite(len) && len > 1e-6f ? v / len : Vector3.UnitZ;
    }

    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
