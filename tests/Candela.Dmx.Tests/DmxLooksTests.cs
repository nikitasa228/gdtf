using System.Numerics;
using Candela.Photometry;

namespace Candela.Dmx.Tests;

/// <summary>
/// <see cref="DmxLooks.SetColor"/> on real fixtures of every colour system: additive emitters, CMY, a colour wheel, HSB,
/// and a fixture whose effect ring the colour channels do not reach.
/// </summary>
public class DmxLooksTests
{
    private static readonly LightColor DeepBlue = LightColor.FromRec709(new Vector3(0.02f, 0.06f, 1f));
    private static readonly LightColor Red = LightColor.FromRec709(new Vector3(1f, 0.02f, 0.02f));
    private static readonly LightColor Magenta = LightColor.FromRec709(new Vector3(1f, 0.02f, 0.55f));

    /// <summary>Open white, then the colour; returns the reported distance and the evaluated state.</summary>
    private static (float Distance, FixtureState State, FixtureDmxBuffer Buffer) Colour(FixtureProfile p, LightColor target)
    {
        var buffer = new FixtureDmxBuffer(p);
        DmxLooks.OpenWhite(buffer);
        float distance = DmxLooks.SetColor(buffer, target);
        return (distance, FixtureEvaluator.Evaluate(p, buffer), buffer);
    }

    private static float Distance(BeamState beam, LightColor target) => Vector2.Distance(beam.Color.ToXy(), target.ToXy());

    [Fact]
    public void Additive_emitters_reach_saturated_colours_and_keep_the_dimmer_open()
    {
        var p = TestData.Profile("Ayrton@MagicPanel_602", "Standard"); // RGBW pixels
        foreach (var target in new[] { DeepBlue, Red, Magenta })
        {
            var (distance, state, _) = Colour(p, target);
            Assert.InRange(distance, 0f, 0.005f);
            Assert.All(state.Beams, b => Assert.InRange(Distance(b, target), 0f, 0.005f));
            Assert.All(state.Beams, b => Assert.Equal(1f, b.Intensity, 1e-3f));
            Assert.All(state.Beams, b => Assert.InRange(b.ColorTransmission, 0.02f, 0.6f));
        }
    }

    [Fact]
    public void Cmy_a_colour_wheel_and_hsb_come_closer_than_white()
    {
        foreach (var (prefix, mode) in new[]
        {
            ("Martin_Professional@MAC_Viper_Profile", "Extended"), // CMY + colour wheel
            ("Clay_Paky@Sharpy@", "Standard"), // colour wheel only
            ("BeamZ_Pro@DOT_BAR_Bat1_IP65", "3 Ch HSV"), // HSB
        })
        {
            var p = TestData.Profile(prefix, mode);
            var white = new FixtureDmxBuffer(p);
            DmxLooks.OpenWhite(white);
            var open = FixtureEvaluator.Evaluate(p, white).Beams[0];
            var (distance, state, buffer) = Colour(p, Red);
            Assert.True(distance < Distance(open, Red) * 0.5f, $"{p}: {distance} vs white {Distance(open, Red)}");
            Assert.Equal(distance, Distance(state.Beams[0], Red), 1e-3f);
            Assert.True(state.Beams[0].PeakCandela > 0f, p.ToString());
            Assert.False(white.GetBreak(0).SequenceEqual(buffer.GetBreak(0)), p.ToString());
        }
    }

    [Fact]
    public void The_colour_of_the_whole_fixture_counts_when_one_beam_is_out_of_reach()
    {
        // Ayrton Zonda 3 FX "Basic": the RGBW channels colour the seven pixels; the liquid-effect ring keeps its white
        // and dilutes the fixture's colour a little. Judged by its brightest beam, the fixture looked white whatever the
        // pixels did (the ring had a pixel's peak); judged as a whole, the pixels turn deep blue
        var p = TestData.Profile("Ayrton@Zonda_3_FX", "Basic");
        var (distance, state, _) = Colour(p, DeepBlue);
        Assert.InRange(distance, 0f, 0.05f);
        var pixels = state.Beams.Where(b => b.BeamType != BeamType.Glow).ToList();
        Assert.Equal(7, pixels.Count);
        Assert.All(pixels, b => Assert.InRange(Distance(b, DeepBlue), 0f, 0.01f));
        var ring = Assert.Single(state.Beams, b => b.BeamType == BeamType.Glow);
        Assert.True(ring.Flux < pixels.Sum(b => b.Flux), $"ring {ring.Flux} lm, pixels {pixels.Sum(b => b.Flux)} lm");
    }

    [Fact]
    public void A_dark_fixture_or_one_without_colour_is_left_unchanged()
    {
        var p = TestData.Profile("Ayrton@MagicPanel_602", "Standard");
        var dark = new FixtureDmxBuffer(p);
        dark.ResetToDefaults(); // dimmer at 0
        var before = dark.GetBreak(0).ToArray();
        Assert.True(float.IsNaN(DmxLooks.SetColor(dark, Red)));
        Assert.Equal(before, dark.GetBreak(0).ToArray());

        // a strobe with a dimmer and nothing else to make colour with keeps its white
        var strobe = TestData.Profile("Martin_Professional@VDO_Atomic_Bold", "3 Channel");
        var (distance, state, _) = Colour(strobe, Red);
        Assert.True(float.IsNaN(distance) || distance > 0.1f);
        Assert.Equal(1f, state.Beams[0].Intensity, 1e-3f);
    }

    [Fact]
    public void The_result_is_deterministic()
    {
        var p = TestData.Profile("Martin_Professional@MAC_Viper_Profile", "Extended");
        var a = Colour(p, Magenta).Buffer;
        var b = Colour(p, Magenta).Buffer;
        Assert.Equal(a.GetBreak(0).ToArray(), b.GetBreak(0).ToArray());
    }
}
