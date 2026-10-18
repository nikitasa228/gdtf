using Candela.Photometry;

namespace Candela.Rendering.Vulkan;

/// <summary>
/// Precomputed line integrals of the beam intensity distributions of <see cref="BeamDistribution"/>: the table that
/// gives the beam renderer the exact profile part of the single-scattering integral of a view ray crossing a beam
/// (an airlight-style integral over the generalised Gaussian, per beam type), so soft beam edges are noise-free and
/// follow the 50 % / 10 % angles and the type's cut-off window exactly.
/// </summary>
/// <remarks>
/// <para><b>Geometry.</b> A view ray and the light S span a plane; the directions from S to the points of the ray form
/// a great circle on the unit sphere. With θ_m the smallest angle between that circle and the beam axis and u the arc
/// from the closest point, the spherical Pythagorean theorem gives cos θ = cos θ_m·cos u, and in chord lengths
/// (Θ = 2·sin(θ/2)) it is exactly Euclidean: Θ² = Θ_m² + cos θ_m·U², U = 2·sin(u/2). In chord units normalised by
/// Θ_n·W (Θ_n = 2·sin(θ_b/2) of the half beam angle θ_b, W = cut-off / θ_b) a ray is therefore a straight line at
/// distance v from the beam centre, and the profile along it depends only on v and the line coordinate y.</para>
/// <para><b>Table.</b> H(v, y) = ∫₀ʸ f̂(W·√(v² + s²)) ds, the cumulative integral along that line of the normalised
/// profile f̂(w) = I(w·θ_b)/I(0) — evaluated, as an approximation, at the normalised chord instead of the normalised
/// angle (exact on the axis and at the beam angle; within ±2 % of the profile up to 60° fields; the renderer's samples
/// correct the remainder). The profile family depends on the exponent p of the generalised Gaussian and on the beam
/// type's cut-off window (Spot/Rectangle 0.35, Wash 0.6, PC 0.8, Fresnel 1.0 × (θ_f − θ_b), at least 0.1·θ_f), so the
/// table has <see cref="Families"/> × <see cref="ExponentSteps"/> layers (p from 0.5 to 64, log-spaced: the GPU
/// interpolates between neighbouring exponents) of <see cref="VSteps"/> × <see cref="YSteps"/> entries over
/// v, y ∈ [0, 1]. Each entry is stored as two half floats (hi + lo, 22 significant bits; both channels interpolate
/// linearly, so their sum is the linear interpolation of H). Uniform cones need no table (H is the chord length).</para>
/// <para>The table is built once per process (about 0.1 s, in parallel) and shared by every renderer.</para>
/// </remarks>
internal static class BeamScatteringTable
{
    /// <summary>Beam-type families of the cut-off window: Spot/Rectangle, Wash, PC, Fresnel.</summary>
    public const int Families = 4;

    /// <summary>Exponent layers per family, log-spaced from <see cref="MinExponent"/> to <see cref="MaxExponent"/>.</summary>
    public const int ExponentSteps = 32;

    /// <summary>Entries along the normalised distance v from the beam centre.</summary>
    public const int VSteps = 64;

    /// <summary>Entries along the normalised line coordinate y.</summary>
    public const int YSteps = 64;

    /// <summary>Smallest exponent p of the generalised Gaussian (<see cref="BeamDistribution.MinExponent"/>).</summary>
    public const float MinExponent = BeamDistribution.MinExponent;

    /// <summary>Largest exponent p of the generalised Gaussian (<see cref="BeamDistribution.MaxExponent"/>).</summary>
    public const float MaxExponent = BeamDistribution.MaxExponent;

    /// <summary>Layers of the 3D texture (families × exponents).</summary>
    public const int Layers = Families * ExponentSteps;

    // Sub-steps of the cumulative integration between two table entries (midpoint rule on 1/1024 of the range).
    private const int SubSteps = 16;

    // Half beam angle of the reference distributions the table is built from: small, so no clamp of
    // BeamDistribution.Create (field ≤ 135°) applies and the normalised profile is exactly scale-invariant.
    private const float ReferenceHalfBeam = 0.01f;

    private static readonly float _log2Min = MathF.Log2(MinExponent);
    private static readonly float _log2Range = MathF.Log2(MaxExponent) - MathF.Log2(MinExponent);
    private static readonly Lazy<Half[]> _data = new(Build, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// The table as RG16F texels (hi, lo), x = y index fastest, then v, then layer = family × <see cref="ExponentSteps"/>
    /// + exponent index. Built on first use.
    /// </summary>
    public static ReadOnlySpan<Half> Texels => _data.Value;

    /// <summary>Beam type → family (window factor of <see cref="BeamDistribution"/>).</summary>
    public static int Family(BeamType type) => type switch
    {
        BeamType.Spot or BeamType.Rectangle => 0,
        BeamType.PC => 2,
        BeamType.Fresnel => 3,
        _ => 1,
    };

    /// <summary>A beam type of each family (to build its reference distributions).</summary>
    public static BeamType FamilyType(int family) => family switch
    {
        0 => BeamType.Spot,
        2 => BeamType.PC,
        3 => BeamType.Fresnel,
        _ => BeamType.Wash,
    };

    /// <summary>Exponent of layer <paramref name="index"/> (0 … <see cref="ExponentSteps"/> − 1).</summary>
    public static float ExponentAt(int index) => MathF.Pow(2f, _log2Min + _log2Range * index / (ExponentSteps - 1));

    /// <summary>
    /// Continuous layer coordinate of a distribution: family × <see cref="ExponentSteps"/> + fractional exponent index
    /// (the GPU interpolates linearly between the two neighbouring exponents of the family, in v and y the shader
    /// interpolates itself in full precision).
    /// </summary>
    public static float LayerOf(BeamType type, float exponent)
    {
        float p = float.IsFinite(exponent) ? Math.Clamp(exponent, MinExponent, MaxExponent) : 2f;
        float index = (MathF.Log2(p) - _log2Min) / _log2Range * (ExponentSteps - 1);
        return Family(BeamTypes.IsKnown(type) ? type : BeamType.Wash) * ExponentSteps + Math.Clamp(index, 0f, ExponentSteps - 1);
    }

    /// <summary>The reference (normalised) distribution of a layer: half beam angle <see cref="ReferenceHalfBeam"/>.</summary>
    public static BeamDistribution Reference(int family, float exponent)
    {
        float ratio = MathF.Pow(MathF.Log(10f) / MathF.Log(2f), 1f / exponent);
        return BeamDistribution.Create(FamilyType(family), 2f * ReferenceHalfBeam, 2f * ReferenceHalfBeam * ratio);
    }

    /// <summary>
    /// H(v, y) of a layer computed directly (CPU reference for tests): the integral of the normalised profile along the
    /// line at normalised distance <paramref name="v"/>, from 0 to <paramref name="y"/> (both in units of the cut-off).
    /// </summary>
    public static double Integral(int family, float exponent, double v, double y, int steps = 20000)
    {
        var d = Reference(family, exponent);
        double w = d.CutoffAngle / ReferenceHalfBeam;
        double sign = Math.Sign(y);
        y = Math.Abs(y);
        double h = y / steps, sum = 0;
        for (int i = 0; i < steps; i++)
        {
            double s = (i + 0.5) * h;
            sum += d.Relative((float)(ReferenceHalfBeam * w * Math.Sqrt(v * v + s * s)));
        }

        return sign * sum * h;
    }

    /// <summary>
    /// H(v, y) as the GPU reads it: bilinear in (v, y) on the table grid, linear between the two exponent layers
    /// (CPU twin of <c>profileIntegral</c> in beam_scatter.comp, for tests).
    /// </summary>
    public static double Lookup(float layer, double v, double y)
    {
        double sign = Math.Sign(y);
        y = Math.Min(Math.Abs(y), 1.0);
        v = Math.Clamp(v, 0.0, 1.0);
        int l0 = Math.Clamp((int)MathF.Floor(layer), 0, Layers - 1);
        int l1 = Math.Min(l0 + 1, Layers - 1);
        double fl = Math.Clamp(layer - l0, 0.0, 1.0);
        double a = Bilinear(l0, v, y), b = Bilinear(l1, v, y);
        return sign * (a + (b - a) * fl);
    }

    private static double At(int layer, int vi, int yi)
    {
        var data = Texels;
        int i = 2 * ((layer * VSteps + vi) * YSteps + yi);
        return (double)(float)data[i] + (float)data[i + 1];
    }

    private static double Bilinear(int layer, double v, double y)
    {
        double fv = v * (VSteps - 1), fy = y * (YSteps - 1);
        int v0 = Math.Min((int)fv, VSteps - 2), y0 = Math.Min((int)fy, YSteps - 2);
        double tv = fv - v0, ty = fy - y0;
        double a = At(layer, v0, y0) + (At(layer, v0, y0 + 1) - At(layer, v0, y0)) * ty;
        double b = At(layer, v0 + 1, y0) + (At(layer, v0 + 1, y0 + 1) - At(layer, v0 + 1, y0)) * ty;
        return a + (b - a) * tv;
    }

    private static Half[] Build()
    {
        var texels = new Half[2 * Layers * VSteps * YSteps];
        Parallel.For(0, Layers * VSteps, row =>
        {
            int layer = row / VSteps;
            int vi = row % VSteps;
            int family = layer / ExponentSteps;
            float exponent = ExponentAt(layer % ExponentSteps);
            var d = Reference(family, exponent);
            double w = d.CutoffAngle / ReferenceHalfBeam;
            double v = vi / (double)(VSteps - 1);
            double dy = 1.0 / (YSteps - 1);
            double h = dy / SubSteps;
            double sum = 0;
            int baseIndex = 2 * (row * YSteps);
            texels[baseIndex] = Half.Zero;
            texels[baseIndex + 1] = Half.Zero;
            for (int yi = 1; yi < YSteps; yi++)
            {
                for (int k = 0; k < SubSteps; k++)
                {
                    double s = (yi - 1) * dy + (k + 0.5) * h;
                    sum += d.Relative((float)(ReferenceHalfBeam * w * Math.Sqrt(v * v + s * s))) * h;
                }

                var hi = (Half)sum;
                texels[baseIndex + 2 * yi] = hi;
                texels[baseIndex + 2 * yi + 1] = (Half)(sum - (double)hi);
            }
        });
        return texels;
    }
}
