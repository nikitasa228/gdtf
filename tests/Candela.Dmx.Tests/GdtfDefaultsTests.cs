using System.Text;
using Candela.Gdtf;

namespace Candela.Dmx.Tests;

/// <summary>
/// The profile builder tells Beam attributes the file gives from GDTF defaults it does not (the reader's
/// <see cref="BeamGeometry.Specified"/> flags), and compiles mode-master ranges with the reader's range semantics.
/// </summary>
public class GdtfDefaultsTests
{
    private static FixtureType Read(string beam, string channels = DimmerChannel)
    {
        string xml = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <GDTF DataVersion="1.2">
              <FixtureType Name="Lamp" Manufacturer="Test">
                <AttributeDefinitions><Attributes><Attribute Name="Dimmer" PhysicalUnit="LuminousIntensity"/></Attributes></AttributeDefinitions>
                <Geometries><Geometry Name="Body">{beam}</Geometry></Geometries>
                <DMXModes><DMXMode Name="M" Geometry="Body"><DMXChannels>{channels}</DMXChannels></DMXMode></DMXModes>
              </FixtureType>
            </GDTF>
            """;
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        return GdtfReader.ReadDescription(ms, "lamp.gdtf");
    }

    private const string DimmerChannel = """
        <DMXChannel Offset="1" Geometry="Beam"><LogicalChannel Attribute="Dimmer"><ChannelFunction Name="Dim" Attribute="Dimmer"/></LogicalChannel></DMXChannel>
        """;

    private static readonly FixtureSpecs WarmSpot = new()
    {
        File = "lamp.gdtf",
        ColorTemperatureK = new SpecRange(3000, 3000),
        BeamAngleDeg = new SpecRange(12, 12),
        FieldAngleDeg = 18,
    };

    private static ProfileBeam Beam(string beam, FixtureSpecs? specs = null) =>
        Assert.Single(FixtureProfile.Compile(Read(beam), null, specs).Beams);

    [Fact]
    public void Colour_temperature_and_angles_the_file_omits_come_from_the_specs()
    {
        var beam = Beam("<Beam Name=\"Beam\" LuminousFlux=\"5000\"/>", WarmSpot);
        Assert.Equal(3000f, beam.SourceKelvin, 1f);
        Assert.Equal(12f * MathF.PI / 180f, beam.NativeBeamAngle, 1e-4f);
        Assert.Equal(18f / 12f, beam.FieldRatio, 1e-3f);
    }

    [Fact]
    public void Colour_temperature_and_angles_the_file_gives_win_over_the_specs()
    {
        var beam = Beam("<Beam Name=\"Beam\" LuminousFlux=\"5000\" ColorTemperature=\"3200\" BeamAngle=\"20\" FieldAngle=\"30\"/>", WarmSpot);
        Assert.Equal(3200f, beam.SourceKelvin, 1f);
        Assert.Equal(20f * MathF.PI / 180f, beam.NativeBeamAngle, 1e-4f);
        Assert.Equal(1.5f, beam.FieldRatio, 1e-3f);
    }

    [Fact]
    public void Written_gdtf_defaults_still_count_as_untouched_editor_defaults()
    {
        // GDTF Builder writes every attribute: 6000 K and 25°/25° written are the editor's defaults, not data.
        var beam = Beam("<Beam Name=\"Beam\" LuminousFlux=\"5000\" ColorTemperature=\"6000\" BeamAngle=\"25\" FieldAngle=\"25\"/>", WarmSpot);
        Assert.Equal(3000f, beam.SourceKelvin, 1f);
        Assert.Equal(12f * MathF.PI / 180f, beam.NativeBeamAngle, 1e-4f);
    }

    [Fact]
    public void Flux_falls_back_from_the_file_to_the_specs_to_the_beam_power_to_the_gdtf_default()
    {
        var fromFile = Beam("<Beam Name=\"Beam\" LuminousFlux=\"8000\"/>", new FixtureSpecs { File = "lamp.gdtf", LuminousFluxLm = 5000 });
        Assert.Equal((8000f, PhotometrySource.GdtfFlux), (fromFile.ReferenceFlux, fromFile.PhotometrySource));

        // A written 10 000 lm is GDTF's default: the researched flux wins.
        var defaultFlux = Beam("<Beam Name=\"Beam\" LuminousFlux=\"10000\"/>", new FixtureSpecs { File = "lamp.gdtf", LuminousFluxLm = 5000 });
        Assert.Equal((5000f, PhotometrySource.SpecsFlux), (defaultFlux.ReferenceFlux, defaultFlux.PhotometrySource));

        // No flux anywhere, but the file gives the electrical power: power × LED efficacy.
        var fromPower = Beam("<Beam Name=\"Beam\" LampType=\"LED\" PowerConsumption=\"200\"/>");
        Assert.Equal(PhotometrySource.Estimated, fromPower.PhotometrySource);
        Assert.Equal(200f * 40f, fromPower.ReferenceFlux, 1f);

        // A written 1000 W is GDTF's default power, and nothing else is known: GDTF's default flux.
        var nothing = Beam("<Beam Name=\"Beam\" PowerConsumption=\"1000\"/>");
        Assert.Equal((10000f, PhotometrySource.Estimated), (nothing.ReferenceFlux, nothing.PhotometrySource));

        // An omitted flux is not GDTF's 10 000 lm "from the file".
        var omitted = Beam("<Beam Name=\"Beam\"/>");
        Assert.NotEqual(PhotometrySource.GdtfFlux, omitted.PhotometrySource);
    }

    [Fact]
    public void Mode_ranges_written_with_fewer_bytes_than_the_master_leave_no_gap()
    {
        const string channels = """
            <DMXChannel Offset="1,2" Geometry="Body">
              <LogicalChannel Attribute="Control"><ChannelFunction Name="Ctl" Attribute="Control" DMXFrom="0/2"/></LogicalChannel>
            </DMXChannel>
            <DMXChannel Offset="3" Geometry="Beam">
              <LogicalChannel Attribute="Dimmer">
                <ChannelFunction Name="Low" Attribute="Dimmer" DMXFrom="0/1" ModeMaster="Body_Control" ModeFrom="0/1" ModeTo="127/1"/>
                <ChannelFunction Name="High" Attribute="Dimmer" DMXFrom="0/1" ModeMaster="Body_Control" ModeFrom="128/1" ModeTo="255/1"/>
              </LogicalChannel>
            </DMXChannel>
            """;
        var p = FixtureProfile.Compile(Read("<Beam Name=\"Beam\" LuminousFlux=\"5000\"/>", channels));
        var low = p.Functions.Single(f => f.Name == "Low");
        var high = p.Functions.Single(f => f.Name == "High");
        Assert.Equal(low.ModeMaster, high.ModeMaster);
        Assert.Equal(2, p.Channels[low.ModeMaster].ByteCount);
        Assert.Equal((0u, 0x807Fu), (low.ModeFrom, low.ModeTo));
        Assert.Equal((0x8080u, 0xFFFFu), (high.ModeFrom, high.ModeTo));
    }
}
