namespace Candela.App.Ui;

/// <summary>Shortening of single-line texts that do not fit their space (status bar messages, long names).</summary>
internal static class TextFit
{
    /// <summary>The character appended to a shortened text.</summary>
    public const string Ellipsis = "…";

    /// <summary>
    /// <paramref name="text"/> itself when <paramref name="measure"/> says it fits <paramref name="maxWidth"/> pixels;
    /// otherwise its longest prefix that fits together with an ellipsis (never cut inside a surrogate pair, trailing
    /// spaces dropped), or an empty string when not even the ellipsis fits. Binary search: O(log n) measurements.
    /// </summary>
    public static string Truncate(string text, float maxWidth, Func<string, float> measure)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(measure);
        if (text.Length == 0 || measure(text) <= maxWidth)
            return text;
        if (!(measure(Ellipsis) <= maxWidth))
            return "";
        int lo = 0, hi = text.Length - 1; // longest prefix length known to fit is lo
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (measure(Candidate(text, mid)) <= maxWidth)
                lo = mid;
            else
                hi = mid - 1;
        }

        return Candidate(text, lo);
    }

    private static string Candidate(string text, int length)
    {
        if (length > 0 && char.IsHighSurrogate(text[length - 1]))
            length--;
        return string.Concat(text.AsSpan(0, length).TrimEnd(), Ellipsis);
    }
}

/// <summary>
/// A text shortened to a width with <see cref="TextFit.Truncate"/>, recomputed only when the text or the width changes,
/// so drawing it every frame does not allocate.
/// </summary>
internal sealed class FittedText
{
    private static readonly Func<string, float> Measure = static s => Hexa.NET.ImGui.ImGui.CalcTextSize(s).X;
    private string? _source;
    private float _width = float.NaN;
    private float _fontSize = float.NaN;
    private string _result = "";

    /// <summary>True when the last result is shorter than its source.</summary>
    public bool IsShortened { get; private set; }

    /// <summary><paramref name="text"/> fitted to <paramref name="maxWidth"/> pixels in the current font.</summary>
    public string Get(string text, float maxWidth)
    {
        ArgumentNullException.ThrowIfNull(text);
        float fontSize = Hexa.NET.ImGui.ImGui.GetFontSize();
        if (ReferenceEquals(text, _source) && MathF.Abs(maxWidth - _width) < 0.5f && fontSize == _fontSize)
            return _result;
        _source = text;
        _width = maxWidth;
        _fontSize = fontSize;
        _result = TextFit.Truncate(text, maxWidth, Measure);
        IsShortened = !ReferenceEquals(_result, text);
        return _result;
    }
}
