using System.IO.Compression;
using System.Text;
using Candela.Gdtf;

namespace Candela.Mvr.Tests;

/// <summary>Locates the repository data (GDTF library in the root, <c>_specs</c>, <c>stages</c>) and builds synthetic archives.</summary>
internal static class TestData
{
    private static readonly Lazy<string> Root = new(FindRoot);
    private static readonly Lazy<FixtureLibrary> SharedLibrary = new(() =>
        FixtureLibrary.Scan(RepoRoot, FixtureSpecsIndex.Load(Path.Combine(RepoRoot, FixtureSpecsIndex.DefaultRelativePath))));

    /// <summary>The folder containing <c>Candela.sln</c>.</summary>
    public static string RepoRoot => Root.Value;

    /// <summary>Path of the repository's <c>stages/Фокина.mvr</c> (Depence export, 91 fixtures, stub GDTFs).</summary>
    public static string FokinaMvr => Path.Combine(RepoRoot, "stages", "Фокина.mvr");

    /// <summary>The fixture library over the repository root (scanned once per test run; parsing is cached and thread-safe).</summary>
    public static FixtureLibrary Library => SharedLibrary.Value;

    /// <summary>A zip archive in memory with the given entries.</summary>
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

    /// <summary>UTF-8 bytes of a string.</summary>
    public static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Candela.sln"))) return dir.FullName;
        throw new InvalidOperationException("Candela.sln not found above " + AppContext.BaseDirectory);
    }
}
