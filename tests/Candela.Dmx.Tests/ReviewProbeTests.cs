using System.Text;
using Candela.Gdtf;

namespace Candela.Dmx.Tests;

public class ReviewProbeTests
{
    [Fact]
    public void Probe_cycle_breaking_keeps_downstream_relations()
    {
        var p = TestData.Profile("BeamZ_Pro@Nereid_760", "51 Ch Pixel");
        foreach (var w in p.Warnings) Console.WriteLine("W: " + w);
        for (int c = 0; c < p.Channels.Count; c++)
        {
            var ch = p.Channels[c];
            Console.WriteLine($"{c} {ch.Name} @ {ch.InstanceName} follows {string.Join(",", p.FollowerRelations[c].Select(ri => p.Relations[ri].Master + ":" + p.Relations[ri].Type))}");
        }
        Console.WriteLine("order " + string.Join(",", p.FollowerChannels));
        int kept = p.FollowerRelations.Sum(f => f.Length);
        Assert.Equal(p.Relations.Count - 1, kept);
    }

    [Fact]
    public void Probe_override_and_multiply_commute()
    {
        float a = Level(OverrideFirst: true), b = Level(OverrideFirst: false);
        Console.WriteLine($"override first {a}, multiply first {b}");
        Assert.Equal(a, b, 1e-4f);
    }

    private static float Level(bool OverrideFirst)
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
                <Relations>{(OverrideFirst ? ovr + mul : mul + ovr)}</Relations>
                </DMXMode></DMXModes>
              </FixtureType>
            </GDTF>
            """;
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        var p = FixtureProfile.Compile(GdtfReader.ReadDescription(ms, "zone.gdtf"));
        foreach (var w in p.Warnings) Console.WriteLine("W: " + w);
        var buffer = new FixtureDmxBuffer(p);
        buffer.SetValue(p.FindChannels("ColorAdd_R")[0], 255);
        buffer.SetValue(p.FindChannels("ColorAdd_G")[0], 204);
        var s = FixtureEvaluator.Evaluate(p, buffer, 0, new Dictionary<string, float> { ["Dimmer"] = 0.5f });
        int r = p.FindChannels("ColorAdd_R")[0];
        var rt = s.GetChannel(r);
        Console.WriteLine($"R value {rt.Value} relation {s.Runtime[r].Relation}");
        return rt.Value * s.Runtime[r].Relation;
    }

    [Fact]
    public void Probe_continue_from_another_mode_keeps_pan()
    {
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
        Console.WriteLine($"mid pan {mid}");
        var s2 = new FixtureState(ext);
        s2.ContinueFrom(s1);
        var b2 = new FixtureDmxBuffer(ext);
        DmxEncoder.Encode(b2, "Pan", -90f);
        FixtureEvaluator.Evaluate(s2, b2, 0.3 + 1.0 / 60, null, applyDynamics: true);
        int a2 = ext.Channels[ext.FindChannels("Pan")[0]].Axis;
        Console.WriteLine($"after pan {s2.Axes[a2].Pan}");
        Assert.InRange(s2.Axes[a2].Pan, mid - 0.2f, mid + 0.2f);
    }
}
