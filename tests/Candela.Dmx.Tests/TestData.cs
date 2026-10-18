using Candela.Gdtf;

namespace Candela.Dmx.Tests;

/// <summary>Locates the repository data (GDTF library in the root, <c>_specs</c>) and shares one library and profile cache.</summary>
internal static class TestData
{
    private static readonly Lazy<string> Root = new(FindRoot);
    private static readonly Lazy<FixtureSpecsIndex> SpecsIndex = new(() => FixtureSpecsIndex.Load(Path.Combine(RepoRoot, FixtureSpecsIndex.DefaultRelativePath)));
    private static readonly Lazy<FixtureLibrary> SharedLibrary = new(() => FixtureLibrary.Scan(RepoRoot, Specs));
    private static readonly Lazy<FixtureProfileCache> SharedProfiles = new(() => new FixtureProfileCache(Library));

    /// <summary>The folder containing <c>Candela.sln</c>.</summary>
    public static string RepoRoot => Root.Value;

    /// <summary>The researched specs index.</summary>
    public static FixtureSpecsIndex Specs => SpecsIndex.Value;

    /// <summary>The repository root scanned as a fixture library.</summary>
    public static FixtureLibrary Library => SharedLibrary.Value;

    /// <summary>A profile cache over <see cref="Library"/>.</summary>
    public static FixtureProfileCache Profiles => SharedProfiles.Value;

    /// <summary>Library entry of the single file whose name starts with <paramref name="prefix"/>.</summary>
    public static FixtureLibraryEntry Entry(string prefix)
    {
        var hits = Library.Entries.Where(e => e.FileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.True(hits.Count == 1, $"Expected one library file starting with '{prefix}', found {hits.Count}.");
        return hits[0];
    }

    /// <summary>Compiled profile of the file starting with <paramref name="prefix"/> in <paramref name="mode"/> (null = first).</summary>
    public static FixtureProfile Profile(string prefix, string? mode = null)
    {
        var r = Profiles.Get(Entry(prefix), mode);
        Assert.True(r.Profile is not null, r.Error);
        return r.Profile!;
    }

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Candela.sln"))) return dir.FullName;
        throw new InvalidOperationException("Candela.sln not found above " + AppContext.BaseDirectory);
    }
}
