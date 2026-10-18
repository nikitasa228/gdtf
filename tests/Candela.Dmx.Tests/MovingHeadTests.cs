using System.Numerics;
using Candela.Photometry;

namespace Candela.Dmx.Tests;

/// <summary>A discharge profile moving head with CMY, CTO, colour wheel, three gobo wheels, prism, iris, zoom and focus.</summary>
public class MovingHeadTests
{
    private const string Viper = "Martin_Professional@MAC_Viper_Profile";
    private const float Deg = MathF.PI / 180f;

    private static FixtureProfile Profile => TestData.Profile(Viper, "Basic");

    /// <summary>Buffer with shutter open, dimmer full and the given attribute values.</summary>
    private static FixtureDmxBuffer Open(FixtureProfile p, params (string Attribute, float Value)[] values)
    {
        var buffer = new FixtureDmxBuffer(p);
        Assert.True(DmxEncoder.Encode(buffer, "Shutter1", 1f) > 0);
        Assert.True(DmxEncoder.Encode(buffer, "Dimmer", 1f) > 0);
        foreach (var (a, v) in values) Assert.True(DmxEncoder.Encode(buffer, a, v) > 0, $"{a} not encoded");
        return buffer;
    }

    [Fact]
    public void Compiles_the_basic_mode_layout()
    {
        var p = Profile;
        Assert.Equal(26, p.Footprint);
        Assert.Single(p.Breaks);
        Assert.Single(p.Beams);
        Assert.Equal(2, p.Axes.Count);
        var pan = p.Channels[p.FindChannels("Pan")[0]];
        Assert.Equal(2, pan.ByteCount);
        Assert.Equal(new[] { 22, 23 }, pan.Offsets);
        Assert.Equal("Yoke", p.Axes[pan.Axis].Name);
        Assert.Equal("Head", p.Axes[p.Channels[p.FindChannels("Tilt")[0]].Axis].Name);
        // Gobo1Pos functions depend on the Gobo1 channel (mode master)
        var gobo1 = p.FindChannels("Gobo1")[0];
        Assert.All(p.FunctionsOf(p.FindChannels("Gobo1Pos")[0]).ToArray(), f => Assert.Equal(gobo1, f.ModeMaster));
    }

    [Theory]
    [InlineData("Pan")]
    [InlineData("Tilt")]
    public void Position_dmx_extremes_and_centre_map_to_the_physical_range(string attribute)
    {
        var p = Profile;
        int c = p.FindChannels(attribute)[0];
        var f = p.Functions[p.Channels[c].FirstFunction];
        Assert.Equal(attribute == "Pan" ? 270f : 134f, f.PhysicalFrom);
        Assert.Equal(-f.PhysicalFrom, f.PhysicalTo);
        var buffer = new FixtureDmxBuffer(p);
        foreach (var (dmx, expected) in new[] { (0u, f.PhysicalFrom), (65535u, f.PhysicalTo), (32768u, f.PhysicalFrom + (f.PhysicalTo - f.PhysicalFrom) * 32768f / 65535f) })
        {
            buffer.SetValue(c, dmx);
            var state = FixtureEvaluator.Evaluate(p, buffer);
            var axis = state.Axes[p.Channels[c].Axis];
            float angle = attribute == "Pan" ? axis.Pan : axis.Tilt;
            Assert.Equal(expected * Deg, angle, 1e-4f);
            Assert.Equal(expected, state.GetChannel(c).Value, 1e-3f);
        }
    }

    [Fact]
    public void Axis_animation_matrices_rotate_the_parts()
    {
        var p = Profile;
        var state = FixtureEvaluator.Evaluate(p, Open(p, ("Pan", 90f), ("Tilt", -45f)));
        var anim = new Matrix4x4[p.Geometry.Parts.Count];
        state.WriteAnimation(anim);
        var yoke = p.Axes[p.Channels[p.FindChannels("Pan")[0]].Axis].PartIndex;
        var head = p.Axes[p.Channels[p.FindChannels("Tilt")[0]].Axis].PartIndex;
        var x = Vector3.Transform(Vector3.UnitX, anim[yoke]);
        Assert.Equal(0f, x.X, 1e-4f);
        Assert.Equal(1f, x.Y, 1e-4f); // +90° about Z turns X into Y
        var down = Vector3.Transform(-Vector3.UnitZ, anim[head]);
        Assert.Equal(MathF.Sin(-45f * Deg), down.Y, 1e-3f); // (0,0,-1)·Rx(θ) = (0, sin θ, -cos θ)
        Assert.Equal(Matrix4x4.Identity, anim[0]);
    }

    [Fact]
    public void Dimmer_scales_intensity_and_peak_candela_reaches_the_datasheet_value()
    {
        var p = Profile;
        var spec = p.Specs!;
        Assert.Equal(760000.0, spec.MaxLuminousIntensityCd!.Value);
        var full = FixtureEvaluator.Evaluate(p, Open(p, ("Zoom", 10f))).Beams[0];
        Assert.Equal(1f, full.Intensity, 1e-5f);
        Assert.Equal(1f, full.ColorTransmission, 1e-3f);
        Assert.Equal(760000f, full.PeakCandela, 760000f * 0.01f);
        var half = FixtureEvaluator.Evaluate(p, Open(p, ("Zoom", 10f), ("Dimmer", 0.5f))).Beams[0];
        Assert.Equal(0.5f, half.Intensity, 0.005f);
        Assert.Equal(full.PeakCandela * half.Intensity, half.PeakCandela, full.PeakCandela * 1e-4f);
        // zooming out keeps the flux: the peak falls roughly with the solid angle
        var wide = FixtureEvaluator.Evaluate(p, Open(p, ("Zoom", 44f))).Beams[0];
        Assert.InRange(full.PeakCandela / wide.PeakCandela, 12f, 25f);
        Assert.Equal(full.Flux, wide.Flux, full.Flux * 1e-3f);
    }

    [Fact]
    public void Zoom_extremes_equal_the_gdtf_zoom_range_and_the_specs_beam_angle()
    {
        var p = Profile;
        int zoom = p.FindChannels("Zoom")[0];
        var f = p.Functions[p.Channels[zoom].FirstFunction];
        var buffer = Open(p);
        buffer.SetValue(zoom, 0);
        var wide = FixtureEvaluator.Evaluate(p, buffer).Beams[0];
        buffer.SetValue(zoom, 255);
        var narrow = FixtureEvaluator.Evaluate(p, buffer).Beams[0];
        Assert.Equal(f.PhysicalFrom * Deg, wide.BeamAngle, 1e-4f);
        Assert.Equal(f.PhysicalTo * Deg, narrow.BeamAngle, 1e-4f);
        var specs = p.Specs!.BeamAngleDeg!.Value;
        Assert.Equal((float)specs.Max, wide.BeamAngle / Deg, 0.05f);
        Assert.Equal((float)specs.Min, narrow.BeamAngle / Deg, 0.05f);
        Assert.True(wide.FieldAngle > wide.BeamAngle);
    }

    [Fact]
    public void Colour_wheel_slots_filter_the_lamp_with_the_slot_colours()
    {
        var p = Profile;
        var beam = p.Beams[0];
        int colorChannel = p.FindChannels("Color1")[0];
        var wheel = p.Wheels[p.Functions[p.Channels[colorChannel].FirstFunction].Wheel];
        var source = LightColor.FromXyz(beam.SourceXyz);
        for (int slot = 1; slot <= wheel.SlotCount; slot++)
        {
            var bs = FixtureEvaluator.Evaluate(p, Open(p, ("Color1", slot))).Beams[0];
            var w = bs.ColorWheels[0];
            Assert.Equal(slot, w.Slot);
            Assert.Equal(0f, w.Blend);
            Assert.Equal(slot == 1, w.IsOpen);
            var c = wheel.Source.Slots[slot - 1].Color;
            var expected = slot == 1
                ? new FilterResult(source, 1f)
                : Filters.ApplyChromaticityFilter(source, new Vector2(c.X, c.Y), c.BigY / 100f);
            Assert.Equal(expected.LuminousTransmission, bs.ColorTransmission, 2e-3f);
            var xy = bs.Color.ToXy();
            var exy = expected.Color.ToXy();
            Assert.True(Vector2.Distance(xy, exy) < 2e-3f, $"slot {slot}: {xy} vs {exy}");
            if (c.BigY >= 4f) Assert.True(Vector2.Distance(xy, new Vector2(c.X, c.Y)) < 0.04f, $"slot {slot} far from its colour");
        }
    }

    [Fact]
    public void Positions_between_adjacent_slots_split_the_colour()
    {
        var p = Profile;
        int ch = p.FindChannels("Color1")[0];
        var f = p.Functions[p.Channels[ch].FirstFunction];
        Assert.True(f.UsesSlotPosition);
        var buffer = Open(p);
        DmxEncoder.EncodeFunction(buffer, f.Index, 2.5f);
        var bs = FixtureEvaluator.Evaluate(p, buffer).Beams[0];
        Assert.Equal(2, bs.ColorWheels[0].Slot);
        Assert.Equal(3, bs.ColorWheels[0].NextSlot);
        Assert.InRange(bs.ColorWheels[0].Blend, 0.3f, 0.7f);
        var a = FixtureEvaluator.Evaluate(p, Open(p, ("Color1", 2))).Beams[0].Color.Rgb;
        var b = FixtureEvaluator.Evaluate(p, Open(p, ("Color1", 3))).Beams[0].Color.Rgb;
        var mid = bs.Color.Rgb;
        Assert.True(Vector3.Distance(mid, a) > 0.05f && Vector3.Distance(mid, b) > 0.05f);
    }

    [Fact]
    public void Gobo_wheel_selects_slots_with_their_images()
    {
        var p = Profile;
        int ch = p.FindChannels("Gobo1")[0];
        var wheel = p.Wheels[p.Functions[p.Channels[ch].FirstFunction].Wheel];
        for (int slot = 1; slot <= wheel.SlotCount; slot++)
        {
            var g = FixtureEvaluator.Evaluate(p, Open(p, ("Gobo1", slot))).Beams[0].Gobos[0];
            Assert.Equal(wheel.Index, g.Wheel);
            Assert.Equal(slot, g.Slot);
            Assert.Equal(slot == 1, g.IsOpen);
            Assert.Equal(slot != 1, wheel.SlotAt(g.Position)?.MediaPath is not null);
        }
    }

    [Fact]
    public void Gobo_indexing_and_continuous_rotation()
    {
        var p = Profile;
        // indexing mode (Gobo1 0…34) + Gobo1Pos 90°
        var indexed = FixtureEvaluator.Evaluate(p, Open(p, ("Gobo1", 3), ("Gobo1Pos", 90f))).Beams[0].Gobos[0];
        Assert.Equal(3, indexed.Slot);
        Assert.Equal(90f * Deg, indexed.Rotation, 0.01f);

        // rotation mode: the second Gobo1 function (35…59) unlocks Gobo1PosRotate on the Gobo1Pos channel
        int gobo = p.FindChannels("Gobo1")[0];
        var rotateSelect = p.FunctionsOf(gobo).ToArray()[1];
        var buffer = Open(p);
        DmxEncoder.EncodeFunction(buffer, rotateSelect.Index, 3f);
        Assert.Equal(1, DmxEncoder.Encode(buffer, "Gobo1PosRotate", 300f));
        var state = new FixtureState(p);
        FixtureEvaluator.Evaluate(state, buffer, 1.0);
        float a1 = state.Beams[0].Gobos[0].Rotation;
        Assert.Equal(300f * Deg, state.Beams[0].Gobos[0].RotationSpeed, 0.05f);
        FixtureEvaluator.Evaluate(state, buffer, 1.5);
        float a2 = state.Beams[0].Gobos[0].Rotation;
        float delta = (a2 - a1) % (2f * MathF.PI);
        if (delta < 0) delta += 2f * MathF.PI;
        Assert.Equal(150f * Deg, delta, 0.03f);
        Assert.True(state.IsAnimating);
        Assert.Equal(3, state.Beams[0].Gobos[0].Slot);
    }

    [Fact]
    public void Prism_insertion_and_indexing()
    {
        var p = Profile;
        var open = FixtureEvaluator.Evaluate(p, Open(p, ("Prism1", 1))).Beams[0];
        Assert.Equal(0, open.PrismFacets);
        var inserted = FixtureEvaluator.Evaluate(p, Open(p, ("Prism1", 2), ("Prism1Pos", 45f))).Beams[0];
        Assert.Equal(4, inserted.Prisms[0].Facets);
        Assert.Equal(4, inserted.PrismFacets);
        Assert.False(inserted.Prisms[0].IsOpen);
        Assert.Equal(45f * Deg, inserted.Prisms[0].Rotation, 0.02f);
    }

    [Fact]
    public void Full_cmy_leaves_very_little_light_and_cto_lowers_the_cct()
    {
        var p = Profile;
        var white = FixtureEvaluator.Evaluate(p, Open(p)).Beams[0];
        var cmy = FixtureEvaluator.Evaluate(p, Open(p, ("ColorSub_C", 1f), ("ColorSub_M", 1f), ("ColorSub_Y", 1f))).Beams[0];
        Assert.True(cmy.ColorTransmission < 0.02f, $"CMY full transmits {cmy.ColorTransmission:P1}");
        Assert.True(cmy.PeakCandela < 0.02f * white.PeakCandela);
        var magenta = FixtureEvaluator.Evaluate(p, Open(p, ("ColorSub_M", 1f))).Beams[0];
        var mxy = magenta.Color.ToXy();
        Assert.True(mxy.Y < 0.25f && mxy.X > 0.3f, $"magenta flag gives {mxy}");

        float whiteCct = white.Color.ToCct().Kelvin;
        var cto = FixtureEvaluator.Evaluate(p, Open(p, ("CTO", 3200f))).Beams[0];
        float ctoCct = cto.Color.ToCct().Kelvin;
        Assert.InRange(whiteCct, 5800f, 6200f);
        Assert.InRange(ctoCct, 3050f, 3350f);
        Assert.InRange(cto.ColorTransmission, 0.25f, 0.8f); // an ideal 6000 → 3200 K conversion filter passes about 37 %
    }

    [Fact]
    public void Iris_frost_and_focus_follow_their_channels()
    {
        var p = Profile;
        int iris = p.FindChannels("Iris")[0];
        var buffer = Open(p, ("Frost1", 1f), ("Focus1", 0.25f));
        buffer.SetValue(iris, 255);
        var bs = FixtureEvaluator.Evaluate(p, buffer).Beams[0];
        Assert.Equal(0.16f, bs.Iris, 1e-3f);
        Assert.Equal(1f, bs.Frost, 1e-3f);
        Assert.Equal(0.25f, bs.Focus, 0.01f);
        buffer.SetValue(iris, 0);
        Assert.Equal(1f, FixtureEvaluator.Evaluate(p, buffer).Beams[0].Iris, 1e-3f);
    }

    [Fact]
    public void Strobe_makes_intensity_a_periodic_function_of_time()
    {
        var p = Profile;
        var buffer = Open(p, ("Shutter1Strobe", 5f));
        var state = new FixtureState(p);
        FixtureEvaluator.Evaluate(state, buffer, 0);
        float f = state.Beams[0].StrobeFrequency;
        Assert.Equal(5f, f, 0.1f); // DMX resolution of the 0.8…10 Hz range
        double period = 1.0 / f;
        var samples = new List<float>();
        for (int i = 0; i < 60; i++)
        {
            double t = 10.0 + (i + 0.5) * period / 60.0; // sample one period, away from the flash edges
            FixtureEvaluator.Evaluate(state, buffer, t);
            float a = state.Beams[0].Intensity;
            samples.Add(a);
            Assert.True(state.Beams[0].Strobing);
            FixtureEvaluator.Evaluate(state, buffer, t + 7 * period);
            Assert.Equal(a, state.Beams[0].Intensity, 1e-4f);
        }
        Assert.Contains(samples, s => s > 0.99f);
        Assert.Contains(samples, s => s < 0.01f);
        Assert.True(state.IsAnimating);
    }

    [Fact]
    public void Manual_overrides_replace_dmx_values()
    {
        var p = Profile;
        var buffer = new FixtureDmxBuffer(p);
        var state = FixtureEvaluator.Evaluate(p, buffer, 0, new Dictionary<string, float>
        {
            ["Dimmer"] = 1f,
            ["Shutter1"] = 1f,
            ["Pan"] = -90f,
            ["Gobo1"] = 4f,
            ["Zoom"] = 20f,
        });
        Assert.Equal(1f, state.Beams[0].Intensity, 1e-4f);
        Assert.Equal(-90f * Deg, state.Axes[p.Channels[p.FindChannels("Pan")[0]].Axis].Pan, 1e-4f);
        Assert.Equal(4, state.Beams[0].Gobos[0].Slot);
        Assert.Equal(20f * Deg, state.Beams[0].BeamAngle, 1e-4f);
        Assert.True(state.TryGetAttribute("Pan", out float pan));
        Assert.Equal(-90f, pan, 1e-4f);
        var all = new Dictionary<string, float>();
        state.CopyAttributes(all);
        Assert.Equal(20f, all["Zoom"], 1e-4f);
        Assert.Equal(4f, all["Gobo1"], 1e-4f);
    }
}
