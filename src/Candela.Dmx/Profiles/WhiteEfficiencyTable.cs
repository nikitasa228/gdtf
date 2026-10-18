using Candela.Photometry;
using PhotometryEmitter = Candela.Photometry.Emitter;

namespace Candela.Dmx;

/// <summary>
/// How much white an additive LED engine can make at each colour temperature: the largest luminous flux its emitters
/// produce at a chromaticity on the Planckian locus, relative to the engine's brightest white (1 = brightest). Used when
/// CTC/CTO/CTB move the white point electronically (the engine re-balances its emitters instead of filtering) and for
/// the white layer of fixtures that cross-fade between a CCT engine and a colour engine.
/// </summary>
/// <remarks>
/// The maximum is a small linear program — maximise Σ gᵢ subject to Σ gᵢ·(Xᵢ/Yᵢ − X/Y) = 0, Σ gᵢ·(Zᵢ/Yᵢ − Z/Y) = 0 and
/// 0 ≤ gᵢ ≤ Fᵢ (gᵢ the flux of emitter i, Fᵢ its maximum) — solved exactly by enumerating its vertices (at most two
/// emitters strictly between off and full), once per profile on a 10-mired grid from 1000 K to 25000 K.
/// </remarks>
internal sealed class WhiteEfficiencyTable
{
    private const float MinMired = 40f;   // 25000 K
    private const float MaxMired = 1000f; // 1000 K
    private const float Step = 10f;
    private const int MaxEmitters = 9;

    /// <summary>Least efficiency reported: colour temperatures outside the emitters' gamut still give a dim, not a black, white.</summary>
    public const float Floor = 0.05f;

    private readonly float[] _values;

    private WhiteEfficiencyTable(float[] values) => _values = values;

    /// <summary>Builds the table for a beam's emitters; null when there are none (efficiency is then 1 everywhere).</summary>
    public static WhiteEfficiencyTable? Create(ReadOnlySpan<PhotometryEmitter> emitters)
    {
        var usable = new List<PhotometryEmitter>();
        foreach (var e in emitters)
            if (e.MaxFlux > 0f && float.IsFinite(e.MaxFlux) && float.IsFinite(e.TristimulusPerLumen.X + e.TristimulusPerLumen.Z)) usable.Add(e);
        if (usable.Count == 0) return null;
        if (usable.Count > MaxEmitters) usable = usable.OrderByDescending(e => e.MaxFlux).Take(MaxEmitters).ToList();
        var a = usable.Select(e => (double)e.TristimulusPerLumen.X).ToArray();
        var c = usable.Select(e => (double)e.TristimulusPerLumen.Z).ToArray();
        var f = usable.Select(e => (double)e.MaxFlux).ToArray();

        int count = (int)((MaxMired - MinMired) / Step) + 1;
        var values = new float[count];
        double best = 0;
        for (int i = 0; i < count; i++)
        {
            var xy = Cct.XyFromCct(1e6f / (MinMired + i * Step));
            double flux = MaxFlux(a, c, f, xy.X / xy.Y, (1.0 - xy.X - xy.Y) / xy.Y);
            values[i] = (float)flux;
            best = Math.Max(best, flux);
        }
        if (!(best > 0)) return null;
        for (int i = 0; i < count; i++) values[i] = Math.Max(Floor, (float)(values[i] / best));
        return new WhiteEfficiencyTable(values);
    }

    /// <summary>Efficiency (0 … 1, ≥ <see cref="Floor"/>) at <paramref name="kelvin"/>, interpolated in mired.</summary>
    public float At(float kelvin)
    {
        if (!(kelvin > 0f) || !float.IsFinite(kelvin)) return 1f;
        float pos = (Math.Clamp(1e6f / kelvin, MinMired, MaxMired) - MinMired) / Step;
        int i = Math.Min((int)pos, _values.Length - 2);
        float t = pos - i;
        return _values[i] + (_values[i + 1] - _values[i]) * t;
    }

    /// <summary>
    /// Largest Σ gᵢ with Σ gᵢ·(aᵢ − ta) = 0, Σ gᵢ·(cᵢ − tc) = 0, 0 ≤ gᵢ ≤ fᵢ, where a = X/Y and c = Z/Y of each emitter
    /// and of the target. 0 when the target is outside the emitters' gamut.
    /// </summary>
    internal static double MaxFlux(double[] a, double[] c, double[] f, double ta, double tc)
    {
        int n = f.Length;
        Span<double> u = stackalloc double[n];
        Span<double> v = stackalloc double[n];
        double scale = 0;
        for (int i = 0; i < n; i++)
        {
            u[i] = a[i] - ta;
            v[i] = c[i] - tc;
            scale = Math.Max(scale, Math.Max(Math.Abs(u[i]), Math.Abs(v[i])) * f[i]);
        }
        double tol = 1e-9 * Math.Max(scale, 1e-12);
        double best = 0;

        // an emitter exactly at the target
        for (int i = 0; i < n; i++)
            if (Math.Abs(u[i]) * f[i] <= tol && Math.Abs(v[i]) * f[i] <= tol) best = Math.Max(best, f[i]);

        // vertices: two basic emitters, every other one off or full
        for (int j = 0; j < n; j++)
        {
            for (int k = j + 1; k < n; k++)
            {
                double det = u[j] * v[k] - u[k] * v[j];
                if (Math.Abs(det) < 1e-15) continue;
                int others = n - 2;
                for (int mask = 0; mask < 1 << others; mask++)
                {
                    double su = 0, sv = 0, sum = 0;
                    for (int i = 0, bit = 0; i < n; i++)
                    {
                        if (i == j || i == k) continue;
                        if ((mask >> bit++ & 1) == 0) continue;
                        su += f[i] * u[i];
                        sv += f[i] * v[i];
                        sum += f[i];
                    }
                    double gj = (-su * v[k] + sv * u[k]) / det;
                    double gk = (-sv * u[j] + su * v[j]) / det;
                    double slackJ = 1e-9 * f[j], slackK = 1e-9 * f[k];
                    if (gj < -slackJ || gj > f[j] + slackJ || gk < -slackK || gk > f[k] + slackK) continue;
                    best = Math.Max(best, sum + Math.Clamp(gj, 0, f[j]) + Math.Clamp(gk, 0, f[k]));
                }
            }
        }
        return best;
    }
}
