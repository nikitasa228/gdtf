using Candela.Photometry;

namespace Candela.Dmx.Tests;

/// <summary>
/// Real GDTF files whose data has typical authoring defects — physical ranges left at GDTF's default 0 … 1, a wrong or
/// default lamp type, shutter states named on the function instead of its channel sets — must still give the physical
/// behaviour of the fixture.
/// </summary>
public class FileDefectTests
{
    private const float Deg = MathF.PI / 180f;

    /// <summary>Full output: highlight values (shutters open), every dimmer and emitter at its brightest.</summary>
    private static FixtureDmxBuffer Full(FixtureProfile p)
    {
        var buffer = new FixtureDmxBuffer(p);
        buffer.ApplyHighlight();
        foreach (var c in p.Channels)
        {
            var f = p.Functions[c.FirstFunction];
            if (c.Class.Kind is AttributeKind.Dimmer or AttributeKind.ColorAdd) DmxEncoder.EncodeFunction(buffer, f.Index, f.MaxValue);
        }
        return buffer;
    }

    private static void SetZoom(FixtureProfile p, FixtureDmxBuffer buffer, uint dmx)
    {
        foreach (int z in p.FindChannels("Zoom")) buffer.SetValue(z, dmx);
    }

    [Fact]
    public void A_zoom_with_the_default_physical_range_spans_the_fixtures_real_zoom()
    {
        // Briteq BTI-Blizzard Wash2: Zoom 0…1 in the file, 3.9…37.5° per the datasheet
        var p = TestData.Profile("Briteq@BTI-BLIZZARD_WASH2");
        var specs = p.Specs!.BeamAngleDeg!.Value;
        var buffer = Full(p);
        int zoom = p.FindChannels("Zoom")[0];
        SetZoom(p, buffer, 0);
        var narrow = FixtureEvaluator.Evaluate(p, buffer);
        SetZoom(p, buffer, p.Channels[zoom].MaxValue);
        var wide = FixtureEvaluator.Evaluate(p, buffer);
        Assert.Equal((float)specs.Min, narrow.Beams[0].BeamAngle / Deg, 0.01f);
        Assert.Equal((float)specs.Max, wide.Beams[0].BeamAngle / Deg, 0.01f);
        Assert.Contains(p.Warnings, w => w.Contains("placeholder", StringComparison.Ordinal));

        // the datasheet candela is reached at the narrowest zoom (not at a 1° pencil beam the file would imply)
        double cd = p.Specs.MaxLuminousIntensityCd!.Value;
        Assert.Equal(cd, narrow.Beams.Sum(b => (double)b.PeakCandela), cd * 0.01);
        Assert.True(wide.Beams[0].PeakCandela < narrow.Beams[0].PeakCandela / 20f);
    }

    [Fact]
    public void A_zoom_function_with_default_range_but_real_channel_set_values_uses_the_set_values()
    {
        // ADJ Inno Pocket Z4: function 0…1, its channel set "Min to Max" 10…60°
        var p = TestData.Profile("ADJ@Inno_Pocket_Z4");
        var buffer = Full(p);
        int zoom = p.FindChannels("Zoom")[0];
        var f = p.Functions[p.Channels[zoom].FirstFunction];
        Assert.Equal(10f, f.MinValue);
        Assert.Equal(60f, f.MaxValue);
        SetZoom(p, buffer, 0);
        var narrow = FixtureEvaluator.Evaluate(p, buffer);
        SetZoom(p, buffer, 255);
        var wide = FixtureEvaluator.Evaluate(p, buffer);
        Assert.Equal(10f, narrow.Beams[0].BeamAngle / Deg, 0.01f);
        Assert.Equal(60f, wide.Beams[0].BeamAngle / Deg, 0.01f);
        double cd = p.Specs!.MaxLuminousIntensityCd!.Value;
        double sum = narrow.Beams.Sum(b => (double)b.PeakCandela);
        Assert.InRange(sum, cd * 0.5, cd * 1.01); // full RGBW (not the brightest white of every emitter mix) at 10°
    }

    [Fact]
    public void A_placeholder_zoom_next_to_a_real_zoom_does_not_drive_the_beam_angle()
    {
        // BeamZ Nereid 760: "Zoom" 46…7° plus a "Zoom Lens Rotation" effect written with attribute Zoom and range 0…1
        var p = TestData.Profile("BeamZ_Pro@Nereid_760_IP65");
        var zoomFunctions = p.Functions.Where(f => f.Source.AttributeName == "Zoom").ToList();
        Assert.Contains(zoomFunctions, f => f.Class.Kind == AttributeKind.Zoom && f.MaxValue > 40f);
        Assert.Contains(zoomFunctions, f => f.Class.Kind == AttributeKind.Ignored);
        foreach (var beam in p.Beams)
            Assert.InRange(beam.SolidAngles.MinBeamAngle / Deg, 6.9f, 7.1f); // the photometric table starts at the real narrowest zoom
        var buffer = Full(p);
        var rotation = zoomFunctions.First(f => f.Class.Kind == AttributeKind.Ignored);
        DmxEncoder.EncodeFunction(buffer, rotation.Index, 0.5f);
        var state = FixtureEvaluator.Evaluate(p, buffer);
        foreach (var b in state.Beams) Assert.True(b.BeamAngle / Deg > 5f, $"{b.Name}: {b.BeamAngle / Deg}°");
    }

    [Fact]
    public void A_zoom_mode_channel_next_to_the_zoom_channel_does_not_freeze_the_beam_angle()
    {
        // Elation Proteus Brutus FS: Zoom 3.5…35° and ZoomModeBeam ("Disabled" / "Long Throw" …, unit Angle)
        var p = TestData.Profile("Elation@Proteus_Brutus_FS");
        Assert.NotEmpty(p.FindChannels("ZoomModeBeam"));
        int zoom = p.FindChannels("Zoom")[0];
        var buffer = Full(p);
        foreach (float angle in new[] { 3.5f, 12f, 35f })
        {
            DmxEncoder.Encode(buffer, "Zoom", angle);
            Assert.Equal(angle, FixtureEvaluator.Evaluate(p, buffer).Beams[0].BeamAngle / Deg, 0.05f);
        }
        Assert.Equal(AttributeKind.Zoom, p.Functions[p.Channels[zoom].FirstFunction].Class.Kind);
    }

    [Fact]
    public void Pan_and_tilt_with_the_default_physical_range_move_over_the_fixtures_real_range()
    {
        var p = TestData.Profile("Briteq@BTI-BLIZZARD_WASH2");
        var specs = p.Specs!;
        foreach (var (attribute, range) in new[] { ("Pan", specs.PanRangeDeg!.Value), ("Tilt", specs.TiltRangeDeg!.Value) })
        {
            int c = p.FindChannels(attribute)[0];
            var buffer = new FixtureDmxBuffer(p);
            buffer.SetValue(c, 0);
            var low = FixtureEvaluator.Evaluate(p, buffer).Axes[p.Channels[c].Axis];
            buffer.SetValue(c, p.Channels[c].MaxValue);
            var high = FixtureEvaluator.Evaluate(p, buffer).Axes[p.Channels[c].Axis];
            float a0 = attribute == "Pan" ? low.Pan : low.Tilt, a1 = attribute == "Pan" ? high.Pan : high.Tilt;
            Assert.Equal((float)range, MathF.Abs(a1 - a0) / Deg, 0.01f);
            Assert.Equal(0f, (a0 + a1) / 2f / Deg, 0.01f); // centred
        }
    }

    [Fact]
    public void A_pan_function_on_a_control_channel_does_not_move_the_head()
    {
        // Elation Proteus Rayzor Blade L: "Display Intensity" on Control1, written with attribute Pan and range 0…1
        var p = TestData.Profile("Elation@Proteus_Rayzor_Blade_L");
        var control = p.Channels.Where(c => c.Attribute == "Control1").ToList();
        Assert.NotEmpty(control);
        Assert.All(control, c => Assert.Equal(-1, c.Axis));
        Assert.All(p.Axes, a => Assert.DoesNotContain(a.Channels, ch => p.Channels[ch].Attribute == "Control1"));
    }

    [Fact]
    public void A_strobe_with_the_default_physical_range_runs_slow_to_fast_not_below_one_hertz()
    {
        // Briteq BTX-Hawk: Shutter1Strobe 0…1 "Hz" with channel sets Min … Max
        var p = TestData.Profile("Briteq@BTX-Hawk");
        var strobe = p.Functions.First(f => f.Class.Kind == AttributeKind.Strobe && f.Source.PhysicalFrom == 0f && f.Source.PhysicalTo == 1f &&
            f.Unit == Gdtf.PhysicalUnit.Frequency && f.Waveform == Waveform.Strobe);
        var buffer = Full(p);
        buffer.SetValue(strobe.Channel, strobe.Sets.First(s => s.Name == "Max").DmxFrom);
        var fast = FixtureEvaluator.Evaluate(p, buffer).Beams[0];
        buffer.SetValue(strobe.Channel, strobe.Sets.First(s => s.Name == "Min").DmxFrom);
        var slow = FixtureEvaluator.Evaluate(p, buffer).Beams[0];
        Assert.True(fast.Strobing && slow.Strobing);
        Assert.InRange(fast.StrobeFrequency, 20f, 25f); // 0 … 1 is the default range, not 0 … 1 Hz
        Assert.Equal(0.5f, slow.StrobeFrequency, 0.01f);
    }

    [Fact]
    public void Rotations_and_shakes_with_the_default_physical_range_turn_and_shake_at_real_rates()
    {
        // Vari-Lite VL2600 Spot: Gobo1PosRotate 0…1 "deg/s" (not a 1 °/s crawl)
        var vl = TestData.Profile("Vari-Lite@VL2600_Spot", "16Bit Enhanced");
        var rotate = vl.Functions.First(f => f.Class.Kind == AttributeKind.SlotRotate && f.Attribute == "Gobo1PosRotate");
        var buffer = Full(vl);
        DmxEncoder.EncodeFunction(buffer, rotate.Index, 1f);
        var gobo = FixtureEvaluator.Evaluate(vl, buffer, 2.0).Beams[0].Gobos[0];
        Assert.Equal(360f * Deg, MathF.Abs(gobo.RotationSpeed), 0.02f);

        // Elation Fuze Max Spot: Gobo1SelectShake 0…1 "Hz"
        var fuze = TestData.Profile("Elation@Fuze_Max_Spot", "Standard Pan540");
        var shake = fuze.Functions.First(f => f.Class.Kind == AttributeKind.WheelSelectShake && f.Attribute == "Gobo1SelectShake");
        buffer = Full(fuze);
        buffer.SetValue(shake.Channel, shake.DmxTo);
        var state = FixtureEvaluator.Evaluate(fuze, buffer, 1.0);
        Assert.Equal(shake.Index, state.GetChannel(shake.Channel).Function);
        Assert.InRange(state.Beams[0].Gobos[0].ShakeFrequency, 5f, 10f);
    }

    [Fact]
    public void A_shutter_function_named_open_opens_the_shutter_over_its_whole_range()
    {
        // LIGHT_SKY IP2021 Profile: Shutter1 function "Open" with GDTF's default physical range and no channel sets
        var p = TestData.Profile("LIGHT_SKY@IP2021Profile");
        var open = p.Functions.First(f => f.Class.Kind == AttributeKind.Shutter && f.Name == "Open" && f.Sets.Count == 0);
        var buffer = Full(p);
        foreach (uint dmx in new[] { open.DmxFrom, (open.DmxFrom + open.DmxTo) / 2, open.DmxTo })
        {
            buffer.SetValue(open.Channel, dmx);
            var bs = FixtureEvaluator.Evaluate(p, buffer).Beams[0];
            Assert.Equal(1f, bs.Shutter);
        }
    }

    [Fact]
    public void Leds_with_a_default_lamp_type_in_the_file_are_led_engines()
    {
        // ARRI Orbiter: the GDTF Beam keeps LampType "Discharge" (the default); it is an RGBACL LED engine
        var p = TestData.Profile("ARRI@Orbiter", "Mode 1 - CCT+RGBACL 8Bit");
        Assert.Equal(Gdtf.LampType.Discharge, p.Beams[0].Geometry.LampType);
        Assert.True(p.Beams[0].ElectronicColor);
        // its CTC re-balances the emitters: at 3200 K it keeps most of its light (an ideal 6000 → 3200 K filter keeps ≈ 37 %)
        var buffer = Full(p);
        DmxEncoder.Encode(buffer, "CTC", 3200f);
        DmxEncoder.Encode(buffer, "Color XF", 0f);
        var bs = FixtureEvaluator.Evaluate(p, buffer).Beams[0];
        Assert.InRange(bs.Color.ToCct().Kelvin, 3100f, 3300f);
        Assert.True(bs.ColorTransmission > 0.6f, $"T = {bs.ColorTransmission}");
    }

    [Fact]
    public void A_tunable_white_led_without_emitter_data_moves_its_white_point_without_filter_losses()
    {
        // ROXX Neo Mini CCT mode: Dimmer, CTC 2000…10000 K, Tint; no emitters in the file
        var p = TestData.Profile("ROXX@Neo_Mini", "CCT Mode (3 CH)");
        Assert.True(p.Beams[0].ElectronicColor);
        var buffer = Full(p);
        foreach (float kelvin in new[] { 2700f, 5600f, 10000f })
        {
            DmxEncoder.Encode(buffer, "CTC", kelvin);
            DmxEncoder.Encode(buffer, "Tint", p.Functions[p.Channels[p.FindChannels("Tint")[0]].FirstFunction] is var tint ? (tint.MinValue + tint.MaxValue) / 2f : 0f);
            var bs = FixtureEvaluator.Evaluate(p, buffer).Beams[0];
            Assert.InRange(bs.Color.ToCct().Kelvin, kelvin * 0.95f, kelvin * 1.05f);
            Assert.True(bs.ColorTransmission > 0.95f, $"{kelvin} K: T = {bs.ColorTransmission}");
        }
    }

    [Fact]
    public void Multi_beam_fixtures_share_the_datasheet_output_by_their_gdtf_flux()
    {
        // Elation Rayzor 760: seven 1200 lm RGBW pixels and 28 SparkLEDs of 15 lm
        var p = TestData.Profile("Elation@Rayzor_760");
        var main = p.Beams.First(b => b.Geometry.LuminousFlux == 1200f);
        var spark = p.Beams.First(b => b.Geometry.LuminousFlux == 15f);
        Assert.Equal(PhotometrySource.SpecsCandela, main.PhotometrySource);
        float angle = 20f * Deg;
        Assert.Equal(80f, main.PeakCandelaAt(angle) / spark.PeakCandelaAt(angle), 0.5f);
        // the RGBW pixels carry the datasheet output, the SparkLEDs their small share of it
        double total = p.Beams.Sum(b => (double)b.PeakCandelaAt(angle));
        double pixels = p.Beams.Where(b => b.Geometry.LuminousFlux == 1200f).Sum(b => (double)b.PeakCandelaAt(angle));
        Assert.Equal(7 * 1200.0 / (7 * 1200 + 28 * 15), pixels / total, 1e-3);
    }
}
