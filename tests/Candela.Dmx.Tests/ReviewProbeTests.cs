using System.Text;
using Candela.Gdtf;

namespace Candela.Dmx.Tests;

/// <summary>Regression tests from the review of the relation, cycle and profile-reload follow-ups.</summary>
public class ReviewProbeTests
{
    [Fact]
    public void Relation_cycles_drop_one_override_edge_each_and_keep_downstream_relations()
    {
        // Nereid 760 "51 Ch Pixel": each pixel's W channel is Multiply-dimmed by the pixel dimmer and (a file quirk) the
        // pixel dimmer is also overridden by W — seven 2-cycles. Only the Override edge of each is dropped.
        var p = TestData.Profile("BeamZ_Pro@Nereid_760", "51 Ch Pixel");
        int kept = p.FollowerRelations.Sum(f => f.Length);
        Assert.Equal(p.Relations.Count - 7, kept);
        Assert.Contains(p.Warnings, w => w.Contains("7 relation(s) closing a cycle", StringComparison.Ordinal));
        foreach (int w in p.FindChannels("ColorAdd_W"))
        {
            if (!p.Channels[w].Name.StartsWith("REF", StringComparison.Ordinal)) continue;
            Assert.Contains(p.FollowerRelations[w], ri => p.Relations[ri].Type == RelationType.Multiply);
        }
        foreach (int d in p.FindChannels("Dimmer"))
            if (p.Channels[d].Name.StartsWith("REF", StringComparison.Ordinal)) Assert.Empty(p.FollowerRelations[d]);
        // every kept relation's master comes before its follower in the evaluation order
        var position = new Dictionary<int, int>();
        for (int i = 0; i < p.FollowerChannels.Length; i++) position[p.FollowerChannels[i]] = i;
        foreach (int c in p.FollowerChannels)
            foreach (int ri in p.FollowerRelations[c])
                if (position.TryGetValue(p.Relations[ri].Master, out int mp)) Assert.True(mp < position[c]);
    }

    [Fact]
    public void Relation_cycle_drops_the_override_edge_whatever_the_channel_order()
    {
        // channel 0 (W) is Multiply-dimmed by channel 1 (dimmer); the dimmer is overridden by W. The lowest-index rule
        // alone would drop W's Multiply; the Override edge must go instead.
        string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <GDTF DataVersion="1.2">
              <FixtureType Name="Cycle" Manufacturer="Test">
                <AttributeDefinitions><Attributes>
                  <Attribute Name="Dimmer" PhysicalUnit="LuminousIntensity"/>
                  <Attribute Name="ColorAdd_W" PhysicalUnit="ColorComponent"/>
                </Attributes></AttributeDefinitions>
                <Geometries><Geometry Name="Body"><Beam Name="Beam" LuminousFlux="1000"/></Geometry></Geometries>
                <DMXModes><DMXMode Name="M" Geometry="Body"><DMXChannels>
                  <DMXChannel Offset="1" Geometry="Beam"><LogicalChannel Attribute="ColorAdd_W"><ChannelFunction Name="W" Attribute="ColorAdd_W"/></LogicalChannel></DMXChannel>
                  <DMXChannel Offset="2" Geometry="Beam"><LogicalChannel Attribute="Dimmer"><ChannelFunction Name="Dim" Attribute="Dimmer"/></LogicalChannel></DMXChannel>
                </DMXChannels>
                <Relations>
                  <Relation Name="Virtual" Master="Beam_Dimmer" Follower="Beam_ColorAdd_W.ColorAdd_W.W" Type="Multiply"/>
                  <Relation Name="Quirk" Master="Beam_ColorAdd_W" Follower="Beam_Dimmer.Dimmer.Dim" Type="Override"/>
                </Relations>
                </DMXMode></DMXModes>
              </FixtureType>
            </GDTF>
            """;
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        var p = FixtureProfile.Compile(GdtfReader.ReadDescription(ms, "cycle.gdtf"));
        int w = p.FindChannels("ColorAdd_W")[0], d = p.FindChannels("Dimmer")[0];
        Assert.Single(p.FollowerRelations[w]);
        Assert.Equal(RelationType.Multiply, p.Relations[p.FollowerRelations[w][0]].Type);
        Assert.Empty(p.FollowerRelations[d]);

        var buffer = new FixtureDmxBuffer(p);
        buffer.SetValue(w, 255);
        buffer.SetValue(d, 0);
        buffer.SetValue(d, 255);
        float lit = FixtureEvaluator.Evaluate(p, buffer, 0).Beams[0].PeakCandela;
        Assert.True(lit > 0f);
        buffer.SetValue(d, 0);
        Assert.Equal(0f, FixtureEvaluator.Evaluate(p, buffer, 0).Beams[0].PeakCandela, 1e-4f * lit);
        buffer.SetValue(d, 128);
        Assert.Equal(128f / 255f, FixtureEvaluator.Evaluate(p, buffer, 0).Beams[0].PeakCandela / lit, 0.01f);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Override_and_multiply_relations_give_the_same_level_in_any_file_order(bool overrideFirst)
    {
        // R is Multiply-dimmed by the beam dimmer and overridden by G while G is in use: the override replaces R's own
        // level and its multipliers, whichever relation the file lists first
        Assert.Equal(0.8f, Level(overrideFirst), 1e-4f);
    }

    private static float Level(bool overrideFirst)
    {
        string mul = """<Relation Name="M" Master="Beam_Dimmer" Follower="Beam_ColorAdd_R.ColorAdd_R.R" Type="Multiply"/>""";
        string ovr = """<Relation Name="O" Master="Beam_ColorAdd_G" Follower="Beam_ColorAdd_R.ColorAdd_R.R" Type="Override"/>""";
        string xml = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <GDTF DataVersion="1.2">
              <FixtureType Name="Zone" Manufacturer="Test">
                <AttributeDefinitions><Attributes>
                  <Attribute Name="Dimmer" PhysicalUnit="LuminousIntensity"/>
                  <Attribute Name="ColorAdd_R" PhysicalUnit="ColorComponent"/>
                  <Attribute Name="ColorAdd_G" PhysicalUnit="ColorComponent"/>
                </Attributes></AttributeDefinitions>
                <Geometries><Geometry Name="Body"><Beam Name="Beam" LuminousFlux="1000"/></Geometry></Geometries>
                <DMXModes><DMXMode Name="M" Geometry="Body"><DMXChannels>
                  <DMXChannel Offset="" Geometry="Beam"><LogicalChannel Attribute="Dimmer"><ChannelFunction Name="Dim" Attribute="Dimmer" Default="255/1"/></LogicalChannel></DMXChannel>
                  <DMXChannel Offset="1" Geometry="Beam"><LogicalChannel Attribute="ColorAdd_R"><ChannelFunction Name="R" Attribute="ColorAdd_R"/></LogicalChannel></DMXChannel>
                  <DMXChannel Offset="2" Geometry="Beam"><LogicalChannel Attribute="ColorAdd_G"><ChannelFunction Name="G" Attribute="ColorAdd_G"/></LogicalChannel></DMXChannel>
                </DMXChannels>
                <Relations>{(overrideFirst ? ovr + mul : mul + ovr)}</Relations>
                </DMXMode></DMXModes>
              </FixtureType>
            </GDTF>
            """;
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        var p = FixtureProfile.Compile(GdtfReader.ReadDescription(ms, "zone.gdtf"));
        var buffer = new FixtureDmxBuffer(p);
        buffer.SetValue(p.FindChannels("ColorAdd_R")[0], 255);
        buffer.SetValue(p.FindChannels("ColorAdd_G")[0], 204);
        var s = FixtureEvaluator.Evaluate(p, buffer, 0, new Dictionary<string, float> { ["Dimmer"] = 0.5f });
        int r = p.FindChannels("ColorAdd_R")[0];
        return s.GetChannel(r).Value * s.Runtime[r].Relation;
    }

    [Fact]
    public void Continue_from_another_mode_with_other_geometry_names_keeps_the_pan_motor()
    {
        // Zonda 3 FX's modes use separate geometry trees ("YOKE Std" / "YOKE Ext"): the pan motor is matched by
        // attribute and rank, so a mode change mid-move continues from where the head physically is
        var std = TestData.Profile("Ayrton@Zonda_3_FX", "Standard");
        var ext = TestData.Profile("Ayrton@Zonda_3_FX", "Extended Pixel 1");
        var b1 = new FixtureDmxBuffer(std);
        DmxEncoder.Encode(b1, "Pan", 90f);
        var s1 = new FixtureState(std);
        FixtureEvaluator.Evaluate(s1, b1, 0, null, applyDynamics: true);
        DmxEncoder.Encode(b1, "Pan", -90f);
        FixtureEvaluator.Evaluate(s1, b1, 0.3, null, applyDynamics: true);
        int a1 = std.Channels[std.FindChannels("Pan")[0]].Axis;
        float mid = s1.Axes[a1].Pan;
        Assert.InRange(mid, -1.4f, 1.4f); // still travelling

        var s2 = new FixtureState(ext);
        s2.ContinueFrom(s1);
        var b2 = new FixtureDmxBuffer(ext);
        DmxEncoder.Encode(b2, "Pan", -90f);
        FixtureEvaluator.Evaluate(s2, b2, 0.3 + 1.0 / 60, null, applyDynamics: true);
        int a2 = ext.Channels[ext.FindChannels("Pan")[0]].Axis;
        Assert.InRange(s2.Axes[a2].Pan, mid - 0.2f, mid + 0.2f);
    }
}
