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
    private long _autosavedVersion = -1;
    private string? _autosavedSettings;

    /// <summary>The autosave session of the open project, or null when autosave is off (see <see cref="EnableAutosave"/>).</summary>
    public AutosaveService? Autosave => _autosave;

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
            or InvalidDataException or ProjectFormatException)
        {
            return Fail(EngineIssueCodes.SaveFailed, $"'{Path.GetFileName(path)}' could not be saved: {e.Message}");
        }
        ProjectPath = saved.Path ?? Path.GetFullPath(path);
        IsModified = false;
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
        _autosaveOptions = options.Interval > TimeSpan.Zero ? options : null;
        StartAutosaveSession();
    }

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
            project = session.Load();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ProjectFormatException)
        {
            return Fail(EngineIssueCodes.OpenFailed, $"The autosave of '{session.SceneName}' could not be read: {e.Message}");
        }
        var result = UseLoadedProject(project, session.OriginalPath, $"Recovered '{session.SceneName ?? "Untitled"}'");
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

    private EngineResult UseLoadedProject(ProjectData project, string? path, string what)
    {
        ResetProgramMessages();
        var issues = new List<Issue>(project.LoadIssues);
        Settings = ReadSection(project, EngineSettings.SectionName, new EngineSettings(), issues).Sanitized();
        View = ReadSection(project, ViewSettings.SectionName, new ViewSettings(), issues);
        ProjectPath = path;
        ReplaceProject(project);
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
        if (paths.Count > 0 && Library.AddFiles(paths) > 0) ReloadFixtureTypes();
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
        _autosavedSettings = null;
        try
        {
            _autosave = new AutosaveService(_project.Id, CaptureAutosave, options);
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

    /// <summary>Autosave capture (main thread): a snapshot when the scene or the engine/view settings changed, else null.</summary>
    private ProjectSnapshot? CaptureAutosave()
    {
        string settings = Core.Serialization.CandelaJson.Serialize(Settings, indented: false) + Core.Serialization.CandelaJson.Serialize(View, indented: false);
        if (_scene.Version == _autosavedVersion && settings == _autosavedSettings) return null;
        _autosavedVersion = _scene.Version;
        _autosavedSettings = settings;
        WriteSettingsSections();
        return ProjectSnapshot.Capture(_project, SaveOptions());
    }
}
