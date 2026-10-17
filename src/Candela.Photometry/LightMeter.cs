using System.Numerics;

namespace Candela.Photometry;

/// <summary>
/// CPU reference light meter: illuminance (lx) at points from a set of <see cref="SpotLight"/>s by the inverse-square
/// cosine law, without occlusion or inter-reflection. It is the ground truth the GPU lux overlay is tested against.
/// </summary>
/// <remarks>
/// For each light: d = |P − L|, θ = angle between the beam axis and P − L, α = angle between the surface normal and
/// L − P; E = I₀·I_rel(θ)·max(cos α, 0)/max(d, <see cref="Photometrics.MinDistance"/>)². A zero normal means "a meter
/// aimed at each light" (cos α = 1 for every light). A point exactly at a light's position receives nothing from it.
/// All methods are allocation-free.
/// </remarks>
public static class LightMeter
{
    /// <summary>Total illuminance at <paramref name="point"/> on a surface with normal <paramref name="normal"/>, lx.</summary>
    public static float IlluminanceAt(ReadOnlySpan<SpotLight> lights, Vector3 point, Vector3 normal)
    {
        var n = NormalizeOrZero(normal);
        double sum = 0;
        foreach (ref readonly var light in lights)
            sum += Contribution(light, point, n);
        return (float)sum;
    }

    /// <summary>
    /// Illuminance at <paramref name="point"/> as linear Rec.2020 RGB: Σ Eᵢ·colourᵢ. Its Rec.2020 luminance equals the
    /// illuminance in lux, and <see cref="LightColor.FromRec2020"/> of it is the colour of the mixed light.
    /// </summary>
    public static Vector3 IlluminanceRgbAt(ReadOnlySpan<SpotLight> lights, Vector3 point, Vector3 normal)
    {
        var n = NormalizeOrZero(normal);
        var sum = Vector3.Zero;
        foreach (ref readonly var light in lights)
        {
            float e = Contribution(light, point, n);
            if (e > 0f)
                sum += light.Color.Rgb * e;
        }

        return sum;
    }

    /// <summary>Illuminance at many points sharing one surface normal; <paramref name="results"/>[i] receives the lux at <paramref name="points"/>[i].</summary>
    /// <exception cref="ArgumentException"><paramref name="results"/> is shorter than <paramref name="points"/>.</exception>
    public static void IlluminanceAt(ReadOnlySpan<SpotLight> lights, ReadOnlySpan<Vector3> points, Vector3 normal, Span<float> results)
    {
        if (results.Length < points.Length)
            throw new ArgumentException("The result span is shorter than the point span.", nameof(results));
        var n = NormalizeOrZero(normal);
        for (int i = 0; i < points.Length; i++)
        {
            double sum = 0;
            foreach (ref readonly var light in lights)
                sum += Contribution(light, points[i], n);
            results[i] = (float)sum;
        }
    }

    /// <summary>
    /// Illuminance on a regular grid of a plane: point (u, v) = <paramref name="origin"/> + u·<paramref name="stepU"/> +
    /// v·<paramref name="stepV"/>, surface normal normalize(stepU × stepV); results are row-major
    /// (<c>results[v * countU + u]</c>), lx.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A count is negative.</exception>
    /// <exception cref="ArgumentException"><paramref name="results"/> has fewer than countU·countV entries.</exception>
    public static void IlluminanceOnGrid(ReadOnlySpan<SpotLight> lights, Vector3 origin, Vector3 stepU, Vector3 stepV, int countU, int countV,
        Span<float> results)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(countU);
        ArgumentOutOfRangeException.ThrowIfNegative(countV);
        if (results.Length < (long)countU * countV)
            throw new ArgumentException("The result span is smaller than the grid.", nameof(results));
        var n = NormalizeOrZero(Vector3.Cross(stepU, stepV));
        for (int v = 0; v < countV; v++)
        {
            for (int u = 0; u < countU; u++)
            {
                var p = origin + u * stepU + v * stepV;
                double sum = 0;
                foreach (ref readonly var light in lights)
                    sum += Contribution(light, p, n);
                results[v * countU + u] = (float)sum;
            }
        }
    }

    private static float Contribution(in SpotLight light, Vector3 point, Vector3 unitNormal)
    {
        if (light.PeakCandela <= 0f)
            return 0f;
        var toPoint = point - light.Position;
        float d = toPoint.Length();
        if (!(d > 0f))
            return 0f;
        var dir = toPoint / d;
        float cosTheta = Math.Clamp(Vector3.Dot(light.Direction, dir), -1f, 1f);
        float relative = light.Distribution.Relative(MathF.Acos(cosTheta));
        if (relative <= 0f)
            return 0f;
        float cosIncidence = unitNormal == Vector3.Zero ? 1f : -Vector3.Dot(unitNormal, dir);
        return Photometrics.Illuminance(light.PeakCandela * relative, d, cosIncidence);
    }

    private static Vector3 NormalizeOrZero(Vector3 v)
    {
        float len = v.Length();
        return len > 1e-12f && float.IsFinite(len) ? v / len : Vector3.Zero;
    }
}
