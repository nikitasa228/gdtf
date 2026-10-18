using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace Candela.Protocols.Sacn;

/// <summary>Constants of ANSI E1.31-2018 (Streaming ACN).</summary>
public static class SacnProtocol
{
    /// <summary>ACN SDT multicast port used by E1.31.</summary>
    public const int DefaultPort = 5568;

    /// <summary>Lowest DMX universe.</summary>
    public const int MinUniverse = 1;

    /// <summary>Highest DMX universe.</summary>
    public const int MaxUniverse = 63999;

    /// <summary>Universe on which universe-discovery packets are sent.</summary>
    public const int DiscoveryUniverse = 64214;

    /// <summary>Highest legal priority.</summary>
    public const int MaxPriority = 200;

    /// <summary>Default priority.</summary>
    public const int DefaultPriority = 100;

    /// <summary>Root-layer vector of data packets.</summary>
    public const uint VectorRootData = 0x00000004;

    /// <summary>Root-layer vector of synchronisation and discovery packets.</summary>
    public const uint VectorRootExtended = 0x00000008;

    /// <summary>Framing-layer vector of data packets.</summary>
    public const uint VectorFramingData = 0x00000002;

    /// <summary>Framing-layer vector of synchronisation packets.</summary>
    public const uint VectorFramingSync = 0x00000001;

    /// <summary>Framing-layer vector of universe-discovery packets.</summary>
    public const uint VectorFramingDiscovery = 0x00000002;

    /// <summary>DMP-layer vector (set property).</summary>
    public const byte VectorDmpSetProperty = 0x02;

    /// <summary>DMP address and data type.</summary>
    public const byte DmpAddressDataType = 0xA1;

    /// <summary>Universe-discovery-layer vector (universe list).</summary>
    public const uint VectorDiscoveryUniverseList = 0x00000001;

    /// <summary>Options bit: preview data (for visualisers in blind mode; ignored by default).</summary>
    public const byte OptionPreview = 0x80;

    /// <summary>Options bit: the source stops sending this universe.</summary>
    public const byte OptionStreamTerminated = 0x40;

    /// <summary>Options bit: force synchronisation.</summary>
    public const byte OptionForceSynchronization = 0x20;

    /// <summary>Length of a full 512-slot data packet.</summary>
    public const int FullDataPacketLength = 638;

    /// <summary>Length of a synchronisation packet.</summary>
    public const int SyncPacketLength = 49;

    /// <summary>Offset of the DMX start code in a data packet.</summary>
    public const int DataOffset = 125;

    /// <summary>The 12-byte ACN packet identifier "ASC-E1.17\0\0\0".</summary>
    public static ReadOnlySpan<byte> AcnPacketIdentifier => "ASC-E1.17\0\0\0"u8;

    /// <summary>The multicast group of a universe: 239.255.{hi}.{lo}.</summary>
    public static IPAddress MulticastGroup(int universe)
    {
        if (universe is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(universe), universe, "Universe is 1..65535.");
        return new IPAddress(new byte[] { 239, 255, (byte)(universe >> 8), (byte)universe });
    }

    internal static string ReadName(ReadOnlySpan<byte> field)
    {
        int end = field.IndexOf((byte)0);
        if (end < 0) end = field.Length;
        return Encoding.UTF8.GetString(field[..end]);
    }

    /// <summary>Encodes a source name as the 64-byte, zero-terminated UTF-8 field of data and discovery packets
    /// (truncated at 63 bytes without splitting a character).</summary>
    public static byte[] EncodeName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var field = new byte[64];
        var bytes = Encoding.UTF8.GetBytes(name);
        int n = bytes.Length;
        if (n > 63)
        {
            n = 63;
            while (n > 0 && (bytes[n] & 0xC0) == 0x80) n--;
        }
        bytes.AsSpan(0, n).CopyTo(field);
        return field;
    }

    internal static void WriteFlagsLength(Span<byte> destination, int length) =>
        BinaryPrimitives.WriteUInt16BigEndian(destination, (ushort)(0x7000 | (length & 0x0FFF)));
}

/// <summary>Kind of a datagram received on the sACN port.</summary>
public enum SacnPacketKind
{
    /// <summary>Not a valid E1.31 root layer.</summary>
    Invalid,

    /// <summary>DMX data packet (root vector 4).</summary>
    Data,

    /// <summary>Synchronisation packet (root vector 8, framing vector 1).</summary>
    Sync,

    /// <summary>Universe-discovery packet (root vector 8, framing vector 2).</summary>
    Discovery,
}

/// <summary>A parsed E1.31 data packet; a view over the datagram (spans valid while it is).</summary>
public readonly ref struct SacnDataPacket
{
    internal SacnDataPacket(Guid cid, ReadOnlySpan<byte> sourceName, byte priority, int syncAddress, byte sequence,
        byte options, int universe, byte startCode, ReadOnlySpan<byte> slots)
    {
        Cid = cid;
        SourceNameField = sourceName;
        Priority = priority;
        SyncAddress = syncAddress;
        Sequence = sequence;
        Options = options;
        Universe = universe;
        StartCode = startCode;
        Slots = slots;
    }

    /// <summary>Component identifier of the source (stable per console/process).</summary>
    public Guid Cid { get; }

    /// <summary>The raw 64-byte UTF-8 source name field.</summary>
    public ReadOnlySpan<byte> SourceNameField { get; }

    /// <summary>The source name (allocates).</summary>
    public string SourceName => SacnProtocol.ReadName(SourceNameField);

    /// <summary>Priority 0..200 (higher wins).</summary>
    public byte Priority { get; }

    /// <summary>Universe of the synchronisation packets this data waits for; 0 = not synchronised.</summary>
    public int SyncAddress { get; }

    /// <summary>Sequence number (per source and universe).</summary>
    public byte Sequence { get; }

    /// <summary>Options byte (preview, stream terminated, force synchronisation).</summary>
    public byte Options { get; }

    /// <summary>Universe 1..63999.</summary>
    public int Universe { get; }

    /// <summary>DMX start code (0 for dimmer data).</summary>
    public byte StartCode { get; }

    /// <summary>DMX slots 1..n (after the start code).</summary>
    public ReadOnlySpan<byte> Slots { get; }

    /// <summary>The preview-data option bit.</summary>
    public bool IsPreview => (Options & SacnProtocol.OptionPreview) != 0;

    /// <summary>The stream-terminated option bit.</summary>
    public bool IsStreamTerminated => (Options & SacnProtocol.OptionStreamTerminated) != 0;
}

/// <summary>A parsed E1.31 synchronisation packet.</summary>
/// <param name="Cid">Sender CID.</param>
/// <param name="Sequence">Sequence number of the sync stream.</param>
/// <param name="SyncAddress">Synchronisation universe.</param>
public readonly record struct SacnSyncPacket(Guid Cid, byte Sequence, int SyncAddress);

/// <summary>A parsed E1.31 universe-discovery packet (one page of a source's universe list).</summary>
/// <param name="Cid">Sender CID.</param>
/// <param name="SourceName">Sender name.</param>
/// <param name="Page">This page (0-based).</param>
/// <param name="LastPage">Index of the last page.</param>
/// <param name="Universes">Universes on this page, ascending.</param>
public sealed record SacnDiscoveryPacket(Guid Cid, string SourceName, int Page, int LastPage, IReadOnlyList<int> Universes);

/// <summary>Parses and writes E1.31-2018 packets (root, framing, DMP and universe-discovery layers).</summary>
public static class SacnPacket
{
    private const int RootVectorOffset = 18;
    private const int CidOffset = 22;
    private const int FramingOffset = 38;

    /// <summary>
    /// Validates the root layer (preamble 0x0010, postamble 0, ACN identifier, flags/length, vector) and the framing
    /// vector, and says which packet this is.
    /// </summary>
    public static SacnPacketKind Classify(ReadOnlySpan<byte> packet, out string? error)
    {
        if (!TryRoot(packet, out var rootVector, out var end, out error)) return SacnPacketKind.Invalid;
        if (end < FramingOffset + 6)
        {
            error = "framing layer missing";
            return SacnPacketKind.Invalid;
        }
        uint framingVector = BinaryPrimitives.ReadUInt32BigEndian(packet[(FramingOffset + 2)..]);
        if (rootVector == SacnProtocol.VectorRootData)
        {
            if (framingVector == SacnProtocol.VectorFramingData) return SacnPacketKind.Data;
            error = "unknown framing vector in a data packet";
            return SacnPacketKind.Invalid;
        }
        if (framingVector == SacnProtocol.VectorFramingSync) return SacnPacketKind.Sync;
        if (framingVector == SacnProtocol.VectorFramingDiscovery) return SacnPacketKind.Discovery;
        error = "unknown framing vector in an extended packet";
        return SacnPacketKind.Invalid;
    }

    /// <summary>Parses a data packet, validating all three layers (E1.31-2018 §5–7).</summary>
    public static bool TryParseData(ReadOnlySpan<byte> packet, out SacnDataPacket result, out string? error)
    {
        result = default;
        if (!TryRoot(packet, out var rootVector, out var end, out error)) return false;
        if (rootVector != SacnProtocol.VectorRootData)
        {
            error = "not a data packet";
            return false;
        }
        if (end < SacnProtocol.DataOffset + 1)
        {
            error = "data packet too short";
            return false;
        }
        if (!TryLayer(packet, FramingOffset, end, out var framingEnd, out error)) return false;
        if (BinaryPrimitives.ReadUInt32BigEndian(packet[40..]) != SacnProtocol.VectorFramingData)
        {
            error = "bad framing vector";
            return false;
        }
        if (framingEnd < SacnProtocol.DataOffset + 1)
        {
            error = "framing layer too short";
            return false;
        }
        byte priority = packet[108];
        if (priority > SacnProtocol.MaxPriority)
        {
            error = "priority above 200";
            return false;
        }
        int syncAddress = BinaryPrimitives.ReadUInt16BigEndian(packet[109..]);
        byte sequence = packet[111];
        byte options = packet[112];
        int universe = BinaryPrimitives.ReadUInt16BigEndian(packet[113..]);
        if (universe is < SacnProtocol.MinUniverse or > SacnProtocol.MaxUniverse)
        {
            error = "universe outside 1..63999";
            return false;
        }
        if (!TryLayer(packet, 115, framingEnd, out var dmpEnd, out error)) return false;
        if (dmpEnd < SacnProtocol.DataOffset + 1)
        {
            error = "DMP layer too short";
            return false;
        }
        if (packet[117] != SacnProtocol.VectorDmpSetProperty)
        {
            error = "bad DMP vector";
            return false;
        }
        if (packet[118] != SacnProtocol.DmpAddressDataType)
        {
            error = "bad DMP address/data type";
            return false;
        }
        if (BinaryPrimitives.ReadUInt16BigEndian(packet[119..]) != 0)
        {
            error = "DMP first property address is not 0";
            return false;
        }
        if (BinaryPrimitives.ReadUInt16BigEndian(packet[121..]) != 1)
        {
            error = "DMP address increment is not 1";
            return false;
        }
        int count = BinaryPrimitives.ReadUInt16BigEndian(packet[123..]);
        if (count is < 1 or > 513 || SacnProtocol.DataOffset + count > dmpEnd)
        {
            error = "DMP property value count does not fit";
            return false;
        }
        var cid = new Guid(packet.Slice(CidOffset, 16), bigEndian: true);
        result = new SacnDataPacket(cid, packet.Slice(44, 64), priority, syncAddress, sequence, options, universe,
            packet[SacnProtocol.DataOffset], packet.Slice(SacnProtocol.DataOffset + 1, count - 1));
        error = null;
        return true;
    }

    /// <summary>Parses a synchronisation packet.</summary>
    public static bool TryParseSync(ReadOnlySpan<byte> packet, out SacnSyncPacket result, out string? error)
    {
        result = default;
        if (!TryRoot(packet, out var rootVector, out var end, out error)) return false;
        if (rootVector != SacnProtocol.VectorRootExtended || end < SacnProtocol.SyncPacketLength - 2)
        {
            error = "not a synchronisation packet";
            return false;
        }
        if (!TryLayer(packet, FramingOffset, end, out _, out error)) return false;
        if (BinaryPrimitives.ReadUInt32BigEndian(packet[40..]) != SacnProtocol.VectorFramingSync)
        {
            error = "bad synchronisation framing vector";
            return false;
        }
        int sync = BinaryPrimitives.ReadUInt16BigEndian(packet[45..]);
        if (sync is < SacnProtocol.MinUniverse or > SacnProtocol.MaxUniverse)
        {
            error = "synchronisation address outside 1..63999";
            return false;
        }
        result = new SacnSyncPacket(new Guid(packet.Slice(CidOffset, 16), bigEndian: true), packet[44], sync);
        error = null;
        return true;
    }

    /// <summary>Parses a universe-discovery packet.</summary>
    public static bool TryParseDiscovery(ReadOnlySpan<byte> packet, out SacnDiscoveryPacket? result, out string? error)
    {
        result = null;
        if (!TryRoot(packet, out var rootVector, out var end, out error)) return false;
        if (rootVector != SacnProtocol.VectorRootExtended || end < 120)
        {
            error = "not a universe-discovery packet";
            return false;
        }
        if (!TryLayer(packet, FramingOffset, end, out _, out error)) return false;
        if (BinaryPrimitives.ReadUInt32BigEndian(packet[40..]) != SacnProtocol.VectorFramingDiscovery)
        {
            error = "bad discovery framing vector";
            return false;
        }
        if (!TryLayer(packet, 112, end, out var layerEnd, out error)) return false;
        if (BinaryPrimitives.ReadUInt32BigEndian(packet[114..]) != SacnProtocol.VectorDiscoveryUniverseList)
        {
            error = "bad discovery-layer vector";
            return false;
        }
        int page = packet[118], last = packet[119];
        if (page > last)
        {
            error = "discovery page beyond the last page";
            return false;
        }
        int count = Math.Min((layerEnd - 120) / 2, 512);
        var universes = new int[count];
        for (int i = 0; i < count; i++) universes[i] = BinaryPrimitives.ReadUInt16BigEndian(packet[(120 + 2 * i)..]);
        result = new SacnDiscoveryPacket(new Guid(packet.Slice(CidOffset, 16), bigEndian: true),
            SacnProtocol.ReadName(packet.Slice(44, 64)), page, last, universes);
        error = null;
        return true;
    }

    /// <summary>Writes a data packet. <paramref name="slots"/> holds up to 512 DMX slots (start code separate).</summary>
    /// <returns>Bytes written (126 + slots).</returns>
    public static int WriteData(Span<byte> destination, Guid cid, string sourceName, byte priority, int syncAddress, byte sequence,
        byte options, int universe, ReadOnlySpan<byte> slots, byte startCode = 0) =>
        WriteData(destination, cid, SacnProtocol.EncodeName(sourceName), priority, syncAddress, sequence, options, universe, slots, startCode);

    /// <summary>Writes a data packet with a pre-encoded 64-byte source-name field (see
    /// <see cref="SacnProtocol.EncodeName"/>); allocation-free.</summary>
    public static int WriteData(Span<byte> destination, Guid cid, ReadOnlySpan<byte> sourceNameField, byte priority, int syncAddress,
        byte sequence, byte options, int universe, ReadOnlySpan<byte> slots, byte startCode = 0)
    {
        if (sourceNameField.Length != 64) throw new ArgumentException("The source-name field is 64 bytes.", nameof(sourceNameField));
        if (slots.Length > 512) throw new ArgumentOutOfRangeException(nameof(slots), slots.Length, "At most 512 slots.");
        if (universe is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(universe), universe, "Universe is 1..65535.");
        int length = SacnProtocol.DataOffset + 1 + slots.Length;
        if (destination.Length < length) throw new ArgumentException($"The destination needs {length} bytes.", nameof(destination));
        var d = destination[..length];
        WriteRoot(d, SacnProtocol.VectorRootData, cid);
        SacnProtocol.WriteFlagsLength(d[38..], length - 38);
        BinaryPrimitives.WriteUInt32BigEndian(d[40..], SacnProtocol.VectorFramingData);
        sourceNameField.CopyTo(d.Slice(44, 64));
        d[108] = priority;
        BinaryPrimitives.WriteUInt16BigEndian(d[109..], (ushort)syncAddress);
        d[111] = sequence;
        d[112] = options;
        BinaryPrimitives.WriteUInt16BigEndian(d[113..], (ushort)universe);
        SacnProtocol.WriteFlagsLength(d[115..], length - 115);
        d[117] = SacnProtocol.VectorDmpSetProperty;
        d[118] = SacnProtocol.DmpAddressDataType;
        BinaryPrimitives.WriteUInt16BigEndian(d[119..], 0);
        BinaryPrimitives.WriteUInt16BigEndian(d[121..], 1);
        BinaryPrimitives.WriteUInt16BigEndian(d[123..], (ushort)(slots.Length + 1));
        d[SacnProtocol.DataOffset] = startCode;
        slots.CopyTo(d[(SacnProtocol.DataOffset + 1)..]);
        return length;
    }

    /// <summary>Writes a synchronisation packet (49 bytes).</summary>
    public static int WriteSync(Span<byte> destination, Guid cid, byte sequence, int syncAddress)
    {
        if (destination.Length < SacnProtocol.SyncPacketLength) throw new ArgumentException("The destination needs 49 bytes.", nameof(destination));
        var d = destination[..SacnProtocol.SyncPacketLength];
        WriteRoot(d, SacnProtocol.VectorRootExtended, cid);
        SacnProtocol.WriteFlagsLength(d[38..], d.Length - 38);
        BinaryPrimitives.WriteUInt32BigEndian(d[40..], SacnProtocol.VectorFramingSync);
        d[44] = sequence;
        BinaryPrimitives.WriteUInt16BigEndian(d[45..], (ushort)syncAddress);
        d[47] = 0;
        d[48] = 0;
        return d.Length;
    }

    /// <summary>Writes one universe-discovery page (up to 512 universes, ascending).</summary>
    public static int WriteDiscovery(Span<byte> destination, Guid cid, string sourceName, int page, int lastPage, ReadOnlySpan<int> universes)
    {
        ArgumentNullException.ThrowIfNull(sourceName);
        if (universes.Length > 512) throw new ArgumentOutOfRangeException(nameof(universes), universes.Length, "At most 512 per page.");
        int length = 120 + 2 * universes.Length;
        if (destination.Length < length) throw new ArgumentException($"The destination needs {length} bytes.", nameof(destination));
        var d = destination[..length];
        WriteRoot(d, SacnProtocol.VectorRootExtended, cid);
        SacnProtocol.WriteFlagsLength(d[38..], length - 38);
        BinaryPrimitives.WriteUInt32BigEndian(d[40..], SacnProtocol.VectorFramingDiscovery);
        SacnProtocol.EncodeName(sourceName).CopyTo(d.Slice(44, 64));
        d.Slice(108, 4).Clear();
        SacnProtocol.WriteFlagsLength(d[112..], length - 112);
        BinaryPrimitives.WriteUInt32BigEndian(d[114..], SacnProtocol.VectorDiscoveryUniverseList);
        d[118] = (byte)page;
        d[119] = (byte)lastPage;
        for (int i = 0; i < universes.Length; i++) BinaryPrimitives.WriteUInt16BigEndian(d[(120 + 2 * i)..], (ushort)universes[i]);
        return length;
    }

    private static void WriteRoot(Span<byte> d, uint vector, Guid cid)
    {
        BinaryPrimitives.WriteUInt16BigEndian(d, 0x0010);
        BinaryPrimitives.WriteUInt16BigEndian(d[2..], 0x0000);
        SacnProtocol.AcnPacketIdentifier.CopyTo(d[4..]);
        SacnProtocol.WriteFlagsLength(d[16..], d.Length - 16);
        BinaryPrimitives.WriteUInt32BigEndian(d[18..], vector);
        cid.TryWriteBytes(d.Slice(CidOffset, 16), bigEndian: true, out _);
    }

    /// <summary>Root layer: returns its vector and the end of the PDU (packet bytes after it are ignored).</summary>
    private static bool TryRoot(ReadOnlySpan<byte> packet, out uint vector, out int end, out string? error)
    {
        vector = 0;
        end = 0;
        if (packet.Length < FramingOffset)
        {
            error = "shorter than the E1.31 root layer";
            return false;
        }
        if (BinaryPrimitives.ReadUInt16BigEndian(packet) != 0x0010)
        {
            error = "bad preamble size";
            return false;
        }
        if (BinaryPrimitives.ReadUInt16BigEndian(packet[2..]) != 0)
        {
            error = "bad postamble size";
            return false;
        }
        if (!packet.Slice(4, 12).SequenceEqual(SacnProtocol.AcnPacketIdentifier))
        {
            error = "bad ACN packet identifier";
            return false;
        }
        if (!TryLayer(packet, 16, packet.Length, out end, out error)) return false;
        vector = BinaryPrimitives.ReadUInt32BigEndian(packet[RootVectorOffset..]);
        if (vector != SacnProtocol.VectorRootData && vector != SacnProtocol.VectorRootExtended)
        {
            error = "unknown root vector";
            return false;
        }
        return true;
    }

    /// <summary>Checks a PDU's flags (0x7) and that its length fits inside its parent.</summary>
    private static bool TryLayer(ReadOnlySpan<byte> packet, int offset, int parentEnd, out int end, out string? error)
    {
        end = 0;
        if (offset + 2 > parentEnd)
        {
            error = "layer header missing";
            return false;
        }
        int flagsLength = BinaryPrimitives.ReadUInt16BigEndian(packet[offset..]);
        if ((flagsLength & 0xF000) != 0x7000)
        {
            error = "bad PDU flags";
            return false;
        }
        int length = flagsLength & 0x0FFF;
        end = offset + length;
        if (length < 2 || end > parentEnd)
        {
            error = "PDU length does not fit the packet";
            return false;
        }
        error = null;
        return true;
    }
}
