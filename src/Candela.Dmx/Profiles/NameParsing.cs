using System.Globalization;
using System.Numerics;
using Candela.Photometry;

namespace Candela.Dmx;

/// <summary>Meaning of a channel set name, as far as DMX evaluation needs it (see <see cref="NameParsing.Flags"/>).</summary>
[Flags]
public enum ChannelSetFlags
{
    /// <summary>Nothing recognised.</summary>
    None = 0,

    /// <summary>"Open", "Shutter open", "LEDs on", "Open (2)" …: the shutter, iris, wheel or effect is open/neutral.</summary>
    Open = 1,

    /// <summary>"Closed", "Close", "Blackout", "Off", "LEDs off": the shutter is closed / the output is off.</summary>
    Closed = 2,

    /// <summary>"No function", "Idle", "No effect", "Stop", "Reserved": the range does nothing.</summary>
    NoFunction = 4,

    /// <summary>The range inverts pan ("Pan reverse", "Invert pan on", …).</summary>
    InvertPan = 8,

    /// <summary>The range inverts tilt.</summary>
    InvertTilt = 16,

    /// <summary>
    /// A wheel selection range that takes the wheel, prism, animation or effect out of the beam ("Open", "Off",
    /// "Removed", "No prism", "Closed" …, selecting no slot or an open one). Set while a profile is compiled.
    /// </summary>
    WheelOut = 32,

    /// <summary>
    /// A wheel selection range that moves a single-slot mechanism (animation wheel, prism) gradually into the beam
    /// from the "out" range before it: an unnamed range after "Open", "Open to Dots", "Enter in proportion". Insertion
    /// runs 0 → 1 across the range. Set while a profile is compiled.
    /// </summary>
    InsertionRamp = 64,

    /// <summary>
    /// A wheel selection range in which the mechanism keeps moving in and out of the beam between two insertions
    /// ("Ramping from open to full position max … min": Robe animation wheels), at a rate that runs across the range.
    /// Set while a profile is compiled.
    /// </summary>
    InsertionCycle = 128,
}

/// <summary>
/// Interprets the free-text names manufacturers give to channel sets and wheel slots: colour names and colour
/// temperatures (for colour macros and CTO/CTC ranges without physical kelvin values), open/closed states, invert
/// switches, dimmer curves, strobe waveforms and prism facet counts. All parsing happens once while a
/// <see cref="FixtureProfile"/> is compiled, never per frame.
/// </summary>
public static class NameParsing
{
    // (keyword, x, y): approximate chromaticities of the colours stage-lighting names refer to (gel-like colours of a
    // white source). Longer keywords win, so "light blue" beats "blue".
    private static readonly (string Key, float X, float Y)[] _colors =
    [
        ("deep red", 0.700f, 0.290f),
        ("dark red", 0.690f, 0.300f),
        ("primary red", 0.680f, 0.310f),
        ("medium red", 0.650f, 0.320f),
        ("light red", 0.580f, 0.330f),
        ("red", 0.670f, 0.315f),
        ("scarlet", 0.660f, 0.320f),
        ("crimson", 0.640f, 0.300f),
        ("dark orange", 0.610f, 0.380f),
        ("orange", 0.585f, 0.400f),
        ("dark amber", 0.600f, 0.390f),
        ("deep amber", 0.600f, 0.390f),
        ("medium amber", 0.570f, 0.410f),
        ("light amber", 0.500f, 0.420f),
        ("pale amber", 0.470f, 0.420f),
        ("gold amber", 0.560f, 0.420f),
        ("amber", 0.570f, 0.415f),
        ("pale gold", 0.490f, 0.430f),
        ("gold", 0.520f, 0.440f),
        ("straw", 0.450f, 0.430f),
        ("bastard amber", 0.470f, 0.400f),
        ("peach", 0.460f, 0.380f),
        ("salmon", 0.500f, 0.360f),
        ("apricot", 0.500f, 0.400f),
        ("medium yellow", 0.490f, 0.480f),
        ("light yellow", 0.420f, 0.450f),
        ("spring yellow", 0.450f, 0.490f),
        ("yellow", 0.485f, 0.485f),
        ("lime", 0.400f, 0.540f),
        ("chartreuse", 0.370f, 0.570f),
        ("light green", 0.300f, 0.550f),
        ("dark green", 0.180f, 0.700f),
        ("primary green", 0.200f, 0.700f),
        ("moss green", 0.300f, 0.580f),
        ("green", 0.210f, 0.680f),
        ("mint", 0.260f, 0.420f),
        ("emerald", 0.200f, 0.550f),
        ("turquoise", 0.170f, 0.400f),
        ("teal", 0.160f, 0.380f),
        ("aqua", 0.180f, 0.360f),
        ("cyan", 0.160f, 0.340f),
        ("light blue", 0.220f, 0.250f),
        ("steel blue", 0.230f, 0.260f),
        ("sky blue", 0.200f, 0.240f),
        ("medium blue", 0.160f, 0.100f),
        ("dark blue", 0.155f, 0.050f),
        ("deep blue", 0.155f, 0.045f),
        ("congo blue", 0.165f, 0.035f),
        ("royal blue", 0.150f, 0.060f),
        ("primary blue", 0.150f, 0.060f),
        ("blue", 0.150f, 0.070f),
        ("indigo", 0.170f, 0.030f),
        ("lavender", 0.250f, 0.200f),
        ("light lavender", 0.270f, 0.240f),
        ("purple", 0.230f, 0.100f),
        ("violet", 0.200f, 0.060f),
        ("uv", 0.170f, 0.010f),
        ("ultraviolet", 0.170f, 0.010f),
        ("black light", 0.170f, 0.010f),
        ("deep magenta", 0.400f, 0.160f),
        ("dark magenta", 0.400f, 0.160f),
        ("magenta", 0.380f, 0.170f),
        ("fuchsia", 0.400f, 0.190f),
        ("dark pink", 0.450f, 0.240f),
        ("medium pink", 0.420f, 0.270f),
        ("light pink", 0.380f, 0.300f),
        ("pink", 0.420f, 0.280f),
        ("rose", 0.450f, 0.280f),
        ("rosy", 0.420f, 0.300f),
        ("coral", 0.520f, 0.340f),
    ];

    private static readonly (string Key, float Kelvin)[] _whites =
    [
        ("warm white", 3000f),
        ("cool white", 6500f),
        ("cold white", 6500f),
        ("neutral white", 4000f),
        ("daylight", 5600f),
        ("tungsten", 3200f),
        ("full ct orange", 3200f),
        ("full cto", 3200f),
        ("half cto", 4300f),
        ("full ct blue", 5600f),
        ("full ctb", 5600f),
        ("white", 6500f),
    ];

    /// <summary>
    /// Colour named by <paramref name="name"/> ("Light Blue", "Congo blue", "Lee 106 Primary Red", "3200K", "Warm White"),
    /// as CIE 1931 xy; false when the name does not name a single colour (effects, macros, numbers, "Open").
    /// </summary>
    public static bool TryParseColor(string? name, out Vector2 xy)
    {
        xy = default;
        if (string.IsNullOrWhiteSpace(name)) return false;
        string lower = name.ToLowerInvariant();
        if (ContainsAny(lower, "effect", "chase", "rainbow", "random", "fade", "program", "auto", "sound", "speed", "cycle", "scroll", "macro", "split", "mix"))
            return false;
        if (TryParseKelvin(lower, out float k))
        {
            xy = Cct.XyFromCct(k);
            return true;
        }
        int bestLength = 0;
        foreach (var (key, x, y) in _colors)
        {
            if (key.Length > bestLength && ContainsWord(lower, key))
            {
                bestLength = key.Length;
                xy = new Vector2(x, y);
            }
        }
        foreach (var (key, kelvin) in _whites)
        {
            if (key.Length > bestLength && ContainsWord(lower, key))
            {
                bestLength = key.Length;
                xy = Cct.XyFromCct(kelvin);
            }
        }
        return bestLength > 0;
    }

    /// <summary>
    /// A colour temperature written in a name: "3200K", "5600 k", "2800k-2900k" (the first value), "CTO 3200". Values
    /// outside 1000–25000 K are rejected.
    /// </summary>
    public static bool TryParseKelvin(string? name, out float kelvin)
    {
        kelvin = float.NaN;
        if (string.IsNullOrEmpty(name)) return false;
        var s = name.AsSpan();
        for (int i = 0; i < s.Length; i++)
        {
            if (!char.IsAsciiDigit(s[i]) || (i > 0 && char.IsAsciiDigit(s[i - 1]))) continue;
            int j = i;
            while (j < s.Length && char.IsAsciiDigit(s[j])) j++;
            int digits = j - i;
            int k = j;
            while (k < s.Length && s[k] == ' ') k++;
            bool hasK = k < s.Length && (s[k] == 'k' || s[k] == 'K') && (k + 1 >= s.Length || !char.IsLetter(s[k + 1]));
            if (digits is >= 4 and <= 5 && hasK &&
                float.TryParse(s[i..j], NumberStyles.None, CultureInfo.InvariantCulture, out float v) && v is >= 1000f and <= 25000f)
            {
                kelvin = v;
                return true;
            }
            i = j;
        }
        return false;
    }

    /// <summary>Flags of a channel set name; <paramref name="attributeKind"/> decides how "open"/"off" are read.</summary>
    public static ChannelSetFlags Flags(string? name, AttributeKind attributeKind)
    {
        if (string.IsNullOrWhiteSpace(name)) return ChannelSetFlags.None;
        string s = name.ToLowerInvariant();
        var flags = ChannelSetFlags.None;
        bool negated = ContainsWord(s, "off") || ContainsWord(s, "normal") || ContainsWord(s, "disable") || ContainsWord(s, "disabled");
        if (ContainsWord(s, "invert") || ContainsWord(s, "inverted") || ContainsWord(s, "reverse") || ContainsWord(s, "resverse"))
        {
            bool invertOn = !negated || ContainsWord(s, "on");
            if (invertOn && !(ContainsWord(s, "on") && ContainsWord(s, "off")))
            {
                bool pan = s.Contains("pan", StringComparison.Ordinal) || attributeKind == AttributeKind.Pan;
                bool tilt = s.Contains("tilt", StringComparison.Ordinal) || attributeKind == AttributeKind.Tilt;
                if (pan) flags |= ChannelSetFlags.InvertPan;
                if (tilt) flags |= ChannelSetFlags.InvertTilt;
            }
        }
        if (s.StartsWith("no function", StringComparison.Ordinal) || s.StartsWith("no feature", StringComparison.Ordinal) ||
            s.StartsWith("nofeature", StringComparison.Ordinal) || s is "idle" or "reserved" or "no effect" or "empty channel set" ||
            s.StartsWith("no effect", StringComparison.Ordinal) || s is "stop" or "no rotation" or "rot stop" or "no rot" ||
            s.StartsWith("no rotation", StringComparison.Ordinal) || s is "no flash" or "no strobe" or "disable" or "disabled")
            flags |= ChannelSetFlags.NoFunction;
        if (s.StartsWith("open", StringComparison.Ordinal) || s.StartsWith("shutter open", StringComparison.Ordinal) ||
            s.Contains("(shutter open)", StringComparison.Ordinal) || s is "leds on" or "led on" or "on" || s.StartsWith("no function (shutter open", StringComparison.Ordinal))
            flags |= ChannelSetFlags.Open;
        if (s.StartsWith("closed", StringComparison.Ordinal) || s.StartsWith("close", StringComparison.Ordinal) ||
            s.StartsWith("shutter closed", StringComparison.Ordinal) || s.StartsWith("blackout", StringComparison.Ordinal) ||
            s is "leds off" or "led off" or "dark")
            flags |= ChannelSetFlags.Closed;
        if (s == "off" && attributeKind is AttributeKind.Shutter or AttributeKind.Dimmer or AttributeKind.Fog or AttributeKind.Haze
                or AttributeKind.Fan or AttributeKind.Flame)
            flags |= ChannelSetFlags.Closed;
        return flags;
    }

    /// <summary>Dimmer law selected by a <c>DimmerCurve</c> channel set name.</summary>
    public static DimmerCurve ParseDimmerCurve(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return DimmerCurve.Linear;
        string s = name.ToLowerInvariant();
        if (s.Contains("inverse square", StringComparison.Ordinal) || s.Contains("inverted square", StringComparison.Ordinal) ||
            s.Contains("inv square", StringComparison.Ordinal) || s.Contains("inv. square", StringComparison.Ordinal))
            return DimmerCurve.InverseSquare;
        if (s.Contains("square", StringComparison.Ordinal)) return DimmerCurve.Square;
        if (s.Contains("s-curve", StringComparison.Ordinal) || s.Contains("s curve", StringComparison.Ordinal) ||
            s.Contains("scurve", StringComparison.Ordinal))
            return DimmerCurve.SCurve;
        if (s.Contains("tungsten", StringComparison.Ordinal) || s.Contains("halogen", StringComparison.Ordinal) ||
            s.Contains("incandescent", StringComparison.Ordinal))
            return DimmerCurve.Square;
        return DimmerCurve.Linear;
    }

    /// <summary>Strobe waveform named by a <c>StrobeMode…</c>/<c>StrobeRate</c> channel set ("ramp up", "random", "lightning" …), or null.</summary>
    public static Waveform? ParseWaveform(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        string s = name.ToLowerInvariant();
        if (s.Contains("ramp up-down", StringComparison.Ordinal) || s.Contains("ramp up to ramp down", StringComparison.Ordinal) ||
            s.Contains("ramp up down", StringComparison.Ordinal))
            return Waveform.Pulse;
        if (s.Contains("ramp up", StringComparison.Ordinal) || s.Contains("pulse open", StringComparison.Ordinal) ||
            s.Contains("pulse opening", StringComparison.Ordinal))
            return Waveform.PulseOpen;
        if (s.Contains("ramp down", StringComparison.Ordinal) || s.Contains("pulse close", StringComparison.Ordinal) ||
            s.Contains("pulse closing", StringComparison.Ordinal))
            return Waveform.PulseClose;
        if (s.Contains("lightning", StringComparison.Ordinal) || s.Contains("lighting", StringComparison.Ordinal) ||
            s.Contains("random", StringComparison.Ordinal) || s.Contains("spikes", StringComparison.Ordinal))
            return Waveform.Random;
        if (s.Contains("double flash", StringComparison.Ordinal) || s.Contains("triple flash", StringComparison.Ordinal) ||
            s.Contains("burst", StringComparison.Ordinal))
            return Waveform.Burst;
        if (s.Contains("sine", StringComparison.Ordinal)) return Waveform.Sine;
        if (s.Contains("pulse", StringComparison.Ordinal)) return Waveform.Pulse;
        if (s.Contains("strobe", StringComparison.Ordinal) || s.Contains("flash", StringComparison.Ordinal)) return Waveform.Strobe;
        if (s.StartsWith("open", StringComparison.Ordinal) || s.Contains("no function", StringComparison.Ordinal) ||
            s.Contains("no effect", StringComparison.Ordinal) || s.Contains("standard", StringComparison.Ordinal))
            return Waveform.None;
        return null;
    }

    /// <summary>Number of prism facets written in a name ("5 facet linear", "4-facet circular prism", "8 facet prism"), or 0.</summary>
    public static int ParseFacetCount(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return 0;
        string s = name.ToLowerInvariant();
        int idx = s.IndexOf("facet", StringComparison.Ordinal);
        if (idx < 0) idx = s.IndexOf("-fold", StringComparison.Ordinal);
        if (idx < 0) idx = s.IndexOf(" fold", StringComparison.Ordinal);
        if (idx < 0) return 0;
        int j = idx - 1;
        while (j >= 0 && (s[j] == ' ' || s[j] == '-')) j--;
        int end = j + 1;
        while (j >= 0 && char.IsAsciiDigit(s[j])) j--;
        return int.TryParse(s.AsSpan(j + 1, end - j - 1), NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n is > 0 and < 100 ? n : 0;
    }

    /// <summary>True when the name describes an open/empty wheel position ("Open", "Open/Hole", "Open position", "No prism").</summary>
    public static bool IsOpenSlotName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        string s = name.Trim().ToLowerInvariant();
        return s.StartsWith("open", StringComparison.Ordinal) || s is "white" or "empty" or "hole" or "no gobo" or "no prism" or "none" ||
            s.StartsWith("no prism", StringComparison.Ordinal) || s.StartsWith("empty", StringComparison.Ordinal);
    }

    /// <summary>
    /// True when a slot name says the slot is a hole even though the file gives it a media file (an image of an open
    /// aperture): "Open", "Open/Hole", "Open position", "Open (2)", "Hole", "No gobo" — but not "Open 2" next to a
    /// gobo image, "Open+Red" or "Open/Close 1", which name a real gobo, a split colour or a shape.
    /// </summary>
    public static bool IsPlainOpenSlotName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        string s = name.Trim().ToLowerInvariant();
        if (s is "hole" or "empty" or "none" or "no gobo" or "no prism") return true;
        if (!s.StartsWith("open", StringComparison.Ordinal)) return false;
        var rest = s.AsSpan(4);
        int i = 0;
        while (i < rest.Length)
        {
            char c = rest[i];
            if (char.IsLetter(c))
            {
                int j = i;
                while (j < rest.Length && char.IsLetter(rest[j])) j++;
                var word = rest[i..j];
                if (!(word is "hole" or "position" or "slot" or "white")) return false;
                i = j;
                continue;
            }
            // "(2)" is a second open position; a bare number ("Open 2") is often a mislabelled gobo
            if (char.IsAsciiDigit(c) && !(i > 0 && rest[i - 1] == '(')) return false;
            i++;
        }
        return true;
    }

    /// <summary>
    /// True when the name of a wheel selection range or function says the mechanism is out of the beam: an open
    /// position ("Open", "Open/Hole", "No prism"), "Off", "Removed", "Out", "Closed", "No function", "Idle", "Disabled",
    /// "None", "Empty". A range that moves from such a state to another ("Open to Dots", "Open > Close") is not out.
    /// </summary>
    public static bool IsWheelOutName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        string s = name.Trim().ToLowerInvariant();
        if (IsTransition(s)) return false;
        if (IsOpenSlotName(s)) return true;
        if (s is "off" or "out" or "removed" or "remove" or "idle" or "neutral" or "disable" or "disabled" or "close" or "closed") return true;
        foreach (string prefix in (ReadOnlySpan<string>)["off ", "off(", "off-", "removed", "out ", "closed", "close ", "no function", "nofunction",
            "no feature", "nofeature", "no animation", "no effect", "no gobo", "no shaper", "no beam shaper", "empty"])
        {
            if (s.StartsWith(prefix, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>
    /// True when a wheel selection range named <paramref name="name"/> that follows an "out" range moves the mechanism
    /// gradually into the beam: no name (the ramp between "Open" and "Animation In"), a transition out of an open
    /// state ("Open to Dots", "Open > In"), or an insertion "in proportion".
    /// </summary>
    public static bool IsInsertionRampName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return true;
        string s = name.Trim().ToLowerInvariant();
        if (s.Contains("proportion", StringComparison.Ordinal) || s.Contains("insertion", StringComparison.Ordinal)) return true;
        if (!IsTransition(s)) return false;
        int cut = TransitionIndex(s);
        return cut > 0 && IsWheelOutName(s[..cut]);
    }

    /// <summary>
    /// A range that keeps moving a mechanism between two insertions: "Ramping from open to full position max",
    /// "Ramp from half position to full position min". <paramref name="from"/> and <paramref name="to"/> are the
    /// insertions (open 0, half 0.5, full 1); <paramref name="speedEnd"/> is +1 when the name marks the fast end of the
    /// range ("max"), −1 for the slow end ("min"), 0 when it names neither.
    /// </summary>
    public static bool TryParseInsertionCycle(string? name, out float from, out float to, out int speedEnd)
    {
        from = to = float.NaN;
        speedEnd = 0;
        if (string.IsNullOrWhiteSpace(name)) return false;
        string s = name.Trim().ToLowerInvariant();
        if (!s.StartsWith("ramp", StringComparison.Ordinal)) return false;
        int f = s.IndexOf("from ", StringComparison.Ordinal);
        if (f < 0) return false;
        int t = s.IndexOf(" to ", f, StringComparison.Ordinal);
        if (t < 0) return false;
        from = InsertionWord(s.AsSpan(f + 5, t - f - 5));
        to = InsertionWord(s.AsSpan(t + 4));
        if (float.IsNaN(from) || float.IsNaN(to) || from == to) return false;
        if (ContainsWord(s, "max")) speedEnd = 1;
        else if (ContainsWord(s, "min")) speedEnd = -1;
        return true;
    }

    /// <summary>Insertion named by the first word of <paramref name="s"/>: open 0, half 0.5, full 1; NaN otherwise.</summary>
    private static float InsertionWord(ReadOnlySpan<char> s)
    {
        s = s.TrimStart();
        if (s.StartsWith("open", StringComparison.Ordinal)) return 0f;
        if (s.StartsWith("half", StringComparison.Ordinal)) return 0.5f;
        if (s.StartsWith("full", StringComparison.Ordinal)) return 1f;
        return float.NaN;
    }

    /// <summary>True for "A to B", "A > B", "A -> B", "A → B".</summary>
    private static bool IsTransition(string s) => TransitionIndex(s) > 0;

    private static int TransitionIndex(string s)
    {
        int i = s.IndexOf(" to ", StringComparison.Ordinal);
        if (i > 0) return i;
        foreach (string arrow in (ReadOnlySpan<string>)["->", "→", ">"])
        {
            i = s.IndexOf(arrow, StringComparison.Ordinal);
            if (i > 0) return i;
        }
        return -1;
    }

    private static bool ContainsAny(string s, params string[] words)
    {
        foreach (var w in words)
            if (s.Contains(w, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>True when <paramref name="word"/> occurs in <paramref name="s"/> delimited by non-letters.</summary>
    private static bool ContainsWord(string s, string word)
    {
        int start = 0;
        while (true)
        {
            int i = s.IndexOf(word, start, StringComparison.Ordinal);
            if (i < 0) return false;
            bool before = i == 0 || !char.IsLetter(s[i - 1]);
            int end = i + word.Length;
            bool after = end >= s.Length || !char.IsLetter(s[end]);
            if (before && after) return true;
            start = i + 1;
        }
    }
}

/// <summary>Dimmer law (DMX level → light output) selected by a <c>DimmerCurve</c> channel.</summary>
public enum DimmerCurve
{
    /// <summary>Output proportional to the level.</summary>
    Linear,

    /// <summary>Output = level² (fine control at the bottom, tungsten-like).</summary>
    Square,

    /// <summary>Output = √level (fast rise).</summary>
    InverseSquare,

    /// <summary>Smoothstep: slow at both ends.</summary>
    SCurve,
}
