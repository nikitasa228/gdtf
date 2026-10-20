using Candela.Gdtf;

namespace Candela.Engine;

/// <summary>
/// Where Candela finds the files it ships with, in an installed copy and in the development layout (the repository).
/// An installed copy (the Windows installer, a <c>dotnet publish</c> folder) keeps everything relative to the program
/// folder (<see cref="AppContext.BaseDirectory"/>), so it runs from any folder — <c>C:\Program Files\Candela</c>, a path
/// with spaces or Cyrillic letters, a USB stick:
/// <code>
/// candela-app.exe, candela.exe         the editor and the command-line tool (also the import worker)
/// data/specs/fixtures_index.json       researched fixture data (copy of _specs/fixtures_index.json)
/// samples/Фокина.mvr                   the sample scene
/// samples/fixtures/*.gdtf              starter fixtures: the GDTF files the sample scene uses
/// docs/                                the documentation (Help → Documentation)
/// </code>
/// <para><b>Lookup order</b> (the first hit wins):</para>
/// <list type="bullet">
/// <item>Fixture library folders: the settings (<c>AppSettings.LibraryFolders</c>) or the CLI's <c>--library</c>; when
/// none is set and the program is not installed, the repository root (its 1091 GDTF files); then always the starter
/// fixtures (<see cref="StarterFixturesFolder"/>) with the lowest priority — a library file of the same name wins.</item>
/// <item>Researched specs (<c>fixtures_index.json</c>): the settings (<c>AppSettings.SpecsIndexPath</c>);
/// <c>_specs/fixtures_index.json</c> in or above a library folder; <c>{program}/data/specs/fixtures_index.json</c>;
/// <c>{repository}/_specs/fixtures_index.json</c>.</item>
/// <item>Sample scene: <c>{program}/samples/Фокина.mvr</c>, then <c>{repository}/stages/Фокина.mvr</c>.</item>
/// <item>Documentation: <c>{program}/docs</c>, then <c>{repository}/docs</c>.</item>
/// <item>Import worker: see <see cref="ImportWorker.FindWorker"/> (<c>candela.exe</c> next to the editor).</item>
/// </list>
/// Fonts and shaders are embedded in the assemblies; the generic cabling devices are generated into the cache folder
/// (<see cref="GenericDeviceLibrary"/>); settings, caches, autosaves and logs live in the per-user folders of
/// <see cref="Core.Settings.ConfigPaths"/>, never in the program folder (which is read-only under Program Files).
/// </summary>
/// <param name="ProgramDirectory">The folder of the running program (normally <see cref="AppContext.BaseDirectory"/>).</param>
/// <param name="RepositoryRoot">The repository (development layout), or null when running from an installed copy.</param>
public sealed record InstallLayout(string ProgramDirectory, string? RepositoryRoot)
{
    /// <summary>Researched fixture data, relative to the program folder.</summary>
    public const string SpecsRelativePath = "data/specs/fixtures_index.json";

    /// <summary>Folder of the sample scenes, relative to the program folder.</summary>
    public const string SamplesFolderName = "samples";

    /// <summary>Folder of the starter fixtures (the GDTF files of the sample scene), relative to the program folder.</summary>
    public const string StarterFixturesRelativePath = "samples/fixtures";

    /// <summary>File name of the sample scene (a real MVR export of a theatre rig with 91 fixtures).</summary>
    public const string SampleSceneFileName = "Фокина.mvr";

    /// <summary>Folder of the documentation, relative to the program folder or the repository.</summary>
    public const string DocsFolderName = "docs";

    /// <summary>Folder of the sample scenes in the repository.</summary>
    public const string RepositoryStagesFolderName = "stages";

    /// <summary>Name of the user's default fixture library folder inside <c>Documents\Candela</c>.</summary>
    public const string DefaultLibraryFolderName = "Fixture Library";

    private static readonly Lazy<InstallLayout> s_current = new(Detect);

    /// <summary>The layout of the running program (detected once).</summary>
    public static InstallLayout Current => s_current.Value;

    /// <summary>
    /// True when the program runs from an installed (or published) folder: the researched specs ship next to it. An
    /// installed copy never falls back to a repository found from the current directory.
    /// </summary>
    public bool IsInstalled => File.Exists(Path.Combine(ProgramDirectory, SpecsRelativePath));

    /// <summary><c>{program}/data/specs/fixtures_index.json</c> when it exists, else null.</summary>
    public string? InstalledSpecsIndex => ExistingFile(Path.Combine(ProgramDirectory, SpecsRelativePath));

    /// <summary>The starter fixtures folder (<c>{program}/samples/fixtures</c>) when it holds GDTF files, else null.</summary>
    public string? StarterFixturesFolder
    {
        get
        {
            string folder = Path.Combine(ProgramDirectory, StarterFixturesRelativePath);
            return CountGdtfFiles(folder) > 0 ? folder : null;
        }
    }

    /// <summary>The sample scene (installed copy first, then the repository's <c>stages</c> folder), or null.</summary>
    public string? SampleScene =>
        ExistingFile(Path.Combine(ProgramDirectory, SamplesFolderName, SampleSceneFileName))
        ?? (RepositoryRoot is { } root ? ExistingFile(Path.Combine(root, RepositoryStagesFolderName, SampleSceneFileName)) : null);

    /// <summary>The documentation folder (installed copy first, then the repository), or null.</summary>
    public string? DocsFolder =>
        ExistingFolder(Path.Combine(ProgramDirectory, DocsFolderName))
        ?? (RepositoryRoot is { } root ? ExistingFolder(Path.Combine(root, DocsFolderName)) : null);

    /// <summary>
    /// The user's default fixture library folder: <c>Documents\Candela\Fixture Library</c> (on Linux and macOS
    /// <c>~/Documents/Candela/Fixture Library</c>). Not created here.
    /// </summary>
    public static string DefaultUserLibraryFolder
    {
        get
        {
            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            // .NET answers $HOME for "My Documents" on Unix systems without an XDG documents folder.
            if (string.IsNullOrEmpty(documents) || (!OperatingSystem.IsWindows() && home.Length > 0 && SamePath(documents, home)))
                documents = Path.Combine(home, "Documents");
            return Path.Combine(documents, "Candela", DefaultLibraryFolderName);
        }
    }

    /// <summary>
    /// The library folders an engine scans for the configured folders <paramref name="configured"/> (settings or
    /// <c>--library</c>): the configured ones; when none and the program is not installed, the repository; then the
    /// starter fixtures (lowest priority, so a library file of the same name wins).
    /// </summary>
    public IReadOnlyList<string> LibraryFoldersFor(IEnumerable<string>? configured)
    {
        var folders = (configured ?? Array.Empty<string>()).Where(f => !string.IsNullOrWhiteSpace(f)).ToList();
        if (folders.Count == 0 && RepositoryRoot is { } root)
            folders.Add(root);
        if (StarterFixturesFolder is { } starter && !folders.Any(f => SamePath(f, starter)))
            folders.Add(starter);
        return folders;
    }

    /// <summary>
    /// The researched specs for <paramref name="libraryFolders"/>: <c>_specs/fixtures_index.json</c> in or above one of
    /// them (in order), else the installed copy, else the repository's; null when there is none.
    /// </summary>
    public string? FindSpecsIndex(IEnumerable<string> libraryFolders)
    {
        ArgumentNullException.ThrowIfNull(libraryFolders);
        foreach (var folder in libraryFolders)
        {
            if (string.IsNullOrWhiteSpace(folder))
                continue;
            try
            {
                for (var dir = new DirectoryInfo(Path.GetFullPath(folder)); dir is not null; dir = dir.Parent)
                {
                    string candidate = Path.Combine(dir.FullName, FixtureSpecsIndex.DefaultRelativePath);
                    if (File.Exists(candidate))
                        return candidate;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException
                or System.Security.SecurityException)
            {
                // An unreadable or malformed folder only ends the search above it.
            }
        }

        return InstalledSpecsIndex
            ?? (RepositoryRoot is { } root ? ExistingFile(Path.Combine(root, FixtureSpecsIndex.DefaultRelativePath)) : null);
    }

    /// <summary>
    /// Number of <c>.gdtf</c> files directly in <paramref name="folder"/> (the library does not read subfolders), at most
    /// <paramref name="limit"/>; 0 for a missing or unreadable folder. Never throws.
    /// </summary>
    public static int CountGdtfFiles(string? folder, int limit = int.MaxValue)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return 0;
        try
        {
            if (!Directory.Exists(folder))
                return 0;
            int count = 0;
            foreach (var _ in Directory.EnumerateFiles(folder, "*.gdtf", GdtfEnumeration))
            {
                if (++count >= limit)
                    break;
            }

            return count;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException
            or System.Security.SecurityException)
        {
            return 0;
        }
    }

    /// <summary>True when one of <paramref name="folders"/> holds at least one <c>.gdtf</c> file.</summary>
    public static bool AnyGdtfFiles(IEnumerable<string>? folders) =>
        folders is not null && folders.Any(f => CountGdtfFiles(f, 1) > 0);

    /// <summary>
    /// True when <paramref name="path"/> is the program folder or inside it (the installed sample scene, the starter
    /// fixtures): a place to read from, not to save into — under <c>C:\Program Files</c> it is read-only for the user,
    /// and an upgrade or uninstall replaces it. False for a malformed path. Never throws.
    /// </summary>
    public bool IsInProgramFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        try
        {
            string program = Path.TrimEndingDirectorySeparator(Path.GetFullPath(ProgramDirectory));
            string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            return string.Equals(full, program, comparison)
                || (full.Length > program.Length && full.StartsWith(program, comparison)
                    && (full[program.Length] == Path.DirectorySeparatorChar || full[program.Length] == Path.AltDirectorySeparatorChar));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>Compares two folder paths after normalising them (case-insensitively on Windows and macOS).</summary>
    public static bool SamePath(string a, string b)
    {
        try
        {
            string x = Path.TrimEndingDirectorySeparator(Path.GetFullPath(a));
            string y = Path.TrimEndingDirectorySeparator(Path.GetFullPath(b));
            return string.Equals(x, y, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            return string.Equals(a, b, StringComparison.Ordinal);
        }
    }

    private static readonly EnumerationOptions GdtfEnumeration = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        MatchCasing = MatchCasing.CaseInsensitive,
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
    };

    private static InstallLayout Detect()
    {
        string program = AppContext.BaseDirectory;
        var probe = new InstallLayout(program, null);
        return probe.IsInstalled ? probe : probe with { RepositoryRoot = DevelopmentLibrary.FindRepositoryRoot() };
    }

    private static string? ExistingFile(string path) => File.Exists(path) ? path : null;

    private static string? ExistingFolder(string path) => Directory.Exists(path) ? path : null;
}
