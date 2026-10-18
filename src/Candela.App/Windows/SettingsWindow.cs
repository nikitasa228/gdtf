using System.Globalization;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Numerics;
using Candela.App.Files;
using Candela.App.Input;
using Candela.App.Shell;
using Candela.App.Ui;
using Candela.App.Viewport;
using Candela.Core.Presets;
using Candela.Core.SceneGraph;
using Candela.Core.Settings;
using Hexa.NET.ImGui;

namespace Candela.App.Windows;

/// <summary>Sections of the settings window.</summary>
internal enum SettingsSection
{
    /// <summary>UI scale, font size, language, theme.</summary>
    Interface,

    /// <summary>Shortcut editor with presets.</summary>
    Keyboard,

    /// <summary>Navigation style, zoom speed, units.</summary>
    Viewport,

    /// <summary>Fixture library folders.</summary>
    Library,

    /// <summary>Network interface, Art-Net universe offset.</summary>
    Network,

    /// <summary>Autosave, recent files.</summary>
    Files,

    /// <summary>GPU selection and validation.</summary>
    Gpu,
}

/// <summary>
/// The settings window (requirement): interface / font scale (100 % and above, applied live), the keyboard shortcut
/// editor (every editor action, click a chord to record a new one, conflicts are flagged, named presets Default /
/// Custom / Save as…), library folders, network interface for the protocols, Art-Net universe offset, display units,
/// navigation style and zoom speed, autosave interval, GPU preference and validation, and the UI language (English /
/// Russian). Every change is saved (debounced) to the user's settings file.
/// </summary>
internal sealed class SettingsWindow
{
    private readonly WorkspaceContext _ctx;
    private readonly KeyBindingsService _keys;
    private readonly CommandRegistry _commands;
    private readonly KeyBindingDispatcher _dispatcher;
    private readonly FileBrowserDialog _browser;
    private readonly Action _styleChanged;
    private SettingsSection _section;
    private string? _captureAction;
    private int _captureIndex;
    private string _presetName = "";
    private string _keyFilter = "";
    private List<(string Label, string Value)>? _interfaces;
    private bool _focusRequested;

    /// <summary>Creates the window.</summary>
    public SettingsWindow(WorkspaceContext context, KeyBindingsService keys, CommandRegistry commands, KeyBindingDispatcher dispatcher,
        FileBrowserDialog browser, Action styleChanged)
    {
        _ctx = context ?? throw new ArgumentNullException(nameof(context));
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
        _commands = commands ?? throw new ArgumentNullException(nameof(commands));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _browser = browser ?? throw new ArgumentNullException(nameof(browser));
        _styleChanged = styleChanged ?? throw new ArgumentNullException(nameof(styleChanged));
    }

    /// <summary>Whether the window is shown.</summary>
    public bool IsOpen { get; set; }

    /// <summary>Section shown.</summary>
    public SettingsSection Section
    {
        get => _section;
        set => _section = value;
    }

    /// <summary>True while a new chord is being recorded.</summary>
    public bool IsCapturing => _captureAction is not null;

    /// <summary>Opens the window (optionally at a section) and focuses it.</summary>
    public void Open(SettingsSection? section = null)
    {
        IsOpen = true;
        _focusRequested = true;
        if (section is { } s)
            _section = s;
        _interfaces = null;
    }

    /// <summary>Draws the window when open.</summary>
    public unsafe void Draw(float scale)
    {
        if (!IsOpen)
        {
            StopCapture();
            return;
        }

        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowSize(new Vector2(820, 560) * scale, ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowPos(viewport.WorkPos + viewport.WorkSize * 0.5f, ImGuiCond.FirstUseEver, new Vector2(0.5f, 0.5f));
        if (_focusRequested)
        {
            ImGui.SetNextWindowFocus();
            _focusRequested = false;
        }

        bool open = true;
        if (ImGui.Begin($"{Icons.Settings}  {Loc.Get(Str.PanelSettings)}###Settings", &open, ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoDocking))
        {
            DrawSections(scale);
            ImGui.SameLine();
            ImGui.BeginChild("##section", Vector2.Zero, ImGuiChildFlags.AlwaysUseWindowPadding);
            switch (_section)
            {
                case SettingsSection.Interface: DrawInterface(scale); break;
                case SettingsSection.Keyboard: DrawKeyboard(scale); break;
                case SettingsSection.Viewport: DrawViewport(scale); break;
                case SettingsSection.Library: DrawLibrary(); break;
                case SettingsSection.Network: DrawNetwork(scale); break;
                case SettingsSection.Files: DrawFiles(scale); break;
                case SettingsSection.Gpu: DrawGpu(scale); break;
            }

            ImGui.EndChild();
        }

        ImGui.End();
        if (!open)
            IsOpen = false;
    }

    private void DrawSections(float scale)
    {
        ImGui.BeginChild("##sections", new Vector2(190 * scale, 0), ImGuiChildFlags.Borders);
        SectionItem(SettingsSection.Interface, Icons.MonitorCog, Str.SettingsInterface);
        SectionItem(SettingsSection.Keyboard, Icons.Keyboard, Str.SettingsKeyboard);
        SectionItem(SettingsSection.Viewport, Icons.Video, Str.SettingsViewport);
        SectionItem(SettingsSection.Library, Icons.Library, Str.SettingsLibrary);
        SectionItem(SettingsSection.Network, Icons.Network, Str.SettingsNetwork);
        SectionItem(SettingsSection.Files, Icons.Save, Str.SettingsFiles);
        SectionItem(SettingsSection.Gpu, Icons.Cpu, Str.SettingsGpu);
        ImGui.EndChild();
    }

    private void SectionItem(SettingsSection section, string icon, Str label)
    {
        if (ImGui.Selectable($"{icon}  {Loc.Get(label)}", _section == section, ImGuiSelectableFlags.None,
                new Vector2(0, ImGui.GetFrameHeight())))
        {
            _section = section;
            StopCapture();
        }
    }

    private void DrawInterface(float scale)
    {
        var s = _ctx.Settings.Settings;
        Heading(Str.SettingsInterface);
        ImGui.PushItemWidth(260 * scale);
        if (ImGui.BeginCombo(Loc.Get(Str.SettingsUiScale), UiScale.Percent(s.UiScale)))
        {
            foreach (var preset in UiScale.Presets)
                if (ImGui.Selectable(UiScale.Percent(preset), MathF.Abs(preset - s.UiScale) < 0.001f))
                    SetScale(preset);
            ImGui.EndCombo();
        }

        float percent = s.UiScale * 100f;
        if (ImGui.SliderFloat("##scale-fine", ref percent, 100f, 300f, "%.0f %%"))
            SetScale(MathF.Round(percent) / 100f);
        float font = s.FontSize;
        if (ImGui.SliderFloat(Loc.Get(Str.SettingsFontSize), ref font, UiScale.MinFontSize, UiScale.MaxFontSize, "%.0f px"))
        {
            s.FontSize = UiScale.ClampFontSize(MathF.Round(font));
            Changed();
            _styleChanged();
        }

        string currentLanguage = Loc.Languages.FirstOrDefault(l => l.Code == s.Language).Name ?? "English";
        if (ImGui.BeginCombo($"{Icons.Languages}  {Loc.Get(Str.SettingsLanguage)}", currentLanguage))
        {
            foreach (var (code, name) in Loc.Languages)
            {
                if (ImGui.Selectable(name, s.Language == code))
                {
                    s.Language = code;
                    Loc.SetLanguage(code);
                    Changed();
                }
            }

            ImGui.EndCombo();
        }

        if (ImGui.BeginCombo(Loc.Get(Str.SettingsTheme), Loc.Get(Str.SettingsThemeDark)))
        {
            ImGui.Selectable(Loc.Get(Str.SettingsThemeDark), true);
            ImGui.EndCombo();
        }

        ImGui.PopItemWidth();
    }

    private void SetScale(float scale)
    {
        _ctx.Settings.Settings.UiScale = UiScale.ClampScale(scale);
        Changed();
        _styleChanged();
    }

    private void DrawKeyboard(float scale)
    {
        Heading(Str.SettingsKeyboard);
        var library = _keys.Library;
        ImGui.SetNextItemWidth(220 * scale);
        if (ImGui.BeginCombo(Loc.Get(Str.SettingsPreset), library.CurrentName))
        {
            foreach (var name in library.Names)
                if (ImGui.Selectable(name, name == library.CurrentName) && name != PresetLibrary<KeyBindingSet>.CustomName)
                {
                    StopCapture();
                    _keys.Select(name);
                }

            ImGui.EndCombo();
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(160 * scale);
        ImGui.InputTextWithHint("##preset-name", Loc.Get(Str.SettingsPresetNameHint), ref _presetName, 64);
        ImGui.SameLine();
        bool validName = _presetName.Trim().Length > 0 && _presetName.Trim() is not (PresetLibrary<KeyBindingSet>.DefaultName or PresetLibrary<KeyBindingSet>.CustomName);
        ImGui.BeginDisabled(!validName);
        if (ImGui.Button($"{Icons.Save}  {Loc.Get(Str.SettingsSaveAs)}"))
        {
            try
            {
                _keys.SaveAs(_presetName.Trim());
                _presetName = "";
            }
            catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
            {
                _ctx.Log.Warn("Keys", $"The preset could not be saved: {e.Message}");
            }
        }

        ImGui.EndDisabled();
        ImGui.SameLine();
        bool deletable = library.CurrentName is not (PresetLibrary<KeyBindingSet>.DefaultName or PresetLibrary<KeyBindingSet>.CustomName);
        ImGui.BeginDisabled(!deletable);
        if (ImGui.Button(Icons.Trash + "##delete-preset"))
            _keys.Delete(library.CurrentName);
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(Loc.Get(Str.SettingsDeletePreset));
        ImGui.SameLine();
        if (ImGui.Button($"{Icons.RotateCcw}  {Loc.Get(Str.SettingsResetDefaults)}"))
        {
            StopCapture();
            _keys.ResetToDefault();
        }

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##key-filter", $"{Icons.Search}  {Loc.Get(Str.IssuesSearch).Replace("…", "", StringComparison.Ordinal)}", ref _keyFilter, 64);
        if (_captureAction is not null)
            PollCapture();

        const ImGuiTableFlags flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp;
        if (!ImGui.BeginTable("##keys", 2, flags, ImGui.GetContentRegionAvail()))
            return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn(Loc.Get(Str.SettingsAction), ImGuiTableColumnFlags.WidthStretch, 1.1f);
        ImGui.TableSetupColumn(Loc.Get(Str.SettingsShortcut), ImGuiTableColumnFlags.WidthStretch, 1.4f);
        ImGui.TableHeadersRow();
        var map = _dispatcher.Map;
        foreach (var command in _commands.All)
        {
            string label = command.Text;
            if (_keyFilter.Length > 0 && !label.Contains(_keyFilter, StringComparison.CurrentCultureIgnoreCase) &&
                !command.Id.Contains(_keyFilter, StringComparison.OrdinalIgnoreCase))
                continue;
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(command.Icon.Length > 0 ? $"{command.Icon}  {label}" : label);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(command.Id);
            ImGui.TableNextColumn();
            ImGui.PushID(command.Id);
            var chords = _keys.Current.Bindings.TryGetValue(command.Id, out var list) ? list : null;
            int count = chords?.Count ?? 0;
            for (int i = 0; i < count; i++)
            {
                ImGui.PushID(i);
                bool capturing = _captureAction == command.Id && _captureIndex == i;
                string text = capturing ? Loc.Get(Str.SettingsPressKey) : chords![i];
                if (capturing)
                    ImGui.PushStyleColor(ImGuiCol.Button, Theme.WithAlpha(Theme.Accent, 0.6f));
                if (ImGui.Button(text))
                    StartCapture(command.Id, i);
                if (capturing)
                    ImGui.PopStyleColor();
                if (ImGui.BeginPopupContextItem("##chord-menu"))
                {
                    if (ImGui.MenuItem($"{Icons.Trash}  {Loc.Get(Str.SettingsRemoveShortcut)}"))
                        _keys.RemoveChord(command.Id, i);
                    ImGui.EndPopup();
                }

                if (!capturing && TryParse(chords![i]) is { } chord)
                {
                    var others = map.ConflictsOf(command.Id, chord).ToList();
                    if (others.Count > 0)
                    {
                        ImGui.SameLine(0, 2);
                        ImGui.TextColored(Theme.Warning, Icons.TriangleAlert);
                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip(Loc.Format(Str.SettingsConflict, string.Join(", ", others.Select(LabelOf))));
                    }
                }

                ImGui.SameLine();
                ImGui.PopID();
            }

            bool addCapturing = _captureAction == command.Id && _captureIndex >= count;
            if (addCapturing)
                ImGui.PushStyleColor(ImGuiCol.Button, Theme.WithAlpha(Theme.Accent, 0.6f));
            if (ImGui.Button(addCapturing ? Loc.Get(Str.SettingsPressKey) : Icons.Plus))
                StartCapture(command.Id, count);
            if (addCapturing)
                ImGui.PopStyleColor();
            if (!addCapturing && ImGui.IsItemHovered())
                ImGui.SetTooltip(Loc.Get(Str.SettingsAddShortcut));
            ImGui.PopID();
        }

        ImGui.EndTable();
    }

    private string LabelOf(string actionId) => _commands.Find(actionId)?.Text ?? actionId;

    private static KeyChord? TryParse(string text)
    {
        try
        {
            return KeyChord.Parse(text);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private void StartCapture(string action, int index)
    {
        _captureAction = action;
        _captureIndex = index;
        _dispatcher.Suspended = true;
    }

    private void StopCapture()
    {
        if (_captureAction is null)
            return;
        _captureAction = null;
        _dispatcher.Suspended = false;
    }

    private void PollCapture()
    {
        var io = ImGui.GetIO();
        foreach (var key in KeyNames.BindableKeys)
        {
            if (!ImGui.IsKeyPressed(key, false))
                continue;
            if (key == ImGuiKey.Escape && !io.KeyCtrl && !io.KeyShift && !io.KeyAlt)
            {
                StopCapture();
                return;
            }

            string? name = KeyNames.FromImGui(key);
            if (name is null)
                continue;
            var chord = new KeyChord(name, KeyNames.ModifiersOf(io.KeyCtrl, io.KeyShift, io.KeyAlt, io.KeySuper));
            _keys.SetChord(_captureAction!, _captureIndex, chord);
            StopCapture();
            return;
        }
    }

    private void DrawViewport(float scale)
    {
        var s = _ctx.Settings.Settings;
        Heading(Str.SettingsViewport);
        ImGui.PushItemWidth(260 * scale);
        if (ImGui.BeginCombo(Loc.Get(Str.SettingsNavigation), s.NavigationStyle))
        {
            foreach (var style in NavigationStyles.All)
            {
                if (ImGui.Selectable(style, s.NavigationStyle == style))
                {
                    s.NavigationStyle = style;
                    Changed();
                }
            }

            ImGui.EndCombo();
        }

        ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextMuted);
        ImGui.TextWrapped(NavigationHelp(s.NavigationStyle));
        ImGui.PopStyleColor();
        float zoom = s.ZoomSpeed;
        if (ImGui.SliderFloat(Loc.Get(Str.SettingsZoomSpeed), ref zoom, 0.1f, 5f, "%.1f×", ImGuiSliderFlags.Logarithmic))
        {
            s.ZoomSpeed = zoom;
            Changed();
        }

        var settings = _ctx.Session.Scene.Settings;
        int units = (int)settings.DisplayUnits;
        string[] unitNames = { Loc.Get(Str.SettingsUnitsMetric), Loc.Get(Str.SettingsUnitsImperial) };
        if (ImGui.Combo(Loc.Get(Str.SettingsUnits), ref units, unitNames, unitNames.Length))
        {
            settings.DisplayUnits = (UnitSystem)units;
            _ctx.Session.Scene.NotifySettingsChanged();
        }

        float grid = settings.GridSpacing;
        if (ImGui.DragFloat($"{Icons.Grid}  {Loc.Get(Str.ViewportGrid)}", ref grid, 0.01f, 0.05f, 50f, "%.2f m"))
        {
            settings.GridSpacing = Math.Clamp(grid, 0.05f, 50f);
            _ctx.Session.Scene.NotifySettingsChanged();
        }

        float angle = _ctx.Tools.AngleStep;
        if (ImGui.DragFloat($"{Icons.Magnet}  {Loc.Get(Str.ToolSnapping)} (°)", ref angle, 0.5f, 1f, 90f, "%.0f°"))
            _ctx.Tools.AngleStep = Math.Clamp(angle, 1f, 90f);
        ImGui.PopItemWidth();
    }

    private static string NavigationHelp(string style) => NavigationStyles.Normalize(style) switch
    {
        NavigationStyles.Blender => "MMB orbit · Shift+MMB pan · Ctrl+MMB zoom · Shift+RMB fly (W/A/S/D/Q/E) · wheel zoom · LMB select",
        NavigationStyles.Maya => "Alt+LMB orbit · Alt+MMB / MMB pan · Alt+RMB zoom · RMB fly (W/A/S/D/Q/E) · wheel zoom · LMB select",
        _ => "MMB / Alt+LMB orbit · Shift+MMB / Alt+Shift+LMB pan · Ctrl+MMB zoom · RMB fly (W/A/S/D/Q/E) · wheel zoom · LMB select",
    };

    private void DrawLibrary()
    {
        var s = _ctx.Settings.Settings;
        Heading(Str.SettingsLibraryFolders);
        int remove = -1;
        for (int i = 0; i < s.LibraryFolders.Count; i++)
        {
            ImGui.PushID(i);
            if (ImGui.Button(Icons.Trash))
                remove = i;
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            bool exists = Directory.Exists(s.LibraryFolders[i]);
            ImGui.TextColored(exists ? Theme.Text : Theme.Error, $"{Icons.Folder}  {s.LibraryFolders[i]}");
            ImGui.PopID();
        }

        if (s.LibraryFolders.Count == 0)
            ImGui.TextDisabled("—");
        if (remove >= 0)
        {
            s.LibraryFolders.RemoveAt(remove);
            Changed();
        }

        if (ImGui.Button($"{Icons.FolderPlus}  {Loc.Get(Str.SettingsAddFolder)}"))
        {
            _browser.Show(FileBrowserMode.SelectFolder, Loc.Get(Str.BrowserFolderTitle), Array.Empty<FileFilter>(), null, null, path =>
            {
                if (!s.LibraryFolders.Contains(path))
                {
                    s.LibraryFolders.Add(path);
                    Changed();
                }
            });
        }

        ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextMuted);
        ImGui.TextWrapped(Loc.Get(Str.SettingsRestartNote));
        ImGui.PopStyleColor();
    }

    private void DrawNetwork(float scale)
    {
        var s = _ctx.Settings.Settings;
        Heading(Str.SettingsNetwork);
        _interfaces ??= ListInterfaces();
        ImGui.PushItemWidth(320 * scale);
        string current = s.NetworkInterface.Length == 0 ? Loc.Get(Str.SettingsAllInterfaces)
            : _interfaces.FirstOrDefault(i => i.Value == s.NetworkInterface).Label ?? s.NetworkInterface;
        if (ImGui.BeginCombo(Loc.Get(Str.SettingsNetworkInterface), current))
        {
            if (ImGui.Selectable(Loc.Get(Str.SettingsAllInterfaces), s.NetworkInterface.Length == 0))
            {
                s.NetworkInterface = "";
                Changed();
            }

            foreach (var (label, value) in _interfaces)
            {
                if (ImGui.Selectable(label, s.NetworkInterface == value))
                {
                    s.NetworkInterface = value;
                    Changed();
                }
            }

            ImGui.EndCombo();
        }

        int offset = s.ArtNetUniverseOffset;
        string[] offsets = { "0 — Art-Net 0:0:0 = universe 0", "1 — Art-Net 0:0:0 = universe 1 (grandMA, Eos, …)" };
        if (ImGui.Combo(Loc.Get(Str.SettingsArtNetOffset), ref offset, offsets, offsets.Length))
        {
            s.ArtNetUniverseOffset = offset;
            Changed();
        }

        ImGui.PopItemWidth();
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextMuted);
        ImGui.TextWrapped(Loc.Get(Str.SettingsRestartNote));
        ImGui.PopStyleColor();
    }

    private static List<(string Label, string Value)> ListInterfaces()
    {
        var result = new List<(string, string)>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up)
                    continue;
                foreach (var address in ni.GetIPProperties().UnicastAddresses)
                {
                    if (address.Address.AddressFamily != AddressFamily.InterNetwork)
                        continue;
                    string ip = address.Address.ToString();
                    result.Add(($"{ni.Name} — {ip}", ip));
                }
            }
        }
        catch (Exception e) when (e is NetworkInformationException or PlatformNotSupportedException)
        {
            // Listing adapters is best effort; "all interfaces" always works.
        }

        return result;
    }

    private void DrawFiles(float scale)
    {
        var s = _ctx.Settings.Settings;
        Heading(Str.SettingsFiles);
        ImGui.PushItemWidth(200 * scale);
        int autosave = s.AutosaveMinutes;
        if (ImGui.SliderInt(Loc.Get(Str.SettingsAutosave), ref autosave, 0, 60, autosave == 0 ? Loc.Get(Str.SettingsAutosaveOff) : "%d min"))
        {
            s.AutosaveMinutes = Math.Clamp(autosave, 0, 240);
            Changed();
        }

        int recent = s.MaxRecentFiles;
        if (ImGui.SliderInt(Loc.Get(Str.SettingsMaxRecent), ref recent, 0, 30))
        {
            s.MaxRecentFiles = recent;
            if (s.RecentFiles.Count > recent)
                s.RecentFiles.RemoveRange(recent, s.RecentFiles.Count - recent);
            Changed();
        }

        ImGui.PopItemWidth();
        ImGui.TextDisabled(_ctx.Settings.FilePath);
    }

    private void DrawGpu(float scale)
    {
        var s = _ctx.Settings.Settings;
        var info = _ctx.Gpu.Context.Info;
        Heading(Str.SettingsGpu);
        ImGui.TextUnformatted($"{Icons.Cpu}  {Loc.Format(Str.SettingsCurrentDevice, info.DeviceName)}");
        ImGui.TextDisabled(string.Create(CultureInfo.InvariantCulture,
            $"{info.DriverName} · Vulkan {info.ApiVersion} · {info.DeviceMemoryBytes / (1024.0 * 1024 * 1024):0.#} GB · RT {(info.SupportsRayTracing ? "yes" : "no")}"));
        ImGui.Spacing();
        string preferred = s.PreferredGpu;
        ImGui.SetNextItemWidth(300 * scale);
        if (ImGui.InputTextWithHint(Loc.Get(Str.SettingsGpuPreferred), Loc.Get(Str.SettingsGpuAutomatic), ref preferred, 128))
        {
            s.PreferredGpu = preferred;
            Changed();
        }

        bool validation = s.GpuValidation;
        if (ImGui.Checkbox(Loc.Get(Str.SettingsGpuValidation), ref validation))
        {
            s.GpuValidation = validation;
            Changed();
        }

        ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextMuted);
        ImGui.TextWrapped(Loc.Get(Str.SettingsRestartNote));
        ImGui.PopStyleColor();
    }

    private void Heading(Str key)
    {
        ImGui.PushFont(_ctx.Fonts.Heading, 0f);
        ImGui.TextUnformatted(Loc.Get(key));
        ImGui.PopFont();
        ImGui.Separator();
        ImGui.Spacing();
    }

    private void Changed() => _ctx.Settings.RequestSave(_ctx.Timer.Elapsed);
}
