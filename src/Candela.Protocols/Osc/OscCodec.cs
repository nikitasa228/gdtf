using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace Candela.Protocols.Osc;

/// <summary>
/// Parses and encodes OSC 1.0/1.1 packets: messages (address, type-tag string, arguments i f s b h t d S c r m T F N I
/// [ ]) and bundles (#bundle, time tag, size-prefixed elements, nested up to <see cref="MaxBundleDepth"/>). Parsing
/// validates every size against the packet and never throws on malformed input.
/// </summary>
public static class OscCodec
{
    /// <summary>Deepest bundle nesting accepted (protects against stack exhaustion from crafted packets).</summary>
    public const int MaxBundleDepth = 8;

    /// <summary>The time tag meaning "immediately".</summary>
    public const ulong Immediately = 1;

    private static ReadOnlySpan<byte> BundleId => "#bundle\0"u8;

    /// <summary>
    /// Parses a packet (message or bundle) and appends its messages to <paramref name="messages"/> in order. On failure
    /// nothing is appended and <paramref name="error"/> says why.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> packet, List<OscMessage> messages, out string? error)
    {
        ArgumentNullException.ThrowIfNull(messages);
        int before = messages.Count;
        if (TryParseElement(packet, messages, null, 0, out error)) return true;
        messages.RemoveRange(before, messages.Count - before);
        return false;
    }

    /// <summary>Encodes a message.</summary>
    public static byte[] Encode(OscMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var writer = new ArrayBufferWriter<byte>(64);
        WriteMessage(writer, message);
        return writer.WrittenSpan.ToArray();
    }

    /// <summary>Encodes a bundle of messages with <paramref name="timeTag"/> (NTP format; 1 = immediately).</summary>
    public static byte[] EncodeBundle(ulong timeTag, IEnumerable<OscMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        return EncodeBundle(timeTag, messages.Select(Encode));
    }

    /// <summary>Encodes a bundle whose elements are already-encoded messages or bundles (allows nesting).</summary>
    public static byte[] EncodeBundle(ulong timeTag, IEnumerable<byte[]> elements)
    {
        ArgumentNullException.ThrowIfNull(elements);
        var writer = new ArrayBufferWriter<byte>(128);
        writer.Write(BundleId);
        WriteUInt64(writer, timeTag);
        foreach (var element in elements)
        {
            if ((element.Length & 3) != 0) throw new ArgumentException("OSC elements are multiples of 4 bytes.", nameof(elements));
            WriteInt32(writer, element.Length);
            writer.Write(element);
        }
        return writer.WrittenSpan.ToArray();
    }

    private static bool TryParseElement(ReadOnlySpan<byte> data, List<OscMessage> output, ulong? timeTag, int depth, out string? error)
    {
        if (data.Length == 0 || (data.Length & 3) != 0)
        {
            error = "OSC packet size is not a positive multiple of 4";
            return false;
        }
        if (data[0] == (byte)'#') return TryParseBundle(data, output, depth, out error);
        if (data[0] == (byte)'/') return TryParseMessage(data, output, timeTag, out error);
        error = "OSC packet does not start with '/' or '#bundle'";
        return false;
    }

    private static bool TryParseBundle(ReadOnlySpan<byte> data, List<OscMessage> output, int depth, out string? error)
    {
        if (depth >= MaxBundleDepth)
        {
            error = "OSC bundles nested too deeply";
            return false;
        }
        if (data.Length < 16 || !data[..8].SequenceEqual(BundleId))
        {
            error = "malformed OSC bundle header";
            return false;
        }
        ulong timeTag = BinaryPrimitives.ReadUInt64BigEndian(data[8..]);
        int offset = 16;
        while (offset < data.Length)
        {
            if (data.Length - offset < 4)
            {
                error = "truncated OSC bundle element size";
                return false;
            }
            int size = BinaryPrimitives.ReadInt32BigEndian(data[offset..]);
            offset += 4;
            if (size <= 0 || (size & 3) != 0 || size > data.Length - offset)
            {
                error = "OSC bundle element size does not fit";
                return false;
            }
            if (!TryParseElement(data.Slice(offset, size), output, timeTag, depth + 1, out error)) return false;
            offset += size;
        }
        error = null;
        return true;
    }

    private static bool TryParseMessage(ReadOnlySpan<byte> data, List<OscMessage> output, ulong? timeTag, out string? error)
    {
        int offset = 0;
        if (!TryReadString(data, ref offset, out var address))
        {
            error = "unterminated OSC address";
            return false;
        }
        if (offset >= data.Length)
        {
            // OSC 1.0 senders may omit the type-tag string for argument-less messages.
            output.Add(new OscMessage(address, Array.Empty<OscArgument>(), timeTag));
            error = null;
            return true;
        }
        if (data[offset] != (byte)',')
        {
            error = "missing OSC type-tag string";
            return false;
        }
        int tagStart = offset + 1;
        int tagEnd = data[tagStart..].IndexOf((byte)0);
        if (tagEnd < 0)
        {
            error = "unterminated OSC type-tag string";
            return false;
        }
        var tags = data.Slice(tagStart, tagEnd);
        offset = Pad(tagStart + tagEnd + 1);
        if (offset > data.Length)
        {
            error = "OSC type-tag padding exceeds the packet";
            return false;
        }

        var args = tags.Length == 0 ? Array.Empty<OscArgument>() : new OscArgument[tags.Length];
        for (int i = 0; i < tags.Length; i++)
        {
            if (!TryReadArgument((OscType)tags[i], data, ref offset, out args[i]))
            {
                error = $"bad or truncated OSC argument '{(char)tags[i]}'";
                return false;
            }
        }
        output.Add(new OscMessage(address, args, timeTag));
        error = null;
        return true;
    }

    private static bool TryReadArgument(OscType type, ReadOnlySpan<byte> data, ref int offset, out OscArgument argument)
    {
        argument = default;
        int remaining = data.Length - offset;
        switch (type)
        {
            case OscType.Int32:
                if (remaining < 4) return false;
                argument = OscArgument.Int(BinaryPrimitives.ReadInt32BigEndian(data[offset..]));
                offset += 4;
                return true;
            case OscType.Float32:
                if (remaining < 4) return false;
                argument = OscArgument.Float(BinaryPrimitives.ReadSingleBigEndian(data[offset..]));
                offset += 4;
                return true;
            case OscType.Char:
                if (remaining < 4) return false;
                argument = OscArgument.Character((char)BinaryPrimitives.ReadUInt32BigEndian(data[offset..]));
                offset += 4;
                return true;
            case OscType.Rgba:
                if (remaining < 4) return false;
                argument = OscArgument.Color(BinaryPrimitives.ReadUInt32BigEndian(data[offset..]));
                offset += 4;
                return true;
            case OscType.Midi:
                if (remaining < 4) return false;
                argument = OscArgument.Midi(BinaryPrimitives.ReadUInt32BigEndian(data[offset..]));
                offset += 4;
                return true;
            case OscType.Int64:
                if (remaining < 8) return false;
                argument = OscArgument.Long(BinaryPrimitives.ReadInt64BigEndian(data[offset..]));
                offset += 8;
                return true;
            case OscType.TimeTag:
                if (remaining < 8) return false;
                argument = OscArgument.Time(BinaryPrimitives.ReadUInt64BigEndian(data[offset..]));
                offset += 8;
                return true;
            case OscType.Double:
                if (remaining < 8) return false;
                argument = OscArgument.Double(BinaryPrimitives.ReadDoubleBigEndian(data[offset..]));
                offset += 8;
                return true;
            case OscType.String:
            case OscType.Symbol:
                if (!TryReadString(data, ref offset, out var text)) return false;
                argument = type == OscType.String ? OscArgument.String(text) : OscArgument.Symbol(text);
                return true;
            case OscType.Blob:
                if (remaining < 4) return false;
                int size = BinaryPrimitives.ReadInt32BigEndian(data[offset..]);
                if (size < 0 || size > remaining - 4 || Pad(4 + size) > remaining) return false;
                argument = OscArgument.Blob(data.Slice(offset + 4, size).ToArray());
                offset += Pad(4 + size);
                return true;
            case OscType.True:
                argument = OscArgument.Bool(true);
                return true;
            case OscType.False:
                argument = OscArgument.Bool(false);
                return true;
            case OscType.Nil:
            case OscType.Impulse:
            case OscType.ArrayBegin:
            case OscType.ArrayEnd:
                argument = OscArgument.Marker(type);
                return true;
            default:
                return false;
        }
    }

    private static bool TryReadString(ReadOnlySpan<byte> data, ref int offset, out string text)
    {
        text = "";
        if (offset >= data.Length) return false;
        int end = data[offset..].IndexOf((byte)0);
        if (end < 0) return false;
        int next = Pad(offset + end + 1);
        if (next > data.Length) return false;
        text = Encoding.UTF8.GetString(data.Slice(offset, end));
        offset = next;
        return true;
    }

    private static int Pad(int length) => (length + 3) & ~3;

    private static void WriteMessage(ArrayBufferWriter<byte> writer, OscMessage message)
    {
        WriteString(writer, message.Address);
        var tags = new StringBuilder(",", message.Arguments.Count + 1);
        foreach (var a in message.Arguments) tags.Append((char)a.Type);
        WriteString(writer, tags.ToString());
        Span<byte> scratch = stackalloc byte[8];
        foreach (var a in message.Arguments)
        {
            switch (a.Type)
            {
                case OscType.Int32:
                case OscType.Char:
                case OscType.Rgba:
                case OscType.Midi:
                    WriteInt32(writer, unchecked((int)a.AsInt64));
                    break;
                case OscType.Float32:
                    BinaryPrimitives.WriteSingleBigEndian(scratch, (float)a.AsDouble);
                    writer.Write(scratch[..4]);
                    break;
                case OscType.Int64:
                case OscType.TimeTag:
                    WriteUInt64(writer, unchecked((ulong)a.AsInt64));
                    break;
                case OscType.Double:
                    BinaryPrimitives.WriteDoubleBigEndian(scratch, a.AsDouble);
                    writer.Write(scratch);
                    break;
                case OscType.String:
                case OscType.Symbol:
                    WriteString(writer, a.AsString!);
                    break;
                case OscType.Blob:
                    var blob = a.AsBlob!;
                    WriteInt32(writer, blob.Length);
                    writer.Write(blob);
                    WriteZeros(writer, Pad(blob.Length) - blob.Length);
                    break;
            }
        }
    }

    private static void WriteString(ArrayBufferWriter<byte> writer, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        writer.Write(bytes);
        WriteZeros(writer, Pad(bytes.Length + 1) - bytes.Length);
    }

    private static void WriteZeros(ArrayBufferWriter<byte> writer, int count)
    {
        if (count <= 0) return;
        writer.GetSpan(count)[..count].Clear();
        writer.Advance(count);
    }

    private static void WriteInt32(ArrayBufferWriter<byte> writer, int value)
    {
        BinaryPrimitives.WriteInt32BigEndian(writer.GetSpan(4), value);
        writer.Advance(4);
    }

    private static void WriteUInt64(ArrayBufferWriter<byte> writer, ulong value)
    {
        BinaryPrimitives.WriteUInt64BigEndian(writer.GetSpan(8), value);
        writer.Advance(8);
    }
}
