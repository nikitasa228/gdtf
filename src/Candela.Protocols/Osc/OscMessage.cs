using System.Globalization;

namespace Candela.Protocols.Osc;

/// <summary>OSC 1.0/1.1 argument type tags.</summary>
public enum OscType : byte
{
    /// <summary>'i' — 32-bit signed integer.</summary>
    Int32 = (byte)'i',

    /// <summary>'f' — 32-bit IEEE float.</summary>
    Float32 = (byte)'f',

    /// <summary>'s' — string.</summary>
    String = (byte)'s',

    /// <summary>'b' — blob (byte array).</summary>
    Blob = (byte)'b',

    /// <summary>'h' — 64-bit signed integer.</summary>
    Int64 = (byte)'h',

    /// <summary>'t' — OSC time tag (NTP format).</summary>
    TimeTag = (byte)'t',

    /// <summary>'d' — 64-bit IEEE double.</summary>
    Double = (byte)'d',

    /// <summary>'S' — symbol (string).</summary>
    Symbol = (byte)'S',

    /// <summary>'c' — ASCII character (sent as 32 bits).</summary>
    Char = (byte)'c',

    /// <summary>'r' — 32-bit RGBA colour.</summary>
    Rgba = (byte)'r',

    /// <summary>'m' — 4-byte MIDI message (port, status, data1, data2).</summary>
    Midi = (byte)'m',

    /// <summary>'T' — true (no data).</summary>
    True = (byte)'T',

    /// <summary>'F' — false (no data).</summary>
    False = (byte)'F',

    /// <summary>'N' — nil (no data).</summary>
    Nil = (byte)'N',

    /// <summary>'I' — impulse / infinitum (no data).</summary>
    Impulse = (byte)'I',

    /// <summary>'[' — array start marker.</summary>
    ArrayBegin = (byte)'[',

    /// <summary>']' — array end marker.</summary>
    ArrayEnd = (byte)']',
}

/// <summary>One OSC argument. Numeric types keep their value in <see cref="AsInt64"/>/<see cref="AsDouble"/>; strings and
/// blobs in <see cref="AsString"/>/<see cref="AsBlob"/>.</summary>
public readonly struct OscArgument : IEquatable<OscArgument>
{
    private readonly long _integer;
    private readonly double _real;
    private readonly object? _reference;

    private OscArgument(OscType type, long integer, double real, object? reference)
    {
        Type = type;
        _integer = integer;
        _real = real;
        _reference = reference;
    }

    /// <summary>The type tag.</summary>
    public OscType Type { get; }

    /// <summary>True for i, h, c, f, d, T, F (values usable as numbers).</summary>
    public bool IsNumeric => Type is OscType.Int32 or OscType.Int64 or OscType.Char or OscType.Float32 or OscType.Double
        or OscType.True or OscType.False;

    /// <summary>True for f and d.</summary>
    public bool IsReal => Type is OscType.Float32 or OscType.Double;

    /// <summary>Integer value of i, h, c, r, m, t (bit pattern), T (1) and F (0); real values truncated.</summary>
    public long AsInt64 => IsReal ? (long)_real : _integer;

    /// <summary>Numeric value as a double (integers converted, T = 1, F = 0).</summary>
    public double AsDouble => IsReal ? _real : _integer;

    /// <summary>String value of s and S, else null.</summary>
    public string? AsString => _reference as string;

    /// <summary>Bytes of a blob, else null.</summary>
    public byte[]? AsBlob => _reference as byte[];

    /// <summary>An 'i' argument.</summary>
    public static OscArgument Int(int value) => new(OscType.Int32, value, 0, null);

    /// <summary>An 'h' argument.</summary>
    public static OscArgument Long(long value) => new(OscType.Int64, value, 0, null);

    /// <summary>An 'f' argument.</summary>
    public static OscArgument Float(float value) => new(OscType.Float32, 0, value, null);

    /// <summary>A 'd' argument.</summary>
    public static OscArgument Double(double value) => new(OscType.Double, 0, value, null);

    /// <summary>An 's' argument.</summary>
    public static OscArgument String(string value) => new(OscType.String, 0, 0, value ?? throw new ArgumentNullException(nameof(value)));

    /// <summary>An 'S' argument.</summary>
    public static OscArgument Symbol(string value) => new(OscType.Symbol, 0, 0, value ?? throw new ArgumentNullException(nameof(value)));

    /// <summary>A 'b' argument.</summary>
    public static OscArgument Blob(byte[] value) => new(OscType.Blob, 0, 0, value ?? throw new ArgumentNullException(nameof(value)));

    /// <summary>A 'T' or 'F' argument.</summary>
    public static OscArgument Bool(bool value) => new(value ? OscType.True : OscType.False, value ? 1 : 0, 0, null);

    /// <summary>A 't' argument (NTP time tag).</summary>
    public static OscArgument Time(ulong ntpTime) => new(OscType.TimeTag, unchecked((long)ntpTime), 0, null);

    /// <summary>A 'c' argument.</summary>
    public static OscArgument Character(char value) => new(OscType.Char, value, 0, null);

    /// <summary>An 'r' argument (0xRRGGBBAA).</summary>
    public static OscArgument Color(uint rgba) => new(OscType.Rgba, rgba, 0, null);

    /// <summary>An 'm' argument (port id, status, data1, data2 packed big-endian).</summary>
    public static OscArgument Midi(uint message) => new(OscType.Midi, message, 0, null);

    /// <summary>An argument without data: N, I, [ or ].</summary>
    public static OscArgument Marker(OscType type)
    {
        if (type is not (OscType.Nil or OscType.Impulse or OscType.ArrayBegin or OscType.ArrayEnd))
            throw new ArgumentOutOfRangeException(nameof(type), type, "Only N, I, [ and ] carry no data.");
        return new OscArgument(type, 0, 0, null);
    }

    /// <inheritdoc />
    public bool Equals(OscArgument other)
    {
        if (Type != other.Type || _integer != other._integer || !_real.Equals(other._real)) return false;
        if (_reference is byte[] a && other._reference is byte[] b) return a.AsSpan().SequenceEqual(b);
        return Equals(_reference, other._reference);
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is OscArgument other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Type, _integer, _real, (_reference as byte[])?.Length ?? _reference?.GetHashCode() ?? 0);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(OscArgument left, OscArgument right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(OscArgument left, OscArgument right) => !left.Equals(right);

    /// <summary>"i:255", "f:0.5", "s:text", "b:4 bytes", …</summary>
    public override string ToString() => Type switch
    {
        OscType.Float32 or OscType.Double => $"{(char)Type}:{_real.ToString("G9", CultureInfo.InvariantCulture)}",
        OscType.String or OscType.Symbol => $"{(char)Type}:{_reference}",
        OscType.Blob => $"b:{AsBlob!.Length} bytes",
        OscType.True or OscType.False or OscType.Nil or OscType.Impulse or OscType.ArrayBegin or OscType.ArrayEnd => ((char)Type).ToString(),
        _ => $"{(char)Type}:{_integer.ToString(CultureInfo.InvariantCulture)}",
    };
}

/// <summary>An OSC message: address and arguments, plus the time tag of the enclosing bundle (if any).</summary>
public sealed class OscMessage
{
    /// <summary>Creates a message.</summary>
    /// <param name="address">OSC address ("/dmx/1/12").</param>
    /// <param name="arguments">Arguments in order.</param>
    public OscMessage(string address, params OscArgument[] arguments)
        : this(address, (IReadOnlyList<OscArgument>)arguments, null)
    {
    }

    /// <summary>Creates a message with an optional bundle time tag.</summary>
    public OscMessage(string address, IReadOnlyList<OscArgument> arguments, ulong? bundleTimeTag)
    {
        Address = address ?? throw new ArgumentNullException(nameof(address));
        Arguments = arguments ?? throw new ArgumentNullException(nameof(arguments));
        BundleTimeTag = bundleTimeTag;
    }

    /// <summary>OSC address.</summary>
    public string Address { get; }

    /// <summary>Arguments in order.</summary>
    public IReadOnlyList<OscArgument> Arguments { get; }

    /// <summary>NTP time tag of the bundle this message arrived in (1 = immediately), or null for a bare message.</summary>
    public ulong? BundleTimeTag { get; }

    /// <summary>"/address i:1 f:0.5".</summary>
    public override string ToString() => Arguments.Count == 0 ? Address : $"{Address} {string.Join(' ', Arguments)}";
}

/// <summary>A message as received, with sender and time (for the external-control panel's message log).</summary>
/// <param name="Message">The message.</param>
/// <param name="Sender">Sender address and port.</param>
/// <param name="ReceivedUtc">Arrival time.</param>
public sealed record OscReceivedMessage(OscMessage Message, string Sender, DateTime ReceivedUtc);

/// <summary>Event data of <see cref="OscReceiverBase.MessageReceived"/>.</summary>
public sealed class OscMessageEventArgs : EventArgs
{
    /// <summary>Creates the event data.</summary>
    public OscMessageEventArgs(OscReceivedMessage message) => Received = message;

    /// <summary>The received message.</summary>
    public OscReceivedMessage Received { get; }
}
