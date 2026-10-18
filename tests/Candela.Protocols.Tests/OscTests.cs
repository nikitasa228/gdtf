using System.Net;
using System.Net.Sockets;
using Candela.Dmx.Input;
using Candela.Protocols.Osc;

namespace Candela.Protocols.Tests;

public class OscTests
{
    [Fact]
    public void Message_with_every_argument_type_round_trips()
    {
        var message = new OscMessage("/test/all",
            OscArgument.Int(-5),
            OscArgument.Float(0.25f),
            OscArgument.String("héllo"),
            OscArgument.Blob(new byte[] { 1, 2, 3, 4, 5 }),
            OscArgument.Long(1L << 40),
            OscArgument.Double(Math.PI),
            OscArgument.Time(OscCodec.Immediately),
            OscArgument.Symbol("sym"),
            OscArgument.Character('Z'),
            OscArgument.Color(0x11223344),
            OscArgument.Midi(0x00903C7F),
            OscArgument.Bool(true),
            OscArgument.Bool(false),
            OscArgument.Marker(OscType.Nil),
            OscArgument.Marker(OscType.Impulse),
            OscArgument.Marker(OscType.ArrayBegin),
            OscArgument.Int(1),
            OscArgument.Marker(OscType.ArrayEnd));
        var bytes = OscCodec.Encode(message);
        Assert.Equal(0, bytes.Length % 4);
        var parsed = new List<OscMessage>();
        Assert.True(OscCodec.TryParse(bytes, parsed, out var error), error);
        var m = Assert.Single(parsed);
        Assert.Equal("/test/all", m.Address);
        Assert.Equal(message.Arguments, m.Arguments);
        Assert.Null(m.BundleTimeTag);
    }

    [Fact]
    public void Known_encoding_of_a_simple_message()
    {
        // "/dmx/1/12" ",i" 255 — the canonical OSC 1.0 layout.
        var bytes = OscCodec.Encode(new OscMessage("/dmx/1/12", OscArgument.Int(255)));
        var expected = "/dmx/1/12\0\0\0,i\0\0"u8.ToArray().Concat(new byte[] { 0, 0, 0, 255 }).ToArray();
        Assert.Equal(expected, bytes);
    }

    [Fact]
    public void Nested_bundles_are_flattened_with_their_time_tags()
    {
        var inner = OscCodec.EncodeBundle(42, new[] { new OscMessage("/b", OscArgument.Float(1)) });
        var outer = OscCodec.EncodeBundle(OscCodec.Immediately, new[] { OscCodec.Encode(new OscMessage("/a", OscArgument.Int(1))), inner });
        var parsed = new List<OscMessage>();
        Assert.True(OscCodec.TryParse(outer, parsed, out _));
        Assert.Equal(new[] { "/a", "/b" }, parsed.Select(m => m.Address));
        Assert.Equal(OscCodec.Immediately, parsed[0].BundleTimeTag);
        Assert.Equal(42UL, parsed[1].BundleTimeTag);
    }

    [Fact]
    public void Malformed_packets_are_rejected_and_nothing_is_appended()
    {
        var parsed = new List<OscMessage>();
        Assert.False(OscCodec.TryParse(new byte[] { 1, 2, 3 }, parsed, out _));
        Assert.False(OscCodec.TryParse("/abc"u8, parsed, out _)); // unterminated
        Assert.False(OscCodec.TryParse("/a\0\0,x\0\0"u8, parsed, out var error)); // unknown tag
        Assert.Contains("'x'", error);
        Assert.False(OscCodec.TryParse("/a\0\0,i\0\0\0\0"u8, parsed, out _)); // truncated int
        var bundle = OscCodec.EncodeBundle(1, new[] { new OscMessage("/ok") });
        bundle[19] = 200; // element size beyond the packet
        Assert.False(OscCodec.TryParse(bundle, parsed, out _));
        Assert.Empty(parsed);

        // A message without a type-tag string (OSC 1.0) has no arguments.
        Assert.True(OscCodec.TryParse("/ping\0\0\0"u8, parsed, out _));
        Assert.Empty(parsed.Single().Arguments);
    }

    [Fact]
    public void Deeply_nested_bundles_are_refused()
    {
        var packet = OscCodec.Encode(new OscMessage("/x"));
        for (int i = 0; i < OscCodec.MaxBundleDepth + 2; i++) packet = OscCodec.EncodeBundle(1, new[] { packet });
        Assert.False(OscCodec.TryParse(packet, new List<OscMessage>(), out var error));
        Assert.Contains("deeply", error);
    }

    [Theory]
    [InlineData("/dmx/{universe}/{channel}", "/dmx/3/17", true, 3, 17)]
    [InlineData("/dmx/{universe}/{channel}", "/dmx/3/513", false, 0, 0)]
    [InlineData("/dmx/{universe}/{channel}", "/dmx/0/1", false, 0, 0)]
    [InlineData("/dmx/{universe}/{channel}", "/dmx/3", false, 0, 0)]
    [InlineData("/dmx/{universe}/{channel}", "/dmx/3/4/5", false, 0, 0)]
    [InlineData("/dmx/{universe}/{channel}", "/DMX/3/4", false, 0, 0)]
    [InlineData("/dmx/{universe}", "/dmx/12", true, 12, 1)]
    [InlineData("/fader/{channel} universe=2", "/fader/9", true, 2, 9)]
    [InlineData("/*/level/{channel}", "/console/level/5", true, 1, 5)]
    [InlineData("/*/level/{channel}", "/level/5", false, 0, 0)]
    public void Patterns_match_addresses(string pattern, string address, bool matches, int universe, int channel)
    {
        var p = OscDmxPattern.Parse(pattern);
        Assert.Equal(matches, p.TryMatch(address, out int u, out int c));
        if (!matches) return;
        Assert.Equal(universe, u);
        Assert.Equal(channel, c);
    }

    [Theory]
    [InlineData("")]
    [InlineData("dmx/{channel}")]
    [InlineData("/dmx/{channel}/{channel}")]
    [InlineData("/dmx/{slot}")]
    [InlineData("/dmx//x")]
    [InlineData("/dmx/{channel} scale=loud")]
    [InlineData("/dmx/{channel} universe=0")]
    public void Bad_patterns_are_rejected_with_a_reason(string text)
    {
        Assert.False(OscDmxPattern.TryParse(text, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Values_are_scaled_by_type_and_option()
    {
        var slots = new byte[512];
        var auto = OscDmxPattern.Parse("/dmx/{universe}/{channel}");
        Assert.Equal(5, auto.WriteValues(new OscMessage("/x", OscArgument.Int(300), OscArgument.Float(0.5f), OscArgument.Int(-4),
            OscArgument.Double(2), OscArgument.Bool(true)), 1, slots));
        Assert.Equal(new byte[] { 255, 128, 0, 255, 255 }, slots[..5]);

        var percent = OscDmxPattern.Parse("/f/{channel} scale=percent");
        percent.WriteValues(new OscMessage("/x", OscArgument.Float(50)), 10, slots);
        Assert.Equal(128, slots[9]);

        var normalized = OscDmxPattern.Parse("/f/{channel} scale=normalized");
        normalized.WriteValues(new OscMessage("/x", OscArgument.Int(1)), 11, slots);
        Assert.Equal(255, slots[10]);

        var blob = new byte[600];
        blob[0] = 7;
        blob[511] = 9;
        Assert.Equal(512, auto.WriteValues(new OscMessage("/x", OscArgument.Blob(blob)), 1, slots));
        Assert.Equal(7, slots[0]);
        Assert.Equal(9, slots[511]);

        Assert.Equal(0, auto.WriteValues(new OscMessage("/x", OscArgument.String("255"), OscArgument.Float(float.NaN)), 1, slots));
        Assert.Equal(1, auto.WriteValues(new OscMessage("/x", OscArgument.Int(1), OscArgument.Int(2)), 512, slots));
    }

    [Fact]
    public void Loopback_messages_and_bundles_drive_the_hub_and_are_exposed_raw()
    {
        var hub = new DmxInputHub();
        using var receiver = new OscReceiver(hub);
        var raw = new List<OscReceivedMessage>();
        receiver.MessageReceived += (_, e) =>
        {
            lock (raw) raw.Add(e.Received);
        };
        Assert.True(receiver.Start(TestSupport.LoopbackOptions()), receiver.Status.Message);
        using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var target = TestSupport.Loopback(receiver.LocalEndPoint!.Port);

        client.Send(OscCodec.Encode(new OscMessage("/dmx/2/10", OscArgument.Int(200))), target);
        client.Send(OscCodec.EncodeBundle(OscCodec.Immediately, new[]
        {
            new OscMessage("/dmx/2/11", OscArgument.Float(1f)),
            new OscMessage("/dmx/3", OscArgument.Blob(new byte[] { 5, 6, 7 })),
            new OscMessage("/eos/out/show/name", OscArgument.String("Gala")),
        }), target);

        Assert.True(TestSupport.WaitUntil(() => TestSupport.Read(hub, 2)[10] == 255 && TestSupport.HasUniverse(hub, 3)));
        Assert.Equal(200, TestSupport.Read(hub, 2)[9]);
        Assert.Equal(new byte[] { 5, 6, 7 }, TestSupport.Read(hub, 3)[..3]);
        Assert.True(TestSupport.WaitUntil(() =>
        {
            lock (raw) return raw.Count == 4;
        }));
        Assert.Contains(receiver.RecentMessages, m => m.Message.Address == "/eos/out/show/name" && m.Message.Arguments[0].AsString == "Gala");
        Assert.StartsWith("127.0.0.1:", receiver.RecentMessages[0].Sender);
        Assert.Equal(1, receiver.Status.IgnoredPackets); // the Eos message has no DMX mapping
        Assert.Equal("osc", hub.Sources().First().Source.Protocol);

        receiver.Stop();
        Assert.Empty(hub.ActiveUniverses());
    }

    [Fact]
    public void Keep_alive_resubmits_held_universes()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var hub = new DmxInputHub(() => now);
        using var receiver = new OscReceiver(hub);
        Assert.True(receiver.Start(TestSupport.LoopbackOptions()));
        receiver.ProcessPacket(OscCodec.Encode(new OscMessage("/dmx/1/1", OscArgument.Int(77))), TestSupport.Sender());
        long before = hub.Sources().Single().PacketCount;
        Assert.True(TestSupport.WaitUntil(() => hub.Sources().Single().PacketCount > before, 4000), "the keep-alive timer must resubmit");
        now += TimeSpan.FromSeconds(2);
        Assert.True(TestSupport.WaitUntil(() => hub.Sources().Single().LastSeenUtc >= now, 4000));
        Assert.Equal(77, TestSupport.Read(hub, 1)[0]);
    }

    [Fact]
    public void Invalid_patterns_become_warnings()
    {
        using var receiver = new OscReceiver(new DmxInputHub());
        Assert.True(receiver.Start(TestSupport.LoopbackOptions() with { OscPatterns = new[] { "/ok/{channel}", "broken" } }));
        Assert.Equal(ProtocolState.Warning, receiver.Status.State);
        Assert.Single(receiver.Patterns);
        Assert.Contains("broken", receiver.Status.Warnings.Single());
    }

    [Fact]
    public void Eos_output_updates_the_console_state()
    {
        var state = EosConsoleState.Empty;
        state = EtcEosReceiver.Apply(state, new OscMessage("/eos/out/show/name", OscArgument.String("Hamlet")), out _);
        state = EtcEosReceiver.Apply(state, new OscMessage("/eos/out/active/cue/1/5.5", OscArgument.Float(0.4f)), out _);
        state = EtcEosReceiver.Apply(state, new OscMessage("/eos/out/active/cue/text", OscArgument.String("1/5.5 Sunset 3 40%")), out _);
        state = EtcEosReceiver.Apply(state, new OscMessage("/eos/out/pending/cue/1/6"), out _);
        state = EtcEosReceiver.Apply(state, new OscMessage("/eos/out/event/state", OscArgument.Int(1)), out _);
        state = EtcEosReceiver.Apply(state, new OscMessage("/eos/out/event/cue/1/5.5/fire"), out var fired);
        Assert.Equal("Hamlet", state.ShowName);
        Assert.Equal(new EosCue("1", "5.5"), state.ActiveCue);
        Assert.Equal(0.4, state.ActiveCueProgress, 3);
        Assert.Equal("1/5.5 Sunset 3 40%", state.ActiveCueText);
        Assert.Equal(new EosCue("1", "6"), state.PendingCue);
        Assert.True(state.Live);
        Assert.Equal(new EosCue("1", "5.5"), fired);
        Assert.Equal(fired, state.LastFiredCue);
        Assert.Same(state, EtcEosReceiver.Apply(state, new OscMessage("/other/thing"), out _));
    }

    [Fact]
    public async Task Eos_receiver_listens_and_raises_cue_events()
    {
        using var eos = new EtcEosReceiver();
        var fired = new TaskCompletionSource<EosCue>(TaskCreationOptions.RunContinuationsAsynchronously);
        eos.CueFired += c => fired.TrySetResult(c);
        Assert.True(eos.Start(TestSupport.LoopbackOptions()));
        using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        client.Send(OscCodec.Encode(new OscMessage("/eos/out/event/cue/2/17/fire")), TestSupport.Loopback(eos.LocalEndPoint!.Port));
        var cue = await fired.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(new EosCue("2", "17"), cue);
        Assert.Equal(new EosCue("2", "17"), eos.State.LastFiredCue);
        Assert.NotNull(eos.State.LastMessageUtc);
    }
}
