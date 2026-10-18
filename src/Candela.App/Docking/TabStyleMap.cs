using System.Globalization;

namespace Candela.App.Docking;

/// <summary>How the panels of one dock node (a group of tabbed panels) are switched (requirement: "tabs on top with
/// icon + name, or icons-only column on the left; a setting in the corner").</summary>
internal enum TabStyle
{
    /// <summary>ImGui's tab bar on top; each tab shows the panel's icon and name.</summary>
    TopTabs,

    /// <summary>No tab bar; a vertical column of panel icons on the left edge of the group.</summary>
    LeftIcons,
}

/// <summary>The tab style of a dock node and, for the icon column, the panel shown.</summary>
/// <param name="Style">The style.</param>
/// <param name="SelectedPanel">Panel id shown in an icon-column node (null = the first panel).</param>
internal readonly record struct TabStyleState(TabStyle Style, string? SelectedPanel = null);

/// <summary>
/// Tab style per dock node id, stored in the layout file. Nodes not in the map use <see cref="TabStyle.TopTabs"/>.
/// Text form (one node per line, stable across versions): <c>0x1A2B3C4D LeftIcons Properties</c>.
/// </summary>
internal sealed class TabStyleMap
{
    private readonly Dictionary<uint, TabStyleState> _nodes = new();

    /// <summary>Number of nodes with a stored state.</summary>
    public int Count => _nodes.Count;

    /// <summary>The node ids with a stored state.</summary>
    public IEnumerable<uint> NodeIds => _nodes.Keys;

    /// <summary>Raised when a style or selection changes (the workspace marks the layout as modified).</summary>
    public event Action? Changed;

    /// <summary>The style of <paramref name="nodeId"/> (top tabs when unknown).</summary>
    public TabStyle StyleOf(uint nodeId) => _nodes.TryGetValue(nodeId, out var s) ? s.Style : TabStyle.TopTabs;

    /// <summary>The full state of a node, or the default.</summary>
    public TabStyleState Get(uint nodeId) => _nodes.TryGetValue(nodeId, out var s) ? s : new TabStyleState(TabStyle.TopTabs);

    /// <summary>Sets the style of a node (keeps the selected panel).</summary>
    public void SetStyle(uint nodeId, TabStyle style)
    {
        if (nodeId == 0)
            return;
        var current = Get(nodeId);
        if (current.Style == style && _nodes.ContainsKey(nodeId))
            return;
        Set(nodeId, current with { Style = style });
    }

    /// <summary>Remembers which panel an icon-column node shows.</summary>
    public void SetSelected(uint nodeId, string? panelId)
    {
        if (nodeId == 0)
            return;
        var current = Get(nodeId);
        if (current.SelectedPanel == panelId && _nodes.ContainsKey(nodeId))
            return;
        Set(nodeId, current with { SelectedPanel = panelId });
    }

    /// <summary>Copies the state of one node to another (a node that was split or re-created).</summary>
    public void Transfer(uint fromNode, uint toNode)
    {
        if (fromNode == toNode || !_nodes.TryGetValue(fromNode, out var s))
            return;
        Set(toNode, s);
    }

    /// <summary>Forgets nodes that no longer exist (before saving).</summary>
    public void Prune(Func<uint, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(exists);
        List<uint>? dead = null;
        foreach (var id in _nodes.Keys)
            if (!exists(id))
                (dead ??= new List<uint>()).Add(id);
        if (dead is null)
            return;
        foreach (var id in dead)
            _nodes.Remove(id);
        Changed?.Invoke();
    }

    /// <summary>Removes every entry.</summary>
    public void Clear()
    {
        if (_nodes.Count == 0)
            return;
        _nodes.Clear();
        Changed?.Invoke();
    }

    /// <summary>Text form: one line per node that is not plain top tabs or has a selection.</summary>
    public string Serialize()
    {
        var lines = new List<string>();
        foreach (var (id, state) in _nodes.OrderBy(kv => kv.Key))
        {
            string line = string.Create(CultureInfo.InvariantCulture, $"0x{id:X8} {state.Style}");
            if (!string.IsNullOrEmpty(state.SelectedPanel))
                line += " " + state.SelectedPanel;
            lines.Add(line);
        }

        return string.Join("\n", lines);
    }

    /// <summary>
    /// Parses the text form. Malformed lines are skipped (a damaged layout must not stop the editor); the number of
    /// skipped lines is returned through <paramref name="skipped"/>.
    /// </summary>
    public static TabStyleMap Parse(string? text, out int skipped)
    {
        var map = new TabStyleMap();
        skipped = 0;
        if (string.IsNullOrWhiteSpace(text))
            return map;
        foreach (var raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 2 || !TryParseId(parts[0], out uint id) || !Enum.TryParse(parts[1], ignoreCase: true, out TabStyle style) ||
                !Enum.IsDefined(style))
            {
                skipped++;
                continue;
            }

            string? selected = parts.Length > 2 && parts[2].Length > 0 ? parts[2] : null;
            map._nodes[id] = new TabStyleState(style, selected);
        }

        return map;
    }

    private static bool TryParseId(string s, out uint id)
    {
        id = 0;
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return uint.TryParse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out id) && id != 0;
        return uint.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out id) && id != 0;
    }

    private void Set(uint nodeId, TabStyleState state)
    {
        _nodes[nodeId] = state;
        Changed?.Invoke();
    }
}
