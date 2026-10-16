using System.Numerics;

namespace Candela.Core.Math;

/// <summary>
/// Local transform of an entity: position in metres, rotation, non-uniform scale.
/// Coordinate system: right-handed, Z up, X right, Y forward (into the stage) — identical to MVR/GDTF.
/// Matrices follow System.Numerics (row vectors): world = local * parentWorld.
/// </summary>
public struct Transform : IEquatable<Transform>
{
    public Vector3 Position;
    public Quaternion Rotation;
    public Vector3 Scale;

    public Transform(Vector3 position, Quaternion rotation, Vector3 scale)
    {
        Position = position;
        Rotation = rotation;
        Scale = scale;
    }

    public Transform(Vector3 position) : this(position, Quaternion.Identity, Vector3.One) { }

    public static Transform Identity => new(Vector3.Zero, Quaternion.Identity, Vector3.One);

    public readonly Matrix4x4 ToMatrix() =>
        Matrix4x4.CreateScale(Scale) * Matrix4x4.CreateFromQuaternion(Rotation) * Matrix4x4.CreateTranslation(Position);

    /// <summary>Decomposes an affine matrix (no shear). Falls back to identity rotation for degenerate input.</summary>
    public static Transform FromMatrix(Matrix4x4 m)
    {
        if (Matrix4x4.Decompose(m, out var scale, out var rotation, out var translation))
            return new Transform(translation, Quaternion.Normalize(rotation), scale);
        return new Transform(m.Translation, Quaternion.Identity, Vector3.One);
    }

    public readonly bool Equals(Transform other) =>
        Position == other.Position && Rotation == other.Rotation && Scale == other.Scale;

    public override readonly bool Equals(object? obj) => obj is Transform t && Equals(t);
    public override readonly int GetHashCode() => HashCode.Combine(Position, Rotation, Scale);
    public static bool operator ==(Transform a, Transform b) => a.Equals(b);
    public static bool operator !=(Transform a, Transform b) => !a.Equals(b);

    public override readonly string ToString() => $"T{Position} R{Rotation} S{Scale}";
}
