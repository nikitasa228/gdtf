using Candela.Core.Diagnostics;
using Candela.Core.SceneGraph.Components;
using Candela.Gdtf;
using Candela.Mvr;

namespace Candela.Engine.Tests;

/// <summary>
/// How <c>stages/Фокина.mvr</c> resolves against the fixture library: the patch spacing Depence wrote matches the
/// manufacturers' footprints, the repository's library substitutes exactly the models it lacks, and the real GDTFs
/// (here: stand-ins with the real names and footprints) replace the substitutes and clear the patch without code changes.
/// </summary>
public sealed class FokinaLibraryTests
{
    private static readonly Guid[] s_standInIds =
    [
        new("6E2D4A31-6C57-4D8B-9F0E-1A3B5C7D9E01"),
        new("6E2D4A31-6C57-4D8B-9F0E-1A3B5C7D9E02"),
        new("6E2D4A31-6C57-4D8B-9F0E-1A3B5C7D9E03"),
        new("6E2D4A31-6C57-4D8B-9F0E-1A3B5C7D9E04"),
    ];

    [Fact]
    public void The_patch_spacing_of_every_type_is_the_manufacturers_footprint()
    {
        var result = MvrImporter.ImportFile(TestEnvironment.Fokina, new MvrImportOptions { Library = TestEnvironment.Library, ValidateGeometry = false });

        Assert.Equal(FokinaFixtureTypes.All.Count, result.Gdtfs.Count);
        foreach (var type in FokinaFixtureTypes.All)
        {
            var reference = Assert.Single(result.Gdtfs, g => g.SpecName == type.Spec);
            Assert.Equal(type.Objects, reference.ObjectCount);
            var mode = Assert.Single(reference.Modes);
            Assert.Equal(type.Mode, mode.Requested);
            Assert.Equal(type.Footprint, mode.InferredFootprint);
        }
    }

    [Fact]
    public void The_repository_library_substitutes_exactly_the_models_it_lacks()
    {
        var missing = FokinaFixtureTypes.MissingFrom(TestEnvironment.Library);
        Assert.DoesNotContain(FokinaFixtureTypes.Zonda3Fx, missing);
        Assert.All(missing, t => Assert.Contains(t, FokinaFixtureTypes.NotInRepository));

        var result = MvrImporter.ImportFile(TestEnvironment.Fokina, new MvrImportOptions { Library = TestEnvironment.Library, ValidateGeometry = false });
        Assert.DoesNotContain(result.Issues, i => i.Severity == IssueSeverity.Error);
        Assert.Equal(missing.Count, result.Issues.Count(i => i.Code == MvrIssueCodes.GdtfSubstitute));
        foreach (var type in FokinaFixtureTypes.All)
        {
            var reference = result.Gdtfs.Single(g => g.SpecName == type.Spec);
            Assert.Equal(MvrGdtfSource.Library, reference.Source);
            Assert.Equal(!missing.Contains(type), reference.IsExactModel);
            if (!reference.IsExactModel) continue;

            // an exact model patches its documented mode without overlapping its neighbours
            var mode = Assert.Single(reference.Modes);
            Assert.Equal(type.Footprint, mode.Footprint);
        }
    }

    [Fact]
    public void The_real_gdtfs_replace_the_substitutes_and_clear_the_patch()
    {
        string folder = TestEnvironment.NewTempDirectory("fokina-library");
        File.Copy(TestEnvironment.Entry(FokinaFixtureTypes.Zonda3FxLibraryFile).FilePath,
            Path.Combine(folder, FokinaFixtureTypes.Zonda3FxLibraryFile));
        var files = new Dictionary<FokinaFixtureTypes.FixtureTypeInfo, string>();
        for (int i = 0; i < FokinaFixtureTypes.NotInRepository.Count; i++)
        {
            var type = FokinaFixtureTypes.NotInRepository[i];
            files[type] = Path.GetFileName(FokinaFixtureTypes.WriteStandIn(folder, type, s_standInIds[i]));
        }
        files[FokinaFixtureTypes.Zonda3Fx] = FokinaFixtureTypes.Zonda3FxLibraryFile;

        var library = FixtureLibrary.Scan(folder);
        Assert.Empty(FokinaFixtureTypes.MissingFrom(library));
        var result = MvrImporter.ImportFile(TestEnvironment.Fokina, new MvrImportOptions { Library = library, ValidateGeometry = false });

        Assert.DoesNotContain(result.Issues, i => i.Severity == IssueSeverity.Error);
        Assert.DoesNotContain(result.Issues, i => i.Code == MvrIssueCodes.GdtfSubstitute);
        Assert.DoesNotContain(result.Issues, i => i.Code == MvrIssueCodes.ModeFootprint);
        foreach (var type in FokinaFixtureTypes.All)
        {
            var reference = result.Gdtfs.Single(g => g.SpecName == type.Spec);
            Assert.Equal(MvrGdtfSource.Library, reference.Source);
            Assert.True(reference.IsExactModel, reference.Describe());
            Assert.Equal(files[type], reference.FileName);
            var mode = Assert.Single(reference.Modes);
            Assert.Equal(type.Footprint, mode.Footprint);
            Assert.Equal(type.Footprint, mode.InferredFootprint);
        }

        // The GDTFs name two of the modes differently from Depence: Rivale "Standard" is "Standard_540" and the Atomic's
        // "Mode 1" is the 3-channel mode. Both are found by name and patch spacing and reported once.
        var unknown = result.Issues.Where(i => i.Code == MvrIssueCodes.ModeUnknown).Select(i => i.Message).ToList();
        Assert.Equal(2, unknown.Count);
        Assert.Contains(unknown, m => m.Contains("'Standard_540'", StringComparison.Ordinal));
        Assert.Contains(unknown, m => m.Contains("'3 Channel'", StringComparison.Ordinal));
        Assert.Equal("Standard_540", result.Gdtfs.Single(g => g.SpecName == FokinaFixtureTypes.RivaleProfile.Spec).Modes[0].Resolved);
        Assert.Equal("3 Channel", result.Gdtfs.Single(g => g.SpecName == FokinaFixtureTypes.Atomic3000Led.Spec).Modes[0].Resolved);

        // The engine patches all 91 fixtures without a single overlap or out-of-range address.
        using var engine = new StageEngine(new StageEngineOptions
        {
            LibraryFolders = [folder],
            CacheDirectory = TestEnvironment.NewTempDirectory("fokina-library-cache"),
            DmxParallelism = 2,
            SimulationParallelism = 2,
        });
        var open = engine.Open(TestEnvironment.Fokina);
        Assert.True(open.Success, open.Summary);
        Assert.DoesNotContain(open.Issues, i => i.Code == MvrIssueCodes.GdtfSubstitute);
        engine.Tick(1.0 / 30);
        Assert.Equal(91, engine.Dmx.FixtureCount);
        foreach (var (entity, fixture) in engine.Scene.Query<FixtureComponent>())
        {
            Assert.Contains(fixture.GdtfSpec, files.Values);
            Assert.NotNull(engine.Dmx.GetState(entity.Id));
        }
        engine.RefreshIssues();
        Assert.DoesNotContain(engine.Issues.Current, i => i.Category == IssueCategory.Patch);
    }
}
