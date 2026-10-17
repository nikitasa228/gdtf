using System.Numerics;
using Candela.Core.SceneGraph.Components;

namespace Candela.Photometry.Tests;

public class ColorMixingTests
{
    private static readonly Vector2 D65 = new(0.3127f, 0.3290f);

    [Fact]
    public void PureRgbLeds_AtProperRatios_ReachD65()
    {
        // Typical stage LEDs as found in GDTF emitter data.
        var red = Emitter.FromXy("Red", 0.7017f, 0.2969f, 900f);
        var green = Emitter.FromXy("Green", 0.1564f, 0.6983f, 1500f);
        var blue = Emitter.FromXy("Blue", 0.1525f, 0.0268f, 200f);
        Assert.True(ColorMixing.TrySolveLevels(red, green, blue, D65, out var levels));
        Assert.Equal(1f, MathF.Max(levels.X, MathF.Max(levels.Y, levels.Z)), 1e-6);

        var mix = ColorMixing.Mix([red, green, blue], [levels.X, levels.Y, levels.Z]);
        Assert.True(Vector2.Distance(D65, mix.Chromaticity) < 0.003f, mix.ToString());
        Assert.True(Vector2.Distance(D65, mix.Chromaticity) < 1e-4f, mix.ToString());
        Assert.True(Vector2.Distance(D65, mix.Color.ToXy()) < 1e-4f);
        Assert.Equal(levels.X * 900f + levels.Y * 1500f + levels.Z * 200f, mix.Flux, 1e-2);
    }

    [Fact]
    public void StandardRgbSet_AtFullIsD65()
    {
        var rgb = StandardEmitters.Rgb(1000f);
        Assert.Equal(3, rgb.Length);
        var mix = ColorMixing.MixAllFull(rgb);
        Assert.Equal(1000.0, mix.Flux, 0.01);
        Assert.True(Vector2.Distance(D65, mix.Chromaticity) < 0.003f, mix.ToString());
    }

    [Fact]
    public void Mixing_IsDoneInXyzWeightedByFlux()
    {
        var a = Emitter.FromXy("A", 0.15f, 0.03f, 100f);  // deep blue, outside Rec.2020
        var b = Emitter.FromXy("B", 0.70f, 0.30f, 400f);  // red
        var mix = ColorMixing.Mix([a, b], [0.5f, 0.25f]);

        // Grassmann: X = Σ Φ·x/y, Y = Σ Φ, Z = Σ Φ·(1−x−y)/y.
        double fa = 50, fb = 100;
        double x = fa * 0.15 / 0.03 + fb * 0.70 / 0.30;
        double y = fa + fb;
        double z = fa * 0.82 / 0.03 + fb * 0.0 / 0.30;
        double sum = x + y + z;
        Assert.Equal(150.0, mix.Flux, 1e-3);
        Assert.Equal(x / sum, mix.Chromaticity.X, 1e-5);
        Assert.Equal(y / sum, mix.Chromaticity.Y, 1e-5);

        // An RGB average of the gamut-clipped colours would land elsewhere.
        var rgbMix = LightColor.Mix(a.Color, 50f, b.Color, 100f).ToXy();
        Assert.True(Vector2.Distance(rgbMix, mix.Chromaticity) > 0.01f);
    }

    [Fact]
    public void Levels_AreClampedAndMissingLevelsAreZero()
    {
        var e = Emitter.FromXy("W", 0.3127f, 0.329f, 100f);
        Assert.Equal(100f, ColorMixing.Mix([e], [3f]).Flux, 1e-4);
        Assert.Equal(0f, ColorMixing.Mix([e], [-1f]).Flux);
        Assert.Equal(0f, ColorMixing.Mix([e], [float.NaN]).Flux);
        Assert.Equal(0f, ColorMixing.Mix([e, e], ReadOnlySpan<float>.Empty).Flux);
        var dark = ColorMixing.Mix([e], [0f]);
        Assert.Equal(LightColor.White, dark.Color);
        Assert.Equal(Vector3.Zero, dark.Tristimulus);
        Assert.Equal(D65, dark.Chromaticity);
    }

    [Fact]
    public void Emitter_SanitisesBadData()
    {
        var bad = Emitter.FromXy("bad", float.NaN, 0.3f, float.NaN);
        Assert.Equal(ColorSpaces.D65WhiteXy, bad.Chromaticity);
        Assert.Equal(0f, bad.MaxFlux);
        var negative = Emitter.FromXy("neg", 0.3f, -0.2f, -5f);
        Assert.Equal(ColorSpaces.D65WhiteXy, negative.Chromaticity);
        var clamped = Emitter.FromXy("clamped", 0.9f, 0.5f, 10f);
        Assert.True(clamped.Chromaticity.X + clamped.Chromaticity.Y <= 1f + 1e-6f);
        Assert.Equal(20f, clamped.WithMaxFlux(20f).MaxFlux);
        Assert.Equal("", default(Emitter).Name);
        Assert.Equal(0f, ColorMixing.Mix([default(Emitter)], [1f]).Flux);
    }

    [Fact]
    public void FromXyY_TakesTheGdtfShareOfBeamFlux()
    {
        var red = Emitter.FromXyY("Red", new CieColor(0.7017f, 0.2969f, 26.5f), 2000f);
        Assert.Equal(530.0, red.MaxFlux, 1e-3);
        Assert.Equal(0.7017f, red.Chromaticity.X);
        Assert.Equal(2000f, Emitter.FromXyY("x", new CieColor(0.3f, 0.3f, float.NaN), 2000f).MaxFlux);
        Assert.Equal(0f, Emitter.FromXyY("x", new CieColor(0.3f, 0.3f, -4f), 2000f).MaxFlux);

        int count = 0;
        foreach (var (file, raw, x, y, bigY) in RepoData.Colors("Emitter"))
        {
            var e = Emitter.FromXyY("e", new CieColor(x, y, bigY), 1000f);
            Assert.True(float.IsFinite(e.MaxFlux) && e.MaxFlux >= 0f, $"{file} {raw}");
            Assert.True(float.IsFinite(e.TristimulusPerLumen.X + e.TristimulusPerLumen.Z), $"{file} {raw}");
            count++;
        }

        Assert.True(count > 500, $"only {count} emitters");
    }

    [Fact]
    public void TrySolveLevels_RejectsTargetsOutsideTheTriangle()
    {
        var rgb = StandardEmitters.Rgb(1000f);
        Assert.False(ColorMixing.TrySolveLevels(rgb[0], rgb[1], rgb[2], new Vector2(0.05f, 0.8f), out _));
        Assert.False(ColorMixing.TrySolveLevels(rgb[0], rgb[1], rgb[2], new Vector2(float.NaN, 0.3f), out _));
        var same = Emitter.FromXy("x", 0.3f, 0.3f, 1f);
        Assert.False(ColorMixing.TrySolveLevels(same, same, same, D65, out _));
        Assert.True(ColorMixing.TrySolveLevels(rgb[0], rgb[1], rgb[2], Cct.PlanckianXy(3000f), out var warm));
        Assert.True(warm.X > warm.Z);
    }

    [Fact]
    public void MixSpectrum_PreservesFluxAndChromaticity()
    {
        var red = Emitter.FromSpectrum("Red", Spectrum.Gaussian(630f, 18f), 500f);
        var green = Emitter.FromSpectrum("Green", Spectrum.Gaussian(525f, 32f), 800f);
        var blue = Emitter.FromSpectrum("Blue", Spectrum.Gaussian(455f, 22f), 100f);
        float[] levels = [1f, 0.5f, 0.75f];
        var spectrum = ColorMixing.MixSpectrum([red, green, blue], levels);
        Assert.NotNull(spectrum);
        var mix = ColorMixing.Mix([red, green, blue], levels);
        Assert.Equal(mix.Flux, Cie1931.PhotopicLuminousFlux(spectrum), mix.Flux * 1e-4);
        Assert.True(Vector2.Distance(mix.Chromaticity, Cie1931.ChromaticityFromSpectrum(spectrum)) < 1e-4f);

        var noSpectrum = Emitter.FromXy("W", 0.31f, 0.33f, 100f);
        Assert.Null(ColorMixing.MixSpectrum([red, noSpectrum], [1f, 1f]));
        Assert.Null(ColorMixing.MixSpectrum([red], [0f]));
    }

    [Fact]
    public void FromSpectralPower_ComputesFluxFromWatts()
    {
        // 1 W/nm flat over 550–560 nm (3 samples at 5 nm = 15 W·nm-weighted).
        var p = new Spectrum(550f, 5f, [1f, 1f, 1f]);
        var e = Emitter.FromSpectralPower("flat", p);
        double expected = 683.002 * 5 * (Cie1931.YBar[38] + Cie1931.YBar[39] + Cie1931.YBar[40]);
        Assert.Equal(expected, e.MaxFlux, expected * 1e-5);
    }

    [Theory]
    [InlineData("RGBW", new[] { "R", "G", "B", "W" })]
    [InlineData("RGB+WW", new[] { "R", "G", "B", "WW" })]
    [InlineData("RGBWA+UV", new[] { "R", "G", "B", "W", "A", "UV" })]
    [InlineData("RGBALC", new[] { "R", "G", "B", "A", "L", "C" })]
    [InlineData("RGBWA+BG+UV", new[] { "R", "G", "B", "W", "A", "BG", "UV" })]
    [InlineData("B+WW+CW", new[] { "B", "WW", "CW" })]
    [InlineData("Tunable White (CW/WW)", new[] { "CW", "WW" })]
    [InlineData("Tunable White", new[] { "CW", "WW" })]
    [InlineData("RGBLCMY", new[] { "R", "G", "B", "L" })]
    [InlineData("rgbw", new[] { "R", "G", "B", "W" })]
    [InlineData("CMY", new string[0])]
    [InlineData("None", new string[0])]
    [InlineData("", new string[0])]
    [InlineData(null, new string[0])]
    public void FromColorMixing_ParsesSpecsCodes(string? code, string[] expected)
    {
        var unrecognized = new List<string>();
        var emitters = StandardEmitters.FromColorMixing(code, 1000f, unrecognized);
        Assert.Empty(unrecognized);
        var names = emitters.Select(e => StandardEmitters.Catalog.First(c => c.Name == e.Name).Code).ToArray();
        Assert.Equal(expected, names);
        if (emitters.Length > 0)
            Assert.Equal(1000.0, emitters.Sum(e => e.MaxFlux), 0.01);
    }

    [Fact]
    public void FromColorMixing_ReportsUnknownCodes()
    {
        var unrecognized = new List<string>();
        var emitters = StandardEmitters.FromColorMixing("RGBX+QQ", 100f, unrecognized);
        Assert.Equal(3, emitters.Length);
        Assert.Equal(["X", "Q", "Q"], unrecognized);
        Assert.DoesNotContain(StandardEmitters.FromColorMixing("RGB", float.NaN), e => e.MaxFlux != 0f);
    }

    [Fact]
    public void StandardEmitterHelpers()
    {
        Assert.Equal(4, StandardEmitters.Rgbw(100f).Length);
        Assert.Equal(4, StandardEmitters.Rgba(100f).Length);
        Assert.Equal(5, StandardEmitters.Rgbal(100f).Length);
        Assert.True(StandardEmitters.TryGet("ww", out var ww));
        Assert.Equal("Warm white", ww.Name);
        Assert.False(StandardEmitters.TryGet("zz", out _));
        Assert.Equal(0.700f, StandardEmitters.Red.X);
        Assert.True(StandardEmitters.Green.Y > 0.6f);
        Assert.True(StandardEmitters.Blue.Y < 0.1f);
        Assert.True(StandardEmitters.Amber.X > 0.55f);
        Assert.True(StandardEmitters.Lime.Y > 0.5f);
        Assert.True(StandardEmitters.Cyan.X < 0.1f);
        Assert.Equal(Cct.PlanckianXy(6500f), StandardEmitters.White);
        var rgbw = StandardEmitters.Rgbw(1000f);
        var rgbOnly = ColorMixing.Mix(rgbw, [1f, 1f, 1f, 0f]);
        Assert.True(Vector2.Distance(D65, rgbOnly.Chromaticity) < 0.003f);
    }

    [Fact]
    public void EveryColorMixingCodeInTheSpecsIndex_IsUnderstood()
    {
        int lit = 0;
        foreach (var fixture in RepoData.SpecFixtures)
        {
            if (!fixture.TryGetProperty("colorMixing", out var cm) || cm.ValueKind != System.Text.Json.JsonValueKind.String)
                continue;
            string code = cm.GetString()!;
            var unrecognized = new List<string>();
            var emitters = StandardEmitters.FromColorMixing(code, 1000f, unrecognized);

            // M and Y are not LED colours; they only appear in hybrid codes such as "RGBWALCMY".
            Assert.All(unrecognized, u => Assert.Contains(u, new[] { "M", "Y" }));
            if (emitters.Length == 0)
                continue;
            lit++;
            var mix = ColorMixing.MixAllFull(emitters);
            Assert.Equal(1000.0, mix.Flux, 0.05);
            Assert.True(mix.Color.ToCct().IsValid, code);
        }

        Assert.True(lit > 500, $"only {lit} fixtures with LED emitters");
    }
}
