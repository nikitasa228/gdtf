using System.Buffers.Binary;
using Candela.Dmx.Input;

namespace Candela.Protocols.ArtNet;

/// <summary>Art-Net OpCodes (Art-Net 4 specification, little-endian on the wire).</summary>
public enum ArtNetOpCode : ushort
{
    /// <summary>ArtPoll: discovery request from a controller.</summary>
    Poll = 0x2000,

    /// <summary>ArtPollReply: a node describes itself.</summary>
    PollReply = 0x2100,

    /// <summary>ArtDiagData: diagnostics text.</summary>
    DiagData = 0x2300,

    /// <summary>ArtCommand: text commands.</summary>
    Command = 0x2400,

    /// <summary>ArtDataRequest.</summary>
    DataRequest = 0x2700,

    /// <summary>ArtDataReply.</summary>
    DataReply = 0x2800,

    /// <summary>ArtDmx (OpOutput): one universe of DMX512 with start code 0.</summary>
    Dmx = 0x5000,

    /// <summary>ArtNzs: DMX512 with a non-zero start code.</summary>
    Nzs = 0x5100,

    /// <summary>ArtSync: release the ArtDmx frames buffered since the previous ArtSync.</summary>
    Sync = 0x5200,

    /// <summary>ArtAddress: remote programming of a node.</summary>
    Address = 0x6000,

    /// <summary>ArtInput: enable/disable inputs.</summary>
    Input = 0x7000,

    /// <summary>ArtTodRequest (RDM).</summary>
    TodRequest = 0x8000,

    /// <summary>ArtTodData (RDM).</summary>
    TodData = 0x8100,

    /// <summary>ArtTodControl (RDM).</summary>
    TodControl = 0x8200,

    /// <summary>ArtRdm.</summary>
    Rdm = 0x8300,

    /// <summary>ArtRdmSub.</summary>
    RdmSub = 0x8400,

    /// <summary>ArtTimeCode: SMPTE/EBU timecode.</summary>
    TimeCode = 0x9700,

    /// <summary>ArtTimeSync: real-time clock.</summary>
    TimeSync = 0x9800,

    /// <summary>ArtTrigger: macro/show triggers.</summary>
    Trigger = 0x9900,

    /// <summary>ArtDirectory.</summary>
    Directory = 0x9A00,

    /// <summary>ArtDirectoryReply.</summary>
    DirectoryReply = 0x9B00,

    /// <summary>ArtIpProg: remote IP programming.</summary>
    IpProg = 0xF800,

    /// <summary>ArtIpProgReply.</summary>
    IpProgReply = 0xF900,
}

/// <summary>Art-Net constants and helpers shared by the packet types.</summary>
public static class ArtNetProtocol
{
    /// <summary>The UDP port of every Art-Net packet (0x1936).</summary>
    public const int DefaultPort = 6454;

    /// <summary>Art-Net protocol revision written by Candela (Art-Net 4 uses 14).</summary>
    public const int ProtocolVersion = 14;

    /// <summary>Largest DMX payload of an ArtDmx packet.</summary>
    public const int MaxDmxLength = 512;

    /// <summary>Highest 15-bit port-address (Net 127, SubNet 15, Universe 15).</summary>
    public const int MaxPortAddress = 0x7FFF;

    /// <summary>The 8-byte packet id "Art-Net" followed by a zero byte.</summary>
    public static ReadOnlySpan<byte> PacketId => "Art-Net\0"u8;

    /// <summary>Reads the OpCode of a datagram; false when it does not start with the Art-Net id.</summary>
    public static bool TryReadOpCode(ReadOnlySpan<byte> packet, out ArtNetOpCode opCode)
    {
        opCode = 0;
        if (packet.Length < 10 || !packet[..8].SequenceEqual(PacketId)) return false;
        opCode = (ArtNetOpCode)BinaryPrimitives.ReadUInt16LittleEndian(packet[8..]);
        return true;
    }

    /// <summary>Builds a 15-bit port-address from Net (0–127), SubNet (0–15) and Universe (0–15).</summary>
    public static int ToPortAddress(int net, int subNet, int universe)
    {
        if (net is < 0 or > 127) throw new ArgumentOutOfRangeException(nameof(net), net, "Net is 0..127.");
        if (subNet is < 0 or > 15) throw new ArgumentOutOfRangeException(nameof(subNet), subNet, "SubNet is 0..15.");
        if (universe is < 0 or > 15) throw new ArgumentOutOfRangeException(nameof(universe), universe, "Universe is 0..15.");
        return (net << 8) | (subNet << 4) | universe;
    }

    /// <summary>Formats a port-address the way consoles show it: "Net:SubNet:Universe" ("0:0:1").</summary>
    public static string FormatPortAddress(int portAddress) => $"{(portAddress >> 8) & 0x7F}:{(portAddress >> 4) & 0x0F}:{portAddress & 0x0F}";

    internal static int WriteHeader(Span<byte> destination, ArtNetOpCode opCode, bool withVersion = true)
    {
        PacketId.CopyTo(destination);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[8..], (ushort)opCode);
        if (!withVersion) return 10;
        destination[10] = 0;
        destination[11] = ProtocolVersion;
        return 12;
    }

    internal static bool HasVersion(ReadOnlySpan<byte> packet) =>
        packet.Length >= 12 && ((packet[10] << 8) | packet[11]) >= ProtocolVersion;

    internal static void EnsureLength(Span<byte> destination, int length)
    {
        if (destination.Length < length)
            throw new ArgumentException($"The destination needs at least {length} bytes.", nameof(destination));
    }
}

/// <summary>A parsed ArtDmx packet. A view over the received datagram: <see cref="Data"/> is only valid while the
/// datagram buffer is.</summary>
public readonly ref struct ArtDmxPacket
{
    /// <summary>Bytes before the DMX data.</summary>
    public const int HeaderLength = 18;

    private ArtDmxPacket(byte sequence, byte physical, int portAddress, ReadOnlySpan<byte> data)
    {
        Sequence = sequence;
        Physical = physical;
        PortAddress = portAddress;
        Data = data;
    }

    /// <summary>Sequence 1–255 for re-ordering, 0 when the sender disabled sequencing.</summary>
    public byte Sequence { get; }

    /// <summary>The physical input port of the sender (informational; Candela keys sources by IP + physical).</summary>
    public byte Physical { get; }

    /// <summary>15-bit port-address: Net (bits 14–8), SubNet (7–4), Universe (3–0).</summary>
    public int PortAddress { get; }

    /// <summary>DMX slots 1..n (start code 0 implied).</summary>
    public ReadOnlySpan<byte> Data { get; }

    /// <summary>Parses an ArtDmx datagram (OpCode 0x5000, ProtVer ≥ 14, length 1–512 that fits the datagram). The
    /// specification asks for even lengths 2–512; odd lengths from older consoles are accepted as the data is
    /// unambiguous.</summary>
    public static bool TryParse(ReadOnlySpan<byte> packet, out ArtDmxPacket result, out string? error)
    {
        result = default;
        if (!ArtNetProtocol.TryReadOpCode(packet, out var op) || op != ArtNetOpCode.Dmx)
        {
            error = "not an ArtDmx packet";
            return false;
        }
        if (packet.Length < HeaderLength)
        {
            error = "ArtDmx shorter than its header";
            return false;
        }
        if (!ArtNetProtocol.HasVersion(packet))
        {
            error = "ArtDmx protocol version below 14";
            return false;
        }
        int length = (packet[16] << 8) | packet[17];
        if (length is < 1 or > ArtNetProtocol.MaxDmxLength)
        {
            error = "ArtDmx length outside 1..512";
            return false;
        }
        if (HeaderLength + length > packet.Length)
        {
            error = "ArtDmx length larger than the datagram";
            return false;
        }
        int portAddress = ((packet[15] & 0x7F) << 8) | packet[14];
        result = new ArtDmxPacket(packet[12], packet[13], portAddress, packet.Slice(HeaderLength, length));
        error = null;
        return true;
    }

    /// <summary>Parses an ArtDmx datagram (see the other overload).</summary>
    public static bool TryParse(ReadOnlySpan<byte> packet, out ArtDmxPacket result) => TryParse(packet, out result, out _);

    /// <summary>
    /// Writes an ArtDmx packet. Odd lengths are padded with a zero slot so the length is even (as the specification
    /// requires).
    /// </summary>
    /// <returns>Bytes written (18 + even data length).</returns>
    public static int Write(Span<byte> destination, int portAddress, ReadOnlySpan<byte> data, byte sequence = 0, byte physical = 0)
    {
        if (portAddress is < 0 or > ArtNetProtocol.MaxPortAddress)
            throw new ArgumentOutOfRangeException(nameof(portAddress), portAddress, "Port-address is 0..32767.");
        if (data.Length is < 1 or > ArtNetProtocol.MaxDmxLength)
            throw new ArgumentOutOfRangeException(nameof(data), data.Length, "ArtDmx carries 1..512 slots.");
        int length = data.Length + (data.Length & 1);
        ArtNetProtocol.EnsureLength(destination, HeaderLength + length);
        ArtNetProtocol.WriteHeader(destination, ArtNetOpCode.Dmx);
        destination[12] = sequence;
        destination[13] = physical;
        destination[14] = (byte)(portAddress & 0xFF);
        destination[15] = (byte)(portAddress >> 8);
        destination[16] = (byte)(length >> 8);
        destination[17] = (byte)length;
        data.CopyTo(destination[HeaderLength..]);
        if (length != data.Length) destination[HeaderLength + data.Length] = 0;
        return HeaderLength + length;
    }
}

/// <summary>An ArtPoll discovery request.</summary>
/// <param name="Flags">TalkToMe flags: bit 1 = send ArtPollReply when node conditions change, bit 5 = targeted mode.</param>
/// <param name="DiagPriority">Lowest diagnostics priority the controller wants.</param>
/// <param name="TargetPortAddressTop">Upper bound of the targeted port-address range (targeted mode only).</param>
/// <param name="TargetPortAddressBottom">Lower bound of the targeted port-address range (targeted mode only).</param>
public readonly record struct ArtPollPacket(byte Flags, byte DiagPriority, int TargetPortAddressTop, int TargetPortAddressBottom)
{
    /// <summary>Length Candela writes (Art-Net 4 with targeted-mode and ESTA/OEM fields).</summary>
    public const int Length = 22;

    /// <summary>Flag bit: send ArtPollReply whenever node conditions change.</summary>
    public const byte FlagReplyOnChange = 0x02;

    /// <summary>Flag bit: only nodes with a port-address inside the target range reply.</summary>
    public const byte FlagTargetedMode = 0x20;

    /// <summary>True when the controller asked for unsolicited replies on change.</summary>
    public bool ReplyOnChange => (Flags & FlagReplyOnChange) != 0;

    /// <summary>True when only nodes in the target port-address range should reply.</summary>
    public bool TargetedMode => (Flags & FlagTargetedMode) != 0;

    /// <summary>True when <paramref name="portAddress"/> is addressed by this poll (always true outside targeted mode).</summary>
    public bool Targets(int portAddress) =>
        !TargetedMode || (portAddress >= TargetPortAddressBottom && portAddress <= TargetPortAddressTop);

    /// <summary>Parses an ArtPoll (OpCode 0x2000, ProtVer ≥ 14). Short Art-Net 3 polls (12–14 bytes) are accepted;
    /// missing fields read as zero.</summary>
    public static bool TryParse(ReadOnlySpan<byte> packet, out ArtPollPacket result, out string? error)
    {
        result = default;
        if (!ArtNetProtocol.TryReadOpCode(packet, out var op) || op != ArtNetOpCode.Poll)
        {
            error = "not an ArtPoll packet";
            return false;
        }
        if (!ArtNetProtocol.HasVersion(packet))
        {
            error = "ArtPoll protocol version below 14";
            return false;
        }
        byte flags = packet.Length > 12 ? packet[12] : (byte)0;
        byte diag = packet.Length > 13 ? packet[13] : (byte)0;
        int top = packet.Length >= 16 ? ((packet[14] << 8) | packet[15]) & ArtNetProtocol.MaxPortAddress : ArtNetProtocol.MaxPortAddress;
        int bottom = packet.Length >= 18 ? ((packet[16] << 8) | packet[17]) & ArtNetProtocol.MaxPortAddress : 0;
        result = new ArtPollPacket(flags, diag, top, bottom);
        error = null;
        return true;
    }

    /// <summary>Writes the packet (22 bytes).</summary>
    public int Write(Span<byte> destination, ushort estaManufacturer = 0x7FF0, ushort oem = 0xFFFF)
    {
        ArtNetProtocol.EnsureLength(destination, Length);
        ArtNetProtocol.WriteHeader(destination, ArtNetOpCode.Poll);
        destination[12] = Flags;
        destination[13] = DiagPriority;
        BinaryPrimitives.WriteUInt16BigEndian(destination[14..], (ushort)TargetPortAddressTop);
        BinaryPrimitives.WriteUInt16BigEndian(destination[16..], (ushort)TargetPortAddressBottom);
        BinaryPrimitives.WriteUInt16BigEndian(destination[18..], estaManufacturer);
        BinaryPrimitives.WriteUInt16BigEndian(destination[20..], oem);
        return Length;
    }
}

/// <summary>An ArtTimeCode packet (OpCode 0x9700).</summary>
/// <param name="Hours">0–23.</param>
/// <param name="Minutes">0–59.</param>
/// <param name="Seconds">0–59.</param>
/// <param name="Frames">0 – frame rate − 1.</param>
/// <param name="Type">Frame rate type (Film 24, EBU 25, DF 29.97, SMPTE 30).</param>
/// <param name="StreamId">0 = master timecode stream.</param>
public readonly record struct ArtTimeCodePacket(int Hours, int Minutes, int Seconds, int Frames, TimecodeType Type, byte StreamId = 0)
{
    /// <summary>Packet length.</summary>
    public const int Length = 19;

    /// <summary>The position as a hub <see cref="Timecode"/>.</summary>
    public Timecode ToTimecode() => new(Hours, Minutes, Seconds, Frames, Type);

    /// <summary>Creates the packet from a hub <see cref="Timecode"/>.</summary>
    public static ArtTimeCodePacket From(Timecode timecode, byte streamId = 0) =>
        new(timecode.Hours, timecode.Minutes, timecode.Seconds, timecode.Frames, timecode.Type, streamId);

    /// <summary>Parses an ArtTimeCode; rejects out-of-range fields (frames beyond the type's frame rate, hours &gt; 23,
    /// type &gt; 3).</summary>
    public static bool TryParse(ReadOnlySpan<byte> packet, out ArtTimeCodePacket result, out string? error)
    {
        result = default;
        if (!ArtNetProtocol.TryReadOpCode(packet, out var op) || op != ArtNetOpCode.TimeCode)
        {
            error = "not an ArtTimeCode packet";
            return false;
        }
        if (packet.Length < Length)
        {
            error = "ArtTimeCode too short";
            return false;
        }
        if (!ArtNetProtocol.HasVersion(packet))
        {
            error = "ArtTimeCode protocol version below 14";
            return false;
        }
        int frames = packet[14], seconds = packet[15], minutes = packet[16], hours = packet[17], type = packet[18];
        if (type > 3)
        {
            error = "ArtTimeCode type outside 0..3";
            return false;
        }
        int maxFrames = type switch { 0 => 24, 1 => 25, _ => 30 };
        if (frames >= maxFrames || seconds > 59 || minutes > 59 || hours > 23)
        {
            error = "ArtTimeCode field out of range";
            return false;
        }
        result = new ArtTimeCodePacket(hours, minutes, seconds, frames, (TimecodeType)type, packet[13]);
        error = null;
        return true;
    }

    /// <summary>Writes the packet (19 bytes).</summary>
    public int Write(Span<byte> destination)
    {
        ArtNetProtocol.EnsureLength(destination, Length);
        ArtNetProtocol.WriteHeader(destination, ArtNetOpCode.TimeCode);
        destination[12] = 0;
        destination[13] = StreamId;
        destination[14] = (byte)Frames;
        destination[15] = (byte)Seconds;
        destination[16] = (byte)Minutes;
        destination[17] = (byte)Hours;
        destination[18] = (byte)Type;
        return Length;
    }
}

/// <summary>The ArtSync packet (OpCode 0x5200).</summary>
public static class ArtSyncPacket
{
    /// <summary>Packet length.</summary>
    public const int Length = 14;

    /// <summary>True when <paramref name="packet"/> is a valid ArtSync.</summary>
    public static bool TryParse(ReadOnlySpan<byte> packet, out string? error)
    {
        if (!ArtNetProtocol.TryReadOpCode(packet, out var op) || op != ArtNetOpCode.Sync)
        {
            error = "not an ArtSync packet";
            return false;
        }
        if (!ArtNetProtocol.HasVersion(packet))
        {
            error = "ArtSync protocol version below 14";
            return false;
        }
        error = null;
        return true;
    }

    /// <summary>Writes the packet (14 bytes).</summary>
    public static int Write(Span<byte> destination)
    {
        ArtNetProtocol.EnsureLength(destination, Length);
        ArtNetProtocol.WriteHeader(destination, ArtNetOpCode.Sync);
        destination[12] = 0;
        destination[13] = 0;
        return Length;
    }
}
