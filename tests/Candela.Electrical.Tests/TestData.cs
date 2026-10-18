using Candela.Gdtf;

namespace Candela.Electrical.Tests;

/// <summary>Repository data shared by the tests: the GDTF library in the root, <c>_specs</c>, fixture profiles.</summary>
internal static class TestData
{
    private static readonly Lazy<string> Root = new(FindRoot);
    private static readonly Lazy<FixtureSpecsIndex> SpecsIndex = new(() => FixtureSpecsIndex.Load(Path.Combine(RepoRoot, FixtureSpecsIndex.DefaultRelativePath)));
    private static readonly Lazy<FixtureLibrary> LibraryValue = new(() => FixtureLibrary.Scan(RepoRoot, Specs, SearchOption.TopDirectoryOnly));
    private static readonly Lazy<LibraryFixtureProfileProvider> ProfilesValue = new(() => new LibraryFixtureProfileProvider(Library));

    // Real fixtures used by the tests (file names of the repository library).
    public const string Naja = "Ayrton@Naja@V1.0_First_Release.gdtf";
    public const string Aeco20 = "ACME@ACME_AECO_20_(AECO_20)@ACME_AECO_20_(AECO_20).gdtf";
    public const string DimmerPack4 = "PRstage@Dimmer_Pack_4@Ver_1_02.gdtf";
    public const string CobCannon = "ADJ@COB_Cannon_LP200X@First_Release_25-24-10.gdtf";
    public const string BmflBlade = "Robe_Lighting@Robin_BMFL_Blade@2026-03-26_Color_macro_empty_wheel_slots_revision.gdtf";
    public const string Face600 = "ACME@ACME_(FACE_600)@ACME__(FACE_600).gdtf";
    public const string Cobra100 = "BeamZ@Cobra_100_Spot@rev100_RELEASE.gdtf";
    public const string PowerPort2000 = "Martin_Professional@P3_PowerPort_2000@20260607.gdtf";

    /// <summary>The folder containing <c>Candela.sln</c> and the GDTF library.</summary>
    public static string RepoRoot => Root.Value;

    /// <summary>The researched specs index.</summary>
    public static FixtureSpecsIndex Specs => SpecsIndex.Value;

    /// <summary>The repository root scanned as a fixture library.</summary>
    public static FixtureLibrary Library => LibraryValue.Value;

    /// <summary>Profiles of library fixtures (shared cache).</summary>
    public static LibraryFixtureProfileProvider Profiles => ProfilesValue.Value;

    /// <summary>Profile of a library fixture (asserted to exist).</summary>
    public static FixtureProfile Profile(string file, string? mode = null)
    {
        var p = Profiles.GetProfile(file, mode);
        Assert.NotNull(p);
        return p!;
    }

    /// <summary>A parsed library fixture.</summary>
    public static FixtureType FixtureType(string file)
    {
        var entry = Library.FindByFileName(file);
        Assert.NotNull(entry);
        return Library.GetFixtureType(entry!);
    }

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Candela.sln")) && File.Exists(Path.Combine(dir.FullName, FixtureSpecsIndex.DefaultRelativePath)))
                return dir.FullName;
        throw new InvalidOperationException("Repository root (Candela.sln + _specs) not found above " + AppContext.BaseDirectory);
    }
}
