using System.Net;
using System.Net.Sockets;
using Candela.Dmx.Input;
using Candela.Protocols.ArtNet;

namespace Candela.Protocols.Tests;

public class ArtNetReceiverTests
{
    [Fact]
    public void Loopback_ArtDmx_reaches_the_hub_with_the_universe_offset()
    {
        var hub = new DmxInputHub();
        using var receiver = new ArtNetReceiver(hub);
        Assert.True(receiver.Start(TestSupport.LoopbackOptions()), receiver.Status.Message);
        Assert.True(receiver.IsRunning);
        using var sender = new ArtNetSender(IPAddress.Loopback);
        var target = TestSupport.Loopback(receiver.LocalEndPoint!.Port);

        var slots = new byte[512];
        slots[0] = 255;
        slots[99] = 128;
        slots[511] = 7;
        sender.SendDmx(target, ArtNetProtocol.ToPortAddress(0, 0, 2), slots, physical: 1);

        Assert.True(TestSupport.WaitUntil(() => TestSupport.HasUniverse(hub, 3)), "port-address 0:0:2 must arrive as universe 3");
        var merged = TestSupport.Read(hub, 3);
        Assert.Equal(255, merged[0]);
        Assert.Equal(128, merged[99]);
        Assert.Equal(7, merged[511]);
        var source = Assert.Single(hub.Sources());
        Assert.Equal("artnet", source.Source.Protocol);
        Assert.Equal("127.0.0.1/1", source.Source.Endpoint);
        Assert.Equal(DmxInputHub.DefaultPriority, source.Priority);

        var status = receiver.Status;
        Assert.Equal(ProtocolState.Running, status.State is ProtocolState.Warning ? ProtocolState.Running : status.State);
        Assert.Equal(1, status.Packets);
        Assert.NotNull(status.LastPacketUtc);
        var u = Assert.Single(status.Universes);
        Assert.Equal(3, u.Universe);
        Assert.Equal(1, u.Packets);

        receiver.Stop();
        Assert.Empty(hub.ActiveUniverses());
        Assert.Equal(ProtocolState.Stopped, receiver.Status.State);
    }

    [Fact]
    public void Offset_zero_maps_port_address_one_to_universe_one()
    {
        var hub = new DmxInputHub();
        using var receiver = new ArtNetReceiver(hub);
        Assert.True(receiver.Start(TestSupport.LoopbackOptions() with { ArtNetUniverseOffset = 0 }));
        var dmx = new byte[30];
        int n = ArtDmxPacket.Write(dmx, 0, new byte[] { 9, 9 });
        receiver.ProcessPacket(dmx.AsSpan(0, n), TestSupport.Sender());
        n = ArtDmxPacket.Write(dmx, 1, new byte[] { 42, 0 });
        receiver.ProcessPacket(dmx.AsSpan(0, n), TestSupport.Sender());
        Assert.Equal(42, TestSupport.Read(hub, 1)[0]);
        Assert.Equal(1, receiver.Status.IgnoredPackets); // port-address 0 → internal 0 does not exist
    }

    [Fact]
    public void ArtPoll_is_answered_with_a_reply_consoles_understand()
    {
        var hub = new DmxInputHub();
        using var receiver = new ArtNetReceiver(hub);
        var options = TestSupport.LoopbackOptions(1, 2, 3, 4, 5, 6) with { NodeShortName = "Candela", NodeLongName = "Candela test node" };
        Assert.True(receiver.Start(options), receiver.Status.Message);
        using var sender = new ArtNetSender(IPAddress.Loopback);
        var target = TestSupport.Loopback(receiver.LocalEndPoint!.Port);

        // Data on universe 2 so its port reports "data being output".
        sender.SendDmx(target, 1, new byte[] { 1, 2 });
        Assert.True(TestSupport.WaitUntil(() => TestSupport.HasUniverse(hub, 2)));

        sender.SendPoll(target);
        var replies = sender.ReceivePollReplies(TimeSpan.FromSeconds(3), stopAfter: 2).OrderBy(r => r.BindIndex).ToArray();
        Assert.Equal(2, replies.Length);

        var root = replies[0];
        Assert.Equal(IPAddress.Loopback, root.IpAddress);
        Assert.Equal(0x1936, root.Port);
        Assert.Equal("Candela", root.ShortName);
        Assert.Equal("Candela test node", root.LongName);
        Assert.StartsWith("#0001 [", root.NodeReport);
        Assert.Equal(4, root.NumPorts);
        Assert.Equal(1, root.BindIndex);
        Assert.Equal(ArtPollReplyPacket.StyleNode, root.Style);
        Assert.Equal(0x08, root.Status2 & 0x08);
        Assert.Equal(0xFFFF, root.Oem);
        Assert.All(root.Ports.Take(4), p => Assert.Equal(ArtPollReplyPort.DmxOutputFromArtNet, p.PortType));
        Assert.Equal(new[] { 0, 1, 2, 3 }, Enumerable.Range(0, 4).Select(root.OutputPortAddress));
        Assert.Equal(0x80, root.Ports[1].GoodOutputA & 0x80);
        Assert.Equal(0x00, root.Ports[0].GoodOutputA & 0x80);
        Assert.Equal(6, root.MacAddress.Count);

        var second = replies[1];
        Assert.Equal(2, second.BindIndex);
        Assert.Equal(2, second.NumPorts);
        Assert.Equal(new[] { 4, 5 }, Enumerable.Range(0, 2).Select(second.OutputPortAddress));
        Assert.Equal(root.IpAddress, second.BindIp);
    }

    [Fact]
    public void Targeted_ArtPoll_outside_our_universes_gets_no_reply()
    {
        using var receiver = new ArtNetReceiver(new DmxInputHub());
        Assert.True(receiver.Start(TestSupport.LoopbackOptions(1)));
        using var sender = new ArtNetSender(IPAddress.Loopback);
        sender.SendPoll(TestSupport.Loopback(receiver.LocalEndPoint!.Port),
            new ArtPollPacket(ArtPollPacket.FlagTargetedMode, 0, TargetPortAddressTop: 300, TargetPortAddressBottom: 200));
        Assert.Empty(sender.ReceivePollReplies(TimeSpan.FromMilliseconds(400)));
    }

    [Fact]
    public async Task ArtTimeCode_is_forwarded_to_the_hub()
    {
        var hub = new DmxInputHub();
        var received = new TaskCompletionSource<Timecode>(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.TimecodeReceived += tc => received.TrySetResult(tc);
        using var receiver = new ArtNetReceiver(hub);
        ArtTimeCodePacket? raw = null;
        receiver.TimeCodeReceived += p => raw = p;
        Assert.True(receiver.Start(TestSupport.LoopbackOptions()));
        using var sender = new ArtNetSender(IPAddress.Loopback);
        sender.SendTimeCode(TestSupport.Loopback(receiver.LocalEndPoint!.Port), new Timecode(1, 2, 3, 4, TimecodeType.Ebu25));
        var timecode = await received.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(new Timecode(1, 2, 3, 4, TimecodeType.Ebu25), timecode);
        Assert.Equal(new Timecode(1, 2, 3, 4, TimecodeType.Ebu25), hub.LastTimecode);
        Assert.Equal(TimecodeType.Ebu25, raw!.Value.Type);
    }

    [Fact]
    public void ArtSync_holds_frames_until_the_next_sync_and_times_out_after_4_s()
    {
        var time = new ManualTime();
        var hub = new DmxInputHub(() => time.UtcDateTime);
        using var receiver = new ArtNetReceiver(hub, time);
        Assert.True(receiver.Start(TestSupport.LoopbackOptions()));
        var console = TestSupport.Sender("10.0.0.7");
        var other = TestSupport.Sender("10.0.0.8");
        var buffer = new byte[600];

        receiver.ProcessPacket(Dmx(buffer, 0, 10), console);
        Assert.Equal(10, TestSupport.Read(hub, 1)[0]); // no sync seen yet: immediate

        receiver.ProcessPacket(Sync(buffer), console);
        receiver.ProcessPacket(Dmx(buffer, 0, 20), console);
        receiver.ProcessPacket(Dmx(buffer, 1, 21), console);
        Assert.Equal(10, TestSupport.Read(hub, 1)[0]);
        Assert.False(TestSupport.HasUniverse(hub, 2));

        receiver.ProcessPacket(Sync(buffer), other); // another controller's sync releases nothing of ours
        Assert.Equal(10, TestSupport.Read(hub, 1)[0]);

        time.Advance(TimeSpan.FromMilliseconds(25));
        receiver.ProcessPacket(Sync(buffer), console);
        Assert.Equal(20, TestSupport.Read(hub, 1)[0]);
        Assert.Equal(21, TestSupport.Read(hub, 2)[0]);

        time.Advance(TimeSpan.FromSeconds(4.5));
        receiver.ProcessPacket(Dmx(buffer, 0, 30), console); // sync stopped for > 4 s: back to immediate output
        Assert.Equal(30, TestSupport.Read(hub, 1)[0]);
    }

    [Fact]
    public void Out_of_order_sequence_is_dropped_but_restarts_are_accepted()
    {
        var hub = new DmxInputHub();
        using var receiver = new ArtNetReceiver(hub);
        Assert.True(receiver.Start(TestSupport.LoopbackOptions()));
        var s = TestSupport.Sender();
        var buffer = new byte[600];
        receiver.ProcessPacket(Dmx(buffer, 0, 50, sequence: 100), s);
        receiver.ProcessPacket(Dmx(buffer, 0, 40, sequence: 95), s);
        Assert.Equal(50, TestSupport.Read(hub, 1)[0]);
        receiver.ProcessPacket(Dmx(buffer, 0, 60, sequence: 101), s);
        Assert.Equal(60, TestSupport.Read(hub, 1)[0]);
        receiver.ProcessPacket(Dmx(buffer, 0, 70, sequence: 30), s); // far behind = sender restarted
        Assert.Equal(70, TestSupport.Read(hub, 1)[0]);
        receiver.ProcessPacket(Dmx(buffer, 0, 80, sequence: 0), s); // 0 = sequencing disabled
        Assert.Equal(80, TestSupport.Read(hub, 1)[0]);
        Assert.Equal(1, receiver.Status.Universes.Single().SequenceErrors);
    }

    [Fact]
    public void Unsubscribed_universes_can_be_filtered()
    {
        var hub = new DmxInputHub();
        using var receiver = new ArtNetReceiver(hub);
        Assert.True(receiver.Start(TestSupport.LoopbackOptions(1) with { AcceptUnsubscribedUniverses = false }));
        var buffer = new byte[600];
        receiver.ProcessPacket(Dmx(buffer, 0, 1), TestSupport.Sender());
        receiver.ProcessPacket(Dmx(buffer, 5, 1), TestSupport.Sender());
        Assert.Equal(new[] { 1 }, hub.ActiveUniverses());
        Assert.Equal(1, receiver.Status.IgnoredPackets);
    }

    [Fact]
    public void Other_nodes_poll_replies_are_listed()
    {
        using var receiver = new ArtNetReceiver(new DmxInputHub());
        Assert.True(receiver.Start(TestSupport.LoopbackOptions()));
        var reply = new ArtPollReplyPacket { IpAddress = IPAddress.Parse("10.0.0.9"), ShortName = "grandMA3", Style = ArtPollReplyPacket.StyleController };
        var buffer = new byte[ArtPollReplyPacket.Length];
        reply.Write(buffer);
        receiver.ProcessPacket(buffer, TestSupport.Sender("10.0.0.9"));
        var node = Assert.Single(receiver.Nodes);
        Assert.Equal("grandMA3", node.Reply.ShortName);
        Assert.Equal("10.0.0.9", node.Sender);

        // Our own reply looping back (broadcast replies, local polls) is not another node.
        var options = TestSupport.LoopbackOptions();
        var own = new ArtPollReplyPacket { IpAddress = IPAddress.Loopback, ShortName = options.NodeShortName, LongName = options.NodeLongName };
        own.Write(buffer);
        receiver.ProcessPacket(buffer, TestSupport.Sender("127.0.0.1"));
        Assert.Single(receiver.Nodes);
    }

    [Fact]
    public void Port_in_use_is_an_error_status_not_an_exception()
    {
        using var blocker = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        if (OperatingSystem.IsWindows()) blocker.ExclusiveAddressUse = true;
        blocker.Bind(new IPEndPoint(IPAddress.Any, 0));
        int port = ((IPEndPoint)blocker.LocalEndPoint!).Port;

        using var receiver = new ArtNetReceiver(new DmxInputHub());
        var changes = new List<ProtocolState>();
        receiver.StatusChanged += (_, e) => changes.Add(e.Status.State);
        bool started = receiver.Start(TestSupport.LoopbackOptions() with { ArtNetPort = port });
        Assert.False(started);
        Assert.False(receiver.IsRunning);
        Assert.Equal(ProtocolState.Error, receiver.Status.State);
        Assert.Contains("already in use", receiver.Status.LastError);
        Assert.Contains(ProtocolState.Error, changes);
    }

    [Fact]
    public void Missing_interface_is_an_error_status()
    {
        using var receiver = new ArtNetReceiver(new DmxInputHub());
        Assert.False(receiver.Start(TestSupport.LoopbackOptions() with { NetworkInterface = "no-such-adapter-42" }));
        Assert.Equal(ProtocolState.Error, receiver.Status.State);
        Assert.Contains("no-such-adapter-42", receiver.Status.LastError);
        Assert.False(receiver.Start(TestSupport.LoopbackOptions() with { NetworkInterface = "203.0.113.77" }));
        Assert.Contains("was not found", receiver.Status.Message);
    }

    [Fact]
    public void Restart_with_new_options_works_and_stop_is_idempotent()
    {
        using var receiver = new ArtNetReceiver(new DmxInputHub());
        Assert.True(receiver.Start(TestSupport.LoopbackOptions()));
        Assert.True(receiver.Start(TestSupport.LoopbackOptions(7, 8)));
        Assert.Equal(new[] { 6, 7 }, receiver.AnnouncedPortAddresses.Single());
        receiver.Stop();
        receiver.Stop();
        Assert.Null(receiver.LocalEndPoint);
    }

    [Fact]
    public void Test_console_drives_the_receiver_with_a_moving_show()
    {
        var hub = new DmxInputHub();
        using var receiver = new ArtNetReceiver(hub);
        Assert.True(receiver.Start(TestSupport.LoopbackOptions()));
        var pattern = ArtNetTestPattern.DemoRig(movingHeads: 50, pars: 10);
        Assert.Equal(new[] { 1, 2 }, pattern.Universes);
        using var console = new ArtNetTestConsole(pattern, TestSupport.Loopback(receiver.LocalEndPoint!.Port)) { TimecodeType = TimecodeType.Ebu25 };

        console.SendFrame(1.0);
        Assert.True(TestSupport.WaitUntil(() => TestSupport.HasUniverse(hub, 2) && hub.LastTimecode is not null));
        pattern.Render(1.0);
        Assert.Equal(pattern.GetUniverse(1).ToArray(), TestSupport.Read(hub, 1));
        Assert.Equal(pattern.GetUniverse(2).ToArray(), TestSupport.Read(hub, 2));
        Assert.Equal(new Timecode(0, 0, 1, 0, TimecodeType.Ebu25), hub.LastTimecode);

        console.SendFrame(2.5);
        pattern.Render(2.5);
        var expected = pattern.GetUniverse(1).ToArray();
        Assert.True(TestSupport.WaitUntil(() => TestSupport.Read(hub, 1).AsSpan().SequenceEqual(expected)), "the second frame must replace the first");
        Assert.Equal(2, console.FramesSent);

        console.Start();
        Assert.True(TestSupport.WaitUntil(() => console.FramesSent > 6));
        console.Stop();
        Assert.True(receiver.Status.Packets > 10);
    }

    private static byte[] Dmx(byte[] buffer, int portAddress, byte firstSlot, byte sequence = 0)
    {
        int n = ArtDmxPacket.Write(buffer, portAddress, new byte[] { firstSlot, 0 }, sequence);
        return buffer[..n];
    }

    private static byte[] Sync(byte[] buffer) => buffer[..ArtSyncPacket.Write(buffer)];
}
