using System.Globalization;
using System.Numerics;
using System.Xml.Linq;
using Candela.Core.SceneGraph.Components;

namespace Candela.Gdtf;

/// <summary>
/// State of one read: the fixture type being built, the archive (for resource checks), the warning sink and tolerant
/// value parsers that record a warning and fall back to the GDTF default instead of throwing.
/// </summary>
internal sealed class ReadContext
{
    public ReadContext(FixtureType fixtureType, GdtfArchive? archive)
    {
        FixtureType = fixtureType;
        Archive = archive;
        Warnings = new WarningSink(fixtureType.Warnings);
    }

    public FixtureType FixtureType { get; }
    public GdtfArchive? Archive { get; }
    public WarningSink Warnings { get; }

    public void Warn(string path, string message) => Warnings.Add(path, message);

    public static string? Str(XElement e, XName name) => e.Attribute(name)?.Value;

    public static string Text(XElement e, XName name) => e.Attribute(name)?.Value ?? "";

    /// <summary>Attribute value, or null when missing, empty or white space.</summary>
    public static string? NonEmpty(XElement e, XName name)
    {
        var v = e.Attribute(name)?.Value;
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }

    public float Float(XElement e, XName name, float fallback, string path)
    {
        var a = e.Attribute(name);
        if (a is null) return fallback;
        return ParseFloat(a.Value, fallback, path, name.LocalName);
    }

    public float? FloatOpt(XElement e, XName name, string path)
    {
        var a = e.Attribute(name);
        if (a is null || string.IsNullOrWhiteSpace(a.Value)) return null;
        if (TryParseFloat(a.Value, out var f)) return f;
        Warn(path, $"{name.LocalName}=\"{a.Value}\" is not a number; ignored.");
        return null;
    }

    public float ParseFloat(string text, float fallback, string path, string what)
    {
        if (string.IsNullOrWhiteSpace(text)) return fallback;
        if (TryParseFloat(text, out var f)) return f;
        Warn(path, $"{what}=\"{text}\" is not a number; using {fallback.ToString(CultureInfo.InvariantCulture)}.");
        return fallback;
    }

    /// <summary>Invariant float, tolerating a decimal comma written by localised exporters.</summary>
    public static bool TryParseFloat(ReadOnlySpan<char> text, out float value)
    {
        text = text.Trim();
        if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && float.IsFinite(value)) return true;
        // A number is short; anything longer is not worth a second attempt (and must not size a stack buffer).
        if (text.Length <= 64 && text.IndexOf(',') >= 0 && text.IndexOf('.') < 0)
        {
            Span<char> copy = stackalloc char[text.Length];
            text.CopyTo(copy);
            copy.Replace(',', '.');
            if (float.TryParse(copy, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && float.IsFinite(value)) return true;
        }
        value = 0;
        return false;
    }

    public int Int(XElement e, XName name, int fallback, string path)
    {
        var a = e.Attribute(name);
        if (a is null || string.IsNullOrWhiteSpace(a.Value)) return fallback;
        var s = a.Value.AsSpan().Trim();
        if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i)) return i;
        if (TryParseFloat(s, out float f) && f >= int.MinValue && f <= int.MaxValue && f == MathF.Round(f)) return (int)f;
        Warn(path, $"{name.LocalName}=\"{a.Value}\" is not an integer; using {fallback}.");
        return fallback;
    }

    /// <summary>
    /// GDTF <c>DMXAddress</c>: an absolute address as an integer, or the alternative form <c>Universe.Address</c>
    /// (universe from 1, address 1 … 512), which is converted to <c>(Universe − 1) · 512 + Address</c>.
    /// </summary>
    public int DmxAddress(XElement e, XName name, int fallback, string path)
    {
        var a = e.Attribute(name);
        if (a is null || string.IsNullOrWhiteSpace(a.Value)) return fallback;
        if (TryParseDmxAddress(a.Value, out int address)) return address;
        Warn(path, $"{name.LocalName}=\"{a.Value}\" is not a DMX address; using {fallback}.");
        return fallback;
    }

    /// <summary>Parses an absolute DMX address or <c>Universe.Address</c>; false for anything else.</summary>
    public static bool TryParseDmxAddress(ReadOnlySpan<char> text, out int address)
    {
        address = 0;
        text = text.Trim();
        int dot = text.IndexOf('.');
        if (dot < 0) return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out address);
        if (!int.TryParse(text[..dot], NumberStyles.None, CultureInfo.InvariantCulture, out int universe) ||
            !int.TryParse(text[(dot + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out int local) ||
            universe < 1 || local is < 1 or > 512 || universe > (int.MaxValue - local) / 512 + 1)
            return false;
        address = (universe - 1) * 512 + local;
        return true;
    }

    /// <summary>
    /// GDTF type <c>Hex</c> (RDM manufacturer, model, software and personality ids): hexadecimal with or without the
    /// <c>0x</c> prefix that every file of the reference library writes.
    /// </summary>
    public uint Hex(XElement e, XName name, uint fallback, string path)
    {
        var a = e.Attribute(name);
        if (a is null || string.IsNullOrWhiteSpace(a.Value)) return fallback;
        var s = a.Value.AsSpan().Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        if (uint.TryParse(s, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint h)) return h;
        Warn(path, $"{name.LocalName}=\"{a.Value}\" is not a hexadecimal number; using {fallback}.");
        return fallback;
    }

    public bool YesNo(XElement e, XName name, bool fallback, string path)
    {
        var a = e.Attribute(name);
        if (a is null || string.IsNullOrWhiteSpace(a.Value)) return fallback;
        var s = a.Value.Trim();
        if (s.Equals("Yes", StringComparison.OrdinalIgnoreCase) || s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1") return true;
        if (s.Equals("No", StringComparison.OrdinalIgnoreCase) || s.Equals("false", StringComparison.OrdinalIgnoreCase) || s == "0") return false;
        Warn(path, $"{name.LocalName}=\"{a.Value}\" is not Yes/No; using {(fallback ? "Yes" : "No")}.");
        return fallback;
    }

    public T Enum<T>(XElement e, XName name, IReadOnlyDictionary<string, T> map, T fallback, string path)
    {
        var a = e.Attribute(name);
        if (a is null || string.IsNullOrWhiteSpace(a.Value)) return fallback;
        if (map.TryGetValue(a.Value.Trim(), out var v)) return v;
        Warn(path, $"{name.LocalName}=\"{a.Value}\" is not a known value; using {fallback}.");
        return fallback;
    }

    /// <summary>Enum value, or null when the attribute is missing, empty or unknown (unknown values are warned about).</summary>
    public T? EnumOpt<T>(XElement e, XName name, IReadOnlyDictionary<string, T> map, string path) where T : struct
    {
        var a = e.Attribute(name);
        if (a is null || string.IsNullOrWhiteSpace(a.Value)) return null;
        if (map.TryGetValue(a.Value.Trim(), out var v)) return v;
        Warn(path, $"{name.LocalName}=\"{a.Value}\" is not a known value; the GDTF default is used.");
        return null;
    }

    public DmxValue Dmx(XElement e, XName name, DmxValue fallback, string path)
    {
        var a = e.Attribute(name);
        if (a is null || string.IsNullOrWhiteSpace(a.Value)) return fallback;
        return ParseDmx(a.Value, fallback, path, name.LocalName);
    }

    /// <summary>DMX value or null for missing / "None".</summary>
    public DmxValue? DmxOpt(XElement e, XName name, string path)
    {
        var a = e.Attribute(name);
        if (a is null || string.IsNullOrWhiteSpace(a.Value) || a.Value.Trim().Equals("None", StringComparison.OrdinalIgnoreCase)) return null;
        if (TryParseDmxRepairing(a.Value, out var v, out bool clamped))
        {
            if (clamped) Warn(path, $"{name.LocalName}=\"{a.Value}\" does not fit its byte count; clamped to {v}.");
            return v;
        }
        Warn(path, $"{name.LocalName}=\"{a.Value}\" is not a DMX value; ignored.");
        return null;
    }

    /// <summary>Parses a DMX value attribute text, repairing overflows (see <see cref="TryParseDmxRepairing"/>).</summary>
    public DmxValue ParseDmx(string text, DmxValue fallback, string path, string what)
    {
        if (string.IsNullOrWhiteSpace(text)) return fallback;
        if (TryParseDmxRepairing(text, out var v, out bool clamped))
        {
            if (clamped) Warn(path, $"{what}=\"{text}\" does not fit its byte count; clamped to {v}.");
            return v;
        }
        Warn(path, $"{what}=\"{text}\" is not a DMX value; using {fallback}.");
        return fallback;
    }

    /// <summary>
    /// Parses a DMX value as written in a file. A value that does not fit its byte count is repaired: a moderate
    /// overflow (e.g. <c>256/1</c>, written by some exporters for "end of range") is clamped to the largest value of that
    /// byte count and <paramref name="clamped"/> is set; values of 2^31 and more (negative numbers written unsigned, such
    /// as <c>4294967295/1</c> for "none") are rejected.
    /// </summary>
    public static bool TryParseDmxRepairing(string text, out DmxValue value, out bool clamped)
    {
        clamped = false;
        if (DmxValue.TryParse(text, out value)) return true;
        var s = text.AsSpan().Trim();
        int slash = s.IndexOf('/');
        if (slash <= 0) return false;
        if (!ulong.TryParse(s[..slash].TrimEnd(), NumberStyles.None, CultureInfo.InvariantCulture, out ulong raw) || raw > int.MaxValue)
            return false;
        var rest = s[(slash + 1)..].Trim();
        var conversion = DmxConversion.ByteMirroring;
        if (rest.Length > 0 && (rest[^1] == 's' || rest[^1] == 'S'))
        {
            conversion = DmxConversion.ByteShifting;
            rest = rest[..^1].TrimEnd();
        }
        if (!int.TryParse(rest, NumberStyles.None, CultureInfo.InvariantCulture, out int bytes) || bytes is < 1 or > DmxValue.MaxByteCount)
            return false;
        uint max = DmxValue.MaxFor(bytes);
        if (raw <= max) return false; // well-formed values were accepted above; anything else here is malformed
        value = new DmxValue(max, bytes, conversion);
        clamped = true;
        return true;
    }

    public CieColor Color(XElement e, XName name, CieColor fallback, string path) =>
        ColorOpt(e, name, path) ?? fallback;

    public CieColor? ColorOpt(XElement e, XName name, string path)
    {
        var a = e.Attribute(name);
        if (a is null || string.IsNullOrWhiteSpace(a.Value)) return null;
        if (TryParseColor(a.Value, out var c)) return c;
        Warn(path, $"{name.LocalName}=\"{a.Value}\" is not a CIE colour \"x,y,Y\"; ignored.");
        return null;
    }

    /// <summary>Parses <c>"x,y,Y"</c> (Y optional, default 100); separators comma, semicolon or white space.</summary>
    public static bool TryParseColor(string text, out CieColor color)
    {
        color = default;
        Span<float> v = stackalloc float[3];
        int n = 0;
        var s = text.AsSpan().Trim().Trim("{}");
        while (!s.IsEmpty && n < 4)
        {
            int sep = s.IndexOfAny(", ;");
            var part = (sep < 0 ? s : s[..sep]).Trim();
            s = sep < 0 ? ReadOnlySpan<char>.Empty : s[(sep + 1)..].TrimStart();
            if (part.IsEmpty) continue;
            if (n >= 3 || !TryParseFloat(part, out float f)) return false;
            v[n++] = f;
        }
        if (n < 2) return false;
        color = new CieColor(v[0], v[1], n == 3 ? v[2] : 100f);
        return true;
    }

    /// <summary>Parses a list of colours <c>{x,y,Y}{x,y,Y}…</c> (gamut points).</summary>
    public static List<CieColor> ParseColorList(string? text)
    {
        var list = new List<CieColor>();
        if (string.IsNullOrWhiteSpace(text)) return list;
        int i = 0;
        while (i < text.Length)
        {
            int open = text.IndexOf('{', i);
            if (open < 0) break;
            int close = text.IndexOf('}', open + 1);
            if (close < 0) break;
            if (TryParseColor(text.Substring(open + 1, close - open - 1), out var c)) list.Add(c);
            i = close + 1;
        }
        return list;
    }

    public Matrix4x4 Matrix(XElement e, XName name, string path)
    {
        var a = e.Attribute(name);
        if (a is null || string.IsNullOrWhiteSpace(a.Value)) return Matrix4x4.Identity;
        if (GdtfMatrix.TryParse(a.Value, out var m))
        {
            // GDTF transforms are affine (last row 0,0,0,1, as in every library file). Anything else would turn the
            // composition of the tree into a projective transform; keep the affine part.
            if (m.M14 != 0f || m.M24 != 0f || m.M34 != 0f || m.M44 != 1f)
            {
                Warn(path, $"{name.LocalName}=\"{Shorten(a.Value)}\" is not affine (last row is not 0,0,0,1); the last row is ignored.");
                m.M14 = 0f;
                m.M24 = 0f;
                m.M34 = 0f;
                m.M44 = 1f;
            }
            return m;
        }
        Warn(path, $"{name.LocalName}=\"{Shorten(a.Value)}\" is not a GDTF matrix; identity used.");
        return Matrix4x4.Identity;
    }

    public Vector2 Vector2(XElement e, XName name, string path)
    {
        var a = e.Attribute(name);
        if (a is null || string.IsNullOrWhiteSpace(a.Value)) return System.Numerics.Vector2.Zero;
        var parts = a.Value.Trim().Trim('{', '}').Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 && TryParseFloat(parts[0], out float x) && TryParseFloat(parts[1], out float y)) return new Vector2(x, y);
        Warn(path, $"{name.LocalName}=\"{a.Value}\" is not a 2D point \"x,y\"; zero used.");
        return System.Numerics.Vector2.Zero;
    }

    public Vector3 Vector3(XElement e, XName name, string path)
    {
        var a = e.Attribute(name);
        if (a is null || string.IsNullOrWhiteSpace(a.Value)) return System.Numerics.Vector3.Zero;
        var parts = a.Value.Trim().Trim('{', '}').Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 3 && TryParseFloat(parts[0], out float x) && TryParseFloat(parts[1], out float y) && TryParseFloat(parts[2], out float z))
            return new Vector3(x, y, z);
        Warn(path, $"{name.LocalName}=\"{a.Value}\" is not a vector \"x,y,z\"; zero used.");
        return System.Numerics.Vector3.Zero;
    }

    public static string Shorten(string s) => s.Length <= 80 ? s : s[..77] + "...";

    /// <summary>
    /// Warning path of a child element: <c>{parent}/{name}</c>, with the name shortened and the parent cut to its end
    /// so that paths stay short however deep the tree or long the names (building them must not be quadratic).
    /// </summary>
    public static string ChildPath(string parentPath, string name)
    {
        const int maxParent = WarningSink.MaxPathLength;
        if (parentPath.Length > maxParent) parentPath = "…" + parentPath[^(maxParent - 1)..];
        return $"{parentPath}/{Shorten(name)}";
    }

    /// <summary>Case-insensitive enum map from GDTF spellings (plus aliases) to values.</summary>
    public static Dictionary<string, T> Map<T>(params (string Name, T Value)[] items)
    {
        var d = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        foreach (var (n, v) in items) d[n] = v;
        return d;
    }

    /// <summary>Enum map using the member names of <typeparamref name="T"/> plus extra aliases.</summary>
    public static Dictionary<string, T> MapEnum<T>(params (string Name, T Value)[] aliases) where T : struct, System.Enum
    {
        var d = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in System.Enum.GetValues<T>()) d[v.ToString()] = v;
        foreach (var (n, v) in aliases) d[n] = v;
        return d;
    }
}
