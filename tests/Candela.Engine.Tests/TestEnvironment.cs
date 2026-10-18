using Candela.Engine;
using Candela.Gdtf;

namespace Candela.Engine.Tests;

/// <summary>
/// Shared test data: the repository (its GDTF library, <c>_specs</c> and <c>stages/</c>), one fixture library scanned
/// once for all tests, and engines with a private cache folder.
/// </summary>
internal static class TestEnvironment
{
    private static readonly Lazy<string> s_root = new(() =>
        DevelopmentLibrary.FindRepositoryRoot() ?? throw new InvalidOperationException("Tests must run inside the repository (Candela.sln not found)."));

    private static readonly Lazy<FixtureLibrary> s_library = new(() =>
        FixtureLibrary.Scan(Root, FixtureSpecsIndex.Load(Path.Combine(Root, FixtureSpecsIndex.DefaultRelativePath))));

    /// <summary>The repository root (folder of Candela.sln, holding the 1091 GDTF files).</summary>
    public static string Root => s_root.Value;

    /// <summary>The repository's fixture library (shared; scanned once).</summary>
    public static FixtureLibrary Library => s_library.Value;

    /// <summary>Path of <c>stages/Фокина.mvr</c>.</summary>
    public static string Fokina => Path.Combine(Root, "stages", "Фокина.mvr");

    /// <summary>A new empty folder below the test output (removed by the OS / next build clean).</summary>
    public static string NewTempDirectory(string name)
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "test-temp", name + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>A library file by name (the test fails clearly when the repository lacks it).</summary>
    public static FixtureLibraryEntry Entry(string fileName) =>
        Library.FindByFileName(fileName) ?? throw new InvalidOperationException($"'{fileName}' is not in the repository's library.");

    /// <summary>An engine over the shared library with its own cache folder.</summary>
    public static StageEngine CreateEngine(string name = "engine", bool isolateNativeImports = true, int dmxThreads = 2) =>
        new(new StageEngineOptions
        {
            Library = Library,
            CacheDirectory = NewTempDirectory(name + "-cache"),
            IsolateNativeImports = isolateNativeImports,
            DmxParallelism = dmxThreads,
            SimulationParallelism = 2,
        });
}
