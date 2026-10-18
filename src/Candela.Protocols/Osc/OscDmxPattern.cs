using System.Globalization;

namespace Candela.Protocols.Osc;

/// <summary>How OSC argument values become DMX bytes.</summary>
public enum OscValueScale
{
    /// <summary>Integers (i, h, c) are DMX values 0..255; reals (f, d) are 0..1; T/F are 255/0.</summary>
    Auto,

    /// <summary>Every number is a DMX value 0..255 (rounded).</summary>
    Byte,

    /// <summary>Every number is 0..1.</summary>
    Normalized,

    /// <summary>Every number is a percentage 0..100.</summary>
    Percent,
}

/// <summary>
/// One OSC → DMX mapping rule, parsed from text such as
/// <c>/dmx/{universe}/{channel}</c>, <c>/dmx/{universe}</c> (whole universe from a blob or a list of values),
/// <c>/fader/{channel} universe=2 scale=percent</c> or <c>/*/level/{channel}</c>.
/// Segments: <c>{universe}</c> (1-based internal universe), <c>{channel}</c> (1–512, the first slot written), <c>*</c>
/// (any one segment) or literal text. Options after the address: <c>universe=N</c> (when the address has no
/// <c>{universe}</c>; default 1) and <c>scale=auto|byte|normalized|percent</c>.
/// Matching is allocation-free.
/// </summary>
public sealed class OscDmxPattern
{
    private const int UniverseSegment = -1;
    private const int ChannelSegment = -2;
    private const int WildcardSegment = -3;

    private readonly string[] _literals;
    private readonly int[] _kinds;

    private OscDmxPattern(string text, string[] literals, int[] kinds, int defaultUniverse, OscValueScale scale)
    {
        Text = text;
        _literals = literals;
        _kinds = kinds;
        DefaultUniverse = defaultUniverse;
        Scale = scale;
    }

    /// <summary>The pattern as written.</summary>
    public string Text { get; }

    /// <summary>Universe used when the address has no <c>{universe}</c> segment.</summary>
    public int DefaultUniverse { get; }

    /// <summary>Value scaling.</summary>
    public OscValueScale Scale { get; }

    /// <summary>True when the address contains <c>{channel}</c>; otherwise values start at slot 1.</summary>
    public bool HasChannel => Array.IndexOf(_kinds, ChannelSegment) >= 0;

    /// <summary>Parses a pattern; returns false with a user-facing reason when the text is not a valid pattern.</summary>
    public static bool TryParse(string? text, out OscDmxPattern? pattern, out string? error)
    {
        pattern = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "empty OSC pattern";
            return false;
        }
        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var address = tokens[0];
        if (address.Length < 2 || address[0] != '/')
        {
            error = $"OSC pattern '{text}' must start with '/'";
            return false;
        }
        int universe = 1;
        var scale = OscValueScale.Auto;
        for (int i = 1; i < tokens.Length; i++)
        {
            var parts = tokens[i].Split('=', 2);
            if (parts.Length != 2)
            {
                error = $"OSC pattern option '{tokens[i]}' is not key=value";
                return false;
            }
            switch (parts[0].ToLowerInvariant())
            {
                case "universe" when int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var u) && u is >= 1 and <= 65535:
                    universe = u;
                    break;
                case "scale" when Enum.TryParse<OscValueScale>(parts[1], ignoreCase: true, out var s) && Enum.IsDefined(s):
                    scale = s;
                    break;
                default:
                    error = $"OSC pattern option '{tokens[i]}' is not understood (universe=1..65535, scale=auto|byte|normalized|percent)";
                    return false;
            }
        }

        var segments = address[1..].Split('/');
        var literals = new string[segments.Length];
        var kinds = new int[segments.Length];
        bool hasUniverse = false, hasChannel = false;
        for (int i = 0; i < segments.Length; i++)
        {
            var seg = segments[i];
            if (seg.Length == 0)
            {
                error = $"OSC pattern '{address}' has an empty segment";
                return false;
            }
            if (seg.Equals("{universe}", StringComparison.OrdinalIgnoreCase))
            {
                if (hasUniverse)
                {
                    error = "an OSC pattern can contain {universe} only once";
                    return false;
                }
                hasUniverse = true;
                kinds[i] = UniverseSegment;
            }
            else if (seg.Equals("{channel}", StringComparison.OrdinalIgnoreCase))
            {
                if (hasChannel)
                {
                    error = "an OSC pattern can contain {channel} only once";
                    return false;
                }
                hasChannel = true;
                kinds[i] = ChannelSegment;
            }
            else if (seg == "*")
            {
                kinds[i] = WildcardSegment;
            }
            else if (seg.Contains('{') || seg.Contains('}'))
            {
                error = $"unknown placeholder '{seg}' (use {{universe}} or {{channel}})";
                return false;
            }
            literals[i] = seg;
        }
        pattern = new OscDmxPattern(text.Trim(), literals, kinds, universe, scale);
        error = null;
        return true;
    }

    /// <summary>Parses a pattern or throws <see cref="FormatException"/>.</summary>
    public static OscDmxPattern Parse(string text) =>
        TryParse(text, out var p, out var error) ? p! : throw new FormatException(error);

    /// <summary>Matches a concrete OSC address. Returns the universe (1..65535) and first channel (1..512; 1 when the
    /// pattern has no <c>{channel}</c>).</summary>
    public bool TryMatch(string address, out int universe, out int channel)
    {
        ArgumentNullException.ThrowIfNull(address);
        universe = DefaultUniverse;
        channel = 1;
        var rest = address.AsSpan();
        if (rest.Length < 2 || rest[0] != '/') return false;
        rest = rest[1..];
        for (int i = 0; i < _kinds.Length; i++)
        {
            if (rest.IsEmpty && i < _kinds.Length) return false;
            int slash = rest.IndexOf('/');
            var segment = slash < 0 ? rest : rest[..slash];
            rest = slash < 0 ? ReadOnlySpan<char>.Empty : rest[(slash + 1)..];
            bool last = i == _kinds.Length - 1;
            if (last != (slash < 0)) return false;
            switch (_kinds[i])
            {
                case UniverseSegment:
                    if (!int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out universe) || universe is < 1 or > 65535) return false;
                    break;
                case ChannelSegment:
                    if (!int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out channel) || channel is < 1 or > 512) return false;
                    break;
                case WildcardSegment:
                    if (segment.IsEmpty) return false;
                    break;
                default:
                    if (!segment.SequenceEqual(_literals[i])) return false;
                    break;
            }
        }
        return true;
    }

    /// <summary>
    /// Converts the message's arguments to DMX values written to consecutive slots from <paramref name="channel"/>
    /// (1-based) into <paramref name="slots"/> (512 bytes). Blobs are copied byte by byte; numbers are scaled with
    /// <see cref="Scale"/>; strings, markers and NaN are skipped. Returns the number of slots written.
    /// </summary>
    public int WriteValues(OscMessage message, int channel, Span<byte> slots)
    {
        ArgumentNullException.ThrowIfNull(message);
        int index = channel - 1, written = 0;
        var args = message.Arguments;
        for (int a = 0; a < args.Count && index < slots.Length; a++)
        {
            var arg = args[a];
            if (arg.Type == OscType.Blob)
            {
                foreach (byte b in arg.AsBlob!)
                {
                    if (index >= slots.Length) break;
                    slots[index++] = b;
                    written++;
                }
                continue;
            }
            if (!arg.IsNumeric) continue;
            double v = arg.AsDouble;
            if (double.IsNaN(v)) continue;
            double dmx = Scale switch
            {
                OscValueScale.Byte => v,
                OscValueScale.Normalized => v * 255,
                OscValueScale.Percent => v / 100.0 * 255,
                _ => arg.IsReal ? v * 255 : arg.Type is OscType.True or OscType.False ? v * 255 : v,
            };
            slots[index++] = (byte)Math.Clamp(Math.Round(dmx, MidpointRounding.AwayFromZero), 0, 255);
            written++;
        }
        return written;
    }

    /// <inheritdoc />
    public override string ToString() => Text;
}
