using Candela.App.Ui;
using Hexa.NET.ImGui;

namespace Candela.App.Shell;

/// <summary>
/// Thin wrapper over ImGui menus that also lets scripts select an item by its label path ("File &gt; Settings…"): while
/// a scripted path is pending, the menu code runs through the matching submenus without opening them and the matching
/// item reports a click — so scripted menu selections exercise exactly the code a mouse click would. Labels match the
/// current language, English, or the string key name, ignoring case, icons and a trailing ellipsis.
/// </summary>
internal sealed class MenuScope
{
    private readonly Stack<bool> _realOpen = new();
    private IReadOnlyList<string>? _path;
    private int _depth;

    /// <summary>True when the innermost begun menu is really open (items drawn now are on screen).</summary>
    public bool IsRealOpen => _realOpen.Count == 0 || _realOpen.Peek();

    /// <summary>True while a scripted selection is pending.</summary>
    public bool HasPending => _path is not null;

    /// <summary>The last path that matched an item (tests).</summary>
    public string? LastMatched { get; private set; }

    /// <summary>Requests the item at <paramref name="path"/> to be selected during the next menu drawing.</summary>
    public void Request(IReadOnlyList<string> path)
    {
        _path = path is { Count: > 0 } ? path : null;
        _depth = 0;
    }

    /// <summary>Starts drawing the menu bar (resets the traversal depth).</summary>
    public void BeginBar() => _depth = 0;

    /// <summary>Ends the menu bar; returns an unmatched scripted path (which is dropped), or null.</summary>
    public string? EndBar()
    {
        if (_path is null)
            return null;
        string missed = string.Join(" > ", _path);
        _path = null;
        return missed;
    }

    /// <summary>Begins a submenu; true when its items should be drawn (really open, or on a scripted path).</summary>
    public bool Begin(Str key, string icon = "", bool enabled = true) => BeginCore(Labels.With(icon, key), key, null, null, enabled);

    /// <summary>Begins a submenu with a dynamic label.</summary>
    public bool Begin(string label, string? keyName = null, string? english = null, bool enabled = true) =>
        BeginCore(label, null, keyName, english, enabled);

    private bool BeginCore(string label, Str? key, string? keyName, string? english, bool enabled)
    {
        bool insideReal = _realOpen.Count == 0 || _realOpen.Peek();
        bool real = insideReal && ImGui.BeginMenu(label, enabled);
        bool onPath = _path is not null && _depth == _realOpen.Count && _depth < _path.Count - 1 && MatchesAt(_depth, label, key, keyName, english);
        bool scripted = onPath && (real || enabled);
        if (!real && !scripted)
            return false;
        _realOpen.Push(real);
        if (scripted)
            _depth++;
        return true;
    }

    /// <summary>Ends a submenu begun with <see cref="Begin(Str, string, bool)"/> (only when it returned true).</summary>
    public void End()
    {
        bool real = _realOpen.Pop();
        if (real)
            ImGui.EndMenu();
        if (_path is not null && _depth > _realOpen.Count)
            _depth = _realOpen.Count;
    }

    /// <summary>A menu item; true when clicked or selected by a script.</summary>
    public bool Item(Str key, string icon = "", string shortcut = "", bool selected = false, bool enabled = true) =>
        ItemCore(Labels.With(icon, key), shortcut, selected, enabled, key, null, null);

    /// <summary>A menu item with a dynamic label.</summary>
    public bool Item(string label, string shortcut, bool selected, bool enabled, string? keyName = null, string? english = null) =>
        ItemCore(label, shortcut, selected, enabled, null, keyName, english);

    private bool ItemCore(string label, string shortcut, bool selected, bool enabled, Str? key, string? keyName, string? english)
    {
        bool insideReal = _realOpen.Count == 0 || _realOpen.Peek();
        bool clicked = insideReal && ImGui.MenuItem(label, shortcut, selected, enabled);
        if (_path is not null && _depth == _path.Count - 1 && _depth == _realOpen.Count && MatchesAt(_depth, label, key, keyName, english))
        {
            if (enabled)
                clicked = true;
            LastMatched = string.Join(" > ", _path);
            _path = null;
        }

        return clicked;
    }

    /// <summary>A separator (only inside really open menus).</summary>
    public void Separator()
    {
        if (_realOpen.Count == 0 || _realOpen.Peek())
            ImGui.Separator();
    }

    // Key names are only built while a scripted path is being matched (Enum.ToString allocates).
    private bool MatchesAt(int depth, string label, Str? key, string? keyName, string? english) =>
        key is { } k ? Matches(_path![depth], label, k.ToString(), Loc.English_(k)) : Matches(_path![depth], label, keyName, english);

    /// <summary>Normalised comparison used for scripted labels.</summary>
    internal static bool Matches(string wanted, string label, string? keyName, string? english)
    {
        string w = Normalize(wanted);
        return w == Normalize(label) || (keyName is not null && w == Normalize(keyName)) || (english is not null && w == Normalize(english));
    }

    /// <summary>Lower-case text without icon glyphs, ellipsis and surrounding spaces.</summary>
    internal static string Normalize(string s)
    {
        var chars = new List<char>(s.Length);
        foreach (char c in s)
        {
            if (c >= Icons.RangeFirst && c <= Icons.RangeLast)
                continue;
            if (c == '…')
                continue;
            chars.Add(char.ToLowerInvariant(c));
        }

        string t = new string(chars.ToArray()).Trim();
        while (t.EndsWith("...", StringComparison.Ordinal))
            t = t[..^3].TrimEnd();
        int hash = t.IndexOf("##", StringComparison.Ordinal);
        return hash >= 0 ? t[..hash].Trim() : t;
    }

}
