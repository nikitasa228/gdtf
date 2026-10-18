using System.Buffers.Binary;

namespace Candela.Protocols.Enttec;

/// <summary>Message labels of the EntTec DMX USB Pro widget API.</summary>
public enum EnttecProLabel : byte
{
    /// <summary>Reprogram firmware request.</summary>
    ReprogramFirmware = 1,

    /// <summary>Program flash page request/reply.</summary>
    ProgramFlashPage = 2,

    /// <summary>Get widget parameters request (2 bytes: user-config size) / reply (firmware, timings, rate, config).</summary>
    GetWidgetParameters = 3,

    /// <summary>Set widget parameters request.</summary>
    SetWidgetParameters = 4,

    /// <summary>Received DMX packet (widget → host): status byte, start code, slots.</summary>
    ReceivedDmx = 5,

    /// <summary>Output-only send DMX packet request (host → widget): start code, 24–512 slots.</summary>
    OutputOnlySendDmx = 6,

    /// <summary>Send RDM packet request.</summary>
    SendRdm = 7,

    /// <summary>Receive DMX on change (1 byte: 0 = send always, 1 = send only on change).</summary>
    ReceiveDmxOnChange = 8,

    /// <summary>Received DMX change-of-state packet (widget → host).</summary>
    ReceivedDmxChangeOfState = 9,

    /// <summary>Get widget serial number request / reply (4 bytes BCD, least significant first).</summary>
    GetWidgetSerialNumber = 10,

    /// <summary>Send RDM discovery request.</summary>
    SendRdmDiscovery = 11,
}

/// <summary>Widget parameters from a <see cref="EnttecProLabel.GetWidgetParameters"/> reply.</summary>
/// <param name="FirmwareVersion">Firmware version (LSB, MSB).</param>
/// <param name="BreakTimeUnits">DMX output break time in 10.67 µs units (9–127).</param>
/// <param name="MarkAfterBreakUnits">DMX output mark-after-break time in 10.67 µs units (1–127).</param>
/// <param name="OutputRateHz">DMX output rate in packets per second (0 = as fast as possible, up to 40).</param>
/// <param name="UserConfiguration">User-defined configuration bytes.</param>
public sealed record EnttecWidgetParameters(
    ushort FirmwareVersion,
    byte BreakTimeUnits,
    byte MarkAfterBreakUnits,
    byte OutputRateHz,
    IReadOnlyList<byte> UserConfiguration)
{
    /// <summary>Duration of one timing unit in microseconds.</summary>
    public const double TimingUnitMicroseconds = 10.67;

    /// <summary>Break time in microseconds.</summary>
    public double BreakTimeMicroseconds => BreakTimeUnits * TimingUnitMicroseconds;

    /// <summary>Mark-after-break time in microseconds.</summary>
    public double MarkAfterBreakMicroseconds => MarkAfterBreakUnits * TimingUnitMicroseconds;
}

/// <summary>
/// Encodes and decodes the widget's message frames: start of message 0x7E, label, data length (LSB, MSB), data
/// (0–600 bytes), end of message 0xE7.
/// </summary>
public static class EnttecProFrame
{
    /// <summary>Start-of-message delimiter.</summary>
    public const byte StartOfMessage = 0x7E;

    /// <summary>End-of-message delimiter.</summary>
    public const byte EndOfMessage = 0xE7;

    /// <summary>Largest data length the widget accepts.</summary>
    public const int MaxPayload = 600;

    /// <summary>Framing bytes around the data (SOM, label, 2 length bytes, EOM).</summary>
    public const int Overhead = 5;

    /// <summary>Smallest DMX packet the widget outputs (slots).</summary>
    public const int MinOutputSlots = 24;

    /// <summary>Encodes one frame; returns bytes written.</summary>
    public static int Encode(Span<byte> destination, EnttecProLabel label, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxPayload) throw new ArgumentOutOfRangeException(nameof(payload), payload.Length, "At most 600 data bytes.");
        int length = payload.Length + Overhead;
        if (destination.Length < length) throw new ArgumentException($"The destination needs {length} bytes.", nameof(destination));
        destination[0] = StartOfMessage;
        destination[1] = (byte)label;
        BinaryPrimitives.WriteUInt16LittleEndian(destination[2..], (ushort)payload.Length);
        payload.CopyTo(destination[4..]);
        destination[4 + payload.Length] = EndOfMessage;
        return length;
    }

    /// <summary>Encodes one frame into a new array.</summary>
    public static byte[] Encode(EnttecProLabel label, ReadOnlySpan<byte> payload)
    {
        var frame = new byte[payload.Length + Overhead];
        Encode(frame, label, payload);
        return frame;
    }

    /// <summary>
    /// Encodes an <see cref="EnttecProLabel.OutputOnlySendDmx"/> frame: start code followed by the slots, padded with
    /// zeros to the widget's minimum of 24 slots.
    /// </summary>
    public static int EncodeSendDmx(Span<byte> destination, ReadOnlySpan<byte> slots, byte startCode = 0)
    {
        if (slots.Length > 512) throw new ArgumentOutOfRangeException(nameof(slots), slots.Length, "At most 512 slots.");
        int count = Math.Max(slots.Length, MinOutputSlots);
        int length = 1 + count + Overhead;
        if (destination.Length < length) throw new ArgumentException($"The destination needs {length} bytes.", nameof(destination));
        destination[0] = StartOfMessage;
        destination[1] = (byte)EnttecProLabel.OutputOnlySendDmx;
        BinaryPrimitives.WriteUInt16LittleEndian(destination[2..], (ushort)(count + 1));
        destination[4] = startCode;
        slots.CopyTo(destination[5..]);
        destination.Slice(5 + slots.Length, count - slots.Length).Clear();
        destination[5 + count] = EndOfMessage;
        return length;
    }

    /// <summary>Encodes a <see cref="EnttecProLabel.GetWidgetParameters"/> request.</summary>
    public static byte[] EncodeGetParameters(ushort userConfigurationSize = 0)
    {
        Span<byte> payload = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, userConfigurationSize);
        return Encode(EnttecProLabel.GetWidgetParameters, payload);
    }

    /// <summary>Encodes a <see cref="EnttecProLabel.GetWidgetSerialNumber"/> request.</summary>
    public static byte[] EncodeGetSerialNumber() => Encode(EnttecProLabel.GetWidgetSerialNumber, ReadOnlySpan<byte>.Empty);

    /// <summary>Encodes a <see cref="EnttecProLabel.ReceiveDmxOnChange"/> request (false = send every received frame).</summary>
    public static byte[] EncodeReceiveDmxOnChange(bool onChangeOnly) =>
        Encode(EnttecProLabel.ReceiveDmxOnChange, new[] { onChangeOnly ? (byte)1 : (byte)0 });

    /// <summary>Encodes a <see cref="EnttecProLabel.ReceivedDmx"/> frame as the widget sends it (fake widgets, tests).</summary>
    public static byte[] EncodeReceivedDmx(ReadOnlySpan<byte> slots, byte status = 0, byte startCode = 0)
    {
        if (slots.Length > 512) throw new ArgumentOutOfRangeException(nameof(slots), slots.Length, "At most 512 slots.");
        var payload = new byte[2 + slots.Length];
        payload[0] = status;
        payload[1] = startCode;
        slots.CopyTo(payload.AsSpan(2));
        return Encode(EnttecProLabel.ReceivedDmx, payload);
    }

    /// <summary>Encodes a widget-parameters reply (fake widgets, tests).</summary>
    public static byte[] EncodeParametersReply(EnttecWidgetParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var payload = new byte[5 + parameters.UserConfiguration.Count];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, parameters.FirmwareVersion);
        payload[2] = parameters.BreakTimeUnits;
        payload[3] = parameters.MarkAfterBreakUnits;
        payload[4] = parameters.OutputRateHz;
        for (int i = 0; i < parameters.UserConfiguration.Count; i++) payload[5 + i] = parameters.UserConfiguration[i];
        return Encode(EnttecProLabel.GetWidgetParameters, payload);
    }

    /// <summary>Encodes a serial-number reply (fake widgets, tests); <paramref name="serial"/> is written as 8 BCD digits.</summary>
    public static byte[] EncodeSerialNumberReply(uint serial)
    {
        if (serial > 99_999_999) throw new ArgumentOutOfRangeException(nameof(serial), serial, "Serial numbers have 8 decimal digits.");
        var payload = new byte[4];
        for (int i = 0; i < 4; i++)
        {
            uint two = serial % 100;
            serial /= 100;
            payload[i] = (byte)((two / 10) << 4 | two % 10);
        }
        return Encode(EnttecProLabel.GetWidgetSerialNumber, payload);
    }

    /// <summary>
    /// Decodes a <see cref="EnttecProLabel.ReceivedDmx"/> payload: status (bit 0 receive queue overflowed, bit 1 receive
    /// overrun), start code and slots.
    /// </summary>
    public static bool TryDecodeReceivedDmx(ReadOnlySpan<byte> payload, out byte status, out byte startCode, out ReadOnlySpan<byte> slots)
    {
        status = 0;
        startCode = 0;
        slots = default;
        if (payload.Length < 2) return false;
        status = payload[0];
        startCode = payload[1];
        slots = payload[2..];
        if (slots.Length > 512) slots = slots[..512];
        return true;
    }

    /// <summary>
    /// Applies a <see cref="EnttecProLabel.ReceivedDmxChangeOfState"/> payload to a 512-slot buffer: start byte
    /// (block × 8), a 5-byte (40-bit) changed mask, then one value per set bit. Index 0 of the block is the start code
    /// (skipped). Returns false for a malformed payload (buffer unchanged).
    /// </summary>
    public static bool TryApplyChangeOfState(ReadOnlySpan<byte> payload, Span<byte> slots512, out int changed)
    {
        changed = 0;
        if (payload.Length < 6 || slots512.Length < 512) return false;
        int start = payload[0] * 8;
        var mask = payload.Slice(1, 5);
        var values = payload[6..];
        int needed = 0;
        for (int i = 0; i < 40; i++)
            if ((mask[i >> 3] & (1 << (i & 7))) != 0) needed++;
        if (values.Length < needed) return false;
        int v = 0;
        for (int i = 0; i < 40; i++)
        {
            if ((mask[i >> 3] & (1 << (i & 7))) == 0) continue;
            int index = start + i; // 0 = start code, 1..512 = slots
            byte value = values[v++];
            if (index is >= 1 and <= 512)
            {
                slots512[index - 1] = value;
                changed++;
            }
        }
        return true;
    }

    /// <summary>Decodes a widget-parameters reply.</summary>
    public static bool TryDecodeParameters(ReadOnlySpan<byte> payload, out EnttecWidgetParameters? parameters)
    {
        parameters = null;
        if (payload.Length < 5) return false;
        parameters = new EnttecWidgetParameters(BinaryPrimitives.ReadUInt16LittleEndian(payload), payload[2], payload[3], payload[4], payload[5..].ToArray());
        return true;
    }

    /// <summary>
    /// Decodes a serial-number reply: 4 bytes of BCD, least significant byte first (8 decimal digits). Bytes that are
    /// not valid BCD are read as a plain little-endian number instead.
    /// </summary>
    public static bool TryDecodeSerialNumber(ReadOnlySpan<byte> payload, out uint serial)
    {
        serial = 0;
        if (payload.Length < 4) return false;
        uint value = 0, scale = 1;
        for (int i = 0; i < 4; i++)
        {
            int hi = payload[i] >> 4, lo = payload[i] & 0xF;
            if (hi > 9 || lo > 9)
            {
                serial = BinaryPrimitives.ReadUInt32LittleEndian(payload);
                return true;
            }
            value += (uint)(hi * 10 + lo) * scale;
            scale *= 100;
        }
        serial = value;
        return true;
    }
}

/// <summary>
/// Turns the widget's byte stream (which arrives in arbitrary chunks) into frames. Garbage before a start byte is
/// skipped; a frame with an impossible length or a wrong end byte is dropped and decoding resynchronises at the next
/// start byte inside it. Allocation-free after construction.
/// </summary>
public sealed class EnttecProFrameDecoder
{
    private const int Capacity = 4096;
    private readonly byte[] _buffer = new byte[Capacity];
    private readonly FrameHandler _onFrame;
    private int _count;

    /// <summary>Receives one decoded frame; the payload span is only valid during the call.</summary>
    public delegate void FrameHandler(EnttecProLabel label, ReadOnlySpan<byte> payload);

    /// <summary>Creates the decoder.</summary>
    public EnttecProFrameDecoder(FrameHandler onFrame) => _onFrame = onFrame ?? throw new ArgumentNullException(nameof(onFrame));

    /// <summary>Frames decoded.</summary>
    public long Frames { get; private set; }

    /// <summary>Frames dropped (bad length or end byte).</summary>
    public long BadFrames { get; private set; }

    /// <summary>Bytes skipped while looking for a start byte.</summary>
    public long DiscardedBytes { get; private set; }

    /// <summary>Feeds received bytes; calls the handler for every complete frame.</summary>
    public void Feed(ReadOnlySpan<byte> bytes)
    {
        while (!bytes.IsEmpty)
        {
            int n = Math.Min(bytes.Length, Capacity - _count);
            bytes[..n].CopyTo(_buffer.AsSpan(_count));
            _count += n;
            bytes = bytes[n..];
            Parse();
        }
    }

    /// <summary>Forgets a partly received frame (after the transport reopened).</summary>
    public void Reset() => _count = 0;

    private void Parse()
    {
        int pos = 0;
        while (true)
        {
            int som = Array.IndexOf(_buffer, EnttecProFrame.StartOfMessage, pos, _count - pos);
            if (som < 0)
            {
                DiscardedBytes += _count - pos;
                pos = _count;
                break;
            }
            DiscardedBytes += som - pos;
            pos = som;
            if (_count - pos < 4) break;
            int length = _buffer[pos + 2] | (_buffer[pos + 3] << 8);
            if (length > EnttecProFrame.MaxPayload)
            {
                BadFrames++;
                pos++;
                continue;
            }
            if (_count - pos < length + EnttecProFrame.Overhead) break;
            if (_buffer[pos + 4 + length] != EnttecProFrame.EndOfMessage)
            {
                BadFrames++;
                pos++;
                continue;
            }
            Frames++;
            _onFrame((EnttecProLabel)_buffer[pos + 1], _buffer.AsSpan(pos + 4, length));
            pos += length + EnttecProFrame.Overhead;
        }
        // Keep the unconsumed tail (at most one incomplete frame, < 605 bytes) at the front.
        if (pos > 0)
        {
            Buffer.BlockCopy(_buffer, pos, _buffer, 0, _count - pos);
            _count -= pos;
        }
    }
}
