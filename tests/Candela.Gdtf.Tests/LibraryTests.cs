using System.Diagnostics;
using System.IO.Compression;
using Xunit.Abstractions;

namespace Candela.Gdtf.Tests;

public sealed class LibraryTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "candela-gdtf-tests-" + Guid.NewGuid().ToString("N"));

    public LibraryTests(ITestOutputHelper output) => _output = output;

    public void Dispose()
    {
        if (Directory.Exists(_temp)) Directory.Delete(_temp, recursive: true);
    }

    [Fact]
    public void Scanning_the_repository_root_lists_every_fixture_with_a_category_without_parsing()
    {
        var sw = Stopwatch.StartNew();
        var library = FixtureLibrary.Scan(TestData.RepoRoot, TestData.Specs);
        _output.WriteLine($"Scanned {library.Count} files in {sw.ElapsedMilliseconds} ms.");
        Assert.Equal(TestData.LibraryFileCount, library.Count);
        Assert.Empty(library.Warnings);
        Assert.Equal(0, library.CachedFixtureTypeCount);
        Assert.All(library.Entries, e =>
        {
            Assert.NotNull(e.Specs);
            Assert.Equal(CategorySource.Specs, e.CategorySource);
            Assert.True(e.FollowsNamingPattern, e.FileName);
            Assert.False(string.IsNullOrWhiteSpace(e.Manufacturer));
            Assert.False(string.IsNullOrWhiteSpace(e.ModelName));
        });

        var groups = library.GroupByCategory();
        Assert.Equal(TestData.LibraryFileCount, groups.Sum(g => g.Entries.Count));
        foreach (var g in groups) _output.WriteLine($"  {g.DisplayName,-15} {g.Entries.Count}");
        Assert.Equal(293, library.GetEntries(FixtureCategory.Wash).Count);
        Assert.Equal(2, library.GetEntries(FixtureCategory.Scanner).Count);
        Assert.Equal("SCANER", groups.Single(g => g.Category == FixtureCategory.Scanner).DisplayName);
        var ordered = groups.Select(g => (int)g.Category).ToList();
        Assert.Equal(ordered.OrderBy(x => x), ordered);
        Assert.Equal(FixtureCategories.All.Count, library.GroupByCategory(includeEmpty: true).Count);

        var megaPointe = library.FindByFileName("Robe_Lighting@Robin_MegaPointe@2026-04-13_Shutter_channel_sets_revision")!;
        Assert.Equal("Robe Lighting", megaPointe.Manufacturer);
        Assert.Equal("Robin MegaPointe", megaPointe.ModelName);
        Assert.Equal(new DateOnly(2026, 4, 13), megaPointe.Revision.Date);
        Assert.Same(megaPointe, library.FindByFileName(Path.Combine("some", "folder", megaPointe.FileName.ToUpperInvariant())));
        Assert.Same(megaPointe, library.FindByFixtureTypeId(Guid.Parse(megaPointe.Specs!.FixtureTypeId!)));
    }

    /// <summary>
    /// The six GDTF references of <c>stages/Фокина.mvr</c> (Depence stubs). Only the Zonda 3 FX exists in the
    /// library; for the others the closest model of the same family is offered as a substitute.
    /// </summary>
    [Theory]
    [InlineData("Ayrton@Zonda 3 FX", "Ayrton@Zonda_3_FX@V1.81_Corrected_Factory_Tilt.gdtf", FixtureMatchKind.SameModel)]
    [InlineData("Ayrton@Rivale Profile", "Ayrton@Rivale_Wash@V1.8_Correct_Gobo_Value.gdtf", FixtureMatchKind.Substitute)]
    [InlineData("Ayrton @Rivale Profile", "Ayrton@Rivale_Wash@V1.8_Correct_Gobo_Value.gdtf", FixtureMatchKind.Substitute)]
    [InlineData("Ayrton@MagicBlade R", "Ayrton@MagicPanel_602@V2.5_Add_Mode.gdtf", FixtureMatchKind.Substitute)]
    [InlineData("Ayrton@Zonda 3 Wash", "Ayrton@Zonda_3_FX@V1.81_Corrected_Factory_Tilt.gdtf", FixtureMatchKind.Substitute)]
    [InlineData("Martin@Atomic 3000 LED", "Martin_Professional@VDO_Atomic_Bold@20260620.gdtf", FixtureMatchKind.Substitute)]
    public void Mvr_stub_names_resolve_to_library_files(string stub, string expectedFile, FixtureMatchKind expectedKind)
    {
        var library = FixtureLibrary.Scan(TestData.RepoRoot, TestData.Specs);
        var match = library.FindBySpec(stub + ".gdtf");
        Assert.NotNull(match);
        Assert.Equal(expectedFile, match!.Entry.FileName);
        Assert.Equal(expectedKind, match.Kind);
        Assert.InRange(match.Score, 0.25, 1.0);
        var parts = stub.Split('@');
        Assert.Equal(match, library.Find(parts[0], parts[1]));
    }

    [Fact]
    public void The_stub_names_are_the_ones_embedded_in_the_Fokina_scene()
    {
        Assert.True(File.Exists(TestData.FokinaMvr), "stages/Фокина.mvr is not present.");
        using var mvr = ZipFile.OpenRead(TestData.FokinaMvr);
        var stubs = mvr.Entries.Where(e => e.FullName.EndsWith(".gdtf", StringComparison.OrdinalIgnoreCase))
            .Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.Equal(new[]
        {
            "Ayrton @Rivale Profile.gdtf", "Ayrton@MagicBlade R.gdtf", "Ayrton@Zonda 3 FX.gdtf", "Ayrton@Zonda 3 Wash.gdtf",
            "Martin@Atomic 3000 LED.gdtf",
        }, stubs);

        var library = FixtureLibrary.Scan(TestData.RepoRoot, TestData.Specs);
        foreach (var entry in mvr.Entries.Where(e => e.FullName.EndsWith(".gdtf", StringComparison.OrdinalIgnoreCase)))
        {
            using var stream = entry.Open();
            var stub = GdtfReader.Read(stream, entry.FullName);
            Assert.Single(stub.Models); // Depence stubs: one empty model, one dimmer channel per mode
            var byName = library.Find(stub.Manufacturer, stub.Name);
            var bySpec = library.FindBySpec(entry.FullName);
            Assert.NotNull(byName);
            Assert.Equal(bySpec!.Entry, byName!.Entry);
            _output.WriteLine($"{entry.FullName} -> {byName.Entry.FileName} ({byName.Kind}, {byName.Score:F2})");
        }
    }

    [Theory]
    [InlineData("Robe", "Robin MegaPointe", "Robe_Lighting@Robin_MegaPointe@")]
    [InlineData("ROBE LIGHTING", "robin_megapointe", "Robe_Lighting@Robin_MegaPointe@")]
    [InlineData("Robe_Lighting", "Robin-MegaPointe", "Robe_Lighting@Robin_MegaPointe@")]
    [InlineData("Clay Paky", "Sharpy", "Clay_Paky@Sharpy@")]
    [InlineData("ClayPaky", "SHARPY", "Clay_Paky@Sharpy@")]
    [InlineData("Martin", "MAC Aura", "Martin_Professional@MAC_Aura@")]
    [InlineData("Martin Professional", "Martin MAC Aura XB", "Martin_Professional@MAC_Aura_XB@")]
    [InlineData("Chauvet", "Maverick Storm 2 Profile", "Chauvet_Professional@Maverick_Storm_2_Profile@")]
    [InlineData("Ayrton ", "Zonda 3 FX", "Ayrton@Zonda_3_FX@")]
    [InlineData("", "Robin MegaPointe", "Robe_Lighting@Robin_MegaPointe@")]
    [InlineData(null, "Martin MAC Aura", "Martin_Professional@MAC_Aura@")]
    public void Find_ignores_case_separators_punctuation_and_brand_suffixes(string? manufacturer, string model, string expectedPrefix)
    {
        var library = FixtureLibrary.Scan(TestData.RepoRoot, TestData.Specs);
        var match = library.Find(manufacturer, model);
        Assert.NotNull(match);
        Assert.True(match!.IsExactModel, $"{match.Kind} {match.Entry}");
        Assert.StartsWith(expectedPrefix, match.Entry.FileName);
    }

    [Theory]
    [InlineData("Robe", "Teleporter 9000")]
    [InlineData("Nobody Lighting", "Foo")]
    [InlineData("Ayrton", "")]
    [InlineData("Ayrton", "Wash")] // a class word alone is not a family
    public void Find_returns_null_when_nothing_fits(string manufacturer, string model)
    {
        var library = FixtureLibrary.Scan(TestData.RepoRoot, TestData.Specs);
        Assert.Null(library.Find(manufacturer, model));
    }

    [Fact]
    public void Newest_revision_wins_by_date_or_version_and_exact_file_names_win_over_both()
    {
        Directory.CreateDirectory(_temp);
        string[] names =
        {
            "Robe_Lighting@Robin_Test@2024-01-05_First.gdtf",
            "Robe_Lighting@Robin_Test@2025-11-07_Second.gdtf",
            "Robe_Lighting@Robin_Test@2023-12-31_Old.gdtf",
            "Ayrton@Test_Wash@V1.8_Old.gdtf",
            "Ayrton@Test_Wash@V1.10_New.gdtf",
            "ADJ@Odd_Date@2024-21-04_Release.gdtf",
            "ADJ@Odd_Date@2024-20-11_Release.gdtf",
            "no_pattern.gdtf",
        };
        foreach (var n in names) File.WriteAllBytes(Path.Combine(_temp, n), new byte[] { 1 });
        var library = FixtureLibrary.Scan(_temp);
        Assert.Equal(names.Length, library.Count);
        Assert.Contains(library.Warnings, w => w.Contains("no_pattern.gdtf", StringComparison.Ordinal));

        var robe = library.Find("Robe", "Robin Test")!;
        Assert.Equal("Robe_Lighting@Robin_Test@2025-11-07_Second.gdtf", robe.Entry.FileName);
        Assert.Equal(FixtureMatchKind.SameModel, robe.Kind);
        Assert.Equal(new[] { "2025-11-07_Second", "2024-01-05_First", "2023-12-31_Old" }, library.GetRevisions(robe.Entry).Select(e => e.RevisionText));
        Assert.True(library.IsLatestRevision(robe.Entry));

        Assert.Equal("Ayrton@Test_Wash@V1.10_New.gdtf", library.Find("Ayrton", "Test Wash")!.Entry.FileName);
        Assert.Equal("ADJ@Odd_Date@2024-20-11_Release.gdtf", library.Find("ADJ", "Odd Date")!.Entry.FileName);

        var exact = library.FindBySpec("Robe_Lighting@Robin_Test@2024-01-05_First.gdtf")!;
        Assert.Equal(FixtureMatchKind.ExactFileName, exact.Kind);
        Assert.Equal("Robe_Lighting@Robin_Test@2024-01-05_First.gdtf", exact.Entry.FileName);
        var sameRevision = library.FindBySpec("Robe@Robin Test@2023-12-31 Old")!;
        Assert.Equal("Robe_Lighting@Robin_Test@2023-12-31_Old.gdtf", sameRevision.Entry.FileName);

        var latest = library.GroupByCategory(latestRevisionsOnly: true).SelectMany(g => g.Entries).ToList();
        Assert.Equal(4, latest.Count);
        Assert.Equal(FixtureCategory.Wash, library.FindByFileName("Ayrton@Test_Wash@V1.8_Old")!.Category);
        Assert.Equal(CategorySource.NameHeuristic, library.FindByFileName("Ayrton@Test_Wash@V1.8_Old")!.CategorySource);
    }

    [Fact]
    public void Missing_folders_and_duplicate_file_names_are_warnings()
    {
        Directory.CreateDirectory(Path.Combine(_temp, "a"));
        Directory.CreateDirectory(Path.Combine(_temp, "b"));
        File.WriteAllBytes(Path.Combine(_temp, "a", "X@Y@1.gdtf"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(_temp, "b", "X@Y@1.gdtf"), new byte[] { 2 });
        File.WriteAllBytes(Path.Combine(_temp, "b", "readme.txt"), new byte[] { 2 });
        var library = FixtureLibrary.Scan(new[] { Path.Combine(_temp, "a"), Path.Combine(_temp, "missing"), Path.Combine(_temp, "b") });
        Assert.Equal(1, library.Count);
        Assert.StartsWith(Path.Combine(_temp, "a"), library.Entries[0].FilePath, StringComparison.Ordinal);
        Assert.Equal(2, library.Warnings.Count);
        Assert.Equal(2, library.Folders.Count);
        Assert.Equal(0, library.AddFolder(Path.Combine(_temp, "a")));
    }

    [Fact]
    public async Task Parsing_is_on_demand_cached_shared_between_threads_and_bounded()
    {
        var library = new FixtureLibrary(TestData.Specs, cacheCapacity: 2);
        library.AddFiles(new[]
        {
            TestData.Fixture("Robe_Lighting@Robin_MegaPointe@"), TestData.Fixture("Clay_Paky@Sharpy@"), TestData.Fixture("Martin_Professional@MAC_Aura@"),
        });
        var entries = library.Entries;
        Assert.Equal(3, entries.Count);
        var first = entries.First(e => e.FileName.StartsWith("Robe", StringComparison.Ordinal));
        Assert.False(library.TryGetCachedFixtureType(first, out _));

        var results = new FixtureType[8];
        Parallel.For(0, results.Length, i => results[i] = library.GetFixtureType(first));
        Assert.All(results, r => Assert.Same(results[0], r));
        Assert.Equal("Robin MegaPointe", results[0].Name);
        Assert.True(library.TryGetCachedFixtureType(first, out var cached));
        Assert.Same(results[0], cached);

        var tasks = entries.Select(e => library.GetFixtureTypeAsync(e)).ToArray();
        var parsed = await Task.WhenAll(tasks);
        Assert.All(parsed, ft => Assert.NotEmpty(ft.DmxModes));
        Assert.True(library.CachedFixtureTypeCount <= 2);

        library.ClearCache();
        Assert.Equal(0, library.CachedFixtureTypeCount);
    }

    [Fact]
    public void Unreadable_files_throw_and_are_not_cached()
    {
        Directory.CreateDirectory(_temp);
        File.WriteAllText(Path.Combine(_temp, "Bad@File@1.gdtf"), "not a zip");
        var library = FixtureLibrary.Scan(_temp);
        var entry = library.Entries.Single();
        Assert.Throws<GdtfFormatException>(() => library.GetFixtureType(entry));
        Assert.Equal(0, library.CachedFixtureTypeCount);
        Assert.Equal(FixtureCategory.Other, entry.Category);
    }

    [Theory]
    [InlineData("Martin Professional", "martin")]
    [InlineData("Martin_Professional", "martin")]
    [InlineData("Robe_Lighting", "robe")]
    [InlineData("Robe", "robe")]
    [InlineData("Clay Paky", "claypaky")]
    [InlineData("Clay_Paky", "claypaky")]
    [InlineData("Chauvet_Professional", "chauvet")]
    [InlineData("Ayrton ", "ayrton")]
    [InlineData("Astera_LED_Technology", "astera")]
    [InlineData("ADJ (Eliminator Lighting)", "adj")]
    [InlineData("American DJ", "adj")]
    [InlineData("JB-Lighting", "jb")]
    [InlineData("Vari-Lite", "varilite")]
    [InlineData("Pro", "pro")]
    public void Manufacturer_keys_reduce_names_to_the_brand(string name, string key) => Assert.Equal(key, FixtureNames.ManufacturerKey(name));

    [Fact]
    public void Name_keys_words_and_similarity()
    {
        Assert.Equal("robint1profile", FixtureNames.Key("Robin_T1 Profile"));
        Assert.Equal(new[] { "magic", "blade", "r" }, FixtureNames.Words("MagicBlade R"));
        Assert.Equal(new[] { "colorado", "solo", "bar", "6" }, FixtureNames.Words("COLORado_Solo-Bar 6"));
        Assert.True(FixtureNames.ModelSimilarity("Zonda 3 Wash", "Zonda 3 FX") > FixtureNames.ModelSimilarity("Zonda 3 Wash", "Rivale Wash"));
        Assert.Equal(0, FixtureNames.ModelSimilarity("Diablo Wash", "Rivale Wash"));
        Assert.Equal(0, FixtureNames.ModelSimilarity("", "Rivale Wash"));
    }

    [Theory]
    [InlineData("2026-07-21_Color_control_channel_revision", 2026, 7, 21)]
    [InlineData("20260620", 2026, 6, 20)]
    [InlineData("2022.12.14", 2022, 12, 14)]
    [InlineData("25-31-10_Release", 2025, 10, 31)]
    [InlineData("Release_2024-20-11", 2024, 11, 20)]
    [InlineData("Firmware_1_00_2026_07_12", 2026, 7, 12)]
    public void Revision_dates_are_found_in_file_names(string text, int y, int m, int d) =>
        Assert.Equal(new DateOnly(y, m, d), FixtureRevision.Parse(text).Date);

    [Theory]
    [InlineData("V1.81_Corrected_Factory_Tilt", "1.81")]
    [InlineData("V_1.09", "1.9")]
    [InlineData("Rev_002_-_All_Modes", "2.0")]
    [InlineData("rev101_RELEASE", "101.0")]
    [InlineData("tested_by_Astera__V3", "3.0")]
    public void Revision_versions_are_found_in_file_names(string text, string version) =>
        Assert.Equal(Version.Parse(version), FixtureRevision.Parse(text).Version);

    [Fact]
    public void Revisions_order_by_date_then_version_then_file_time()
    {
        var older = FixtureRevision.Parse("2024-01-01");
        var newer = FixtureRevision.Parse("2025-01-01");
        Assert.True(newer.CompareTo(older) > 0);
        Assert.True(FixtureRevision.Parse("V2").CompareTo(FixtureRevision.Parse("V10")) < 0);
        Assert.True(FixtureRevision.Parse("x", new DateTime(2020, 1, 2)).CompareTo(FixtureRevision.Parse("x", new DateTime(2020, 1, 1))) > 0);
        Assert.Null(FixtureRevision.Parse("Offical").Date);
        Assert.Null(FixtureRevision.Parse("Offical").Version);
    }

    [Theory]
    [InlineData("Robin T1 Profile", FixtureCategory.Profile)]
    [InlineData("COLORado Solo Bar 6", FixtureCategory.LedBar)]
    [InlineData("Hazer 1000", FixtureCategory.Haze)]
    [InlineData("Beam Wash 7", FixtureCategory.WashBeam)]
    [InlineData("Atomic Strobe", FixtureCategory.Strobe)]
    [InlineData("Follow Spot 2500", FixtureCategory.FollowSpot)]
    [InlineData("Mystery Box", FixtureCategory.Other)]
    public void Category_guess_uses_model_name_words(string model, FixtureCategory expected) =>
        Assert.Equal(expected, FixtureCategories.Guess(model));

    [Fact]
    public void Category_display_names_are_exactly_the_project_spelling()
    {
        Assert.Equal(
            new[]
            {
                "PROFILE", "WASH", "WASH BEAM", "BEAM", "SPOT", "FOLLOW SPOT", "LED BAR", "PAR", "HAZE", "FOG", "FAN", "SCANER", "LASER",
                "STROBE", "BLINDER", "UV", "BSW", "GENERATOR FIRE", "OTHER",
            },
            FixtureCategories.All.Select(c => c.DisplayName()));
        foreach (var c in FixtureCategories.All)
        {
            Assert.True(FixtureCategories.TryParse(c.DisplayName(), out var parsed));
            Assert.Equal(c, parsed);
        }
        Assert.Equal(FixtureCategory.WashBeam, FixtureCategories.Parse("wash_beam"));
        Assert.Equal(FixtureCategory.Scanner, FixtureCategories.Parse("Scanner"));
        Assert.Equal(FixtureCategory.Other, FixtureCategories.Parse("nonsense"));
    }
}
