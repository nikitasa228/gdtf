using System.Numerics;
using Candela.Photometry;

namespace Candela.Dmx.Tests;

/// <summary>
/// GDTF relations (Multiply, Override) and intensity on real pixel fixtures: master dimmers reach every pixel, relation
/// chains, virtual dimmers written as Override, zone overrides, and dimmer/shutter channel-set names that contradict the
/// physical range.
/// </summary>
public class RelationTests
{
    private const string MagicPanel = "Ayrton@MagicPanel_602";

    [Theory]
    [InlineData("Extended")]
    [InlineData("Standard")]
    [InlineData("Basic")]
    public void MagicPanel_open_white_lights_every_pixel_and_the_master_dimmer_dims_them(string mode)
    {
        // the head dimmer's channel sets are named "Open" at DMX 0 and "Closed" at DMX 255 although its physical range
        // is 0 → 1: the physical range wins, so the master at 255 is full, not closed
        var p = TestData.Profile(MagicPanel, mode);
        Assert.Equal(36, p.Beams.Count);
        var buffer = new FixtureDmxBuffer(p);
        DmxLooks.OpenWhite(buffer);
        var full = FixtureEvaluator.Evaluate(p, buffer);
        float first = full.Beams[0].PeakCandela;
        Assert.True(first > 0f);
        foreach (var b in full.Beams)
        {
            Assert.Equal(1f, b.Intensity, 1e-4f);
            Assert.Equal(first, b.PeakCandela, first * 1e-3f);
        }

        int master = p.FindChannels("Dimmer").Single(c => !p.Channels[c].IsVirtual);
        buffer.SetValue(master, 128);
        var half = FixtureEvaluator.Evaluate(p, buffer);
        foreach (var b in half.Beams) Assert.Equal(128f / 255f, b.PeakCandela / first, 0.01f);
        buffer.SetValue(master, 0);
        Assert.All(FixtureEvaluator.Evaluate(p, buffer).Beams, b => Assert.Equal(0f, b.PeakCandela));
        Assert.Contains(p.Warnings, w => w.Contains("contradict the dimmer's physical range", StringComparison.Ordinal));
    }

    [Fact]
    public void MagicPanel_pixel_virtual_dimmer_and_the_master_multiply()
    {
        var p = TestData.Profile(MagicPanel, "Extended");
        var buffer = new FixtureDmxBuffer(p);
        DmxLooks.OpenWhite(buffer);
        float full = FixtureEvaluator.Evaluate(p, buffer).Beams[0].PeakCandela;
        int master = p.FindChannels("Dimmer").Single(c => !p.Channels[c].IsVirtual);
        buffer.SetValue(master, 128);
        // the first pixel's virtual dimmer at a quarter, by hand: that pixel gets master × pixel, the others the master only
        var state = FixtureEvaluator.Evaluate(p, buffer, 0, new Dictionary<string, float> { ["Dimmer@LED1"] = 0.25f });
        Assert.Equal(0.25f * 128f / 255f, state.Beams[0].PeakCandela / full, 0.01f);
        Assert.Equal(128f / 255f, state.Beams[1].PeakCandela / full, 0.01f);
    }

    [Fact]
    public void A_relation_chain_carries_the_grand_master_to_the_pixel_colours()
    {
        // ArcPod 27Q 17ch: Cells_STR_Dimmer → each cell's virtual dimmer → the cell's R, G, B (and W …)
        var p = TestData.Profile("Prolights@ArcPod27Q", "17ch");
        int grand = p.FindChannel("Cells_STR_Dimmer");
        Assert.True(grand >= 0);
        Assert.Contains(p.Relations, r => r.Master == grand && p.Channels[r.Follower].Class.Kind == AttributeKind.Dimmer);
        Assert.Contains(p.Relations, r => p.Channels[r.Master].IsVirtual && p.Channels[r.Follower].Class.Kind == AttributeKind.ColorAdd);
        var buffer = new FixtureDmxBuffer(p);
        DmxLooks.OpenWhite(buffer);
        var full = FixtureEvaluator.Evaluate(p, buffer);
        buffer.SetValue(grand, 64);
        var quarter = FixtureEvaluator.Evaluate(p, buffer);
        for (int b = 0; b < p.Beams.Count; b++)
        {
            Assert.True(full.Beams[b].PeakCandela > 0f);
            Assert.Equal(64f / 255f, quarter.Beams[b].PeakCandela / full.Beams[b].PeakCandela, 0.01f);
        }
        // and a cell's own virtual dimmer still multiplies on top
        var cell = FixtureEvaluator.Evaluate(p, buffer, 0, new Dictionary<string, float> { ["Dimmer@Cell 2"] = 0.5f });
        Assert.Equal(0.5f * 64f / 255f, cell.Beams[1].PeakCandela / full.Beams[1].PeakCandela, 0.01f);
        Assert.Equal(64f / 255f, cell.Beams[0].PeakCandela / full.Beams[0].PeakCandela, 0.01f);
    }

    [Fact]
    public void A_virtual_dimmer_written_as_Override_scales_the_colours_instead_of_replacing_them()
    {
        // Elation Fuze Par Z175, 5CH RGBW: R, G, B, W addressed, a virtual Beam_Dimmer "over" each with Type="Override".
        // Read literally, every colour would equal the (full) virtual dimmer: always white, R/G/B/W inoperative.
        var p = TestData.Profile("Elation@Fuze_Par_Z175", "5CH RGBW");
        Assert.All(p.Relations, r => Assert.Equal(Gdtf.RelationType.Multiply, r.Type));
        Assert.Contains(p.Warnings, w => w.Contains("read as Multiply", StringComparison.Ordinal));
        var red = Rgbw(p, 1, 0, 0, 0);
        var white = Rgbw(p, 1, 1, 1, 1);
        Assert.True(red.Beams[0].PeakCandela > 0f);
        Assert.True(red.Beams[0].PeakCandela < 0.6f * white.Beams[0].PeakCandela);
        var xy = red.Beams[0].Color.ToXy();
        Assert.True(xy.X > 0.6f, $"red is {xy}");
        // the virtual dimmer set by hand dims the red, it does not turn it white
        var dimmedRed = Rgbw(p, 1, 0, 0, 0, new Dictionary<string, float> { ["Dimmer"] = 0.5f });
        Assert.Equal(0.5f, dimmedRed.Beams[0].PeakCandela / red.Beams[0].PeakCandela, 0.01f);
        Assert.True(Vector2.Distance(xy, dimmedRed.Beams[0].Color.ToXy()) < 1e-3f);
    }

    [Fact]
    public void An_Override_master_replaces_its_followers_only_while_it_is_in_use()
    {
        // BeamZ LCB 246, 8 zones: a virtual zone colour (default full) overrides each pixel's addressed colour
        var p = TestData.Profile("BeamZ@LCB_246", "48 Ch 8 Zone 1");
        var relation = p.Relations.First(r => r.Type == Gdtf.RelationType.Override);
        var master = p.Channels[relation.Master];
        var follower = p.Channels[relation.Follower];
        Assert.True(master.IsVirtual);
        var buffer = new FixtureDmxBuffer(p);
        DmxLooks.OpenWhite(buffer);
        buffer.SetValue(follower.Index, 0);
        // at its default the zone master is not in use: the pixel keeps its own DMX (0), it is not forced to full
        var own = FixtureEvaluator.Evaluate(p, buffer);
        Assert.Equal(0f, own.GetChannel(follower.Index).Value, 1e-4f);
        // set by hand, the zone value replaces the pixel's
        string key = $"{p.Functions[master.FirstFunction].Attribute}@{master.InstanceName}";
        var zone = FixtureEvaluator.Evaluate(p, buffer, 0, new Dictionary<string, float> { [key] = 0.4f });
        Assert.Equal(0.4f, zone.GetChannel(follower.Index).Value, 0.01f);
    }

    [Fact]
    public void A_master_dimmer_whose_relations_miss_the_pixel_colours_still_dims_the_pixels()
    {
        // Cyclops CPX-412OB II: the master dimmer multiplies virtual head colours, while addressed pixel colours light
        // the pixels; it must dim the pixels directly
        var p = TestData.Profile("Cyclops_Lighting@CPX-412OB", "52 Channels");
        int master = p.FindChannel("52 Channels_Dimmer");
        var buffer = new FixtureDmxBuffer(p);
        DmxLooks.OpenWhite(buffer);
        var full = FixtureEvaluator.Evaluate(p, buffer);
        buffer.SetValue(master, 128);
        var half = FixtureEvaluator.Evaluate(p, buffer);
        for (int b = 0; b < p.Beams.Count; b++)
        {
            Assert.True(full.Beams[b].PeakCandela > 0f);
            Assert.Equal(128f / 255f, half.Beams[b].PeakCandela / full.Beams[b].PeakCandela, 0.02f);
        }
    }

    [Theory]
    [InlineData("Robe_Lighting@Robin_Tetra1", null)]
    [InlineData("Prolights@Lumipix15IP", null)]
    [InlineData("Robe_Lighting@Robin_iBar_15", null)]
    [InlineData("BeamZ_Pro@StarColor_540_Wash", "27 Ch + Virtual Dimmers")]
    public void LED_bars_at_open_white_light_every_pixel_and_their_master_dims_all(string prefix, string? mode)
    {
        foreach (var m in mode is null ? TestData.Library.GetFixtureType(TestData.Entry(prefix)).DmxModes.Select(x => x.Name) : [mode])
        {
            var p = TestData.Profile(prefix, m);
            var buffer = new FixtureDmxBuffer(p);
            DmxLooks.OpenWhite(buffer);
            var full = FixtureEvaluator.Evaluate(p, buffer);
            Assert.All(full.Beams, b => Assert.True(b.PeakCandela > 0f, $"{p}: {b.Name} dark"));
            var masters = p.Channels.Where(c => !c.IsVirtual && c.Class.Kind == AttributeKind.Dimmer && c.Beams.Count == p.Beams.Count).ToList();
            foreach (var ch in masters)
            {
                var f = p.Functions[full.GetChannel(ch.Index).Function];
                if (f.IsConstant) continue;
                var half = new FixtureDmxBuffer(p);
                half.CopyFrom(buffer);
                DmxEncoder.EncodeFunction(half, f.Index, f.MinValue + 0.5f * (f.MaxValue - f.MinValue));
                var dimmed = FixtureEvaluator.Evaluate(p, half);
                for (int b = 0; b < p.Beams.Count; b++)
                    Assert.True(dimmed.Beams[b].PeakCandela < 0.75f * full.Beams[b].PeakCandela, $"{p}: {ch} does not dim {p.Beams[b].Name}");
            }
        }
    }

    [Fact]
    public void A_dimmer_range_running_against_its_open_closed_names_is_reversed()
    {
        // LIGHT SKY IPL1015 40CH: virtual pixel dimmers with PhysicalFrom 100 → PhysicalTo 1, "Closed" at DMX 0 and
        // "Open" at the top: DMX 0 must be dark and the top full
        var p = TestData.Profile("LIGHT_SKY@IPL1015_WASH", "40CH ");
        var f = p.Functions.First(x => x.Class.Kind == AttributeKind.Dimmer && x.Sets.Any(s => (s.Flags & ChannelSetFlags.Closed) != 0) &&
            p.Channels[x.Channel].IsVirtual);
        Assert.True(f.PhysicalTo > f.PhysicalFrom);
        Assert.Contains(p.Warnings, w => w.Contains("reversed", StringComparison.Ordinal));
    }

    [Fact]
    public void An_Off_set_of_a_strobe_off_function_leaves_the_shutter_open()
    {
        // Astera AX1 PixelTube: Shutter1 function "Strobe Off" with the channel set "Off" (1060 such sets in 15 files)
        var p = TestData.Profile("Astera_LED_Technology@AX1_PixelTube", "9: RGBWS");
        var shutter = p.Functions.First(f => f.Class.Kind == AttributeKind.Shutter && f.Name == "Strobe Off");
        var off = shutter.Sets.Single(s => s.Name == "Off");
        Assert.True((off.Flags & ChannelSetFlags.Open) != 0);
        Assert.True((off.Flags & ChannelSetFlags.Closed) == 0);
        var buffer = new FixtureDmxBuffer(p);
        buffer.ResetToDefaults();
        Assert.True(FixtureEvaluator.Evaluate(p, buffer).Beams[0].Shutter > 0.99f);
    }

    [Fact]
    public void A_dimmer_function_on_a_channel_of_another_attribute_does_not_dim_the_beam()
    {
        // Vari-Lite VL3600 Profile IP: the Prism2 channel's initial function "Animation2Adjust(m)" carries the attribute
        // Dimmer; at its default (0) it would black the fixture out
        var p = TestData.Profile("Vari-Lite@VL3600_Profile_IP", "16Bit EN Channel Mode");
        var buffer = new FixtureDmxBuffer(p);
        DmxLooks.OpenWhite(buffer);
        var state = FixtureEvaluator.Evaluate(p, buffer);
        Assert.Equal(1f, state.Beams[0].Dimmer, 1e-4f);
        Assert.True(state.Beams[0].PeakCandela > 0f);
        Assert.Contains(p.Warnings, w => w.Contains("Dimmer function on a 'Prism2' channel", StringComparison.Ordinal));
        // a hue channel whose function says Dimmer keeps its hue meaning (BeamZ DOT BAR, 3 Ch HSV)
        var hsv = TestData.Profile("BeamZ_Pro@DOT_BAR_Bat1_IP65", "3 Ch HSV");
        var hue = hsv.Channels.Single(c => c.Attribute == "HSB_Hue");
        Assert.Equal(AttributeKind.HsbHue, hsv.Functions[hue.FirstFunction].Class.Kind);
    }

    [Fact]
    public void Open_white_finds_the_brightest_dimmer_range_and_an_open_shutter()
    {
        // Elation Proton 3K Color: the dimmer channel starts with a "Flash Intensity Blackout" function
        var proton = TestData.Profile("Elation@Proton_3K_Color", "3 Channel");
        var buffer = new FixtureDmxBuffer(proton);
        DmxLooks.OpenWhite(buffer);
        Assert.Equal(1f, FixtureEvaluator.Evaluate(proton, buffer).Beams[0].Intensity, 1e-3f);
        // CLF Poseidon Wash XL shape mode: the ring's effects channel highlights a "Closed" shutter range
        var poseidon = TestData.Profile("CLF_Lighting@Poseidon_WashXL", "41ch - Shape Mode");
        buffer = new FixtureDmxBuffer(poseidon);
        DmxLooks.OpenWhite(buffer);
        Assert.All(FixtureEvaluator.Evaluate(poseidon, buffer).Beams, b => Assert.True(b.PeakCandela > 0f, b.Name));
        // the written channels are reported
        var written = DmxLooks.OpenWhite(buffer);
        Assert.Contains(poseidon.FindChannel("RingReference_Effects1"), written);
    }

    private static FixtureState Rgbw(FixtureProfile p, float r, float g, float b, float w, Dictionary<string, float>? manual = null)
    {
        var buffer = new FixtureDmxBuffer(p);
        DmxLooks.OpenWhite(buffer);
        DmxEncoder.Encode(buffer, "ColorAdd_R", r);
        DmxEncoder.Encode(buffer, "ColorAdd_G", g);
        DmxEncoder.Encode(buffer, "ColorAdd_B", b);
        DmxEncoder.Encode(buffer, "ColorAdd_W", w);
        return FixtureEvaluator.Evaluate(p, buffer, 0, manual);
    }
}
