using System.Numerics;

namespace Candela.Photometry;

/// <summary>
/// Correlated colour temperature and distance from the Planckian locus of a chromaticity.
/// </summary>
/// <param name="Kelvin">Correlated colour temperature, K; clamped to <see cref="Cct.MinKelvin"/>–<see cref="Cct.MaxKelvin"/>; NaN when the input chromaticity was invalid.</param>
/// <param name="Duv">
/// Signed distance from the Planckian locus in the CIE 1960 (u, v) diagram (ANSI C78.377 / CIE 015:2018 Δuv):
/// positive above the locus (greenish), negative below (magenta-pink).
/// </param>
public readonly record struct CctResult(float Kelvin, float Duv)
{
    /// <summary>Result for an invalid chromaticity.</summary>
    public static CctResult Invalid => new(float.NaN, float.NaN);

    /// <summary>True when the input chromaticity was valid and a temperature was found.</summary>
    public bool IsValid => float.IsFinite(Kelvin) && float.IsFinite(Duv);

    /// <summary>
    /// True when the CCT is meaningful: CIE 015:2004 §9.5 recommends quoting a CCT only within |Δuv| ≤ 0.05.
    /// </summary>
    public bool IsMeaningful => IsValid && MathF.Abs(Duv) <= 0.05f;
}

/// <summary>
/// Correlated colour temperature (CCT): the Planckian locus, CCT and Δuv of a chromaticity by Ohno's method
/// (Y. Ohno, "Practical Use and Calculation of CCT and Duv", LEUKOS 10(1):47–55, 2014 — "Ohno 2013"), the inverse
/// (chromaticity of a CCT with a Δuv offset), McCamy's approximation for UI use and mired conversions.
/// </summary>
/// <remarks>
/// The locus is computed from Planck's law and the CIE 1931 colour-matching functions (not from an analytic fit) and
/// cached on a 0.25 % logarithmic temperature grid (see <see cref="PlanckianLocus"/>). With that step the combined
/// triangular/parabolic solution is accurate to well below 1 K over 1000–25000 K, so Ohno's 0.99991 correction factor
/// for a 1 % table is not needed.
/// </remarks>
public static class Cct
{
    /// <summary>Lowest CCT handled by the locus table, K.</summary>
    public const float MinKelvin = 1000f;

    /// <summary>Highest CCT handled by the locus table, K.</summary>
    public const float MaxKelvin = 25000f;

    /// <summary>|Δuv| above which <see cref="FromXy(float, float)"/> switches from the triangular to the parabolic solution (Ohno 2013).</summary>
    private const double ParabolicThreshold = 0.002;

    /// <summary>Node stride of the coarse nearest-node search (8 nodes = 2 % in temperature).</summary>
    private const int SearchStride = 8;

    /// <summary>CIE 1931 (x, y) of the Planckian radiator at <paramref name="kelvin"/> (clamped to 1000–25000 K; NaN → 6500 K).</summary>
    public static Vector2 PlanckianXy(float kelvin)
    {
        PlanckianLocus.InterpolateUv(ClampKelvin(kelvin), out double u, out double v);
        return ColorSpaces.Uv1960ToXy(new Vector2((float)u, (float)v));
    }

    /// <summary>CIE 1960 (u, v) of the Planckian radiator at <paramref name="kelvin"/> (clamped to 1000–25000 K; NaN → 6500 K).</summary>
    public static Vector2 PlanckianUv(float kelvin)
    {
        PlanckianLocus.InterpolateUv(ClampKelvin(kelvin), out double u, out double v);
        return new Vector2((float)u, (float)v);
    }

    /// <summary>CCT and Δuv of CIE 1931 chromaticity <paramref name="xy"/>, see <see cref="FromXy(float, float)"/>.</summary>
    public static CctResult FromXy(Vector2 xy) => FromXy(xy.X, xy.Y);

    /// <summary>
    /// CCT and Δuv of a CIE 1931 chromaticity by Ohno's combined method: nearest node of the cached Planckian table,
    /// then the triangular solution when |Δuv| &lt; 0.002, otherwise the parabolic solution through the three nearest
    /// nodes. Returns <see cref="CctResult.Invalid"/> for non-finite input or y ≤ 0; temperatures outside 1000–25000 K
    /// are clamped to that range.
    /// </summary>
    /// <param name="x">CIE 1931 x.</param>
    /// <param name="y">CIE 1931 y.</param>
    public static CctResult FromXy(float x, float y)
    {
        if (!float.IsFinite(x) || !float.IsFinite(y) || y <= 0f)
            return CctResult.Invalid;
        double den = -2.0 * x + 12.0 * y + 3.0;
        if (!(den > 0))
            return CctResult.Invalid;
        double u = 4.0 * x / den, v = 6.0 * y / den;

        // Nearest table node: coarse pass over every 8th node, then a fine pass around the coarse minimum. The distance
        // to the locus is unimodal along it for any chromaticity within |Δuv| ≤ 0.05 (and far beyond), so this finds the
        // same node as a full scan at a tenth of the cost.
        int n = PlanckianLocus.TableLength;
        int m = NearestNode(u, v, 0, n - 1, SearchStride);
        m = NearestNode(u, v, Math.Max(0, m - SearchStride), Math.Min(n - 1, m + SearchStride), 1);
        m = Math.Clamp(m, 1, n - 2);
        PlanckianLocus.Node(m - 1, out double t0, out double u0, out double v0);
        PlanckianLocus.Node(m, out double t1, out double u1, out double v1);
        PlanckianLocus.Node(m + 1, out double t2, out double u2, out double v2);
        double d0 = Math.Sqrt((u - u0) * (u - u0) + (v - v0) * (v - v0));
        double d1 = Math.Sqrt((u - u1) * (u - u1) + (v - v1) * (v - v1));
        double d2n = Math.Sqrt((u - u2) * (u - u2) + (v - v2) * (v - v2));

        // Triangular solution.
        double l = Math.Sqrt((u2 - u0) * (u2 - u0) + (v2 - v0) * (v2 - v0));
        double along = (d0 * d0 - d2n * d2n + l * l) / (2.0 * l);
        double kelvin = t0 + (t2 - t0) * along / l;
        double vOnChord = v0 + (v2 - v0) * along / l;
        double duv = Math.Sqrt(Math.Max(d0 * d0 - along * along, 0.0)) * Math.Sign(v - vOnChord);

        if (Math.Abs(duv) >= ParabolicThreshold)
        {
            // Parabolic solution d(T) = a·s² + b·s + c with s = T − T(m), fitted through the three nearest nodes.
            double s0 = t0 - t1, s2 = t2 - t1;
            double a = ((d2n - d1) / s2 - (d0 - d1) / s0) / (s2 - s0);
            double b = (d2n - d1) / s2 - a * s2;
            if (a > 0)
            {
                double s = -b / (2.0 * a);
                kelvin = t1 + s;
                PlanckianLocus.InterpolateUv(Math.Clamp(kelvin, t0 / PlanckianLocus.TableRatio, t2 * PlanckianLocus.TableRatio),
                    out _, out double vt);
                duv = (a * s * s + b * s + d1) * Math.Sign(v - vt);
            }
        }

        return new CctResult((float)Math.Clamp(kelvin, MinKelvin, MaxKelvin), (float)duv);
    }

    /// <summary>
    /// McCamy's cubic approximation of CCT (C. S. McCamy, Color Res. Appl. 17:142–144, 1992): within a few kelvin
    /// of the exact value between 2856 K and 6504 K near the locus, but degrades beyond. Fast enough for UI readouts;
    /// use <see cref="FromXy(float, float)"/> for anything physical.
    /// </summary>
    public static float FromXyMcCamy(float x, float y)
    {
        float den = 0.1858f - y;
        if (!float.IsFinite(x) || !float.IsFinite(y) || MathF.Abs(den) < 1e-6f)
            return float.NaN;
        float n = (x - 0.3320f) / den;
        return ((449f * n + 3525f) * n + 6823.3f) * n + 5520.33f;
    }

    /// <summary>
    /// CIE 1931 (x, y) of correlated colour temperature <paramref name="kelvin"/> offset by <paramref name="duv"/>
    /// perpendicular to the Planckian locus in the (u, v) diagram (positive = above the locus, greenish). On the locus
    /// (Δuv = 0) the cached table is used; with an offset the locus point and its tangent are computed exactly from
    /// Planck's law. Temperatures are clamped to 1000–25000 K (NaN → 6500 K); a non-finite Δuv is treated as 0.
    /// </summary>
    public static Vector2 XyFromCct(float kelvin, float duv = 0f)
    {
        double t = ClampKelvin(kelvin);
        double offset = float.IsFinite(duv) ? duv : 0.0;
        double u, v;
        if (offset == 0.0)
        {
            PlanckianLocus.InterpolateUv(t, out u, out v);
        }
        else
        {
            PlanckianLocus.ComputeUvWithDerivative(t, out u, out v, out double du, out double dv);
            double len = Math.Sqrt(du * du + dv * dv);
            if (len > 0)
            {
                // Normal pointing to +v ("above"): tangent (du, dv) rotated clockwise.
                u += offset * dv / len;
                v -= offset * du / len;
            }
        }

        return ColorSpaces.Uv1960ToXy(new Vector2((float)u, (float)v));
    }

    /// <summary>Mired (micro reciprocal degree) value of a colour temperature: 10⁶/T.</summary>
    public static float ToMired(float kelvin) => 1e6f / kelvin;

    /// <summary>Colour temperature (K) of a mired value: 10⁶/mired.</summary>
    public static float FromMired(float mired) => 1e6f / mired;

    /// <summary>
    /// Mired shift of a conversion filter that turns <paramref name="sourceKelvin"/> into <paramref name="targetKelvin"/>
    /// (positive = warming / CTO, negative = cooling / CTB). Full CTO (6500 K → 3200 K) is about +159 mired.
    /// </summary>
    public static float MiredShift(float sourceKelvin, float targetKelvin) => 1e6f / targetKelvin - 1e6f / sourceKelvin;

    internal static double ClampKelvin(float kelvin) => float.IsNaN(kelvin) ? 6500.0 : Math.Clamp(kelvin, MinKelvin, MaxKelvin);

    /// <summary>Index of the table node nearest to (u, v) among first, first+stride, …, and last.</summary>
    internal static int NearestNode(double u, double v, int first, int last, int stride)
    {
        int best = first;
        double bestD2 = double.MaxValue;
        for (int i = first; ; i += stride)
        {
            if (i > last)
                i = last;
            PlanckianLocus.Node(i, out _, out double ui, out double vi);
            double d2 = (u - ui) * (u - ui) + (v - vi) * (v - vi);
            if (d2 < bestD2)
            {
                bestD2 = d2;
                best = i;
            }

            if (i == last)
                return best;
        }
    }
}
