using Candela.App.Diagnostics;
using Candela.App.Settings;
using Candela.App.Ui;
using Candela.Engine;
using Candela.Gdtf;

namespace Candela.App.Session;

/// <summary>What a folder holds for the fixture library.</summary>
/// <param name="Folder">The folder (full path).</param>
/// <param name="Exists">False when it does not exist (or cannot be read).</param>
/// <param name="Direct">GDTF files directly in the folder (the ones the library reads).</param>
/// <param name="InSubfolders">GDTF files in its subfolders (not read by the library).</param>
/// <param name="Complete">False when the subfolder count was cut short (a very large tree, or it took too long).</param>
internal readonly record struct FolderProbe(string Folder, bool Exists, int Direct, int InSubfolders, bool Complete);

/// <summary>
/// The logic of the fixture library setup that an installed editor offers at its first start (requirement: the 1.8 GB
/// GDTF library is not part of the installer): whether to open the window at start-up, the folder the user chose — the
/// default <c>Documents\Candela\Fixture Library</c> is created on request —, adding it to the settings and to the running
/// engine (its fixtures appear in the Library panel at once), and counting the GDTF files of a folder on a background
/// task so a slow network share never freezes the window. Main thread only, except the counting.
/// </summary>
internal sealed class LibrarySetup : IDisposable
{
    /// <summary>Longest time a folder count may take before it reports what it found so far.</summary>
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(8);

    /// <summary>Most directory entries a count visits in subfolders.</summary>
    public const int ProbeEntryLimit = 200_000;

    private readonly EditorSession _session;
    private readonly SettingsStore _settings;
    private readonly AppLog _log;
    private CancellationTokenSource? _probeCancel;
    private Task<FolderProbe>? _probe;

    /// <summary>Creates the setup over the session's engine and the settings.</summary>
    public LibrarySetup(EditorSession session, SettingsStore settings, AppLog log)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        // Counted only when the window shows it (EnsureProbe): most starts never open the window, and the count walks
        // the folder's subfolders (a network share, a whole drive) in the background.
        Folder = FirstConfiguredFolder(settings.Settings.LibraryFolders);
    }

    /// <summary>The folder shown in the window: the one chosen, else the first one of the settings; null for none.</summary>
    public string? Folder { get; private set; }

    /// <summary>The count of <see cref="Folder"/> once it is known.</summary>
    public FolderProbe? Probe { get; private set; }

    /// <summary>True while <see cref="Folder"/> is being counted.</summary>
    public bool Probing => _probe is { IsCompleted: false };

    /// <summary>The reason the last folder could not be used, or null.</summary>
    public string? Error { get; private set; }

    /// <summary>The engine's summary of the last folder added (status bar), or null.</summary>
    public string? Added { get; private set; }

    /// <summary>The installed sample scene (or the repository's during development), or null.</summary>
    public string? SampleScene => _session.Layout.SampleScene;

    /// <summary>
    /// True when one of <paramref name="configured"/> (the settings' library folders) has brought a fixture into
    /// <paramref name="library"/>: the user has a library. Uses the scan the engine already made (no disk access).
    /// </summary>
    public static bool HasUserLibrary(IReadOnlyList<string> configured, FixtureLibrary library)
    {
        ArgumentNullException.ThrowIfNull(configured);
        ArgumentNullException.ThrowIfNull(library);
        var folders = configured.Where(f => !string.IsNullOrWhiteSpace(f)).Select(Full).Where(f => f.Length > 0).ToList();
        if (folders.Count == 0)
            return false;
        foreach (var entry in library.Entries)
        {
            string? folder = Path.GetDirectoryName(entry.FilePath);
            if (folder is not null && folders.Any(f => InstallLayout.SamePath(f, folder)))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Whether the window opens at start-up: always when <paramref name="forced"/> (<c>--library-setup</c>); never in a
    /// headless run, during development (the repository is the library) or when a file was opened from the command
    /// line (a double-clicked project comes first); otherwise while the user has no library and has not switched it off.
    /// </summary>
    public static bool ShouldOpenAtStart(bool forced, bool headless, bool openingFile, bool showPreference, InstallLayout layout,
        IReadOnlyList<string> configured, FixtureLibrary library)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (forced)
            return true;
        if (headless || openingFile || !showPreference || layout.RepositoryRoot is not null)
            return false;
        return !HasUserLibrary(configured, library);
    }

    /// <summary>
    /// Counts the GDTF files of <paramref name="folder"/>: directly in it, and in its subfolders (at most
    /// <see cref="ProbeEntryLimit"/> entries, stopping at <paramref name="cancel"/>). Never throws.
    /// </summary>
    public static FolderProbe ProbeFolder(string folder, CancellationToken cancel)
    {
        string full = Full(folder);
        if (full.Length == 0 || !SafeExists(full))
            return new FolderProbe(full.Length == 0 ? folder : full, false, 0, 0, true);
        int direct = InstallLayout.CountGdtfFiles(full);
        int nested = 0;
        var visited = new System.Runtime.CompilerServices.StrongBox<int>(0);
        bool complete = true;
        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
            };
            // Every entry counts towards the limit and the cancellation also stops the descent, so a huge tree (a whole
            // drive) or a slow share ends the count instead of keeping the task busy.
            var files = new System.IO.Enumeration.FileSystemEnumerable<string>(full,
                (ref System.IO.Enumeration.FileSystemEntry entry) => entry.ToFullPath(), options)
            {
                ShouldIncludePredicate = (ref System.IO.Enumeration.FileSystemEntry entry) =>
                    ++visited.Value <= ProbeEntryLimit && !entry.IsDirectory
                    && entry.FileName.EndsWith(".gdtf", StringComparison.OrdinalIgnoreCase),
                ShouldRecursePredicate = (ref System.IO.Enumeration.FileSystemEntry entry) =>
                    !cancel.IsCancellationRequested && visited.Value <= ProbeEntryLimit,
            };
            foreach (var path in files)
            {
                string? parent = Path.GetDirectoryName(path);
                if (parent is not null && !InstallLayout.SamePath(parent, full))
                    nested++;
            }

            complete = !cancel.IsCancellationRequested && visited.Value <= ProbeEntryLimit;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        {
            complete = false;
        }

        return new FolderProbe(full, true, direct, nested, complete);
    }

    /// <summary>
    /// Starts counting <see cref="Folder"/> when it has not been counted yet (the window opened); does nothing while a
    /// count runs or when there is no folder.
    /// </summary>
    public void EnsureProbe()
    {
        if (Folder is { } folder && Probe is null && _probe is null)
            StartProbe(folder);
    }

    /// <summary>
    /// Reads <see cref="Folder"/> again — the GDTF files copied into it since (in the file manager) reach the library and
    /// the count is renewed; the window's Rescan button. False (with <see cref="Error"/>) when there is no folder or it
    /// cannot be used.
    /// </summary>
    public bool Rescan() => Folder is { } folder ? UseFolder(folder) : false;

    /// <summary>Picks up a finished count (call once per frame).</summary>
    public void Update()
    {
        if (_probe is not { IsCompleted: true } task)
            return;
        _probe = null;
        if (task.IsCompletedSuccessfully && Folder is { } folder && InstallLayout.SamePath(task.Result.Folder, folder))
            Probe = task.Result;
    }

    /// <summary>
    /// Makes <paramref name="folder"/> the library folder: it goes first in the settings' library folders (saved), the
    /// engine reads its GDTF files at once and the count starts. False (with <see cref="Error"/>) when the folder does not
    /// exist or cannot be used.
    /// </summary>
    public bool UseFolder(string folder)
    {
        Error = null;
        Added = null;
        string full = Full(folder);
        if (full.Length == 0 || !SafeExists(full))
        {
            Error = InstallLoc.Format(InstallStr.SetupFolderFailed, folder, InstallLoc.Get(InstallStr.SetupMissing));
            return false;
        }

        var folders = _settings.Settings.LibraryFolders;
        folders.RemoveAll(f => InstallLayout.SamePath(f, full));
        folders.Insert(0, full);
        _settings.SaveNow();
        Folder = full;
        Probe = null;
        StartProbe(full);
        if (_session.Engine is { } engine)
        {
            var result = engine.AddLibraryFolder(full);
            Added = result.Summary;
            _log.Info("Library", result.Summary);
            if (!result.Success)
                Error = InstallLoc.Format(InstallStr.SetupFolderFailed, full, result.Summary);
        }

        return Error is null;
    }

    /// <summary>
    /// Creates <see cref="InstallLayout.DefaultUserLibraryFolder"/> (when missing) and uses it (<see cref="UseFolder"/>).
    /// </summary>
    public bool UseDefaultFolder() => UseDefaultFolder(InstallLayout.DefaultUserLibraryFolder);

    /// <summary>Creates <paramref name="folder"/> (when missing) and uses it; tests pass their own folder.</summary>
    internal bool UseDefaultFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            Error = InstallLoc.Format(InstallStr.SetupFolderFailed, folder, e.Message);
            return false;
        }

        return UseFolder(folder);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _probeCancel?.Cancel();
        _probeCancel?.Dispose();
        _probeCancel = null;
    }

    private void StartProbe(string folder)
    {
        _probeCancel?.Cancel();
        _probeCancel?.Dispose();
        var cancel = new CancellationTokenSource(ProbeTimeout);
        _probeCancel = cancel;
        _probe = Task.Run(() => ProbeFolder(folder, cancel.Token));
    }

    private static string? FirstConfiguredFolder(IReadOnlyList<string> configured) =>
        configured.FirstOrDefault(f => !string.IsNullOrWhiteSpace(f)) is { } first ? Full(first) : null;

    private static string Full(string folder)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            return "";
        }
    }

    private static bool SafeExists(string folder)
    {
        try
        {
            return Directory.Exists(folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }
}
