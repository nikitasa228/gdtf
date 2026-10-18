using System.Buffers.Binary;
using System.Net;
using Candela.Dmx.Input;
using Candela.Protocols.ArtNet;
using Candela.Protocols.Enttec;
using Candela.Protocols.Sacn;

namespace Candela.Protocols.Tests;

/// <summary>Regression tests for defects found while reviewing the module against the specifications.</summary>
public class ReviewRegressionTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(7)]
    public void Sacn_discovery_with_a_short_universe_list_layer_is_rejected_not_thrown(int layerLength)
    {
        var buffer = new byte[200];
        int n = SacnPacket.WriteDiscovery(buffer, Guid.NewGuid(), "x", 0, 0, new[] { 1, 2, 3 });
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(112), (ushort)(0x7000 | layerLength));
        var packet = buffer.AsSpan(0, n);
        Assert.False(SacnPacket.TryParseDiscovery(packet, out _, out var error));
        Assert.NotNull(error);

        using var receiver = new SacnReceiver(new DmxInputHub());
        Assert.True(receiver.Start(TestSupport.LoopbackOptions(1) with { SacnJoinMulticast = false }));
        receiver.ProcessPacket(packet, TestSupport.Sender());
        Assert.Equal(1, receiver.Status.BadPackets);
    }

    [Fact]
    public void Unrelated_option_change_does_not_restart_art_net()
    {
        using var manager = new ProtocolManager(new DmxInputHub());
        var options = TestSupport.LoopbackOptions();
        manager.Apply(new[] { ProtocolIds.ArtNet, ProtocolIds.Osc }, options);
        var artNet = manager.Get<ArtNetReceiver>()!;
        var endPoint = artNet.LocalEndPoint;
        using (var sender = new ArtNetSender(IPAddress.Loopback))
            sender.SendDmx(TestSupport.Loopback(endPoint!.Port), 0, new byte[] { 1, 2 });
        Assert.True(TestSupport.WaitUntil(() => artNet.Status.Packets == 1));

        manager.Apply(new[] { ProtocolIds.ArtNet, ProtocolIds.Osc }, options with { OscPatterns = new[] { "/fader/{channel}" }, EnttecSerialPort = "COM9" });
        Assert.Equal(endPoint, artNet.LocalEndPoint);
        Assert.Equal(1, artNet.Status.Packets);
        Assert.Equal("/fader/{channel}", manager.Get<Osc.OscReceiver>()!.Patterns.Single().Text);
    }

    [Fact]
    public void A_throwing_status_subscriber_cannot_kill_the_serial_reader_thread_or_make_start_throw()
    {
        var hub = new DmxInputHub();
        FakeEnttecTransport? transport = null;
        using var widget = new EnttecDmxUsbPro(hub, name => transport = new FakeEnttecTransport(name));
        int calls = 0;
        widget.StatusChanged += (_, _) =>
        {
            Interlocked.Increment(ref calls);
            throw new InvalidOperationException("UI handler bug");
        };
        Assert.True(widget.Start(new ProtocolOptions { EnttecSerialPort = "ttyFAKE" }));

        // The reader thread reports the unplug through StatusChanged; the subscriber throws on that thread.
        transport!.Unplug();
        Assert.True(TestSupport.WaitUntil(() => widget.Status.State == ProtocolState.Error));
        Assert.Contains("disconnected", widget.Status.LastError);
        Assert.True(calls >= 2);
        widget.Stop();
        Assert.Equal(ProtocolState.Stopped, widget.Status.State);
    }

    [Fact]
    public void Own_poll_reply_with_a_long_or_non_ascii_name_is_not_listed_as_another_node()
    {
        var hub = new DmxInputHub();
        using var receiver = new ArtNetReceiver(hub);
        var options = TestSupport.LoopbackOptions() with
        {
            NodeShortName = "Candela FOH visualiser",
            NodeLongName = "Candela – Bühne 1 " + new string('x', 80),
            ArtNetBroadcastPollReply = false,
        };
        Assert.True(receiver.Start(options));
        using var sender = new ArtNetSender(IPAddress.Loopback);
        sender.SendPoll(TestSupport.Loopback(receiver.LocalEndPoint!.Port));
        var reply = Assert.Single(sender.ReceivePollReplies(TimeSpan.FromSeconds(3), stopAfter: 1));
        Assert.Equal("Candela FOH visua", reply.ShortName); // 17 characters fit the field
        Assert.Equal(63, reply.LongName.Length);
        Assert.StartsWith("Candela ? B?hne 1 x", reply.LongName, StringComparison.Ordinal);

        // The same reply looping back (broadcast, or a poll from this computer) is ours, not another node.
        var buffer = new byte[ArtPollReplyPacket.Length];
        reply.Write(buffer);
        receiver.ProcessPacket(buffer, TestSupport.Sender("127.0.0.1"));
        Assert.Empty(receiver.Nodes);
    }

    [Fact]
    public void Poll_reply_ports_report_rdm_and_discovery_off_in_good_output_b()
    {
        using var receiver = new ArtNetReceiver(new DmxInputHub());
        Assert.True(receiver.Start(TestSupport.LoopbackOptions(1, 2)));
        using var sender = new ArtNetSender(IPAddress.Loopback);
        sender.SendPoll(TestSupport.Loopback(receiver.LocalEndPoint!.Port));
        var reply = Assert.Single(sender.ReceivePollReplies(TimeSpan.FromSeconds(3), stopAfter: 1));
        Assert.Equal(2, reply.NumPorts);
        Assert.All(reply.Ports.Take(2), p => Assert.Equal(0xF0, p.GoodOutputB));
        Assert.All(reply.Ports.Skip(2), p => Assert.Equal(0, p.PortType));
    }

    [Fact]
    public void Art_net_stream_table_is_bounded_and_idle_streams_make_room()
    {
        var time = new ManualTime();
        var hub = new DmxInputHub(() => time.UtcDateTime);
        using var receiver = new ArtNetReceiver(hub, time);
        Assert.True(receiver.Start(TestSupport.LoopbackOptions() with { ArtNetSync = false }));
        var buffer = new byte[64];
        var sender = TestSupport.Sender("10.0.0.40");
        for (int pa = 0; pa < ArtNetReceiver.MaxStreams; pa++)
            receiver.ProcessPacket(buffer.AsSpan(0, ArtDmxPacket.Write(buffer, pa, new byte[] { 1, 2 })), sender);
        Assert.Equal(ArtNetReceiver.MaxStreams, receiver.Status.Packets);

        // A further stream while every known one is live is ignored (with a warning) instead of growing the table.
        receiver.ProcessPacket(buffer.AsSpan(0, ArtDmxPacket.Write(buffer, ArtNetReceiver.MaxStreams, new byte[] { 3, 4 })), sender);
        Assert.Equal(1, receiver.Status.IgnoredPackets);
        Assert.Equal(ProtocolState.Warning, receiver.Status.State);

        // Known streams keep working.
        receiver.ProcessPacket(buffer.AsSpan(0, ArtDmxPacket.Write(buffer, 5, new byte[] { 99, 0 })), sender);
        Assert.Equal(99, TestSupport.Read(hub, 6)[0]);

        // Once the old streams have been idle long enough, a new one is accepted again.
        time.Advance(TimeSpan.FromSeconds(11));
        receiver.ProcessPacket(buffer.AsSpan(0, ArtDmxPacket.Write(buffer, ArtNetReceiver.MaxStreams, new byte[] { 3, 4 })), sender);
        Assert.Equal(3, TestSupport.Read(hub, ArtNetReceiver.MaxStreams + 1)[0]);
    }

    [Fact]
    public void Sacn_stream_table_is_bounded_and_idle_streams_make_room()
    {
        var time = new ManualTime();
        var hub = new DmxInputHub(() => time.UtcDateTime);
        using var receiver = new SacnReceiver(hub, time);
        Assert.True(receiver.Start(TestSupport.LoopbackOptions(1) with { SacnJoinMulticast = false }));
        var buffer = new byte[700];
        var name = SacnProtocol.EncodeName("flood");
        var sender = TestSupport.Sender("10.0.0.41");
        for (int i = 0; i < SacnReceiver.MaxStreams; i++)
        {
            var cid = new Guid(i, 0, 0, new byte[8]);
            receiver.ProcessPacket(buffer.AsSpan(0, SacnPacket.WriteData(buffer, cid, name, 100, 0, 1, 0, 1, new byte[] { 1 })), sender);
        }
        var late = Guid.NewGuid();
        receiver.ProcessPacket(buffer.AsSpan(0, SacnPacket.WriteData(buffer, late, name, 200, 0, 1, 0, 1, new byte[] { 77 })), sender);
        Assert.Equal(1, receiver.Status.IgnoredPackets);
        Assert.Equal(1, TestSupport.Read(hub, 1)[0]);

        time.Advance(TimeSpan.FromSeconds(11));
        receiver.ProcessPacket(buffer.AsSpan(0, SacnPacket.WriteData(buffer, late, name, 200, 0, 2, 0, 1, new byte[] { 77 })), sender);
        Assert.Equal(77, TestSupport.Read(hub, 1)[0]);
    }

    [Fact]
    public void Osc_held_universes_are_bounded()
    {
        var hub = new DmxInputHub();
        using var receiver = new Osc.OscReceiver(hub);
        Assert.True(receiver.Start(TestSupport.LoopbackOptions()));
        var sender = TestSupport.Sender("10.0.0.42", 9000);
        for (int u = 1; u <= Osc.OscReceiver.MaxHeldUniverses + 10; u++)
            receiver.ProcessPacket(Osc.OscCodec.Encode(new Osc.OscMessage($"/dmx/{u}/1", Osc.OscArgument.Int(9))), sender);
        Assert.Equal(Osc.OscReceiver.MaxHeldUniverses, hub.ActiveUniverses().Count);
        Assert.Equal(10, receiver.Status.IgnoredPackets);
        Assert.Equal(ProtocolState.Warning, receiver.Status.State);
    }

    [Fact]
    public void Multicast_is_joined_on_every_adapter_when_no_interface_is_selected()
    {
        var eth = Info("eth0", "2.0.0.10", up: true);
        var wifi = Info("wlan0", "192.168.1.5", up: true);
        var down = Info("eth1", "10.0.0.2", up: false);
        var lo = Info("lo", "127.0.0.1", up: true, loopback: true);
        var all = new[] { eth, wifi, down, lo };
        Assert.Equal(new[] { eth.Address, wifi.Address }, Networking.NetworkInterfaceCatalog.MulticastInterfaces(null, all));
        Assert.Equal(new[] { wifi.Address }, Networking.NetworkInterfaceCatalog.MulticastInterfaces(wifi, all));
        Assert.Equal(new[] { IPAddress.Any }, Networking.NetworkInterfaceCatalog.MulticastInterfaces(null, new[] { lo }));
    }

    [Fact]
    public void Multicast_join_after_dispose_fails_softly()
    {
        var channel = Networking.UdpChannel.Open("test", IPAddress.Loopback, 0, enableBroadcast: false);
        channel.Dispose();
        Assert.False(channel.TryJoinMulticast(IPAddress.Parse("239.255.0.1"), new[] { IPAddress.Any }, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Enttec_transport_is_disposed_when_opening_fails()
    {
        FakeEnttecTransport? transport = null;
        using var widget = new EnttecDmxUsbPro(new DmxInputHub(), name => transport = new FakeEnttecTransport(name) { FailOpen = true });
        Assert.False(widget.Start(new ProtocolOptions { EnttecSerialPort = "ttyGONE" }));
        Assert.True(transport!.Disposed);
    }

    [Fact]
    public void Unexpected_serial_read_failure_is_an_error_status_not_a_crash()
    {
        FakeEnttecTransport? transport = null;
        using var widget = new EnttecDmxUsbPro(new DmxInputHub(), name => transport = new FakeEnttecTransport(name));
        Assert.True(widget.Start(new ProtocolOptions { EnttecSerialPort = "ttyODD" }));
        transport!.ReadFailure = new FormatException("driver returned garbage");
        Assert.True(TestSupport.WaitUntil(() => widget.Status.State == ProtocolState.Error));
        Assert.Contains("FormatException", widget.Status.LastError);
    }

    [Fact]
    public void Test_console_survives_a_failing_encoder()
    {
        var pattern = new ArtNetTestPattern(new ITestPatternEncoder[] { new ThrowingEncoder() });
        using var console = new ArtNetTestConsole(pattern, TestSupport.Loopback(9)) { FrameRate = 44 };
        console.Start();
        Assert.True(TestSupport.WaitUntil(() => console.LastError is not null));
        Assert.True(console.IsRunning);
        console.Stop();
        Assert.Contains("encoder bug", console.LastError);
    }

    private static Networking.NetworkInterfaceInfo Info(string name, string ip, bool up, bool loopback = false) =>
        new(name, name, name, IPAddress.Parse(ip), IPAddress.Parse("255.0.0.0"), IPAddress.Parse("255.255.255.255"), up, loopback,
            Array.Empty<byte>());

    private sealed class ThrowingEncoder : ITestPatternEncoder
    {
        public int Universe => 1;

        public void Encode(in TestPatternLook look, Span<byte> universeSlots) => throw new InvalidOperationException("encoder bug");
    }

    [Fact]
    public void Restarting_under_traffic_is_safe_and_stop_withdraws_every_source()
    {
        var hub = new DmxInputHub();
        using var receiver = new ArtNetReceiver(hub);
        using var sacn = new SacnReceiver(hub);
        var options = TestSupport.LoopbackOptions(1, 2) with { SacnJoinMulticast = false };
        Assert.True(receiver.Start(options));
        Assert.True(sacn.Start(options));
        using var stop = new CancellationTokenSource();
        var errors = new List<Exception>();
        var traffic = new Thread(() =>
        {
            using var art = new ArtNetSender(IPAddress.Loopback);
            using var acn = new SacnSender(Guid.NewGuid(), "stress");
            var slots = new byte[512];
            slots[0] = 200;
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    if (receiver.LocalEndPoint is { } a)
                    {
                        art.SendDmx(TestSupport.Loopback(a.Port), 0, slots);
                        art.SendPoll(TestSupport.Loopback(a.Port));
                        art.SendSync(TestSupport.Loopback(a.Port));
                    }
                    if (sacn.LocalEndPoint is { } s) acn.SendData(2, slots, TestSupport.Loopback(s.Port));
                }
                catch (System.Net.Sockets.SocketException)
                {
                }
                catch (Exception e)
                {
                    lock (errors) errors.Add(e);
                }
                Thread.Sleep(1);
            }
        })
        { IsBackground = true };
        traffic.Start();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 8; i++)
        {
            Thread.Sleep(20);
            Assert.True(receiver.Start(options with { ArtNetUniverseOffset = 1 + (i & 1) }));
            Assert.True(sacn.Start(options));
        }
        Thread.Sleep(50);
        receiver.Stop();
        sacn.Stop();
        stop.Cancel();
        traffic.Join();
        Assert.Empty(errors);
        Assert.Empty(hub.Sources());
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), $"restarts took {sw.Elapsed}");
    }
}
