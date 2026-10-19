using Candela.Assets;
using Candela.Core.Diagnostics;
using Candela.Core.SceneGraph.Components;
using Candela.Project;

namespace Candela.Engine;

public sealed partial class StageEngine
{
    /// <summary>File extension of Candela projects.</summary>
    public const string ProjectExtension = ProjectFile.Extension;

    private AutosaveService? _autosave;
    private AutosaveOptions? _autosaveOptions;
    private AutosaveOptions? _autosaveTemplate;
    private bool _autosaveEnabled = true;
    private RecoverableSession? _recovered;
    private long _autosavedVersion = -1;
    private long _assetEdits;
    private long _autosavedAssetEdits = -1;
    private string? _autosavedSettings;
    private long _autosavedSettingsVersion = -1;

    /// <summary>The autosave session of the open project, or null when autosave is off (see <see cref="EnableAutosave"/>).</summary>
    public AutosaveService? Autosave => _autosave;

    /// <summary>True when autosave is set up (<see cref="EnableAutosave"/>) and not paused by <see cref="ConfigureAutosave"/>.</summary>
    public bool IsAutosaveEnabled => _autosaveOptions is not null && _autosaveEnabled;

    /// <summary>The autosave interval in use (or last used), or null when autosave was never set up.</summary>
    public TimeSpan? AutosaveInterval => _autosaveOptions?.Interval ?? _autosaveTemplate?.Interval;

    /// <summary>
    /// Saves the project to <see cref="ProjectPath"/>. Fails (with an issue) when the scene has never been saved: the
    /// caller asks for a file name and uses <see cref="SaveAs"/>.
    /// </summary>
    public EngineResult Save()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return ProjectPath is { } path
            ? SaveAs(path)
            : Fail(EngineIssueCodes.SaveFailed, "The scene has not been saved yet; choose a file name (Save as).");
    }

    /// <summary>
    /// Writes the project as a <c>.candela</c> archive (atomically: a failure leaves an existing file untouched) with the
    /// scene, every asset with its original bytes, copies of the GDTF files the fixtures use (from the library, or the
    /// project's own copies), the thumbnail and the <c>engine</c> and <c>view</c> settings sections. The file becomes
    /// <see cref="ProjectPath"/>.
    /// </summary>
    public EngineResult SaveAs(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(path)) return Fail(EngineIssueCodes.SaveFailed, "No file name was given.");
        WriteSettingsSections();
        ProjectSaveResult saved;
        try
        {
            saved = ProjectFile.Save(path, _project, SaveOptions());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException
            or InvalidDataException or ProjectFormatException or System.Text.Json.JsonException)
        {
            // data that cannot be written (e.g. kept JSON of a newer version the writer refuses) fails the save, not the editor
            return Fail(EngineIssueCodes.SaveFailed, $"'{Path.GetFileName(path)}' could not be saved: {e.Message}");
        }
        ProjectPath = saved.Path ?? Path.GetFullPath(path);
        IsModified = false;
        if (_autosave is not null)
        {
            // the saved file now holds everything the autosaves hold: they are discarded and tracking restarts
            // (a recovered session is continued by this service, so its crash data goes too)
            _autosavedVersion = _scene.Version;
            _autosavedAssetEdits = _assetEdits;
            _autosavedSettings = AutosaveSettingsKey();
            _autosavedSettingsVersion = SettingsVersion;
            _ = _autosave.MarkSaved();
        }
        else if (_recovered is not null)
        {
            // recovered and saved without autosave: the crash data must not be offered again
            try
            {
                _recovered.Delete();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // offered again at the next start, where the user can discard it
            }
        }
        _recovered = null;
        var issues = Sorted(saved.Issues);
        Report(issues);
        return new EngineResult(true, $"Saved '{Path.GetFileName(path)}' ({saved.StoredGdtfs.Count} GDTF files, {Assets.Count} assets, " +
            $"{saved.BytesWritten / 1024.0:0} KiB){Counts(issues)}.", issues, Array.Empty<Guid>());
    }

    /// <summary>
    /// Starts autosaving the open project (and every project opened later) into <see cref="AutosaveOptions.Directory"/> at
    /// <see cref="AutosaveOptions.Interval"/>; <see cref="Tick"/> drives it. A non-positive interval turns autosave off.
    /// </summary>
    public void EnableAutosave(AutosaveOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ObjectDisposedException.ThrowIf(_disposed, this);
        CloseAutosaveSession(complete: false);
        _autosaveTemplate = options;
        _autosaveEnabled = true;
        _autosaveOptions = options.Interval > TimeSpan.Zero ? options : null;
        StartAutosaveSession();
    }

    /// <summary>
    /// Changes the autosave interval and pauses or resumes autosave <b>without</b> ending the running session: its files,
    /// lock and <see cref="AutosaveService.SessionKey"/> stay, so changing the setting never leaves a session behind that
    /// would be offered as a crash at the next start. Projects opened later use the same values. When autosave was never
    /// set up (or was turned off with <see cref="DisableAutosave"/>), enabling it starts a session with the options last
    /// given to <see cref="EnableAutosave"/> (else the defaults). A non-positive <paramref name="interval"/> pauses
    /// autosave and keeps the previous interval.
    /// </summary>
    /// <param name="enabled">False pauses autosave (the session stays open; explicit saves still work).</param>
    /// <param name="interval">Time between autosaves.</param>
    public void ConfigureAutosave(bool enabled, TimeSpan interval)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        bool validInterval = interval > TimeSpan.Zero;
        if (!validInterval) enabled = false;
        if (_autosaveOptions is null)
        {
            var template = _autosaveTemplate ?? AutosaveOptions.Default;
            // a session started here is a new one: the engine resumes a recovered session by itself (Recover)
            _autosaveTemplate = CopyOptions(template, validInterval ? interval : template.Interval, resume: null);
            if (!enabled || _autosaveTemplate.Interval <= TimeSpan.Zero) return;
            _autosaveEnabled = true;
            _autosaveOptions = _autosaveTemplate;
            StartAutosaveSession();
            return;
        }
        if (validInterval && interval != _autosaveOptions.Interval)
        {
            _autosaveOptions = CopyOptions(_autosaveOptions, interval, _autosaveOptions.Resume);
            _autosaveTemplate = _autosaveOptions;
            if (_autosave is not null) _autosave.Interval = interval;
        }
        _autosaveEnabled = enabled;
        if (_autosave is not null) _autosave.Enabled = enabled;
    }

    private static AutosaveOptions CopyOptions(AutosaveOptions options, TimeSpan interval, RecoverableSession? resume) => new()
    {
        Directory = options.Directory,
        Interval = interval,
        KeepCount = options.KeepCount,
        CompressionLevel = options.CompressionLevel,
        TimeProvider = options.TimeProvider,
        Resume = resume,
    };

    /// <summary>
    /// Stops autosaving. <paramref name="completeSession"/> (a clean exit or close) deletes the session's recovery files;
    /// false keeps them.
    /// </summary>
    public void DisableAutosave(bool completeSession = true)
    {
        CloseAutosaveSession(completeSession);
        _autosaveOptions = null;
    }

    /// <summary>
    /// Opens the newest readable autosave of a session that did not end cleanly (see
    /// <see cref="AutosaveService.FindRecoverable"/>); saving writes back to the project's original file.
    /// </summary>
    public EngineResult Recover(RecoverableSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        ObjectDisposedException.ThrowIf(_disposed, this);
        ProjectData project;
        try
        {
            project = session.Load(new ProjectLoadOptions { Materials = MaterialLibrary.Default });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ProjectFormatException)
        {
            return Fail(EngineIssueCodes.OpenFailed, $"The autosave of '{session.SceneName}' could not be read: {e.Message}");
        }
        // the autosave session of the recovered project continues the crashed session's files (AutosaveOptions.Resume),
        // so a save or a clean close removes them instead of leaving them to be offered again
        var result = UseLoadedProject(project, session.OriginalPath, $"Recovered '{session.SceneName ?? "Untitled"}'", session);
        IsModified = true;
        return result;
    }

    private EngineResult OpenProject(string path)
    {
        ProjectData project;
        try
        {
            project = ProjectFile.Load(path, new ProjectLoadOptions { Materials = MaterialLibrary.Default });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ProjectFormatException)
        {
            return Fail(EngineIssueCodes.OpenFailed, $"'{Path.GetFileName(path)}' could not be opened: {e.Message}");
        }
        return UseLoadedProject(project, Path.GetFullPath(path), $"Opened '{Path.GetFileName(path)}'");
    }

    private EngineResult UseLoadedProject(ProjectData project, string? path, string what, RecoverableSession? recovered = null)
    {
        ResetProgramMessages();
        var issues = new List<Issue>(project.LoadIssues);
        Settings = ReadSection(project, EngineSettings.SectionName, new EngineSettings(), issues).Sanitized();
        View = ReadSection(project, ViewSettings.SectionName, new ViewSettings(), issues);
        ProjectPath = path;
        ReplaceProject(project, recovered);
        RegisterProjectGdtfs(project.GdtfFiles.Keys, issues);
        IsModified = false;
        var sorted = Sorted(issues);
        Report(sorted);
        RefreshIssues();
        int fixtures = _scene.Query<FixtureComponent>().Count();
        return new EngineResult(true, $"{what}: {fixtures} fixtures, {Assets.Count} assets{Counts(sorted)}.", sorted,
            _scene.Roots.Select(r => r.Id).ToArray());
    }

    private EngineResult MergeProject(string path, EngineImportOptions options)
    {
        ProjectData source;
        try
        {
            source = ProjectFile.Load(path, new ProjectLoadOptions { Materials = MaterialLibrary.Default });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ProjectFormatException)
        {
            return Fail(EngineIssueCodes.ImportFailed, $"'{Path.GetFileName(path)}' could not be imported: {e.Message}", IssueCategory.Scene);
        }
        var command = ProjectMerge.CreateCommand(_project, source, new ProjectMergeOptions
        {
            GroupName = options.GroupName ?? Path.GetFileNameWithoutExtension(path),
            Placement = options.Placement,
            TargetGdtfProvider = LibraryGdtf,
        });
        Undo.Execute(command);
        var merged = command.Result;
        var issues = new List<Issue>(merged.Issues);
        RegisterProjectGdtfs(merged.AddedGdtfs, issues);
        Selection.Set(merged.Group.Id);
        var sorted = Sorted(issues);
        Report(sorted);
        RefreshIssues();
        int fixtures = merged.Group.SelfAndDescendants().Count(e => e.Has<FixtureComponent>());
        return new EngineResult(true, $"Imported '{Path.GetFileName(path)}': {fixtures} fixtures, {merged.AddedAssets.Count} assets{Counts(sorted)}.",
            sorted, new[] { merged.Group.Id });
    }

    /// <summary>
    /// Offers the project's GDTF copies to the fixture library: names the library does not know are extracted to the
    /// cache and added, so the project's fixtures evaluate on a computer without those files.
    /// </summary>
    private void RegisterProjectGdtfs(IEnumerable<string> names, List<Issue> issues)
    {
        var paths = new List<string>();
        foreach (var name in names)
        {
            if (Library.FindByFileName(name) is not null || _project.FindGdtf(name) is not { } bytes) continue;
            if (ExtractGdtf(name, bytes, issues) is { } file) paths.Add(file);
        }
        AddGdtfsToLibrary(paths, FixtureLibraryChangeSource.Project);
    }

    private ProjectSaveOptions SaveOptions() => new() { GdtfProvider = LibraryGdtf };

    /// <summary>The bytes of a GDTF of the library by file name (for project files and merges), or null.</summary>
    private byte[]? LibraryGdtf(string name)
    {
        var entry = Library.FindByFileName(name);
        if (entry is null) return null;
        try
        {
            return File.ReadAllBytes(entry.FilePath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void WriteSettingsSections()
    {
        _project.Settings.Set(EngineSettings.SectionName, Settings);
        _project.Settings.Set(ViewSettings.SectionName, View);
    }

    private static T ReadSection<T>(ProjectData project, string name, T fallback, List<Issue> issues)
    {
        if (!project.Settings.ContainsKey(name)) return fallback;
        if (project.Settings.TryGet<T>(name, out var value) && value is not null) return value;
        issues.Add(new Issue(IssueSeverity.Warning, IssueCategory.Program, EngineIssueCodes.SettingsInvalid,
            $"The project's '{name}' settings could not be read; the defaults are used."));
        return fallback;
    }

    private void StartAutosaveSession()
    {
        if (_autosaveOptions is not { } options || _disposed) return;
        _autosavedVersion = _scene.Version;
        _autosavedAssetEdits = _assetEdits;
        _autosavedSettings = AutosaveSettingsKey();
        _autosavedSettingsVersion = SettingsVersion;
        if (_recovered is { } resume && (resume.ProjectId == Guid.Empty || resume.ProjectId == _project.Id))
            options = CopyOptions(options, options.Interval, resume);
        try
        {
            _autosave = new AutosaveService(_project.Id, CaptureAutosave, options) { Enabled = _autosaveEnabled };
            _autosave.Failed += e => Issues.Report(new Issue(IssueSeverity.Warning, IssueCategory.Program, EngineIssueCodes.SaveFailed,
                $"Autosave failed: {e.Message}"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Issues.Report(new Issue(IssueSeverity.Warning, IssueCategory.Program, EngineIssueCodes.SaveFailed,
                $"Autosave is not available: {e.Message}"));
        }
    }

    /// <summary>Ends the open project's autosave session; <paramref name="complete"/> deletes its files (closed normally).</summary>
    private void CloseAutosaveSession(bool complete = true)
    {
        if (_autosave is null) return;
        try
        {
            if (complete) _autosave.CompleteSession();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // recovery files that cannot be deleted are offered again at the next start and can be discarded there
        }
        _autosave.Dispose();
        _autosave = null;
    }

    private string AutosaveSettingsKey() =>
        Core.Serialization.CandelaJson.Serialize(Settings, indented: false) + Core.Serialization.CandelaJson.Serialize(View, indented: false);

    /// <summary>
    /// Autosave capture (main thread): a snapshot when the scene, an asset (import options, removal) or the engine/view
    /// settings changed, else null.
    /// </summary>
    private ProjectSnapshot? CaptureAutosave()
    {
        string settings = AutosaveSettingsKey();
        if (_scene.Version == _autosavedVersion && _assetEdits == _autosavedAssetEdits && SettingsVersion == _autosavedSettingsVersion
            && settings == _autosavedSettings) return null;
        _autosavedVersion = _scene.Version;
        _autosavedAssetEdits = _assetEdits;
        _autosavedSettingsVersion = SettingsVersion;
        _autosavedSettings = settings;
        WriteSettingsSections();
        return ProjectSnapshot.Capture(_project, SaveOptions());
    }
}
