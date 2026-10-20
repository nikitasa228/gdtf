using System.Globalization;
using Candela.App.Diagnostics;
using Candela.App.Settings;
using Candela.App.Ui;
using Candela.Core.Diagnostics;
using Candela.Core.Editing;
using Candela.Core.SceneGraph.Components;
using Candela.Engine;
using Candela.Project;
using Candela.Rendering;
using SceneModel = Candela.Core.SceneGraph.Scene;

namespace Candela.App.Session;

/// <summary>How an <see cref="EditorSession"/> sets up its stage engine.</summary>
internal sealed record EditorSessionOptions
{
    /// <summary>Engine options (library folders, caches); null builds them from the user settings.</summary>
    public StageEngineOptions? Engine { get; init; }

    /// <summary>
    /// Diagnostics: the viewports show the renderer's procedural test scene (<see cref="PreviewStage"/>) instead of the
    /// engine's render binding (<c>--preview-stage</c>). The engine still runs; its issue analysis is skipped.
    /// </summary>
    public bool PreviewStage { get; init; }

    /// <summary>Autosave folder; null = <see cref="AutosaveService.DefaultDirectory"/> (in the Candela cache folder).</summary>
    public string? AutosaveDirectory { get; init; }

    /// <summary>Autosave at the interval of the settings (false: never, e.g. tests).</summary>
    public bool Autosave { get; init; } = true;

    /// <summary>Fixture models load on background threads (false: during the frame that needs them; tests).</summary>
    public bool LoadFixtureModelsInBackground { get; init; } = true;
}

/// <summary>
/// The document being edited and the editor's only gateway to the stage engine (<see cref="StageEngine"/>): the scene,
/// selection, undo history and issue log the panels work on are the engine's; File → New / Open / Save / Save As /
/// Import / Export run the engine's operations and turn their <see cref="EngineResult"/> into localised one-line results
/// (the issues go to the issues panel). Each frame <see cref="Update"/> ticks the engine (DMX input, fixture motors,
/// smoke, fire, sound, lights, render binding), re-runs the issue analysis shortly after edits, and applies the autosave
/// interval of the settings. Nothing here throws for content problems: a damaged file is a failed result with a reason.
/// </summary>
internal sealed class EditorSession : IDisposable
{
    /// <summary>
    /// Most distinct program messages kept in the issues panel; an error repeated every frame is reported once, so
    /// a recurring failure cannot flood the panel (each report copies the issue list).
    /// </summary>
    public const int MaxProgramMessages = 200;

    /// <summary>Seconds after the last edit before the issue analysis (patch, power, rigging, assets) runs again.</summary>
    public const double IssueRefreshDelay = 0.5;

    private readonly AppLog _log;
    private readonly SettingsStore _settings;
    private readonly EditorSessionOptions _options;
    private readonly HashSet<(string Code, string Message)> _reported = new();
    private readonly Func<Guid, bool> _entityExists;
    private IViewportContent? _content;
    private long _previewBaseline = -1;
    private long _issuesVersion = -1;
    private long _seenVersion = -1;
    private double _lastChangeAt;
    private double _clock;
    private int _autosaveMinutes = -1;
    private bool _endedCleanly;
    private string? _displaySource;
    private string _displayFileName = "";
    private bool _disposed;

    /// <summary>Creates a session with a new, empty scene and its stage engine.</summary>
    public EditorSession(AppLog log, SettingsStore settings, EditorSessionOptions? options = null)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _options = options ?? new EditorSessionOptions();
        var engineOptions = _options.Engine ?? StageEngineOptions.FromSettings(_settings.Settings);
        Engine = new StageEngine(engineOptions);
        // The cable tool's generic devices (splitters, nodes, dimmer) join the fixture library before any scene opens.
        GenericDeviceLibrary.Register(Engine);
        Engine.New(Loc.Get(Str.Untitled));
        Engine.SceneReplaced += OnEngineSceneReplaced;
        _entityExists = id => Engine.Scene.Find(id) is not null;
        _log.Info("Session", $"Stage engine ready: {Engine.Library.Count} fixtures in the library ({string.Join(", ", Engine.Library.Folders)}).");
    }

    /// <summary>The stage engine (the open project, fixture library, DMX, simulations, render binding); never null.</summary>
    public StageEngine? Engine { get; }

    /// <summary>The scene being edited (main thread only); replaced by New / Open.</summary>
    public SceneModel Scene => EngineOrThrow.Scene;

    /// <summary>The selected entities (Shift+click adds/removes; the last one is the primary selection).</summary>
    public Selection Selection => EngineOrThrow.Selection;

    /// <summary>Undo/redo history of every scene edit.</summary>
    public UndoStack Undo => EngineOrThrow.Undo;

    /// <summary>Errors, warnings and notes about the scene and the program (issues panel).</summary>
    public IssueLog Issues => EngineOrThrow.Issues;

    /// <summary>The project file the scene was opened from / saved to, or null (new scene, opened MVR).</summary>
    public string? FilePath => EngineOrThrow.ProjectPath;

    /// <summary>The file the scene came from: the project file, or the MVR it was opened from; null for a new scene.</summary>
    public string? SourcePath { get; private set; }

    /// <summary>True: the stage engine is part of this build (open/save/import/export work).</summary>
    public bool EngineAvailable => true;

    /// <summary>True in the <c>--preview-stage</c> diagnostics mode.</summary>
    public bool IsPreviewStage => _options.PreviewStage;

    /// <summary>What the viewports show (null until a renderer is attached).</summary>
    public IViewportContent? Content => _content;

    /// <summary>
    /// True while something else drives the engine clock (a render job rendering a timecode range ticks the engine at
    /// the sequence's frame rate): <see cref="Update"/> must then not tick the engine with wall-clock time.
    /// </summary>
    public bool EngineClockHeld { get; set; }

    /// <summary>True when the scene, its assets or its settings changed since it was opened or saved.</summary>
    public bool IsModified => _options.PreviewStage ? Scene.Version != _previewBaseline : EngineOrThrow.IsModified;

    /// <summary>
    /// Name for the title bar and dialogs: the scene's name; for a scene still called "Untitled" the file name, else
    /// "Untitled" in the current language.
    /// </summary>
    public string DisplayName
    {
        get
        {
            string name = Scene.Name;
            if (!IsDefaultName(name))
                return name;
            if (SourcePath is not { Length: > 0 } path)
                return Loc.Get(Str.Untitled);
            // Read every frame by the title bar: the file name is taken once per source file.
            if (!ReferenceEquals(path, _displaySource))
            {
                _displaySource = path;
                _displayFileName = Path.GetFileNameWithoutExtension(path);
            }

            return _displayFileName;
        }
    }

    /// <summary>Smoke, fire and sound are frozen (DMX and moving lights keep running).</summary>
    public bool SimulationPaused
    {
        get => EngineOrThrow.Settings.SimulationPaused;
        set
        {
            if (EngineOrThrow.Settings.SimulationPaused == value)
                return;
            EngineOrThrow.Settings.SimulationPaused = value;
            _log.Info("Session", value ? "Simulation paused." : "Simulation resumed.");
        }
    }

    /// <summary>Raised after the scene was replaced (new, open, recover); panels drop cached entity references.</summary>
    public event Action? SceneReplaced;

    private StageEngine EngineOrThrow => Engine ?? throw new ObjectDisposedException(nameof(EditorSession));

    /// <summary>Connects the renderer: the viewports show the engine's scene (or the preview stage in diagnostics mode).</summary>
    public void AttachRenderer(IRenderer renderer)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        ObjectDisposedException.ThrowIf(_disposed, this);
        _content?.Dispose();
        if (_options.PreviewStage)
        {
            _content = PreviewStage.Create(renderer.World, Scene, Selection);
            Undo.Clear();
            _previewBaseline = Scene.Version;
            _log.Info("Session", "Diagnostics: the viewports show the preview stage (the renderer's test scene).");
            return;
        }

        _content = new EngineViewportContent(EngineOrThrow, renderer, _options.LoadFixtureModelsInBackground);
    }

    /// <summary>
    /// Per-frame update before the UI: autosave interval from the settings, the engine tick (DMX input, fixture motors,
    /// simulations, lights, render binding) and — half a second after the last edit — the issue analysis.
    /// </summary>
    public void Update(double deltaTime)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var engine = EngineOrThrow;
        double dt = double.IsFinite(deltaTime) && deltaTime > 0 ? Math.Min(deltaTime, 1.0) : 0;
        _clock += dt;
        ApplyAutosaveSetting();

        // Deleted entities leave the selection (checked without allocating; Prune only when needed).
        var ids = Selection.Ids;
        for (int i = 0; i < ids.Count; i++)
        {
            if (Scene.Find(ids[i]) is null)
            {
                Selection.Prune(_entityExists);
                break;
            }
        }

        if (!EngineClockHeld) engine.Tick(dt);
        (_content as EngineViewportContent)?.MarkTicked();
        RefreshIssuesWhenSettled();
    }

    /// <summary>Brings this frame's UI edits into the render world (after the UI, before the viewports render).</summary>
    public void SyncRender() => _content?.Sync();

    /// <summary>File → New: an empty scene (the caller has asked about unsaved changes).</summary>
    public SessionResult New()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var result = EngineOrThrow.New(Loc.Get(Str.Untitled));
        SourcePath = null;
        return result.Success ? SessionResult.Done(Loc.Get(Str.MsgNewScene)) : Failed(result);
    }

    /// <summary>
    /// File → Open: a <c>.candela</c> project or an MVR scene replaces the open scene. An MVR without a floor (exports
    /// often hold only the rig) gets the engine's stage floor (<see cref="StageFloor"/>, the floor of the CLI's
    /// <c>--floor auto</c>) as the first step of the undo history, so the light lands on something and Undo removes it.
    /// </summary>
    public SessionResult Open(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!File.Exists(path))
            return SessionResult.Failed(Loc.Format(Str.MsgFileMissing, path));
        var engine = EngineOrThrow;
        var result = engine.Open(path);
        if (!result.Success)
            return Failed(result);
        SourcePath = Path.GetFullPath(path);
        _log.Info("Session", result.Summary);
        string message = Loc.Format(Str.MsgOpened, Path.GetFileName(path), FixtureCount());
        if (engine.ProjectPath is null && AddStageFloorIfMissing() is { } floor)
        {
            var size = StageFloor.SizeOf(floor);
            _log.Info("Session", string.Create(CultureInfo.InvariantCulture, $"Added a {size.X:0.#} × {size.Y:0.#} m stage floor (the scene has none)."));
            message += " · " + Loc.Get(Str.MsgStageFloorAdded);
        }

        return Done(message, result);
    }

    /// <summary>
    /// Adds the engine's stage floor (<see cref="StageFloor.CreateIfMissing"/>) to the scene as one undoable step when
    /// it has no floor, and returns it; null when the scene has a floor or no content.
    /// </summary>
    public Core.SceneGraph.Entity? AddStageFloorIfMissing()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var engine = EngineOrThrow;
        var floor = StageFloor.CreateIfMissing(engine.Scene, engine.Models.GetBounds, Loc.Get(Str.StageFloorName));
        if (floor is not null)
            Undo.Execute(new AddEntityCommand(engine.Scene, floor));
        return floor;
    }

    /// <summary>File → Save (to the current project file; callers ask for a path first when there is none).</summary>
    public SessionResult Save()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (FilePath is not { } path)
            return SessionResult.Failed(Loc.Get(Str.MsgNeedsSaveAs));
        return SaveAs(path);
    }

    /// <summary>File → Save As: writes a <c>.candela</c> project (the extension is added when missing).</summary>
    public SessionResult SaveAs(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!path.EndsWith(StageEngine.ProjectExtension, StringComparison.OrdinalIgnoreCase))
            path += StageEngine.ProjectExtension;
        // A scene saved for the first time takes the file's name instead of "Untitled" (not when the save fails).
        string previousName = Scene.Name;
        if (FilePath is null && IsDefaultName(previousName))
            Scene.Name = Path.GetFileNameWithoutExtension(path);
        var result = EngineOrThrow.SaveAs(path);
        if (!result.Success)
        {
            if (!string.Equals(Scene.Name, previousName, StringComparison.Ordinal))
                Scene.Name = previousName;
            return Failed(result);
        }

        SourcePath = EngineOrThrow.ProjectPath ?? Path.GetFullPath(path);
        _previewBaseline = Scene.Version;
        _log.Info("Session", result.Summary);
        return Done(Loc.Format(Str.MsgSaved, Path.GetFileName(path)), result);
    }

    /// <summary>
    /// File → Import: merges a project or an MVR under a new group, places a 3D model or an image (at
    /// <paramref name="options"/>' placement), or adds a GDTF file to the fixture library. Undoable.
    /// </summary>
    public SessionResult Import(string path, ImportKind kind, EngineImportOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!File.Exists(path))
            return SessionResult.Failed(Loc.Format(Str.MsgFileMissing, path));
        // The engine decides by the file's content type; the kind only chose the file dialog's filter.
        bool gdtf = kind == ImportKind.Gdtf || path.EndsWith(".gdtf", StringComparison.OrdinalIgnoreCase);
        // A file the library already has is selected, not added again (the message must not claim it was added).
        var known = gdtf ? EngineOrThrow.Library.FindByFileName(Path.GetFileName(path)) : null;
        var result = EngineOrThrow.Import(path, options);
        if (!result.Success)
        {
            if (!gdtf)
                return Failed(result);
            // A GDTF the engine refused (unreadable, no DMX mode): its reason, under a line in the UI language.
            _log.Warn("Session", result.Summary);
            return SessionResult.Failed(Loc.Format(Str.LibImportRefused, result.Summary));
        }

        _log.Info("Session", result.Summary);
        if (result.LibraryEntry is { } entry)
        {
            string name = string.IsNullOrWhiteSpace(entry.Manufacturer) ? entry.ModelName : $"{entry.Manufacturer} {entry.ModelName}";
            var message = ReferenceEquals(entry, known) ? Str.LibImportAlreadyThere : Str.LibImportedSelected;
            return Done(Loc.Format(message, name), result) with { LibraryEntry = entry };
        }

        return Done(Loc.Format(Str.MsgImported, Path.GetFileName(path)), result);
    }

    /// <summary>File → Export: MVR 1.6 with the fixtures' GDTF files and the models as GLB.</summary>
    public SessionResult Export(string path, ExportKind kind)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ObjectDisposedException.ThrowIf(_disposed, this);
        _ = kind; // MVR is the only export format
        if (!path.EndsWith(StageEngine.MvrExtension, StringComparison.OrdinalIgnoreCase))
            path += StageEngine.MvrExtension;
        var result = EngineOrThrow.ExportMvr(path);
        if (!result.Success)
            return Failed(result);
        _log.Info("Session", result.Summary);
        return Done(Loc.Format(Str.MsgExported, Path.GetFileName(path)), result);
    }

    /// <summary>Render panel → Render (final, path-traced frame or timecode range).</summary>
    public SessionResult StartFinalRender(FinalRenderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return SessionResult.NotAvailable(Loc.Get(Str.RenderNotAvailable));
    }

    /// <summary>Autosaved sessions of earlier runs that did not end cleanly, newest first (start-up recovery prompt).</summary>
    public IReadOnlyList<RecoverableSession> FindRecoverable()
    {
        try
        {
            return AutosaveService.FindRecoverable(_options.AutosaveDirectory);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _log.Warn("Autosave", $"The autosave folder could not be read: {e.Message}");
            return Array.Empty<RecoverableSession>();
        }
    }

    /// <summary>Opens the newest autosave of a crashed session as the scene (unsaved; saving writes back to its original file).</summary>
    public SessionResult Recover(RecoverableSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var result = EngineOrThrow.Recover(session);
        if (!result.Success)
            return Failed(result);
        SourcePath = session.OriginalPath;
        _log.Info("Session", result.Summary);
        return Done(Loc.Format(Str.MsgRecovered, session.SceneName ?? Loc.Get(Str.Untitled)), result);
    }

    /// <summary>Deletes the autosave files of a crashed session the user does not want back.</summary>
    public SessionResult Discard(RecoverableSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        try
        {
            session.Delete();
            return SessionResult.Done(Loc.Format(Str.MsgRecoveryDiscarded, session.SceneName ?? Loc.Get(Str.Untitled)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return SessionResult.Failed(e.Message);
        }
    }

    /// <summary>
    /// Reports a program error (exception caught by the frame loop, failed panel) in the issues panel — once per code
    /// and message until the messages are cleared, and at most <see cref="MaxProgramMessages"/> different ones.
    /// </summary>
    public void ReportProgramError(string code, string message) =>
        ReportOnce(new Issue(IssueSeverity.Error, IssueCategory.Program, code, message));

    /// <summary>Reports a program note (shown once per message, like errors).</summary>
    public void ReportProgramNote(string code, string message) =>
        ReportOnce(new Issue(IssueSeverity.Note, IssueCategory.Program, code, message));

    /// <summary>
    /// Issues panel → clear: removes the program messages (errors, notes of operations) and shows the scene's own issues
    /// again; the same messages can be reported anew afterwards.
    /// </summary>
    public void ClearProgramMessages()
    {
        _reported.Clear();
        Issues.ClearProgramMessages();
        RefreshIssuesNow();
    }

    /// <summary>Runs the issue analysis now (after a structural edit the user wants to see checked at once).</summary>
    public void RefreshIssuesNow()
    {
        if (_disposed)
            return;
        _issuesVersion = Scene.Version;
        if (_options.PreviewStage)
        {
            // The preview stage's procedural meshes are not project assets: only program messages are listed.
            Issues.Refresh(new SceneModel());
            return;
        }

        EngineOrThrow.RefreshIssues();
    }

    /// <summary>Adds a file to the recent-files list of the settings (after a successful open/save).</summary>
    public void RememberRecentFile(string path)
    {
        _settings.Settings.AddRecentFile(Path.GetFullPath(path));
        _settings.RequestSave(0);
    }

    /// <summary>Removes a file from the recent-files list (it no longer exists).</summary>
    public void ForgetRecentFile(string path)
    {
        var recent = _settings.Settings.RecentFiles;
        recent.RemoveAll(p => string.Equals(p, path, StringComparison.Ordinal));
        _settings.RequestSave(0);
    }

    /// <summary>
    /// Marks the end of the run as clean (the window was closed after the unsaved-changes question was answered):
    /// <see cref="Dispose"/> then deletes the session's recovery files. Without it — the frame loop gave up, an exception
    /// ended the program — they are kept (after a last autosave of unsaved changes) and offered at the next start.
    /// </summary>
    public void EndCleanly() => _endedCleanly = true;

    /// <summary>
    /// Ends the session: autosave stops; after <see cref="EndCleanly"/> the session's recovery files are deleted,
    /// otherwise unsaved changes are autosaved one last time and the files stay for recovery. The render binding and the
    /// engine are released.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try
        {
            _content?.Dispose();
        }
        catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            // The render binding of a failing renderer must not keep the recovery data below from being written.
            _log.Warn("Session", $"The render binding could not be released: {e.Message}");
        }

        _content = null;
        if (Engine is { } engine)
        {
            engine.SceneReplaced -= OnEngineSceneReplaced;
            if (!_endedCleanly)
                SaveRecoveryNow(engine);
            try
            {
                engine.DisableAutosave(completeSession: _endedCleanly);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _log.Warn("Autosave", $"The autosave files could not be removed: {e.Message}");
            }

            engine.Dispose();
        }
    }

    /// <summary>Number of fixtures in the scene.</summary>
    internal int FixtureCount()
    {
        int n = 0;
        foreach (var _ in Scene.Query<FixtureComponent>())
            n++;
        return n;
    }

    private static bool IsDefaultName(string name) =>
        string.IsNullOrWhiteSpace(name) || name == "Untitled" || name == Loc.English_(Str.Untitled) ||
        Loc.Russian_(Str.Untitled) is { } ru && name == ru;

    private void OnEngineSceneReplaced(SceneModel scene)
    {
        _ = scene;
        _reported.Clear();
        _issuesVersion = -1;
        SceneReplaced?.Invoke();
    }

    /// <summary>
    /// Follows the autosave interval of the settings. The engine's autosave is set up once, with this session's folder
    /// (<see cref="StageEngine.EnableAutosave"/>); later changes of the interval, or turning autosave off and on, go to
    /// <see cref="StageEngine.ConfigureAutosave"/>, which adjusts the running session instead of starting a new one (that
    /// would leave the old session's files behind to be offered as a crashed session at the next start) and carries the
    /// values over to projects opened later. Allocation-free while nothing changes.
    /// </summary>
    private void ApplyAutosaveSetting()
    {
        int minutes = _options.Autosave && !_options.PreviewStage ? Math.Clamp(_settings.Settings.AutosaveMinutes, 0, 24 * 60) : 0;
        if (minutes == _autosaveMinutes)
            return;
        bool first = _autosaveMinutes < 0;
        bool wasOn = _autosaveMinutes > 0;
        _autosaveMinutes = minutes;
        var engine = EngineOrThrow;
        try
        {
            if (engine.AutosaveInterval is null)
            {
                // Never set up: the first session needs this editor's folder (ConfigureAutosave would use the defaults).
                if (minutes > 0)
                {
                    engine.EnableAutosave(new AutosaveOptions
                    {
                        Directory = _options.AutosaveDirectory,
                        Interval = TimeSpan.FromMinutes(minutes),
                    });
                }
            }
            else
            {
                // Off keeps the session (and its interval) paused; on resumes it with the new interval.
                engine.ConfigureAutosave(minutes > 0, minutes > 0 ? TimeSpan.FromMinutes(minutes) : TimeSpan.Zero);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _log.Warn("Autosave", $"Autosave could not be set up: {e.Message}");
        }

        if (minutes > 0)
            _log.Info("Autosave", string.Create(CultureInfo.InvariantCulture, $"Autosave every {minutes} min."));
        else if (first || wasOn)
            _log.Info("Autosave", "Autosave is off.");
    }

    /// <summary>
    /// The run is ending without a clean exit: unsaved changes are autosaved now (waiting a few seconds for the write),
    /// so the recovery offered at the next start holds the latest state rather than the one of the last interval.
    /// </summary>
    private void SaveRecoveryNow(StageEngine engine)
    {
        if (engine.Autosave is not { } service || !engine.IsModified)
            return;
        try
        {
            if (!service.SaveNowAsync().Wait(TimeSpan.FromSeconds(10)))
                _log.Warn("Autosave", "The last autosave did not finish in time; the previous one is kept for recovery.");
            else
                _log.Info("Autosave", $"Unsaved changes were autosaved for recovery ({service.FilePath}).");
        }
        catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            _log.Warn("Autosave", $"The last autosave failed: {e.Message}");
        }
    }

    /// <summary>Runs the issue analysis once the scene has not changed for <see cref="IssueRefreshDelay"/> seconds.</summary>
    private void RefreshIssuesWhenSettled()
    {
        long version = Scene.Version;
        if (version != _seenVersion)
        {
            // A gizmo drag changes the scene every frame: the analysis waits until the edit has settled.
            _seenVersion = version;
            _lastChangeAt = _clock;
        }

        if (version != _issuesVersion && _clock - _lastChangeAt >= IssueRefreshDelay)
            RefreshIssuesNow();
    }

    private SessionResult Done(string message, EngineResult result)
    {
        int errors = 0, warnings = 0;
        foreach (var issue in result.Issues)
        {
            if (issue.Severity == IssueSeverity.Error)
                errors++;
            else if (issue.Severity == IssueSeverity.Warning)
                warnings++;
        }

        if (errors > 0 || warnings > 0)
            message += " · " + Loc.Format(Str.MsgIssueCounts, errors, warnings);
        return SessionResult.Done(message) with { Errors = errors, Warnings = warnings, Entities = result.Entities };
    }

    private SessionResult Failed(EngineResult result)
    {
        _log.Warn("Session", result.Summary);
        return SessionResult.Failed(result.Summary);
    }

    private void ReportOnce(Issue issue)
    {
        if (_reported.Count >= MaxProgramMessages || !_reported.Add((issue.Code, issue.Message)))
            return;
        Issues.Report(issue);
    }
}
