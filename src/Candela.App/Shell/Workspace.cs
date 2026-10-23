using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Candela.App.Diagnostics;
using Candela.App.Docking;
using Candela.App.Editing;
using Candela.App.Files;
using Candela.App.Input;
using Candela.App.Panels;
using Candela.App.Session;
using Candela.App.Ui;
using Candela.App.Viewport;
using Candela.App.Windows;
using Candela.Core.Diagnostics;
using Candela.Core.Presets;
using Candela.Core.Settings;
using Candela.Rendering;
using Hexa.NET.ImGui;

namespace Candela.App.Shell;

/// <summary>
/// The editor's UI: main menu, status bar, the docking workspace with all panels (viewports, library, properties,
/// issues, console, preview, render), dialogs (settings, file browser, about, shortcuts, message boxes), the command
/// registry behind menus, shortcuts and scripts, and layout persistence (the workspace at exit, named layouts, presets).
/// </summary>
internal sealed unsafe partial class Workspace : IViewportHost, IScriptTarget, IDisposable
{
    /// <summary>Most viewports that can be open.</summary>
    public const int MaxViewports = 16;

    private readonly WorkspaceContext _ctx;
    private readonly AppOptions _options;
    private readonly DockWorkspace _dock = new();
    private readonly List<Panel> _panels = new();
    private readonly List<ViewportPanel> _viewports = new();
    private readonly CommandRegistry _commands = new();
    private readonly EditOperations _edit = new();
    private readonly MenuScope _menu = new();
    private readonly MessageBoxes _messages = new();
    private readonly LayoutStore _layouts;
    private readonly KeyBindingsService _keys;
    private readonly KeyBindingDispatcher _dispatcher;
    private readonly FileBrowserDialog _browser;
    private readonly SettingsWindow _settingsWindow;
    private readonly AboutWindow _about;
    private readonly ShortcutsWindow _shortcuts;
    private readonly LibraryPanel _library;
    private readonly PropertiesPanel _properties;
    private readonly IssuesPanel _issues;
    private readonly ConsolePanel _console;
    private readonly RenderPanel _render;
    private readonly PreviewPanel _preview;
    private readonly ExternalControlPanel _external;
    private readonly CableLinksPanel _cableLinks;
    private ViewportPanel? _activeViewport;
    private readonly FittedText _statusFit = new();
    private IReadOnlyList<string>? _savedLayouts;
    private double _savedLayoutsAt;
    private string? _pendingIni;
    private bool _styleDirty;
    private string _status = "";
    private double _statusTime;
    private string _title = "";
    private string _titleName = "";
    private bool _titleModified;
    private readonly StatusTexts _statusTexts = new();
    private readonly Action _onLanguageChanged;
    private readonly Action<RenderJobRunner> _onRenderStateChanged;
    private Panel? _dockNextPanel;
    private uint _dockNextNode;
    private readonly Action<Panel> _drawPanelContent;
    private readonly Action<Panel> _focusPanelAction;
    private readonly Action<Panel> _undockAction = DockWorkspace.Undock;
    private string _focusPanel = "";
    private readonly UiGuard _guard;
    private float _frameScale = 1f;
    // Cached delegates of the guarded UI parts (no allocation per frame).
    private readonly Action _applyTabStyles, _drawMainMenu, _drawStatusBar, _drawDockSpace, _drawPanels, _drawTabStyleMenu;
    private readonly Action _drawSettings, _drawAbout, _drawShortcuts, _drawBrowser, _drawMessages, _dispatch;

    /// <summary>Builds the workspace and restores the last layout (or the one named on the command line).</summary>
    public Workspace(WorkspaceContext context, AppOptions options)
    {
        _ctx = context ?? throw new ArgumentNullException(nameof(context));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _drawPanelContent = DrawPanelContent;
        _focusPanelAction = FocusPanel;
        _guard = new UiGuard(_ctx.Log, (part, message) =>
            _ctx.Session.ReportProgramError("app.ui-exception", Loc.Format(Str.MsgUnhandled, part, message)));
        _applyTabStyles = () => _dock.ApplyTabStyles();
        _drawMainMenu = DrawMainMenu;
        _drawStatusBar = () => DrawStatusBar(_frameScale);
        _drawDockSpace = () => _dock.DrawDockSpace();
        _drawPanels = DrawPanels;
        _drawTabStyleMenu = () => _dock.DrawTabStyleMenu(_panels, _focusPanelAction, _undockAction);
        _drawSettings = () => _settingsWindow!.Draw(_frameScale);
        _drawAbout = () => _about!.Draw(_frameScale);
        _drawShortcuts = () => _shortcuts!.Draw(_frameScale);
        _drawBrowser = () => _browser!.Draw(_frameScale);
        _drawMessages = () => _messages.Draw(_frameScale);
        _dispatch = () => _dispatcher!.Dispatch();
        _layouts = new LayoutStore(ConfigPaths.LayoutsDir);
        _keys = new KeyBindingsService(_ctx.Settings.Settings, new FilePresetStore(ConfigPaths.PresetsDir),
            Path.Combine(ConfigPaths.ConfigDir, "keybindings.custom.json"), _ctx.Log);
        _dispatcher = new KeyBindingDispatcher(_commands, _keys.Current);
        _keys.Changed += () =>
        {
            _dispatcher.Rebuild(_keys.Current);
            _ctx.Settings.RequestSave(_ctx.Timer.Elapsed);
        };
        _keys.Library.Changed += () => _dispatcher.Rebuild(_keys.Current);
        _browser = new FileBrowserDialog(Path.Combine(ConfigPaths.ConfigDir, "recent-places.json"));
        _settingsWindow = new SettingsWindow(_ctx, _keys, _commands, _dispatcher, _browser, () => _styleDirty = true);
        _about = new AboutWindow(_ctx, _dispatcher);
        _shortcuts = new ShortcutsWindow(_commands, _keys, _dispatcher);

        _ctx.Session.AttachRenderer(_ctx.Gpu.Renderer);
        AddViewport();
        _library = Add(new LibraryPanel(_ctx, this, CreateModelLibrary()));
        _properties = Add(new PropertiesPanel(_ctx));
        _preview = Add(new PreviewPanel(_ctx));
        _render = Add(new RenderPanel(_ctx, Report));
        _issues = Add(new IssuesPanel(_ctx, FrameSelectionInActiveViewport));
        _console = Add(new ConsolePanel(_ctx));
        _cableLinks = Add(new CableLinksPanel(StagePanelContext.From(_ctx), ShowCableOverlay, ShowPropertiesOfSelection));
        _external = Add(new ExternalControlPanel(_ctx));
        _ctx.Tools.Stage.Browser = _browser;
        RegisterCommands();
        InitializeSession();
        InitializeCabling();
        InitializeInstall();
        RestoreStartupLayout();
        _activeViewport = _viewports[0];
        _onLanguageChanged = () => _title = "";
        Loc.LanguageChanged += _onLanguageChanged;
        _onRenderStateChanged = OnRenderStateChanged;
        _ctx.Tools.Stage.Render.StateChanged += _onRenderStateChanged;
    }

    /// <summary>All panels (viewports first), in Window-menu order.</summary>
    public IReadOnlyList<Panel> Panels => _panels;

    /// <summary>The open viewports.</summary>
    public IReadOnlyList<ViewportPanel> Viewports => _viewports;

    /// <summary>The command registry (menus, shortcuts, scripts).</summary>
    internal CommandRegistry Commands => _commands;

    /// <summary>Message boxes (tests and scripts).</summary>
    internal MessageBoxes Messages => _messages;

    /// <summary>The docking workspace.</summary>
    internal DockWorkspace Dock => _dock;

    /// <summary>The settings window.</summary>
    internal SettingsWindow SettingsWindow => _settingsWindow;

    /// <summary>The file dialog.</summary>
    internal FileBrowserDialog FileBrowser => _browser;

    /// <inheritdoc/>
    public Hexa.NET.ImGui.Backends.GLFW.GLFWwindowPtr MainWindow => _ctx.Window.BackendHandle;

    /// <summary>True once when the theme must be re-applied (UI scale or font size changed).</summary>
    public bool ConsumeStyleDirty()
    {
        bool dirty = _styleDirty;
        _styleDirty = false;
        return dirty;
    }

    /// <summary>Work outside the ImGui frame: layout loading and pick queries of the last frame.</summary>
    public void BeforeFrame()
    {
        if (_pendingIni is { } ini)
        {
            _pendingIni = null;
            ImGui.LoadIniSettingsFromMemory(ini);
        }

        foreach (var viewport in _viewports)
            viewport.ProcessPicks(_ctx.Gpu.Renderer);
        RunSessionWork();
        _library.Update();
        _ctx.Tools.Stage.Update(_ctx.Session.Engine, _ctx.Gpu.Renderer, _ctx.Settings.Settings, _ctx.Settings.ReadOnly, _ctx.Timer.Elapsed, _ctx.Gpu.Context.DeferDispose);
        _ctx.Session.EngineClockHeld = _ctx.Tools.Stage.Render.HoldsEngineClock;
    }

    /// <summary>
    /// Builds the UI for this frame (inside the ImGui frame). Every top-level part runs under the <see cref="UiGuard"/>:
    /// a failing dialog is closed, a failing menu or status bar only loses that frame's drawing, and panels keep their own
    /// per-panel error display.
    /// </summary>
    public void Draw(float deltaTime)
    {
        _ = deltaTime;
        _frameScale = UiScale.Effective(_ctx.Settings.Settings.UiScale, ImGui.GetStyle().FontScaleDpi);
        _guard.Run("dock tab styles", _applyTabStyles);
        _guard.Run("main menu", _drawMainMenu);
        _guard.Run("status bar", _drawStatusBar);
        _guard.Run("dock space", _drawDockSpace);
        _guard.Run("panels", _drawPanels);
        _guard.Run("tab style menu", _drawTabStyleMenu);
        DrawPlacementPopupGuarded();
        if (!_guard.Run("settings", _drawSettings))
            _settingsWindow.IsOpen = false;
        if (!_guard.Run("about", _drawAbout))
            _about.IsOpen = false;
        if (!_guard.Run("keyboard shortcuts", _drawShortcuts))
            _shortcuts.IsOpen = false;
        DrawLibrarySetup();
        if (!_guard.Run("file dialog", _drawBrowser))
        {
            _browser.Cancel();
            UiGuard.CloseAllPopups();
        }

        DrawSessionDialogs();
        if (!_sessionDialogs.IsOpen && !_guard.Run("message", _drawMessages))
        {
            _messages.DismissCurrent();
            UiGuard.CloseAllPopups();
        }

        _guard.Run("editing keys", _handleEditingKeys);
        _guard.Run("shortcuts", _dispatch);
        UpdateTitle();
    }

    private void DrawPanels()
    {
        float scale = _frameScale;
        foreach (var panel in _panels)
        {
            if (!panel.IsOpen)
                continue;
            if (_focusPanel == panel.Id)
            {
                ImGui.SetNextWindowFocus();
                _focusPanel = "";
            }

            if (ReferenceEquals(panel, _dockNextPanel))
            {
                ImGui.SetNextWindowDockID(_dockNextNode, ImGuiCond.Always);
                _dockNextPanel = null;
            }

            _dock.DrawPanel(panel, _panels, scale, _drawPanelContent);
        }

        foreach (var viewport in _viewports)
            viewport.EndInteractionIfHidden();
    }

    /// <summary>Renders every visible viewport (inside the GPU frame).</summary>
    public void RenderViewports(float deltaTime)
    {
        if (_ctx.Session.Content is EngineViewportContent content)
        {
            // One light list serves every viewport: a view measuring stage light (lux overlay) keeps the work light out,
            // and a running final render decides the work light alone.
            bool lux = false;
            foreach (var viewport in _viewports)
                lux |= viewport.IsShown && viewport.View.Overlay == OverlayMode.Lux;
            content.LuxOverlayShown = lux;
            content.WorkLightOverride = _ctx.Tools.Stage.Render.WorkLightInUse;
        }

        _ctx.Session.SyncRender();
        foreach (var viewport in _viewports)
            viewport.Render(_ctx.Gpu.Renderer, deltaTime);
    }

    /// <summary>End of frame housekeeping.</summary>
    public void AfterFrame()
    {
    }

    /// <summary>Whether the window may close now (asks about unsaved changes first; see <see cref="ConfirmCloseRequest"/>).</summary>
    public bool ConfirmClose() => ConfirmCloseRequest();

    /// <summary>Opens a file given on the command line (behind the busy overlay of the first frame).</summary>
    public void OpenAtStartup(string path) => OpenFile(path);

    /// <summary>Saves the current workspace as the layout restored at the next start.</summary>
    public void SaveWorkspace() => _layouts.SaveWorkspace(CaptureLayout(LayoutStore.WorkspaceName));

    /// <inheritdoc/>
    public void Dispose()
    {
        Loc.LanguageChanged -= _onLanguageChanged;
        _ctx.Tools.Stage.Render.StateChanged -= _onRenderStateChanged;
        DisposeCabling();
        DisposeSession();
        DisposeInstall();
        _library.Dispose();
        _render.Dispose();
        foreach (var viewport in _viewports)
            viewport.ReleaseTarget();
        _ctx.Tools.Stage.Dispose();
    }

    // ---- IViewportHost -------------------------------------------------------------------------------------------

    /// <inheritdoc/>
    public void SetActiveViewport(ViewportPanel viewport) => _activeViewport = viewport;

    /// <inheritdoc/>
    public bool IsActive(ViewportPanel viewport)
    {
        if (!ReferenceEquals(ActiveViewport, viewport))
            return false;
        int shown = 0;
        foreach (var v in _viewports)
            if (v.IsShown)
                shown++;
        return shown > 1;
    }

    /// <inheritdoc/>
    public void ShowViewportSettings() => FocusPanel(_preview);

    /// <inheritdoc/>
    public void SuspendShortcuts(bool suspended) => _dispatcher.Suspended = suspended || _settingsWindow.IsCapturing;

    /// <summary>True while a viewport is in the middle of a drag (gizmo, navigation, box selection).</summary>
    private bool ViewportBusy()
    {
        foreach (var viewport in _viewports)
            if (viewport.IsInteracting)
                return true;
        return false;
    }

    /// <summary>The viewport View-menu commands act on.</summary>
    internal ViewportPanel ActiveViewport => ActiveViewportRule.Choose(_activeViewport, _viewports);

    // ---- panels -------------------------------------------------------------------------------------------------

    private T Add<T>(T panel) where T : Panel
    {
        _panels.Add(panel);
        return panel;
    }

    /// <summary>View → New Viewport: opens a viewport as a tab of the active viewport's group (or floating).</summary>
    internal ViewportPanel? NewViewportNextToActive()
    {
        uint node = DockWorkspace.DockIdOf(ActiveViewport);
        var viewport = AddViewport();
        if (viewport is not null && node != 0)
        {
            _dockNextPanel = viewport;
            _dockNextNode = node;
        }

        return viewport;
    }

    /// <summary>Opens a new viewport (reusing a closed one first); returns null when the maximum is reached.</summary>
    internal ViewportPanel? AddViewport()
    {
        var closed = _viewports.FirstOrDefault(v => !v.IsOpen);
        if (closed is not null)
        {
            closed.IsOpen = true;
            _focusPanel = closed.Id;
            return closed;
        }

        if (_viewports.Count >= MaxViewports)
            return null;
        var viewport = new ViewportPanel(_viewports.Count + 1, _ctx, this);
        if (_viewports.Count > 0)
            viewport.Camera.CopyFrom(ActiveViewport.Camera);
        _viewports.Add(viewport);
        _panels.Insert(_viewports.Count - 1, viewport);
        _focusPanel = viewport.Id;
        return viewport;
    }

    private ViewportPanel EnsureViewport(int number)
    {
        while (_viewports.Count < number)
        {
            if (AddViewport() is null)
                break;
        }

        var v = _viewports[Math.Min(number, _viewports.Count) - 1];
        v.IsOpen = true;
        return v;
    }

    private void DrawPanelContent(Panel panel)
    {
        if (panel.Fault is { } fault)
        {
            SafeText.Colored(Theme.Error, $"{Icons.OctagonAlert}  {Loc.Format(Str.MsgPanelFailed, fault.Message)}");
            if (ImGui.Button(Labels.With(Icons.Refresh, Str.MsgPanelRetry)))
                panel.Fault = null;
            return;
        }

        ImGuiErrorRecoveryState state;
        ImGuiP.ErrorRecoveryStoreState(&state);
        try
        {
            panel.Draw();
        }
        catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            UiFrame.Recover(&state);
            panel.Fault = e;
            _ctx.Log.Error("UI", $"Panel '{panel.Id}' failed", e);
            _ctx.Session.ReportProgramError("app.panel-exception", Loc.Format(Str.MsgUnhandled, panel.Title, e.Message));
        }
    }

    private void FocusPanel(Panel panel)
    {
        panel.IsOpen = true;
        _focusPanel = panel.Id;
    }

    /// <summary>
    /// F12 / File → Render: shows the Render panel and starts a render with its current settings (renderer, preset,
    /// frame or range, work light) from the active viewport's camera. A running render goes on.
    /// </summary>
    private void StartRender()
    {
        FocusPanel(_render);
        _render.StartFromCommand();
    }

    /// <summary>A render ended (written, stopped, cancelled or failed): its message goes to the status bar and the log.</summary>
    private void OnRenderStateChanged(RenderJobRunner job)
    {
        if (job.State == RenderJobState.Running || job.Message is not { } message)
            return;
        SetStatus(message);
        if (job.State == RenderJobState.Failed)
            _ctx.Log.Warn("Render", message);
        else
            _ctx.Log.Info("Render", message);
    }

    /// <summary>
    /// Window menu: a checked (open) panel is closed, an unchecked one is opened and brought to the front — the check
    /// mark shows what a click will undo. Panels that cannot close (the first viewport) are only focused.
    /// </summary>
    internal void TogglePanel(Panel panel)
    {
        if (panel.IsOpen && panel.CanClose)
        {
            panel.IsOpen = false;
            panel.OnClosed();
            return;
        }

        FocusPanel(panel);
    }

    private Panel? FindPanel(string id) =>
        _panels.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)) ??
        _panels.FirstOrDefault(p => MenuScope.Matches(id, p.Title, p.Id, null));

    private void FrameSelectionInActiveViewport() => ActiveViewport.FrameSelection();

    // ---- results and status -------------------------------------------------------------------------------------

    /// <summary>Shows the outcome of a session operation.</summary>
    internal void Report(SessionResult result)
    {
        switch (result.Outcome)
        {
            case SessionOutcome.Done:
                SetStatus(result.Message);
                break;
            default:
                _ctx.Log.Warn("Session", result.Message);
                _messages.Show(MessageKind.Error, Loc.Get(Str.MsgError), result.Message);
                break;
        }
    }

    private void SetStatus(string text)
    {
        _status = text;
        _statusTime = _ctx.Timer.Elapsed;
    }

    private void UpdateTitle()
    {
        var session = _ctx.Session;
        string name = session.DisplayName;
        bool modified = session.IsModified;
        if (_title.Length > 0 && ReferenceEquals(name, _titleName) && modified == _titleModified)
            return;
        _titleName = name;
        _titleModified = modified;
        string title = $"Candela — {name}{(modified ? " •" : "")}";
        if (title == _title)
            return;
        _title = title;
        _ctx.Window.SetTitle(title);
    }

    private void DrawStatusBar(float scale)
    {
        var viewport = ImGui.GetMainViewport();
        float height = ImGui.GetFrameHeight() + 2 * scale;
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.MenuBar;
        ImGui.PushStyleColor(ImGuiCol.MenuBarBg, Theme.Rgb(0x18191C));
        if (ImGuiP.BeginViewportSideBar("##status-bar", viewport, ImGuiDir.Down, height, flags))
        {
            if (ImGui.BeginMenuBar())
            {
                var session = _ctx.Session;
                bool recent = _status.Length > 0 && _ctx.Timer.Elapsed - _statusTime < 12;
                var texts = _statusTexts;
                texts.Update(recent ? _status : null, session.Selection.Count, session.Undo.UndoDescription, _ctx.Gpu.Context.Info.DeviceName,
                    _ctx.Timer.FramesPerSecond, _ctx.Timer.Elapsed);
                // Right part at the right edge, selection count after the message; a message too long for the space
                // left is shortened with an ellipsis (whole text in the tooltip) instead of pushing the rest off the window.
                float gap = 24 * scale;
                string right = texts.Right;
                float rightX = ImGui.GetWindowWidth() - ImGui.CalcTextSize(right).X - 16 * scale;
                float selectionWidth = session.Selection.IsEmpty ? 0f : ImGui.CalcTextSize(texts.Selection).X + gap;
                float leftSpace = rightX - gap - selectionWidth - ImGui.GetCursorPosX();
                string left = _statusFit.Get(texts.Left, MathF.Max(0f, leftSpace));
                SafeText.Colored(Theme.TextMuted, left);
                if (_statusFit.IsShortened && ImGui.IsItemHovered())
                    SafeText.Tooltip(texts.Left);
                ImGui.SameLine(0, gap);
                if (!session.Selection.IsEmpty)
                    SafeText.Colored(Theme.Accent, texts.Selection);
                ImGui.SameLine(MathF.Max(ImGui.GetCursorPosX() + 16, rightX));
                SafeText.Colored(Theme.TextMuted, right);
                ImGui.EndMenuBar();
            }
        }

        ImGui.End();
        ImGui.PopStyleColor();
    }

    // ---- layouts ------------------------------------------------------------------------------------------------

    private LayoutPlan Plan(bool fourViewports)
    {
        var plan = new LayoutPlan();
        plan.Left.Add(_library.DockName);
        plan.Right.Add(_properties.DockName);
        plan.Right.Add(_preview.DockName);
        plan.Right.Add(_render.DockName);
        plan.Bottom.Add(_issues.DockName);
        plan.Bottom.Add(_console.DockName);
        plan.Bottom.Add(_cableLinks.DockName);
        plan.Bottom.Add(_external.DockName);
        int count = fourViewports ? 4 : 1;
        for (int i = 0; i < count; i++)
            plan.Center.Add(_viewports[i].DockName);
        return plan;
    }

    /// <summary>Builds a preset layout (Window → Layouts, Reset Layout).</summary>
    internal void ApplyPreset(LayoutPreset preset)
    {
        foreach (var p in _panels)
            p.IsOpen = p is not ViewportPanel;
        if (preset == LayoutPreset.FourViewports)
        {
            var top = EnsureViewport(1);
            var front = EnsureViewport(2);
            var right = EnsureViewport(3);
            var persp = EnsureViewport(4);
            top.Camera.SnapTo(ViewDirection.Top);
            front.Camera.SnapTo(ViewDirection.Front);
            right.Camera.SnapTo(ViewDirection.Right);
            if (persp.Camera.Projection == ProjectionKind.Orthographic)
                persp.Camera.ToggleProjection();
            foreach (var v in new[] { top, front, right })
            {
                v.View.Shading = ViewShading.Colorless;
                v.FrameSelection();
            }
        }
        else
        {
            EnsureViewport(1);
        }

        _dock.RequestPreset(preset, Plan(preset == LayoutPreset.FourViewports));
        _focusPanel = _properties.Id;
    }

    private void RestoreStartupLayout()
    {
        if (_options.Layout is { Length: > 0 } named)
        {
            ApplyLayout(named);
            return;
        }

        if (!_options.ResetLayout)
        {
            try
            {
                if (_layouts.LoadWorkspace() is { } saved)
                {
                    LoadLayout(saved);
                    return;
                }
            }
            catch (LayoutFormatException e)
            {
                _ctx.Log.Warn("Layout", $"The saved workspace could not be restored ({e.Message}); the default layout is used.");
            }
        }

        ApplyPreset(LayoutPreset.Default);
    }

    /// <inheritdoc/>
    public void ApplyLayout(string name)
    {
        string n = name.Trim();
        if (n.Equals("default", StringComparison.OrdinalIgnoreCase) || MenuScope.Matches(n, Loc.Get(Str.WindowLayoutDefault), null, "Default"))
        {
            ApplyPreset(LayoutPreset.Default);
            return;
        }

        if (n.Equals("viewports-4", StringComparison.OrdinalIgnoreCase) || n.Equals("FourViewports", StringComparison.OrdinalIgnoreCase) ||
            MenuScope.Matches(n, Loc.Get(Str.WindowLayoutFourViews), null, "Four Viewports"))
        {
            ApplyPreset(LayoutPreset.FourViewports);
            return;
        }

        try
        {
            var file = _layouts.Load(n);
            if (file is null)
            {
                _messages.Show(MessageKind.Error, Loc.Get(Str.MsgError), Loc.Format(Str.MsgLayoutLoadFailed, n, "not found"));
                return;
            }

            LoadLayout(file);
        }
        catch (LayoutFormatException e)
        {
            _messages.Show(MessageKind.Error, Loc.Get(Str.MsgError), Loc.Format(Str.MsgLayoutLoadFailed, n, e.Message));
        }
    }

    private void LoadLayout(LayoutFile file)
    {
        int maxViewport = 1;
        foreach (var v in file.Viewports)
            maxViewport = Math.Max(maxViewport, int.Parse(v.Id.AsSpan(8), CultureInfo.InvariantCulture));
        foreach (var id in file.Panels.Where(LayoutFile.IsViewportId))
            maxViewport = Math.Max(maxViewport, int.Parse(id.AsSpan(8), CultureInfo.InvariantCulture));
        while (_viewports.Count < Math.Min(maxViewport, MaxViewports))
            AddViewport();
        foreach (var p in _panels)
            p.IsOpen = file.Panels.Count == 0 ? p is not ViewportPanel || p == _viewports[0] : file.Panels.Contains(p.Id);
        _viewports[0].IsOpen = true;
        foreach (var state in file.Viewports)
            if (_viewports.FirstOrDefault(v => v.Id == state.Id) is { } viewport)
                viewport.ApplyLayout(state);
        _dock.ReplaceTabStyles(TabStyleMap.Parse(file.TabStyles, out int skipped));
        if (skipped > 0)
            _ctx.Log.Warn("Layout", $"{skipped} tab style entries of the layout were not understood.");
        if (file.ImGui.Length > 0)
            _pendingIni = file.ImGui;
        else
            _dock.RequestPreset(LayoutPreset.Default, Plan(false));
        _focusPanel = "";
    }

    private LayoutFile CaptureLayout(string name)
    {
        nuint size = 0;
        byte* ini = ImGui.SaveIniSettingsToMemory(&size);
        string iniText = ini == null ? "" : Marshal.PtrToStringUTF8((nint)ini, (int)size) ?? "";
        return new LayoutFile
        {
            Name = name,
            Panels = _panels.Where(p => p.IsOpen).Select(p => p.Id).ToList(),
            Viewports = _viewports.Select(v => v.ToLayout()).ToList(),
            TabStyles = _dock.SerializeTabStyles(),
            ImGui = iniText,
        };
    }

    /// <summary>
    /// Names of the saved layouts for the Window → Layouts menu: the folder is read when the menu needs it, at most once a
    /// second while it stays open (not every frame), and again right after a layout was saved or deleted here.
    /// </summary>
    private IReadOnlyList<string> SavedLayouts()
    {
        double now = _ctx.Timer.Elapsed;
        if (_savedLayouts is null || now - _savedLayoutsAt > 1.0)
        {
            _savedLayouts = _layouts.List();
            _savedLayoutsAt = now;
        }

        return _savedLayouts;
    }

    private void SaveLayoutAs(string name)
    {
        _savedLayouts = null;
        try
        {
            _layouts.Save(name, CaptureLayout(name));
            SetStatus(Loc.Format(Str.MsgLayoutSaved, name));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _messages.Show(MessageKind.Error, Loc.Get(Str.MsgError), e.Message);
        }
    }

    // ---- IScriptTarget ------------------------------------------------------------------------------------------

    /// <inheritdoc/>
    public void RunMenu(IReadOnlyList<string> path) => _menu.Request(path);

    /// <inheritdoc/>
    public bool RunAction(string id) => _commands.TryRun(id);

    /// <inheritdoc/>
    public void SetPanelOpen(string panelId, bool open)
    {
        if (FindPanel(panelId) is not { } panel)
            throw new KeyNotFoundException($"no panel '{panelId}'");
        panel.IsOpen = open || !panel.CanClose;
        if (open)
            _focusPanel = panel.Id;
    }

    /// <inheritdoc/>
    public void UndockPanel(string panelId, Vector2 position, Vector2 size)
    {
        if (FindPanel(panelId) is not { } panel)
            throw new KeyNotFoundException($"no panel '{panelId}'");
        panel.IsOpen = true;
        var window = ImGuiP.FindWindowByID(DockWorkspace.WindowIdOf(panel));
        if (window.IsNull)
            return;
        if (!window.DockNode.IsNull)
            ImGuiP.DockContextQueueUndockWindow(ImGui.GetCurrentContext(), window);
        var origin = ImGui.GetMainViewport().Pos;
        if (position != Vector2.Zero)
            ImGuiP.SetWindowPos(window, origin + position, ImGuiCond.Always);
        if (size.X > 0 && size.Y > 0)
            ImGuiP.SetWindowSize(window, size, ImGuiCond.Always);
        _focusPanel = panel.Id;
    }

    /// <inheritdoc/>
    public void SetTabStyle(string panelId, string style)
    {
        if (FindPanel(panelId) is not { } panel)
            throw new KeyNotFoundException($"no panel '{panelId}'");
        if (!Enum.TryParse(style, true, out TabStyle tabStyle))
            throw new FormatException($"unknown tab style '{style}'");
        var window = ImGuiP.FindWindowByID(DockWorkspace.WindowIdOf(panel));
        if (window.IsNull || window.DockNode.IsNull)
            throw new InvalidOperationException($"panel '{panelId}' is not docked");
        _dock.TabStyles.SetStyle(window.DockNode.ID, tabStyle);
        _dock.TabStyles.SetSelected(window.DockNode.ID, panel.Id);
    }

    /// <inheritdoc/>
    public void ConfigureViewport(string viewportId, string view, string projection, string shading, string overlay, bool grid)
    {
        if (FindPanel(viewportId) is not ViewportPanel viewport)
        {
            int number = LayoutFile.IsViewportId(viewportId) ? int.Parse(viewportId.AsSpan(8), CultureInfo.InvariantCulture) : 1;
            viewport = EnsureViewport(number);
        }

        viewport.IsOpen = true;
        viewport.Configure(view, projection, shading, overlay, grid);
        _activeViewport = viewport;
    }

    /// <inheritdoc/>
    public void SelectByName(IReadOnlyList<string> names)
    {
        var scene = _ctx.Session.Scene;
        var ids = scene.All().Where(e => names.Any(n => string.Equals(n, e.Name, StringComparison.OrdinalIgnoreCase))).Select(e => e.Id).ToList();
        if (ids.Count == 0)
            throw new KeyNotFoundException($"no entity named {string.Join(", ", names)}");
        _ctx.Session.Selection.Set(ids);
    }

    /// <inheritdoc/>
    public Vector2? ScreenPointOf(Vector3? world, string entityName)
    {
        var viewport = ActiveViewport;
        Vector3 point;
        if (world is { } w)
        {
            point = w;
        }
        else
        {
            var entity = _ctx.Session.Scene.All().FirstOrDefault(e => string.Equals(e.Name, entityName, StringComparison.OrdinalIgnoreCase))
                         ?? throw new KeyNotFoundException($"no entity named {entityName}");
            point = _ctx.Session.Content is { } content && content.TryGetBounds(entity.Id, out var bounds) && !bounds.IsEmpty ? bounds.Center : entity.WorldPosition;
        }

        return viewport.ScreenOf(point);
    }

    /// <inheritdoc/>
    public void SetSetting(string name, string value)
    {
        var s = _ctx.Settings.Settings;
        switch (name.Trim().ToLowerInvariant())
        {
            case "uiscale" or "scale":
                s.UiScale = UiScale.ClampScale(float.Parse(value, CultureInfo.InvariantCulture));
                _styleDirty = true;
                break;
            case "fontsize":
                s.FontSize = UiScale.ClampFontSize(float.Parse(value, CultureInfo.InvariantCulture));
                _styleDirty = true;
                break;
            case "language":
                s.Language = value;
                Loc.SetLanguage(value);
                break;
            case "navigation" or "navigationstyle":
                s.NavigationStyle = NavigationStyles.Normalize(value);
                break;
            case "settingssection":
                _settingsWindow.Open(Enum.Parse<SettingsSection>(value, true));
                break;
            case "librarysetup":
                RunLibrarySetupScript(value);
                break;
            default:
                throw new KeyNotFoundException($"unknown setting '{name}'");
        }
    }

    /// <inheritdoc/>
    public void SetMainWindow(int width, int height, string state)
    {
        var window = _ctx.Window;
        switch (state.Trim().ToLowerInvariant())
        {
            case "minimize":
                window.Minimize();
                break;
            case "restore":
                window.Restore();
                break;
            case "maximize":
                window.Maximize();
                break;
            default:
                window.SetSize(width, height);
                break;
        }
    }

    // ---- documentation folder -----------------------------------------------------------------------------------

    private void OpenDocsFolder()
    {
        // The installed copy's docs folder, else the repository's (InstallLayout's lookup order).
        string? docs = _ctx.Session.Layout.DocsFolder;
        if (docs is null)
        {
            _messages.Show(MessageKind.Warning, Loc.Get(Str.HelpDocs), Loc.Get(Str.StatusDocsMissing));
            return;
        }

        SetStatus(Loc.Format(Str.MsgDocsOpened, docs));
        try
        {
            Process.Start(new ProcessStartInfo(docs) { UseShellExecute = true });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            _ctx.Log.Info("Help", $"Documentation: {docs} ({e.Message})");
        }
    }

    // ---- misc ---------------------------------------------------------------------------------------------------

    private void Notify(string text) => SetStatus(text);

    private static string ChordText(KeyBindingsService keys, string action) =>
        keys.Current.Bindings.TryGetValue(action, out var list) && list is { Count: > 0 } ? KeyNames.DisplayChord(list[0]) : "";

    private void ReportMissingMenu(string path) => _ctx.Log.Warn("Script", $"No menu item '{path}'.");

    private static IssueSeverity Severity(SessionOutcome outcome) => outcome == SessionOutcome.Failed ? IssueSeverity.Error : IssueSeverity.Note;

    /// <summary>Status bar texts, rebuilt only when what they show changes (no string building per frame).</summary>
    private sealed class StatusTexts
    {
        private string? _message;
        private int _selectionCount = -1;
        private string? _undo;
        private string _device = "";
        private int _fps = -1;
        private double _rightBuiltAt = double.NegativeInfinity;
        private string _language = "";

        public string Left { get; private set; } = "";

        public string Selection { get; private set; } = "";

        public string Right { get; private set; } = "";

        public void Update(string? message, int selectionCount, string? undo, string device, double fps, double now)
        {
            bool language = !string.Equals(_language, Loc.Language, StringComparison.Ordinal);
            _language = Loc.Language;
            if (language || !ReferenceEquals(message, _message) || Left.Length == 0)
            {
                _message = message;
                Left = message is null ? Labels.With(Icons.CircleCheck, Str.StatusReady) : $"{Icons.Info}  {message}";
            }

            if (language || selectionCount != _selectionCount)
            {
                _selectionCount = selectionCount;
                Selection = $"{Icons.Pointer}  {Loc.Format(Str.StatusSelected, selectionCount)}";
            }

            int roundedFps = (int)Math.Round(fps);
            // The frame rate is refreshed twice a second; undo text and device immediately.
            if (language || !ReferenceEquals(undo, _undo) || !ReferenceEquals(device, _device) || (roundedFps != _fps && now - _rightBuiltAt > 0.5))
            {
                _undo = undo;
                _device = device;
                _fps = roundedFps;
                _rightBuiltAt = now;
                Right = string.Create(CultureInfo.InvariantCulture,
                    $"{(undo is { } u ? Loc.Format(Str.StatusUndo, u) + "    " : "")}{Icons.Cpu}  {device}    {roundedFps} fps");
            }
        }
    }
}
