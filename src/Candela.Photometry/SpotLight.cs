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
        float cosTheta = Math.Clamp(Vector3.Dot(Direction, v) / d, -1f, 1f);
        return PeakCandela * Distribution.Relative(MathF.Acos(cosTheta));
    }
}
