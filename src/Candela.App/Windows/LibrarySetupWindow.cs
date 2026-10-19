using System.Numerics;
using Candela.App.Files;
using Candela.App.Input;
using Candela.App.Session;
using Candela.App.Shell;
using Candela.App.Ui;
using Candela.Engine;
using Hexa.NET.ImGui;

namespace Candela.App.Windows;

/// <summary>
/// The fixture library setup window ("Welcome to Candela"): shown at the first start of an installed editor while no
/// library folder holds GDTF files, and from Help → Fixture Library Setup. It explains what the library is, lets the
/// user choose a folder with the portable file browser or create <c>Documents\Candela\Fixture Library</c>, shows how
/// many fixture files the folder holds (counted in the background; files in subfolders are pointed out, since the
/// library reads only the folder itself), and offers the sample scene or to continue without a library. The folder goes
/// into the settings (Settings → Library changes it later) and into the running engine, so the Library panel fills at
/// once. A normal window, not a modal: the editor stays usable behind it.
/// </summary>
internal sealed class LibrarySetupWindow
{
    private readonly LibrarySetup _setup;
    private readonly FileBrowserDialog _browser;
    private readonly KeyBindingDispatcher _dispatcher;
    private readonly Func<bool> _getShowAtStart;
    private readonly Action<bool> _setShowAtStart;
    private readonly Action<string> _openScene;
    private readonly Action<string> _status;
    private bool _focusRequested;
    private string _statusLanguage = "";
    private FolderProbe? _statusProbe;
    private bool _statusProbing;
    private string _statusText = "";
    private string _subfolderText = "";
    private string _folderText = "";
    private string? _folderTextFor;
    private string _title = "";
    private string _titleLanguage = "";

    /// <summary>Creates the window.</summary>
    /// <param name="setup">The setup logic (folder, counting, settings, engine).</param>
    /// <param name="browser">The portable file browser for choosing the folder.</param>
    /// <param name="dispatcher">Shortcut dispatcher (Escape closes the window).</param>
    /// <param name="getShowAtStart">Reads the "show at start-up" preference.</param>
    /// <param name="setShowAtStart">Stores the "show at start-up" preference.</param>
    /// <param name="openScene">Opens a scene like File → Open (the sample scene).</param>
    /// <param name="status">Shows a line in the status bar.</param>
    public LibrarySetupWindow(LibrarySetup setup, FileBrowserDialog browser, KeyBindingDispatcher dispatcher, Func<bool> getShowAtStart,
        Action<bool> setShowAtStart, Action<string> openScene, Action<string> status)
    {
        _setup = setup ?? throw new ArgumentNullException(nameof(setup));
        _browser = browser ?? throw new ArgumentNullException(nameof(browser));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _getShowAtStart = getShowAtStart ?? throw new ArgumentNullException(nameof(getShowAtStart));
        _setShowAtStart = setShowAtStart ?? throw new ArgumentNullException(nameof(setShowAtStart));
        _openScene = openScene ?? throw new ArgumentNullException(nameof(openScene));
        _status = status ?? throw new ArgumentNullException(nameof(status));
    }

    /// <summary>Whether the window is shown.</summary>
    public bool IsOpen { get; set; }

    /// <summary>The setup logic (tests and scripts).</summary>
    internal LibrarySetup Setup => _setup;

    /// <summary>Opens the window and brings it to the front.</summary>
    public void Open()
    {
        IsOpen = true;
        _focusRequested = true;
    }

    /// <summary>Uses <paramref name="folder"/> as the library folder (the browser's answer, scripts and tests).</summary>
    public void ChooseFolder(string folder)
    {
        if (_setup.UseFolder(folder))
            _status(InstallLoc.Format(InstallStr.SetupAdded, folder));
    }

    /// <summary>Creates and uses the default folder (the button, scripts and tests).</summary>
    public void UseDefaultFolder()
    {
        if (_setup.UseDefaultFolder() && _setup.Folder is { } folder)
            _status(InstallLoc.Format(InstallStr.SetupAdded, folder));
    }

    /// <summary>Opens the sample scene and closes the window (the button, scripts and tests).</summary>
    public void OpenSample()
    {
        if (_setup.SampleScene is not { } sample)
            return;
        IsOpen = false;
        _openScene(sample);
    }

    /// <summary>Draws the window when open (once per frame, top level).</summary>
    public unsafe void Draw(float scale)
    {
        _setup.Update();
        if (!IsOpen)
            return;
        var viewport = ImGui.GetMainViewport();
        var size = PopupPlacement.FitInWindow(new Vector2(700, 0) * scale, viewport.WorkSize);
        ImGui.SetNextWindowPos(viewport.WorkPos + viewport.WorkSize * 0.5f, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSizeConstraints(new Vector2(size.X, 0), new Vector2(size.X, viewport.WorkSize.Y * 0.95f));
        if (_focusRequested)
        {
            ImGui.SetNextWindowFocus();
            _focusRequested = false;
        }

        bool open = true;
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.AlwaysAutoResize
            | ImGuiWindowFlags.NoSavedSettings;
        if (!string.Equals(_titleLanguage, Loc.Language, StringComparison.Ordinal))
        {
            _titleLanguage = Loc.Language;
            _title = InstallLoc.With(Icons.Library, InstallStr.SetupTitle) + "###LibrarySetup";
        }

        if (ImGui.Begin(_title, &open, flags))
        {
            DrawContent(scale);
            if (WindowKeys.CloseRequested(_dispatcher))
                open = false;
        }

        ImGui.End();
        if (!open)
            IsOpen = false;
    }

    private void DrawContent(float scale)
    {
        float wrap = 660f * scale;
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + wrap);
        SafeText.Colored(Theme.Accent, InstallLoc.With(Icons.Lightbulb, InstallStr.SetupWelcome));
        ImGui.Spacing();
        SafeText.Wrapped(InstallLoc.Get(InstallStr.SetupIntro));
        ImGui.Spacing();
        SafeText.Colored(Theme.TextMuted, InstallLoc.Get(InstallStr.SetupWhere));
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        SafeText.Disabled(InstallLoc.Get(InstallStr.SetupFolderLabel));
        string? folder = _setup.Folder;
        SafeText.Colored(folder is null ? Theme.TextMuted : Theme.Text, FolderText(folder));
        if (ImGui.Button(InstallLoc.With(Icons.FolderSearch, InstallStr.SetupChoose)))
        {
            string from = folder ?? Path.GetDirectoryName(InstallLayout.DefaultUserLibraryFolder) ?? "";
            _browser.Show(FileBrowserMode.SelectFolder, InstallLoc.Get(InstallStr.SetupBrowserTitle), Array.Empty<FileFilter>(),
                Directory.Exists(from) ? from : null, null, ChooseFolder);
        }

        ImGui.SameLine();
        if (ImGui.Button(InstallLoc.With(Icons.FolderPlus, InstallStr.SetupUseDefault)))
            UseDefaultFolder();
        if (folder is not null && _setup.Probe is { Exists: true })
        {
            ImGui.SameLine();
            if (ImGui.Button(InstallLoc.With(Icons.FolderOpen, InstallStr.SetupOpenFolder)))
                NativeShell.Open(folder);
        }

        DrawStatus();
        ImGui.Spacing();
        SafeText.Colored(Theme.TextMuted, InstallLoc.Get(InstallStr.SetupHowToGet));
        SafeText.Colored(Theme.TextMuted, InstallLoc.Get(InstallStr.SetupWithout));
        ImGui.PopTextWrapPos();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        bool show = _getShowAtStart();
        if (ImGui.Checkbox(InstallLoc.Get(InstallStr.SetupShowAtStart), ref show))
            _setShowAtStart(show);
        ImGui.Spacing();

        bool hasLibrary = _setup.Probe is { Direct: > 0 };
        string sample = InstallLoc.With(Icons.Clapperboard, InstallStr.SetupOpenSample);
        string start = InstallLoc.Get(hasLibrary ? InstallStr.SetupStart : InstallStr.SetupContinueWithout);
        var style = ImGui.GetStyle();
        float width = (_setup.SampleScene is null ? 0 : ImGui.CalcTextSize(sample).X + style.FramePadding.X * 2 + style.ItemSpacing.X)
            + ImGui.CalcTextSize(start).X + style.FramePadding.X * 2;
        ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), ImGui.GetContentRegionAvail().X + ImGui.GetCursorPosX() - width));
        if (_setup.SampleScene is not null)
        {
            if (ImGui.Button(sample))
                OpenSample();
            ImGui.SameLine();
        }

        ImGui.PushStyleColor(ImGuiCol.Button, Theme.WithAlpha(Theme.Accent, 0.85f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Theme.Accent);
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.Rgb(0x141414));
        if (ImGui.Button(start))
            IsOpen = false;
        ImGui.PopStyleColor(3);
    }

    private void DrawStatus()
    {
        RefreshStatus();
        if (_setup.Error is { } error)
        {
            SafeText.Colored(Theme.Error, error);
            return;
        }

        if (_statusText.Length == 0)
            return;
        var color = _setup.Probing ? Theme.TextMuted
            : _setup.Probe is { Exists: false } ? Theme.Error
            : _setup.Probe is { Direct: > 0 } ? Theme.Success
            : Theme.Warning;
        SafeText.Colored(color, _statusText);
        if (_subfolderText.Length > 0)
            SafeText.Colored(Theme.Warning, _subfolderText);
    }

    /// <summary>The status lines, rebuilt only when the count or the language changed (no allocation per frame).</summary>
    private void RefreshStatus()
    {
        var probe = _setup.Probe;
        bool probing = _setup.Probing;
        if (probe == _statusProbe && probing == _statusProbing && string.Equals(_statusLanguage, Loc.Language, StringComparison.Ordinal))
            return;
        _statusProbe = probe;
        _statusProbing = probing;
        _statusLanguage = Loc.Language;
        _subfolderText = "";
        if (_setup.Folder is null)
            _statusText = "";
        else if (probing || probe is null)
            _statusText = InstallLoc.With(Icons.FolderSearch, InstallStr.SetupCounting);
        else if (!probe.Value.Exists)
            _statusText = InstallLoc.With(Icons.TriangleAlert, InstallStr.SetupMissing);
        else if (probe.Value.Direct > 0)
            _statusText = Icons.CircleCheck + "  " + InstallLoc.Format(InstallStr.SetupFound, probe.Value.Direct);
        else
            _statusText = Icons.TriangleAlert + "  " + InstallLoc.Get(InstallStr.SetupFoundNone);
        if (probe is { Exists: true, InSubfolders: > 0 } p)
            _subfolderText = InstallLoc.Format(InstallStr.SetupFoundSubfolders, p.Complete ? p.InSubfolders.ToString(System.Globalization.CultureInfo.InvariantCulture) : p.InSubfolders + "+");
    }

    private string FolderText(string? folder)
    {
        if (folder is null)
            return InstallLoc.Get(InstallStr.SetupNoFolder);
        if (!ReferenceEquals(folder, _folderTextFor))
        {
            _folderTextFor = folder;
            _folderText = Icons.Folder + "  " + folder;
        }

        return _folderText;
    }
}
