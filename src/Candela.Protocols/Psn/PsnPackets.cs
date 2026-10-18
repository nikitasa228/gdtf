using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace Candela.Protocols.Psn;

/// <summary>PosiStageNet 2.0 constants (chunk ids, defaults).</summary>
public static class PsnProtocol
{
    /// <summary>Default UDP port.</summary>
    public const int DefaultPort = 56565;

    /// <summary>Default multicast group.</summary>
    public const string DefaultMulticastGroup = "236.10.10.10";

    /// <summary>Protocol major version handled.</summary>
    public const byte VersionHigh = 2;

    /// <summary>Protocol minor version written.</summary>
    public const byte VersionLow = 0;

    /// <summary>Root chunk of an info packet.</summary>
    public const ushort InfoPacket = 0x6756;

    /// <summary>Root chunk of a data packet.</summary>
    public const ushort DataPacket = 0x6755;

    /// <summary>Packet header chunk (both packet types).</summary>
    public const ushort PacketHeader = 0x0000;

    /// <summary>Info: system name chunk.</summary>
    public const ushort InfoSystemName = 0x0001;

    /// <summary>Info: tracker list chunk.</summary>
    public const ushort InfoTrackerList = 0x0002;

    /// <summary>Info: tracker name chunk (inside a tracker chunk).</summary>
    public const ushort InfoTrackerName = 0x0000;

    /// <summary>Data: tracker list chunk.</summary>
    public const ushort DataTrackerList = 0x0001;

    /// <summary>Data: position (3 × float32, metres).</summary>
    public const ushort TrackerPosition = 0x0000;

    /// <summary>Data: speed (3 × float32, m/s).</summary>
    public const ushort TrackerSpeed = 0x0001;

    /// <summary>Data: orientation (3 × float32, rotation axis scaled by the angle in radians).</summary>
    public const ushort TrackerOrientation = 0x0002;

    /// <summary>Data: status (float32 validity).</summary>
    public const ushort TrackerStatus = 0x0003;

    /// <summary>Data: acceleration (3 × float32, m/s²).</summary>
    public const ushort TrackerAcceleration = 0x0004;

    /// <summary>Data: target position (3 × float32, metres).</summary>
    public const ushort TrackerTargetPosition = 0x0005;

    /// <summary>Data: tracker timestamp (uint64, µs).</summary>
    public const ushort TrackerTimestamp = 0x0006;
}

/// <summary>Which fields a tracker record carries.</summary>
[Flags]
public enum PsnTrackerFields
{
    /// <summary>Nothing.</summary>
    None = 0,

    /// <summary><see cref="PsnTracker.Position"/>.</summary>
    Position = 1,

    /// <summary><see cref="PsnTracker.Speed"/>.</summary>
    Speed = 2,

    /// <summary><see cref="PsnTracker.Orientation"/>.</summary>
    Orientation = 4,

    /// <summary><see cref="PsnTracker.Validity"/>.</summary>
    Status = 8,

    /// <summary><see cref="PsnTracker.Acceleration"/>.</summary>
    Acceleration = 16,

    /// <summary><see cref="PsnTracker.TargetPosition"/>.</summary>
    TargetPosition = 32,

    /// <summary><see cref="PsnTracker.TimestampMicroseconds"/>.</summary>
    Timestamp = 64,
}

/// <summary>
/// One tracker's data from a PSN data packet. Vectors are kept exactly as sent, in the PSN server's coordinate frame
/// (metres, m/s, m/s²; orientation is a rotation vector — axis × angle in radians); converting to Candela's Z-up stage
/// frame depends on how the tracking system was calibrated and is done by the consumer.
/// </summary>
/// <param name="Id">Tracker id (0–65535).</param>
/// <param name="Fields">Which of the values below were present.</param>
/// <param name="Position">Position in metres.</param>
/// <param name="Speed">Velocity in m/s.</param>
/// <param name="Orientation">Rotation vector (axis × angle, radians).</param>
/// <param name="Validity">Status/validity value sent by the server.</param>
/// <param name="Acceleration">Acceleration in m/s².</param>
/// <param name="TargetPosition">Target position in metres.</param>
/// <param name="TimestampMicroseconds">Tracker timestamp in µs (server clock).</param>
public readonly record struct PsnTracker(
    int Id,
    PsnTrackerFields Fields,
    Vector3 Position,
    Vector3 Speed,
    Vector3 Orientation,
    float Validity,
    Vector3 Acceleration,
    Vector3 TargetPosition,
    ulong TimestampMicroseconds)
{
    /// <summary>Copies the fields present in <paramref name="update"/> over this record.</summary>
    public PsnTracker Merge(in PsnTracker update)
    {
        var f = update.Fields;
        return new PsnTracker(
            update.Id,
            Fields | f,
            (f & PsnTrackerFields.Position) != 0 ? update.Position : Position,
            (f & PsnTrackerFields.Speed) != 0 ? update.Speed : Speed,
            (f & PsnTrackerFields.Orientation) != 0 ? update.Orientation : Orientation,
            (f & PsnTrackerFields.Status) != 0 ? update.Validity : Validity,
            (f & PsnTrackerFields.Acceleration) != 0 ? update.Acceleration : Acceleration,
            (f & PsnTrackerFields.TargetPosition) != 0 ? update.TargetPosition : TargetPosition,
            (f & PsnTrackerFields.Timestamp) != 0 ? update.TimestampMicroseconds : TimestampMicroseconds);
    }
}

/// <summary>The header chunk of info and data packets.</summary>
/// <param name="TimestampMicroseconds">Server time in µs.</param>
/// <param name="VersionHigh">Protocol major version (2).</param>
/// <param name="VersionLow">Protocol minor version.</param>
/// <param name="FrameId">Frame counter (wraps at 256).</param>
/// <param name="FramePacketCount">Number of UDP packets that make up this frame.</param>
public readonly record struct PsnPacketHeader(ulong TimestampMicroseconds, byte VersionHigh, byte VersionLow, byte FrameId, byte FramePacketCount);

/// <summary>Kind of a PSN datagram.</summary>
public enum PsnPacketKind
{
    /// <summary>Not a PSN 2 packet.</summary>
    Invalid,

    /// <summary>Info packet (system name, tracker names).</summary>
    Info,

    /// <summary>Data packet (tracker values).</summary>
    Data,
}

/// <summary>
/// Reads PSN 2.0 packets: a tree of chunks, each with a 32-bit little-endian header (id 16 bits, data length 15 bits,
/// has-subchunks 1 bit). Unknown chunks are skipped; every length is checked against its parent, so malformed input
/// returns false instead of throwing.
/// </summary>
public static class PsnPacketReader
{
    /// <summary>Says which packet this is (by its root chunk) without parsing it.</summary>
    public static PsnPacketKind GetKind(ReadOnlySpan<byte> packet)
    {
        int offset = 0;
        if (!TryReadChunk(packet, ref offset, out var id, out _, out _)) return PsnPacketKind.Invalid;
        return id switch
        {
            PsnProtocol.DataPacket => PsnPacketKind.Data,
            PsnProtocol.InfoPacket => PsnPacketKind.Info,
            _ => PsnPacketKind.Invalid,
        };
    }

    /// <summary>Parses a data packet; trackers are appended to <paramref name="trackers"/> (cleared first).</summary>
    public static bool TryReadData(ReadOnlySpan<byte> packet, out PsnPacketHeader header, List<PsnTracker> trackers, out string? error)
    {
        ArgumentNullException.ThrowIfNull(trackers);
        trackers.Clear();
        header = default;
        if (!TryRoot(packet, PsnProtocol.DataPacket, out var body, out error)) return false;
        bool hasHeader = false;
        int offset = 0;
        while (offset < body.Length)
        {
            if (!TryReadChunk(body, ref offset, out var id, out var data, out _)) return Fail("truncated PSN data chunk", out error, trackers);
            if (id == PsnProtocol.PacketHeader)
            {
                if (!TryReadHeader(data, out header, out error)) return Fail(error!, out error, trackers);
                hasHeader = true;
            }
            else if (id == PsnProtocol.DataTrackerList)
            {
                int t = 0;
                while (t < data.Length)
                {
                    if (!TryReadChunk(data, ref t, out var trackerId, out var trackerData, out _))
                        return Fail("truncated PSN tracker chunk", out error, trackers);
                    if (!TryReadTracker(trackerId, trackerData, out var tracker)) return Fail("malformed PSN tracker field", out error, trackers);
                    trackers.Add(tracker);
                }
            }
        }
        if (!hasHeader) return Fail("PSN data packet without header", out error, trackers);
        error = null;
        return true;
    }

    /// <summary>Parses an info packet: system name and tracker names (appended to <paramref name="trackerNames"/>,
    /// cleared first).</summary>
    public static bool TryReadInfo(ReadOnlySpan<byte> packet, out PsnPacketHeader header, out string systemName,
        List<KeyValuePair<int, string>> trackerNames, out string? error)
    {
        ArgumentNullException.ThrowIfNull(trackerNames);
        trackerNames.Clear();
        header = default;
        systemName = "";
        if (!TryRoot(packet, PsnProtocol.InfoPacket, out var body, out error)) return false;
        bool hasHeader = false;
        int offset = 0;
        while (offset < body.Length)
        {
            if (!TryReadChunk(body, ref offset, out var id, out var data, out _))
            {
                trackerNames.Clear();
                error = "truncated PSN info chunk";
                return false;
            }
            switch (id)
            {
                case PsnProtocol.PacketHeader:
                    if (!TryReadHeader(data, out header, out error))
                    {
                        trackerNames.Clear();
                        return false;
                    }
                    hasHeader = true;
                    break;
                case PsnProtocol.InfoSystemName:
                    systemName = Encoding.UTF8.GetString(data);
                    break;
                case PsnProtocol.InfoTrackerList:
                    int t = 0;
                    while (t < data.Length)
                    {
                        if (!TryReadChunk(data, ref t, out var trackerId, out var trackerData, out _))
                        {
                            trackerNames.Clear();
                            error = "truncated PSN tracker-name chunk";
                            return false;
                        }
                        int n = 0;
                        while (n < trackerData.Length)
                        {
                            if (!TryReadChunk(trackerData, ref n, out var fieldId, out var fieldData, out _))
                            {
                                trackerNames.Clear();
                                error = "truncated PSN tracker-name field";
                                return false;
                            }
                            if (fieldId == PsnProtocol.InfoTrackerName)
                                trackerNames.Add(new KeyValuePair<int, string>(trackerId, Encoding.UTF8.GetString(fieldData)));
                        }
                    }
                    break;
            }
        }
        if (!hasHeader)
        {
            trackerNames.Clear();
            error = "PSN info packet without header";
            return false;
        }
        error = null;
        return true;
    }

    private static bool TryRoot(ReadOnlySpan<byte> packet, ushort expected, out ReadOnlySpan<byte> body, out string? error)
    {
        body = default;
        int offset = 0;
        if (!TryReadChunk(packet, ref offset, out var id, out body, out _))
        {
            error = "not a PSN packet (root chunk does not fit)";
            return false;
        }
        if (id != expected)
        {
            error = "unexpected PSN root chunk";
            return false;
        }
        error = null;
        return true;
    }

    private static bool TryReadHeader(ReadOnlySpan<byte> data, out PsnPacketHeader header, out string? error)
    {
        header = default;
        if (data.Length < 12)
        {
            error = "PSN packet header too short";
            return false;
        }
        header = new PsnPacketHeader(BinaryPrimitives.ReadUInt64LittleEndian(data), data[8], data[9], data[10], data[11]);
        if (header.VersionHigh != PsnProtocol.VersionHigh)
        {
            error = "unsupported PSN version (2.x expected)";
            return false;
        }
        error = null;
        return true;
    }

    private static bool TryReadTracker(int id, ReadOnlySpan<byte> data, out PsnTracker tracker)
    {
        tracker = new PsnTracker { Id = id };
        var fields = PsnTrackerFields.None;
        Vector3 position = default, speed = default, orientation = default, acceleration = default, target = default;
        float validity = 0;
        ulong timestamp = 0;
        int offset = 0;
        while (offset < data.Length)
        {
            if (!TryReadChunk(data, ref offset, out var fieldId, out var value, out _)) return false;
            switch (fieldId)
            {
                case PsnProtocol.TrackerPosition when value.Length >= 12:
                    position = ReadVector(value);
                    fields |= PsnTrackerFields.Position;
                    break;
                case PsnProtocol.TrackerSpeed when value.Length >= 12:
                    speed = ReadVector(value);
                    fields |= PsnTrackerFields.Speed;
                    break;
                case PsnProtocol.TrackerOrientation when value.Length >= 12:
                    orientation = ReadVector(value);
                    fields |= PsnTrackerFields.Orientation;
                    break;
                case PsnProtocol.TrackerStatus when value.Length >= 4:
                    validity = BinaryPrimitives.ReadSingleLittleEndian(value);
                    fields |= PsnTrackerFields.Status;
                    break;
                case PsnProtocol.TrackerAcceleration when value.Length >= 12:
                    acceleration = ReadVector(value);
                    fields |= PsnTrackerFields.Acceleration;
                    break;
                case PsnProtocol.TrackerTargetPosition when value.Length >= 12:
                    target = ReadVector(value);
                    fields |= PsnTrackerFields.TargetPosition;
                    break;
                case PsnProtocol.TrackerTimestamp when value.Length >= 8:
                    timestamp = BinaryPrimitives.ReadUInt64LittleEndian(value);
                    fields |= PsnTrackerFields.Timestamp;
                    break;
                case <= PsnProtocol.TrackerTimestamp:
                    return false; // a known field with a too-short value
            }
        }
        tracker = new PsnTracker(id, fields, position, speed, orientation, validity, acceleration, target, timestamp);
        return true;
    }

    private static Vector3 ReadVector(ReadOnlySpan<byte> value) => new(
        BinaryPrimitives.ReadSingleLittleEndian(value),
        BinaryPrimitives.ReadSingleLittleEndian(value[4..]),
        BinaryPrimitives.ReadSingleLittleEndian(value[8..]));

    private static bool TryReadChunk(ReadOnlySpan<byte> data, scoped ref int offset, out ushort id, out ReadOnlySpan<byte> body, out bool hasSubchunks)
    {
        id = 0;
        body = default;
        hasSubchunks = false;
        if (data.Length - offset < 4) return false;
        uint header = BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
        int length = (int)((header >> 16) & 0x7FFF);
        if (length > data.Length - offset - 4) return false;
        id = (ushort)header;
        hasSubchunks = (header & 0x80000000u) != 0;
        body = data.Slice(offset + 4, length);
        offset += 4 + length;
        return true;
    }

    private static bool Fail(string message, out string? error, List<PsnTracker> trackers)
    {
        trackers.Clear();
        error = message;
        return false;
    }
}

/// <summary>Builds PSN 2.0 packets (for tests, a future PSN server and tracking simulators).</summary>
public static class PsnPacketWriter
{
    /// <summary>Encodes a data packet with the given trackers (each tracker writes only its present fields).</summary>
    public static byte[] WriteData(PsnPacketHeader header, IEnumerable<PsnTracker> trackers)
    {
        ArgumentNullException.ThrowIfNull(trackers);
        var trackerChunks = new ArrayBufferWriter<byte>();
        foreach (var t in trackers)
        {
            var fields = new ArrayBufferWriter<byte>();
            if ((t.Fields & PsnTrackerFields.Position) != 0) Chunk(fields, PsnProtocol.TrackerPosition, Vector(t.Position), false);
            if ((t.Fields & PsnTrackerFields.Speed) != 0) Chunk(fields, PsnProtocol.TrackerSpeed, Vector(t.Speed), false);
            if ((t.Fields & PsnTrackerFields.Orientation) != 0) Chunk(fields, PsnProtocol.TrackerOrientation, Vector(t.Orientation), false);
            if ((t.Fields & PsnTrackerFields.Status) != 0)
            {
                var v = new byte[4];
                BinaryPrimitives.WriteSingleLittleEndian(v, t.Validity);
                Chunk(fields, PsnProtocol.TrackerStatus, v, false);
            }
            if ((t.Fields & PsnTrackerFields.Acceleration) != 0) Chunk(fields, PsnProtocol.TrackerAcceleration, Vector(t.Acceleration), false);
            if ((t.Fields & PsnTrackerFields.TargetPosition) != 0) Chunk(fields, PsnProtocol.TrackerTargetPosition, Vector(t.TargetPosition), false);
            if ((t.Fields & PsnTrackerFields.Timestamp) != 0)
            {
                var v = new byte[8];
                BinaryPrimitives.WriteUInt64LittleEndian(v, t.TimestampMicroseconds);
                Chunk(fields, PsnProtocol.TrackerTimestamp, v, false);
            }
            Chunk(trackerChunks, (ushort)t.Id, fields.WrittenSpan, true);
        }
        var body = new ArrayBufferWriter<byte>();
        Chunk(body, PsnProtocol.PacketHeader, Header(header), false);
        Chunk(body, PsnProtocol.DataTrackerList, trackerChunks.WrittenSpan, true);
        var packet = new ArrayBufferWriter<byte>();
        Chunk(packet, PsnProtocol.DataPacket, body.WrittenSpan, true);
        return packet.WrittenSpan.ToArray();
    }

    /// <summary>Encodes an info packet with the system name and tracker names.</summary>
    public static byte[] WriteInfo(PsnPacketHeader header, string systemName, IEnumerable<KeyValuePair<int, string>> trackerNames)
    {
        ArgumentNullException.ThrowIfNull(systemName);
        ArgumentNullException.ThrowIfNull(trackerNames);
        var list = new ArrayBufferWriter<byte>();
        foreach (var (id, name) in trackerNames)
        {
            var nameChunk = new ArrayBufferWriter<byte>();
            Chunk(nameChunk, PsnProtocol.InfoTrackerName, Encoding.UTF8.GetBytes(name), false);
            Chunk(list, (ushort)id, nameChunk.WrittenSpan, true);
        }
        var body = new ArrayBufferWriter<byte>();
        Chunk(body, PsnProtocol.PacketHeader, Header(header), false);
        Chunk(body, PsnProtocol.InfoSystemName, Encoding.UTF8.GetBytes(systemName), false);
        Chunk(body, PsnProtocol.InfoTrackerList, list.WrittenSpan, true);
        var packet = new ArrayBufferWriter<byte>();
        Chunk(packet, PsnProtocol.InfoPacket, body.WrittenSpan, true);
        return packet.WrittenSpan.ToArray();
    }

    private static byte[] Header(PsnPacketHeader h)
    {
        var b = new byte[12];
        BinaryPrimitives.WriteUInt64LittleEndian(b, h.TimestampMicroseconds);
        b[8] = h.VersionHigh;
        b[9] = h.VersionLow;
        b[10] = h.FrameId;
        b[11] = h.FramePacketCount;
        return b;
    }

    private static byte[] Vector(Vector3 v)
    {
        var b = new byte[12];
        BinaryPrimitives.WriteSingleLittleEndian(b, v.X);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(4), v.Y);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(8), v.Z);
        return b;
    }

    private static void Chunk(ArrayBufferWriter<byte> writer, ushort id, ReadOnlySpan<byte> data, bool hasSubchunks)
    {
        if (data.Length > 0x7FFF) throw new ArgumentException("A PSN chunk holds at most 32767 bytes.", nameof(data));
        uint header = id | ((uint)data.Length << 16) | (hasSubchunks ? 0x80000000u : 0);
        BinaryPrimitives.WriteUInt32LittleEndian(writer.GetSpan(4), header);
        writer.Advance(4);
        writer.Write(data);
    }
}
