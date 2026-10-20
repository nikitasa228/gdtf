using System.Numerics;

namespace Candela.Photometry.Tests;

/// <summary>
/// <see cref="ColorMixing.TryBalance"/>: re-balancing an LED engine whose emitters at full do not make the white its
/// datasheet gives (GDTF files with placeholder emitter outputs).
/// </summary>
[Collection(AllocationMeasurementCollection.Name)]
public class ColorBalanceTests
{
    private static readonly Vector2 D65 = new(0.3127f, 0.3290f);

    /// <summary>The Ayrton Zonda 3 FX file's RGBW emitters, every one written with Y 100 (they mix to x 0.255, y 0.178).</summary>
    private static Emitter[] PlaceholderRgbw() =>
    [
        Emitter.FromXy("Red", 0.6942f, 0.3052f, 100f),
        Emitter.FromXy("Green", 0.1700f, 0.7000f, 100f),
        Emitter.FromXy("Blue", 0.1355f, 0.0399f, 100f),
        Emitter.FromXy("White", 0.3100f, 0.3200f, 100f),
    ];

    private static Emitter[] Scaled(Emitter[] emitters, ReadOnlySpan<float> factors)
    {
        var result = new Emitter[emitters.Length];
        for (int i = 0; i < emitters.Length; i++)
            result[i] = new Emitter(emitters[i].Name, emitters[i].Chromaticity, emitters[i].MaxFlux * factors[i]);
        return result;
    }

    [Theory]
    [InlineData(2700f)]
    [InlineData(5600f)]
    [InlineData(8000f)]
    public void Placeholder_outputs_are_balanced_to_the_target_white_keeping_the_total_flux(float kelvin)
    {
        var emitters = PlaceholderRgbw();
        var target = Cct.XyFromCct(kelvin);
        Span<float> factors = stackalloc float[4];
        Assert.True(ColorMixing.TryBalance(emitters, target, factors));
        foreach (float k in factors) Assert.True(k > 0f && float.IsFinite(k), $"factor {k}");

        var mix = ColorMixing.MixAllFull(Scaled(emitters, factors));
        Assert.True(Vector2.Distance(target, mix.Chromaticity) < 1e-4f, $"{mix.Chromaticity} for {target}");
        Assert.Equal(400.0, mix.Flux, 0.01);
        var cct = Cct.FromXy(mix.Chromaticity);
        Assert.Equal(kelvin, cct.Kelvin, kelvin * 0.005f);
    }

    [Fact]
    public void An_engine_already_at_the_target_is_left_unchanged()
    {
        var red = Emitter.FromXy("Red", 0.7017f, 0.2969f, 900f);
        var green = Emitter.FromXy("Green", 0.1564f, 0.6983f, 1500f);
        var blue = Emitter.FromXy("Blue", 0.1525f, 0.0268f, 200f);
        Assert.True(ColorMixing.TrySolveLevels(red, green, blue, D65, out var levels));
        Emitter[] balanced =
        [
            new(red.Name, red.Chromaticity, red.MaxFlux * levels.X),
            new(green.Name, green.Chromaticity, green.MaxFlux * levels.Y),
            new(blue.Name, blue.Chromaticity, blue.MaxFlux * levels.Z),
        ];
        Span<float> factors = stackalloc float[3];
        Assert.True(ColorMixing.TryBalance(balanced, D65, factors));
        foreach (float k in factors) Assert.Equal(1f, k, 1e-3f);
    }

    [Fact]
    public void The_change_is_spread_over_the_emitters_rather_than_switching_one_off()
    {
        // a white emitter between the colours: the least change trims all four a little, none goes to (near) zero
        var emitters = PlaceholderRgbw();
        Span<float> factors = stackalloc float[4];
        Assert.True(ColorMixing.TryBalance(emitters, Cct.XyFromCct(6500f), factors));
        float min = float.MaxValue, max = 0f;
        foreach (float k in factors)
        {
            min = MathF.Min(min, k);
            max = MathF.Max(max, k);
        }
        Assert.True(min > 0.02f, $"smallest factor {min}");
        Assert.True(max / min < 100f, $"factors {min} … {max}");
    }

    [Fact]
    public void Targets_outside_the_gamut_and_invalid_input_are_refused()
    {
        var emitters = PlaceholderRgbw();
        Span<float> factors = stackalloc float[4];
        Assert.False(ColorMixing.TryBalance(emitters, new Vector2(0.08f, 0.85f), factors)); // beyond green
        Assert.False(ColorMixing.TryBalance(emitters, new Vector2(float.NaN, 0.3f), factors));
        Assert.False(ColorMixing.TryBalance(emitters, new Vector2(0.3f, 0f), factors));
        Assert.False(ColorMixing.TryBalance(emitters.AsSpan(0, 1), D65, factors)); // one emitter
        Assert.False(ColorMixing.TryBalance(emitters, D65, stackalloc float[3])); // too few factors
        Emitter[] dark = [Emitter.FromXy("Red", 0.70f, 0.30f, 0f), Emitter.FromXy("Blue", 0.14f, 0.04f, 100f)];
        Assert.False(ColorMixing.TryBalance(dark, D65, factors)); // only one emitter gives light
        // two emitters can only make the colours on the line between them
        Emitter[] pair = [Emitter.FromXy("Amber", 0.57f, 0.42f, 100f), Emitter.FromXy("Cyan", 0.16f, 0.37f, 100f)];
        Assert.False(ColorMixing.TryBalance(pair, D65, factors));
    }

    [Fact]
    public void Balancing_does_not_allocate()
    {
        var emitters = PlaceholderRgbw();
        Span<float> factors = stackalloc float[4];
        var target = Cct.XyFromCct(8000f);
        Assert.True(ColorMixing.TryBalance(emitters, target, factors)); // warm up
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) ColorMixing.TryBalance(emitters, target, factors);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void Random_engines_reach_every_target_inside_their_gamut_and_refuse_targets_outside()
    {
        // deterministic sweep: RGB(+W) engines with random primaries and outputs over two decades, targets anywhere in
        // the triangle of the primaries (inside → reached exactly with the flux kept; outside → refused)
        var rng = new Random(20261019);
        Span<float> factors = stackalloc float[4];
        int reached = 0, refused = 0;
        for (int trial = 0; trial < 400; trial++)
        {
            var r = new Vector2(0.62f + 0.08f * rng.NextSingle(), 0.28f + 0.05f * rng.NextSingle());
            var g = new Vector2(0.12f + 0.15f * rng.NextSingle(), 0.62f + 0.12f * rng.NextSingle());
            var b = new Vector2(0.13f + 0.03f * rng.NextSingle(), 0.03f + 0.05f * rng.NextSingle());
            float Flux() => MathF.Pow(10f, 1f + 2f * rng.NextSingle());
            bool white = trial % 2 == 0;
            Emitter[] emitters = white
                ?
                [
                    new("R", r, Flux()), new("G", g, Flux()), new("B", b, Flux()),
                    Emitter.FromXy("W", 0.30f + 0.05f * rng.NextSingle(), 0.31f + 0.05f * rng.NextSingle(), Flux()),
                ]
                : [new("R", r, Flux()), new("G", g, Flux()), new("B", b, Flux())];
            // barycentric target: inside when all weights are positive
            float w0 = rng.NextSingle(), w1 = rng.NextSingle(), w2 = rng.NextSingle();
            bool inside = trial % 5 != 0;
            if (!inside) w2 = -0.3f - 0.5f * rng.NextSingle();
            float sum = w0 + w1 + w2;
            var target = (r * w0 + g * w1 + b * w2) / sum;
            if (inside)
            {
                // keep away from the edges, where the factors (and the iteration) run off to extremes
                float m = MathF.Min(w0, MathF.Min(w1, w2)) / sum;
                if (m < 0.05f) continue;
            }
            var f = factors[..emitters.Length];
            bool ok = ColorMixing.TryBalance(emitters, target, f);
            if (!inside)
            {
                Assert.False(ok, $"trial {trial}: {target} outside the gamut was accepted");
                refused++;
                continue;
            }
            Assert.True(ok, $"trial {trial}: {target} inside the gamut was refused");
            var mix = ColorMixing.MixAllFull(Scaled(emitters, f));
            Assert.True(Vector2.Distance(target, mix.Chromaticity) < 1e-3f, $"trial {trial}: {mix.Chromaticity} for {target}");
            double total = 0;
            foreach (var e in emitters) total += e.MaxFlux;
            Assert.Equal(total, mix.Flux, total * 1e-4);
            reached++;
        }
        Assert.True(reached > 150 && refused > 50, $"{reached} reached, {refused} refused");
    }
}
