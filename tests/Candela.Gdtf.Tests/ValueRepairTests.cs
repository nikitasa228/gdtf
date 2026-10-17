namespace Candela.Gdtf.Tests;

/// <summary>
/// Values that real files get wrong in recurring ways: DMX values that overflow their byte count, "none" written as an
/// unsigned −1, hexadecimal ids, and power inputs that name a GDTF 1.0/1.1 connector instead of a wiring object.
/// </summary>
public sealed class ValueRepairTests
{
    [Theory]
    [InlineData("256/1", 255u, 1)]
    [InlineData("300/1s", 255u, 1)]
    [InlineData("70000/2", 65535u, 2)]
    [InlineData(" 65536 / 2 ", 65535u, 2)]
    public void Overflowing_dmx_values_are_clamped_to_their_byte_count(string text, uint expected, int bytes)
    {
        Assert.True(ReadContext.TryParseDmxRepairing(text, out var value, out bool clamped));
        Assert.True(clamped);
        Assert.Equal((expected, bytes), (value.Value, value.ByteCount));
    }

    [Theory]
    [InlineData("4294967295/1")] // −1 written unsigned: "no value"
    [InlineData("2147483648/2")]
    [InlineData("12/5")]
    [InlineData("abc/1")]
    [InlineData("None")]
    public void Sentinels_and_malformed_dmx_values_are_rejected(string text)
    {
        Assert.False(ReadContext.TryParseDmxRepairing(text, out _, out _));
    }

    [Fact]
    public void Well_formed_dmx_values_are_not_reported_as_repaired()
    {
        Assert.True(ReadContext.TryParseDmxRepairing("255/1s", out var value, out bool clamped));
        Assert.False(clamped);
        Assert.Equal(DmxConversion.ByteShifting, value.Conversion);
    }

    [Fact]
    public void Repairs_are_applied_while_reading_and_reported()
    {
        var ft = TestData.ReadXml("""
            <GDTF DataVersion="1.2">
              <FixtureType Name="Repair" Manufacturer="M" FixtureTypeID="00000000-0000-0000-0000-000000000001">
                <AttributeDefinitions><Attributes><Attribute Name="Gobo1" PhysicalUnit="None"/></Attributes></AttributeDefinitions>
                <PhysicalDescriptions>
                  <Connectors><Connector Name="Mains In " Type="PowerconTRUE1"/><Connector Name="DMX In" Type="XLR5" DMXBreak="1"/></Connectors>
                  <Properties><PowerConsumption Value="300" PowerFactor="0.9" Connector="mains in"/><PowerConsumption Value="1" Connector="Nope"/></Properties>
                </PhysicalDescriptions>
                <Geometries><Geometry Name="Body"/></Geometries>
                <DMXModes>
                  <DMXMode Name="Mode" Geometry="Body">
                    <DMXChannels>
                      <DMXChannel Offset="1" Geometry="Body" Highlight="4294967295/1">
                        <LogicalChannel Attribute="Gobo1">
                          <ChannelFunction Attribute="Gobo1" DMXFrom="0/1">
                            <ChannelSet Name="Open" DMXFrom="0/1"/>
                            <ChannelSet Name="End" DMXFrom="256/1"/>
                          </ChannelFunction>
                        </LogicalChannel>
                      </DMXChannel>
                    </DMXChannels>
                  </DMXMode>
                </DMXModes>
                <Protocols>
                  <FTRDM ManufacturerID="5253" DeviceModelID="0x00E2">
                    <SoftwareVersionID Value="1A"><DMXPersonality Value="0x1" DMXMode="Mode"/></SoftwareVersionID>
                  </FTRDM>
                </Protocols>
              </FixtureType>
            </GDTF>
            """);

        var channel = ft.DmxModes.Single().Channels.Single();
        Assert.Null(channel.Highlight);
        var sets = channel.Functions.Single().ChannelSets;
        Assert.Equal((0u, 254u), (sets[0].DmxFromValue, sets[0].DmxToValue));
        Assert.Equal((255u, 255u), (sets[1].DmxFromValue, sets[1].DmxToValue));

        var rdm = ft.Protocols.Rdm.Single();
        Assert.Equal((0x5253u, 0xE2u), (rdm.ManufacturerId, rdm.DeviceModelId));
        Assert.Equal(0x1Au, rdm.SoftwareVersions.Single().Value);
        Assert.Same(ft.DmxModes[0], rdm.SoftwareVersions[0].Personalities.Single().DmxMode);

        var power = ft.PhysicalDescriptions.Properties.PowerConsumptions;
        Assert.Null(power[0].Connector);
        Assert.Same(ft.PhysicalDescriptions.Connectors[0], power[0].LegacyConnector);
        Assert.Null(power[1].LegacyConnector);

        string all = string.Join("\n", ft.Warnings);
        Assert.Contains("DMXFrom=\"256/1\" does not fit its byte count; clamped to 255/1", all);
        Assert.Contains("Highlight=\"4294967295/1\" is not a DMX value", all);
        Assert.Contains("Connector 'Nope' is neither a wiring object nor a connector", all);
        Assert.DoesNotContain("mains in", all, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Real_files_link_power_inputs_to_legacy_connectors()
    {
        // GDTF 1.2 files without wiring objects name a <Connector> of PhysicalDescriptions as their power input.
        foreach (var (prefix, connector) in new[]
        {
            ("JB-Lighting@A12@", "powerCON IN"),
            ("DTS_Lighting@Synergy_7_Profile@", "Mains IN"),
            ("Clay_Paky@Mini_Xtylos_HPE@", "POWER IN "),
        })
        {
            var ft = GdtfReader.ReadFile(TestData.Fixture(prefix));
            var power = Assert.Single(ft.PhysicalDescriptions.Properties.PowerConsumptions);
            Assert.Equal(connector, power.LegacyConnector?.Name);
            Assert.True(power.Value > 0 && power.PowerFactor is > 0 and <= 1, prefix);
            Assert.DoesNotContain(ft.Warnings, w => w.Path.StartsWith("Properties", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Real_overflowing_channel_set_is_clamped_instead_of_restarting_at_zero()
    {
        // The "Counter Clockwise Rotation" function starts at 229 and ends with sets "Fastest" (255/1) and "" (256/1).
        // Read as 0 the last set would fall outside its function; clamped it stays at the top of the range.
        var ft = GdtfReader.ReadFile(TestData.Fixture("ADJ@Hydro_Profile@"));
        var functions = ft.DmxModes.SelectMany(m => m.Channels).SelectMany(c => c.Functions).ToList();
        var rotation = functions.Where(f => f.Name == "Counter Clockwise Rotation" && f.ChannelSets.Count > 0 && f.ChannelSets[^1].Name == "").ToList();
        Assert.NotEmpty(rotation);
        foreach (var f in rotation)
            Assert.Equal((229u, 255u, 255u), (f.DmxFromValue, f.DmxToValue, f.ChannelSets[^1].DmxFromValue));
        foreach (var f in functions)
            foreach (var s in f.ChannelSets)
                Assert.InRange(s.DmxFromValue, f.DmxFromValue, f.DmxToValue);
        Assert.Contains(ft.Warnings, w => w.Message.Contains("\"256/1\" does not fit its byte count", StringComparison.Ordinal));
    }
}
