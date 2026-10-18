using System.Collections.Concurrent;
using Candela.Dmx.Input;
using Candela.Protocols.Enttec;

namespace Candela.Protocols.Tests;

/// <summary>An in-memory widget: bytes queued by the test are "received"; written frames are recorded.</summary>
internal sealed class FakeEnttecTransport : IEnttecTransport
{
    private readonly BlockingCollection<byte[]> _incoming = new();
    private readonly object _lock = new();
    private readonly List<byte> _written = new();
    private byte[]? _partial;
    private int _partialOffset;
    private volatile bool _unplugged;

    public FakeEnttecTransport(string name) => Name = name;

    public string Name { get; }

    public bool IsOpen { get; private set; }

    public bool FailOpen { get; init; }

    public byte[] Written
    {
        get
        {
            lock (_lock) return _written.ToArray();
        }
    }

    public void Deliver(byte[] bytes) => _incoming.Add(bytes);

    public void Unplug() => _unplugged = true;

    public void Open()
    {
        if (FailOpen) throw new IOException("No such device");
        IsOpen = true;
    }

    public int Read(Span<byte> buffer)
    {
        if (_unplugged) throw new IOException("Device removed");
        if (ReadFailure is { } failure) throw failure;
        if (_partial is null)
        {
            if (!_incoming.TryTake(out _partial, 20)) return 0;
            _partialOffset = 0;
        }
        int n = Math.Min(buffer.Length, _partial.Length - _partialOffset);
        _partial.AsSpan(_partialOffset, n).CopyTo(buffer);
        _partialOffset += n;
        if (_partialOffset == _partial.Length) _partial = null;
        return n;
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        if (_unplugged) throw new IOException("Device removed");
        lock (_lock) _written.AddRange(data.ToArray());
    }

    public void Close() => IsOpen = false;

    public bool Disposed { get; private set; }

    /// <summary>When set, <see cref="Read"/> throws this instead of reading (unexpected driver failures).</summary>
    public Exception? ReadFailure { get; set; }

    public void Dispose()
    {
        Close();
        Disposed = true;
    }
}

public class EnttecTests
{
    [Fact]
    public void Frames_encode_with_som_label_length_and_eom()
    {
        var frame = EnttecProFrame.Encode(EnttecProLabel.GetWidgetSerialNumber, ReadOnlySpan<byte>.Empty);
        Assert.Equal(new byte[] { 0x7E, 10, 0, 0, 0xE7 }, frame);
        Assert.Equal(new byte[] { 0x7E, 3, 2, 0, 0, 0, 0xE7 }, EnttecProFrame.EncodeGetParameters());
        Assert.Equal(new byte[] { 0x7E, 8, 1, 0, 0, 0xE7 }, EnttecProFrame.EncodeReceiveDmxOnChange(false));

        var dmx = new byte[600];
        int n = EnttecProFrame.EncodeSendDmx(dmx, new byte[] { 1, 2, 3 });
        Assert.Equal(5 + 25, n);
        Assert.Equal(new byte[] { 0x7E, 6, 25, 0, 0, 1, 2, 3 }, dmx[..8]);
        Assert.Equal(0xE7, dmx[n - 1]);
        n = EnttecProFrame.EncodeSendDmx(dmx, new byte[512]);
        Assert.Equal(518, n);
        Assert.Equal(new byte[] { 0x01, 0x02 }, dmx[2..4]); // 513 = 0x0201, LSB first
    }

    [Fact]
    public void Decoder_handles_chunks_garbage_and_corruption()
    {
        var frames = new List<(EnttecProLabel Label, byte[] Payload)>();
        var decoder = new EnttecProFrameDecoder((label, payload) => frames.Add((label, payload.ToArray())));
        var a = EnttecProFrame.EncodeReceivedDmx(new byte[] { 10, 20, 30 });
        var b = EnttecProFrame.EncodeSerialNumberReply(12345678);
        var corrupt = (byte[])a.Clone();
        corrupt[^1] = 0x00; // wrong end byte
        var tooLong = new byte[] { 0x7E, 5, 0xFF, 0xFF };
        var stream = new byte[] { 1, 2, 3 }.Concat(corrupt).Concat(tooLong).Concat(a).Concat(new byte[] { 0x55 }).Concat(b).ToArray();

        foreach (var chunk in stream.Chunk(3)) decoder.Feed(chunk);

        Assert.Equal(2, frames.Count);
        Assert.Equal(EnttecProLabel.ReceivedDmx, frames[0].Label);
        Assert.Equal(new byte[] { 0, 0, 10, 20, 30 }, frames[0].Payload);
        Assert.Equal(EnttecProLabel.GetWidgetSerialNumber, frames[1].Label);
        Assert.True(decoder.BadFrames >= 2);
        Assert.Equal(2, decoder.Frames);
    }

    [Fact]
    public void Replies_decode()
    {
        var parameters = new EnttecWidgetParameters(0x0144, 9, 1, 40, new byte[] { 7 });
        var frames = new List<(EnttecProLabel, byte[])>();
        var decoder = new EnttecProFrameDecoder((l, p) => frames.Add((l, p.ToArray())));
        decoder.Feed(EnttecProFrame.EncodeParametersReply(parameters));
        decoder.Feed(EnttecProFrame.EncodeSerialNumberReply(2_001_234));
        Assert.True(EnttecProFrame.TryDecodeParameters(frames[0].Item2, out var parsed));
        Assert.Equal(0x0144, parsed!.FirmwareVersion);
        Assert.Equal(96.03, parsed.BreakTimeMicroseconds, 2);
        Assert.Equal(40, parsed.OutputRateHz);
        Assert.Equal(new byte[] { 7 }, parsed.UserConfiguration);
        Assert.Equal(new byte[] { 0x34, 0x12, 0x00, 0x02 }, frames[1].Item2);
        Assert.True(EnttecProFrame.TryDecodeSerialNumber(frames[1].Item2, out var serial));
        Assert.Equal(2_001_234u, serial);
        Assert.False(EnttecProFrame.TryDecodeParameters(new byte[] { 1, 2 }, out _));
    }

    [Fact]
    public void Change_of_state_updates_only_flagged_slots()
    {
        var slots = new byte[512];
        slots[9] = 1;
        // Block 1 → slots 8..47 of the receive buffer (index 0 is the start code). Bits 1 and 2 → buffer 9 and 10 = slots 9, 10.
        var payload = new byte[] { 1, 0b0000_0110, 0, 0, 0, 0, 0xAA, 0xBB };
        Assert.True(EnttecProFrame.TryApplyChangeOfState(payload, slots, out int changed));
        Assert.Equal(2, changed);
        Assert.Equal(0xAA, slots[8]);
        Assert.Equal(0xBB, slots[9]);
        Assert.False(EnttecProFrame.TryApplyChangeOfState(new byte[] { 0, 0xFF, 0, 0, 0, 0, 1 }, slots, out _)); // 8 bits, 1 value
    }

    [Fact]
    public void Fake_widget_delivers_dmx_into_the_hub()
    {
        var hub = new DmxInputHub();
        FakeEnttecTransport? transport = null;
        using var widget = new EnttecDmxUsbPro(hub, name => transport = new FakeEnttecTransport(name));
        var info = new ManualResetEventSlim();
        widget.WidgetInfoReceived += (_, _) =>
        {
            if (widget.SerialNumber is not null && widget.WidgetParameters is not null) info.Set();
        };
        Assert.True(widget.Start(new ProtocolOptions { EnttecSerialPort = "/dev/ttyUSB7", EnttecUniverse = 4 }), widget.Status.Message);
        Assert.Equal(ProtocolState.Running, widget.Status.State);

        // Start-up: receive-always, get parameters, get serial number.
        var written = transport!.Written;
        Assert.Equal(EnttecProFrame.EncodeReceiveDmxOnChange(false).Concat(EnttecProFrame.EncodeGetParameters())
            .Concat(EnttecProFrame.EncodeGetSerialNumber()), written);

        var slots = new byte[512];
        slots[0] = 255;
        slots[300] = 77;
        transport.Deliver(EnttecProFrame.EncodeParametersReply(new EnttecWidgetParameters(0x0144, 9, 1, 40, Array.Empty<byte>())));
        transport.Deliver(EnttecProFrame.EncodeSerialNumberReply(11223344));
        transport.Deliver(EnttecProFrame.EncodeReceivedDmx(slots));
        Assert.True(TestSupport.WaitUntil(() => TestSupport.HasUniverse(hub, 4)));
        Assert.Equal(255, TestSupport.Read(hub, 4)[0]);
        Assert.Equal(77, TestSupport.Read(hub, 4)[300]);
        Assert.True(info.Wait(TimeSpan.FromSeconds(3)));
        Assert.Equal(11223344u, widget.SerialNumber);
        Assert.Equal(40, widget.WidgetParameters!.OutputRateHz);
        var source = Assert.Single(hub.Sources());
        Assert.Equal(new DmxSourceId("enttec", "/dev/ttyUSB7"), source.Source);

        // Overrun frames are dropped, non-zero start codes ignored, change-of-state applied.
        transport.Deliver(EnttecProFrame.EncodeReceivedDmx(new byte[512], status: 0x02));
        transport.Deliver(EnttecProFrame.EncodeReceivedDmx(new byte[] { 1 }, startCode: 0xCC));
        transport.Deliver(EnttecProFrame.Encode(EnttecProLabel.ReceivedDmxChangeOfState, new byte[] { 0, 0b10, 0, 0, 0, 0, 33 }));
        Assert.True(TestSupport.WaitUntil(() => TestSupport.Read(hub, 4)[0] == 33));
        Assert.Equal(77, TestSupport.Read(hub, 4)[300]);
        Assert.True(TestSupport.WaitUntil(() => widget.Status.BadPackets == 1 && widget.Status.IgnoredPackets == 1));
        Assert.Contains("overrun", widget.Status.LastBadPacket);

        // DMX output through label 6.
        Assert.True(widget.SendDmx(new byte[] { 9, 8, 7 }));
        Assert.Equal(new byte[] { 0x7E, 6, 25, 0, 0, 9, 8, 7 }, transport.Written[^30..^22]);

        widget.Stop();
        Assert.False(transport.IsOpen);
        Assert.Empty(hub.ActiveUniverses());
    }

    [Fact]
    public void Unplugged_widget_becomes_an_error_status()
    {
        FakeEnttecTransport? transport = null;
        using var widget = new EnttecDmxUsbPro(new DmxInputHub(), name => transport = new FakeEnttecTransport(name));
        Assert.True(widget.Start(new ProtocolOptions { EnttecSerialPort = "COM9" }));
        transport!.Unplug();
        Assert.True(TestSupport.WaitUntil(() => widget.Status.State == ProtocolState.Error));
        Assert.Contains("disconnected", widget.Status.LastError);
        Assert.False(widget.SendDmx(new byte[] { 1 }));
        widget.Stop();
        Assert.Equal(ProtocolState.Stopped, widget.Status.State);
    }

    [Fact]
    public void Missing_port_or_open_failure_is_an_error_status()
    {
        using var widget = new EnttecDmxUsbPro(new DmxInputHub(), name => new FakeEnttecTransport(name) { FailOpen = true });
        Assert.False(widget.Start(new ProtocolOptions()));
        Assert.Contains("serial port", widget.Status.LastError);
        Assert.False(widget.Start(new ProtocolOptions { EnttecSerialPort = "COM42" }));
        Assert.Contains("COM42", widget.Status.LastError);
        Assert.Equal(ProtocolState.Error, widget.Status.State);
    }

    [Fact]
    public void Serial_port_listing_never_throws() => Assert.NotNull(SerialPortEnttecTransport.GetPortNames());
}
