using System.Numerics;
using Candela.App.Docking;
using Candela.App.Shell;
using Candela.App.Ui;
using Candela.Core.Diagnostics;
using Hexa.NET.ImGui;

namespace Candela.App.Panels;

/// <summary>
/// Issues panel (requirement): the errors, warnings and notes of the scene and the program from Core's
/// <see cref="IssueLog"/>, with severity icons and toggles, a category filter and a search field. Clicking an issue
/// selects the entities it refers to and frames them in the active viewport.
/// </summary>
internal sealed class IssuesPanel : Panel
{
    private readonly WorkspaceContext _ctx;
    private readonly Action _frameSelection;
    private readonly List<Issue> _visible = new();
    private readonly List<string> _categories = new();
    private IReadOnlyList<Issue>? _source;
    private string _search = "";
    private string _appliedSearch = "";
    private int _categoryIndex = -1;
    private int _appliedCategory = -1;
    private bool _showErrors = true;
    private bool _showWarnings = true;
    private bool _showNotes = true;
    private int _appliedMask = -1;
    private int _errors;
    private int _warnings;
    private int _notes;
    private string _errorsLabel = "";
    private string _warningsLabel = "";
    private string _notesLabel = "";
    private int _selected = -1;

    /// <summary>Creates the panel; <paramref name="frameSelection"/> frames the selection in the active viewport.</summary>
    public IssuesPanel(WorkspaceContext context, Action frameSelection)
        : base("Issues", Icons.TriangleAlert)
    {
        _ctx = context ?? throw new ArgumentNullException(nameof(context));
        _frameSelection = frameSelection ?? throw new ArgumentNullException(nameof(frameSelection));
    }

    /// <inheritdoc/>
    public override string Title => Loc.Get(Str.PanelIssues);

    /// <inheritdoc/>
    public override Vector2 DefaultSize => new(640, 240);

    /// <summary>Issues currently listed (after filtering).</summary>
    internal IReadOnlyList<Issue> VisibleIssues => _visible;

    /// <inheritdoc/>
    public override void Draw()
    {
        var issues = _ctx.Session.Issues;
        Refresh(issues.Current);
        DrawToolbar(issues);
        ImGui.Separator();
        if (_visible.Count == 0)
        {
            PropertiesPanel.EmptyState(Icons.CircleCheck, Loc.Get(Str.IssuesNone));
            return;
        }

        const ImGuiTableFlags flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable |
                                      ImGuiTableFlags.SizingStretchProp;
        if (!ImGui.BeginTable("##issues", 3, flags))
            return;
        float scale = ImGui.GetStyle().FontScaleMain * ImGui.GetStyle().FontScaleDpi;
        ImGui.TableSetupScrollFreeze(0, 0);
        ImGui.TableSetupColumn("sev", ImGuiTableColumnFlags.WidthFixed, 28 * scale);
        ImGui.TableSetupColumn("cat", ImGuiTableColumnFlags.WidthFixed, 96 * scale);
        ImGui.TableSetupColumn("msg", ImGuiTableColumnFlags.WidthStretch);
        unsafe
        {
            var clipper = new ImGuiListClipper();
            clipper.Begin(_visible.Count);
            while (clipper.Step())
            {
                for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                    DrawRow(i, _visible[i]);
            }

            clipper.End();
        }

        ImGui.EndTable();
    }

    private void DrawRow(int index, Issue issue)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.PushID(index);
        var (icon, color) = SeverityStyle(issue.Severity);
        ImGui.TextColored(color, icon);
        ImGui.TableNextColumn();
        ImGui.TextDisabled(issue.Category);
        ImGui.TableNextColumn();
        if (ImGui.Selectable(issue.Message, _selected == index, ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowDoubleClick))
        {
            _selected = index;
            SelectRelated(issue);
        }

        if (ImGui.IsItemHovered() && issue.Entities.Count > 0)
            ImGui.SetTooltip(Loc.Format(Str.IssuesRelatedObjects, issue.Code, issue.Entities.Count));
        else if (ImGui.IsItemHovered())
            ImGui.SetTooltip(issue.Code);
        ImGui.PopID();
    }

    private void SelectRelated(Issue issue)
    {
        var scene = _ctx.Session.Scene;
        var ids = issue.Entities.Where(id => scene.Find(id) is not null).ToList();
        if (ids.Count == 0)
            return;
        _ctx.Session.Selection.Set(ids);
        _frameSelection();
    }

    private void DrawToolbar(IssueLog issues)
    {
        SeverityToggle(_errorsLabel, Theme.Error, ref _showErrors, Str.IssueSeverityError);
        ImGui.SameLine();
        SeverityToggle(_warningsLabel, Theme.Warning, ref _showWarnings, Str.IssueSeverityWarning);
        ImGui.SameLine();
        SeverityToggle(_notesLabel, Theme.Note, ref _showNotes, Str.IssueSeverityNote);
        ImGui.SameLine();
        float scale = ImGui.GetStyle().FontScaleMain * ImGui.GetStyle().FontScaleDpi;
        ImGui.SetNextItemWidth(150 * scale);
        string current = _categoryIndex < 0 || _categoryIndex >= _categories.Count ? Loc.Get(Str.IssuesAllCategories) : _categories[_categoryIndex];
        if (ImGui.BeginCombo("##category", current))
        {
            if (ImGui.Selectable(Loc.Get(Str.IssuesAllCategories), _categoryIndex < 0))
                _categoryIndex = -1;
            for (int i = 0; i < _categories.Count; i++)
                if (ImGui.Selectable(_categories[i], _categoryIndex == i))
                    _categoryIndex = i;
            ImGui.EndCombo();
        }

        ImGui.SameLine();
        float clearWidth = ImGui.CalcTextSize(Icons.Trash).X + ImGui.GetStyle().FramePadding.X * 2;
        ImGui.SetNextItemWidth(MathF.Max(80 * scale, ImGui.GetContentRegionAvail().X - clearWidth - ImGui.GetStyle().ItemSpacing.X));
        ImGui.InputTextWithHint("##search", Labels.With(Icons.Search, Str.IssuesSearch), ref _search, 128);
        ImGui.SameLine();
        if (ImGui.Button(Icons.Trash))
            issues.ClearProgramMessages();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Loc.Get(Str.IssuesClearMessages));
    }

    private static void SeverityToggle(string label, Vector4 color, ref bool on, Str tooltip)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, on ? color : Theme.WithAlpha(color, 0.35f));
        if (on)
            ImGui.PushStyleColor(ImGuiCol.Button, Theme.Rgb(0x34373D));
        if (ImGui.Button(label))
            on = !on;
        if (on)
            ImGui.PopStyleColor();
        ImGui.PopStyleColor();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Loc.Get(tooltip));
    }

    /// <summary>Rebuilds the filtered list only when the log or a filter changed.</summary>
    private void Refresh(IReadOnlyList<Issue> source)
    {
        int mask = (_showErrors ? 1 : 0) | (_showWarnings ? 2 : 0) | (_showNotes ? 4 : 0);
        if (ReferenceEquals(source, _source) && mask == _appliedMask && _search == _appliedSearch && _categoryIndex == _appliedCategory)
            return;
        if (!ReferenceEquals(source, _source))
        {
            string? selectedCategory = _categoryIndex >= 0 && _categoryIndex < _categories.Count ? _categories[_categoryIndex] : null;
            _categories.Clear();
            _categories.AddRange(source.Select(i => i.Category).Distinct().OrderBy(c => c, StringComparer.OrdinalIgnoreCase));
            _categoryIndex = selectedCategory is null ? -1 : _categories.IndexOf(selectedCategory);
            _errors = source.Count(i => i.Severity == IssueSeverity.Error);
            _warnings = source.Count(i => i.Severity == IssueSeverity.Warning);
            _notes = source.Count(i => i.Severity == IssueSeverity.Note);
        }

        if (_errorsLabel.Length == 0 || !ReferenceEquals(source, _source))
        {
            // Rebuilt only when the issue list changes, not every frame.
            _errorsLabel = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{Icons.OctagonAlert} {_errors}##errors");
            _warningsLabel = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{Icons.TriangleAlert} {_warnings}##warnings");
            _notesLabel = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{Icons.Info} {_notes}##notes");
        }

        _source = source;
        _appliedMask = mask;
        _appliedSearch = _search;
        _appliedCategory = _categoryIndex;
        _selected = -1;
        _visible.Clear();
        string? category = _categoryIndex >= 0 && _categoryIndex < _categories.Count ? _categories[_categoryIndex] : null;
        foreach (var issue in source)
        {
            if (!Passes(issue.Severity, mask))
                continue;
            if (category is not null && issue.Category != category)
                continue;
            if (_search.Length > 0 && !issue.Message.Contains(_search, StringComparison.CurrentCultureIgnoreCase) &&
                !issue.Code.Contains(_search, StringComparison.OrdinalIgnoreCase))
                continue;
            _visible.Add(issue);
        }
    }

    private static bool Passes(IssueSeverity severity, int mask) => severity switch
    {
        IssueSeverity.Error => (mask & 1) != 0,
        IssueSeverity.Warning => (mask & 2) != 0,
        _ => (mask & 4) != 0,
    };

    /// <summary>Icon and colour of a severity.</summary>
    public static (string Icon, Vector4 Color) SeverityStyle(IssueSeverity severity) => severity switch
    {
        IssueSeverity.Error => (Icons.OctagonAlert, Theme.Error),
        IssueSeverity.Warning => (Icons.TriangleAlert, Theme.Warning),
        _ => (Icons.Info, Theme.Note),
    };
}
