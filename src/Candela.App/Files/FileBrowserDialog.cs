using System.Globalization;
using System.Numerics;
using Candela.App.Ui;
using Hexa.NET.ImGui;

namespace Candela.App.Files;

/// <summary>
/// The portable file dialog (GLFW has none): a modal window with places and recent folders on the left, the folder's
/// entries in a sortable table, the path bar, a file-name field and the type filter. Double-click opens a folder or
/// accepts a file; saving over an existing file asks first. <see cref="Show"/> opens it; the callback receives the
/// chosen path.
/// </summary>
internal sealed class FileBrowserDialog
{
    private const string PopupId = "###file-browser";
    private readonly string _recentPlacesFile;
    private FileBrowserModel? _model;
    private Action<string>? _onAccept;
    private string _title = "";
    private string _pathText = "";
    private bool _openRequested;
    private bool _focusFileName;
    private bool _confirmOverwrite;
    private string _pendingPath = "";
    private int _selected = -1;
    private IReadOnlyList<(string Label, string Path)> _places = Array.Empty<(string, string)>();
    private string[] _rowLabels = Array.Empty<string>();
    private string[] _rowSizes = Array.Empty<string>();
    private string[] _rowDates = Array.Empty<string>();
    private FileBrowserModel? _rowsModel;
    private int _rowsVersion = -1;

    /// <summary>Creates the dialog; recently used folders are kept in <paramref name="recentPlacesFile"/>.</summary>
    public FileBrowserDialog(string recentPlacesFile)
    {
        _recentPlacesFile = recentPlacesFile;
    }

    /// <summary>True while the dialog is open.</summary>
    public bool IsOpen => _model is not null;

    /// <summary>The model of the open dialog (scripts and tests).</summary>
    internal FileBrowserModel? Model => _model;

    /// <summary>Opens the dialog.</summary>
    /// <param name="mode">Open, save or folder.</param>
    /// <param name="title">Window title.</param>
    /// <param name="filters">Type filters (first = default).</param>
    /// <param name="startFolder">Folder to show first (null = the most recent place or the documents folder).</param>
    /// <param name="fileName">Initial file name (save).</param>
    /// <param name="onAccept">Receives the chosen path.</param>
    public void Show(FileBrowserMode mode, string title, IReadOnlyList<FileFilter> filters, string? startFolder, string? fileName, Action<string> onAccept)
    {
        _model = new FileBrowserModel(mode, filters, _recentPlacesFile) { FileName = fileName ?? "" };
        _places = FileBrowserModel.StandardPlaces(Loc.Get(Str.BrowserHome), Loc.Get(Str.BrowserDocuments),
            Loc.Get(Str.BrowserDesktop), Loc.Get(Str.BrowserWorkingFolder));
        _onAccept = onAccept ?? throw new ArgumentNullException(nameof(onAccept));
        _title = title + PopupId;
        string start = startFolder is { Length: > 0 } && Directory.Exists(startFolder) ? startFolder
            : _model.RecentPlaces.FirstOrDefault(Directory.Exists) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (!_model.Navigate(start) && !_model.Navigate(Environment.CurrentDirectory))
            _model.Navigate(Path.GetPathRoot(Environment.CurrentDirectory) ?? "/");
        _pathText = _model.CurrentDirectory;
        _openRequested = true;
        _focusFileName = mode == FileBrowserMode.Save;
        _confirmOverwrite = false;
        _selected = -1;
    }

    /// <summary>Draws the dialog when open (call once per frame at top level).</summary>
    public unsafe void Draw(float scale)
    {
        if (_model is null)
            return;
        if (_openRequested)
        {
            _openRequested = false;
            ImGui.OpenPopup(PopupId);
        }

        var viewport = ImGui.GetMainViewport();
        var size = Vector2.Min(new Vector2(860, 540) * scale, viewport.WorkSize * 0.92f);
        ImGui.SetNextWindowSize(size, ImGuiCond.Appearing);
        ImGui.SetNextWindowPos(viewport.WorkPos + viewport.WorkSize * 0.5f, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        bool open = true;
        if (!ImGui.BeginPopupModal(_title, &open, ImGuiWindowFlags.NoSavedSettings))
        {
            if (!open)
                Close();
            return;
        }

        var model = _model;
        DrawPathBar(model, scale);
        float footer = FooterHeight(ImGui.GetFrameHeightWithSpacing(), ImGui.GetTextLineHeightWithSpacing(), model.Error.Length > 0);
        float placesWidth = 190f * scale;
        ImGui.BeginChild("##places", new Vector2(placesWidth, -footer), ImGuiChildFlags.Borders);
        DrawPlaces(model);
        ImGui.EndChild();
        ImGui.SameLine();
        ImGui.BeginChild("##entries", new Vector2(0, -footer), ImGuiChildFlags.Borders);
        DrawEntries(model);
        ImGui.EndChild();

        if (model.Error.Length > 0)
            ImGui.TextColored(Theme.Error, $"{Icons.TriangleAlert}  {model.Error}");
        DrawFooter(model, scale);
        DrawOverwriteConfirmation();

        if (ImGui.IsKeyPressed(ImGuiKey.Escape) && !_confirmOverwrite)
        {
            ImGui.CloseCurrentPopup();
            Close();
        }

        ImGui.EndPopup();
        if (!open)
            Close();
    }

    /// <summary>
    /// Height reserved below the places and entries lists: the file-name row and the file-type row (each a frame plus
    /// item spacing) and, when the folder could not be read, the error line. Everything the footer draws must fit, or
    /// the dialog grows a scroll bar and hides its last row.
    /// </summary>
    internal static float FooterHeight(float frameHeightWithSpacing, float textLineHeightWithSpacing, bool hasError) =>
        frameHeightWithSpacing * 2f + (hasError ? textLineHeightWithSpacing : 0f);

    /// <summary>Accepts a path programmatically (scripts): as if typed into the name field and confirmed.</summary>
    internal bool AcceptForTesting(string path)
    {
        if (_model is null)
            return false;
        _model.FileName = path;
        return TryAccept(_model, confirmOverwrite: false);
    }

    private void DrawPathBar(FileBrowserModel model, float scale)
    {
        if (IconButton(Icons.ArrowUp, Loc.Get(Str.BrowserUp)))
            Navigate(model, Directory.GetParent(model.CurrentDirectory)?.FullName);
        ImGui.SameLine();
        if (IconButton(Icons.Refresh, ""))
            model.Refresh();
        ImGui.SameLine();
        // Hidden files: a toggle button at the end of the path bar (eye = shown), so the footer keeps two rows.
        var style = ImGui.GetStyle();
        float toggleWidth = ImGui.CalcTextSize(Icons.Eye).X + style.FramePadding.X * 2f;
        ImGui.SetNextItemWidth(-(toggleWidth + style.ItemSpacing.X));
        ImGui.InputText("##path", ref _pathText, 1024);
        if (TextInput.Confirmed())
            Navigate(model, _pathText);
        ImGui.SameLine();
        bool hidden = model.ShowHidden;
        if (hidden)
            ImGui.PushStyleColor(ImGuiCol.Button, Theme.WithAlpha(Theme.Accent, 0.55f));
        if (IconButton(hidden ? Icons.Eye + "##hidden" : Icons.EyeOff + "##hidden", Loc.Get(Str.BrowserShowHidden)))
            model.SetShowHidden(!hidden);
        if (hidden)
            ImGui.PopStyleColor();
        _ = scale;
    }

    private void DrawPlaces(FileBrowserModel model)
    {
        ImGui.TextDisabled(Loc.Get(Str.BrowserPlaces));
        foreach (var (label, path) in _places)
        {
            string icon = label == Loc.Get(Str.BrowserHome) ? Icons.House : path.Length <= 3 || path == "/" ? Icons.HardDrive : Icons.Folder;
            if (ImGui.Selectable($"{icon}  {label}##{path}", model.CurrentDirectory == path))
                Navigate(model, path);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(path);
        }

        if (model.RecentPlaces.Count > 0)
        {
            ImGui.Spacing();
            ImGui.TextDisabled(Loc.Get(Str.BrowserRecentPlaces));
            foreach (var path in model.RecentPlaces)
            {
                string name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (name.Length == 0)
                    name = path;
                if (ImGui.Selectable($"{Icons.History}  {name}##recent{path}", model.CurrentDirectory == path))
                    Navigate(model, path);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(path);
            }
        }
    }

    private unsafe void DrawEntries(FileBrowserModel model)
    {
        const ImGuiTableFlags flags = ImGuiTableFlags.Sortable | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY |
                                      ImGuiTableFlags.Resizable | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp;
        if (!ImGui.BeginTable("##files", 3, flags))
            return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn(Loc.Get(Str.BrowserName), ImGuiTableColumnFlags.DefaultSort | ImGuiTableColumnFlags.WidthStretch, 4f);
        ImGui.TableSetupColumn(Loc.Get(Str.BrowserSize), ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn(Loc.Get(Str.BrowserModified), ImGuiTableColumnFlags.WidthStretch, 1.6f);
        ImGui.TableHeadersRow();
        var sort = ImGui.TableGetSortSpecs();
        if (!sort.IsNull && sort.SpecsDirty && sort.SpecsCount > 0)
        {
            var spec = sort.Specs;
            var column = spec.ColumnIndex switch { 1 => FileSortColumn.Size, 2 => FileSortColumn.Modified, _ => FileSortColumn.Name };
            model.SortBy(column, spec.SortDirection == ImGuiSortDirection.Descending);
            sort.SpecsDirty = false;
        }

        // Only the visible rows are submitted, and their texts are built once per listing: a folder with tens of
        // thousands of entries costs the same per frame as a small one.
        EnsureRowTexts(model);
        var entries = model.Entries;
        bool navigated = false;
        var clipper = new ImGuiListClipper();
        clipper.Begin(entries.Count);
        while (!navigated && clipper.Step())
        {
            for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
            {
                var e = entries[i];
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                if (e.IsDirectory)
                    ImGui.PushStyleColor(ImGuiCol.Text, Theme.Warning);
                bool clicked = ImGui.Selectable(_rowLabels[i], _selected == i, ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowDoubleClick);
                if (e.IsDirectory)
                    ImGui.PopStyleColor();
                if (clicked)
                {
                    _selected = i;
                    if (!e.IsDirectory || model.Mode == FileBrowserMode.SelectFolder)
                        model.FileName = e.Name;
                    if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                    {
                        if (e.IsDirectory)
                        {
                            Navigate(model, e.FullPath);
                            navigated = true; // the listing changed under the loop
                            break;
                        }

                        TryAccept(model, confirmOverwrite: true);
                    }
                }

                ImGui.TableNextColumn();
                if (!e.IsDirectory)
                    ImGui.TextDisabled(_rowSizes[i]);
                ImGui.TableNextColumn();
                if (e.Modified != DateTime.MinValue)
                    ImGui.TextDisabled(_rowDates[i]);
            }
        }

        clipper.End();
        ImGui.EndTable();
    }

    /// <summary>Builds the row texts (name with icon and row id, size, date) when the listing changed.</summary>
    private void EnsureRowTexts(FileBrowserModel model)
    {
        if (ReferenceEquals(model, _rowsModel) && model.Version == _rowsVersion)
            return;
        var entries = model.Entries;
        if (_rowLabels.Length < entries.Count)
        {
            int capacity = Math.Max(entries.Count, _rowLabels.Length * 2);
            _rowLabels = new string[capacity];
            _rowSizes = new string[capacity];
            _rowDates = new string[capacity];
        }

        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            _rowLabels[i] = string.Create(CultureInfo.InvariantCulture, $"{(e.IsDirectory ? Icons.Folder : Icons.File)}  {e.Name}##{i}");
            _rowSizes[i] = e.IsDirectory ? "" : FileBrowserModel.FormatSize(e.Size);
            _rowDates[i] = e.Modified == DateTime.MinValue ? "" : e.Modified.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        }

        _rowsModel = model;
        _rowsVersion = model.Version;
    }

    private void DrawFooter(FileBrowserModel model, float scale)
    {
        float buttonWidth = 110f * scale;
        float labelWidth = 90f * scale;
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(Loc.Get(Str.BrowserFileName));
        ImGui.SameLine(labelWidth);
        ImGui.SetNextItemWidth(-buttonWidth - ImGui.GetStyle().ItemSpacing.X);
        string name = model.FileName;
        if (_focusFileName)
        {
            // Saving: the name field has the keyboard from the start, so the name can be typed at once.
            ImGui.SetKeyboardFocusHere();
            _focusFileName = false;
        }

        // The model follows every keystroke, so clicking Save / Open after typing uses the typed name.
        ImGui.InputText("##filename", ref name, 512);
        model.FileName = name;
        if (TextInput.Confirmed())
            TryAccept(model, confirmOverwrite: true);

        ImGui.SameLine();
        string accept = model.Mode switch
        {
            FileBrowserMode.Save => Loc.Get(Str.BrowserSave),
            FileBrowserMode.SelectFolder => Loc.Get(Str.BrowserSelectFolder),
            _ => Loc.Get(Str.BrowserOpen),
        };
        ImGui.PushStyleColor(ImGuiCol.Button, Theme.WithAlpha(Theme.Accent, 0.75f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Theme.Accent);
        if (ImGui.Button(accept, new Vector2(buttonWidth, 0)))
            TryAccept(model, confirmOverwrite: true);
        ImGui.PopStyleColor(2);

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(Loc.Get(Str.BrowserFileType));
        ImGui.SameLine(labelWidth);
        ImGui.SetNextItemWidth(-buttonWidth - ImGui.GetStyle().ItemSpacing.X);
        if (ImGui.BeginCombo("##filter", model.Filter.Label))
        {
            for (int i = 0; i < model.Filters.Count; i++)
                if (ImGui.Selectable(model.Filters[i].Label, i == model.FilterIndex))
                    model.SelectFilter(i);
            ImGui.EndCombo();
        }

        ImGui.SameLine();
        if (ImGui.Button(Loc.Get(Str.BrowserCancel), new Vector2(buttonWidth, 0)))
        {
            ImGui.CloseCurrentPopup();
            Close();
        }

    }

    private void DrawOverwriteConfirmation()
    {
        if (!_confirmOverwrite)
            return;
        const string id = "###overwrite";
        if (!ImGui.IsPopupOpen(id))
            ImGui.OpenPopup(id);
        if (ImGui.BeginPopupModal($"{Loc.Get(Str.BrowserOverwrite)}{id}", ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings))
        {
            ImGui.TextUnformatted(Loc.Format(Str.BrowserOverwriteQuestion, Path.GetFileName(_pendingPath)));
            ImGui.Spacing();
            if (ImGui.Button(Loc.Get(Str.MsgYes), new Vector2(100, 0) * ImGui.GetStyle().FontScaleMain))
            {
                _confirmOverwrite = false;
                ImGui.CloseCurrentPopup();
                Finish(_pendingPath);
            }

            ImGui.SameLine();
            if (ImGui.Button(Loc.Get(Str.MsgNo), new Vector2(100, 0) * ImGui.GetStyle().FontScaleMain))
            {
                _confirmOverwrite = false;
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }
    }

    private bool TryAccept(FileBrowserModel model, bool confirmOverwrite)
    {
        var path = model.Resolve();
        if (path is null)
            return false;
        if (model.Mode == FileBrowserMode.Save && confirmOverwrite && FileBrowserModel.WouldOverwrite(path))
        {
            _pendingPath = path;
            _confirmOverwrite = true;
            return false;
        }

        Finish(path);
        return true;
    }

    private void Finish(string path)
    {
        var callback = _onAccept;
        _model?.AddRecentPlace(path);
        Close();
        if (ImGui.GetCurrentContext().IsNull == false)
            ImGui.CloseCurrentPopup();
        callback?.Invoke(path);
    }

    private void Navigate(FileBrowserModel model, string? path)
    {
        if (path is null)
            return;
        if (model.Navigate(path))
        {
            _pathText = model.CurrentDirectory;
            _selected = -1;
        }
    }

    /// <summary>Closes the dialog without accepting anything.</summary>
    public void Cancel() => Close();

    private void Close()
    {
        _model = null;
        _onAccept = null;
        _confirmOverwrite = false;
    }

    private static bool IconButton(string icon, string tooltip)
    {
        bool clicked = ImGui.Button(icon);
        if (tooltip.Length > 0 && ImGui.IsItemHovered())
            ImGui.SetTooltip(tooltip);
        return clicked;
    }
}
