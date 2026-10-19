using Candela.App.Diagnostics;
using Candela.App.Settings;
using Candela.App.Ui;
using Candela.Core.Diagnostics;
using Candela.Core.Editing;
using Candela.Rendering;
using SceneModel = Candela.Core.SceneGraph.Scene;

namespace Candela.App.Session;

/// <summary>
/// The document being edited and every operation that needs the stage engine — the one place the editor talks to it.
/// Holds the scene, selection, undo history and issue log the panels work on, the current file and its modified
/// state, and routes File → New / Open / Save / Save As / Import / Export / Render. In this build the stage engine
/// (<c>Candela.Engine.StageEngine</c>) is not available yet, so those operations return
/// <see cref="SessionOutcome.NotAvailable"/> with a clear message and change nothing, and the viewports show the
/// <see cref="PreviewStage"/>. The next build phase fills these methods with the engine calls (open/save through
/// <c>Candela.Project</c>, MVR import/export, autosave, the engine tick and its render synchronisation).
/// </summary>
internal sealed class EditorSession : IDisposable
{
    private readonly AppLog _log;
    private readonly SettingsStore _settings;
    private IViewportContent? _content;
    private long _savedVersion;
    private bool _disposed;

    /// <summary>Creates a session with an empty scene.</summary>
    public EditorSession(AppLog log, SettingsStore settings)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        Scene = new SceneModel(Loc.Get(Str.Untitled));
        Selection = new Selection();
        Undo = new UndoStack();
        Issues = new IssueLog();
        _savedVersion = Scene.Version;
        _entityExists = id => Scene.Find(id) is not null;
    }

    private readonly Func<Guid, bool> _entityExists;
    private readonly HashSet<(string Code, string Message)> _reported = new();

    /// <summary>
    /// Most distinct program messages kept in the issues panel; an error repeated every frame is reported once, so
    /// a recurring failure cannot flood the panel (each report copies the issue list).
    /// </summary>
    public const int MaxProgramMessages = 200;

    /// <summary>The scene being edited (main thread only).</summary>
    public SceneModel Scene { get; private set; }

    /// <summary>The selected entities (Shift+click adds/removes; the last one is the primary selection).</summary>
    public Selection Selection { get; }

    /// <summary>Undo/redo history of every scene edit.</summary>
    public UndoStack Undo { get; }

    /// <summary>Errors, warnings and notes about the scene and the program (issues panel).</summary>
    public IssueLog Issues { get; }

    /// <summary>The file the scene was opened from / saved to, or null for a new scene.</summary>
    public string? FilePath { get; private set; }

    /// <summary>True when the stage engine is available (open/save/import/export work).</summary>
    public bool EngineAvailable => false;

    /// <summary>What the viewports show (null until a renderer is attached).</summary>
    public IViewportContent? Content => _content;

    /// <summary>The stage engine of the open scene, or null while the editor runs without one (engine-backed panels then show an empty state).</summary>
    public Candela.Engine.StageEngine? Engine { get; private set; }

    /// <summary>True when the scene changed since it was opened or saved.</summary>
    public bool IsModified => Scene.Version != _savedVersion;

    /// <summary>Name for the title bar: the file name, or the scene name for a new scene.</summary>
    public string DisplayName => FilePath is { Length: > 0 } path ? Path.GetFileNameWithoutExtension(path) : Scene.Name;

    /// <summary>Raised when the scene object was replaced (new/open); panels drop cached entity references.</summary>
    public event Action? SceneReplaced;

    /// <summary>Connects the renderer: builds what the viewports show.</summary>
    public void AttachRenderer(IRenderer renderer)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        _content?.Dispose();
        _content = PreviewStage.Create(renderer.World, Scene, Selection);
        // Building the preview is not a user edit.
        _savedVersion = Scene.Version;
        Undo.Clear();
        _log.Info("Session", "The viewports show the preview stage (the renderer's test scene); the stage engine arrives in the next build phase.");
    }

    /// <summary>Per-frame update before the UI (the engine tick: DMX input, fixture motors, simulations).</summary>
    public void Update(double deltaTime)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _ = deltaTime;
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
    }

    /// <summary>Brings scene changes into the render world (after the UI edited the scene, before viewports render).</summary>
    public void SyncRender() => _content?.Sync();

    /// <summary>File → New.</summary>
    public SessionResult New() => NotAvailable(Loc.Get(Str.FileNew));

    /// <summary>File → Open (a <c>.candela</c> project or an MVR scene).</summary>
    public SessionResult Open(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!File.Exists(path))
            return SessionResult.Failed($"'{path}' does not exist.");
        return NotAvailable(Loc.Get(Str.FileOpen).TrimEnd('…'));
    }

    /// <summary>File → Save (to the current file; callers ask for a path first when there is none).</summary>
    public SessionResult Save() => FilePath is null
        ? SessionResult.Failed("The scene has no file yet; use Save As.")
        : NotAvailable(Loc.Get(Str.FileSave));

    /// <summary>File → Save As (<c>.candela</c>).</summary>
    public SessionResult SaveAs(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return NotAvailable(Loc.Get(Str.FileSaveAs).TrimEnd('…'));
    }

    /// <summary>File → Import (merges into the open scene).</summary>
    public SessionResult Import(string path, ImportKind kind)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!File.Exists(path))
            return SessionResult.Failed($"'{path}' does not exist.");
        string what = kind switch
        {
            ImportKind.Project => Loc.Get(Str.FileImportProject),
            ImportKind.Model => Loc.Get(Str.FileImportModel),
            ImportKind.Mvr => Loc.Get(Str.FileImportMvr),
            _ => Loc.Get(Str.FileImportImage),
        };
        return NotAvailable($"{Loc.Get(Str.FileImport)}: {what.TrimEnd('…')}");
    }

    /// <summary>File → Export.</summary>
    public SessionResult Export(string path, ExportKind kind)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        _ = kind;
        return NotAvailable($"{Loc.Get(Str.FileExport)}: {Loc.Get(Str.FileExportMvr).TrimEnd('…')}");
    }

    /// <summary>Render panel → Render (final, path-traced frame or timecode range).</summary>
    public SessionResult StartFinalRender(FinalRenderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return SessionResult.NotAvailable(Loc.Get(Str.RenderNotAvailable));
    }

    /// <summary>
    /// Reports a program error (exception caught by the frame loop, failed panel) in the issues panel — once per code
    /// and message until the messages are cleared, and at most <see cref="MaxProgramMessages"/> different ones.
    /// </summary>
    public void ReportProgramError(string code, string message) =>
        ReportOnce(new Issue(IssueSeverity.Error, IssueCategory.Program, code, message));

    /// <summary>
    /// Issues panel → clear: removes the program messages (errors, "not available" notes) and shows the scene's own
    /// issues again; the same messages can be reported anew afterwards.
    /// </summary>
    public void ClearProgramMessages()
    {
        _reported.Clear();
        Issues.ClearProgramMessages();
        // IssueLog keeps showing the old list until it is rebuilt.
        Issues.Refresh(Scene);
    }

    /// <summary>Adds a file to the recent-files list of the settings (after a successful open/save).</summary>
    public void RememberRecentFile(string path)
    {
        _settings.Settings.AddRecentFile(Path.GetFullPath(path));
        _settings.RequestSave(0);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _content?.Dispose();
        _content = null;
    }

    private SessionResult NotAvailable(string operation)
    {
        string message = Loc.Format(Str.MsgEngineMissing, operation);
        _log.Info("Session", message);
        ReportOnce(new Issue(IssueSeverity.Note, IssueCategory.Program, "app.engine-not-available", message));
        return SessionResult.NotAvailable(message);
    }

    private void ReportOnce(Issue issue)
    {
        if (_reported.Count >= MaxProgramMessages || !_reported.Add((issue.Code, issue.Message)))
            return;
        Issues.Report(issue);
    }

    /// <summary>Marks the scene as saved (used by the engine-backed save, and tests).</summary>
    internal void MarkSaved(string? path)
    {
        FilePath = path;
        _savedVersion = Scene.Version;
    }

    /// <summary>Replaces the scene (engine-backed open/new) and notifies the panels.</summary>
    internal void ReplaceScene(SceneModel scene, string? path)
    {
        ArgumentNullException.ThrowIfNull(scene);
        Scene = scene;
        Selection.Clear();
        Undo.Clear();
        MarkSaved(path);
        SceneReplaced?.Invoke();
    }
}
