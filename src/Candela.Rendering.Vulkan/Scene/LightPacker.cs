using System.Numerics;
using Candela.Photometry;

namespace Candela.Rendering.Vulkan;

/// <summary>
/// Converts <see cref="SpotLight"/>s (render contract) into <see cref="GpuLight"/>s. The angular profile is not
/// re-derived here: its parameters (exponent p, half beam/field angles, cut-off) are taken from
/// <see cref="Candela.Photometry.BeamDistribution"/>, and photometry.glsl evaluates exactly
/// <see cref="BeamDistribution.Relative"/> with them. Allocation-free.
/// </summary>
/// <remarks>
/// <para><b>Beam type.</b> <see cref="SpotLight"/> carries beam and field angles but no GDTF beam type, so every light
/// uses <see cref="DefaultBeamType"/> (Wash, the GDTF default: window factor 0.6 beyond the field angle).</para>
/// <para><b>Frost</b> (0..1) convolves the beam with a Gaussian diffusion kernel of standard deviation
/// <see cref="FrostSigmaAtFull"/>·frost: half-angles grow in quadrature (θ₅₀² + (1.1774σ)², θ₁₀² + (2.1460σ)², the
/// 50 % / 10 % half-widths of a Gaussian), which widens the beam and pulls the profile towards a Gaussian (softer
/// edge). The diffuser is assumed lossless, so the peak intensity drops to keep the flux: I₀' = I₀·Ω_eff/Ω_eff'.</para>
/// <para><b>Iris</b> (fraction of the field angle, 1 = open) clips the beam at θ = iris·θ_f with a 10 % soft edge;
/// intensity inside the aperture is unchanged (an iris is a gate stop). <c>Iris ≤ 0</c> means "not set" and is
/// treated as open because <c>default(SpotLight)</c> has 0 there; a closing iris is clamped to 2 %.</para>
/// </remarks>
internal static class LightPacker
{
    /// <summary>Beam type used for all lights (see remarks).</summary>
    public const BeamType DefaultBeamType = BeamType.Wash;

    /// <summary>Standard deviation of the frost diffusion kernel at frost = 1, rad (10°).</summary>
    public const float FrostSigmaAtFull = 10f * MathF.PI / 180f;

    /// <summary>Iris angle used when the iris is open (larger than any cut-off).</summary>
    public const float IrisOpenAngle = 4f;

    /// <summary>Relative width of the iris edge.</summary>
    public const float IrisSoftness = 0.1f;

    /// <summary>Smallest iris opening (fraction of the field angle).</summary>
    public const float MinIris = 0.02f;

    /// <summary>The beam distribution the GPU evaluates for <paramref name="light"/> (frost applied) and the peak intensity.</summary>
    public static BeamDistribution Distribution(in SpotLight light, out float peakCandela)
    {
        peakCandela = float.IsFinite(light.PeakCandela) && light.PeakCandela > 0f ? light.PeakCandela : 0f;
        var distribution = BeamDistribution.Create(DefaultBeamType, light.BeamAngle, light.FieldAngle);
        float frost = float.IsFinite(light.Frost) ? Math.Clamp(light.Frost, 0f, 1f) : 0f;
        if (frost <= 0f || distribution.Shape != BeamShape.GeneralizedGaussian)
            return distribution;

        float sigma = FrostSigmaAtFull * frost;
        float halfBeam = 0.5f * distribution.BeamAngle;
        float halfField = 0.5f * distribution.FieldAngle;
        float beam = 2f * MathF.Sqrt(halfBeam * halfBeam + 1.1774f * 1.1774f * sigma * sigma);
        float field = 2f * MathF.Sqrt(halfField * halfField + 2.1460f * 2.1460f * sigma * sigma);
        var frosted = distribution.WithAngles(beam, field);
        double before = distribution.SolidAngleIntegral();
        double after = frosted.SolidAngleIntegral();
        if (after > 0 && before > 0)
            peakCandela = (float)(peakCandela * before / after);
        return frosted;
    }

    /// <summary>Iris aperture half-angle (rad) for a light, <see cref="IrisOpenAngle"/> when open.</summary>
    public static float IrisAngle(in SpotLight light, in BeamDistribution distribution)
    {
        float iris = light.Iris;
        if (!float.IsFinite(iris) || iris <= 0f || iris >= 1f)
            return IrisOpenAngle;
        return MathF.Max(iris, MinIris) * 0.5f * distribution.FieldAngle;
    }

    /// <summary>Packs one light.</summary>
    public static void Pack(in SpotLight light, out GpuLight gpu)
    {
        var distribution = Distribution(light, out float peak);
        var color = Sanitize(light.Color);
        var intensity = color * peak;

        float halfBeam = 0.5f * distribution.BeamAngle;
        float halfField = 0.5f * distribution.FieldAngle;
        float cutoff = distribution.CutoffAngle;
        float irisAngle = IrisAngle(light, distribution);
        float effectiveCutoff = MathF.Min(cutoff, irisAngle);
        var direction = NormalizeOr(light.Direction, -Vector3.UnitZ);
        var up = light.Up - Vector3.Dot(light.Up, direction) * direction;
        up = NormalizeOr(up, MathF.Abs(direction.Z) < 0.99f ? Vector3.UnitZ : Vector3.UnitY);

        gpu = new GpuLight
        {
            Position = Finite(light.Position),
            MaxIntensity = MathF.Max(intensity.X, MathF.Max(intensity.Y, intensity.Z)),
            Direction = direction,
            CosCutoff = effectiveCutoff >= MathF.PI ? -1f : MathF.Cos(effectiveCutoff),
            Intensity = intensity,
            InvHalfBeam = halfBeam > 0f ? 1f / halfBeam : 0f,
            Exponent = distribution.Exponent,
            HalfField = halfField,
            InvWindow = cutoff > halfField ? 1f / (cutoff - halfField) : 0f,
            Cutoff = cutoff,
            IrisAngle = irisAngle,
            IrisSoftStart = irisAngle >= IrisOpenAngle ? IrisOpenAngle : irisAngle * (1f - IrisSoftness),
            Shape = distribution.Shape switch
            {
                BeamShape.Lambertian => ShaderConstants.ShapeLambertian,
                BeamShape.UniformCone => ShaderConstants.ShapeUniformCone,
                _ => ShaderConstants.ShapeGeneralizedGaussian,
            },
            Flags = light.CastShadows ? 1u : 0u,
            Up = up,
            SourceRadius = float.IsFinite(light.SourceRadius) ? MathF.Max(light.SourceRadius, 0f) : 0f,
        };
    }

    private static Vector3 Sanitize(Vector3 color) => new(
        float.IsFinite(color.X) ? MathF.Max(color.X, 0f) : 0f,
        float.IsFinite(color.Y) ? MathF.Max(color.Y, 0f) : 0f,
        float.IsFinite(color.Z) ? MathF.Max(color.Z, 0f) : 0f);

    private static Vector3 Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z) ? v : Vector3.Zero;

    private static Vector3 NormalizeOr(Vector3 v, Vector3 fallback)
    {
        float length = v.Length();
        return length > 1e-12f && float.IsFinite(length) ? v / length : fallback;
    }
}
