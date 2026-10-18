namespace Candela.App.Files;

/// <summary>
/// A named file filter of the file browser ("Scenes (Candela, MVR)" → <c>*.candela;*.mvr</c>). Patterns are matched
/// case-insensitively against the file name; <c>*</c> matches any run of characters and <c>?</c> one character.
/// An empty pattern list or <c>*</c> / <c>*.*</c> matches every file.
/// </summary>
internal sealed class FileFilter
{
    /// <summary>Creates a filter.</summary>
    public FileFilter(string name, params string[] patterns)
    {
        Name = name ?? "";
        var list = (patterns ?? Array.Empty<string>())
            .SelectMany(p => p.Split(new[] { ';', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(p => p.Length > 0)
            .ToArray();
        Patterns = list;
        MatchesAll = list.Length == 0 || list.Any(p => p is "*" or "*.*");
    }

    /// <summary>Display name.</summary>
    public string Name { get; }

    /// <summary>Wildcard patterns ("*.mvr").</summary>
    public IReadOnlyList<string> Patterns { get; }

    /// <summary>True when the filter accepts every file.</summary>
    public bool MatchesAll { get; }

    /// <summary>The extension the first pattern implies (".candela"), used to complete typed save names; null for "all files".</summary>
    public string? DefaultExtension
    {
        get
        {
            if (MatchesAll)
                return null;
            var p = Patterns[0];
            return p.StartsWith("*.", StringComparison.Ordinal) && p.IndexOfAny(new[] { '*', '?' }, 2) < 0 ? p[1..] : null;
        }
    }

    /// <summary>Text for the filter dropdown ("MVR scenes (*.mvr)").</summary>
    public string Label => MatchesAll ? Name : $"{Name} ({string.Join(", ", Patterns)})";

    /// <summary>Parses "Name|*.a;*.b" (the part after '|' are the patterns; without '|' the whole text is patterns).</summary>
    public static FileFilter Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        int bar = text.IndexOf('|');
        return bar < 0 ? new FileFilter(text, text) : new FileFilter(text[..bar].Trim(), text[(bar + 1)..]);
    }

    /// <summary>True when <paramref name="fileName"/> (name only, no folder) matches.</summary>
    public bool Matches(string fileName)
    {
        if (MatchesAll)
            return true;
        if (string.IsNullOrEmpty(fileName))
            return false;
        foreach (var p in Patterns)
            if (Wildcard(p, fileName))
                return true;
        return false;
    }

    /// <summary>Case-insensitive wildcard match with '*' and '?' (iterative, linear in practice).</summary>
    internal static bool Wildcard(string pattern, string text)
    {
        int p = 0, t = 0, starP = -1, starT = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(text[t])))
            {
                p++;
                t++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                starP = p++;
                starT = t;
            }
            else if (starP >= 0)
            {
                p = starP + 1;
                t = ++starT;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
            p++;
        return p == pattern.Length;
    }
}
