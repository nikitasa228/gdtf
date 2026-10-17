using System.Numerics;

namespace Candela.Photometry;

/// <summary>A point noise source (fixture fan, hazer, fog machine): position (m) and sound pressure level at 1 m, dB(A).</summary>
/// <param name="Position">Source position, m.</param>
/// <param name="LevelAt1MetreDb">Sound pressure level measured at 1 m, dB(A) (the <c>_specs</c> <c>noiseLevelDb</c> value).</param>
public readonly record struct SoundSource(Vector3 Position, float LevelAt1MetreDb);

/// <summary>
/// Free-field sound pressure levels for the noise overlay: spherical spreading from point sources
/// (L(d) = L₁ − 20·log₁₀ d) and energetic (incoherent) summation of sources, L = 10·log₁₀ Σ 10^(Lᵢ/10).
/// No reflections, absorption or directivity.
/// </summary>
public static class Acoustics
{
    /// <summary>Distances below this (m) are clamped: the point-source model does not hold inside the near field.</summary>
    public const float MinDistance = 0.1f;

    /// <summary>Level of silence. Energetic sums of nothing, and of only silent sources, are −∞ dB.</summary>
    public const float Silence = float.NegativeInfinity;

    /// <summary>
    /// SPL at <paramref name="distanceMetres"/> from a point source of <paramref name="levelAt1MetreDb"/> at 1 m, in a
    /// free field: L₁ − 20·log₁₀(d), with d clamped to ≥ <see cref="MinDistance"/> (so at most L₁ + 20 dB). Non-finite
    /// input gives <see cref="Silence"/>.
    /// </summary>
    public static float SplAtDistance(float levelAt1MetreDb, float distanceMetres)
    {
        if (!float.IsFinite(levelAt1MetreDb) || float.IsNaN(distanceMetres))
            return Silence;
        float d = MathF.Max(distanceMetres, MinDistance);
        return float.IsPositiveInfinity(d) ? Silence : levelAt1MetreDb - 20f * MathF.Log10(d);
    }

    /// <summary>Energetic sum of levels in dB: 10·log₁₀ Σ 10^(Lᵢ/10). Two equal sources add 3.01 dB. NaN entries are ignored.</summary>
    public static float EnergeticSum(params float[] levelsDb)
    {
        ArgumentNullException.ThrowIfNull(levelsDb);
        return EnergeticSum(levelsDb.AsSpan());
    }

    /// <summary>Energetic sum of levels in dB; see <see cref="EnergeticSum(float[])"/>.</summary>
    public static float EnergeticSum(ReadOnlySpan<float> levelsDb)
    {
        // Factor out the loudest level so 10^(L/10) cannot overflow for any realistic or unrealistic input.
        float max = float.NegativeInfinity;
        foreach (float l in levelsDb)
        {
            if (!float.IsNaN(l))
                max = MathF.Max(max, l);
        }

        if (float.IsNegativeInfinity(max))
            return Silence;
        if (float.IsPositiveInfinity(max))
            return max;
        double sum = 0;
        foreach (float l in levelsDb)
        {
            if (!float.IsNaN(l))
                sum += Math.Pow(10.0, (l - max) / 10.0);
        }

        return (float)(max + 10.0 * Math.Log10(sum));
    }

    /// <summary>
    /// Total SPL at <paramref name="point"/> from all <paramref name="sources"/>, dB(A): the energetic sum of each
    /// source's free-field level at its distance. Sources with a non-finite level are ignored; no sources (or only
    /// ignored ones) give <see cref="Silence"/>. Single pass and allocation-free.
    /// </summary>
    public static float SplAt(Vector3 point, ReadOnlySpan<SoundSource> sources)
    {
        // Streaming form of the overflow-safe sum: running maximum M and Σ 10^((Lᵢ − M)/10), rescaled when M grows,
        // so each source costs one distance, one logarithm and one power.
        float max = float.NegativeInfinity;
        double sum = 0;
        foreach (var s in sources)
        {
            float level = SplAtDistance(s.LevelAt1MetreDb, Vector3.Distance(point, s.Position));
            if (!float.IsFinite(level))
                continue;
            if (level > max)
            {
                sum = sum * Math.Pow(10.0, (max - level) / 10.0) + 1.0;
                max = level;
            }
            else
            {
                sum += Math.Pow(10.0, (level - max) / 10.0);
            }
        }

        return float.IsNegativeInfinity(max) ? Silence : (float)(max + 10.0 * Math.Log10(sum));
    }
}
