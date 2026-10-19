using Candela.App.Diagnostics;
using Candela.App.Session;
using Candela.App.Settings;
using Candela.Engine;
using Candela.Gdtf;

namespace Candela.App.Tests;

/// <summary>
/// The repository's fixture library (1091 GDTF files plus <c>_specs</c>), scanned once for every test, and editor
/// sessions over it with private cache and autosave folders, so no test scans the library again or writes to the
/// user's folders.
/// </summary>
internal static class EngineTestSupport
{
    private static readonly Lazy<string> s_root = new(() =>
        DevelopmentLibrary.FindRepositoryRoot() ?? throw new InvalidOperationException("Tests must run inside the repository (Candela.sln not found)."));

    private static readonly Lazy<FixtureLibrary> s_library = new(() =>
        GenericDeviceFiles.AddTo(FixtureLibrary.Scan(Root, FixtureSpecsIndex.Load(Path.Combine(Root, FixtureSpecsIndex.DefaultRelativePath)))));

    /// <summary>The repository root.</summary>
    public static string Root => s_root.Value;

    /// <summary>The shared fixture library of the repository.</summary>
    public static FixtureLibrary Library => s_library.Value;

    /// <summary><c>stages/Фокина.mvr</c> (91 fixtures, exported by Depence).</summary>
    public static string Fokina => Path.Combine(Root, "stages", "Фокина.mvr");

    /// <summary>Session options over the shared library with caches and autosaves inside <paramref name="dir"/>.</summary>
    public static EditorSessionOptions Options(TempDir dir, bool previewStage = false, bool autosave = false) => new()
    {
        Engine = new StageEngineOptions
        {
            Library = Library,
            CacheDirectory = dir.File("cache"),
            DmxParallelism = 2,
            SimulationParallelism = 2,
        },
        AutosaveDirectory = dir.File("autosave"),
        Autosave = autosave,
        PreviewStage = previewStage,
        LoadFixtureModelsInBackground = false,
    };

    /// <summary>A session over the shared library.</summary>
    public static EditorSession CreateSession(AppLog log, SettingsStore settings, TempDir dir, bool previewStage = false, bool autosave = false) =>
        new(log, settings, Options(dir, previewStage, autosave));
}
