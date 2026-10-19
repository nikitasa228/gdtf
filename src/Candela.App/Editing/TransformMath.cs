using System.Numerics;
using Candela.Core.Math;
using Candela.Core.SceneGraph;

namespace Candela.App.Editing;

/// <summary>
/// Transform helpers of the gizmo and the properties panel: applying a gizmo drag of the primary entity to the whole
/// selection (around the primary pivot), local transforms from world matrices, grid / angle snapping and Euler angles
/// in degrees for numeric entry. Row-vector convention of System.Numerics (<c>world = local · parentWorld</c>).
/// </summary>
internal static class TransformMath
{
    /// <summary>
    /// The world-space change from the pivot's start matrix to its current matrix: <c>Δ = start⁻¹ · current</c>, so that
    /// <c>start · Δ = current</c> and any other world matrix <c>W</c> moves to <c>W · Δ</c> (rigidly with the pivot).
    /// </summary>
    public static Matrix4x4 Delta(Matrix4x4 pivotStart, Matrix4x4 pivotNow)
    {
        if (!Matrix4x4.Invert(pivotStart, out var inverse))
            return Matrix4x4.Identity;
        return inverse * pivotNow;
    }

    /// <summary>
    /// New local transforms for <paramref name="entities"/> whose world matrices were <paramref name="startWorld"/> when
    /// the drag began, after the pivot moved by <paramref name="delta"/> (see <see cref="Delta"/>). The scale of each
    /// entity is preserved exactly (rotation/translation-only gizmos never shear or rescale through decomposition noise).
    /// </summary>
    public static Transform[] ApplyDelta(IReadOnlyList<Entity> entities, IReadOnlyList<Matrix4x4> startWorld, IReadOnlyList<Transform> startLocal,
        Matrix4x4 delta)
    {
        ArgumentNullException.ThrowIfNull(entities);
        var result = new Transform[entities.Count];
        for (int i = 0; i < entities.Count; i++)
        {
            var world = startWorld[i] * delta;
            var parentWorld = entities[i].Parent?.WorldMatrix ?? Matrix4x4.Identity;
            var local = LocalFromWorld(world, parentWorld);
            local.Scale = startLocal[i].Scale;
            result[i] = local;
        }

        return result;
    }

    /// <summary>Local transform that gives <paramref name="world"/> under a parent with world matrix <paramref name="parentWorld"/>.</summary>
    public static Transform LocalFromWorld(Matrix4x4 world, Matrix4x4 parentWorld)
    {
        if (Matrix4x4.Invert(parentWorld, out var inverseParent))
            world *= inverseParent;
        return Transform.FromMatrix(world);
    }

    /// <summary>Rounds each component to the nearest multiple of <paramref name="step"/> (m); step ≤ 0 = unchanged.</summary>
    public static Vector3 SnapPosition(Vector3 position, float step)
    {
        if (!(step > 0f) || !float.IsFinite(step))
            return position;
        return new Vector3(MathF.Round(position.X / step) * step, MathF.Round(position.Y / step) * step, MathF.Round(position.Z / step) * step);
    }

    /// <summary>Rounds an angle (degrees) to the nearest multiple of <paramref name="stepDegrees"/>.</summary>
    public static float SnapAngle(float degrees, float stepDegrees)
    {
        if (!(stepDegrees > 0f) || !float.IsFinite(stepDegrees))
            return degrees;
        return MathF.Round(degrees / stepDegrees) * stepDegrees;
    }

    /// <summary>
    /// Euler angles in degrees (X, Y, Z applied in that order about the fixed world axes, i.e. <c>q = qZ · qY · qX</c>)
    /// of a rotation, for the properties panel. Near gimbal lock Z absorbs the remaining rotation.
    /// </summary>
    public static Vector3 ToEulerDegrees(Quaternion q)
    {
        q = Quaternion.Normalize(q);
        var m = Matrix4x4.CreateFromQuaternion(q);
        // Row-vector matrix of R = Rx·Ry·Rz (applied X first): M13 = −sin(y) in this convention.
        float sy = -m.M13;
        float x, y, z;
        if (MathF.Abs(sy) < 0.99999f)
        {
            y = MathF.Asin(Math.Clamp(sy, -1f, 1f));
            x = MathF.Atan2(m.M23, m.M33);
            z = MathF.Atan2(m.M12, m.M11);
        }
        else
        {
            y = sy > 0 ? MathF.PI / 2f : -MathF.PI / 2f;
            x = 0f;
            z = MathF.Atan2(-m.M21, m.M22);
        }

        const float deg = 180f / MathF.PI;
        return new Vector3(Clean(x * deg), Clean(y * deg), Clean(z * deg));
    }

    /// <summary>Inverse of <see cref="ToEulerDegrees"/>.</summary>
    public static Quaternion FromEulerDegrees(Vector3 degrees)
    {
        const float rad = MathF.PI / 180f;
        var qx = Quaternion.CreateFromAxisAngle(Vector3.UnitX, degrees.X * rad);
        var qy = Quaternion.CreateFromAxisAngle(Vector3.UnitY, degrees.Y * rad);
        var qz = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, degrees.Z * rad);
        // Composed as row-vector matrices (X applied first, then Y, then Z) so the order is unambiguous.
        var m = Matrix4x4.CreateFromQuaternion(qx) * Matrix4x4.CreateFromQuaternion(qy) * Matrix4x4.CreateFromQuaternion(qz);
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(m));
    }

    private static float Clean(float v)
    {
        float r = MathF.Round(v, 4);
        return r == 0f ? 0f : r; // no "-0"
    }
}
