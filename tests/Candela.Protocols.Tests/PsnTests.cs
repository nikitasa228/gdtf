using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Text;
using Candela.Protocols.Psn;

namespace Candela.Protocols.Tests;

public class PsnTests
{
    [Fact]
    public void Crafted_data_packet_parses_every_tracker_field()
    {
        var packet = CraftedDataPacket();
        Assert.Equal(PsnPacketKind.Data, PsnPacketReader.GetKind(packet));
        var trackers = new List<PsnTracker>();
        Assert.True(PsnPacketReader.TryReadData(packet, out var header, trackers, out var error), error);
        Assert.Equal(987654321UL, header.TimestampMicroseconds);
        Assert.Equal(2, header.VersionHigh);
        Assert.Equal(3, header.FrameId);
        Assert.Equal(1, header.FramePacketCount);

        Assert.Equal(2, trackers.Count);
        var t = trackers[0];
        Assert.Equal(7, t.Id);
        Assert.Equal(new Vector3(1.5f, 2f, -0.5f), t.Position);
        Assert.Equal(new Vector3(0.1f, 0, 0), t.Speed);
        Assert.Equal(new Vector3(0, 0, 1.57f), t.Orientation);
        Assert.Equal(1f, t.Validity);
        Assert.Equal(new Vector3(0, 0, -9.8f), t.Acceleration);
        Assert.Equal(new Vector3(3, 3, 0), t.TargetPosition);
        Assert.Equal(123456UL, t.TimestampMicroseconds);
        Assert.Equal((PsnTrackerFields)127, t.Fields);

        var partial = trackers[1];
        Assert.Equal(300, partial.Id);
        Assert.Equal(PsnTrackerFields.Position, partial.Fields);
        Assert.Equal(new Vector3(-1, 0, 4), partial.Position);
    }

    [Fact]
    public void Crafted_info_packet_parses_names()
    {
        var trackerName = Chunk(0x0000, Encoding.UTF8.GetBytes("Lead singer"), false);
        var trackerList = Chunk(0x0002, Chunk(7, trackerName, true), true);
        var body = Concat(Chunk(0x0000, Header(), false), Chunk(0x0001, Encoding.UTF8.GetBytes("BlackTrax server"), false), trackerList);
        var packet = Chunk(0x6756, body, true);

        var names = new List<KeyValuePair<int, string>>();
        Assert.Equal(PsnPacketKind.Info, PsnPacketReader.GetKind(packet));
        Assert.True(PsnPacketReader.TryReadInfo(packet, out _, out var system, names, out var error), error);
        Assert.Equal("BlackTrax server", system);
        Assert.Equal(new KeyValuePair<int, string>(7, "Lead singer"), Assert.Single(names));
    }

    [Fact]
    public void Writer_and_reader_agree()
    {
        var header = new PsnPacketHeader(42, PsnProtocol.VersionHigh, PsnProtocol.VersionLow, 9, 1);
        var tracker = new PsnTracker(65535, PsnTrackerFields.Position | PsnTrackerFields.Status | PsnTrackerFields.Timestamp,
            new Vector3(1, 2, 3), default, default, 0.5f, default, default, 99);
        var bytes = PsnPacketWriter.WriteData(header, new[] { tracker });
        var trackers = new List<PsnTracker>();
        Assert.True(PsnPacketReader.TryReadData(bytes, out var parsedHeader, trackers, out _));
        Assert.Equal(header, parsedHeader);
        Assert.Equal(tracker, trackers.Single());

        var info = PsnPacketWriter.WriteInfo(header, "System", new[] { new KeyValuePair<int, string>(65535, "Truss A") });
        var names = new List<KeyValuePair<int, string>>();
        Assert.True(PsnPacketReader.TryReadInfo(info, out _, out var system, names, out _));
        Assert.Equal("System", system);
        Assert.Equal("Truss A", names.Single().Value);
    }

    [Fact]
    public void Wrong_version_and_truncation_are_rejected()
    {
        var packet = CraftedDataPacket();
        var trackers = new List<PsnTracker>();
        var wrongVersion = (byte[])packet.Clone();
        wrongVersion[8 + 8] = 1; // header version_high inside the header chunk
        Assert.False(PsnPacketReader.TryReadData(wrongVersion, out _, trackers, out var error));
        Assert.Contains("version", error);
        for (int len = 0; len < packet.Length; len += 3) Assert.False(PsnPacketReader.TryReadData(packet.AsSpan(0, len), out _, trackers, out _));
        Assert.Empty(trackers);
    }

    [Fact]
    public void Receiver_builds_a_tracker_table_and_merges_partial_updates()
    {
        var time = new ManualTime();
        using var receiver = new PsnReceiver(time);
        Assert.True(receiver.Start(TestSupport.LoopbackOptions()));
        Assert.Contains(receiver.Status.State, new[] { ProtocolState.Running, ProtocolState.Warning });
        int updates = 0;
        receiver.TrackersUpdated += (_, _) => updates++;
        var server = TestSupport.Sender("10.0.0.40", 56565);

        var header = new PsnPacketHeader(1, 2, 0, 0, 1);
        receiver.ProcessPacket(PsnPacketWriter.WriteInfo(header, "Stage tracking", new[] { new KeyValuePair<int, string>(1, "Actor 1") }), server);
        receiver.ProcessPacket(PsnPacketWriter.WriteData(header, new[]
        {
            new PsnTracker(1, PsnTrackerFields.Position | PsnTrackerFields.Speed, new Vector3(1, 2, 0), new Vector3(0.5f, 0, 0), default, 0, default, default, 0),
            new PsnTracker(2, PsnTrackerFields.Position, new Vector3(5, 5, 0), default, default, 0, default, default, 0),
        }), server);
        receiver.ProcessPacket(PsnPacketWriter.WriteData(header, new[]
        {
            new PsnTracker(1, PsnTrackerFields.Orientation, default, default, new Vector3(0, 0, 3.14f), 0, default, default, 0),
        }), server);

        var table = receiver.Trackers;
        Assert.Equal(2, table.Count);
        var actor = table[0];
        Assert.Equal("Actor 1", actor.Name);
        Assert.Equal("Stage tracking", actor.SystemName);
        Assert.Equal("10.0.0.40", actor.Source);
        Assert.Equal(new Vector3(1, 2, 0), actor.Data.Position);
        Assert.Equal(new Vector3(0, 0, 3.14f), actor.Data.Orientation);
        Assert.Equal(PsnTrackerFields.Position | PsnTrackerFields.Speed | PsnTrackerFields.Orientation, actor.Data.Fields);
        Assert.Equal(2, updates);
        Assert.True(receiver.TryGetTracker(2, out var second));
        Assert.Equal(new Vector3(5, 5, 0), second!.Data.Position);
        Assert.Equal(3, receiver.Status.Packets);
        Assert.Contains("2 tracker(s)", receiver.Status.Message);

        time.Advance(TimeSpan.FromSeconds(11));
        Assert.Empty(receiver.Trackers);
    }

    [Fact]
    public void Receiver_accepts_unicast_over_loopback()
    {
        using var receiver = new PsnReceiver();
        Assert.True(receiver.Start(TestSupport.LoopbackOptions()));
        using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        client.Send(CraftedDataPacket(), TestSupport.Loopback(receiver.LocalEndPoint!.Port));
        Assert.True(TestSupport.WaitUntil(() => receiver.Trackers.Count == 2));
        Assert.Equal("127.0.0.1", receiver.Trackers[0].Source);
    }

    [Fact]
    public void Bad_group_is_an_error_status()
    {
        using var receiver = new PsnReceiver();
        Assert.False(receiver.Start(TestSupport.LoopbackOptions() with { PsnMulticastGroup = "10.0.0.1" }));
        Assert.Equal(ProtocolState.Error, receiver.Status.State);
        Assert.Contains("multicast", receiver.Status.LastError);
    }

    /// <summary>A data packet assembled by hand from the PSN 2.0 specification (independent of PsnPacketWriter).</summary>
    internal static byte[] CraftedDataPacket()
    {
        var fields = Concat(
            Chunk(0x0000, Floats(1.5f, 2f, -0.5f), false),
            Chunk(0x0001, Floats(0.1f, 0, 0), false),
            Chunk(0x0002, Floats(0, 0, 1.57f), false),
            Chunk(0x0003, Floats(1f), false),
            Chunk(0x0004, Floats(0, 0, -9.8f), false),
            Chunk(0x0005, Floats(3, 3, 0), false),
            Chunk(0x0006, BitConverter.GetBytes(123456UL), false),
            Chunk(0x0042, new byte[] { 1, 2, 3 }, false)); // unknown field: skipped
        var tracker7 = Chunk(7, fields, true);
        var tracker300 = Chunk(300, Chunk(0x0000, Floats(-1, 0, 4), false), true);
        var body = Concat(Chunk(0x0000, Header(), false), Chunk(0x0001, Concat(tracker7, tracker300), true));
        return Chunk(0x6755, body, true);
    }

    private static byte[] Header()
    {
        var h = new byte[12];
        BinaryPrimitives.WriteUInt64LittleEndian(h, 987654321UL);
        h[8] = 2;
        h[9] = 0;
        h[10] = 3;
        h[11] = 1;
        return h;
    }

    private static byte[] Floats(params float[] values)
    {
        var b = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(i * 4), values[i]);
        return b;
    }

    private static byte[] Chunk(int id, byte[] data, bool hasSubchunks)
    {
        var b = new byte[4 + data.Length];
        uint header = (uint)id | ((uint)data.Length << 16) | (hasSubchunks ? 1u << 31 : 0);
        BinaryPrimitives.WriteUInt32LittleEndian(b, header);
        data.CopyTo(b, 4);
        return b;
    }

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
}
