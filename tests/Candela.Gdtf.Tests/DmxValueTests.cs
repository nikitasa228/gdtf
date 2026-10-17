namespace Candela.Gdtf.Tests;

public sealed class DmxValueTests
{
    [Theory]
    [InlineData("128/1", 128u, 1, DmxConversion.ByteMirroring)]
    [InlineData("32768/2", 32768u, 2, DmxConversion.ByteMirroring)]
    [InlineData(" 255/1s ", 255u, 1, DmxConversion.ByteShifting)]
    [InlineData("16777215/3", 16777215u, 3, DmxConversion.ByteMirroring)]
    [InlineData("4294967295/4", uint.MaxValue, 4, DmxConversion.ByteMirroring)]
    [InlineData("0/1", 0u, 1, DmxConversion.ByteMirroring)]
    public void Parses_value_and_byte_count(string text, uint value, int bytes, DmxConversion conversion)
    {
        var v = DmxValue.Parse(text);
        Assert.Equal(value, v.Value);
        Assert.Equal(bytes, v.ByteCount);
        Assert.Equal(conversion, v.Conversion);
    }

    [Theory]
    [InlineData("")]
    [InlineData("None")]
    [InlineData("256/1")]
    [InlineData("65536/2")]
    [InlineData("1/0")]
    [InlineData("1/5")]
    [InlineData("-1/1")]
    [InlineData("12/x")]
    [InlineData("abc")]
    public void Rejects_invalid_text(string text)
    {
        Assert.False(DmxValue.TryParse(text, out _));
        Assert.Throws<FormatException>(() => DmxValue.Parse(text));
    }

    [Fact]
    public void Bare_integers_get_the_smallest_byte_count()
    {
        Assert.True(DmxValue.TryParse("200", out var a));
        Assert.Equal(new DmxValue(200, 1), a);
        Assert.True(DmxValue.TryParse("300", out var b));
        Assert.Equal(new DmxValue(300, 2), b);
    }

    /// <summary>DIN SPEC 15800:2022 table 1: byte mirroring by default — 255/1 in a 16-bit channel is 65535.</summary>
    [Theory]
    [InlineData("255/1", 2, 65535u)]
    [InlineData("128/1", 2, 0x8080u)]
    [InlineData("128/1", 3, 0x808080u)]
    [InlineData("1/1", 4, 0x01010101u)]
    [InlineData("4660/2", 3, 0x123412u)]
    [InlineData("4660/2", 4, 0x12341234u)]
    [InlineData("0/1", 4, 0u)]
    public void Default_conversion_to_more_bytes_mirrors_the_written_bytes(string text, int target, uint expected) =>
        Assert.Equal(expected, DmxValue.Parse(text).ToResolution(target));

    /// <summary>DIN SPEC 15800:2022 table 1: with the "s" suffix bytes are shifted — 255/1s in a 16-bit channel is 65280.</summary>
    [Theory]
    [InlineData("255/1s", 2, 65280u)]
    [InlineData("128/1s", 2, 32768u)]
    [InlineData("128/1s", 3, 0x800000u)]
    [InlineData("4660/2s", 4, 0x12340000u)]
    public void Shift_suffix_converts_by_byte_shifting(string text, int target, uint expected) =>
        Assert.Equal(expected, DmxValue.Parse(text).ToResolution(target));

    [Theory]
    [InlineData("32768/2", 1, 128u)]
    [InlineData("65535/2", 1, 255u)]
    [InlineData("16777215/3", 2, 65535u)]
    [InlineData("305419896/4", 1, 0x12u)]
    [InlineData("65280/2s", 1, 255u)]
    public void Conversion_to_fewer_bytes_drops_the_least_significant_bytes(string text, int target, uint expected) =>
        Assert.Equal(expected, DmxValue.Parse(text).ToResolution(target));

    [Fact]
    public void Same_resolution_is_unchanged_and_round_trips_through_text()
    {
        foreach (var text in new[] { "128/1", "32768/2", "255/1s", "1234567/3", "4294967295/4" })
        {
            var v = DmxValue.Parse(text);
            Assert.Equal(v.Value, v.ToResolution(v.ByteCount));
            Assert.Equal(text, v.ToString());
            Assert.Equal(v, DmxValue.Parse(v.ToString()));
        }
    }

    [Fact]
    public void Equality_is_field_wise_and_comparison_goes_through_a_resolution()
    {
        var a = DmxValue.Parse("255/1");
        var b = DmxValue.Parse("65535/2");
        Assert.NotEqual(a, b);
        Assert.True(a != b);
        Assert.Equal(a.ToResolution(2), b.ToResolution(2));
        Assert.Equal(a.ConvertTo(2), b);
        Assert.Equal(DmxValue.Parse("255/1"), new DmxValue(255, 1));
        Assert.Equal(DmxValue.Zero, default(DmxValue).ConvertTo(1));
    }

    [Fact]
    public void Normalized_value_and_construction_from_a_fraction()
    {
        Assert.Equal(1.0, DmxValue.Parse("255/1").Normalized, 12);
        Assert.Equal(0.5, DmxValue.Parse("32768/2").Normalized, 4);
        Assert.Equal(new DmxValue(128, 1), DmxValue.FromNormalized(0.5, 1));
        Assert.Equal(new DmxValue(65535, 2), DmxValue.FromNormalized(2.0, 2));
        Assert.Equal(new DmxValue(0, 2), DmxValue.FromNormalized(double.NaN, 2));
        Assert.Equal(255u, DmxValue.MaxFor(1));
        Assert.Equal(65535u, DmxValue.MaxFor(2));
        Assert.Equal(uint.MaxValue, DmxValue.MaxFor(4));
    }

    [Fact]
    public void Invalid_arguments_throw()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DmxValue(256, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DmxValue(1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => DmxValue.Parse("1/1").ToResolution(5));
    }

    [Fact]
    public void Function_values_are_converted_to_the_channel_resolution_as_the_spec_prescribes()
    {
        // No library file mixes resolutions on an addressed channel, so this uses a synthetic 16-bit channel.
        var ft = TestData.ReadXml("""
            <GDTF DataVersion="1.2"><FixtureType Name="T" Manufacturer="M" FixtureTypeID="00000000-0000-0000-0000-000000000001">
              <AttributeDefinitions><Attributes><Attribute Name="Dimmer" PhysicalUnit="None"/><Attribute Name="Pan" PhysicalUnit="Angle"/></Attributes></AttributeDefinitions>
              <Geometries><Geometry Name="Body"/></Geometries>
              <DMXModes><DMXMode Name="M" Geometry="Body"><DMXChannels>
                <DMXChannel Offset="1,2" Geometry="Body" InitialFunction="Body_Dimmer.Dimmer.Dim">
                  <LogicalChannel Attribute="Dimmer"><ChannelFunction Name="Dim" Attribute="Dimmer" DMXFrom="0/1" Default="255/1"/></LogicalChannel>
                </DMXChannel>
                <DMXChannel Offset="3,4" Geometry="Body">
                  <LogicalChannel Attribute="Pan">
                    <ChannelFunction Name="Low" Attribute="Pan" DMXFrom="0/1" Default="255/1s"/>
                    <ChannelFunction Name="High" Attribute="Pan" DMXFrom="128/1"/>
                  </LogicalChannel>
                </DMXChannel>
              </DMXChannels></DMXMode></DMXModes>
            </FixtureType></GDTF>
            """);
        var mode = ft.DmxModes[0];
        var dim = mode.FindChannel("Body_Dimmer")!;
        Assert.Equal(2, dim.Resolution);
        Assert.Equal(65535u, dim.DefaultValue);
        var pan = mode.FindChannel("Body_Pan")!;
        Assert.Equal(65280u, pan.DefaultValue);
        var low = pan.Functions.First();
        var high = pan.Functions.Last();
        Assert.Equal(0u, low.DmxFromValue);
        Assert.Equal(0x8080u, high.DmxFromValue);
        Assert.Equal(0x8080u - 1, low.DmxToValue);
        Assert.Equal(65535u, high.DmxToValue);
        Assert.Empty(ft.Warnings);
    }
}
