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
}
