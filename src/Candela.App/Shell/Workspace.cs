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
    private ViewportPanel? _activeViewport;
    private string? _pendingIni;
    private bool _styleDirty;
    private string _status = "";
    private double _statusTime;
    private string _title = "";
    private string _titleName = "";
    private bool _titleModified;
    private readonly StatusTexts _statusTexts = new();
    private readonly Action _onLanguageChanged;
    private Panel? _dockNextPanel;
    private uint _dockNextNode;
    private readonly Action<Panel> _drawPanelContent;
    private readonly Action<Panel> _focusPanelAction;
    private readonly Action<Panel> _undockAction = DockWorkspace.Undock;
    private string _focusPanel = "";

    /// <summary>Builds the workspace and restores the last layout (or the one named on the command line).</summary>
    public Workspace(WorkspaceContext context, AppOptions options)
    {
        _ctx = context ?? throw new ArgumentNullException(nameof(context));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _drawPanelContent = DrawPanelContent;
        _focusPanelAction = FocusPanel;
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
        _library = Add(new LibraryPanel(_ctx));
        _properties = Add(new PropertiesPanel(_ctx));
        _preview = Add(new PreviewPanel(_ctx));
        _render = Add(new RenderPanel(_ctx, Report));
        _issues = Add(new IssuesPanel(_ctx, FrameSelectionInActiveViewport));
        _console = Add(new ConsolePanel(_ctx));
        RegisterCommands();
        RestoreStartupLayout();
        _activeViewport = _viewports[0];
        _onLanguageChanged = () => _title = "";
        Loc.LanguageChanged += _onLanguageChanged;
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
    }

    /// <summary>Builds the UI for this frame (inside the ImGui frame).</summary>
    public void Draw(float deltaTime)
    {
        _ = deltaTime;
        float scale = UiScale.Effective(_ctx.Settings.Settings.UiScale, ImGui.GetStyle().FontScaleDpi);
        _dock.ApplyTabStyles();
        DrawMainMenu();
        DrawStatusBar(scale);
        _dock.DrawDockSpace();
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

        _dock.DrawTabStyleMenu(_panels, _focusPanelAction, _undockAction);
        _settingsWindow.Draw(scale);
        _about.Draw(scale);
        _shortcuts.Draw(scale);
        _browser.Draw(scale);
        _messages.Draw(scale);
        _dispatcher.Dispatch();
        UpdateTitle();
    }

    /// <summary>Renders every visible viewport (inside the GPU frame).</summary>
    public void RenderViewports(float deltaTime)
    {
        _ctx.Session.SyncRender();
        foreach (var viewport in _viewports)
            viewport.Render(_ctx.Gpu.Renderer, deltaTime);
    }

    /// <summary>End of frame housekeeping.</summary>
    public void AfterFrame()
    {
    }

    /// <summary>Whether the window may close (unsaved changes are confirmed once the engine can save).</summary>
    public bool ConfirmClose() => true;

    /// <summary>Opens a file given on the command line.</summary>
    public void OpenAtStartup(string path) => Report(_ctx.Session.Open(path));

    /// <summary>Saves the current workspace as the layout restored at the next start.</summary>
    public void SaveWorkspace() => _layouts.SaveWorkspace(CaptureLayout(LayoutStore.WorkspaceName));

    /// <inheritdoc/>
    public void Dispose()
    {
        Loc.LanguageChanged -= _onLanguageChanged;
        foreach (var viewport in _viewports)
            viewport.ReleaseTarget();
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
            ImGui.TextColored(Theme.Error, $"{Icons.OctagonAlert}  {Loc.Format(Str.MsgPanelFailed, fault.Message)}");
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
            ImGuiP.ErrorRecoveryTryToRecoverState(&state);
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
            case SessionOutcome.NotAvailable:
                SetStatus(result.Message);
                _messages.Show(MessageKind.Warning, Loc.Get(Str.MsgNotAvailableTitle), result.Message);
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
                ImGui.TextColored(Theme.TextMuted, texts.Left);
                ImGui.SameLine(0, 24 * scale);
                if (!session.Selection.IsEmpty)
                    ImGui.TextColored(Theme.Accent, texts.Selection);
                string right = texts.Right;
                float w = ImGui.CalcTextSize(right).X;
                ImGui.SameLine(MathF.Max(ImGui.GetCursorPosX() + 16, ImGui.GetWindowWidth() - w - 16 * scale));
                ImGui.TextColored(Theme.TextMuted, right);
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

    private void SaveLayoutAs(string name)
    {
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
            default:
                throw new KeyNotFoundException($"unknown setting '{name}'");
        }
    }

    // ---- documentation folder -----------------------------------------------------------------------------------

    private void OpenDocsFolder()
    {
        string? docs = null;
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, "docs");
                if (File.Exists(Path.Combine(candidate, "editor.md")))
                {
                    docs = candidate;
                    break;
                }
            }

            if (docs is not null)
                break;
        }

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
