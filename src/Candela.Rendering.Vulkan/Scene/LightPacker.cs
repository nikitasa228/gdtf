using System.Numerics;
using Candela.Photometry;

namespace Candela.Rendering.Vulkan;

/// <summary>
/// Converts <see cref="SpotLight"/>s (render contract) into <see cref="GpuLight"/>s. The angular profile is not
/// re-derived here: its parameters (exponent p, half beam/field angles, cut-off) are taken from
/// <see cref="Candela.Photometry.BeamDistribution"/> for the light's <see cref="SpotLight.BeamType"/>, and
/// photometry.glsl evaluates exactly <see cref="BeamDistribution.Relative"/> with them. Allocation-free.
/// </summary>
/// <remarks>
/// <para><b>Gate coordinates.</b> The cookie (gobo, framing blades) lives in the gate plane: a direction from the light
/// in the light frame (right = direction × up, up, direction) is projected onto the plane at unit distance and
/// scaled by 1/tan(θ_f) of the unfrosted beam, so the field-angle circle is the unit circle. The gobo image fills
/// the square around that circle; outside the gobo the gate blocks the light.</para>
/// <para><b>Frost</b> (0..1) convolves the beam with a Gaussian diffusion kernel of standard deviation
/// <see cref="FrostSigmaAtFull"/>·frost: half-angles grow in quadrature (θ₅₀² + (1.1774σ)², θ₁₀² + (2.1460σ)², the
/// 50 % / 10 % half-widths of a Gaussian), which widens the beam and pulls the profile towards a Gaussian (softer
/// edge). The diffuser is assumed lossless, so the peak intensity drops to keep the flux: I₀' = I₀·Ω_eff/Ω_eff'. Frost
/// also blurs the cookie (gobo mip level, blade and iris edges).</para>
/// <para><b>Iris</b> (fraction of the field angle, 1 = open) clips the beam at θ = iris·θ_f (unfrosted field) with a
/// soft edge (10 %, more with focus blur and frost); intensity inside the aperture is unchanged (an iris is a gate
/// stop). <c>Iris ≤ 0</c> means "not set" and is treated as open because <c>default(SpotLight)</c> has 0 there; a
/// closing iris is clamped to 2 %.</para>
/// <para><b>Framing blades</b> A, B, C, D sit at the top, right, bottom and left of the gate (seen from behind the
/// fixture looking along the beam with <see cref="SpotLight.Up"/> up), the module turned by
/// <see cref="SpotLight.BladeRotation"/>. Insertion 0 puts a blade's edge at the cut-off angle (no effect), 1 on the
/// beam axis; <see cref="SpotLight.BladeAngle"/> tilts the edge about the point where it crosses the blade's axis.
/// Each blade removes the half-plane beyond its edge.</para>
/// <para><b>Prism</b>: <see cref="SpotLight.PrismFacets"/> copies of the beam (cookie included), each tilted by
/// <see cref="SpotLight.PrismSpread"/> towards azimuth <see cref="SpotLight.PrismRotation"/> + 2πk/N and carrying 1/N
/// of the intensity (flux), so the total flux is unchanged. At most <see cref="MaxPrismFacets"/> facets.</para>
/// </remarks>
internal static class LightPacker
{
    /// <summary>Standard deviation of the frost diffusion kernel at frost = 1, rad (10°).</summary>
    public const float FrostSigmaAtFull = 10f * MathF.PI / 180f;

    /// <summary>Iris angle used when the iris is open (larger than any cut-off).</summary>
    public const float IrisOpenAngle = 4f;

    /// <summary>Relative width of a sharp iris edge.</summary>
    public const float IrisSoftness = 0.1f;

    /// <summary>Smallest iris opening (fraction of the field angle).</summary>
    public const float MinIris = 0.02f;

    /// <summary>Most prism facets evaluated per light.</summary>
    public const int MaxPrismFacets = (int)ShaderConstants.MaxPrismFacets;

    /// <summary>Largest prism spread, rad (60°).</summary>
    public const float MaxPrismSpread = MathF.PI / 3f;

    /// <summary>Defocus radius of the gobo image at GoboBlur = 1, as a fraction of the image width.</summary>
    public const float GoboBlurAtFull = 0.12f;

    /// <summary>Half width of a focused blade edge in gate units (optical softness of a real gate).</summary>
    public const float SharpEdgeSoftness = 0.004f;

    /// <summary>Largest half-angle used for the shadow frustum and the bounding cone of the cookie (80°).</summary>
    public const float MaxGateHalfAngle = 1.5f;

    /// <summary>The beam distribution the GPU evaluates for <paramref name="light"/> (frost applied) and the peak intensity.</summary>
    public static BeamDistribution Distribution(in SpotLight light, out float peakCandela) =>
        Distribution(light, out peakCandela, out _);

    /// <summary>Like <see cref="Distribution(in SpotLight, out float)"/>, also returning the beam before frost (gate).</summary>
    public static BeamDistribution Distribution(in SpotLight light, out float peakCandela, out BeamDistribution unfrosted)
    {
        peakCandela = float.IsFinite(light.PeakCandela) && light.PeakCandela > 0f ? light.PeakCandela : 0f;
        var type = BeamTypes.IsKnown(light.BeamType) ? light.BeamType : BeamType.Wash;
        var distribution = BeamDistribution.Create(type, light.BeamAngle, light.FieldAngle);
        unfrosted = distribution;
        float frost = Frost(light);
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
    public static float IrisAngle(in SpotLight light, in BeamDistribution gate)
    {
        float iris = light.Iris;
        if (!float.IsFinite(iris) || iris <= 0f || iris >= 1f)
            return IrisOpenAngle;
        return MathF.Max(iris, MinIris) * 0.5f * gate.FieldAngle;
    }

    /// <summary>Number of prism facets the GPU evaluates (0 = no prism).</summary>
    public static int PrismFacetCount(in SpotLight light) => Math.Clamp(light.PrismFacets, 0, MaxPrismFacets);

    /// <summary>Tilt of the prism facets, rad (0 without a prism).</summary>
    public static float PrismSpread(in SpotLight light) =>
        PrismFacetCount(light) > 0 && float.IsFinite(light.PrismSpread) ? Math.Clamp(MathF.Abs(light.PrismSpread), 0f, MaxPrismSpread) : 0f;

    /// <summary>Packs one light. <paramref name="goboSlot"/> is the bindless slot of its gobo (0 = none).</summary>
    public static void Pack(in SpotLight light, uint goboSlot, out GpuLight gpu)
    {
        var distribution = Distribution(light, out float peak, out var gate);
        var color = Sanitize(light.Color);
        int facets = PrismFacetCount(light);
        float spread = PrismSpread(light);
        var intensity = color * (facets > 1 ? peak / facets : peak);

        float halfBeam = 0.5f * distribution.BeamAngle;
        float halfField = 0.5f * distribution.FieldAngle;
        float cutoff = distribution.CutoffAngle;
        float gateHalf = Math.Clamp(0.5f * gate.FieldAngle, 1e-3f, MaxGateHalfAngle);
        float gateTan = MathF.Tan(gateHalf);
        float frost = Frost(light);
        float blur = float.IsFinite(light.GoboBlur) ? Math.Clamp(light.GoboBlur, 0f, 1f) : 0f;
        float irisAngle = IrisAngle(light, gate);
        float irisSoftness = MathF.Min(IrisSoftness + 0.3f * blur + 0.3f * frost, 0.9f);
        // Cookie blur radius in gobo-image units (the gate is 2 gate units = 1 image wide): focus blur plus the angular
        // spread of the frost (its half width at half maximum) seen from the gate.
        float focusBlur = GoboBlurAtFull * blur;
        float frostBlur = frost > 0f ? 0.5f * MathF.Tan(MathF.Min(1.1774f * FrostSigmaAtFull * frost, 1.2f)) / gateTan : 0f;
        float cookieBlur = MathF.Sqrt(focusBlur * focusBlur + frostBlur * frostBlur);
        float effectiveCutoff = MathF.Min(cutoff, irisAngle);
        float bounding = MathF.Min(effectiveCutoff + spread, MathF.PI);
        var direction = NormalizeOr(light.Direction, -Vector3.UnitZ);
        var up = light.Up - Vector3.Dot(light.Up, direction) * direction;
        up = NormalizeOr(up, MathF.Abs(direction.Z) < 0.99f ? Vector3.UnitZ : Vector3.UnitY);
        up = NormalizeOr(up - Vector3.Dot(up, direction) * direction, Vector3.UnitX);
        var right = Vector3.Cross(direction, up);

        uint flags = light.CastShadows ? ShaderConstants.LightCastShadows : 0u;
        if (goboSlot != 0)
            flags |= ShaderConstants.LightGobo;
        if (facets > 0)
            flags |= ShaderConstants.LightPrism;
        float rotation = float.IsFinite(light.GoboRotation) ? light.GoboRotation : 0f;
        float prismRotation = float.IsFinite(light.PrismRotation) ? light.PrismRotation : 0f;

        gpu = new GpuLight
        {
            Position = Finite(light.Position),
            MaxIntensity = MathF.Max(color.X, MathF.Max(color.Y, color.Z)) * peak,
            Direction = direction,
            CosCutoff = bounding >= MathF.PI ? -1f : MathF.Cos(bounding),
            Intensity = intensity,
            InvHalfBeam = halfBeam > 0f ? 1f / halfBeam : 0f,
            Exponent = distribution.Exponent,
            HalfField = halfField,
            InvWindow = cutoff > halfField ? 1f / (cutoff - halfField) : 0f,
            Cutoff = cutoff,
            IrisAngle = irisAngle,
            IrisSoftStart = irisAngle >= IrisOpenAngle ? IrisOpenAngle : irisAngle * (1f - irisSoftness),
            Shape = distribution.Shape switch
            {
                BeamShape.Lambertian => ShaderConstants.ShapeLambertian,
                BeamShape.UniformCone => ShaderConstants.ShapeUniformCone,
                _ => ShaderConstants.ShapeGeneralizedGaussian,
            },
            Up = up,
            SourceRadius = float.IsFinite(light.SourceRadius) ? MathF.Max(light.SourceRadius, 0f) : 0f,
            Right = right,
            GateScale = 1f / gateTan,
            GoboTexture = goboSlot,
            GoboBlur = cookieBlur,
            EdgeSoftness = SharpEdgeSoftness + 2f * cookieBlur,
            PrismFacets = (uint)facets,
            GoboRotation = new Vector2(MathF.Cos(rotation), MathF.Sin(rotation)),
            PrismCosSpread = MathF.Cos(spread),
            PrismSinSpread = MathF.Sin(spread),
            PrismRotation = prismRotation,
            BoundingAngle = bounding,
            SolidAngle = ApproximateSolidAngle(distribution),
            BeamLayer = distribution.Shape == BeamShape.GeneralizedGaussian ? BeamScatteringTable.LayerOf(distribution.Type, distribution.Exponent) : 0f,
        };

        // Framing blades: nominal sides top (A), right (B), bottom (C), left (D), turned by the module rotation.
        float moduleRotation = float.IsFinite(light.BladeRotation) ? light.BladeRotation : 0f;
        float maxOffset = MathF.Tan(MathF.Min(cutoff, MaxGateHalfAngle)) / gateTan;
        bool anyBlade = false;
        gpu.BladeA = Blade(light.BladeInsertion.X, light.BladeAngle.X, moduleRotation + 0.5f * MathF.PI, maxOffset, ref anyBlade);
        gpu.BladeB = Blade(light.BladeInsertion.Y, light.BladeAngle.Y, moduleRotation, maxOffset, ref anyBlade);
        gpu.BladeC = Blade(light.BladeInsertion.Z, light.BladeAngle.Z, moduleRotation - 0.5f * MathF.PI, maxOffset, ref anyBlade);
        gpu.BladeD = Blade(light.BladeInsertion.W, light.BladeAngle.W, moduleRotation + MathF.PI, maxOffset, ref anyBlade);
        if (anyBlade)
            flags |= ShaderConstants.LightBlades;
        gpu.Flags = flags;
    }

    /// <summary>
    /// Approximate effective solid angle of one facet's profile, sr (used for flux estimates when prioritising
    /// lights, not for shading): a Gaussian of the same half beam angle, π·θ_b²/ln 2, limited to a hemisphere.
    /// </summary>
    public static float ApproximateSolidAngle(in BeamDistribution distribution)
    {
        if (distribution.Shape == BeamShape.Lambertian)
            return MathF.PI;
        float halfBeam = 0.5f * distribution.BeamAngle;
        if (distribution.Shape == BeamShape.UniformCone)
            return 2f * MathF.PI * (1f - MathF.Cos(halfBeam));
        return MathF.Min(MathF.PI * halfBeam * halfBeam / 0.6931472f, 2f * MathF.PI);
    }

    private static Vector4 Blade(float insertion, float angle, float side, float maxOffset, ref bool any)
    {
        float t = float.IsFinite(insertion) ? Math.Clamp(insertion, 0f, 1f) : 0f;
        if (t <= 0f)
            return Vector4.Zero;
        any = true;
        float tilt = float.IsFinite(angle) ? angle : 0f;
        var nominal = new Vector2(MathF.Cos(side), MathF.Sin(side));
        var normal = new Vector2(MathF.Cos(side + tilt), MathF.Sin(side + tilt));
        var crossing = nominal * ((1f - t) * maxOffset);
        return new Vector4(normal, Vector2.Dot(crossing, normal), 1f);
    }

    private static float Frost(in SpotLight light) => float.IsFinite(light.Frost) ? Math.Clamp(light.Frost, 0f, 1f) : 0f;

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
