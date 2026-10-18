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
    private IReadOnlyList<string>? _devices;
    private string _gridFormat = "";
    private string _formatsLanguage = "";
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
        _devices = null;
    }

    private void RefreshFormats()
    {
        if (_gridFormat.Length > 0 && string.Equals(_formatsLanguage, Loc.Language, StringComparison.Ordinal))
            return;
        _formatsLanguage = Loc.Language;
        _gridFormat = "%.2f " + Loc.Get(Str.UnitMetres);
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
        ImGui.SetNextWindowSize(new Vector2(860, 560) * scale, ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowPos(viewport.WorkPos + viewport.WorkSize * 0.5f, ImGuiCond.FirstUseEver, new Vector2(0.5f, 0.5f));
        if (_focusRequested)
        {
            ImGui.SetNextWindowFocus();
            _focusRequested = false;
        }

        bool open = true;
        if (ImGui.Begin(Labels.With(Icons.Settings, Str.PanelSettings, "###Settings"), &open, ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoDocking))
        {
            DrawSections(scale);
            ImGui.SameLine();
            ImGui.BeginChild("##section", Vector2.Zero, ImGuiChildFlags.AlwaysUseWindowPadding);
            switch (_section)
            {
                case SettingsSection.Interface: DrawInterface(); break;
                case SettingsSection.Keyboard: DrawKeyboard(scale); break;
                case SettingsSection.Viewport: DrawViewport(); break;
                case SettingsSection.Library: DrawLibrary(); break;
                case SettingsSection.Network: DrawNetwork(); break;
                case SettingsSection.Files: DrawFiles(); break;
                case SettingsSection.Gpu: DrawGpu(); break;
            }

            ImGui.EndChild();
            if (WindowKeys.CloseRequested(_dispatcher))
                open = false;
        }

        ImGui.End();
        if (!open)
        {
            IsOpen = false;
            StopCapture();
        }
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
        if (ImGui.Selectable(Labels.With(icon, label), _section == section, ImGuiSelectableFlags.None,
                new Vector2(0, ImGui.GetFrameHeight())))
        {
            _section = section;
            StopCapture();
        }
    }

    private void DrawInterface()
    {
        var s = _ctx.Settings.Settings;
        Heading(Str.SettingsInterface);
        if (!PropertyGrid.Begin("##interface", 170f))
            return;
        PropertyGrid.Row(Str.SettingsUiScale);
        if (ImGui.BeginCombo("##scale", UiScale.Percent(s.UiScale)))
        {
            foreach (var preset in UiScale.Presets)
                if (ImGui.Selectable(UiScale.Percent(preset), MathF.Abs(preset - s.UiScale) < 0.001f))
                    SetScale(preset);
            ImGui.EndCombo();
        }

        PropertyGrid.Row("");
        float percent = s.UiScale * 100f;
        if (ImGui.SliderFloat("##scale-fine", ref percent, 100f, 300f, "%.0f %%"))
            SetScale(MathF.Round(percent) / 100f);
        PropertyGrid.Row(Str.SettingsFontSize);
        float font = s.FontSize;
        if (ImGui.SliderFloat("##font", ref font, UiScale.MinFontSize, UiScale.MaxFontSize, "%.0f px"))
        {
            s.FontSize = UiScale.ClampFontSize(MathF.Round(font));
            Changed();
            _styleChanged();
        }

        PropertyGrid.Row(Str.SettingsLanguage);
        string currentLanguage = Loc.Languages.FirstOrDefault(l => l.Code == s.Language).Name ?? "English";
        if (ImGui.BeginCombo("##language", currentLanguage))
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

        PropertyGrid.Row(Str.SettingsTheme);
        if (ImGui.BeginCombo("##theme", Loc.Get(Str.SettingsThemeDark)))
        {
            ImGui.Selectable(Loc.Get(Str.SettingsThemeDark), true);
            ImGui.EndCombo();
        }

        PropertyGrid.End();
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
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(Loc.Get(Str.SettingsPreset));
        ImGui.SameLine();
        ImGui.SetNextItemWidth(200 * scale);
        if (ImGui.BeginCombo("##preset", PresetDisplayName(library.CurrentName)))
        {
            foreach (var name in library.Names)
                if (ImGui.Selectable(PresetDisplayName(name), name == library.CurrentName) && name != PresetLibrary<KeyBindingSet>.CustomName)
                {
                    StopCapture();
                    _keys.Select(name);
                }

            ImGui.EndCombo();
        }

        ImGui.SameLine();
        bool deletable = library.CurrentName is not (PresetLibrary<KeyBindingSet>.DefaultName or PresetLibrary<KeyBindingSet>.CustomName);
        ImGui.BeginDisabled(!deletable);
        if (ImGui.Button(Icons.Trash + "##delete-preset"))
            _keys.Delete(library.CurrentName);
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(Loc.Get(Str.SettingsDeletePreset));
        ImGui.SameLine();
        if (ImGui.Button(Labels.With(Icons.RotateCcw, Str.SettingsResetDefaults)))
        {
            StopCapture();
            _keys.ResetToDefault();
        }

        ImGui.SetNextItemWidth(200 * scale);
        ImGui.InputTextWithHint("##preset-name", Loc.Get(Str.SettingsPresetNameHint), ref _presetName, 64);
        ImGui.SameLine();
        string trimmed = _presetName.Trim();
        bool validName = trimmed.Length > 0 && trimmed is not (PresetLibrary<KeyBindingSet>.DefaultName or PresetLibrary<KeyBindingSet>.CustomName);
        ImGui.BeginDisabled(!validName);
        if (ImGui.Button(Labels.With(Icons.Save, Str.SettingsSaveAs)))
        {
            try
            {
                _keys.SaveAs(trimmed);
                _presetName = "";
            }
            catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
            {
                _ctx.Log.Warn("Keys", $"The preset could not be saved: {e.Message}");
            }
        }

        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##key-filter", Labels.With(Icons.Search, Str.Search), ref _keyFilter, 64);
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
                string text = capturing ? Loc.Get(Str.SettingsPressKey) : KeyNames.DisplayChord(chords![i]);
                if (capturing)
                    ImGui.PushStyleColor(ImGuiCol.Button, Theme.WithAlpha(Theme.Accent, 0.6f));
                if (ImGui.Button(text))
                    StartCapture(command.Id, i);
                if (capturing)
                    ImGui.PopStyleColor();
                if (ImGui.BeginPopupContextItem("##chord-menu"))
                {
                    if (ImGui.MenuItem(Labels.With(Icons.Trash, Str.SettingsRemoveShortcut)))
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

    /// <summary>The shown name of a preset: the reserved "Default" and "Custom" are translated, saved names are not.</summary>
    internal static string PresetDisplayName(string name) => name switch
    {
        PresetLibrary<KeyBindingSet>.DefaultName => Loc.Get(Str.PresetDefault),
        PresetLibrary<KeyBindingSet>.CustomName => Loc.Get(Str.PresetCustom),
        _ => name,
    };

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
                // Escape cancels the recording only: it neither closes the window nor clears the scene selection.
                _dispatcher.Consume(ImGuiKey.Escape);
                StopCapture();
                return;
            }

            string? name = KeyNames.FromImGui(key);
            if (name is null)
                continue;
            var chord = new KeyChord(name, KeyNames.ModifiersOf(io.KeyCtrl, io.KeyShift, io.KeyAlt, io.KeySuper));
            _keys.SetChord(_captureAction!, _captureIndex, chord);
            // The recorded press must not also run the command it was just bound to (shortcuts resume this frame).
            _dispatcher.Consume(key);
            StopCapture();
            return;
        }
    }

    private void DrawViewport()
    {
        var s = _ctx.Settings.Settings;
        Heading(Str.SettingsViewport);
        if (!PropertyGrid.Begin("##viewport", 190f))
            return;
        PropertyGrid.Row(Str.SettingsNavigation);
        if (ImGui.BeginCombo("##navigation", s.NavigationStyle))
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

        PropertyGrid.Row("");
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextMuted);
        ImGui.TextWrapped(Loc.Get(NavigationHelp(s.NavigationStyle)));
        ImGui.PopStyleColor();
        PropertyGrid.Row(Str.SettingsZoomSpeed);
        float zoom = s.ZoomSpeed;
        if (ImGui.SliderFloat("##zoom", ref zoom, 0.1f, 5f, "%.1f×", ImGuiSliderFlags.Logarithmic))
        {
            s.ZoomSpeed = zoom;
            Changed();
        }

        var settings = _ctx.Session.Scene.Settings;
        PropertyGrid.Row(Str.SettingsUnits);
        int units = (int)settings.DisplayUnits;
        string[] unitNames = { Loc.Get(Str.SettingsUnitsMetric), Loc.Get(Str.SettingsUnitsImperial) };
        if (ImGui.Combo("##units", ref units, unitNames, unitNames.Length))
        {
            settings.DisplayUnits = (UnitSystem)units;
            _ctx.Session.Scene.NotifySettingsChanged();
        }

        PropertyGrid.Row(Str.SettingsGridSpacing);
        float grid = settings.GridSpacing;
        RefreshFormats();
        if (ImGui.DragFloat("##grid", ref grid, 0.01f, 0.05f, 50f, _gridFormat))
        {
            settings.GridSpacing = Math.Clamp(grid, 0.05f, 50f);
            _ctx.Session.Scene.NotifySettingsChanged();
        }

        PropertyGrid.Row(Str.SettingsAngleStep);
        float angle = _ctx.Tools.AngleStep;
        if (ImGui.DragFloat("##angle", ref angle, 0.5f, 1f, 90f, "%.0f°"))
            _ctx.Tools.AngleStep = Math.Clamp(angle, 1f, 90f);
        PropertyGrid.End();
    }

    /// <summary>The summary of a navigation style's mouse bindings.</summary>
    internal static Str NavigationHelp(string style) => NavigationStyles.Normalize(style) switch
    {
        NavigationStyles.Blender => Str.SettingsNavHelpBlender,
        NavigationStyles.Maya => Str.SettingsNavHelpMaya,
        _ => Str.SettingsNavHelpCandela,
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

        if (ImGui.Button(Labels.With(Icons.FolderPlus, Str.SettingsAddFolder)))
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

    private void DrawNetwork()
    {
        var s = _ctx.Settings.Settings;
        Heading(Str.SettingsNetwork);
        _interfaces ??= ListInterfaces();
        if (PropertyGrid.Begin("##network", 230f))
        {
            PropertyGrid.Row(Str.SettingsNetworkInterface);
            string current = s.NetworkInterface.Length == 0 ? Loc.Get(Str.SettingsAllInterfaces)
                : _interfaces.FirstOrDefault(i => i.Value == s.NetworkInterface).Label ?? s.NetworkInterface;
            if (ImGui.BeginCombo("##interface", current))
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

            PropertyGrid.Row(Str.SettingsArtNetOffset);
            int offset = Math.Clamp(s.ArtNetUniverseOffset, 0, 1);
            if (ImGui.BeginCombo("##offset", Loc.Get(offset == 0 ? Str.SettingsArtNetOffset0 : Str.SettingsArtNetOffset1)))
            {
                for (int i = 0; i <= 1; i++)
                {
                    if (ImGui.Selectable(Loc.Get(i == 0 ? Str.SettingsArtNetOffset0 : Str.SettingsArtNetOffset1), offset == i))
                    {
                        s.ArtNetUniverseOffset = i;
                        Changed();
                    }
                }

                ImGui.EndCombo();
            }

            PropertyGrid.End();
        }

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

    private void DrawFiles()
    {
        var s = _ctx.Settings.Settings;
        Heading(Str.SettingsFiles);
        if (PropertyGrid.Begin("##files", 230f))
        {
            PropertyGrid.Row(Str.SettingsAutosave);
            int autosave = s.AutosaveMinutes;
            if (ImGui.SliderInt("##autosave", ref autosave, 0, 60, autosave == 0 ? Loc.Get(Str.SettingsAutosaveOff) : Loc.Get(Str.UnitMinutesFormat)))
            {
                s.AutosaveMinutes = Math.Clamp(autosave, 0, 240);
                Changed();
            }

            PropertyGrid.Row(Str.SettingsMaxRecent);
            int recent = s.MaxRecentFiles;
            if (ImGui.SliderInt("##recent", ref recent, 0, 30))
            {
                s.MaxRecentFiles = recent;
                if (s.RecentFiles.Count > recent)
                    s.RecentFiles.RemoveRange(recent, s.RecentFiles.Count - recent);
                Changed();
            }

            PropertyGrid.End();
        }

        // Long paths wrap inside the window instead of running off its right edge.
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextMuted);
        ImGui.TextWrapped(Loc.Format(Str.SettingsFileLocation, _ctx.Settings.FilePath));
        ImGui.PopStyleColor();
    }

    private void DrawGpu()
    {
        var s = _ctx.Settings.Settings;
        var info = _ctx.Gpu.Context.Info;
        Heading(Str.SettingsGpu);
        ImGui.TextUnformatted($"{Icons.Cpu}  {Loc.Format(Str.SettingsCurrentDevice, info.DeviceName)}");
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextMuted);
        ImGui.TextWrapped(string.Create(CultureInfo.InvariantCulture,
            $"{info.DriverName} · Vulkan {info.ApiVersion} · {info.DeviceMemoryBytes / (1024.0 * 1024 * 1024):0.#} GB · {Loc.Format(Str.SettingsRayTracing, Loc.Get(info.SupportsRayTracing ? Str.MsgYes : Str.MsgNo))}"));
        ImGui.PopStyleColor();
        ImGui.Spacing();
        if (PropertyGrid.Begin("##gpu", 230f))
        {
            PropertyGrid.Row(Str.SettingsGpuPreferred);
            _devices ??= GpuDevices.List(_ctx.Gpu.Context);
            string preferred = s.PreferredGpu.Trim();
            string shown = preferred.Length == 0 ? Loc.Get(Str.SettingsGpuAutomatic)
                : GpuDevices.Find(_devices, preferred) ?? Loc.Format(Str.SettingsGpuNotListed, preferred);
            if (ImGui.BeginCombo("##preferred", shown))
            {
                if (ImGui.Selectable(Loc.Get(Str.SettingsGpuAutomatic), preferred.Length == 0))
                {
                    s.PreferredGpu = "";
                    Changed();
                }

                foreach (var device in _devices)
                {
                    if (ImGui.Selectable(device, string.Equals(device, preferred, StringComparison.OrdinalIgnoreCase)))
                    {
                        s.PreferredGpu = device;
                        Changed();
                    }
                }

                ImGui.EndCombo();
            }

            PropertyGrid.Row(Str.SettingsGpuValidation);
            bool validation = s.GpuValidation;
            if (ImGui.Checkbox("##validation", ref validation))
            {
                s.GpuValidation = validation;
                Changed();
            }

            PropertyGrid.End();
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
