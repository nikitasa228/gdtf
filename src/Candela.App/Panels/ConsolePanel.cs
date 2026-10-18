using System.Globalization;
using System.Numerics;
using Candela.App.Diagnostics;
using Candela.App.Docking;
using Candela.App.Shell;
using Candela.App.Ui;
using Hexa.NET.ImGui;

namespace Candela.App.Panels;

/// <summary>The editor log (program messages, GPU and renderer messages, errors caught by the frame loop).</summary>
internal sealed class ConsolePanel : Panel
{
    private readonly WorkspaceContext _ctx;
    private readonly List<LogEntry> _entries = new();
    private readonly List<int> _visible = new();
    private readonly List<string> _lines = new();
    private long _version = -1;
    private string _filter = "";
    private string _appliedFilter = "";
    private bool _showDebug;
    private bool _appliedDebug;
    private bool _autoScroll = true;
    private bool _scrollToEnd;

    /// <summary>Creates the panel.</summary>
    public ConsolePanel(WorkspaceContext context)
        : base("Console", Icons.Terminal)
    {
        _ctx = context ?? throw new ArgumentNullException(nameof(context));
    }

    /// <inheritdoc/>
    public override string Title => Loc.Get(Str.PanelConsole);

    /// <inheritdoc/>
    public override Vector2 DefaultSize => new(640, 240);

    /// <inheritdoc/>
    public override void Draw()
    {
        var log = _ctx.Log;
        float scale = ImGui.GetStyle().FontScaleMain * ImGui.GetStyle().FontScaleDpi;
        if (ImGui.Button(Labels.With(Icons.Trash, Str.ConsoleClear)))
            log.Clear();
        ImGui.SameLine();
        ImGui.Checkbox(Loc.Get(Str.ConsoleAutoScroll), ref _autoScroll);
        ImGui.SameLine();
        ImGui.Checkbox(Loc.Get(Str.ConsoleDebug), ref _showDebug);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(MathF.Max(120 * scale, ImGui.GetContentRegionAvail().X));
        ImGui.InputTextWithHint("##filter", Labels.With(Icons.Filter, Str.ConsoleFilter), ref _filter, 128);
        ImGui.Separator();

        if (log.Version != _version || _filter != _appliedFilter || _showDebug != _appliedDebug)
        {
            _version = log.Version;
            _appliedFilter = _filter;
            _appliedDebug = _showDebug;
            log.CopyTo(_entries);
            _visible.Clear();
            _lines.Clear();
            for (int i = 0; i < _entries.Count; i++)
            {
                var e = _entries[i];
                if (e.Level == LogLevel.Debug && !_showDebug)
                    continue;
                if (_filter.Length > 0 && !e.Message.Contains(_filter, StringComparison.CurrentCultureIgnoreCase) &&
                    !e.Source.Contains(_filter, StringComparison.OrdinalIgnoreCase))
                    continue;
                _visible.Add(i);
                _lines.Add(string.Create(CultureInfo.InvariantCulture, $"{e.Time:HH:mm:ss}  [{e.Source}]  {e.Message}"));
            }

            _scrollToEnd = _autoScroll;
        }

        if (!ImGui.BeginChild("##log", Vector2.Zero, ImGuiChildFlags.None, ImGuiWindowFlags.HorizontalScrollbar))
        {
            ImGui.EndChild();
            return;
        }

        unsafe
        {
            var clipper = new ImGuiListClipper();
            clipper.Begin(_visible.Count);
            while (clipper.Step())
            {
                for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                {
                    var e = _entries[_visible[i]];
                    var color = e.Level switch
                    {
                        LogLevel.Error => Theme.Error,
                        LogLevel.Warning => Theme.Warning,
                        LogLevel.Debug => Theme.WithAlpha(Theme.TextMuted, 0.7f),
                        _ => Theme.Text,
                    };
                    ImGui.PushStyleColor(ImGuiCol.Text, color);
                    ImGui.TextUnformatted(_lines[i]);
                    ImGui.PopStyleColor();
                }
            }

            clipper.End();
        }

        if (_scrollToEnd)
        {
            ImGui.SetScrollHereY(1f);
            _scrollToEnd = false;
        }

        ImGui.EndChild();
    }
}
