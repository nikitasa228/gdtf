using System.Numerics;
using Candela.Core.Math;
using Candela.Core.SceneGraph.Components;

namespace Candela.App.Editing;

/// <summary>
/// A resize handle of a wall, floor or ceiling: which sides of its box it moves (−1 the minimum side, +1 the maximum
/// side, 0 not along that axis), in the entity's local frame (see <see cref="WallKind"/>: walls span X centred and Z
/// upwards from the origin; floors and ceilings span X and Y centred).
/// </summary>
/// <param name="X">Side moved along local X.</param>
/// <param name="Y">Side moved along local Y (floors, ceilings).</param>
/// <param name="Z">1 when the top of a wall moves.</param>
internal readonly record struct ShapeHandle(int X, int Y, int Z)
{
    /// <summary>True for a corner of a floor (moves along X and Y at once).</summary>
    public bool IsCorner => X != 0 && Y != 0;
}

/// <summary>
/// The geometry behind the Wall and Floor tools (requirement: walls and floors drawn as resizable rectangles): a
/// rectangle dragged on a horizontal plane becomes a floor, a line dragged on it becomes a wall standing on it (its
/// front facing the camera), and the handles of a selected wall or floor change its size while the opposite side stays
/// where it is. Pure math in metres, tested without a window.
/// </summary>
internal static class WallShapes
{
    /// <summary>Height of a new wall, m.</summary>
    public const float DefaultWallHeight = 3f;

    /// <summary>Thickness of a new wall, m.</summary>
    public const float DefaultWallThickness = 0.1f;

    /// <summary>Thickness of a new floor, m.</summary>
    public const float DefaultFloorThickness = 0.05f;

    /// <summary>Smallest side a drawn or resized shape keeps, m.</summary>
    public const float MinimumSize = 0.05f;

    private static readonly ShapeHandle[] WallHandles = { new(-1, 0, 0), new(1, 0, 0), new(0, 0, 1) };

    private static readonly ShapeHandle[] FloorHandles =
    {
        new(-1, 0, 0), new(1, 0, 0), new(0, -1, 0), new(0, 1, 0),
        new(-1, -1, 0), new(1, -1, 0), new(-1, 1, 0), new(1, 1, 0),
    };

    /// <summary>The built-in material a new shape of <paramref name="kind"/> gets.</summary>
    public static string DefaultMaterial(WallKind kind) => kind switch
    {
        WallKind.Floor => MaterialIds.WoodStage,
        _ => MaterialIds.PaintedWallWhite,
    };

    /// <summary>The handles of a shape of <paramref name="kind"/>.</summary>
    public static IReadOnlyList<ShapeHandle> HandlesOf(WallKind kind) => kind == WallKind.Wall ? WallHandles : FloorHandles;

    /// <summary>Position of a handle in the entity's local frame (walls: ends at mid-height, top in the middle).</summary>
    public static Vector3 HandlePosition(WallComponent wall, ShapeHandle handle)
    {
        ArgumentNullException.ThrowIfNull(wall);
        float w = Size(wall.Width), h = Size(wall.Height);
        if (wall.Kind == WallKind.Wall)
            return new Vector3(handle.X * w * 0.5f, 0f, handle.Z != 0 ? h : h * 0.5f);
        return new Vector3(handle.X * w * 0.5f, handle.Y * h * 0.5f, 0f);
    }

    /// <summary>The local direction a single-axis handle moves along (zero for a corner, which moves in its plane).</summary>
    public static Vector3 HandleAxis(ShapeHandle handle) =>
        handle.IsCorner ? Vector3.Zero : handle.X != 0 ? Vector3.UnitX : handle.Y != 0 ? Vector3.UnitY : Vector3.UnitZ;

    /// <summary>
    /// The new size of <paramref name="wall"/> when <paramref name="handle"/> is dragged to <paramref name="target"/>
    /// (local frame), and how far the entity origin moves (local frame) so the opposite side stays in place. Sizes are
    /// rounded to <paramref name="snap"/> metres when it is positive and never fall below <see cref="MinimumSize"/>.
    /// </summary>
    public static (float Width, float Height, Vector3 OriginShift) Resize(WallComponent wall, ShapeHandle handle, Vector3 target, float snap = 0f)
    {
        ArgumentNullException.ThrowIfNull(wall);
        float w = Size(wall.Width), h = Size(wall.Height);
        if (!Finite(target))
            return (w, h, Vector3.Zero);
        var shift = Vector3.Zero;
        if (handle.X != 0)
        {
            // The opposite side (at −X·w/2) stays; the moved side follows the target.
            float fixedSide = -handle.X * w * 0.5f;
            float size = Snap(handle.X * (target.X - fixedSide), snap);
            shift.X = fixedSide + handle.X * size * 0.5f;
            w = size;
        }

        if (wall.Kind == WallKind.Wall)
        {
            if (handle.Z != 0)
                h = Snap(target.Z, snap);
        }
        else if (handle.Y != 0)
        {
            float fixedSide = -handle.Y * h * 0.5f;
            float size = Snap(handle.Y * (target.Y - fixedSide), snap);
            shift.Y = fixedSide + handle.Y * size * 0.5f;
            h = size;
        }

        return (w, h, shift);
    }

    /// <summary>
    /// The local transform after moving the origin by <paramref name="localShift"/> (in the entity's own frame, scaled
    /// and rotated with it).
    /// </summary>
    public static Transform ShiftOrigin(Transform local, Vector3 localShift)
    {
        var moved = local;
        moved.Position += Vector3.Transform(localShift * local.Scale, local.Rotation);
        return moved;
    }

    /// <summary>
    /// A floor spanning the rectangle between two corners on a horizontal plane (their mean height): placement (centre,
    /// axis-aligned) and size (width along X, length along Y). Null when a side is shorter than
    /// <see cref="MinimumSize"/>.
    /// </summary>
    public static (Transform Placement, float Width, float Length)? FloorFromCorners(Vector3 a, Vector3 b)
    {
        if (!Finite(a) || !Finite(b))
            return null;
        float width = MathF.Abs(b.X - a.X), length = MathF.Abs(b.Y - a.Y);
        if (width < MinimumSize || length < MinimumSize)
            return null;
        var centre = new Vector3((a.X + b.X) * 0.5f, (a.Y + b.Y) * 0.5f, (a.Z + b.Z) * 0.5f);
        return (new Transform(centre), width, length);
    }

    /// <summary>
    /// A wall standing on the line from <paramref name="a"/> to <paramref name="b"/> (its base at their mean height),
    /// turned so its front face looks towards <paramref name="eye"/>. Null when the line is shorter than
    /// <see cref="MinimumSize"/>.
    /// </summary>
    public static (Transform Placement, float Width)? WallFromLine(Vector3 a, Vector3 b, Vector3 eye)
    {
        if (!Finite(a) || !Finite(b))
            return null;
        var d = new Vector2(b.X - a.X, b.Y - a.Y);
        float width = d.Length();
        if (width < MinimumSize)
            return null;
        float yaw = MathF.Atan2(d.Y, d.X);
        var centre = new Vector3((a.X + b.X) * 0.5f, (a.Y + b.Y) * 0.5f, (a.Z + b.Z) * 0.5f);
        var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, yaw);
        // The front face (local −Y) faces the viewer who drew it.
        var front = Vector3.Transform(-Vector3.UnitY, rotation);
        if (Finite(eye) && Vector3.Dot(front, eye - centre) < 0f)
            rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, yaw + MathF.PI);
        return (new Transform(centre, Quaternion.Normalize(rotation), Vector3.One), width);
    }

    /// <summary>
    /// The point of the line through <paramref name="origin"/> along <paramref name="axis"/> closest to a ray; null when
    /// they are (nearly) parallel.
    /// </summary>
    public static Vector3? ClosestOnAxis(Vector3 origin, Vector3 axis, Ray ray)
    {
        if (axis.LengthSquared() < 1e-12f || ray.Direction.LengthSquared() < 1e-12f)
            return null;
        // Lines origin + u·s and ray.Origin + v·t: s of the closest pair (u is a unit vector, so u·u = 1).
        var u = Vector3.Normalize(axis);
        var v = ray.Direction;
        var w0 = origin - ray.Origin;
        float b = Vector3.Dot(u, v), c = Vector3.Dot(v, v), d = Vector3.Dot(u, w0), e = Vector3.Dot(v, w0);
        float denom = c - b * b;
        if (MathF.Abs(denom) < 1e-8f * c)
            return null;
        float s = (b * e - c * d) / denom;
        var point = origin + u * s;
        return Finite(point) ? point : null;
    }

    /// <summary>Where a ray meets the horizontal plane z = <paramref name="height"/> (ahead of its origin), or null.</summary>
    public static Vector3? OnPlaneZ(Ray ray, float height)
    {
        if (MathF.Abs(ray.Direction.Z) < 1e-7f)
            return null;
        float t = (height - ray.Origin.Z) / ray.Direction.Z;
        if (!(t > 0f) || !float.IsFinite(t))
            return null;
        var p = ray.At(t);
        return Finite(p) ? p with { Z = height } : null;
    }

    /// <summary><paramref name="value"/> rounded to a multiple of <paramref name="step"/> (when positive) and at least <see cref="MinimumSize"/>.</summary>
    public static float Snap(float value, float step)
    {
        if (!float.IsFinite(value))
            return MinimumSize;
        if (step > 0f && float.IsFinite(step))
            value = MathF.Round(value / step) * step;
        return MathF.Max(MinimumSize, value);
    }

    /// <summary>A point with X and Y rounded to the grid (Z kept).</summary>
    public static Vector3 SnapPoint(Vector3 p, float step) =>
        step > 0f && float.IsFinite(step) ? new Vector3(MathF.Round(p.X / step) * step, MathF.Round(p.Y / step) * step, p.Z) : p;

    private static float Size(float v) => float.IsFinite(v) && v > 0f ? v : MinimumSize;

    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
