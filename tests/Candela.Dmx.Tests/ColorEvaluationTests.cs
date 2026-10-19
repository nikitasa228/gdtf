using System.Numerics;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;
using Candela.Dmx.Input;
using Candela.Photometry;

namespace Candela.Dmx.Tests;

/// <summary>
/// Colour of LED engines and gels: the full-white point against the datasheet's CCT range, colour-wheel presets on
/// additive engines, LED rings that a mode does not address, and MVR gel colours on fixtures without a colour system.
/// </summary>
public class ColorEvaluationTests
{
    private const string Zonda = "Ayrton@Zonda_3_FX"; // RGBW pixels, virtual colour wheel 'Color1' (SCARLET at DMX 24), CTC 8000 → 2700 K
    private const string Ellipsoidal = "ACME@ACME_ELLIPSOIDAL_300_D2550"; // a conventional profile: dimmer only
    private const string Viper = "Martin_Professional@MAC_Viper_Profile"; // CMY and colour wheels

    private static readonly string[] Rgbw = ["ColorAdd_R", "ColorAdd_G", "ColorAdd_B", "ColorAdd_W"];

    /// <summary>Open white with the RGBW emitters at <paramref name="level"/> and the colour preset channel at <paramref name="preset"/>.</summary>
    private static FixtureDmxBuffer ZondaLook(FixtureProfile p, float level, uint preset = 0)
    {
        var buffer = new FixtureDmxBuffer(p);
        DmxLooks.OpenWhite(buffer);
        foreach (var a in Rgbw) Assert.True(DmxEncoder.Encode(buffer, a, level) > 0, a);
        foreach (int c in p.FindChannels("Color1")) buffer.SetValue(c, preset);
        return buffer;
    }

    [Fact]
    public void Zonda_full_white_lies_in_the_datasheet_cct_range_and_is_marked_as_an_estimate()
    {
        // the file's emitters (Y 100 each) mix to x 0.255, y 0.178 — far below the locus; the datasheet gives 2700–8000 K
        var range = TestData.Entry(Zonda).Specs!.ColorTemperatureK!.Value;
        var p = TestData.Profile(Zonda, "Basic");
        Assert.Contains(p.Warnings, w => w.Contains("(estimate)", StringComparison.Ordinal) && w.Contains("2700–8000 K", StringComparison.Ordinal));
        var s = FixtureEvaluator.Evaluate(p, ZondaLook(p, 1f));
        int lit = 0;
        foreach (var b in s.Beams)
        {
            if (!(b.PeakCandela > 0f)) continue;
            lit++;
            Assert.True(b.Beam.WhitePointEstimated, b.Name);
            var cct = b.Color.ToCct();
            Assert.True(cct.IsMeaningful, $"{b.Name}: {b.Color.ToXy()}");
            Assert.InRange(cct.Kelvin, (float)range.Min / 1.02f, (float)range.Max * 1.02f);
            Assert.InRange(MathF.Abs(cct.Duv), 0f, 0.02f);
            Assert.Equal(b.Beam.WhitePointKelvin, cct.Kelvin, b.Beam.WhitePointKelvin * 0.01f);
        }
        Assert.True(lit >= 3, $"{lit} lit beams");
    }

    [Fact]
    public void A_colour_preset_on_an_rgbw_engine_mixes_the_slot_colour()
    {
        var p = TestData.Profile(Zonda, "Basic");
        int ch = p.FindChannels("Color1")[0];
        var f = p.Functions.Single(x => x.Channel == ch && x.SetAt(24) is { WheelSlot: > 0 });
        var set = f.SetAt(24)!;
        Assert.Equal("SCARLET", set.Name.Trim(), ignoreCase: true);
        var slot = p.Wheels[f.Wheel].Source.GetSlot(set.WheelSlot)!;
        var slotXy = new Vector2(slot.Color.X, slot.Color.Y);

        var white = FixtureEvaluator.Evaluate(p, ZondaLook(p, 1f));
        var scarlet = FixtureEvaluator.Evaluate(p, ZondaLook(p, 1f, 24));
        int checkedBeams = 0;
        foreach (int b in p.Channels[ch].Beams)
        {
            var beam = scarlet.Beams[b];
            if (!(white.Beams[b].PeakCandela > 0f)) continue;
            checkedBeams++;
            Assert.True(Vector2.Distance(slotXy, beam.Color.ToXy()) <= 0.02f, $"{beam.Name}: {beam.Color.ToXy()} vs slot {slotXy}");
            // a colour the emitters make with less light than their white
            Assert.InRange(beam.Flux, 0.01f * white.Beams[b].Flux, white.Beams[b].Flux);
        }
        Assert.True(checkedBeams >= 3, $"{checkedBeams} beams");

        // the preset colours the emitters' light: with the emitters at 0 the fixture stays dark
        var dark = FixtureEvaluator.Evaluate(p, ZondaLook(p, 0f, 24));
        Assert.All(dark.Beams, b => Assert.Equal(0f, b.PeakCandela));
    }

    [Fact]
    public void A_glow_ring_no_channel_of_the_mode_addresses_stays_dark()
    {
        // Zonda 3 FX 'Basic' has no channel for the 'Liquid Effect' ring (it is lit by the 'Zonda Effect' RGB of the
        // '+ Liquid' modes); it used to take the full-white emitters and glow at RGBW 0 … 255
        var basic = TestData.Profile(Zonda, "Basic");
        var rings = basic.Beams.Where(b => b.Name.StartsWith("Liquid", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.NotEmpty(rings);
        Assert.All(rings, b => Assert.True(b.Unaddressed, b.Name));
        Assert.Contains(basic.Warnings, w => w.Contains("Liquid", StringComparison.Ordinal) && w.Contains("stays dark", StringComparison.Ordinal));
        foreach (float level in new[] { 0f, 1f })
        {
            var s = FixtureEvaluator.Evaluate(basic, ZondaLook(basic, level));
            foreach (var ring in rings) Assert.Equal(0f, s.Beams[ring.Index].PeakCandela);
            if (level > 0f) Assert.Contains(s.Beams, b => b.PeakCandela > 0f);
        }

        var liquid = TestData.Profile(Zonda, "Extended Pixel 1 + Liquid");
        Assert.DoesNotContain(liquid.Beams, b => b.Unaddressed);
    }

    [Fact]
    public void A_gel_colours_a_white_fixture_and_leaves_a_colour_mixing_fixture_alone()
    {
        var p = TestData.Profile(Ellipsoidal);
        Assert.False(p.Beams[0].HasColorSystem);
        var buffer = new FixtureDmxBuffer(p);
        DmxLooks.OpenWhite(buffer);
        var state = new FixtureState(p);
        FixtureEvaluator.Evaluate(state, buffer, 0, null, applyDynamics: false);
        var open = state.Beams[0];
        var openXy = open.Color.ToXy();
        float openFlux = open.Flux;
        Assert.True(openFlux > 0f);

        // a deep red gel passing 9 % of the light (Lee 106 Primary Red: x 0.69, y 0.30)
        var red = new CieColor(0.69f, 0.30f, 9f);
        state.SetGel(red);
        Assert.Equal(red, state.Gel);
        FixtureEvaluator.Evaluate(state, buffer, 0.1, null, applyDynamics: false);
        var gelled = state.Beams[0];
        var xy = gelled.Color.ToXy();
        Assert.True(Vector2.Distance(xy, new Vector2(red.X, red.Y)) < 0.25f * Vector2.Distance(openXy, new Vector2(red.X, red.Y)), $"{xy} from {openXy}");
        Assert.True(xy.X > 0.6f, $"{xy}");
        Assert.InRange(gelled.Flux, 0.03f * openFlux, 0.3f * openFlux);
        Assert.Equal(open.BeamAngle, gelled.BeamAngle);

        // invalid colours remove the gel
        state.SetGel(new CieColor(float.NaN, 0.3f, 50f));
        Assert.Null(state.Gel);
        FixtureEvaluator.Evaluate(state, buffer, 0.2, null, applyDynamics: false);
        Assert.Equal(openFlux, state.Beams[0].Flux, openFlux * 1e-4f);

        // a fixture with its own colour system ignores the gel
        var viper = TestData.Profile(Viper, "Basic");
        Assert.True(viper.Beams[0].HasColorSystem);
        var vb = new FixtureDmxBuffer(viper);
        DmxLooks.OpenWhite(vb);
        var vs = new FixtureState(viper);
        FixtureEvaluator.Evaluate(vs, vb, 0, null, applyDynamics: false);
        var before = vs.Beams[0].Color.ToXy();
        float beforeFlux = vs.Beams[0].Flux;
        vs.SetGel(red);
        FixtureEvaluator.Evaluate(vs, vb, 0.1, null, applyDynamics: false);
        Assert.Equal(before, vs.Beams[0].Color.ToXy());
        Assert.Equal(beforeFlux, vs.Beams[0].Flux);
    }

    [Fact]
    public void The_dmx_engine_applies_the_fixture_components_gel_colour()
    {
        var scene = new Scene();
        var entity = new Entity("profile");
        var fixture = entity.Add(new FixtureComponent
        {
            GdtfSpec = TestData.Entry(Ellipsoidal).FileName,
            Addresses = { new DmxAddress(1, 1) },
            ManualAttributes = { ["Dimmer"] = 1f },
            Color = new CieColor(0.15f, 0.06f, 5f), // a deep blue gel
        });
        scene.Add(entity);
        using var engine = new DmxEngine(scene, TestData.Profiles, new DmxInputHub()) { ApplyDynamics = false };
        engine.Update(0);
        var state = engine.GetState(entity.Id)!;
        Assert.Equal(fixture.Color, state.Gel);
        var xy = state.Beams[0].Color.ToXy();
        Assert.True(xy.Y < 0.2f && xy.X < 0.25f, $"{xy}");

        // removing the colour from the component removes the gel
        fixture.Color = null;
        engine.Invalidate(entity.Id);
        engine.Update(0.02);
        Assert.Null(state.Gel);
        Assert.True(state.Beams[0].Color.ToXy().Y > 0.3f);
    }
}
