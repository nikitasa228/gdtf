using System.Buffers.Binary;
using Candela.Dmx.Input;
using Candela.Protocols.ArtNet;
using Candela.Protocols.Osc;
using Candela.Protocols.Psn;
using Candela.Protocols.Sacn;

namespace Candela.Protocols.Tests;

/// <summary>
/// Structure-aware fuzzing: random bytes rarely produce a valid header, so these tests keep every header intact and
/// randomise only the length and size fields (the usual source of out-of-range reads). Fixed seeds; nothing may throw.
/// </summary>
public class StructuredFuzzTests
{
    private const int Iterations = 3000;

    [Fact]
    public void Sacn_layer_lengths_are_never_trusted()
    {
        var cid = Guid.NewGuid();
        var seeds = new[]
        {
            Write(b => SacnPacket.WriteData(b, cid, "f", 100, 0, 1, 0, 1, new byte[512])),
            Write(b => SacnPacket.WriteData(b, cid, "f", 100, 7, 2, 0, 1, new byte[3])),
            Write(b => SacnPacket.WriteSync(b, cid, 1, 7)),
            Write(b => SacnPacket.WriteDiscovery(b, cid, "f", 0, 0, new[] { 1, 2, 3, 4 })),
        };
        int[] lengthFields = { 16, 38, 112, 115 };
        var hub = new DmxInputHub();
        using var receiver = new SacnReceiver(hub);
        Assert.True(receiver.Start(TestSupport.LoopbackOptions(1) with { SacnJoinMulticast = false }));
        var random = new Random(11);
        for (int i = 0; i < Iterations; i++)
        {
            var packet = (byte[])seeds[random.Next(seeds.Length)].Clone();
            int edits = 1 + random.Next(3);
            for (int e = 0; e < edits; e++)
            {
                int at = lengthFields[random.Next(lengthFields.Length)];
                if (at + 2 > packet.Length) continue;
                // Small lengths and lengths just around the true one are where off-by-one bounds hide.
                int length = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(at)) & 0x0FFF;
                length = random.Next(3) switch { 0 => random.Next(0, 24), 1 => length + random.Next(-10, 11), _ => random.Next(0x1000) };
                BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(at), (ushort)(0x7000 | (length & 0x0FFF)));
            }
            if (random.Next(3) == 0) BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(123 % packet.Length), (ushort)random.Next(0x10000));
            var span = packet.AsSpan(0, random.Next(4) == 0 ? random.Next(packet.Length + 1) : packet.Length);
            SacnPacket.Classify(span, out _);
            SacnPacket.TryParseData(span, out _, out _);
            SacnPacket.TryParseSync(span, out _, out _);
            SacnPacket.TryParseDiscovery(span, out _, out _);
            receiver.ProcessPacket(span, TestSupport.Sender());
        }
        Assert.True(receiver.Status.BadPackets > 0);
        receiver.ProcessPacket(Write(b => SacnPacket.WriteData(b, Guid.NewGuid(), "after", 200, 0, 1, 0, 1, new byte[] { 42 })),
            TestSupport.Sender("10.9.9.9"));
        Assert.Equal(42, TestSupport.Read(hub, 1)[0]);
    }

    [Fact]
    public void Art_net_length_fields_are_never_trusted()
    {
        var seeds = new[]
        {
            Write(b => ArtDmxPacket.Write(b, 3, new byte[512], 1)),
            Write(b => new ArtPollPacket(0x22, 0, 100, 0).Write(b)),
            Write(b => new ArtTimeCodePacket(1, 2, 3, 4, TimecodeType.Ebu25).Write(b)),
            Write(b => new ArtPollReplyPacket { ShortName = "x", NumPorts = 4 }.Write(b)),
        };
        var hub = new DmxInputHub();
        using var receiver = new ArtNetReceiver(hub);
        Assert.True(receiver.Start(TestSupport.LoopbackOptions()));
        var random = new Random(12);
        for (int i = 0; i < Iterations; i++)
        {
            var packet = (byte[])seeds[random.Next(seeds.Length)].Clone();
            if (packet.Length > 17) BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(16), (ushort)random.Next(0x10000));
            if (packet.Length > 173 && random.Next(2) == 0) BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(172), (ushort)random.Next(0x10000));
            var span = packet.AsSpan(0, random.Next(3) == 0 ? random.Next(packet.Length + 1) : packet.Length);
            ArtDmxPacket.TryParse(span, out _, out _);
            ArtPollPacket.TryParse(span, out _, out _);
            ArtTimeCodePacket.TryParse(span, out _, out _);
            ArtPollReplyPacket.TryParse(span, out _, out _);
            receiver.ProcessPacket(span, new Networking.IPv4Endpoint(0x7F000001, 1 + random.Next(65535)));
        }
    }

    [Fact]
    public void Osc_size_fields_are_never_trusted()
    {
        var seeds = new[]
        {
            OscCodec.Encode(new OscMessage("/dmx/1", OscArgument.Blob(new byte[40]), OscArgument.String("abc"), OscArgument.Int(4))),
            OscCodec.EncodeBundle(1, new[] { OscCodec.Encode(new OscMessage("/a", OscArgument.Float(1))),
                OscCodec.EncodeBundle(2, new[] { new OscMessage("/b", OscArgument.Blob(new byte[7])) }) }),
        };
        var list = new List<OscMessage>();
        using var receiver = new OscReceiver(new DmxInputHub());
        Assert.True(receiver.Start(TestSupport.LoopbackOptions()));
        var random = new Random(13);
        for (int i = 0; i < Iterations; i++)
        {
            var packet = (byte[])seeds[random.Next(seeds.Length)].Clone();
            int edits = 1 + random.Next(3);
            for (int e = 0; e < edits; e++)
            {
                int at = random.Next(packet.Length / 4) * 4;
                int value = random.Next(3) switch { 0 => random.Next(-8, 64), 1 => random.Next(), _ => -random.Next() };
                if (random.Next(2) == 0) value = BinaryPrimitives.ReadInt32BigEndian(packet.AsSpan(at)) + random.Next(-6, 7);
                BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(at), value);
            }
            list.Clear();
            OscCodec.TryParse(packet, list, out _);
            receiver.ProcessPacket(packet, TestSupport.Sender());
        }
    }

    [Fact]
    public void Psn_chunk_lengths_are_never_trusted()
    {
        var header = new PsnPacketHeader(1, 2, 0, 0, 1);
        var seeds = new[]
        {
            PsnTests.CraftedDataPacket(),
            PsnPacketWriter.WriteInfo(header, "sys", new[] { new KeyValuePair<int, string>(1, "a"), new KeyValuePair<int, string>(2, "bb") }),
        };
        var trackers = new List<PsnTracker>();
        var names = new List<KeyValuePair<int, string>>();
        using var receiver = new PsnReceiver();
        Assert.True(receiver.Start(TestSupport.LoopbackOptions()));
        var random = new Random(14);
        for (int i = 0; i < Iterations; i++)
        {
            var packet = (byte[])seeds[random.Next(seeds.Length)].Clone();
            int edits = 1 + random.Next(3);
            for (int e = 0; e < edits; e++)
            {
                // Chunk headers are 4-byte aligned in these seeds: rewrite one length (bits 16–30) and the subchunk flag.
                int at = random.Next(packet.Length / 4) * 4;
                uint h = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(at));
                int length = (int)((h >> 16) & 0x7FFF);
                length = random.Next(3) switch { 0 => random.Next(0, 24), 1 => Math.Max(0, length + random.Next(-6, 7)), _ => random.Next(0x8000) };
                h = (h & 0xFFFF) | ((uint)(length & 0x7FFF) << 16) | (random.Next(2) == 0 ? 0x80000000u : 0);
                BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(at), h);
            }
            PsnPacketReader.TryReadData(packet, out _, trackers, out _);
            PsnPacketReader.TryReadInfo(packet, out _, out _, names, out _);
            receiver.ProcessPacket(packet, TestSupport.Sender());
        }
    }

    private static byte[] Write(Func<byte[], int> writer)
    {
        var buffer = new byte[2048];
        return buffer[..writer(buffer)];
    }
}
