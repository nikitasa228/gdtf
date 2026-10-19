using System.Globalization;
using System.Text;
using Candela.Core.Diagnostics;
using Candela.Core.SceneGraph.Components;
using Candela.Gdtf;

namespace Candela.Mvr.Tests;

/// <summary>
/// The DMX mode the importer gives a substitute GDTF (a similar model standing in for one the library lacks): the
/// stand-in's mode that fits the patch spacing of the fixtures, so callers of <see cref="MvrImporter"/> alone get a
/// patch without stand-in overlaps — the rule of <see cref="FixtureType.FitModeToSpacing(DmxMode?, int?)"/>, which the DMX
/// engine uses as well.
/// </summary>
public sealed class SubstituteModeTests
{
    /// <summary>Not in the library: resolved to the same-brand stand-in Ayrton MagicPanel 602 (Extended 160 / Standard 20 channels).</summary>
    private const string MissingSpec = "Ayrton@MagicBlade R.gdtf";

    private const string StandIn = "Ayrton@MagicPanel_602@V2.5_Add_Mode.gdtf";

    private static MvrImportResult Import(string mode, params int[] addresses)
    {
        var fixtures = new StringBuilder();
        for (int i = 0; i < addresses.Length; i++)
        {
            fixtures.Append(CultureInfo.InvariantCulture, $"""
                <Fixture uuid="{new Guid(i + 1, 0, 0, new byte[8]):D}" name="Blade {i + 1}">
                  <Matrix>{"{"}1,0,0{"}"}{"{"}0,1,0{"}"}{"{"}0,0,1{"}"}{"{"}{i * 500},0,3000{"}"}</Matrix>
                  <GDTFSpec>{MissingSpec}</GDTFSpec>
                  <GDTFMode>{mode}</GDTFMode>
                  <Addresses><Address break="0">{addresses[i]}</Address></Addresses>
                  <FixtureID>{i + 1}</FixtureID>
                </Fixture>
                """);
        }
        string xml = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <GeneralSceneDescription verMajor="1" verMinor="6" provider="Test">
              <Scene><Layers><Layer uuid="{Guid.Parse("44444444-0000-4000-8000-0000000000AA"):D}" name="Rig"><ChildList>
                {fixtures}
              </ChildList></Layer></Layers></Scene>
            </GeneralSceneDescription>
            """;
        var bytes = TestData.Zip(("GeneralSceneDescription.xml", TestData.Utf8(xml)));
        using var ms = new MemoryStream(bytes);
        return MvrImporter.Import(ms, "blades.mvr", new MvrImportOptions { Library = TestData.Library });
    }

    private static IEnumerable<string> ModesOf(MvrImportResult result) =>
        result.Scene.Query<FixtureComponent>().Select(f => f.Component.GdtfMode ?? "").Distinct();

    [Fact]
    public void A_stand_in_mode_of_the_requested_name_that_overlaps_the_patch_is_replaced_by_the_widest_that_fits()
    {
        var result = Import("Extended", 1, 45, 89, 133);
        var gdtf = Assert.Single(result.Gdtfs);
        Assert.Equal((FixtureMatchKind.Substitute, StandIn), (gdtf.MatchKind, gdtf.FileName));
        var mode = Assert.Single(gdtf.Modes);
        Assert.Equal(("Extended", "Standard", 20, false), (mode.Requested, mode.Resolved, mode.Footprint, mode.Exact));
        Assert.Equal(new[] { "Standard" }, ModesOf(result));
        var note = Assert.Single(result.Issues, i => i.Code == MvrIssueCodes.SubstituteMode);
        Assert.Equal(IssueSeverity.Note, note.Severity);
        Assert.Equal(4, note.Entities.Count);
        Assert.DoesNotContain(result.Issues, i => i.Code is MvrIssueCodes.ModeFootprint or MvrIssueCodes.ModeUnknown);
    }

    [Fact]
    public void A_requested_mode_that_fits_is_kept()
    {
        // 160 channels apart: the stand-in's own 'Extended' fits
        var result = Import("Extended", 1, 161, 321);
        var mode = Assert.Single(Assert.Single(result.Gdtfs).Modes);
        Assert.Equal(("Extended", true), (mode.Resolved, mode.Exact));
        Assert.DoesNotContain(result.Issues, i => i.Code is MvrIssueCodes.SubstituteMode or MvrIssueCodes.ModeFootprint or MvrIssueCodes.ModeUnknown);
    }

    [Fact]
    public void An_unknown_mode_name_gets_the_widest_mode_that_fits_with_a_warning()
    {
        var result = Import("Mode 7", 1, 45, 89);
        var mode = Assert.Single(Assert.Single(result.Gdtfs).Modes);
        Assert.Equal(("Standard", false), (mode.Resolved, mode.Exact));
        var warning = Assert.Single(result.Issues, i => i.Code == MvrIssueCodes.ModeUnknown);
        Assert.Equal(IssueSeverity.Warning, warning.Severity);
        Assert.Contains("44-channel patch spacing", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Issues, i => i.Code == MvrIssueCodes.ModeFootprint);
    }

    [Fact]
    public void When_no_mode_fits_the_smallest_is_used_and_the_overlap_reported()
    {
        var type = TestData.Library.GetFixtureType(TestData.Library.FindByFileName(StandIn)!);
        int smallest = type.DmxModes.Min(m => m.FirstBreakFootprint);
        Assert.True(smallest > 2);
        var result = Import("Extended", 1, 3, 5);
        var mode = Assert.Single(Assert.Single(result.Gdtfs).Modes);
        Assert.Equal(smallest, mode.Footprint);
        Assert.Contains(result.Issues, i => i.Code == MvrIssueCodes.ModeFootprint && i.Severity == IssueSeverity.Warning);
    }

    [Fact]
    public void A_single_fixture_has_no_spacing_and_keeps_the_mode_by_name()
    {
        var result = Import("Extended", 1);
        var mode = Assert.Single(Assert.Single(result.Gdtfs).Modes);
        Assert.Equal(("Extended", true, (int?)null), (mode.Resolved, mode.Exact, mode.InferredFootprint));
        Assert.DoesNotContain(result.Issues, i => i.Code is MvrIssueCodes.SubstituteMode or MvrIssueCodes.ModeFootprint);
    }

    [Fact]
    public void Fixtures_in_different_universes_do_not_make_a_spacing()
    {
        Assert.Null(DmxPatchSpacing.Infer([1, 513, 1025]));
        Assert.Equal(44, DmxPatchSpacing.Infer([89, 1, 45, 45, 600]));
        Assert.Equal(3, DmxPatchSpacing.Infer([510, 513, 516]));
    }

    [Fact]
    public void The_importer_and_the_dmx_engine_choose_the_same_stand_in_mode_for_every_library_type()
    {
        // both resolve stand-ins through FixtureType.FitModeToSpacing; the importer matches the requested name loosely
        // (case, spaces, punctuation), the DMX engine by FindMode: for every real type, mode name and spacing the result
        // of the import is the mode the profile cache resolves
        int[] spacings = [1, 3, 4, 8, 12, 16, 20, 26, 32, 42, 44, 64, 100, 200, 512];
        int types = 0, disagreements = 0;
        var examples = new List<string>();
        foreach (var entry in TestData.Library.Entries)
        {
            FixtureType type;
            try
            {
                type = TestData.Library.GetFixtureType(entry);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                continue;
            }
            if (type.DmxModes.Count == 0) continue;
            types++;
            foreach (int s in spacings)
            {
                foreach (string? requested in type.DmxModes.Select(m => m.Name).Prepend(null).Prepend("No such mode"))
                {
                    var mvr = type.FitModeToSpacing(ModeMatcher.MatchName(type, requested), s);
                    var dmx = type.FitModeToSpacing(requested, s);
                    if (ReferenceEquals(mvr, dmx)) continue;
                    disagreements++;
                    if (examples.Count < 5) examples.Add($"{entry.FileName} '{requested}' at {s}: '{mvr?.Name}' vs '{dmx?.Name}'");
                }
            }
        }
        Assert.True(types > 1000, $"{types} types read");
        Assert.True(disagreements == 0, string.Join("\n", examples));
    }
}
