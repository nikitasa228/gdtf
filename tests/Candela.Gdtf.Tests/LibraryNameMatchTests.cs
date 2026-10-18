using System.Text;

namespace Candela.Gdtf.Tests;

/// <summary>
/// Name matching where two models answer to the same normalised name. "Robin Spikie+" (file <c>Robin_SpikiePlus</c>)
/// used to normalise to the key of "Robin Spikie", so <c>Find("Robe Lighting", "Robin Spikie")</c> returned whichever
/// of the two files had the later modification time — the Spikie+ on the reference checkout.
/// </summary>
public sealed class LibraryNameMatchTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "candela-gdtf-names-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_temp)) Directory.Delete(_temp, recursive: true);
    }

    [Theory]
    [InlineData("Robin Spikie+", "Robin_SpikiePlus")]
    [InlineData("SIX+ BAR L", "SIX_plus_BAR_L")]
    [InlineData("PAR 2410 RGBW+UV", "PAR_2410_RGBW+UV")]
    public void A_plus_is_spelled_out_like_file_names_do(string written, string fileModel) =>
        Assert.Equal(FixtureNames.Key(fileModel), FixtureNames.Key(written));

    [Fact]
    public void A_plus_keeps_models_apart()
    {
        Assert.NotEqual(FixtureNames.Key("Robin Spikie"), FixtureNames.Key("Robin Spikie+"));
        Assert.Equal(new[] { "robin", "spikie", "plus" }, FixtureNames.Words("Robin Spikie+"));
        Assert.Equal(new[] { "robin", "spikie", "plus" }, FixtureNames.Words("Robin_SpikiePlus"));
    }

    [Fact]
    public void Robin_Spikie_and_Spikie_plus_resolve_to_their_own_files_in_the_library()
    {
        var library = FixtureLibrary.Scan(TestData.RepoRoot, TestData.Specs);
        foreach (var (model, file) in new[] { ("Robin Spikie", "Robe_Lighting@Robin_Spikie@"), ("Robin Spikie+", "Robe_Lighting@Robin_SpikiePlus@"), ("Robin SpikiePlus", "Robe_Lighting@Robin_SpikiePlus@") })
        {
            var match = library.Find("Robe Lighting", model);
            Assert.NotNull(match);
            Assert.Equal(FixtureMatchKind.SameModel, match!.Kind);
            Assert.StartsWith(file, match.Entry.FileName);
        }
    }

    /// <summary>
    /// A file whose model name matches beats a newer file that only has the name as a specs alias, whatever the file
    /// times say.
    /// </summary>
    [Fact]
    public void A_file_name_match_beats_a_newer_specs_alias()
    {
        Directory.CreateDirectory(_temp);
        string plain = Path.Combine(_temp, "Robe_Lighting@Robin_Spikie@2025-06-10_Rev.gdtf");
        string plus = Path.Combine(_temp, "Robe_Lighting@Robin_SpikiePlus@2025-06-10_Rev.gdtf");
        string alpha = Path.Combine(_temp, "Brand@Alpha@2026-01-01_New.gdtf");
        string beta = Path.Combine(_temp, "Brand@Beta@2020-01-01_Old.gdtf");
        foreach (var f in new[] { plain, plus, alpha, beta }) File.WriteAllBytes(f, new byte[] { 1 });
        File.SetLastWriteTimeUtc(plain, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(plus, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)); // the alias holder is newer
        string json = """
            [
              { "file": "Robe_Lighting@Robin_SpikiePlus@2025-06-10_Rev.gdtf", "category": "BEAM", "manufacturer": "Robe Lighting", "longName": "Robin Spikie+" },
              { "file": "Robe_Lighting@Robin_Spikie@2025-06-10_Rev.gdtf", "category": "BEAM", "manufacturer": "Robe Lighting", "longName": "Robin Spikie" },
              { "file": "Brand@Alpha@2026-01-01_New.gdtf", "category": "WASH", "manufacturer": "Brand", "longName": "Beta" }
            ]
            """;
        var specs = FixtureSpecsIndex.Load(new MemoryStream(Encoding.UTF8.GetBytes(json)));
        var library = FixtureLibrary.Scan(_temp, specs);

        Assert.Equal(Path.GetFileName(plain), library.Find("Robe", "Robin Spikie")!.Entry.FileName);
        Assert.Equal(Path.GetFileName(plus), library.Find("Robe", "Robin Spikie+")!.Entry.FileName);
        // "Beta" is the specs long name of the newer Alpha file and the file name of the older Beta file.
        Assert.Equal(Path.GetFileName(beta), library.Find("Brand", "Beta")!.Entry.FileName);
        Assert.Equal(Path.GetFileName(beta), library.Find(null, "Beta")!.Entry.FileName);
        Assert.Equal(Path.GetFileName(alpha), library.Find("Brand", "Alpha")!.Entry.FileName);
    }
}
