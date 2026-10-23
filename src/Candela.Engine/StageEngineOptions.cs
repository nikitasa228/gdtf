using Candela.Core.Settings;
using Candela.Gdtf;

namespace Candela.Engine;

/// <summary>
/// How a <see cref="StageEngine"/> is set up: where fixtures, researched specs and caches live and how the isolated
/// model importer is found. Built from the user's <see cref="AppSettings"/> by <see cref="FromSettings"/>; tests and
/// tools set the properties directly.
/// </summary>
public sealed record StageEngineOptions
{
    /// <summary>Folders scanned for <c>.gdtf</c> files, in priority order. Empty: the development library (see <see cref="FromSettings"/>).</summary>
    public IReadOnlyList<string> LibraryFolders { get; init; } = Array.Empty<string>();

    /// <summary>Path of <c>_specs/fixtures_index.json</c>; null looks next to (and above) the first library folder.</summary>
    public string? SpecsIndexPath { get; init; }

    /// <summary>An already loaded fixture library to share (tests, several engines in one process); overrides <see cref="LibraryFolders"/>.</summary>
    public FixtureLibrary? Library { get; init; }

    /// <summary>
    /// Folder for the engine's caches: decoded models of the import worker, GDTF files extracted from MVR files and
    /// projects, thumbnails. Null uses <see cref="ConfigPaths.CacheDir"/>.
    /// </summary>
    public string? CacheDirectory { get; init; }

    /// <summary>
    /// Path of the program that imports 3D models in a separate process (<c>candela import-worker</c>): the
    /// <c>candela</c> executable or <c>candela.dll</c>. Null searches <c>CANDELA_IMPORT_WORKER</c> and next to the running
    /// program; a path that does not exist means "no worker". Without a worker, models are imported inside the
    /// application process and a note says so (see docs/engine.md).
    /// </summary>
    public string? ImportWorkerPath { get; init; }

    /// <summary>Longest time one model import may take in the worker before it is stopped and reported.</summary>
    public TimeSpan ImportWorkerTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// When true (the default) model formats read by the native Assimp library (OBJ, FBX, COLLADA, 3DS) are decoded in the
    /// import worker; false decodes everything in-process (only for trusted files, e.g. in tests of other features).
    /// </summary>
    public bool IsolateNativeImports { get; init; } = true;

    /// <summary>Threads used to evaluate fixtures (default: all cores). Results do not depend on it.</summary>
    public int DmxParallelism { get; init; } = Math.Max(1, Environment.ProcessorCount);

    /// <summary>Threads used by the smoke simulation (−1: all cores). Results do not depend on it.</summary>
    public int SimulationParallelism { get; init; } = -1;

    /// <summary>
    /// Internal universe = Art-Net port-address + this offset (see <see cref="AppSettings.ArtNetUniverseOffset"/>).
    /// </summary>
    public int ArtNetUniverseOffset { get; init; } = 1;

    /// <summary>Network interface for protocol input (IPv4 address or adapter name; empty = all).</summary>
    public string NetworkInterface { get; init; } = "";

    /// <summary>
    /// Options for a program run from the user's settings, located by <see cref="InstallLayout.Current"/>: the library
    /// folders of the settings (or the repository during development, when none is set), then the starter fixtures of an
    /// installed copy; the specs of the settings, else the ones next to a library folder, the installed copy or the
    /// repository's (see <see cref="InstallLayout"/> for the order).
    /// </summary>
    public static StageEngineOptions FromSettings(AppSettings settings) => FromSettings(settings, null, InstallLayout.Current);

    /// <summary>
    /// Options for a program run from the user's settings with the library folders <paramref name="libraryFolders"/>
    /// instead of the settings' ones (the CLI's <c>--library</c>; null or empty keeps the settings' folders), located by
    /// <paramref name="layout"/> (default <see cref="InstallLayout.Current"/>).
    /// </summary>
    public static StageEngineOptions FromSettings(AppSettings settings, IReadOnlyList<string>? libraryFolders, InstallLayout? layout = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        layout ??= InstallLayout.Current;
        var configured = libraryFolders is { Count: > 0 } ? libraryFolders : settings.LibraryFolders ?? new List<string>();
        var folders = layout.LibraryFoldersFor(configured);
        string? specs = string.IsNullOrWhiteSpace(settings.SpecsIndexPath) ? layout.FindSpecsIndex(folders) : settings.SpecsIndexPath;
        return new StageEngineOptions
        {
            LibraryFolders = folders,
            SpecsIndexPath = specs,
            ArtNetUniverseOffset = settings.ArtNetUniverseOffset,
            NetworkInterface = settings.NetworkInterface ?? "",
        };
    }
}

/// <summary>Finds the repository (the folder containing <c>Candela.sln</c>), which holds the bundled fixture library.</summary>
public static class DevelopmentLibrary
{
    /// <summary>Marker file of the repository root.</summary>
    public const string SolutionFile = "Candela.sln";

    /// <summary>
    /// The folder containing <see cref="SolutionFile"/>, searched upwards from <paramref name="startDirectory"/>, the
    /// program's folder and the current directory; null when none of them is inside the repository.
    /// </summary>
    public static string? FindRepositoryRoot(string? startDirectory = null)
    {
        foreach (var start in new[] { startDirectory, AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            if (string.IsNullOrWhiteSpace(start)) continue;
            try
            {
                for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
                    if (File.Exists(Path.Combine(dir.FullName, SolutionFile))) return dir.FullName;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
            {
                // An unreadable folder on the way up ends the search from this start point.
            }
        }
        return null;
    }
}
