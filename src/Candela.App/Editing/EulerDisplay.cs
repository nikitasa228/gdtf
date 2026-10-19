using System.Numerics;

namespace Candela.App.Editing;

/// <summary>
/// The Euler angles the properties panel shows for an entity's rotation. A rotation has many Euler triples and
/// <see cref="TransformMath.ToEulerDegrees"/> always returns the one with Y in [−90°, 90°], so recomputing the angles
/// from the quaternion every frame turns a typed Y of 95° into (−170°, 85°, −160°) and makes a drag on Y bounce back at
/// 90°. The triple the user entered is therefore kept for as long as the entity's rotation is the one it produced;
/// a rotation changed elsewhere (gizmo, undo, another entity) is shown freshly decomposed.
/// </summary>
internal sealed class EulerDisplay
{
    private object? _owner;
    private Quaternion _rotation;
    private Vector3 _degrees;

    /// <summary>The angles (degrees) to show for <paramref name="rotation"/> of <paramref name="owner"/>.</summary>
    public Vector3 Get(object owner, Quaternion rotation)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (ReferenceEquals(owner, _owner) && Same(rotation, _rotation))
            return _degrees;
        _owner = owner;
        _rotation = rotation;
        _degrees = TransformMath.ToEulerDegrees(rotation);
        return _degrees;
    }

    /// <summary>Remembers that <paramref name="owner"/> was given <paramref name="degrees"/>, i.e. <paramref name="applied"/>.</summary>
    public void Set(object owner, Vector3 degrees, Quaternion applied)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _owner = owner;
        _degrees = degrees;
        _rotation = applied;
    }

    /// <summary>True when two unit quaternions describe the same rotation (q and −q included).</summary>
    private static bool Same(Quaternion a, Quaternion b) => MathF.Abs(Quaternion.Dot(a, b)) > 1f - 1e-6f;
}
