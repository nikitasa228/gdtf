using System.Diagnostics;
using System.Text;

namespace Candela.Gdtf.Tests;

/// <summary>
/// Reference resolution must stay linear in the size of the description. Each case below is a few megabytes of XML
/// (far below <see cref="GdtfReader.MaxDescriptionBytes"/>) that used to make the resolver quadratic: a scan of every
/// channel per dangling mode master, relation or macro value, of every feature per attribute, of every same-named
/// geometry per channel, of every mode per RDM personality, of every later function per function, and seven name
/// indexes rebuilt for every mode. Before the fix these took 10 s to several minutes each; now they take well under a
/// second (Release).
/// </summary>
public sealed class ResolverScalingTests
{
    /// <summary>Generous bound (Debug build, heavily loaded CI machine) that the quadratic versions far exceeded.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(15);

    private static string Fixture(string modes, string geometries = "<Geometry Name=\"Body\"/>", string definitions = "", string protocols = "") => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <GDTF DataVersion="1.2"><FixtureType Name="Scaling" Manufacturer="Test">{definitions}<Geometries>{geometries}</Geometries><DMXModes>{modes}</DMXModes>{protocols}</FixtureType></GDTF>
        """;

    private static string Channels(int n, Func<int, string> function)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < n; i++)
            sb.Append("<DMXChannel Offset=\"1\" Geometry=\"Body\"><LogicalChannel Attribute=\"A").Append(i).Append("\">")
                .Append(function(i)).Append("</LogicalChannel></DMXChannel>");
        return sb.ToString();
    }

    private static string Repeat(int n, Func<int, string> item)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < n; i++) sb.Append(item(i));
        return sb.ToString();
    }

    /// <summary>The descriptions, by name (built lazily: only the case under test is generated).</summary>
    private static string Build(string name) => name switch
    {
        "DanglingModeMasters" => Fixture($"""
            <DMXMode Name="M" Geometry="Body"><DMXChannels>{Channels(20_000, i => $"<ChannelFunction ModeMaster=\"x{i}\"/>")}</DMXChannels></DMXMode>
            """),
        "DanglingRelations" => Fixture($"""
            <DMXMode Name="M" Geometry="Body"><DMXChannels>{Channels(20_000, _ => "<ChannelFunction/>")}</DMXChannels>
            <Relations>{Repeat(20_000, i => $"<Relation Name=\"r{i}\" Master=\"q{i}\" Follower=\"z{i}\"/>")}</Relations></DMXMode>
            """),
        "DanglingMacroValues" => Fixture($"""
            <DMXMode Name="M" Geometry="Body"><DMXChannels>{Channels(20_000, _ => "<ChannelFunction/>")}</DMXChannels>
            <FTMacros><FTMacro Name="m"><MacroDMX><MacroDMXStep>{Repeat(20_000, i => $"<MacroDMXValue Value=\"1/1\" DMXChannel=\"nope{i}\"/>")}</MacroDMXStep></MacroDMX></FTMacro></FTMacros></DMXMode>
            """),
        "ModesTimesDefinitions" => Fixture(
            Repeat(20_000, i => $"<DMXMode Name=\"M{i}\" Geometry=\"Body\"/>"),
            definitions: $"<AttributeDefinitions><Attributes>{Repeat(20_000, i => $"<Attribute Name=\"A{i}\"/>")}</Attributes></AttributeDefinitions>" +
                         $"<Wheels>{Repeat(20_000, i => $"<Wheel Name=\"W{i}\"/>")}</Wheels>"),
        "DanglingFeatures" => Fixture("", definitions:
            $"<AttributeDefinitions><FeatureGroups><FeatureGroup Name=\"G\">{Repeat(60_000, i => $"<Feature Name=\"F{i}\"/>")}</FeatureGroup></FeatureGroups>" +
            $"<Attributes>{Repeat(60_000, i => $"<Attribute Name=\"A{i}\" Feature=\"X.Nope{i}\"/>")}</Attributes></AttributeDefinitions>"),
        "RepeatedGeometryNames" => Fixture(
            $"""<DMXMode Name="M" Geometry="Other"><DMXChannels>{Channels(60_000, _ => "<ChannelFunction/>").Replace("Geometry=\"Body\"", "Geometry=\"Dup\"", StringComparison.Ordinal)}</DMXChannels></DMXMode>""",
            geometries: $"<Geometry Name=\"Body\">{Repeat(60_000, _ => "<Geometry Name=\"Dup\"/>")}</Geometry><Geometry Name=\"Other\"/>"),
        "DanglingPersonalities" => Fixture(
            Repeat(50_000, i => $"<DMXMode Name=\"M{i}\" Geometry=\"Body\"/>"),
            protocols: $"<Protocols><FTRDM ManufacturerID=\"0x1\" DeviceModelID=\"0x1\"><SoftwareVersionID Value=\"0x1\">" +
                       $"{Repeat(50_000, i => $"<DMXPersonality Value=\"0x1\" DMXMode=\"X{i}\"/>")}</SoftwareVersionID></FTRDM></Protocols>"),
        "FunctionsWithEqualStarts" => Fixture($"""
            <DMXMode Name="M" Geometry="Body"><DMXChannels><DMXChannel Offset="1" Geometry="Body"><LogicalChannel Attribute="Dimmer">
            {Repeat(100_000, i => $"<ChannelFunction Name=\"f{i}\"/>")}
            </LogicalChannel></DMXChannel></DMXChannels></DMXMode>
            """),
        "SetsWithEqualStarts" => Fixture($"""
            <DMXMode Name="M" Geometry="Body"><DMXChannels><DMXChannel Offset="1" Geometry="Body"><LogicalChannel Attribute="Dimmer">
            <ChannelFunction Name="f">{Repeat(100_000, i => $"<ChannelSet Name=\"s{i}\"/>")}</ChannelFunction>
            </LogicalChannel></DMXChannel></DMXChannels></DMXMode>
            """),
        "MutuallyExclusiveFunctions" => Fixture($"""
            <DMXMode Name="M" Geometry="Body"><DMXChannels>
            <DMXChannel Offset="1,2" Geometry="Body"><LogicalChannel Attribute="Mode"><ChannelFunction Name="m"/></LogicalChannel></DMXChannel>
            <DMXChannel Offset="3,4,5,6" Geometry="Body"><LogicalChannel Attribute="Dimmer">
            {Repeat(50_000, i => $"<ChannelFunction Name=\"f{i}\" DMXFrom=\"{i}/4\" ModeMaster=\"Body_Mode\" ModeFrom=\"{i}/2\" ModeTo=\"{i}/2\"/>")}
            </LogicalChannel></DMXChannel></DMXChannels></DMXMode>
            """),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [Theory]
    [InlineData("DanglingModeMasters")]
    [InlineData("DanglingRelations")]
    [InlineData("DanglingMacroValues")]
    [InlineData("ModesTimesDefinitions")]
    [InlineData("DanglingFeatures")]
    [InlineData("RepeatedGeometryNames")]
    [InlineData("DanglingPersonalities")]
    [InlineData("FunctionsWithEqualStarts")]
    [InlineData("SetsWithEqualStarts")]
    [InlineData("MutuallyExclusiveFunctions")]
    public void Resolving_large_descriptions_stays_linear(string name)
    {
        string xml = Build(name);
        Assert.True(xml.Length < GdtfReader.MaxDescriptionBytes);
        var sw = Stopwatch.StartNew();
        var ft = TestData.ReadXml(xml);
        sw.Stop();
        Assert.True(sw.Elapsed < Bound, $"{name}: reading {xml.Length / 1e6:F1} MB took {sw.Elapsed}.");
        Assert.NotEmpty(ft.Name);
    }

    [Fact]
    public void Ranges_of_a_channel_with_too_many_exclusive_functions_fall_back_to_the_next_start_with_a_warning()
    {
        var ft = TestData.ReadXml(Build("MutuallyExclusiveFunctions"));
        var functions = ft.DmxModes[0].Channels[1].Functions.ToList();
        Assert.Equal(50_000, functions.Count);
        // Early functions were worked out exactly: each is the only one active for its master value.
        Assert.Equal(uint.MaxValue, functions[0].DmxToValue);
        // Late ones end at the next start, and the reader says it gave up.
        Assert.Equal(functions[^1].DmxFromValue - 1, functions[^2].DmxToValue);
        Assert.Contains(ft.Warnings, w => w.Message.Contains("too many mutually exclusive functions", StringComparison.Ordinal));
    }

    /// <summary>The indexed lookups accept the same spellings the scans did.</summary>
    [Fact]
    public void Tolerant_node_lookups_still_resolve_other_spellings()
    {
        var ft = TestData.ReadXml(Fixture("""
            <DMXMode Name="Mode A" Geometry="Body"><DMXChannels>
              <DMXChannel Offset="1" Geometry="Body" InitialFunction="Wrong_Prefix.Mode.Programs">
                <LogicalChannel Attribute="Mode"><ChannelFunction Name="Direct"/><ChannelFunction Name="Programs" DMXFrom="128/1"/></LogicalChannel>
              </DMXChannel>
              <DMXChannel Offset="2" Geometry="body" InitialFunction=" body_dimmer.dimmer.dim ">
                <LogicalChannel Attribute="Dimmer">
                  <ChannelFunction Name="Dim" ModeMaster=" body_mode " ModeFrom="0/1" ModeTo="127/1"/>
                  <ChannelFunction Name="Fx" ModeMaster="BODY_MODE.Mode.programs" ModeFrom="128/1" ModeTo="255/1"/>
                  <ChannelFunction Name="Other" ModeMaster="Body_Mode.Something.Direct" ModeFrom="0/1" ModeTo="255/1"/>
                </LogicalChannel>
              </DMXChannel>
            </DMXChannels>
            <Relations><Relation Name="R" Master="BODY_MODE" Follower="Body_Dimmer.Dimmer.fx"/></Relations>
            <FTMacros><FTMacro Name="X" ChannelFunction="Body_Dimmer.Typo.Dim"><MacroDMX><MacroDMXStep><MacroDMXValue Value="1/1" DMXChannel=" Body_Dimmer"/></MacroDMXStep></MacroDMX></FTMacro></FTMacros>
            </DMXMode>
            """, geometries: """<Geometry Name="Body"/><Geometry Name="body"/>""",
            definitions: """<AttributeDefinitions><Attributes><Attribute Name="Mode"/><Attribute Name="Dimmer"/></Attributes></AttributeDefinitions>""",
            protocols: """<Protocols><FTRDM ManufacturerID="0x1" DeviceModelID="0x1"><SoftwareVersionID Value="0x1"><DMXPersonality Value="0x1" DMXMode=" mode a "/></SoftwareVersionID></FTRDM></Protocols>"""));

        var mode = ft.DmxModes[0];
        var modeChannel = mode.Channels[0];
        var dimmer = mode.Channels[1];
        var fns = dimmer.Functions.ToList();
        Assert.Equal("Programs", modeChannel.InitialFunction?.Name); // channel prefix spelled differently
        Assert.Equal("Dim", dimmer.InitialFunction?.Name);           // case and white space
        Assert.Same(modeChannel, fns[0].ModeMasterChannel);            // channel, case and white space
        Assert.Null(fns[0].ModeMasterFunction);
        Assert.Same(modeChannel, fns[1].ModeMasterChannel);            // function node, case differs
        Assert.Equal("Programs", fns[1].ModeMasterFunction?.Name);
        Assert.Equal("Direct", fns[2].ModeMasterFunction?.Name);      // logical channel misspelled: the function name decides
        Assert.Same(modeChannel, mode.Relations[0].Master);
        Assert.Same(fns[1], mode.Relations[0].Follower);
        Assert.Same(fns[0], mode.Macros[0].ChannelFunction);
        Assert.Same(dimmer, mode.Macros[0].Steps[0].Values[0].DmxChannel);
        Assert.Same(mode, ft.Protocols.Rdm[0].SoftwareVersions[0].Personalities[0].DmxMode);
        Assert.Equal("body", dimmer.Geometry?.Name);
    }
}
