using System.Numerics;

namespace Candela.Photometry;

/// <summary>
/// A photometric point light with a rotationally symmetric beam — the CPU reference description of a fixture's
/// output for light-meter queries (and the data the GPU lux overlay is tested against). World space: metres, Z up.
/// </summary>
public readonly struct SpotLight
{
    /// <summary>Creates a light.</summary>
    /// <param name="position">Position of the (virtual) point source, m.</param>
    /// <param name="direction">Beam axis; normalised here. A zero or non-finite vector becomes straight down (−Z).</param>
    /// <param name="peakCandela">On-axis luminous intensity, cd (negative or non-finite → 0).</param>
    /// <param name="distribution">Relative intensity distribution.</param>
    /// <param name="color">Light colour (default: D65 white).</param>
    public SpotLight(Vector3 position, Vector3 direction, float peakCandela, BeamDistribution distribution, LightColor color = default)
    {
        Position = position;
        float len = direction.Length();
        Direction = len > 1e-12f && float.IsFinite(len) ? direction / len : -Vector3.UnitZ;
        PeakCandela = float.IsFinite(peakCandela) && peakCandela > 0f ? peakCandela : 0f;
        Distribution = distribution;
        Color = color;
    }

    /// <summary>Position of the point source, m.</summary>
    public Vector3 Position { get; }

    /// <summary>Unit beam axis.</summary>
    public Vector3 Direction { get; }

    /// <summary>On-axis luminous intensity, cd.</summary>
    public float PeakCandela { get; }

    /// <summary>Relative intensity distribution.</summary>
    public BeamDistribution Distribution { get; }

    /// <summary>Colour of the light.</summary>
    public LightColor Color { get; }

    /// <summary>Luminous intensity toward <paramref name="point"/>, cd (0 when the point coincides with the source).</summary>
    public float IntensityToward(Vector3 point)
    {
        var v = point - Position;
        float d = v.Length();
        if (!(d > 0f))
            return 0f;
        return PeakCandela * Distribution.Relative(AngleFromAxis(Direction, v));
    }

    /// <summary>
    /// Angle θ between the beam axis and the direction from the light to <paramref name="point"/>, rad (0..π); 0 when
    /// the point coincides with the source. Accurate near the axis, see <see cref="AngleFromAxis"/>.
    /// </summary>
    public float OffAxisAngle(Vector3 point) => AngleFromAxis(Direction, point - Position);

    /// <summary>
    /// Angle between unit vector <paramref name="unitAxis"/> and vector <paramref name="v"/> (any length), rad (0..π),
    /// as atan2(|a × v|, a · v). Unlike acos of the normalised dot product this keeps full precision near the axis:
    /// in single precision the cosine of a small angle is quantised in steps of 6·10⁻⁸, so acos cannot resolve angles
    /// below ≈ 3·10⁻⁴ rad and misplaces small ones by up to that much — 23 % of the on-axis intensity of a 0.2° beam.
    /// </summary>
    internal static float AngleFromAxis(Vector3 unitAxis, Vector3 v) =>
        MathF.Atan2(Vector3.Cross(unitAxis, v).Length(), Vector3.Dot(unitAxis, v));
}
