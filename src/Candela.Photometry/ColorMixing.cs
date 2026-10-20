using System.Numerics;

namespace Candela.Photometry;

/// <summary>Result of additive emitter mixing.</summary>
/// <param name="Color">Colour of the mixed light.</param>
/// <param name="Flux">Total luminous flux, lm.</param>
/// <param name="Tristimulus">Tristimulus values of the mix scaled in lumens (Y = <paramref name="Flux"/>).</param>
public readonly record struct MixResult(LightColor Color, float Flux, Vector3 Tristimulus)
{
    /// <summary>CIE 1931 chromaticity (x, y) of the mix, computed before any gamut mapping; D65 when dark.</summary>
    public Vector2 Chromaticity
    {
        get
        {
            float sum = Tristimulus.X + Tristimulus.Y + Tristimulus.Z;
            return sum > 0f ? new Vector2(Tristimulus.X / sum, Tristimulus.Y / sum) : ColorSpaces.D65WhiteXy;
        }
    }
}

/// <summary>
/// Additive colour mixing of a fixture's emitters (RGB, RGBW, RGBA, RGBAL, … LED engines, tunable whites).
/// </summary>
/// <remarks>
/// Mixing is done in CIE XYZ, which is linear in radiant power: the tristimulus vector of each emitter, scaled by
/// the flux it emits (level × full-level flux), is summed; the total Y is the total luminous flux and the
/// chromaticity of the sum is the colour of the mix (Grassmann's law). Mixing in display RGB after gamut clipping would
/// be wrong for saturated LEDs, so it is never done here. Levels are linear light output 0..1 — DMX curves (dimmer
/// laws, 8/16-bit) are applied by the caller.
/// </remarks>
public static class ColorMixing
{
    /// <summary>
    /// Mixes emitters at the given levels. Missing levels (shorter span) count as 0; levels are clamped to 0..1 and
    /// NaN counts as 0. Allocation-free.
    /// </summary>
    /// <param name="emitters">The emitters of one fixture (or one pixel).</param>
    /// <param name="levels">Linear output level 0..1 per emitter, same order.</param>
    /// <returns>Mixed colour, total flux (lm) and tristimulus values; <see cref="LightColor.White"/> with 0 lm when dark.</returns>
    public static MixResult Mix(ReadOnlySpan<Emitter> emitters, ReadOnlySpan<float> levels)
    {
        double x = 0, y = 0, z = 0;
        for (int i = 0; i < emitters.Length; i++)
        {
            float level = i < levels.Length ? levels[i] : 0f;
            if (!(level > 0f))
                continue;
            double flux = MathF.Min(level, 1f) * emitters[i].MaxFlux;
            var t = emitters[i].TristimulusPerLumen;
            x += flux * t.X;
            y += flux * t.Y;
            z += flux * t.Z;
        }

        var xyz = new Vector3((float)x, (float)y, (float)z);
        return y > 0 ? new MixResult(LightColor.FromXyz(xyz), (float)y, xyz) : new MixResult(LightColor.White, 0f, Vector3.Zero);
    }

    /// <summary>All emitters at full level — the fixture's nominal "all on" output.</summary>
    public static MixResult MixAllFull(ReadOnlySpan<Emitter> emitters)
    {
        double x = 0, y = 0, z = 0;
        foreach (var e in emitters)
        {
            var t = e.TristimulusPerLumen;
            x += e.MaxFlux * t.X;
            y += e.MaxFlux * t.Y;
            z += e.MaxFlux * t.Z;
        }

        var xyz = new Vector3((float)x, (float)y, (float)z);
        return y > 0 ? new MixResult(LightColor.FromXyz(xyz), (float)y, xyz) : new MixResult(LightColor.White, 0f, Vector3.Zero);
    }

    /// <summary>
    /// Levels (0..1, the largest equal to 1) at which three emitters mix to <paramref name="targetXy"/> with the
    /// highest possible flux — e.g. the white point of an RGB engine or a virtual colour temperature.
    /// </summary>
    /// <returns>
    /// False when the target lies outside the triangle spanned by the three emitter chromaticities, when the emitters
    /// are degenerate (collinear) or when an emitter that is needed has no flux.
    /// </returns>
    public static bool TrySolveLevels(in Emitter a, in Emitter b, in Emitter c, Vector2 targetXy, out Vector3 levels)
    {
        levels = Vector3.Zero;
        if (!float.IsFinite(targetXy.X) || !float.IsFinite(targetXy.Y) || targetXy.Y <= 0f)
            return false;
        var ta = a.TristimulusPerLumen;
        var tb = b.TristimulusPerLumen;
        var tc = c.TristimulusPerLumen;
        double[] m = [ta.X, tb.X, tc.X, ta.Y, tb.Y, tc.Y, ta.Z, tb.Z, tc.Z];
        double[] inverse;
        try
        {
            inverse = ColorMatrix3.Invert(m);
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        double tx = targetXy.X / (double)targetXy.Y, tz = (1.0 - targetXy.X - targetXy.Y) / targetXy.Y;

        // Lumens of each emitter per lumen of mix.
        double wa = inverse[0] * tx + inverse[1] + inverse[2] * tz;
        double wb = inverse[3] * tx + inverse[4] + inverse[5] * tz;
        double wc = inverse[6] * tx + inverse[7] + inverse[8] * tz;
        const double tolerance = -1e-9;
        if (wa < tolerance || wb < tolerance || wc < tolerance)
            return false;
        wa = Math.Max(wa, 0);
        wb = Math.Max(wb, 0);
        wc = Math.Max(wc, 0);
        if ((wa > 0 && a.MaxFlux <= 0f) || (wb > 0 && b.MaxFlux <= 0f) || (wc > 0 && c.MaxFlux <= 0f))
            return false;
        double la = wa > 0 ? wa / a.MaxFlux : 0, lb = wb > 0 ? wb / b.MaxFlux : 0, lc = wc > 0 ? wc / c.MaxFlux : 0;
        double max = Math.Max(la, Math.Max(lb, lc));
        if (!(max > 0))
            return false;
        levels = new Vector3((float)(la / max), (float)(lb / max), (float)(lc / max));
        return true;
    }

    /// <summary>
    /// Re-balances an engine's emitter outputs so that all emitters at full mix to <paramref name="targetXy"/>, changing
    /// them as little as possible: positive factors kᵢ with the least Σ (ln kᵢ)² (a calibration trims every emitter a
    /// little rather than switching one off), then scaled together so the total flux stays Σ MaxFlux. Solved by
    /// minimum-norm Gauss–Newton steps on ln kᵢ against the two chromaticity conditions Σ kᵢ·Fᵢ·(Xᵢ/Yᵢ − X/Y) = 0 and
    /// Σ kᵢ·Fᵢ·(Zᵢ/Yᵢ − Z/Y) = 0; a handful of iterations for real engines. Where that iteration does not settle (it can
    /// cycle when one emitter's output dwarfs the others'), the factors are the least change Σ (kᵢ − 1)² that meets
    /// the conditions and keeps the total flux, with every factor at least <see cref="MinBalanceFactor"/> (an active
    /// set over equality-constrained least squares). Allocation-free for up to 16 emitters.
    /// </summary>
    /// <param name="emitters">The engine's emitters at their nominal full-level flux.</param>
    /// <param name="targetXy">The white (or any chromaticity) the full mix should make.</param>
    /// <param name="factors">Receives kᵢ per emitter (same length as <paramref name="emitters"/>).</param>
    /// <returns>False when the target lies outside the emitters' gamut, fewer than two emitters have flux, or the input is invalid.</returns>
    public static bool TryBalance(ReadOnlySpan<Emitter> emitters, Vector2 targetXy, Span<float> factors)
    {
        int n = emitters.Length;
        if (factors.Length < n || n < 2 || n > 16 || !float.IsFinite(targetXy.X) || !float.IsFinite(targetXy.Y) || !(targetXy.Y > 0f))
            return false;
        double ta = targetXy.X / (double)targetXy.Y, tc = (1.0 - targetXy.X - targetXy.Y) / targetXy.Y;
        Span<double> p = stackalloc double[n];
        Span<double> q = stackalloc double[n];
        Span<double> x = stackalloc double[n];
        double total = 0;
        int lit = 0;
        for (int i = 0; i < n; i++)
        {
            double f = emitters[i].MaxFlux;
            var t = emitters[i].TristimulusPerLumen;
            p[i] = f * (t.X - ta);
            q[i] = f * (t.Z - tc);
            total += f;
            if (f > 0) lit++;
        }
        if (lit < 2 || !(total > 0)) return false;

        for (int iteration = 0; iteration < 200; iteration++)
        {
            // residual and Jacobian rows (∂/∂xᵢ of Σ e^xᵢ pᵢ and Σ e^xᵢ qᵢ)
            double r1 = 0, r2 = 0, j11 = 0, j12 = 0, j22 = 0, jx1 = 0, jx2 = 0, flux = 0;
            for (int i = 0; i < n; i++)
            {
                double k = Math.Exp(x[i]);
                flux += k * emitters[i].MaxFlux;
                double u = k * p[i], v = k * q[i];
                r1 += u;
                r2 += v;
                j11 += u * u;
                j12 += u * v;
                j22 += v * v;
                jx1 += u * x[i];
                jx2 += v * x[i];
            }
            // converged relative to the current mix: the conditions are homogeneous in k, so shrinking every factor
            // together would shrink the residual without reaching the target (two emitters off the line through it)
            double scale = Math.Max(Math.Abs(r1), Math.Abs(r2));
            if (scale <= 1e-10 * flux) break;
            if (iteration == 199) return TryBalanceLeastChange(emitters, p, q, total, factors);
            // x ← Jᵀ (J Jᵀ)⁻¹ (J x − r): the smallest x that meets the linearised conditions
            double det = j11 * j22 - j12 * j12;
            if (!(Math.Abs(det) > 1e-300)) return TryBalanceLeastChange(emitters, p, q, total, factors);
            double b1 = jx1 - r1, b2 = jx2 - r2;
            double l1 = (j22 * b1 - j12 * b2) / det, l2 = (j11 * b2 - j12 * b1) / det;
            for (int i = 0; i < n; i++)
            {
                double k = Math.Exp(x[i]);
                double next = k * p[i] * l1 + k * q[i] * l2;
                // damped: a step of more than e² in one factor is taken in part, the gamut edge is approached slowly
                x[i] += Math.Clamp(next - x[i], -2.0, 2.0);
                if (!double.IsFinite(x[i]) || Math.Abs(x[i]) > 30) return TryBalanceLeastChange(emitters, p, q, total, factors);
            }
        }

        double mixed = 0;
        for (int i = 0; i < n; i++) mixed += Math.Exp(x[i]) * emitters[i].MaxFlux;
        if (!(mixed > 0)) return false;
        double norm = total / mixed;
        for (int i = 0; i < n; i++) factors[i] = (float)(Math.Exp(x[i]) * norm);
        return true;
    }

    /// <summary>Smallest factor <see cref="TryBalance"/> gives an emitter when it falls back to the least change: every emitter stays lit.</summary>
    public const float MinBalanceFactor = 1e-3f;

    /// <summary>
    /// The fallback of <see cref="TryBalance"/>: kᵢ ≥ <see cref="MinBalanceFactor"/> with the least Σ (kᵢ − 1)² under the
    /// linear conditions Σ kᵢ pᵢ = 0, Σ kᵢ qᵢ = 0 (chromaticity, <paramref name="p"/> and <paramref name="q"/> as in
    /// <see cref="TryBalance"/>) and Σ kᵢ Fᵢ = Σ Fᵢ (flux) — a strictly convex quadratic programme. Its solution is the
    /// equality-constrained least change of some set of emitters held at the floor, so every such set is tried (at most
    /// 2¹⁶ small 3 × 3 solves, once per beam while a profile is compiled) and the feasible one with the least change
    /// kept; false when none is feasible (a target outside the gamut).
    /// </summary>
    private static bool TryBalanceLeastChange(ReadOnlySpan<Emitter> emitters, ReadOnlySpan<double> p, ReadOnlySpan<double> q, double total,
        Span<float> factors)
    {
        int n = emitters.Length;
        Span<double> k = stackalloc double[n];
        Span<double> best = stackalloc double[n];
        double bestChange = double.PositiveInfinity;
        for (int pinned = 0; pinned < 1 << n; pinned++)
        {
            // conditions on the free factors: M k = c − (pinned factors' share), M rows p, q, F
            double c1 = 0, c2 = 0, c3 = total;
            double a11 = 0, a12 = 0, a13 = 0, a22 = 0, a23 = 0, a33 = 0, m1 = 0, m2 = 0, m3 = 0;
            int free = 0;
            for (int i = 0; i < n; i++)
            {
                double f = emitters[i].MaxFlux;
                if ((pinned & (1 << i)) != 0)
                {
                    c1 -= MinBalanceFactor * p[i];
                    c2 -= MinBalanceFactor * q[i];
                    c3 -= MinBalanceFactor * f;
                    continue;
                }
                free++;
                a11 += p[i] * p[i];
                a12 += p[i] * q[i];
                a13 += p[i] * f;
                a22 += q[i] * q[i];
                a23 += q[i] * f;
                a33 += f * f;
                m1 += p[i];
                m2 += q[i];
                m3 += f;
            }
            if (free < 3) continue;
            // k = 1 + Mᵀ λ with (M Mᵀ) λ = c − M·1 (symmetric 3 × 3, by cofactors)
            double b1 = c1 - m1, b2 = c2 - m2, b3 = c3 - m3;
            double d11 = a22 * a33 - a23 * a23, d12 = a13 * a23 - a12 * a33, d13 = a12 * a23 - a13 * a22;
            double det = a11 * d11 + a12 * d12 + a13 * d13;
            if (!double.IsFinite(det) || !(Math.Abs(det) > 1e-12 * Math.Abs(a11 * a22 * a33)) || !(Math.Abs(det) > 1e-300)) continue;
            double d22 = a11 * a33 - a13 * a13, d23 = a12 * a13 - a11 * a23, d33 = a11 * a22 - a12 * a12;
            double l1 = (d11 * b1 + d12 * b2 + d13 * b3) / det;
            double l2 = (d12 * b1 + d22 * b2 + d23 * b3) / det;
            double l3 = (d13 * b1 + d23 * b2 + d33 * b3) / det;
            double change = 0;
            bool feasible = true;
            for (int i = 0; i < n && feasible; i++)
            {
                k[i] = (pinned & (1 << i)) != 0 ? MinBalanceFactor : 1.0 + p[i] * l1 + q[i] * l2 + emitters[i].MaxFlux * l3;
                feasible = double.IsFinite(k[i]) && k[i] >= MinBalanceFactor * (1 - 1e-9);
                change += (k[i] - 1) * (k[i] - 1);
            }
            if (!feasible || !(change < bestChange)) continue;
            bestChange = change;
            k.CopyTo(best);
        }
        if (double.IsPositiveInfinity(bestChange)) return false;

        // the conditions hold to rounding: check the mix before handing the factors out
        double r1 = 0, r2 = 0, flux = 0;
        for (int i = 0; i < n; i++)
        {
            r1 += best[i] * p[i];
            r2 += best[i] * q[i];
            flux += best[i] * emitters[i].MaxFlux;
        }
        if (!(flux > 0) || Math.Max(Math.Abs(r1), Math.Abs(r2)) > 1e-6 * flux) return false;
        for (int i = 0; i < n; i++) factors[i] = (float)best[i];
        return true;
    }

    /// <summary>
    /// Spectral power distribution (W/nm) of the mix, for spectral filtering of an LED engine. Each contributing
    /// emitter's relative spectrum is scaled so that its luminous flux equals level × full-level flux.
    /// </summary>
    /// <returns>Null when nothing is lit or a contributing emitter has no spectrum.</returns>
    public static Spectrum? MixSpectrum(ReadOnlySpan<Emitter> emitters, ReadOnlySpan<float> levels)
    {
        Spectrum? sum = null;
        for (int i = 0; i < emitters.Length; i++)
        {
            float level = i < levels.Length ? levels[i] : 0f;
            if (!(level > 0f) || emitters[i].MaxFlux <= 0f)
                continue;
            var spectrum = emitters[i].Spectrum;
            if (spectrum is null)
                return null;
            double lumensPerUnit = Cie1931.PhotopicLuminousFlux(spectrum);
            if (!(lumensPerUnit > 0))
                continue;
            var scaled = spectrum.Scale((float)(MathF.Min(level, 1f) * emitters[i].MaxFlux / lumensPerUnit));
            sum = sum is null ? scaled : sum.Add(scaled);
        }

        return sum;
    }
}
