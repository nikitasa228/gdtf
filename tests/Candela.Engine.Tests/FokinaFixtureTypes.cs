using System.Globalization;
using System.IO.Compression;
using System.Text;
using Candela.Gdtf;

namespace Candela.Engine.Tests;

/// <summary>
/// The five fixture types <c>stages/Фокина.mvr</c> patches (Depence stub GDTFs), with the DMX mode each one asks for and
/// that mode's first-break footprint as the manufacturer documents it. Four of the models are not in the repository's
/// library: Ayrton publishes their GDTFs only on GDTF-Share (download needs a login or a CAPTCHA) and Martin publishes
/// none for the Atomic 3000 LED, so the library substitutes them until the files are added (docs/mvr.md).
/// </summary>
internal static class FokinaFixtureTypes
{
    /// <summary>One fixture type of the scene.</summary>
    /// <param name="Spec">The <c>GDTFSpec</c> written in the MVR.</param>
    /// <param name="Mode">The <c>GDTFMode</c> written in the MVR.</param>
    /// <param name="Objects">Number of fixtures of this type in the scene.</param>
    /// <param name="Footprint">Channels of that mode according to the manufacturer's DMX chart or user manual.</param>
    /// <param name="Manufacturer">Manufacturer as the manufacturer's GDTF writes it.</param>
    /// <param name="Name">Fixture type name as the manufacturer's GDTF writes it (GDTF-Share listing).</param>
    /// <param name="Modes">The manufacturer's DMX modes as (GDTF mode name, first-break channels).</param>
    public sealed record FixtureTypeInfo(string Spec, string Mode, int Objects, int Footprint, string Manufacturer, string Name,
        IReadOnlyList<(string Name, int Channels)> Modes)
    {
        /// <summary>Library file name in the repository's convention for a revision called <paramref name="revision"/>.</summary>
        public string FileName(string revision) => $"{Manufacturer.Replace(' ', '_')}@{Name.Replace(' ', '_')}@{revision}.gdtf";
    }

    /// <summary>
    /// Ayrton Rivale Profile: Standard 42 / Extended 65 channels (DMX chart V122); the GDTF names them
    /// <c>Standard_540</c> and <c>Extended_540</c> (GDTF-Share fixture 10439).
    /// </summary>
    public static readonly FixtureTypeInfo RivaleProfile = new("Ayrton @Rivale Profile.gdtf", "Standard", 27, 42,
        "Ayrton", "Rivale Profile", [("Extended_540", 65), ("Standard_540", 42)]);

    /// <summary>Ayrton MagicBlade-R: Basic 18 / Standard 20 / Extended 44 channels (DMX chart V1.0; GDTF-Share fixture 5377).</summary>
    public static readonly FixtureTypeInfo MagicBladeR = new("Ayrton@MagicBlade R.gdtf", "Extended", 28, 44,
        "Ayrton", "MagicBlade R", [("Extended", 44), ("Standard", 20), ("Basic", 18)]);

    /// <summary>
    /// Ayrton Zonda 3 Wash: Basic 26 / Standard 37 / Extended Ring 46 / Extended Pixel 79 channels (DMX chart V130;
    /// GDTF-Share fixture 9750, named "Zonda 3 WASH").
    /// </summary>
    public static readonly FixtureTypeInfo Zonda3Wash = new("Ayrton@Zonda 3 Wash.gdtf", "Basic", 28, 26,
        "Ayrton", "Zonda 3 WASH", [("Extended Ring", 46), ("Standard", 37), ("Extended Pixel", 79), ("Basic", 26)]);

    /// <summary>
    /// Martin Atomic 3000 LED: 3-channel, 4-channel and 14-channel Extended modes (user manual rev. C). Martin has not
    /// published a GDTF for it; the mode names follow Martin's other GDTFs (e.g. VDO Atomic Bold: "3 Channel").
    /// </summary>
    public static readonly FixtureTypeInfo Atomic3000Led = new("Martin@Atomic 3000 LED.gdtf", "Mode 1", 4, 3,
        "Martin Professional", "Atomic 3000 LED", [("3 Channel", 3), ("4 Channel", 4), ("Extended", 14)]);

    /// <summary>Ayrton Zonda 3 FX, Basic 26 channels: the one model of the scene that is in the library.</summary>
    public static readonly FixtureTypeInfo Zonda3Fx = new("Ayrton@Zonda 3 FX.gdtf", "Basic", 4, 26,
        "Ayrton", "Zonda 3 FX", [("Basic", 26)]);

    /// <summary>The library file of the Zonda 3 FX.</summary>
    public const string Zonda3FxLibraryFile = "Ayrton@Zonda_3_FX@V1.81_Corrected_Factory_Tilt.gdtf";

    /// <summary>The four types the repository's library lacks (as of this writing).</summary>
    public static readonly IReadOnlyList<FixtureTypeInfo> NotInRepository = [MagicBladeR, Atomic3000Led, Zonda3Wash, RivaleProfile];

    /// <summary>All five types of the scene, in the order the MVR first uses them.</summary>
    public static readonly IReadOnlyList<FixtureTypeInfo> All = [MagicBladeR, Atomic3000Led, Zonda3Wash, RivaleProfile, Zonda3Fx];

    /// <summary>The types for which <paramref name="library"/> has no exact model (they resolve to substitutes).</summary>
    public static IReadOnlyList<FixtureTypeInfo> MissingFrom(FixtureLibrary library) =>
        All.Where(t => library.FindBySpec(t.Spec) is not { IsExactModel: true }).ToList();

    /// <summary>
    /// Writes a minimal but complete GDTF for <paramref name="type"/> (a cube body and the manufacturer's DMX modes with
    /// one 8-bit channel per slot) into <paramref name="folder"/>. These are test stand-ins with the real names and
    /// footprints, not fixture data: they only prove that the real files resolve the scene once they are added.
    /// </summary>
    public static string WriteStandIn(string folder, FixtureTypeInfo type, Guid fixtureTypeId, string revision = "Test")
    {
        int maxChannels = type.Modes.Max(m => m.Channels);
        var xml = new StringBuilder();
        xml.Append(CultureInfo.InvariantCulture, $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <GDTF DataVersion="1.2">
              <FixtureType Name="{type.Name}" ShortName="{type.Name}" LongName="{type.Name}" Manufacturer="{type.Manufacturer}"
                           Description="Synthetic test stand-in" Thumbnail="" FixtureTypeID="{fixtureTypeId.ToString("D").ToUpperInvariant()}">
                <AttributeDefinitions>
                  <FeatureGroups><FeatureGroup Name="Control" Pretty="Control"><Feature Name="Control"/></FeatureGroup></FeatureGroups>
                  <Attributes>

            """);
        for (int i = 1; i <= maxChannels; i++)
            xml.Append(CultureInfo.InvariantCulture, $"""        <Attribute Name="Reserved{i}" Pretty="R{i}" Feature="Control.Control" PhysicalUnit="None"/>{"\n"}""");
        xml.Append("""
                  </Attributes>
                </AttributeDefinitions>
                <Models><Model Name="Body" Length="0.4" Width="0.3" Height="0.5" PrimitiveType="Cube"/></Models>
                <Geometries><Geometry Name="Body" Model="Body" Position="{1,0,0,0}{0,1,0,0}{0,0,1,0}{0,0,0,1}"/></Geometries>
                <DMXModes>

            """);
        foreach (var (name, channels) in type.Modes)
        {
            xml.Append(CultureInfo.InvariantCulture, $"""      <DMXMode Name="{name}" Geometry="Body"><DMXChannels>{"\n"}""");
            for (int i = 1; i <= channels; i++)
            {
                xml.Append(CultureInfo.InvariantCulture,
                    $"""        <DMXChannel DMXBreak="1" Offset="{i}" Geometry="Body"><LogicalChannel Attribute="Reserved{i}"><ChannelFunction Name="R{i}" Attribute="Reserved{i}" DMXFrom="0/1"/></LogicalChannel></DMXChannel>{"\n"}""");
            }
            xml.Append("      </DMXChannels></DMXMode>\n");
        }
        xml.Append("""
                </DMXModes>
              </FixtureType>
            </GDTF>
            """);

        string path = Path.Combine(folder, type.FileName(revision));
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using var writer = new StreamWriter(zip.CreateEntry("description.xml").Open(), new UTF8Encoding(false));
        writer.Write(xml.ToString());
        return path;
    }
}
