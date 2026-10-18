using Candela.Dmx.Input;
using Candela.Protocols.ArtNet;
using Candela.Protocols.Enttec;
using Candela.Protocols.Osc;
using Candela.Protocols.Psn;
using Candela.Protocols.Sacn;

namespace Candela.Protocols.Tests;

/// <summary>
/// Thousands of random and mutated datagrams through every parser and every receiver's packet path: nothing may throw,
/// and the receivers must keep working afterwards. Seeds are fixed so failures reproduce.
/// </summary>
public class FuzzTests
{
    private const int Iterations = 4000;

    [Fact]
    public void ArtNet_receiver_survives_fuzzing()
    {
        var hub = new DmxInputHub();
        using var receiver = new ArtNetReceiver(hub);
        Assert.True(receiver.Start(TestSupport.LoopbackOptions()));
        var seeds = Seeds(
            Write(b => ArtDmxPacket.Write(b, 3, new byte[512], 1)),
            Write(b => new ArtPollPacket(0x22, 0, 100, 0).Write(b)),
            Write(b => new ArtTimeCodePacket(1, 2, 3, 4, TimecodeType.Ebu25).Write(b)),
            Write(b => ArtSyncPacket.Write(b)),
            Write(b => new ArtPollReplyPacket { ShortName = "x" }.Write(b)));
        Run(seeds, 1, (packet, sender) => receiver.ProcessPacket(packet, sender), packet =>
        {
            ArtDmxPacket.TryParse(packet, out _, out _);
            ArtPollPacket.TryParse(packet, out _, out _);
            ArtTimeCodePacket.TryParse(packet, out _, out _);
            ArtPollReplyPacket.TryParse(packet, out _, out _);
            ArtSyncPacket.TryParse(packet, out _);
        });
        Assert.True(receiver.Status.BadPackets > 0);
        AssertStillWorks(() => receiver.ProcessPacket(Write(b => ArtDmxPacket.Write(b, 0, new byte[] { 123, 0 })), TestSupport.Sender("10.9.9.9")), hub, 1);
    }

    [Fact]
    public void Sacn_receiver_survives_fuzzing()
    {
        var hub = new DmxInputHub();
        using var receiver = new SacnReceiver(hub);
        Assert.True(receiver.Start(TestSupport.LoopbackOptions(1) with { SacnJoinMulticast = false }));
        var cid = Guid.NewGuid();
        var seeds = Seeds(
            Write(b => SacnPacket.WriteData(b, cid, "fuzz", 100, 0, 1, 0, 1, new byte[512])),
            Write(b => SacnPacket.WriteData(b, cid, "fuzz", 100, 9, 1, SacnProtocol.OptionStreamTerminated, 2, new byte[3])),
            Write(b => SacnPacket.WriteSync(b, cid, 1, 9)),
            Write(b => SacnPacket.WriteDiscovery(b, cid, "fuzz", 0, 0, new[] { 1, 2, 3 })));
        Run(seeds, 2, (packet, sender) => receiver.ProcessPacket(packet, sender), packet =>
        {
            SacnPacket.Classify(packet, out _);
            SacnPacket.TryParseData(packet, out _, out _);
            SacnPacket.TryParseSync(packet, out _, out _);
            SacnPacket.TryParseDiscovery(packet, out _, out _);
        });
        Assert.True(receiver.Status.BadPackets > 0);
        AssertStillWorks(() => receiver.ProcessPacket(
            Write(b => SacnPacket.WriteData(b, Guid.NewGuid(), "after", 200, 0, 1, 0, 1, new byte[] { 123 })), TestSupport.Sender("10.9.9.9")), hub, 1);
    }

    [Fact]
    public void Osc_receivers_survive_fuzzing()
    {
        var hub = new DmxInputHub();
        using var receiver = new OscReceiver(hub);
        using var eos = new EtcEosReceiver();
        Assert.True(receiver.Start(TestSupport.LoopbackOptions()));
        Assert.True(eos.Start(TestSupport.LoopbackOptions()));
        var seeds = Seeds(
            OscCodec.Encode(new OscMessage("/dmx/1/1", OscArgument.Int(5), OscArgument.Float(0.5f), OscArgument.String("s"), OscArgument.Blob(new byte[] { 1, 2, 3 }))),
            OscCodec.EncodeBundle(1, new[] { new OscMessage("/dmx/2", OscArgument.Blob(new byte[40])), new OscMessage("/eos/out/active/cue/1/2", OscArgument.Float(1)) }),
            OscCodec.EncodeBundle(1, new[] { OscCodec.EncodeBundle(2, new[] { new OscMessage("/eos/out/event/cue/1/1/fire") }) }));
        var list = new List<OscMessage>();
        Run(seeds, 3, (packet, sender) =>
        {
            receiver.ProcessPacket(packet, sender);
            eos.ProcessPacket(packet, sender);
        }, packet => OscCodec.TryParse(packet, list, out _));
        Assert.True(receiver.Status.BadPackets > 0);
        AssertStillWorks(() => receiver.ProcessPacket(OscCodec.Encode(new OscMessage("/dmx/1/1", OscArgument.Int(123))), TestSupport.Sender("10.9.9.9")), hub, 1);
    }

    [Fact]
    public void Psn_receiver_survives_fuzzing()
    {
        using var receiver = new PsnReceiver();
        Assert.True(receiver.Start(TestSupport.LoopbackOptions()));
        var header = new PsnPacketHeader(1, 2, 0, 0, 1);
        var seeds = Seeds(
            PsnTests.CraftedDataPacket(),
            PsnPacketWriter.WriteInfo(header, "sys", new[] { new KeyValuePair<int, string>(1, "a"), new KeyValuePair<int, string>(2, "b") }));
        var trackers = new List<PsnTracker>();
        var names = new List<KeyValuePair<int, string>>();
        Run(seeds, 4, (packet, sender) => receiver.ProcessPacket(packet, sender), packet =>
        {
            PsnPacketReader.TryReadData(packet, out _, trackers, out _);
            PsnPacketReader.TryReadInfo(packet, out _, out _, names, out _);
        });
        Assert.True(receiver.Status.BadPackets > 0);
        receiver.ProcessPacket(PsnTests.CraftedDataPacket(), TestSupport.Sender("10.9.9.9"));
        Assert.Contains(receiver.Trackers, t => t.Source == "10.9.9.9" && t.Data.Id == 7);
    }

    [Fact]
    public void Enttec_decoder_survives_fuzzing()
    {
        var hub = new DmxInputHub();
        FakeEnttecTransport? transport = null;
        using var widget = new EnttecDmxUsbPro(hub, name => transport = new FakeEnttecTransport(name));
        Assert.True(widget.Start(new ProtocolOptions { EnttecSerialPort = "fuzz" }));
        var random = new Random(5);
        var valid = EnttecProFrame.EncodeReceivedDmx(new byte[512]);
        var decoder = new EnttecProFrameDecoder((label, payload) => widget.HandleFrame(label, payload));
        for (int i = 0; i < Iterations; i++)
        {
            var bytes = i % 2 == 0 ? Mutate(valid, random) : RandomBytes(random, 1 + random.Next(700));
            if (random.Next(4) == 0) bytes[random.Next(bytes.Length)] = EnttecProFrame.StartOfMessage;
            decoder.Feed(bytes);
            EnttecProFrame.TryDecodeReceivedDmx(bytes, out _, out _, out _);
            EnttecProFrame.TryDecodeParameters(bytes, out _);
            EnttecProFrame.TryDecodeSerialNumber(bytes, out _);
            EnttecProFrame.TryApplyChangeOfState(bytes, new byte[512], out _);
        }
        Assert.True(decoder.BadFrames > 0);

        // The real reader thread must also survive garbage and keep delivering.
        for (int i = 0; i < 200; i++) transport!.Deliver(RandomBytes(random, 1 + random.Next(300)));
        var good = new byte[512];
        good[0] = 123;
        transport!.Deliver(new byte[] { 0x7E, 0xE7, 0xE7 });
        transport.Deliver(EnttecProFrame.EncodeReceivedDmx(good));
        Assert.True(TestSupport.WaitUntil(() => TestSupport.Read(hub, 1)[0] == 123, 5000));
        Assert.True(widget.IsRunning);
    }

    private static void Run(byte[][] seeds, int seed, Action<byte[], Networking.IPv4Endpoint> process, Action<byte[]> parse)
    {
        var random = new Random(seed);
        for (int i = 0; i < Iterations; i++)
        {
            byte[] packet = (i % 4) switch
            {
                0 => RandomBytes(random, random.Next(0, 700)),
                1 => Truncate(seeds[random.Next(seeds.Length)], random),
                _ => Mutate(seeds[random.Next(seeds.Length)], random),
            };
            // Senders stay in 127/8: a fuzzed ArtPoll is answered, and replies must never leave the machine.
            var sender = new Networking.IPv4Endpoint(0x7F000000u | (uint)random.Next(1 << 24), random.Next(1, 65536));
            parse(packet);
            process(packet, sender);
        }
    }

    private static void AssertStillWorks(Action send, DmxInputHub hub, int universe)
    {
        send();
        Assert.Contains(hub.Sources(), s => s.Universe == universe && s.Source.Endpoint.StartsWith("10.9.9.9", StringComparison.Ordinal));
    }

    private static byte[][] Seeds(params byte[][] seeds) => seeds;

    private static byte[] Write(Func<byte[], int> writer)
    {
        var buffer = new byte[2048];
        return buffer[..writer(buffer)];
    }

    private static byte[] RandomBytes(Random random, int length)
    {
        var b = new byte[length];
        random.NextBytes(b);
        return b;
    }

    private static byte[] Truncate(byte[] seed, Random random) => seed[..random.Next(0, seed.Length + 1)];

    private static byte[] Mutate(byte[] seed, Random random)
    {
        var b = (byte[])seed.Clone();
        int flips = 1 + random.Next(8);
        for (int f = 0; f < flips && b.Length > 0; f++)
        {
            int at = random.Next(b.Length);
            b[at] = random.Next(3) switch
            {
                0 => (byte)random.Next(256),
                1 => (byte)(b[at] ^ (1 << random.Next(8))),
                _ => random.Next(2) == 0 ? (byte)0 : (byte)0xFF,
            };
        }
        return b;
    }
}
