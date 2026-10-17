using System.Globalization;

namespace Candela.Gdtf;

/// <summary>
/// How a <see cref="DmxValue"/> written with fewer bytes is expanded to a channel with more bytes
/// (DIN SPEC 15800:2022, table "XML generic value types", type <c>DMXValue</c>).
/// </summary>
public enum DmxConversion
{
    /// <summary>
    /// Default for values without suffix: the written bytes are repeated into the lower bytes, so full scale stays full
    /// scale. <c>255/1</c> in a 16-bit channel is 65535, <c>128/1</c> is 32896 (0x8080).
    /// </summary>
    ByteMirroring,

    /// <summary>
    /// Values written with the <c>s</c> suffix (GDTF 1.2): the written bytes are shifted up and the lower bytes are
    /// zero. <c>255/1s</c> in a 16-bit channel is 65280.
    /// </summary>
    ByteShifting,
}

/// <summary>
/// A GDTF DMX value <c>"value/bytes"</c> (optionally <c>"value/bytess"</c>): an unsigned value written with a byte count
/// (1 = 8 bit … 4 = 32 bit) that is independent of the resolution of the channel it is used in. Use
/// <see cref="ToResolution"/> to compare values or to send them on a channel with a given resolution.
/// </summary>
/// <remarks>
/// Conversion to a higher resolution follows DIN SPEC 15800:2022: byte mirroring by default, byte shifting when the
/// value carries the <c>s</c> suffix. Conversion to a lower resolution always drops the least significant bytes.
/// No file in the reference library mixes resolutions on addressed channels, so both rules give the same results there.
/// </remarks>
public readonly struct DmxValue : IEquatable<DmxValue>
{
    /// <summary>Largest byte count GDTF allows (32-bit channels).</summary>
    public const int MaxByteCount = 4;

    /// <summary>Creates a value; throws when <paramref name="value"/> does not fit into <paramref name="byteCount"/> bytes.</summary>
    public DmxValue(uint value, int byteCount, DmxConversion conversion = DmxConversion.ByteMirroring)
    {
        if (byteCount is < 1 or > MaxByteCount)
            throw new ArgumentOutOfRangeException(nameof(byteCount), byteCount, "A DMX value has 1 to 4 bytes.");
        if (value > MaxFor(byteCount))
            throw new ArgumentOutOfRangeException(nameof(value), value, $"Value does not fit into {byteCount} byte(s).");
        Value = value;
        ByteCount = byteCount;
        Conversion = conversion;
    }

    /// <summary>The raw value in its own resolution (0 … 256^<see cref="ByteCount"/> − 1).</summary>
    public uint Value { get; }

    /// <summary>Number of bytes the value was written with (1 … 4). 0 only for <c>default(DmxValue)</c>, read as 0/1.</summary>
    public int ByteCount { get; }

    /// <summary>How the value expands to more bytes.</summary>
    public DmxConversion Conversion { get; }

    /// <summary>The zero value <c>0/1</c> (GDTF default of DMXFrom, Default, ModeFrom, ModeTo).</summary>
    public static DmxValue Zero => new(0, 1);

    /// <summary>Largest value of this value's own resolution.</summary>
    public uint Max => MaxFor(EffectiveByteCount);

    /// <summary>Value as a fraction of full scale in its own resolution (0 … 1).</summary>
    public double Normalized => (double)Value / Max;

    private int EffectiveByteCount => ByteCount == 0 ? 1 : ByteCount;

    /// <summary>Largest value representable with <paramref name="byteCount"/> bytes.</summary>
    public static uint MaxFor(int byteCount) => byteCount switch
    {
        <= 0 => 0,
        >= MaxByteCount => uint.MaxValue,
        _ => (1u << (8 * byteCount)) - 1,
    };

    /// <summary>
    /// The value expressed in a channel with <paramref name="byteCount"/> bytes (1 … 4), converted as the GDTF spec
    /// prescribes (see <see cref="DmxConversion"/>).
    /// </summary>
    public uint ToResolution(int byteCount)
    {
        if (byteCount is < 1 or > MaxByteCount)
            throw new ArgumentOutOfRangeException(nameof(byteCount), byteCount, "A DMX channel has 1 to 4 bytes.");
        int own = EffectiveByteCount;
        if (byteCount == own) return Value;
        if (byteCount < own) return Value >> (8 * (own - byteCount));
        if (Conversion == DmxConversion.ByteShifting) return Value << (8 * (byteCount - own));

        // Byte mirroring: repeat the written bytes (most significant first) until the target width is filled.
        uint result = 0;
        for (int i = 0; i < byteCount; i++)
        {
            int sourceByte = i % own;
            uint b = (Value >> (8 * (own - 1 - sourceByte))) & 0xFF;
            result = (result << 8) | b;
        }
        return result;
    }

    /// <summary>Same value converted to another resolution, keeping the conversion mode.</summary>
    public DmxValue ConvertTo(int byteCount) => new(ToResolution(byteCount), byteCount, Conversion);

    /// <summary>Value nearest to <paramref name="fraction"/> (clamped to 0 … 1) of full scale with the given byte count.</summary>
    public static DmxValue FromNormalized(double fraction, int byteCount)
    {
        if (byteCount is < 1 or > MaxByteCount)
            throw new ArgumentOutOfRangeException(nameof(byteCount), byteCount, "A DMX value has 1 to 4 bytes.");
        double f = double.IsNaN(fraction) ? 0 : System.Math.Clamp(fraction, 0, 1);
        return new DmxValue((uint)System.Math.Round(f * MaxFor(byteCount)), byteCount);
    }

    /// <summary>Parses <c>"value/bytes"</c> or <c>"value/bytess"</c>; throws <see cref="FormatException"/> on failure.</summary>
    public static DmxValue Parse(string text) =>
        TryParse(text, out var v) ? v : throw new FormatException($"'{text}' is not a GDTF DMX value (expected e.g. \"128/1\").");

    /// <summary>
    /// Parses <c>"value/bytes"</c> or <c>"value/bytess"</c> (surrounding white space allowed). A bare integer is accepted
    /// as well and gets the smallest byte count that holds it. <c>"None"</c> and anything else fail.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<char> text, out DmxValue value)
    {
        value = default;
        text = text.Trim();
        if (text.IsEmpty) return false;
        int slash = text.IndexOf('/');
        ReadOnlySpan<char> number = slash < 0 ? text : text[..slash].TrimEnd();
        if (!ulong.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out ulong raw) || raw > uint.MaxValue)
            return false;
        if (slash < 0)
        {
            int bytes = raw <= 0xFF ? 1 : raw <= 0xFFFF ? 2 : raw <= 0xFFFFFF ? 3 : 4;
            value = new DmxValue((uint)raw, bytes);
            return true;
        }

        var rest = text[(slash + 1)..].Trim();
        var conversion = DmxConversion.ByteMirroring;
        if (rest.Length > 0 && (rest[^1] == 's' || rest[^1] == 'S'))
        {
            conversion = DmxConversion.ByteShifting;
            rest = rest[..^1].TrimEnd();
        }
        if (!int.TryParse(rest, NumberStyles.None, CultureInfo.InvariantCulture, out int byteCount) || byteCount is < 1 or > MaxByteCount)
            return false;
        if (raw > MaxFor(byteCount)) return false;
        value = new DmxValue((uint)raw, byteCount, conversion);
        return true;
    }

    /// <summary>GDTF text form, e.g. <c>"128/1"</c> or <c>"255/1s"</c>.</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Value}/{EffectiveByteCount}{(Conversion == DmxConversion.ByteShifting ? "s" : "")}");

    /// <summary>Field-wise equality (<c>255/1</c> and <c>65535/2</c> are different values; compare with <see cref="ToResolution"/>).</summary>
    public bool Equals(DmxValue other) =>
        Value == other.Value && EffectiveByteCount == other.EffectiveByteCount && Conversion == other.Conversion;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is DmxValue other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Value, EffectiveByteCount, Conversion);

    /// <summary>Field-wise equality.</summary>
    public static bool operator ==(DmxValue left, DmxValue right) => left.Equals(right);

    /// <summary>Field-wise inequality.</summary>
    public static bool operator !=(DmxValue left, DmxValue right) => !left.Equals(right);
}
