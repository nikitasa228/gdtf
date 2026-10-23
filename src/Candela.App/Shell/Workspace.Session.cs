using System.Numerics;
using Candela.App.Editing;
using Candela.App.Files;
using Candela.App.Input;
using Candela.App.Session;
using Candela.App.Ui;
using Candela.App.Viewport;
using Candela.App.Windows;
using Candela.Core.Math;
using Candela.Core.SceneGraph.Components;
using Candela.Engine;
using Candela.Project;
using Hexa.NET.ImGui;

namespace Candela.App.Shell;

/// <summary>
/// The document side of the workspace: New / Open / Save / Save As / Import / Export through the
/// <see cref="EditorSession"/> with a busy overlay and the unsaved-changes question, files dropped onto the window, the
/// start-up recovery prompt and framing the viewports on a scene that was just opened.
/// </summary>
/// <remarks>
/// File operations are synchronous engine calls. They run between two frames (<see cref="RunSessionWork"/>), after the
/// busy overlay naming the operation has been presented once, so the window never looks frozen without a reason; their
/// outcome goes to the status bar, a message box when they failed, and their issues to the Issues panel (brought to the
/// front when there are errors).
/// </remarks>
internal sealed partial class Workspace
{
    private static readonly FileFilter GdtfFilter = new("GDTF", "*.gdtf");

    private readonly SessionDialogs _sessionDialogs = new();
    private FileDropQueue? _drops;
    private PendingOperation? _pendingOperation;
    private Action? _onSessionSceneReplaced;
    private Action? _drawSessionDialogs;
    private bool _closeConfirmed;
    private bool _recoveryChecked;
    private bool _frameScenePending;
    private bool _framedOnce;
    private ViewportPanel? _savedCameraViewport;
    private Vector2 _lastDropPosition;
    private int _frameSceneAttempts;

    /// <summary>The session dialogs (tests and scripts).</summary>
    internal SessionDialogs SessionDialogs => _sessionDialogs;

    /// <summary>True while a file operation waits to run or runs.</summary>
    internal bool IsBusy => _pendingOperation is not null;

    /// <summary>Sets up the document side: drop target, scene-replaced handling, session commands.</summary>
    private void InitializeSession()
    {
        _drops = new FileDropQueue(_ctx.Window);
        _onSessionSceneReplaced = () =>
        {
            _frameScenePending = true;
            _frameSceneAttempts = 0;
            _framedOnce = false;
        };
        _ctx.Session.SceneReplaced += _onSessionSceneReplaced;
        _drawSessionDialogs = () =>
        {
            _sessionDialogs.Draw(_frameScale, ImGui.GetIO().DeltaTime);
            if (_pendingOperation is { } op && _sessionDialogs.Kind == SessionDialogKind.Busy)
                op.Shown = true;
        };

        var session = _ctx.Session;
        Command(SessionActions.PauseSimulation, Str.SimPause, Icons.Clock, ToggleSimulation, () => session.Engine is not null,
            () => session.SimulationPaused);
        Command(SessionActions.ImportGdtf, Str.FileImportGdtf, Icons.Lightbulb, () => ShowImport(ImportKind.Gdtf));
        Command(SessionActions.OpenWhite, Str.LookOpenWhite, Icons.Sun, ApplyOpenWhite, () => session.Engine is not null && session.FixtureCount() > 0);
        Command(SessionActions.WorkLightOff, Str.CmdWorkLightOff, Icons.Lightbulb, () => SetWorkLight(WorkLightMode.Off),
            () => session.Engine is not null, () => session.Engine?.View.WorkLight == WorkLightMode.Off);
        Command(SessionActions.WorkLightAuto, Str.CmdWorkLightAuto, Icons.Lightbulb, () => SetWorkLight(WorkLightMode.Auto),
            () => session.Engine is not null, () => session.Engine?.View.WorkLight == WorkLightMode.Auto);
        Command(SessionActions.WorkLightOn, Str.CmdWorkLightOn, Icons.Lightbulb, () => SetWorkLight(WorkLightMode.On),
            () => session.Engine is not null, () => session.Engine?.View.WorkLight == WorkLightMode.On);
    }

    /// <summary>View → Work Light: the preview's work light (stored with the project; an unsaved change).</summary>
    private void SetWorkLight(WorkLightMode mode)
    {
        if (_ctx.Session.Engine is not { } engine || !ViewSettingsEdits.SetWorkLight(engine, mode))
            return;
        SetStatus(Loc.Format(Str.MsgWorkLight, Loc.Get(WorkLightCombo.Label(mode))));
    }

    /// <summary>Releases the drop target and the scene subscription.</summary>
    private void DisposeSession()
    {
        if (_onSessionSceneReplaced is not null)
            _ctx.Session.SceneReplaced -= _onSessionSceneReplaced;
        _drops?.Dispose();
        _drops = null;
    }

    /// <summary>Draws the session dialogs (busy overlay, questions, recovery prompt).</summary>
    private void DrawSessionDialogs()
    {
        if (_drawSessionDialogs is not null && !_guard.Run("session dialog", _drawSessionDialogs))
        {
            _sessionDialogs.HideBusy();
            UiGuard.CloseAllPopups();
            // The overlay could not be drawn: the waiting operation runs without it rather than never (every later file
            // operation would otherwise be refused as "still running").
            if (_pendingOperation is { } op)
                op.Shown = true;
        }
    }

    /// <summary>
    /// Work between frames (outside the ImGui frame and the GPU frame): the pending file operation once its overlay was
    /// shown, then dropped files, framing a new scene and the start-up recovery prompt.
    /// </summary>
    private void RunSessionWork()
    {
        if (_pendingOperation is { Shown: true } op)
        {
            _pendingOperation = null;
            RunOperation(op);
        }

        if (_pendingOperation is null && !ModalOpen() && _drops is not null && _drops.TryDequeue(out var drop))
            HandleDrop(drop);
        if (_frameScenePending)
            FrameNewScene();
        if (!_recoveryChecked && _pendingOperation is null && !ModalOpen())
        {
            _recoveryChecked = true;
            if (!_options.IsScreenshotMode)
                OfferRecovery();
        }
        else if (_recoveryChecked && _pendingOperation is null && !ModalOpen())
        {
            // After the recovery prompt was answered: the fixture library setup of a first start.
            OfferLibrarySetup();
        }
    }

    /// <summary>True while a modal dialog (session question, message box, file browser) has the user's attention.</summary>
    private bool ModalOpen() => _sessionDialogs.IsOpen || _messages.Count > 0 || _browser.IsOpen;

    // ---- operations -----------------------------------------------------------------------------------------------

    /// <summary>
    /// Queues <paramref name="run"/> behind a busy overlay showing <paramref name="text"/>; <paramref name="then"/> receives
    /// the result after it was reported. One operation at a time (a second request while one waits is ignored).
    /// </summary>
    internal void RunBusy(string text, Func<SessionResult> run, Action<SessionResult>? then = null)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (_pendingOperation is not null)
        {
            _ctx.Log.Warn("Session", $"'{text}' was ignored: another file operation is still running.");
            return;
        }

        _pendingOperation = new PendingOperation(text, run, then);
        _sessionDialogs.ShowBusy(text);
    }

    private void RunOperation(PendingOperation op)
    {
        SessionResult result;
        try
        {
            result = op.Run();
        }
        catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            // The engine reports content problems as results; anything else is a program error that must not end the run.
            _ctx.Log.Error("Session", $"'{op.Text}' failed", e);
            _ctx.Session.ReportProgramError("app.operation-exception", Loc.Format(Str.MsgUnhandled, op.Text, e.Message));
            result = SessionResult.Failed(e.Message);
        }
        finally
        {
            _sessionDialogs.HideBusy();
        }

        Report(result);
        if (result.Errors > 0)
            FocusPanel(_issues);
        op.Then?.Invoke(result);
    }

    /// <summary>Runs <paramref name="proceed"/> once unsaved changes were saved or discarded (asks first when modified).</summary>
    internal void WithUnsavedCheck(Action proceed)
    {
        ArgumentNullException.ThrowIfNull(proceed);
        var session = _ctx.Session;
        if (!session.IsModified)
        {
            proceed();
            return;
        }

        _sessionDialogs.AskUnsaved(session.DisplayName, choice =>
        {
            switch (choice)
            {
                case UnsavedChoice.Save:
                    SaveThen(proceed);
                    break;
                case UnsavedChoice.Discard:
                    proceed();
                    break;
            }
        });
    }

    /// <summary>File → New (asks about unsaved changes first).</summary>
    internal void NewScene() => WithUnsavedCheck(() => RunBusy(Loc.Get(Str.MsgNewScene), _ctx.Session.New));

    /// <summary>Opens a project or MVR file (asks about unsaved changes first); a missing recent file leaves the list.</summary>
    public void OpenFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        if (!File.Exists(path))
        {
            _ctx.Session.ForgetRecentFile(path);
            Report(SessionResult.Failed(Loc.Format(Str.MsgFileMissing, path)));
            return;
        }

        WithUnsavedCheck(() => RunBusy(Loc.Format(Str.BusyOpening, Path.GetFileName(path)), () => _ctx.Session.Open(path), result =>
        {
            if (result.Succeeded)
                _ctx.Session.RememberRecentFile(path);
        }));
    }

    /// <summary>Saves (to the project file, or through Save As when there is none), then runs <paramref name="then"/> on success.</summary>
    private void SaveThen(Action? then)
    {
        var session = _ctx.Session;
        if (session.FilePath is not { } path)
        {
            ShowSaveAs(then);
            return;
        }

        RunBusy(Loc.Format(Str.BusySaving, Path.GetFileName(path)), () =>
        {
            StoreCamera();
            return session.Save();
        }, result =>
        {
            if (result.Succeeded)
            {
                session.RememberRecentFile(path);
                then?.Invoke();
            }
        });
    }

    /// <summary>File → Save As (then <paramref name="then"/> after a successful save).</summary>
    private void ShowSaveAs(Action? then) => _browser.Show(FileBrowserMode.Save, Loc.Get(Str.BrowserSaveTitle),
        new[] { Named(ProjectFilter, Str.BrowserCandelaProjects) }, StartFolder(), SuggestedName() + ".candela", path => SaveAs(path, then));

    /// <inheritdoc/>
    public void SaveFile(string path) => SaveAs(path);

    /// <summary>Saves the scene as <paramref name="path"/> (".candela" is added when missing).</summary>
    internal void SaveAs(string path, Action? then = null)
    {
        var session = _ctx.Session;
        RunBusy(Loc.Format(Str.BusySaving, Path.GetFileName(path)), () =>
        {
            StoreCamera();
            return session.SaveAs(path);
        }, result =>
        {
            if (!result.Succeeded)
                return;
            if (session.FilePath is { } saved)
                session.RememberRecentFile(saved);
            then?.Invoke();
        });
    }

    /// <summary>
    /// Imports a file into the scene (models and images at <paramref name="placement"/>, default the origin). An image
    /// comes in as a reference drawing; the popup that follows offers to make it a poster.
    /// </summary>
    internal void ImportFile(string path, ImportKind kind, Transform? placement = null)
    {
        var options = new EngineImportOptions
        {
            Placement = placement ?? Transform.Identity,
            ImageMode = ImageMode.Reference,
        };
        var at = ImGui.GetIO().MousePos;
        RunBusy(Loc.Format(Str.BusyImporting, Path.GetFileName(path)), () => _ctx.Session.Import(path, kind, options), result =>
        {
            if (!result.Succeeded)
                return;
            if (result.LibraryEntry is { } entry)
                ShowInLibrary(entry);
            if (kind is ImportKind.Model or ImportKind.Image)
                _library.RememberModel(path);
            if (kind == ImportKind.Image && result.Entities.Count == 1)
                ShowImagePopup(result.Entities[0], at);
        });
    }

    /// <summary>Brings the Library panel forward with a fixture type selected (a GDTF file was just imported).</summary>
    private void ShowInLibrary(Candela.Gdtf.FixtureLibraryEntry entry)
    {
        _library.Select(entry);
        if (!_library.IsOpen)
            _library.IsOpen = true;
        FocusPanel(_library);
    }

    /// <summary>File → Export → MVR.</summary>
    internal void ExportMvr(string path) =>
        RunBusy(Loc.Format(Str.BusyExporting, Path.GetFileName(path)), () => _ctx.Session.Export(path, ExportKind.Mvr));

    /// <summary>
    /// Stores the active viewport's camera in the project's view settings before a save, so the project opens with the
    /// view it was saved with (the change is reported to the engine, and the save that follows stores it).
    /// </summary>
    private void StoreCamera()
    {
        if (_ctx.Session.Engine is { } engine && !_ctx.Session.IsPreviewStage)
            ViewSettingsEdits.StoreCamera(engine, ActiveViewport.Camera);
    }

    private string SuggestedName()
    {
        string name = _ctx.Session.DisplayName;
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "scene" : name;
    }

    private void ApplyOpenWhite() => RunBusy(Loc.Get(Str.BusyOpenWhite), () =>
    {
        int n = Candela.Engine.StageLooks.ApplyOpenWhite(_ctx.Session.Engine!);
        return SessionResult.Done(Loc.Format(Str.MsgOpenWhite, n));
    });

    private void ToggleSimulation()
    {
        var session = _ctx.Session;
        session.SimulationPaused = !session.SimulationPaused;
        SetStatus(Loc.Get(session.SimulationPaused ? Str.MsgSimPaused : Str.MsgSimResumed));
    }

    /// <summary>
    /// Whether the main window may close now: true when nothing is unsaved or the user already answered; otherwise the
    /// unsaved-changes question is shown and the window closes once it is answered with Save (after saving) or Don't Save.
    /// </summary>
    private bool ConfirmCloseRequest()
    {
        if (_closeConfirmed || !_ctx.Session.IsModified)
            return true;
        // While another dialog has the user's attention (a file browser, a message, a session question) the request is
        // ignored: a second modal opened over it would close it and leave its owner waiting for an answer.
        if (_pendingOperation is null && !ModalOpen())
        {
            WithUnsavedCheck(() =>
            {
                _closeConfirmed = true;
                _ctx.Window.SetShouldClose(true);
            });
        }

        return false;
    }

    // ---- recovery -------------------------------------------------------------------------------------------------

    private void OfferRecovery()
    {
        var sessions = _ctx.Session.FindRecoverable();
        if (sessions.Count == 0)
            return;
        _ctx.Log.Info("Autosave", $"{sessions.Count} autosaved session(s) can be recovered.");
        _sessionDialogs.OfferRecovery(sessions, RecoverSession, s => Report(_ctx.Session.Discard(s)));
    }

    /// <summary>Recovers an autosaved session (asks about unsaved changes of the open scene first).</summary>
    internal void RecoverSession(RecoverableSession session) => WithUnsavedCheck(() =>
        RunBusy(Loc.Format(Str.BusyRecovering, session.SceneName ?? Loc.Get(Str.Untitled)), () => _ctx.Session.Recover(session)));

    // ---- dropped files --------------------------------------------------------------------------------------------

    /// <summary>Queues files as if they were dropped onto the main window at <paramref name="position"/> (main-window pixels).</summary>
    public void DropFiles(IReadOnlyList<string> paths, Vector2 position)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _drops?.Enqueue(new FileDrop(paths.ToArray(), position));
    }

    private void HandleDrop(FileDrop drop)
    {
        _lastDropPosition = drop.Position;
        var screen = ImGui.GetMainViewport().Pos + drop.Position;
        var target = ViewportAt(screen);
        var files = drop.Paths.Where(p => !string.IsNullOrWhiteSpace(p)).Select(Path.GetFullPath).Distinct(StringComparer.Ordinal).ToList();
        if (files.Count == 0)
            return;
        if (files.Count == 1 && IsScene(files[0]))
        {
            DropScene(files[0], target, screen);
            return;
        }

        // Several files: each is imported; a scene among them is merged rather than opened.
        SurfaceHit? hit = target?.SurfaceAt(screen, _ctx.Gpu.Renderer);
        RunBusy(Loc.Format(Str.BusyImporting, files.Count == 1 ? Path.GetFileName(files[0]) : files.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            () => ImportMany(files, hit));
    }

    private void DropScene(string path, ViewportPanel? target, Vector2 screen)
    {
        var session = _ctx.Session;
        if (session.Scene.Count == 0 && !session.IsModified)
        {
            OpenFile(path);
            return;
        }

        _sessionDialogs.AskOpenOrImport(Path.GetFileName(path), choice =>
        {
            if (choice == DropChoice.Open)
                OpenFile(path);
            else if (choice == DropChoice.Import)
                ImportFile(path, KindOf(path));
        });
    }

    private SessionResult ImportMany(IReadOnlyList<string> files, SurfaceHit? hit)
    {
        int imported = 0, failed = 0;
        SessionResult last = default;
        Candela.Gdtf.FixtureLibraryEntry? libraryEntry = null;
        var reasons = new List<string>();
        var offset = Vector3.Zero;
        foreach (var file in files)
        {
            if (Directory.Exists(file) || !CanImport(file))
            {
                failed++;
                string unsupported = Loc.Format(Str.MsgDropUnsupported, Path.GetFileName(file));
                reasons.Add(unsupported);
                _ctx.Session.ReportProgramNote("app.drop-unsupported", unsupported);
                continue;
            }

            var kind = KindOf(file);
            var at = PlacementFor(kind, hit);
            at.Position += offset;
            last = _ctx.Session.Import(file, kind, new EngineImportOptions { Placement = at });
            if (last.Succeeded && files.Count == 1 && kind == ImportKind.Image && last.Entities.Count == 1)
                ShowImagePopup(last.Entities[0], ImGui.GetMainViewport().Pos + _lastDropPosition);
            if (last.Succeeded)
            {
                imported++;
                libraryEntry = last.LibraryEntry ?? libraryEntry;
                // Several models dropped at once are placed side by side instead of inside each other.
                offset += new Vector3(1.5f, 0f, 0f);
                if (kind is ImportKind.Model or ImportKind.Image)
                    _library.RememberModel(file);
            }
            else
            {
                failed++;
                // The engine's reason (a refused GDTF says why it was refused).
                reasons.Add($"{Path.GetFileName(file)}: {last.Message}");
            }
        }

        // The last fixture type a dropped GDTF brought in is selected in the Library panel.
        if (libraryEntry is not null)
            ShowInLibrary(libraryEntry);
        if (files.Count == 1)
            return failed == 0 ? last : last.Message is { Length: > 0 } ? last : SessionResult.Failed(Loc.Format(Str.MsgDropUnsupported, Path.GetFileName(files[0])));
        string summary = Loc.Format(Str.MsgDropSummary, files.Count, imported, failed);
        if (imported > 0 && failed > 0)
        {
            // Some files came in: the status bar says so, and a message lists the files that did not, with the reasons.
            _messages.Show(MessageKind.Warning, Loc.Get(Str.FileImport), summary + "\n\n" + string.Join("\n", reasons));
            return SessionResult.Done(summary);
        }

        return imported > 0 ? SessionResult.Done(summary) : SessionResult.Failed(summary + "\n\n" + string.Join("\n", reasons));
    }

    /// <summary>True for files Candela opens as a scene (projects, MVR).</summary>
    internal static bool IsScene(string path) => ProjectFilter.Matches(Path.GetFileName(path)) || MvrFilter.Matches(Path.GetFileName(path));

    /// <summary>True for files Candela can import (scenes, models, images, GDTF).</summary>
    internal static bool CanImport(string path)
    {
        string name = Path.GetFileName(path);
        return IsScene(path) || GdtfFilter.Matches(name) || ImageFilter.Matches(name) || AssetFormatsAllowModel(path);
    }

    private static bool AssetFormatsAllowModel(string path) =>
        Candela.Assets.AssetFormats.IsModelFile(path) || Candela.Assets.AssetFormats.IsImageFile(path);

    private ViewportPanel? ViewportAt(Vector2 screen)
    {
        foreach (var v in _viewports)
            if (v.IsShown && v.ContainsScreenPoint(screen))
                return v;
        return null;
    }

    /// <summary>
    /// Where an imported file goes: models stand on the surface hit, images lie on it; scenes and fixture files (and
    /// anything dropped outside a viewport) keep their own coordinates.
    /// </summary>
    internal static Transform PlacementFor(ImportKind kind, SurfaceHit? hit) => (kind, hit) switch
    {
        (ImportKind.Model, { } h) => Placement.ForModel(h),
        (ImportKind.Image, { } h) => Placement.ForImage(h),
        _ => Transform.Identity,
    };

    // ---- framing ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Frames every open viewport on a scene that was just opened: at once (from what is known), and again when the
    /// fixture models have loaded and been drawn — unless the user has moved the camera in between.
    /// </summary>
    private void FrameNewScene()
    {
        // The viewports need a laid-out image (size) to frame for; wait a few frames for a scene opened at start-up.
        bool laidOut = false;
        foreach (var v in _viewports)
            laidOut |= v.IsOpen && v.ImageRect.Size.X > 0;
        _frameSceneAttempts++;
        if (!laidOut && _frameSceneAttempts < 30)
            return;
        if (!_framedOnce)
        {
            _framedOnce = true;
            // A project saved with a camera opens with that view in the active viewport; the others are framed.
            _savedCameraViewport = _ctx.Session.Engine?.View.Camera is { } saved && ActiveViewport.ShowSavedCamera(saved)
                ? ActiveViewport
                : null;
            FrameOpenViewports(onlyUntouched: false);
        }

        bool settled = _ctx.Session.Content?.IsSettled ?? true;
        if (settled || _frameSceneAttempts > 1800)
        {
            FrameOpenViewports(onlyUntouched: true);
            _frameScenePending = false;
            _framedOnce = false;
            _savedCameraViewport = null;
        }
    }

    private void FrameOpenViewports(bool onlyUntouched)
    {
        foreach (var v in _viewports)
        {
            if (!v.IsOpen || ReferenceEquals(v, _savedCameraViewport) || (onlyUntouched && v.CameraMovedSinceFraming))
                continue;
            v.FrameScene();
        }
    }

    private sealed class PendingOperation(string text, Func<SessionResult> run, Action<SessionResult>? then)
    {
        public string Text { get; } = text;

        public Func<SessionResult> Run { get; } = run;

        public Action<SessionResult>? Then { get; } = then;

        /// <summary>The busy overlay was drawn (the operation may run before the next frame).</summary>
        public bool Shown { get; set; }
    }
}
