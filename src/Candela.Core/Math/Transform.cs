using System.Numerics;

namespace Candela.Core.Math;

/// <summary>
/// Local transform of an entity: position in metres, rotation, non-uniform scale.
/// Coordinate system: right-handed, Z up, X right, Y forward (into the stage) — identical to MVR/GDTF.
/// Matrices follow System.Numerics (row vectors): world = local * parentWorld.
/// </summary>
public struct Transform : IEquatable<Transform>
{
    /// <summary>Position relative to the parent, metres.</summary>
    public Vector3 Position;

    /// <summary>Rotation relative to the parent (unit quaternion).</summary>
    public Quaternion Rotation;

    /// <summary>Scale along the local axes (1 = unscaled).</summary>
    public Vector3 Scale;

    /// <summary>Creates a transform from its parts.</summary>
    public Transform(Vector3 position, Quaternion rotation, Vector3 scale)
    {
        Position = position;
        Rotation = rotation;
        Scale = scale;
    }

    /// <summary>A translation by <paramref name="position"/> (metres), unrotated and unscaled.</summary>
    public Transform(Vector3 position) : this(position, Quaternion.Identity, Vector3.One) { }

    /// <summary>No translation, rotation or scaling.</summary>
    public static Transform Identity => new(Vector3.Zero, Quaternion.Identity, Vector3.One);

    /// <summary>The matrix scale · rotation · translation (row-vector convention: <c>v' = v * M</c>).</summary>
    public readonly Matrix4x4 ToMatrix() =>
        Matrix4x4.CreateScale(Scale) * Matrix4x4.CreateFromQuaternion(Rotation) * Matrix4x4.CreateTranslation(Position);

    /// <summary>Decomposes an affine matrix (no shear). Falls back to identity rotation for degenerate input.</summary>
    public static Transform FromMatrix(Matrix4x4 m)
    {
        if (Matrix4x4.Decompose(m, out var scale, out var rotation, out var translation))
            return new Transform(translation, Quaternion.Normalize(rotation), scale);
        return new Transform(m.Translation, Quaternion.Identity, Vector3.One);
    }

    /// <summary>Exact component-wise equality.</summary>
    public readonly bool Equals(Transform other) =>
        Position == other.Position && Rotation == other.Rotation && Scale == other.Scale;

    /// <inheritdoc/>
    public override readonly bool Equals(object? obj) => obj is Transform t && Equals(t);

    /// <inheritdoc/>
    public override readonly int GetHashCode() => HashCode.Combine(Position, Rotation, Scale);

    /// <summary>Exact component-wise equality.</summary>
    public static bool operator ==(Transform a, Transform b) => a.Equals(b);

    /// <summary>Exact component-wise inequality.</summary>
    public static bool operator !=(Transform a, Transform b) => !a.Equals(b);

    /// <inheritdoc/>
    public override readonly string ToString() => $"T{Position} R{Rotation} S{Scale}";
}
