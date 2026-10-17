using System.Numerics;
using Candela.Core.Math;

namespace Candela.Simulation;

/// <summary>
/// A solid oriented box that blocks air and smoke: a wall, floor, truss section, riser, fixture body… The engine
/// approximates scene geometry with these boxes and hands them to <see cref="SmokeSimulation.SetObstacles"/>.
/// </summary>
/// <param name="Center">World-space centre, m.</param>
/// <param name="HalfExtents">Half the edge lengths along the box's local X, Y, Z axes, m.</param>
/// <param name="Rotation">Rotation from the box's local axes to world axes.</param>
public readonly record struct ObstacleBox(Vector3 Center, Vector3 HalfExtents, Quaternion Rotation)
{
    /// <summary>An axis-aligned box from its world-space corners.</summary>
    public static ObstacleBox FromAabb(Aabb box) => new(box.Center, box.Size * 0.5f, Quaternion.Identity);

    /// <summary>
    /// The box <paramref name="localMin"/>–<paramref name="localMax"/> of an entity's local space placed by its world
    /// matrix (row-vector convention, <c>v' = v · M</c>). Scale and rotation are taken from the matrix; shear is ignored.
    /// </summary>
    public static ObstacleBox FromLocalBox(Vector3 localMin, Vector3 localMax, Matrix4x4 world)
    {
        var center = Vector3.Transform((localMin + localMax) * 0.5f, world);
        var half = Vector3.Abs(localMax - localMin) * 0.5f;
        var ax = new Vector3(world.M11, world.M12, world.M13);
        var ay = new Vector3(world.M21, world.M22, world.M23);
        var az = new Vector3(world.M31, world.M32, world.M33);
        var scale = new Vector3(ax.Length(), ay.Length(), az.Length());
        var rotation = Quaternion.Identity;
        if (scale.X > 1e-12f && scale.Y > 1e-12f && scale.Z > 1e-12f)
        {
            var r = new Matrix4x4(
                ax.X / scale.X, ax.Y / scale.X, ax.Z / scale.X, 0f,
                ay.X / scale.Y, ay.Y / scale.Y, ay.Z / scale.Y, 0f,
                az.X / scale.Z, az.Y / scale.Z, az.Z / scale.Z, 0f,
                0f, 0f, 0f, 1f);
            rotation = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(r));
        }

        return new ObstacleBox(center, half * scale, rotation);
    }

    /// <summary>True when every component is finite and the box has a non-negative size.</summary>
    public bool IsValid =>
        float.IsFinite(Center.X) && float.IsFinite(Center.Y) && float.IsFinite(Center.Z)
        && HalfExtents.X >= 0f && HalfExtents.Y >= 0f && HalfExtents.Z >= 0f
        && float.IsFinite(HalfExtents.X) && float.IsFinite(HalfExtents.Y) && float.IsFinite(HalfExtents.Z)
        && float.IsFinite(Rotation.X) && float.IsFinite(Rotation.Y) && float.IsFinite(Rotation.Z) && float.IsFinite(Rotation.W)
        && Rotation.LengthSquared() > 1e-12f;

    /// <summary>World-space bounding box of the oriented box.</summary>
    public Aabb WorldBounds()
    {
        var r = Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(Rotation));
        // Extent along each world axis = Σ |R_ij| · h_j.
        var e = new Vector3(
            MathF.Abs(r.M11) * HalfExtents.X + MathF.Abs(r.M21) * HalfExtents.Y + MathF.Abs(r.M31) * HalfExtents.Z,
            MathF.Abs(r.M12) * HalfExtents.X + MathF.Abs(r.M22) * HalfExtents.Y + MathF.Abs(r.M32) * HalfExtents.Z,
            MathF.Abs(r.M13) * HalfExtents.X + MathF.Abs(r.M23) * HalfExtents.Y + MathF.Abs(r.M33) * HalfExtents.Z);
        return new Aabb(Center - e, Center + e);
    }
}
