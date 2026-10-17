using System.IO.Compression;
using System.Text;

namespace Candela.Gdtf.Tests;

/// <summary>Locates the repository data (GDTF library in the root, <c>_specs</c>, <c>stages</c>) and builds synthetic archives.</summary>
internal static class TestData
{
    private static readonly Lazy<string> Root = new(FindRoot);
    private static readonly Lazy<FixtureSpecsIndex> SpecsIndex = new(() => FixtureSpecsIndex.Load(Path.Combine(RepoRoot, FixtureSpecsIndex.DefaultRelativePath)));
    private static readonly Lazy<string[]> Files = new(() => Directory.GetFiles(RepoRoot, "*.gdtf").OrderBy(f => f, StringComparer.Ordinal).ToArray());

    /// <summary>Number of fixture files committed in the repository root.</summary>
    public const int LibraryFileCount = 1091;

    /// <summary>The folder containing <c>Candela.sln</c>.</summary>
    public static string RepoRoot => Root.Value;

    /// <summary>All <c>.gdtf</c> files of the repository root, sorted.</summary>
    public static IReadOnlyList<string> GdtfFiles => Files.Value;

    /// <summary>The researched specs index.</summary>
    public static FixtureSpecsIndex Specs => SpecsIndex.Value;

    /// <summary>Path of the repository's <c>stages/Фокина.mvr</c>.</summary>
    public static string FokinaMvr => Path.Combine(RepoRoot, "stages", "Фокина.mvr");

    /// <summary>Full path of the single library file whose name starts with <paramref name="prefix"/>.</summary>
    public static string Fixture(string prefix)
    {
        var hits = GdtfFiles.Where(f => Path.GetFileName(f).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.True(hits.Count == 1, $"Expected one library file starting with '{prefix}', found {hits.Count}.");
        return hits[0];
    }

    /// <summary>A zip archive in memory with the given entries (text entries are UTF-8).</summary>
    public static byte[] Zip(params (string Name, byte[] Data)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, data) in entries)
            {
                var e = zip.CreateEntry(name, CompressionLevel.Fastest);
                using var s = e.Open();
                s.Write(data);
            }
        }
        return ms.ToArray();
    }

    /// <summary>A GDTF archive with only a description.xml.</summary>
    public static byte[] GdtfWithDescription(string xml) => Zip(("description.xml", Encoding.UTF8.GetBytes(xml)));

    /// <summary>Reads a synthetic description.xml string.</summary>
    public static FixtureType ReadXml(string xml)
    {
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        return GdtfReader.ReadDescription(ms, "test.gdtf");
    }

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Candela.sln"))) return dir.FullName;
        throw new InvalidOperationException("Candela.sln not found above " + AppContext.BaseDirectory);
    }
}
