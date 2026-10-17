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
