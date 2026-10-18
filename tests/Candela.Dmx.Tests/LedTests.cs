using System.Numerics;
using Candela.Photometry;

namespace Candela.Dmx.Tests;

/// <summary>LED fixtures: an RGBW wash with GDTF emitter data, virtual dimmers (relations) and a multi-pixel bar.</summary>
public class LedTests
{
    private const string ArcPod = "Prolights@ArcPod27Q";
    private static readonly string[] Rgbw = ["ColorAdd_R", "ColorAdd_G", "ColorAdd_B", "ColorAdd_W"];

    /// <summary>The ArcPod mode with an addressed dimmer, a shutter and RGBW.</summary>
    private static FixtureProfile WashWithDimmer()
    {
        foreach (var mode in TestData.Library.GetFixtureType(TestData.Entry(ArcPod)).DmxModes)
        {
            var p = TestData.Profile(ArcPod, mode.Name);
            var dim = p.FindChannels("Dimmer");
            if (dim.Count > 0 && !p.Channels[dim[0]].IsVirtual && p.FindChannels("ColorAdd_W").Count > 0 && p.FindChannels("Shutter1").Count > 0)
                return p;
        }
        throw new InvalidOperationException("No ArcPod mode with dimmer, shutter and RGBW.");
    }

    private static FixtureDmxBuffer Levels(FixtureProfile p, float r, float g, float b, float w, float dimmer = 1f)
    {
        var buffer = new FixtureDmxBuffer(p);
        if (p.FindChannels("Shutter1").Count > 0) DmxEncoder.Encode(buffer, "Shutter1", 1f);
        if (p.FindChannels("Dimmer").Count > 0) DmxEncoder.Encode(buffer, "Dimmer", dimmer);
        float[] v = [r, g, b, w];
        for (int i = 0; i < 4; i++)
        {
            int c = p.FindChannels(Rgbw[i])[0];
            float scale = p.Functions[p.Channels[c].FirstFunction].LevelScale;
            DmxEncoder.Encode(buffer, Rgbw[i], v[i] * scale);
        }
        return buffer;
    }

    [Fact]
    public void Pure_emitters_have_the_gdtf_emitter_chromaticities()
    {
        var p = WashWithDimmer();
        for (int i = 0; i < 4; i++)
        {
            var level = new float[4];
            level[i] = 1f;
            var bs = FixtureEvaluator.Evaluate(p, Levels(p, level[0], level[1], level[2], level[3])).Beams[0];
            var f = p.Functions[p.Channels[p.FindChannels(Rgbw[i])[0]].FirstFunction];
            var emitter = p.Emitters[f.Emitter];
            Assert.NotNull(emitter.Source); // the colour comes from the file's PhysicalDescriptions
            var c = emitter.Source!.Color!.Value;
            var expected = LightColor.FromXy(c.X, c.Y);
            Assert.True(Vector3.Distance(expected.Rgb, bs.Color.Rgb) < 1e-3f, $"{Rgbw[i]}: {bs.Color} vs {expected}");
            Assert.True(bs.ColorTransmission is > 0f and < 1f);
        }
    }

    [Fact]
    public void Full_white_is_the_emitter_mix_and_intensity_scales_peak_candela()
    {
        var p = WashWithDimmer();
        var beam = p.Beams[0];
        Assert.Equal(PhotometrySource.SpecsCandela, beam.PhotometrySource);
        var full = FixtureEvaluator.Evaluate(p, Levels(p, 1, 1, 1, 1)).Beams[0];
        var mix = ColorMixing.MixAllFull(beam.Emitters);
        Assert.True(Vector2.Distance(mix.Color.ToXy(), full.Color.ToXy()) < 1e-3f);
        Assert.Equal(1f, full.ColorTransmission, 1e-3f);
        float specCd = (float)p.Specs!.MaxLuminousIntensityCd!.Value;
        Assert.Equal(specCd, full.PeakCandela, specCd * 0.01f);
        var cct = full.Color.ToCct();
        Assert.True(cct.IsMeaningful, $"RGBW full white is not white: {cct}");

        foreach (float d in new[] { 0.75f, 0.3f, 0.05f })
        {
            var dimmed = FixtureEvaluator.Evaluate(p, Levels(p, 1, 1, 1, 1, d)).Beams[0];
            Assert.Equal(d, dimmed.Intensity, 0.005f);
            Assert.Equal(full.PeakCandela * dimmed.Intensity, dimmed.PeakCandela, full.PeakCandela * 1e-4f);
        }
        var red = FixtureEvaluator.Evaluate(p, Levels(p, 1, 0, 0, 0)).Beams[0];
        Assert.True(red.PeakCandela < 0.5f * full.PeakCandela);
    }

    [Fact]
    public void Led_strobe_is_periodic()
    {
        var p = WashWithDimmer();
        var strobe = p.FindChannels("Shutter1Strobe");
        Assert.NotEmpty(strobe);
        var buffer = Levels(p, 1, 1, 1, 1);
        DmxEncoder.Encode(buffer, "Shutter1Strobe", 10f);
        var state = new FixtureState(p);
        FixtureEvaluator.Evaluate(state, buffer, 0);
        float f = state.Beams[0].StrobeFrequency;
        Assert.True(f > 0f);
        var values = new HashSet<float>();
        for (int i = 0; i < 50; i++)
        {
            double t = 3 + (i + 0.5) / 50.0 / f;
            FixtureEvaluator.Evaluate(state, buffer, t);
            float a = state.Beams[0].PeakCandela;
            values.Add(MathF.Round(a));
            FixtureEvaluator.Evaluate(state, buffer, t + 11 / f);
            Assert.Equal(a, state.Beams[0].PeakCandela, Math.Max(1f, a * 1e-4f));
        }
        Assert.True(values.Count >= 2);
    }

    [Fact]
    public void A_dimmer_relation_multiplies_its_followers_once()
    {
        // a mode where an addressed Dimmer is the Multiply master of the colour channels (virtual-dimmer style)
        var (p, relation) = FindAddressedRelationMaster();
        var master = p.Channels[relation.Master];
        var follower = p.Channels[relation.Follower];
        var beamIndex = follower.Beams[0];
        float At(float dimmer)
        {
            var buffer = new FixtureDmxBuffer(p);
            foreach (var c in p.Channels)
                if (c.Class.Kind == AttributeKind.ColorAdd) buffer.SetValue(c.Index, 0);
            if (p.FindChannels("Shutter1").Count > 0) DmxEncoder.Encode(buffer, "Shutter1", 1f);
            var ff = p.Functions[relation.FollowerFunction];
            DmxEncoder.EncodeFunction(buffer, ff.Index, ff.MaxValue);
            buffer.SetValue(master.Index, (uint)MathF.Round(dimmer * master.MaxValue));
            return FixtureEvaluator.Evaluate(p, buffer).Beams[beamIndex].PeakCandela;
        }
        float full = At(1f), half = At(0.5f), off = At(0f);
        Assert.True(full > 0f);
        Assert.Equal(0.5f, half / full, 0.01f); // not 0.25: the master is not applied a second time as a beam dimmer
        Assert.Equal(0f, off);
    }

    [Fact]
    public void A_virtual_dimmer_without_address_leaves_the_colour_channels_in_control()
    {
        // ArcPod 5ch: R, G, B, W addressed, Dimmer virtual (relation master)
        var p = TestData.Profile(ArcPod, "5ch");
        var dim = p.Channels[p.FindChannels("Dimmer")[0]];
        Assert.True(dim.IsVirtual);
        Assert.Contains(p.Relations, r => r.Master == dim.Index);
        var full = FixtureEvaluator.Evaluate(p, Levels(p, 1, 1, 1, 1)).Beams[0];
        Assert.Equal(1f, full.ColorTransmission, 1e-3f);
        var half = FixtureEvaluator.Evaluate(p, Levels(p, 0.5f, 0.5f, 0.5f, 0.5f)).Beams[0];
        Assert.Equal(0.5f * full.PeakCandela, half.PeakCandela, full.PeakCandela * 0.01f);
        // the virtual dimmer can still be set by hand
        var manual = FixtureEvaluator.Evaluate(p, Levels(p, 1, 1, 1, 1), 0, new Dictionary<string, float> { ["Dimmer"] = 0.25f }).Beams[0];
        Assert.Equal(0.25f * full.PeakCandela, manual.PeakCandela, full.PeakCandela * 0.01f);
    }

    [Fact]
    public void Multi_pixel_bar_gives_one_beam_per_pixel_with_independent_colours()
    {
        var (p, pixels) = FindPixelBar();
        Assert.True(pixels.Count >= 4);
        var buffer = new FixtureDmxBuffer(p);
        foreach (var c in p.Channels)
            if (c.Class.Kind == AttributeKind.ColorAdd) buffer.SetValue(c.Index, 0);
        foreach (var c in p.Channels)
            if (c.Class.Kind == AttributeKind.Dimmer) buffer.SetValue(c.Index, c.MaxValue);
        if (p.FindChannels("Shutter1").Count > 0) DmxEncoder.Encode(buffer, "Shutter1", 1f);
        string[] codes = ["ColorAdd_R", "ColorAdd_G", "ColorAdd_B"];
        for (int i = 0; i < pixels.Count; i++)
        {
            var c = p.Channels[pixels[i].Channels[i % 3]];
            buffer.SetValue(c.Index, c.MaxValue);
        }
        var state = FixtureEvaluator.Evaluate(p, buffer);
        Assert.Equal(p.Beams.Count, state.Beams.Count);
        for (int i = 0; i < pixels.Count; i++)
        {
            var bs = state.Beams[pixels[i].Beam];
            var f = p.Functions[p.Channels[pixels[i].Channels[i % 3]].FirstFunction];
            var emitter = p.Emitters[f.Emitter];
            Assert.True(bs.PeakCandela > 0f, $"pixel {i} is dark");
            Assert.True(Vector2.Distance(bs.Color.ToXy(), LightColor.FromXy(emitter.Chromaticity).ToXy()) < 0.01f,
                $"pixel {i} ({codes[i % 3]}) is {bs.Color.ToXy()}");
        }
        // a pixel without any colour stays dark
        var dark = p.Beams.Select(b => b.Index).Except(pixels.Select(x => x.Beam)).ToList();
        foreach (int b in dark) Assert.Equal(0f, state.Beams[b].PeakCandela);
    }

    /// <summary>A library mode where an addressed Dimmer is a Multiply master of an addressed ColorAdd follower.</summary>
    private static (FixtureProfile, ProfileRelation) FindAddressedRelationMaster()
    {
        string[] candidates = ["BeamZ_Pro@Neutron_Atom_TM_IP65", "ADJ@Vintage_X", "ADJ@ElectraPix_Bar_8"];
        foreach (var prefix in candidates)
            foreach (var mode in TestData.Library.GetFixtureType(TestData.Entry(prefix)).DmxModes)
            {
                var p = TestData.Profile(prefix, mode.Name);
                foreach (var r in p.Relations)
                {
                    var m = p.Channels[r.Master];
                    var f = p.Channels[r.Follower];
                    if (r.Type == Gdtf.RelationType.Multiply && !m.IsVirtual && m.Class.Kind == AttributeKind.Dimmer && !f.IsVirtual &&
                        f.Class.Kind == AttributeKind.ColorAdd && f.Beams.Count > 0)
                        return (p, r);
                }
            }
        throw new InvalidOperationException("No addressed relation master found.");
    }

    /// <summary>An LED bar mode whose pixels (beams) each have their own addressed R, G and B channels.</summary>
    private static (FixtureProfile, List<(int Beam, int[] Channels)>) FindPixelBar()
    {
        string[] candidates = ["Robe_Lighting@Robin_Tetra1", "Prolights@Lumipix15IP", "Robe_Lighting@Robin_iBar_15"];
        foreach (var prefix in candidates)
            foreach (var mode in TestData.Library.GetFixtureType(TestData.Entry(prefix)).DmxModes)
            {
                var p = TestData.Profile(prefix, mode.Name);
                var pixels = new List<(int, int[])>();
                foreach (var b in p.Beams)
                {
                    var own = new int[3];
                    bool ok = true;
                    string[] codes = ["ColorAdd_R", "ColorAdd_G", "ColorAdd_B"];
                    for (int k = 0; k < 3 && ok; k++)
                    {
                        int c = Array.FindIndex(b.OwnChannels, ch => p.Channels[ch].Attribute == codes[k] && !p.Channels[ch].IsVirtual &&
                            p.Channels[ch].Beams.Count == 1);
                        if (c < 0) ok = false; else own[k] = b.OwnChannels[c];
                    }
                    if (ok) pixels.Add((b.Index, own));
                }
                if (pixels.Count >= 4) return (p, pixels);
            }
        throw new InvalidOperationException("No pixel bar found.");
    }
}
