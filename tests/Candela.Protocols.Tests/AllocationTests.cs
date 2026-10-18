using Candela.Dmx.Input;
using Candela.Protocols.ArtNet;
using Candela.Protocols.Enttec;
using Candela.Protocols.Sacn;

namespace Candela.Protocols.Tests;

/// <summary>
/// DMX arrives at up to 44 Hz per universe: the steady-state receive path must not allocate. Each test warms up first
/// (new senders/universes and the runtime's tiered JIT allocate once), then measures.
/// </summary>
public class AllocationTests
{
    private const int Warmup = 1000;
    private const int Measured = 5000;

    [Fact]
    public void ArtDmx_processing_does_not_allocate_once_warm()
    {
        var hub = new DmxInputHub();
        using var receiver = new ArtNetReceiver(hub);
        Assert.True(receiver.Start(TestSupport.LoopbackOptions() with { ArtNetSync = false }));
        var packets = Enumerable.Range(0, 8).Select(u =>
        {
            var b = new byte[600];
            return b[..ArtDmxPacket.Write(b, u, new byte[512], 0)];
        }).ToArray();
        var sender = TestSupport.Sender();
        for (int i = 0; i < Warmup; i++) receiver.ProcessPacket(packets[i % packets.Length], sender);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < Measured; i++) receiver.ProcessPacket(packets[i % packets.Length], sender);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(Warmup + Measured, receiver.Status.Packets);
    }

    [Fact]
    public void Sacn_data_processing_does_not_allocate_once_warm()
    {
        var hub = new DmxInputHub();
        using var receiver = new SacnReceiver(hub);
        Assert.True(receiver.Start(TestSupport.LoopbackOptions(1) with { SacnJoinMulticast = false }));
        var cid = Guid.NewGuid();
        var name = SacnProtocol.EncodeName("Console");
        var slots = new byte[512];
        var buffer = new byte[700];
        var sender = TestSupport.Sender();
        byte sequence = 0;
        for (int i = 0; i < Warmup + Measured; i++)
        {
            if (i == Warmup) Measure.Start();
            int n = SacnPacket.WriteData(buffer, cid, name, 100, 0, ++sequence, 0, 1 + (i & 3), slots);
            receiver.ProcessPacket(buffer.AsSpan(0, n), sender);
        }
        Assert.Equal(0, Measure.Stop());
        Assert.Equal(Warmup + Measured, receiver.Status.Packets);
    }

    [Fact]
    public void Enttec_frame_handling_does_not_allocate_once_warm()
    {
        var hub = new DmxInputHub();
        using var widget = new EnttecDmxUsbPro(hub, name => new FakeEnttecTransport(name));
        Assert.True(widget.Start(new ProtocolOptions { EnttecSerialPort = "alloc" }));
        var frame = EnttecProFrame.EncodeReceivedDmx(new byte[512]);
        var decoder = new EnttecProFrameDecoder(widget.HandleFrame);
        for (int i = 0; i < Warmup; i++) decoder.Feed(frame);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < Measured; i++) decoder.Feed(frame);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private static class Measure
    {
        [ThreadStatic]
        private static long _start;

        public static void Start() => _start = GC.GetAllocatedBytesForCurrentThread();

        public static long Stop() => GC.GetAllocatedBytesForCurrentThread() - _start;
    }
}
