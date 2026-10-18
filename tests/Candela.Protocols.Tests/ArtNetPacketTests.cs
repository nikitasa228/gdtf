using System.Net;
using Candela.Dmx.Input;
using Candela.Protocols.ArtNet;

namespace Candela.Protocols.Tests;

public class ArtNetPacketTests
{
    [Fact]
    public void ArtDmx_round_trips_and_pads_odd_lengths()
    {
        var buffer = new byte[600];
        var data = new byte[] { 1, 2, 3 };
        int n = ArtDmxPacket.Write(buffer, ArtNetProtocol.ToPortAddress(1, 2, 3), data, sequence: 7, physical: 2);
        Assert.Equal(18 + 4, n);
        Assert.Equal("Art-Net\0"u8.ToArray(), buffer[..8]);
        Assert.Equal(0x00, buffer[8]);
        Assert.Equal(0x50, buffer[9]);
        Assert.Equal(14, buffer[11]);
        Assert.True(ArtDmxPacket.TryParse(buffer.AsSpan(0, n), out var p));
        Assert.Equal(7, p.Sequence);
        Assert.Equal(2, p.Physical);
        Assert.Equal(0x123, p.PortAddress);
        Assert.Equal(new byte[] { 1, 2, 3, 0 }, p.Data.ToArray());
        Assert.Equal("1:2:3", ArtNetProtocol.FormatPortAddress(p.PortAddress));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(513)]
    public void ArtDmx_rejects_impossible_lengths(int length)
    {
        var buffer = new byte[18 + 520];
        ArtDmxPacket.Write(buffer, 0, new byte[2]);
        buffer[16] = (byte)(length >> 8);
        buffer[17] = (byte)length;
        Assert.False(ArtDmxPacket.TryParse(buffer, out _, out var error));
        Assert.Contains("length", error);
    }

    [Fact]
    public void ArtDmx_rejects_length_beyond_datagram_and_old_versions()
    {
        var buffer = new byte[18 + 512];
        int n = ArtDmxPacket.Write(buffer, 0, new byte[512]);
        Assert.False(ArtDmxPacket.TryParse(buffer.AsSpan(0, n - 1), out _));
        buffer[11] = 13;
        Assert.False(ArtDmxPacket.TryParse(buffer.AsSpan(0, n), out _, out var error));
        Assert.Contains("version", error);
    }

    [Fact]
    public void Port_address_net_bit_15_is_ignored()
    {
        var buffer = new byte[30];
        int n = ArtDmxPacket.Write(buffer, 0x7FFF, new byte[2]);
        buffer[15] |= 0x80;
        Assert.True(ArtDmxPacket.TryParse(buffer.AsSpan(0, n), out var p));
        Assert.Equal(0x7FFF, p.PortAddress);
    }

    [Fact]
    public void ArtPoll_round_trips_with_targeted_mode()
    {
        var buffer = new byte[64];
        var poll = new ArtPollPacket(ArtPollPacket.FlagReplyOnChange | ArtPollPacket.FlagTargetedMode, 0x10, 20, 10);
        int n = poll.Write(buffer);
        Assert.Equal(ArtPollPacket.Length, n);
        Assert.True(ArtPollPacket.TryParse(buffer.AsSpan(0, n), out var parsed, out _));
        Assert.Equal(poll, parsed);
        Assert.True(parsed.TargetedMode);
        Assert.True(parsed.Targets(15));
        Assert.False(parsed.Targets(21));
        // Art-Net 3 polls are 14 bytes: without range fields every port-address is targeted.
        Assert.True(ArtPollPacket.TryParse(buffer.AsSpan(0, 14), out var shortPoll, out _));
        Assert.True(shortPoll.Targets(15) && shortPoll.Targets(ArtNetProtocol.MaxPortAddress));
    }

    [Fact]
    public void ArtTimeCode_round_trips_and_validates_ranges()
    {
        var buffer = new byte[32];
        var tc = new ArtTimeCodePacket(1, 2, 3, 24, TimecodeType.Smpte30, StreamId: 3);
        Assert.Equal(19, tc.Write(buffer));
        Assert.True(ArtTimeCodePacket.TryParse(buffer.AsSpan(0, 19), out var parsed, out _));
        Assert.Equal(tc, parsed);
        Assert.Equal(new Timecode(1, 2, 3, 24, TimecodeType.Smpte30), parsed.ToTimecode());

        new ArtTimeCodePacket(0, 0, 0, 24, TimecodeType.Film24).Write(buffer);
        Assert.False(ArtTimeCodePacket.TryParse(buffer.AsSpan(0, 19), out _, out _));
        new ArtTimeCodePacket(24, 0, 0, 0, TimecodeType.Ebu25).Write(buffer);
        Assert.False(ArtTimeCodePacket.TryParse(buffer.AsSpan(0, 19), out _, out _));
        new ArtTimeCodePacket(0, 0, 0, 0, TimecodeType.Ebu25).Write(buffer);
        buffer[18] = 4;
        Assert.False(ArtTimeCodePacket.TryParse(buffer.AsSpan(0, 19), out _, out _));
    }

    [Fact]
    public void ArtSync_round_trips()
    {
        var buffer = new byte[14];
        Assert.Equal(14, ArtSyncPacket.Write(buffer));
        Assert.True(ArtSyncPacket.TryParse(buffer, out _));
        Assert.True(ArtNetProtocol.TryReadOpCode(buffer, out var op));
        Assert.Equal(ArtNetOpCode.Sync, op);
    }

    [Fact]
    public void ArtPollReply_has_the_art_net_4_layout()
    {
        var reply = new ArtPollReplyPacket
        {
            IpAddress = IPAddress.Parse("192.168.1.20"),
            FirmwareVersion = 0x0102,
            NetSwitch = 0x12,
            SubSwitch = 0x3,
            Oem = 0xFFFF,
            Status1 = 0xD0,
            EstaManufacturer = 0x7FF0,
            ShortName = "Candela",
            LongName = "Candela stage-lighting visualiser",
            NodeReport = "#0001 [0001] OK",
            NumPorts = 2,
            Ports = new[]
            {
                new ArtPollReplyPort(0x80, 0, 0x80, 0xC0, 0, 4),
                new ArtPollReplyPort(0x80, 0, 0x00, 0xC0, 0, 5),
            },
            Style = ArtPollReplyPacket.StyleNode,
            MacAddress = new byte[] { 1, 2, 3, 4, 5, 6 },
            BindIp = IPAddress.Parse("192.168.1.20"),
            BindIndex = 2,
            Status2 = 0x08,
            RefreshRate = 44,
        };
        var buffer = new byte[ArtPollReplyPacket.Length];
        Assert.Equal(239, reply.Write(buffer));
        // Fixed offsets from the Art-Net 4 specification.
        Assert.Equal(new byte[] { 0x00, 0x21 }, buffer[8..10]);
        Assert.Equal(new byte[] { 192, 168, 1, 20 }, buffer[10..14]);
        Assert.Equal(new byte[] { 0x36, 0x19 }, buffer[14..16]);
        Assert.Equal(0x12, buffer[18]);
        Assert.Equal(0x03, buffer[19]);
        Assert.Equal((byte)'C', buffer[26]);
        Assert.Equal(0, buffer[26 + 7]);
        Assert.Equal(2, buffer[173]);
        Assert.Equal(0x80, buffer[174]);
        Assert.Equal(4, buffer[190]);
        Assert.Equal(5, buffer[191]);
        Assert.Equal(0x00, buffer[200]);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, buffer[201..207]);
        Assert.Equal(2, buffer[211]);
        Assert.Equal(0x08, buffer[212]);

        Assert.True(ArtPollReplyPacket.TryParse(buffer, out var parsed, out _));
        Assert.Equal("Candela", parsed!.ShortName);
        Assert.Equal(reply.LongName, parsed.LongName);
        Assert.Equal(reply.IpAddress, parsed.IpAddress);
        Assert.Equal(ArtNetProtocol.DefaultPort, parsed.Port);
        Assert.Equal(2, parsed.NumPorts);
        Assert.Equal(0x1234, parsed.OutputPortAddress(0));
        Assert.Equal(0x1235, parsed.OutputPortAddress(1));
        Assert.Equal(2, parsed.BindIndex);
        Assert.Equal(44, parsed.RefreshRate);
        Assert.Equal(0x7FF0, parsed.EstaManufacturer);
    }

    [Fact]
    public void Binds_group_four_ports_sharing_net_and_subnet()
    {
        var binds = ArtNetReceiver.BuildBinds(new[] { 1, 2, 3, 4, 5, 6, 17, 18 }, 1, out var warning);
        Assert.Null(warning);
        Assert.Equal(new[] { new[] { 0, 1, 2, 3 }, new[] { 4, 5 }, new[] { 16, 17 } }, binds.Select(b => b.PortAddresses));

        var offsetZero = ArtNetReceiver.BuildBinds(new[] { 1 }, 0, out _);
        Assert.Equal(new[] { 1 }, offsetZero[0].PortAddresses);

        var unreachable = ArtNetReceiver.BuildBinds(new[] { 1, 40000 }, 1, out warning);
        Assert.NotNull(warning);
        Assert.Single(unreachable);
    }
}
