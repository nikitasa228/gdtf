using System.Numerics;
using Candela.Photometry;

namespace Candela.Dmx.Tests;

/// <summary>
/// LED colour engines: white ↔ colour cross-fades (ARRI SkyPanel/L-series style), colour temperature moved electronically
/// with the emitters' efficiency, CTO channels that select a CCT, saturation without hue, CMY written as ColorAdd.
/// </summary>
public class ColorEngineTests
{
    private const string ArriL5 = "ARRI@L5-C"; // "Mode 1 CCT+RGBW 8Bit": Dimmer, CTC, Tint, Color XF, R, G, B, W
    private const string PrFresnel = "PRstage@FRESNEL_400_RGBALC"; // Dimmer, CTO (Open, 2000 … 10000 K), virtual gel wheel, saturation
    private const string F1000 = "LIGHT_SKY@F1000_Spot"; // CMY flags written as ColorAdd_C/M/Y
    private const string FuzeProfile = "Elation@Fuze_Profile@"; // RGBAL engine with CTC 2400 … 8500 K

    private static FixtureProfile Arri => TestData.Profile(ArriL5);

    private static BeamState Evaluate(FixtureProfile p, params (string Attribute, float Value)[] values)
    {
        var buffer = new FixtureDmxBuffer(p);
        buffer.ApplyHighlight();
        foreach (var (a, v) in values) Assert.True(DmxEncoder.Encode(buffer, a, v) > 0, a);
        return FixtureEvaluator.Evaluate(p, buffer).Beams[0];
    }

    [Fact]
    public void Arri_white_engine_follows_cct_and_ignores_the_colour_channels()
    {
        var p = Arri;
        Assert.True(p.Beams[0].ElectronicColor);
        foreach (float kelvin in new[] { 3200f, 5600f, 8000f })
        {
            var white = Evaluate(p, ("Color XF", 0f), ("CTC", kelvin), ("ColorAdd_R", 1f), ("ColorAdd_G", 0f), ("ColorAdd_B", 0f), ("ColorAdd_W", 0f));
            var cct = white.Color.ToCct();
            Assert.Equal(kelvin, cct.Kelvin, kelvin * 0.02f);
            Assert.InRange(MathF.Abs(cct.Duv), 0f, 0.003f);
            // the luminous output is the emitters' best white at that CCT
            Assert.Equal(p.Beams[0].WhiteEfficiency(cct.Kelvin), white.ColorTransmission, 0.01f);
        }
        // the default state is the white engine at the default CCT, fully lit (no colour channel is needed)
        var defaults = new FixtureDmxBuffer(p);
        DmxEncoder.Encode(defaults, "Dimmer", 1f);
        var d = FixtureEvaluator.Evaluate(p, defaults).Beams[0];
        Assert.True(d.ColorTransmission > 0.5f, $"default transmission {d.ColorTransmission}");
        Assert.True(d.PeakCandela > 0.5f * p.Specs!.MaxLuminousIntensityCd!.Value);
    }

    [Fact]
    public void Arri_colour_engine_and_the_cross_fade_between_them()
    {
        var p = Arri;
        var red = p.Emitters.First(e => e.Name.Contains("Red", StringComparison.OrdinalIgnoreCase));
        var colour = Evaluate(p, ("Color XF", 1f), ("CTC", 3200f), ("ColorAdd_R", 1f), ("ColorAdd_G", 0f), ("ColorAdd_B", 0f), ("ColorAdd_W", 0f));
        Assert.True(Vector2.Distance(red.Chromaticity, colour.Color.ToXy()) < 0.005f, $"{colour.Color.ToXy()}");
        var white = Evaluate(p, ("Color XF", 0f), ("CTC", 3200f), ("ColorAdd_R", 1f), ("ColorAdd_G", 0f), ("ColorAdd_B", 0f), ("ColorAdd_W", 0f));
        var half = Evaluate(p, ("Color XF", 0.5f), ("CTC", 3200f), ("ColorAdd_R", 1f), ("ColorAdd_G", 0f), ("ColorAdd_B", 0f), ("ColorAdd_W", 0f));
        // half way: the average of the two engines' light (in flux), within the 8-bit quantisation of the fade
        float expectedFlux = 0.5f * (white.Flux + colour.Flux);
        Assert.Equal(expectedFlux, half.Flux, expectedFlux * 0.02f);
        float x = half.Color.ToXy().X;
        Assert.InRange(x, white.Color.ToXy().X + 0.01f, colour.Color.ToXy().X - 0.01f);
    }

    [Fact]
    public void Placeholder_emitter_outputs_fall_back_to_typical_led_shares()
    {
        // the ARRI file gives red, green and white the same Y (100): not a measurement, so RGBW at full must be near white
        var p = Arri;
        var full = Evaluate(p, ("Color XF", 1f), ("ColorAdd_R", 1f), ("ColorAdd_G", 1f), ("ColorAdd_B", 1f), ("ColorAdd_W", 1f));
        var cct = full.Color.ToCct();
        Assert.InRange(cct.Kelvin, 3500f, 9000f);
        Assert.InRange(MathF.Abs(cct.Duv), 0f, 0.02f);
        Assert.Equal(1f, full.ColorTransmission, 1e-3f);
    }

    [Fact]
    public void A_cto_channel_with_an_open_range_selects_the_colour_temperature()
    {
        var p = TestData.Profile(PrFresnel);
        Assert.True(p.Beams[0].ElectronicColor);
        foreach (float kelvin in new[] { 2000f, 3200f, 5600f, 10000f })
        {
            var b = Evaluate(p, ("CTO", kelvin));
            Assert.Equal(kelvin, b.Color.ToCct().Kelvin, kelvin * 0.03f);
            Assert.Equal(1f, b.ColorTransmission, 0.01f); // no emitter data in this mode: no luminous cost known
        }
        var buffer = new FixtureDmxBuffer(p);
        buffer.ApplyHighlight();
        buffer.SetValue(p.FindChannels("CTO")[0], 0); // "Open": the native white
        var open = FixtureEvaluator.Evaluate(p, buffer).Beams[0];
        Assert.Equal(p.Beams[0].SourceKelvin, open.Color.ToCct().Kelvin, 100f);
    }

    [Fact]
    public void Saturation_without_hue_desaturates_the_virtual_gels()
    {
        var p = TestData.Profile(PrFresnel);
        int wheel = p.FindChannels("Color1")[0];
        var redSlot = p.FunctionsOf(wheel).ToArray().SelectMany(f => f.Sets).First(s => s.Name == "Red");
        float RedTransmission(float saturation)
        {
            var buffer = new FixtureDmxBuffer(p);
            buffer.ApplyHighlight();
            DmxEncoder.Encode(buffer, "CTO", 5600f);
            buffer.SetValue(wheel, redSlot.DmxFrom);
            DmxEncoder.Encode(buffer, "HSB_Saturation", saturation);
            return FixtureEvaluator.Evaluate(p, buffer).Beams[0].ColorTransmission;
        }
        float full = RedTransmission(100f), half = RedTransmission(50f), none = RedTransmission(0f);
        Assert.True(full < 0.5f, $"red gel {full}");
        Assert.InRange(half, full + 0.1f, none - 0.1f);
        Assert.Equal(1f, none, 0.02f);
    }

    [Fact]
    public void Electronic_colour_temperature_moves_the_white_point_at_the_engines_cost()
    {
        // an RGBAL engine with a CTC channel and no cross-fade: the white point is moved by the emitters, not a filter
        var p = TestData.Profile(FuzeProfile, "Standard Pan540");
        var beam = p.Beams[0];
        Assert.True(beam.ElectronicColor);
        var emitters = p.Emitters.Select(e => FindColorAdd(p, e)).Where(a => a is not null).Select(a => (a!, 1f)).ToArray();
        Assert.True(emitters.Length >= 4);
        var neutral = Evaluate(p, emitters);
        var warm = Evaluate(p, [.. emitters, ("CTC", 3200f)]);
        Assert.True(warm.Color.ToCct().Kelvin < neutral.Color.ToCct().Kelvin - 1000f);
        float expected = MathF.Min(1f, beam.WhiteEfficiency(3200f) / beam.WhiteEfficiency(beam.SourceKelvin));
        Assert.Equal(expected, warm.ColorTransmission / neutral.ColorTransmission, 0.15f * expected);
        var filter = beam.Subtractive!;
        float filterTransmission = ColorSpaces.LuminanceRec2020(filter.SourceRgb * filter.RgbTransmission(0f, 0f, 0f, 3200f));
        Assert.True(warm.ColorTransmission / neutral.ColorTransmission > filterTransmission + 0.1f,
            $"engine {warm.ColorTransmission / neutral.ColorTransmission}, filter {filterTransmission}");
    }

    private static string? FindColorAdd(FixtureProfile p, ProfileEmitter e) =>
        p.Functions.FirstOrDefault(f => f.Class.Kind == AttributeKind.ColorAdd && f.Emitter == e.Index)?.Attribute;

    [Fact]
    public void Cmy_written_as_color_add_is_read_as_cmy_flags()
    {
        var p = TestData.Profile(F1000);
        foreach (string a in new[] { "ColorAdd_C", "ColorAdd_M", "ColorAdd_Y" })
            Assert.Equal(AttributeKind.ColorSub, p.Functions[p.Channels[p.FindChannels(a)[0]].FirstFunction].Class.Kind);
        Assert.Contains(p.Warnings, w => w.Contains("ColorAdd_C/M/Y"));
        var open = Evaluate(p);
        Assert.Equal(1f, open.ColorTransmission, 0.01f); // DMX 0 = white, not "emitters off"
        var full = Evaluate(p, ("ColorAdd_C", 1f), ("ColorAdd_M", 1f), ("ColorAdd_Y", 1f));
        Assert.True(full.ColorTransmission < 0.05f, $"CMY full {full.ColorTransmission}");
        var cyan = Evaluate(p, ("ColorAdd_C", 1f));
        Assert.True(cyan.Color.Rgb.X < 0.5f * cyan.Color.Rgb.Z, $"cyan {cyan.Color}");
    }
}
