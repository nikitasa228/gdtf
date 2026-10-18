using System.Buffers.Binary;
using System.Net;
using Candela.Dmx.Input;
using Candela.Protocols.Sacn;

namespace Candela.Protocols.Tests;

public class SacnTests
{
    private static readonly Guid CidA = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid CidB = Guid.Parse("66666666-7777-8888-9999-aaaaaaaaaaaa");

    [Fact]
    public void Data_packet_round_trips_with_e131_layout()
    {
        var buffer = new byte[700];
        var slots = Enumerable.Range(0, 512).Select(i => (byte)i).ToArray();
        int n = SacnPacket.WriteData(buffer, CidA, "Eos Ti", 150, 0, 42, 0, 7, slots);
        Assert.Equal(SacnProtocol.FullDataPacketLength, n);
        Assert.Equal(0x0010, BinaryPrimitives.ReadUInt16BigEndian(buffer));
        Assert.Equal("ASC-E1.17\0\0\0"u8.ToArray(), buffer[4..16]);
        Assert.Equal(0x7000 | (638 - 16), BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(16)));
        Assert.Equal(0x7000 | (638 - 38), BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(38)));
        Assert.Equal(0x7000 | (638 - 115), BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(115)));
        Assert.Equal(513, BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(123)));
        Assert.Equal(SacnPacketKind.Data, SacnPacket.Classify(buffer.AsSpan(0, n), out _));

        Assert.True(SacnPacket.TryParseData(buffer.AsSpan(0, n), out var p, out var error), error);
        Assert.Equal(CidA, p.Cid);
        Assert.Equal("Eos Ti", p.SourceName);
        Assert.Equal(150, p.Priority);
        Assert.Equal(42, p.Sequence);
        Assert.Equal(7, p.Universe);
        Assert.Equal(0, p.StartCode);
        Assert.Equal(slots, p.Slots.ToArray());
        Assert.Equal(IPAddress.Parse("239.255.0.7"), SacnProtocol.MulticastGroup(7));
        Assert.Equal(IPAddress.Parse("239.255.1.2"), SacnProtocol.MulticastGroup(258));
    }

    [Theory]
    [InlineData(1, 0x11, "preamble")]
    [InlineData(3, 0x01, "postamble")]
    [InlineData(6, (byte)'X', "identifier")]
    [InlineData(21, 0x05, "root vector")]
    [InlineData(43, 0x07, "framing vector")]
    [InlineData(108, 201, "priority")]
    [InlineData(113, 0xFA, "universe")]
    [InlineData(117, 0x03, "DMP vector")]
    [InlineData(118, 0xA2, "address/data type")]
    [InlineData(120, 0x01, "first property address")]
    [InlineData(122, 0x02, "increment")]
    [InlineData(123, 0x09, "count")]
    [InlineData(16, 0x60, "flags")]
    public void Every_layer_is_validated(int offset, byte value, string expected)
    {
        var buffer = new byte[700];
        int n = SacnPacket.WriteData(buffer, CidA, "x", 100, 0, 1, 0, 1, new byte[512]);
        buffer[offset] = value;
        Assert.False(SacnPacket.TryParseData(buffer.AsSpan(0, n), out _, out var error));
        Assert.Contains(expected, error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Truncated_packets_are_rejected()
    {
        var buffer = new byte[700];
        int n = SacnPacket.WriteData(buffer, CidA, "x", 100, 0, 1, 0, 1, new byte[512]);
        for (int len = 0; len < n; len += 37)
            Assert.False(SacnPacket.TryParseData(buffer.AsSpan(0, len), out _, out _));
    }

    [Fact]
    public void Sync_and_discovery_packets_round_trip()
    {
        var buffer = new byte[1200];
        int n = SacnPacket.WriteSync(buffer, CidB, 9, 500);
        Assert.Equal(49, n);
        Assert.Equal(SacnPacketKind.Sync, SacnPacket.Classify(buffer.AsSpan(0, n), out _));
        Assert.True(SacnPacket.TryParseSync(buffer.AsSpan(0, n), out var sync, out _));
        Assert.Equal(new SacnSyncPacket(CidB, 9, 500), sync);

        n = SacnPacket.WriteDiscovery(buffer, CidB, "grandMA3", 0, 1, new[] { 1, 2, 300 });
        Assert.Equal(SacnPacketKind.Discovery, SacnPacket.Classify(buffer.AsSpan(0, n), out _));
        Assert.True(SacnPacket.TryParseDiscovery(buffer.AsSpan(0, n), out var discovery, out var error), error);
        Assert.Equal("grandMA3", discovery!.SourceName);
        Assert.Equal(1, discovery.LastPage);
        Assert.Equal(new[] { 1, 2, 300 }, discovery.Universes);
    }

    [Fact]
    public void Loopback_unicast_data_reaches_the_hub_with_priority_and_name()
    {
        var hub = new DmxInputHub();
        using var receiver = new SacnReceiver(hub);
        Assert.True(receiver.Start(TestSupport.LoopbackOptions(1, 2)), receiver.Status.Message);
        Assert.NotEqual(ProtocolState.Error, receiver.Status.State);
        using var sender = new SacnSender(CidA, "Test console", destinationPort: receiver.LocalEndPoint!.Port);
        var target = TestSupport.Loopback(receiver.LocalEndPoint!.Port);
        sender.SendData(5, new byte[] { 10, 20, 30 }, target, priority: 120);

        Assert.True(TestSupport.WaitUntil(() => TestSupport.HasUniverse(hub, 5)));
        Assert.Equal(new byte[] { 10, 20, 30, 0 }, TestSupport.Read(hub, 5)[..4]);
        var source = Assert.Single(hub.Sources());
        Assert.Equal(120, source.Priority);
        Assert.Equal("Test console", source.Name);
        Assert.Equal(CidA, source.Source.Cid);
        Assert.Equal("sacn", source.Source.Protocol);
        Assert.Equal("127.0.0.1", source.Source.Endpoint);
    }

    [Fact]
    public void Higher_priority_source_wins_in_the_hub()
    {
        var hub = new DmxInputHub();
        using var receiver = StartedReceiver(hub);
        var buffer = new byte[700];
        receiver.ProcessPacket(Data(buffer, CidA, 1, 1, new byte[] { 200 }, priority: 100), TestSupport.Sender("10.0.0.1"));
        receiver.ProcessPacket(Data(buffer, CidB, 1, 1, new byte[] { 50 }, priority: 150), TestSupport.Sender("10.0.0.2"));
        Assert.Equal(50, TestSupport.Read(hub, 1)[0]);
        Assert.Equal(new[] { 100, 150 }, hub.Sources().Select(s => s.Priority).Order());
    }

    [Fact]
    public void Sequence_rule_rejects_late_packets_and_accepts_restarts()
    {
        var hub = new DmxInputHub();
        using var receiver = StartedReceiver(hub);
        var s = TestSupport.Sender();
        var buffer = new byte[700];
        receiver.ProcessPacket(Data(buffer, CidA, 1, 10, new byte[] { 1 }), s);
        receiver.ProcessPacket(Data(buffer, CidA, 1, 10, new byte[] { 2 }), s); // duplicate: diff 0 → discard
        receiver.ProcessPacket(Data(buffer, CidA, 1, 5, new byte[] { 3 }), s); // diff -5 → discard
        Assert.Equal(1, TestSupport.Read(hub, 1)[0]);
        receiver.ProcessPacket(Data(buffer, CidA, 1, 11, new byte[] { 4 }), s);
        Assert.Equal(4, TestSupport.Read(hub, 1)[0]);
        receiver.ProcessPacket(Data(buffer, CidA, 1, 200, new byte[] { 5 }), s); // diff -67 → accepted (restart)
        Assert.Equal(5, TestSupport.Read(hub, 1)[0]);
        receiver.ProcessPacket(Data(buffer, CidA, 1, 180, new byte[] { 6 }), s); // diff -20 → accepted
        Assert.Equal(6, TestSupport.Read(hub, 1)[0]);
        receiver.ProcessPacket(Data(buffer, CidA, 1, 179, new byte[] { 7 }), s); // diff -1 → discard
        Assert.Equal(6, TestSupport.Read(hub, 1)[0]);
        receiver.ProcessPacket(Data(buffer, CidA, 1, 0, new byte[] { 8 }), s); // 180 → 0 wraps: diff +76
        Assert.Equal(8, TestSupport.Read(hub, 1)[0]);
        Assert.Equal(3, receiver.Status.Universes.Single().SequenceErrors);
    }

    [Fact]
    public void Stream_termination_removes_the_source_immediately()
    {
        var hub = new DmxInputHub();
        using var receiver = new SacnReceiver(hub);
        Assert.True(receiver.Start(TestSupport.LoopbackOptions(1)));
        using var a = new SacnSender(CidA, "A");
        using var b = new SacnSender(CidB, "B");
        var target = TestSupport.Loopback(receiver.LocalEndPoint!.Port);
        a.SendData(1, new byte[] { 100 }, target);
        b.SendData(1, new byte[] { 30 }, target);
        Assert.True(TestSupport.WaitUntil(() => hub.Sources().Count == 2));
        Assert.Equal(100, TestSupport.Read(hub, 1)[0]);
        a.SendTermination(1, target);
        Assert.True(TestSupport.WaitUntil(() => hub.Sources().Count == 1));
        Assert.Equal(30, TestSupport.Read(hub, 1)[0]);
        Assert.Equal(CidB, hub.Sources().Single().Source.Cid);
    }

    [Fact]
    public void Preview_data_is_ignored_unless_enabled()
    {
        var hub = new DmxInputHub();
        using var receiver = StartedReceiver(hub);
        var buffer = new byte[700];
        receiver.ProcessPacket(Data(buffer, CidA, 1, 1, new byte[] { 99 }, options: SacnProtocol.OptionPreview), TestSupport.Sender());
        Assert.False(TestSupport.HasUniverse(hub, 1));
        Assert.Equal(1, receiver.Status.IgnoredPackets);

        receiver.Start(TestSupport.LoopbackOptions(1) with { SacnAcceptPreviewData = true });
        receiver.ProcessPacket(Data(buffer, CidA, 1, 2, new byte[] { 99 }, options: SacnProtocol.OptionPreview), TestSupport.Sender());
        Assert.Equal(99, TestSupport.Read(hub, 1)[0]);
    }

    [Fact]
    public void Non_zero_start_codes_are_not_dimmer_data()
    {
        var hub = new DmxInputHub();
        using var receiver = StartedReceiver(hub);
        var buffer = new byte[700];
        int n = SacnPacket.WriteData(buffer, CidA, "x", 100, 0, 1, 0, 1, new byte[] { 255 }, startCode: 0xDD);
        receiver.ProcessPacket(buffer.AsSpan(0, n), TestSupport.Sender());
        Assert.False(TestSupport.HasUniverse(hub, 1));
    }

    [Fact]
    public void Synchronised_data_waits_for_its_sync_packet()
    {
        var time = new ManualTime();
        var hub = new DmxInputHub(() => time.UtcDateTime);
        using var receiver = new SacnReceiver(hub, time);
        Assert.True(receiver.Start(TestSupport.LoopbackOptions(1) with { SacnJoinMulticast = false }));
        var s = TestSupport.Sender();
        var buffer = new byte[700];

        // No sync packet seen yet for address 7000: output immediately.
        receiver.ProcessPacket(Data(buffer, CidA, 1, 1, new byte[] { 10 }, syncAddress: 7000), s);
        Assert.Equal(10, TestSupport.Read(hub, 1)[0]);
        receiver.ProcessPacket(Sync(buffer, 7000), s);
        receiver.ProcessPacket(Data(buffer, CidA, 1, 2, new byte[] { 20 }, syncAddress: 7000), s);
        receiver.ProcessPacket(Data(buffer, CidA, 2, 1, new byte[] { 21 }, syncAddress: 7000), s);
        Assert.Equal(10, TestSupport.Read(hub, 1)[0]);
        Assert.False(TestSupport.HasUniverse(hub, 2));
        receiver.ProcessPacket(Sync(buffer, 7000), s);
        Assert.Equal(20, TestSupport.Read(hub, 1)[0]);
        Assert.Equal(21, TestSupport.Read(hub, 2)[0]);

        time.Advance(TimeSpan.FromSeconds(3)); // sync stopped: data flows unsynchronised again
        receiver.ProcessPacket(Data(buffer, CidA, 1, 3, new byte[] { 30 }, syncAddress: 7000), s);
        Assert.Equal(30, TestSupport.Read(hub, 1)[0]);
    }

    [Fact]
    public void Universe_discovery_is_collected_across_pages()
    {
        using var receiver = StartedReceiver(new DmxInputHub());
        var buffer = new byte[1200];
        int n = SacnPacket.WriteDiscovery(buffer, CidA, "Onyx", 0, 1, new[] { 1, 2 });
        receiver.ProcessPacket(buffer.AsSpan(0, n), TestSupport.Sender("10.0.0.3"));
        n = SacnPacket.WriteDiscovery(buffer, CidA, "Onyx", 1, 1, new[] { 600 });
        receiver.ProcessPacket(buffer.AsSpan(0, n), TestSupport.Sender("10.0.0.3"));
        var d = Assert.Single(receiver.DiscoveredSources);
        Assert.Equal("Onyx", d.Name);
        Assert.Equal("10.0.0.3", d.Address);
        Assert.Equal(new[] { 1, 2, 600 }, d.Universes);
    }

    [Fact]
    public void Multicast_join_failures_are_warnings_not_errors()
    {
        using var receiver = new SacnReceiver(new DmxInputHub());
        Assert.True(receiver.Start(TestSupport.LoopbackOptions(1, 2, 3)));
        Assert.Contains(receiver.Status.State, new[] { ProtocolState.Running, ProtocolState.Warning });
        if (receiver.Status.State == ProtocolState.Warning) Assert.Contains("multicast", receiver.Status.Warnings[0]);
        else Assert.Equal(new[] { 1, 2, 3, SacnProtocol.DiscoveryUniverse }, receiver.JoinedUniverses);
    }

    private static SacnReceiver StartedReceiver(DmxInputHub hub)
    {
        var receiver = new SacnReceiver(hub);
        Assert.True(receiver.Start(TestSupport.LoopbackOptions(1) with { SacnJoinMulticast = false }));
        return receiver;
    }

    private static byte[] Data(byte[] buffer, Guid cid, int universe, byte sequence, byte[] slots, byte priority = 100, byte options = 0, int syncAddress = 0)
    {
        int n = SacnPacket.WriteData(buffer, cid, cid == CidA ? "A" : "B", priority, syncAddress, sequence, options, universe, slots);
        return buffer[..n];
    }

    private static byte[] Sync(byte[] buffer, int address) => buffer[..SacnPacket.WriteSync(buffer, CidB, 1, address)];
}
