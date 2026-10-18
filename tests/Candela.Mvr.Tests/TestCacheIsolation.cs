using System.Globalization;
using System.Runtime.CompilerServices;

namespace Candela.Mvr.Tests;

/// <summary>
/// Points the fixture-type disk cache (which <see cref="Candela.Gdtf.FixtureLibrary"/> uses by default, under
/// <c>ConfigPaths.CacheDir</c>) at a folder of this test run that is deleted when the run ends, so tests neither write
/// into the user's cache folder nor depend on what an earlier run left there. An explicit <c>CANDELA_CACHE_DIR</c> wins.
/// </summary>
internal static class TestCacheIsolation
{
#pragma warning disable CA2255 // a test assembly is the intended place for this module initializer
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Initialize()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CANDELA_CACHE_DIR"))) return;
        string dir = Path.Combine(Path.GetTempPath(), "candela-test-cache", Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        Environment.SetEnvironmentVariable("CANDELA_CACHE_DIR", dir);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Best effort: the temporary folder is cleaned up by the system otherwise.
            }
        };
    }
}
