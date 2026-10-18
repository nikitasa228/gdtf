using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace Candela.Protocols.ArtNet;

/// <summary>One port of an ArtPollReply (up to four per packet).</summary>
/// <param name="PortType">Bit 7 = outputs data from Art-Net, bit 6 = inputs onto Art-Net, bits 5–0 protocol (0 = DMX512).</param>
/// <param name="GoodInput">Input status bits.</param>
/// <param name="GoodOutputA">Output status: bit 7 data being output, bit 3 merging, bit 1 LTP merge, bit 0 sACN.</param>
/// <param name="GoodOutputB">Output status B: bit 7 RDM disabled, bit 6 continuous output style.</param>
/// <param name="SwIn">Low nibble of the input port-address.</param>
/// <param name="SwOut">Low nibble of the output port-address.</param>
public readonly record struct ArtPollReplyPort(byte PortType, byte GoodInput, byte GoodOutputA, byte GoodOutputB, byte SwIn, byte SwOut)
{
    /// <summary>Port type of a DMX512 output fed from Art-Net.</summary>
    public const byte DmxOutputFromArtNet = 0x80;
}

/// <summary>
/// ArtPollReply (OpCode 0x2100): a node describes itself, its ports and their port-addresses. Candela sends one per
/// group of up to four subscribed universes (BindIndex 1, 2, …) and parses replies of other nodes for the UI.
/// </summary>
public sealed record ArtPollReplyPacket
{
    /// <summary>Length of an Art-Net 4 ArtPollReply.</summary>
    public const int Length = 239;

    /// <summary>Shortest reply Candela accepts (Art-Net 3 replies end after the MAC address).</summary>
    public const int MinimumLength = 207;

    /// <summary>Style code of a DMX node (StNode).</summary>
    public const byte StyleNode = 0x00;

    /// <summary>Style code of a lighting console (StController).</summary>
    public const byte StyleController = 0x01;

    /// <summary>Style code of a visualiser (StVisual).</summary>
    public const byte StyleVisual = 0x04;

    /// <summary>Node IP address.</summary>
    public IPAddress IpAddress { get; init; } = IPAddress.Any;

    /// <summary>UDP port, always 0x1936.</summary>
    public int Port { get; init; } = ArtNetProtocol.DefaultPort;

    /// <summary>Firmware revision.</summary>
    public ushort FirmwareVersion { get; init; }

    /// <summary>Bits 14–8 of the port-addresses of this bind.</summary>
    public byte NetSwitch { get; init; }

    /// <summary>Bits 7–4 of the port-addresses of this bind.</summary>
    public byte SubSwitch { get; init; }

    /// <summary>Art-Net OEM code.</summary>
    public ushort Oem { get; init; }

    /// <summary>UBEA version (0 = none).</summary>
    public byte UbeaVersion { get; init; }

    /// <summary>Status1: indicators (bits 7–6), port-address authority (5–4), boot (2), RDM (1), UBEA (0).</summary>
    public byte Status1 { get; init; }

    /// <summary>ESTA manufacturer code.</summary>
    public ushort EstaManufacturer { get; init; }

    /// <summary>Short name (max 17 characters).</summary>
    public string ShortName { get; init; } = "";

    /// <summary>Long name (max 63 characters).</summary>
    public string LongName { get; init; } = "";

    /// <summary>Node report text, e.g. "#0001 [0042] Power On Tests successful".</summary>
    public string NodeReport { get; init; } = "";

    /// <summary>Number of ports as declared by the node (0–4 per bind).</summary>
    public int NumPorts { get; init; }

    /// <summary>The four port slots (index 0–3; unused ones are default).</summary>
    public IReadOnlyList<ArtPollReplyPort> Ports { get; init; } = Array.Empty<ArtPollReplyPort>();

    /// <summary>sACN priority the node uses (Art-Net 4).</summary>
    public byte AcnPriority { get; init; }

    /// <summary>Macro key inputs.</summary>
    public byte SwMacro { get; init; }

    /// <summary>Remote trigger inputs.</summary>
    public byte SwRemote { get; init; }

    /// <summary>Equipment style (StNode = 0, StController = 1, StVisual = 4, …).</summary>
    public byte Style { get; init; }

    /// <summary>MAC address (6 bytes; zeros when unknown).</summary>
    public IReadOnlyList<byte> MacAddress { get; init; } = new byte[6];

    /// <summary>IP of the root device of a multi-bind node.</summary>
    public IPAddress BindIp { get; init; } = IPAddress.Any;

    /// <summary>1-based index of this bind (1 = root device).</summary>
    public byte BindIndex { get; init; } = 1;

    /// <summary>Status2: bit 3 = supports 15-bit port-addresses, bit 2 DHCP capable, bit 1 DHCP used, bit 0 web config.</summary>
    public byte Status2 { get; init; }

    /// <summary>Status3 (failsafe and LLRP capabilities).</summary>
    public byte Status3 { get; init; }

    /// <summary>User-defined field.</summary>
    public ushort User { get; init; }

    /// <summary>Maximum DMX refresh rate in Hz the node can output (0 = not specified, 44 = DMX512 maximum).</summary>
    public ushort RefreshRate { get; init; }

    /// <summary>The 15-bit output port-address of port <paramref name="index"/> (0–3).</summary>
    public int OutputPortAddress(int index) =>
        ((NetSwitch & 0x7F) << 8) | ((SubSwitch & 0x0F) << 4) | (index < Ports.Count ? Ports[index].SwOut & 0x0F : 0);

    /// <summary>Writes the reply (239 bytes).</summary>
    public int Write(Span<byte> destination)
    {
        ArtNetProtocol.EnsureLength(destination, Length);
        var d = destination[..Length];
        d.Clear();
        ArtNetProtocol.WriteHeader(d, ArtNetOpCode.PollReply, withVersion: false);
        IpAddress.MapToIPv4().TryWriteBytes(d[10..14], out _);
        BinaryPrimitives.WriteUInt16LittleEndian(d[14..], (ushort)Port);
        BinaryPrimitives.WriteUInt16BigEndian(d[16..], FirmwareVersion);
        d[18] = (byte)(NetSwitch & 0x7F);
        d[19] = (byte)(SubSwitch & 0x0F);
        BinaryPrimitives.WriteUInt16BigEndian(d[20..], Oem);
        d[22] = UbeaVersion;
        d[23] = Status1;
        BinaryPrimitives.WriteUInt16LittleEndian(d[24..], EstaManufacturer);
        WriteText(d.Slice(26, ShortNameField), ShortName);
        WriteText(d.Slice(44, LongNameField), LongName);
        WriteText(d.Slice(108, 64), NodeReport);
        BinaryPrimitives.WriteUInt16BigEndian(d[172..], (ushort)Math.Clamp(NumPorts, 0, 4));
        for (int i = 0; i < 4 && i < Ports.Count; i++)
        {
            var p = Ports[i];
            d[174 + i] = p.PortType;
            d[178 + i] = p.GoodInput;
            d[182 + i] = p.GoodOutputA;
            d[186 + i] = p.SwIn;
            d[190 + i] = p.SwOut;
            d[213 + i] = p.GoodOutputB;
        }
        d[194] = AcnPriority;
        d[195] = SwMacro;
        d[196] = SwRemote;
        d[200] = Style;
        for (int i = 0; i < 6 && i < MacAddress.Count; i++) d[201 + i] = MacAddress[i];
        BindIp.MapToIPv4().TryWriteBytes(d[207..211], out _);
        d[211] = BindIndex;
        d[212] = Status2;
        d[217] = Status3;
        BinaryPrimitives.WriteUInt16BigEndian(d[224..], User);
        BinaryPrimitives.WriteUInt16BigEndian(d[226..], RefreshRate);
        return Length;
    }

    /// <summary>Parses a reply of any Art-Net node (at least <see cref="MinimumLength"/> bytes; fields after the MAC
    /// address are read when present).</summary>
    public static bool TryParse(ReadOnlySpan<byte> packet, out ArtPollReplyPacket? result, out string? error)
    {
        result = null;
        if (!ArtNetProtocol.TryReadOpCode(packet, out var op) || op != ArtNetOpCode.PollReply)
        {
            error = "not an ArtPollReply packet";
            return false;
        }
        if (packet.Length < MinimumLength)
        {
            error = "ArtPollReply too short";
            return false;
        }
        var ports = new ArtPollReplyPort[4];
        bool hasB = packet.Length >= 217;
        for (int i = 0; i < 4; i++)
            ports[i] = new ArtPollReplyPort(packet[174 + i], packet[178 + i], packet[182 + i], hasB ? packet[213 + i] : (byte)0,
                packet[186 + i], packet[190 + i]);
        result = new ArtPollReplyPacket
        {
            IpAddress = new IPAddress(packet.Slice(10, 4)),
            Port = BinaryPrimitives.ReadUInt16LittleEndian(packet[14..]),
            FirmwareVersion = BinaryPrimitives.ReadUInt16BigEndian(packet[16..]),
            NetSwitch = (byte)(packet[18] & 0x7F),
            SubSwitch = (byte)(packet[19] & 0x0F),
            Oem = BinaryPrimitives.ReadUInt16BigEndian(packet[20..]),
            UbeaVersion = packet[22],
            Status1 = packet[23],
            EstaManufacturer = BinaryPrimitives.ReadUInt16LittleEndian(packet[24..]),
            ShortName = ReadText(packet.Slice(26, 18)),
            LongName = ReadText(packet.Slice(44, 64)),
            NodeReport = ReadText(packet.Slice(108, 64)),
            NumPorts = Math.Min(4, (int)BinaryPrimitives.ReadUInt16BigEndian(packet[172..])),
            Ports = ports,
            AcnPriority = packet[194],
            SwMacro = packet[195],
            SwRemote = packet[196],
            Style = packet[200],
            MacAddress = packet.Slice(201, 6).ToArray(),
            BindIp = packet.Length >= 211 ? new IPAddress(packet.Slice(207, 4)) : IPAddress.Any,
            BindIndex = packet.Length >= 212 ? packet[211] : (byte)1,
            Status2 = packet.Length >= 213 ? packet[212] : (byte)0,
            Status3 = packet.Length >= 218 ? packet[217] : (byte)0,
            User = packet.Length >= 226 ? BinaryPrimitives.ReadUInt16BigEndian(packet[224..]) : (ushort)0,
            RefreshRate = packet.Length >= 228 ? BinaryPrimitives.ReadUInt16BigEndian(packet[226..]) : (ushort)0,
        };
        error = null;
        return true;
    }

    /// <summary>Short-name field size (17 characters and the terminating zero).</summary>
    internal const int ShortNameField = 18;

    /// <summary>Long-name field size (63 characters and the terminating zero).</summary>
    internal const int LongNameField = 64;

    /// <summary>The text a receiver reads back from a name field written with <paramref name="text"/> (truncated,
    /// non-ASCII characters replaced by '?').</summary>
    internal static string AsWritten(string text, int fieldLength)
    {
        Span<byte> field = stackalloc byte[fieldLength];
        WriteText(field, text);
        return ReadText(field);
    }

    private static void WriteText(Span<byte> field, string text)
    {
        // Null-terminated ASCII; non-ASCII characters become '?'.
        int n = Math.Min(text.Length, field.Length - 1);
        for (int i = 0; i < n; i++)
        {
            char c = text[i];
            field[i] = c is >= ' ' and < (char)127 ? (byte)c : (byte)'?';
        }
        field[n] = 0;
    }

    private static string ReadText(ReadOnlySpan<byte> field)
    {
        int end = field.IndexOf((byte)0);
        if (end < 0) end = field.Length;
        return Encoding.Latin1.GetString(field[..end]);
    }
}
