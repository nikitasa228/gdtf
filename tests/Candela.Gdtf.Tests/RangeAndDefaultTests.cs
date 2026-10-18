namespace Candela.Gdtf.Tests;

/// <summary>
/// Requests from the DMX layer: Beam attributes record whether the file gave them, mode-master ranges keep the spec's
/// range semantics across resolutions, and DMXTo only counts functions that can be active at the same time.
/// </summary>
public sealed class RangeAndDefaultTests
{
    private static FixtureType Read(string geometries, string modes) => TestData.ReadXml($"""
        <GDTF DataVersion="1.2">
          <FixtureType Name="T" Manufacturer="M">
            <Geometries>{geometries}</Geometries>
            <DMXModes>{modes}</DMXModes>
          </FixtureType>
        </GDTF>
        """);

    // ------------------------------------------------------------------ Beam: specified or GDTF default

    [Fact]
    public void Beam_records_which_attributes_the_file_gives()
    {
        var ft = Read("""
            <Geometry Name="Body">
              <Beam Name="Written" LampType="LED" PowerConsumption="1000" LuminousFlux="10000" ColorTemperature="6000" BeamAngle="25"
                    FieldAngle="25" ThrowRatio="1" RectangleRatio="1.7777" BeamRadius="0.05" BeamType="Wash" ColorRenderingIndex="100"/>
              <Beam Name="Bare"/>
              <Beam Name="Partial" ColorTemperature="3200" BeamAngle="" LuminousFlux="lots" BeamType="Laser-ish"/>
            </Geometry>
            """, "");
        var beams = ft.Geometries[0].Children.Cast<BeamGeometry>().ToDictionary(b => b.Name);

        var written = beams["Written"];
        Assert.Equal(BeamAttributes.All, written.Specified);
        Assert.True(written.IsColorTemperatureSpecified && written.IsLuminousFluxSpecified && written.IsBeamAngleSpecified &&
                    written.IsFieldAngleSpecified && written.IsBeamTypeSpecified && written.IsPowerConsumptionSpecified &&
                    written.IsColorRenderingIndexSpecified && written.IsLampTypeSpecified);
        Assert.Equal(6000f, written.ColorTemperature);

        var bare = beams["Bare"];
        Assert.Equal(BeamAttributes.None, bare.Specified);
        Assert.False(bare.IsColorTemperatureSpecified || bare.IsLuminousFluxSpecified || bare.IsBeamAngleSpecified ||
                     bare.IsFieldAngleSpecified || bare.IsBeamTypeSpecified || bare.IsPowerConsumptionSpecified ||
                     bare.IsColorRenderingIndexSpecified || bare.IsLampTypeSpecified);
        // The values are still usable GDTF defaults.
        Assert.Equal((6000f, 10000f, 25f, 25f, BeamType.Wash, 1000f, 100f, LampType.Discharge),
            (bare.ColorTemperature, bare.LuminousFlux, bare.BeamAngle, bare.FieldAngle, bare.BeamType, bare.PowerConsumption,
             bare.ColorRenderingIndex, bare.LampType));

        // Empty or unparsable values are not "specified" (the second ones with a warning).
        var partial = beams["Partial"];
        Assert.Equal(BeamAttributes.ColorTemperature, partial.Specified);
        Assert.Equal(3200f, partial.ColorTemperature);
        Assert.Equal(10000f, partial.LuminousFlux);
        Assert.Contains(ft.Warnings, w => w.Message.Contains("LuminousFlux=\"lots\"", StringComparison.Ordinal));
        Assert.Contains(ft.Warnings, w => w.Message.Contains("Laser-ish", StringComparison.Ordinal));
        Assert.True(partial.IsSpecified(BeamAttributes.ColorTemperature));
        Assert.False(partial.IsSpecified(BeamAttributes.ColorTemperature | BeamAttributes.BeamAngle));
        Assert.False(partial.IsSpecified(BeamAttributes.None));
    }

    [Fact]
    public void Assigning_a_beam_attribute_in_code_marks_it_specified()
    {
        var beam = new BeamGeometry();
        Assert.False(beam.IsLuminousFluxSpecified);
        beam.LuminousFlux = 12000f;
        Assert.True(beam.IsLuminousFluxSpecified);
        Assert.Equal(BeamAttributes.LuminousFlux, beam.Specified);
        beam.Specified = BeamAttributes.None; // e.g. an editor resetting a field to "unknown"
        Assert.False(beam.IsLuminousFluxSpecified);
        Assert.Equal(12000f, beam.LuminousFlux);
    }

    [Fact]
    public void Real_files_written_by_gdtf_builder_specify_every_beam_attribute()
    {
        var ft = GdtfReader.ReadFile(TestData.Fixture("Robe_Lighting@Robin_MegaPointe@"));
        var beams = ft.Geometries.SelectMany(g => g.DescendantsAndSelf()).OfType<BeamGeometry>().ToList();
        Assert.NotEmpty(beams);
        Assert.All(beams, b => Assert.Equal(BeamAttributes.All, b.Specified));
    }

    // ------------------------------------------------------------------ DMX values as range ends

    [Theory]
    [InlineData("127/1", 2, 0x807Fu)]  // mirrored 128/1 = 0x8080 starts the next range
    [InlineData("127/1s", 2, 0x7FFFu)] // shifted 128/1s = 0x8000
    [InlineData("0/1", 2, 0x0100u)]    // mirrored 1/1 = 0x0101
    [InlineData("255/1", 2, 0xFFFFu)]
    [InlineData("255/1s", 2, 0xFFFFu)]
    [InlineData("127/1", 3, 0x80807Fu)]
    [InlineData("127/1", 1, 127u)]       // same resolution: the value itself
    [InlineData("33023/2", 1, 128u)]     // fewer bytes: the high byte
    [InlineData("65535/2", 4, 0xFFFFFFFFu)]
    public void Range_ends_cover_their_whole_step_in_a_finer_channel(string text, int bytes, uint expected) =>
        Assert.Equal(expected, DmxValue.Parse(text).ToRangeEnd(bytes));

    [Fact]
    public void Adjacent_written_ranges_stay_adjacent_at_every_resolution()
    {
        for (int bytes = 1; bytes <= DmxValue.MaxByteCount; bytes++)
            for (uint v = 0; v < 255; v++)
                foreach (var conversion in new[] { DmxConversion.ByteMirroring, DmxConversion.ByteShifting })
                {
                    var end = new DmxValue(v, 1, conversion);
                    var nextStart = new DmxValue(v + 1, 1, conversion);
                    Assert.Equal(nextStart.ToResolution(bytes), end.ToRangeEnd(bytes) + 1);
                    Assert.True(end.ToRangeEnd(bytes) >= end.ToResolution(bytes));
                }
    }

    // ------------------------------------------------------------------ mode-master ranges

    private const string Body = """
        <Geometry Name="Body"/>
        """;

    private static string Channel(string geometry, int offset, string attribute, string functions, string offsets = "") => $"""
        <DMXChannel Offset="{(offsets.Length > 0 ? offsets : offset.ToString(System.Globalization.CultureInfo.InvariantCulture))}" Geometry="{geometry}">
          <LogicalChannel Attribute="{attribute}">{functions}</LogicalChannel>
        </DMXChannel>
        """;

    [Fact]
    public void Mode_ranges_written_in_8_bit_tile_a_16_bit_master_without_gaps()
    {
        string master = Channel("Body", 1, "Control", """
            <ChannelFunction Name="Ctl" Attribute="Control" DMXFrom="0/2"/>
            """, offsets: "1,2");
        string follower = Channel("Body", 3, "Dimmer", """
            <ChannelFunction Name="Low" Attribute="Dimmer" DMXFrom="0/1" ModeMaster="Body_Control" ModeFrom="0/1" ModeTo="127/1"/>
            <ChannelFunction Name="High" Attribute="Dimmer" DMXFrom="0/1" ModeMaster="Body_Control" ModeFrom="128/1" ModeTo="255/1"/>
            <ChannelFunction Name="Shifted" Attribute="Dimmer" DMXFrom="0/1" ModeMaster="Body_Control" ModeFrom="16/1s" ModeTo="31/1s"/>
            <ChannelFunction Name="Reversed" Attribute="Dimmer" DMXFrom="0/1" ModeMaster="Body_Control" ModeFrom="200/1" ModeTo="100/1"/>
            """);
        var ft = Read(Body, $"<DMXMode Name=\"M\" Geometry=\"Body\"><DMXChannels>{master}{follower}</DMXChannels></DMXMode>");
        var fs = ft.DmxModes[0].Channels[1].Functions.ToDictionary(f => f.Name);

        Assert.Equal(2, fs["Low"].ModeMasterChannel!.Resolution);
        Assert.Equal((0u, 0x807Fu), (fs["Low"].ModeFromValue, fs["Low"].ModeToValue));
        Assert.Equal((0x8080u, 0xFFFFu), (fs["High"].ModeFromValue, fs["High"].ModeToValue));
        Assert.Equal((0x1000u, 0x1FFFu), (fs["Shifted"].ModeFromValue, fs["Shifted"].ModeToValue));
        // Every master value selects exactly one of Low / High: no one-value gaps at the range ends.
        for (uint v = 0; v <= 0xFFFF; v += 0x7F)
            Assert.True(fs["Low"].IsModeActive(v) ^ fs["High"].IsModeActive(v), $"master {v:X4}");
        Assert.True(fs["Low"].IsModeActive(0x807F) && fs["High"].IsModeActive(0x8080));

        // A reversed range is swapped with a warning and still covers both written ends.
        Assert.Equal((0x6464u, 0xC9C8u), (fs["Reversed"].ModeFromValue, fs["Reversed"].ModeToValue));
        Assert.Contains(ft.Warnings, w => w.Message.Contains("'Reversed'", StringComparison.Ordinal) && w.Message.Contains("swapped", StringComparison.Ordinal));
    }

    [Fact]
    public void Functions_without_a_resolved_master_are_always_active()
    {
        var f = new ChannelFunction();
        Assert.True(f.IsModeActive(0) && f.IsModeActive(uint.MaxValue));
    }

    /// <summary>
    /// DMXTo is the next function's DMXFrom − 1 among the functions that can be active at the same time. Functions that
    /// depend on the same master channel — spelled as the channel or as one of its functions — with disjoint mode
    /// ranges, or on two different functions of that channel, never end each other's range.
    /// </summary>
    [Fact]
    public void Dmx_ranges_ignore_functions_that_cannot_be_active_together()
    {
        string master = Channel("Body", 1, "Effects", """
            <ChannelFunction Name="Off" Attribute="Effects" DMXFrom="0/1"/>
            <ChannelFunction Name="Static" Attribute="Effects" DMXFrom="16/1"/>
            <ChannelFunction Name="Dynamic" Attribute="Effects" DMXFrom="32/1"/>
            """);
        string other = Channel("Body", 2, "Control", """
            <ChannelFunction Name="Ctl" Attribute="Control" DMXFrom="0/1"/>
            """);
        string follower = Channel("Body", 3, "EffectsRate", """
            <ChannelFunction Name="Idle" Attribute="NoFeature" DMXFrom="0/1" ModeMaster="Body_Effects" ModeFrom="0/1" ModeTo="15/1"/>
            <ChannelFunction Name="Rate" Attribute="EffectsRate" DMXFrom="24/1" ModeMaster="Body_Effects.Effects.Dynamic" ModeFrom="32/1" ModeTo="255/1"/>
            <ChannelFunction Name="Select" Attribute="EffectsRate" DMXFrom="0/1" ModeMaster="Body_Effects.Effects.Static" ModeFrom="16/1" ModeTo="31/1">
              <ChannelSet Name="A" DMXFrom="0/1"/><ChannelSet Name="B" DMXFrom="128/1"/>
            </ChannelFunction>
            <ChannelFunction Name="Fine" Attribute="EffectsRate" DMXFrom="200/1" ModeMaster="Body_Control" ModeFrom="0/1" ModeTo="255/1"/>
            """);
        var ft = Read(Body, $"<DMXMode Name=\"M\" Geometry=\"Body\"><DMXChannels>{master}{other}{follower}</DMXChannels></DMXMode>");
        var fs = ft.DmxModes[0].Channels[2].Functions.ToDictionary(f => f.Name);

        // "Fine" depends on another channel, so it can be active together with every other function and ends them all
        // at 199; among themselves the Effects-dependent functions are exclusive.
        Assert.Equal((0u, 199u), (fs["Idle"].DmxFromValue, fs["Idle"].DmxToValue));
        Assert.Equal((24u, 199u), (fs["Rate"].DmxFromValue, fs["Rate"].DmxToValue));
        Assert.Equal((0u, 199u), (fs["Select"].DmxFromValue, fs["Select"].DmxToValue));
        Assert.Equal((200u, 255u), (fs["Fine"].DmxFromValue, fs["Fine"].DmxToValue));
        var sets = fs["Select"].ChannelSets;
        Assert.Equal((0u, 127u), (sets[0].DmxFromValue, sets[0].DmxToValue));
        Assert.Equal((128u, 199u), (sets[1].DmxFromValue, sets[1].DmxToValue));

        Assert.False(Resolver.CanBeActiveTogether(fs["Idle"], fs["Select"])); // channel master vs function master, disjoint ranges
        Assert.False(Resolver.CanBeActiveTogether(fs["Rate"], fs["Select"])); // two functions of the same master channel
        Assert.True(Resolver.CanBeActiveTogether(fs["Idle"], fs["Fine"]));   // different master channels
    }

    /// <summary>
    /// BeamZ MHL 1940: the effect rate channel depends on the effect channel, by channel name for "no function" and by
    /// function node for the static and dynamic patterns. Spelled differently, the functions used to be treated as
    /// independent, which cut the static pattern selection (and its last pattern) at the dynamic rate's start (23).
    /// </summary>
    [Fact]
    public void Real_mode_master_functions_spelled_as_channel_and_function_nodes_keep_their_full_range()
    {
        var ft = GdtfReader.ReadFile(TestData.Fixture("BeamZ_Pro@MHL_1940_RGBW@"));
        foreach (var modeName in new[] { "38 CH", "114 CH", "114 CH + Virtual Dimmer" })
        {
            var ch = ft.FindMode(modeName)!.FindChannel("Foreground_Effects1Rate")!;
            var fs = ch.Functions.ToList();
            var idle = fs.Single(f => f.Name == "NoFeature");
            var select = fs.Single(f => f.Name == "Effects1 Static Patterns Select");
            var rate = fs.Single(f => f.Name == "Effects1 Dynamic Patterns Rate");
            Assert.Equal((0u, 255u), (idle.DmxFromValue, idle.DmxToValue));
            Assert.Equal((0u, 255u), (select.DmxFromValue, select.DmxToValue));
            Assert.Equal((24u, 255u), (rate.DmxFromValue, rate.DmxToValue));
            var last = select.ChannelSets.Single(s => s.Name == "Static MultiColor Pattern 11");
            Assert.Equal((252u, 255u), (last.DmxFromValue, last.DmxToValue));
            Assert.Same(ft.FindMode(modeName)!.FindChannel("Foreground_Effects1"), select.ModeMasterChannel);
            Assert.Equal("Effects1 Static Patterns", select.ModeMasterFunction?.Name);
        }
    }

    [Fact]
    public void Function_and_set_ranges_tile_every_channel_without_mode_masters_in_the_library()
    {
        // Spec rule checked on real files: without mode masters, functions (sorted by DMXFrom) tile the channel from
        // their first start to the channel maximum, and the sets of a function tile it from their first start.
        int checkedChannels = 0;
        foreach (var file in TestData.GdtfFiles.Where((_, i) => i % 25 == 0))
        {
            var ft = GdtfReader.ReadFile(file);
            foreach (var ch in ft.DmxModes.SelectMany(m => m.Channels))
            {
                var fs = ch.Functions.ToList();
                if (fs.Count == 0 || fs.Any(f => f.ModeMasterName is not null)) continue;
                checkedChannels++;
                var ordered = fs.OrderBy(f => f.DmxFromValue).ThenBy(f => f.Index).ToList();
                Assert.Equal(ch.MaxValue, ordered.Max(f => f.DmxToValue));
                for (int i = 0; i + 1 < ordered.Count; i++)
                    if (ordered[i + 1].DmxFromValue > ordered[i].DmxFromValue)
                        Assert.Equal(ordered[i + 1].DmxFromValue - 1, ordered[i].DmxToValue);
                foreach (var f in fs)
                {
                    var sets = f.ChannelSets.OrderBy(s => s.DmxFromValue).ToList();
                    for (int i = 0; i + 1 < sets.Count; i++)
                        if (sets[i + 1].DmxFromValue > sets[i].DmxFromValue)
                            Assert.Equal(sets[i + 1].DmxFromValue - 1, sets[i].DmxToValue);
                    if (sets.Count > 0 && sets[^1].DmxFromValue <= f.DmxToValue) Assert.Equal(f.DmxToValue, sets[^1].DmxToValue);
                }
            }
        }
        Assert.True(checkedChannels > 500, $"{checkedChannels} channels checked");
    }
}
