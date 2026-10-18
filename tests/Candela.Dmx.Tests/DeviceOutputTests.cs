using System.Text;
using Candela.Gdtf;

namespace Candela.Dmx.Tests;

/// <summary>Non-light devices: hazers, fog machines, blowers and flame projectors expose their outputs.</summary>
public class DeviceOutputTests
{
    private static DeviceOutputs Outputs(FixtureProfile p, params (string Attribute, float Value)[] values)
    {
        var buffer = new FixtureDmxBuffer(p);
        foreach (var (a, v) in values) Assert.True(DmxEncoder.Encode(buffer, a, v) > 0, a);
        return FixtureEvaluator.Evaluate(p, buffer).Outputs;
    }

    [Fact]
    public void Hazer_reports_haze_output_and_fan_speed()
    {
        var p = TestData.Profile("ADJ@Entour_Haze_PRO", "4 Channel Mode");
        Assert.Equal(FixtureCategory.Haze, p.Category);
        Assert.True(p.IsAtmospheric);
        var full = Outputs(p, ("Haze1", 100f), ("Fan1", 100f));
        Assert.Equal(1f, full.Haze, 1e-3f);
        Assert.Equal(1f, full.FanSpeed, 1e-3f);
        Assert.Equal(0f, full.Fog);
        var half = Outputs(p, ("Haze1", 100f), ("Fan1", 50f));
        Assert.Equal(0.5f, half.FanSpeed, 0.01f);
        var off = Outputs(p, ("Haze1", 0f));
        Assert.Equal(0f, off.Haze);
    }

    [Theory]
    [InlineData("Briteq@BT-FOG1500", "1Ch")]
    [InlineData("PRstage@FOG_1500", "1Ch")]
    [InlineData("PRstage@LOWFOG_3000", "2CH")]
    [InlineData("Magmatic@SIDEWINDER", "1-CH")]
    public void Fog_machines_report_fog_output(string prefix, string mode)
    {
        var p = TestData.Profile(prefix, mode);
        Assert.True(p.IsAtmospheric);
        int fog = p.FindChannels("Fog1")[0];
        var buffer = new FixtureDmxBuffer(p);
        buffer.SetValue(fog, 255);
        Assert.Equal(1f, FixtureEvaluator.Evaluate(p, buffer).Outputs.Fog, 0.01f);
        buffer.SetValue(fog, 0);
        Assert.True(FixtureEvaluator.Evaluate(p, buffer).Outputs.Fog < 0.01f);
    }

    [Fact]
    public void Low_fog_blower_drives_the_fan_speed()
    {
        var p = TestData.Profile("PRstage@LOWFOG_3000", "2CH");
        int blower = p.FindChannels("Blower1")[0];
        var buffer = new FixtureDmxBuffer(p);
        buffer.SetValue(blower, 255);
        Assert.Equal(1f, FixtureEvaluator.Evaluate(p, buffer).Outputs.FanSpeed, 0.01f);
        buffer.SetValue(blower, 128);
        Assert.InRange(FixtureEvaluator.Evaluate(p, buffer).Outputs.FanSpeed, 0.4f, 0.6f);
    }

    [Fact]
    public void Cooling_fans_of_lights_are_not_device_outputs()
    {
        FixtureProfile? light = null;
        foreach (var mode in TestData.Library.GetFixtureType(TestData.Entry("High_End_Systems@Lonestar")).DmxModes)
        {
            var p = TestData.Profile("High_End_Systems@Lonestar", mode.Name);
            if (p.FindChannels("Fan1").Count > 0)
            {
                light = p;
                break;
            }
        }
        Assert.NotNull(light);
        Assert.False(light!.IsAtmospheric);
        var buffer = new FixtureDmxBuffer(light);
        foreach (int c in light.FindChannels("Fan1")) buffer.SetValue(c, light.Channels[c].MaxValue);
        Assert.Equal(0f, FixtureEvaluator.Evaluate(light, buffer).Outputs.FanSpeed);
    }

    [Fact]
    public void Flame_projector_reports_flame_output_and_height()
    {
        var ft = ReadXml(FlameJet);
        var p = FixtureProfile.Compile(ft);
        Assert.True(p.IsAtmospheric);
        Assert.Empty(p.Beams);
        var on = Outputs(p, ("Flame1", 80f), ("Flame1Height", 3.5f), ("Fan1", -50f));
        Assert.Equal(0.8f, on.Flame, 0.01f);
        Assert.Equal(3.5f, on.FlameHeight, 0.05f);
        Assert.Equal(0.5f, on.FanSpeed, 0.01f);
        Assert.Equal(-1f, on.FanDirection);
        Assert.True(on.Any);
        var buffer = new FixtureDmxBuffer(p);
        buffer.SetValue(p.FindChannels("Flame1")[0], 5); // inside the "Off" channel set
        Assert.Equal(0f, FixtureEvaluator.Evaluate(p, buffer).Outputs.Flame);
    }

    [Fact]
    public void Generator_fire_without_flame_attributes_fires_from_its_dimmer()
    {
        var ft = ReadXml(FlameJet.Replace("Flame1Height", "Dummy2").Replace("\"Flame1\"", "\"Dimmer\""));
        var p = FixtureProfile.Compile(ft, null, new FixtureSpecs { Category = FixtureCategory.GeneratorFire, File = "test.gdtf" });
        Assert.Equal(FixtureCategory.GeneratorFire, p.Category);
        var buffer = new FixtureDmxBuffer(p);
        buffer.SetValue(p.FindChannels("Dimmer")[0], 255);
        Assert.Equal(1f, FixtureEvaluator.Evaluate(p, buffer).Outputs.Flame, 1e-3f);
    }

    private static FixtureType ReadXml(string xml)
    {
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        return GdtfReader.ReadDescription(ms, "flamejet.gdtf");
    }

    private const string FlameJet = """
        <?xml version="1.0" encoding="UTF-8"?>
        <GDTF DataVersion="1.2">
          <FixtureType Name="Flame Jet" ShortName="FJ" LongName="Test Flame Jet" Manufacturer="Test" Description="" Thumbnail=""
                       FixtureTypeID="22222222-3333-4444-5555-666666666666">
            <AttributeDefinitions>
              <FeatureGroups><FeatureGroup Name="Control" Pretty="Control"><Feature Name="Control"/></FeatureGroup></FeatureGroups>
              <Attributes>
                <Attribute Name="Flame1" Pretty="Flame" Feature="Control.Control" PhysicalUnit="Percent"/>
                <Attribute Name="Flame1Height" Pretty="Height" Feature="Control.Control" PhysicalUnit="Length"/>
                <Attribute Name="Fan1" Pretty="Fan" Feature="Control.Control" PhysicalUnit="Percent"/>
              </Attributes>
            </AttributeDefinitions>
            <Models><Model Name="Body" Length="0.3" Width="0.3" Height="0.4" PrimitiveType="Cube"/></Models>
            <Geometries><Geometry Name="Body" Model="Body" Position="{1,0,0,0}{0,1,0,0}{0,0,1,0}{0,0,0,1}"/></Geometries>
            <DMXModes>
              <DMXMode Name="3ch" Geometry="Body">
                <DMXChannels>
                  <DMXChannel DMXBreak="1" Offset="1" Geometry="Body">
                    <LogicalChannel Attribute="Flame1">
                      <ChannelFunction Name="Flame" Attribute="Flame1" DMXFrom="0/1" PhysicalFrom="0" PhysicalTo="100">
                        <ChannelSet Name="Off" DMXFrom="0/1"/>
                        <ChannelSet Name="Fire" DMXFrom="10/1"/>
                      </ChannelFunction>
                    </LogicalChannel>
                  </DMXChannel>
                  <DMXChannel DMXBreak="1" Offset="2" Geometry="Body">
                    <LogicalChannel Attribute="Flame1Height">
                      <ChannelFunction Name="Height" Attribute="Flame1Height" DMXFrom="0/1" PhysicalFrom="0" PhysicalTo="8"/>
                    </LogicalChannel>
                  </DMXChannel>
                  <DMXChannel DMXBreak="1" Offset="3" Geometry="Body">
                    <LogicalChannel Attribute="Fan1">
                      <ChannelFunction Name="Fan" Attribute="Fan1" DMXFrom="0/1" PhysicalFrom="-100" PhysicalTo="100"/>
                    </LogicalChannel>
                  </DMXChannel>
                </DMXChannels>
              </DMXMode>
            </DMXModes>
          </FixtureType>
        </GDTF>
        """;
}
