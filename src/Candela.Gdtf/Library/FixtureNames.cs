using System.Text;

namespace Candela.Gdtf;

/// <summary>
/// Name normalisation used to match fixtures by human-written manufacturer and model names (MVR files that reference
/// stub GDTFs, user search): case, spaces vs underscores and punctuation are ignored, and manufacturer names are reduced
/// to their core ("Martin Professional" → "martin", "Robe Lighting" → "robe", "Clay_Paky" → "claypaky").
/// </summary>
public static class FixtureNames
{
    /// <summary>
    /// Trailing words of company names that do not identify the brand. They are removed from the end of a
    /// manufacturer name (repeatedly) to get its core, unless nothing would remain.
    /// </summary>
    private static readonly HashSet<string> CompanySuffixes = new(StringComparer.Ordinal)
    {
        "lighting", "light", "lights", "professional", "pro", "systems", "system", "technology", "technologies",
        "led", "laser", "inc", "ltd", "llc", "gmbh", "co", "corp", "corporation", "company", "srl", "spa", "sa", "ag",
        "bv", "international", "intl", "group", "electronics", "official",
    };

    /// <summary>Brand names whose short and long spellings share no core (keys are normalised).</summary>
    private static readonly Dictionary<string, string> ManufacturerAliases = new(StringComparer.Ordinal)
    {
        ["americandj"] = "adj",
        ["adjeliminatorlighting"] = "adj",
        ["eliminatorlighting"] = "adj",
        ["electronictheatrecontrols"] = "etc",
        ["germanlightproducts"] = "glp",
        ["hes"] = "highend",
    };

    /// <summary>
    /// Words that describe a fixture class rather than a product family; sharing only these does not make two models
    /// similar (see <see cref="ModelSimilarity"/>).
    /// </summary>
    private static readonly HashSet<string> GenericModelWords = new(StringComparer.Ordinal)
    {
        "led", "wash", "spot", "beam", "profile", "par", "bar", "pro", "plus", "mk", "mkii", "mkiii", "ii", "iii", "iv",
        "fx", "ip", "hybrid", "zoom", "rgb", "rgbw", "rgba", "rgbaw", "cw", "ww", "tw", "color", "colour", "white", "mini",
        "max", "xl", "xs", "light", "moving", "head", "the", "and", "of", "with", "series", "version", "new", "compact",
        "pixel", "fs", "hc", "hp", "outdoor", "indoor", "mode", "fixture",
    };

    /// <summary>
    /// Lower-case letters and digits of <paramref name="text"/> only ("Robin_T1 Profile" → "robint1profile"); the key
    /// two names share when they differ only in case, separators or punctuation. A <c>+</c> is a word of product names,
    /// not punctuation, and is spelled out as "plus" the way file names write it ("Robin Spikie+" →
    /// "robinspikieplus" = "Robin_SpikiePlus", "SIX+ BAR L" = "SIX_plus_BAR_L"), so "Spikie+" and "Spikie" stay apart.
    /// </summary>
    public static string Key(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            else if (c == '+') sb.Append("plus");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Words of a name in lower case: split at anything that is not a letter or digit and at lower-to-upper case
    /// changes ("MagicBlade R" → magic, blade, r); a <c>+</c> is the word "plus" (see <see cref="Key"/>).
    /// </summary>
    public static IReadOnlyList<string> Words(string? text)
    {
        var words = new List<string>();
        if (string.IsNullOrEmpty(text)) return words;
        var sb = new StringBuilder();
        char prev = '\0';
        foreach (char c in text)
        {
            if (!char.IsLetterOrDigit(c))
            {
                Flush();
                if (c == '+') words.Add("plus");
            }
            else
            {
                if (sb.Length > 0 && char.IsLower(prev) && char.IsUpper(c)) Flush();
                sb.Append(char.ToLowerInvariant(c));
            }
            prev = c;
        }
        Flush();
        return words;

        void Flush()
        {
            if (sb.Length > 0) words.Add(sb.ToString());
            sb.Clear();
        }
    }

    /// <summary>
    /// The core key of a manufacturer name: company suffixes ("Lighting", "Professional", "Systems", …) removed from the
    /// end and known aliases applied, so "Martin", "Martin_Professional" and "MARTIN PROFESSIONAL" all give "martin".
    /// </summary>
    public static string ManufacturerKey(string? manufacturer)
    {
        if (string.IsNullOrWhiteSpace(manufacturer)) return "";

        // "ADJ (Eliminator Lighting)": a parenthesised remark is not part of the brand.
        int paren = manufacturer.IndexOf('(');
        if (paren > 0)
        {
            var head = ManufacturerKey(manufacturer[..paren]);
            if (head.Length > 0) return head;
        }

        var words = new List<string>();
        foreach (var part in manufacturer.Split(SeparatorChars, StringSplitOptions.RemoveEmptyEntries))
        {
            var k = Key(part);
            if (k.Length > 0) words.Add(k);
        }
        while (words.Count > 1 && CompanySuffixes.Contains(words[^1])) words.RemoveAt(words.Count - 1);
        string key = string.Concat(words);
        if (key.Length == 0) key = Key(manufacturer);
        return ManufacturerAliases.TryGetValue(key, out var alias) ? alias : key;
    }

    private static readonly char[] SeparatorChars = { ' ', '_', '-', '.', ',', '/', '\\', '&', '+', '\t' };

    /// <summary>
    /// Similarity of two model names in 0 … 1 for choosing a substitute when the exact model is not in the library:
    /// a weighted word overlap (product-family words count fully, class words like "Wash" or "LED", single letters and
    /// numbers count little) blended with the character-bigram overlap of the keys. Returns 0 unless the names share at
    /// least one family word of three or more letters, so models that are merely of the same class never match.
    /// </summary>
    public static double ModelSimilarity(string? a, string? b)
    {
        var wa = Words(a);
        var wb = Words(b);
        if (wa.Count == 0 || wb.Count == 0) return 0;
        var setA = new HashSet<string>(wa, StringComparer.Ordinal);
        var setB = new HashSet<string>(wb, StringComparer.Ordinal);
        bool distinctiveShared = false;
        double shared = 0, union = 0;
        foreach (var w in setA)
        {
            double weight = WordWeight(w);
            union += weight;
            if (!setB.Contains(w)) continue;
            shared += weight;
            if (weight >= 1) distinctiveShared = true;
        }
        foreach (var w in setB)
            if (!setA.Contains(w)) union += WordWeight(w);
        if (!distinctiveShared || union <= 0) return 0;
        double words = shared / union;
        return 0.6 * words + 0.4 * BigramDice(Key(a), Key(b));
    }

    /// <summary>1 for family words, 0.3 for class words, single letters and pure numbers.</summary>
    private static double WordWeight(string word)
    {
        if (word.Length < 3 || GenericModelWords.Contains(word)) return 0.3;
        foreach (char c in word)
            if (!char.IsDigit(c)) return 1;
        return 0.3;
    }

    private static double BigramDice(string a, string b)
    {
        if (a.Length < 2 || b.Length < 2) return a == b && a.Length > 0 ? 1 : 0;
        var counts = new Dictionary<int, int>();
        for (int i = 0; i + 1 < a.Length; i++)
        {
            int k = (a[i] << 16) | a[i + 1];
            counts[k] = counts.GetValueOrDefault(k) + 1;
        }
        int common = 0;
        for (int i = 0; i + 1 < b.Length; i++)
        {
            int k = (b[i] << 16) | b[i + 1];
            if (counts.TryGetValue(k, out int n) && n > 0)
            {
                counts[k] = n - 1;
                common++;
            }
        }
        return 2.0 * common / (a.Length - 1 + b.Length - 1);
    }
}
