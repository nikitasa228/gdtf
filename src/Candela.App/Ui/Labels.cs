namespace Candela.App.Ui;

/// <summary>
/// "icon  text" labels for buttons, tabs and headers, built once per language and then reused, so drawing a panel
/// every frame does not allocate strings. Main (UI) thread only.
/// </summary>
internal static class Labels
{
    private static readonly Dictionary<(string Icon, Str Key, string Suffix), string> s_cache = new();
    private static string s_language = "";

    /// <summary>The icon followed by the localised text of <paramref name="key"/>.</summary>
    public static string With(string icon, Str key) => With(icon, key, "");

    /// <summary>The icon, the localised text and an ImGui id suffix such as <c>"##search"</c>.</summary>
    public static string With(string icon, Str key, string suffix)
    {
        if (!string.Equals(s_language, Loc.Language, StringComparison.Ordinal))
        {
            s_cache.Clear();
            s_language = Loc.Language;
        }

        if (!s_cache.TryGetValue((icon, key, suffix), out var label))
        {
            label = icon.Length > 0 ? icon + "  " + Loc.Get(key) + suffix : Loc.Get(key) + suffix;
            s_cache[(icon, key, suffix)] = label;
        }

        return label;
    }
}
