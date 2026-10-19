using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Candela.Dmx.Input;
using Candela.Protocols.ArtNet;
using Candela.Protocols.Enttec;
using Candela.Protocols.Osc;
using Candela.Protocols.Psn;
using Candela.Protocols.Sacn;

namespace Candela.Protocols.Tests;

/// <summary>
/// Structured protocol status for the editor: state, bound endpoint, interface, universe count, error code and the
/// message as a key with arguments (English kept), plus the sender of ArtTimeCode.
/// </summary>
public sealed class StructuredStatusTests
{
    private static void AssertConsistent(ProtocolStatus status)
    {
        Assert.Equal(status.Message, status.Text.English);
        Assert.Equal(ProtocolMessages.Format(status.Text.Key, status.Text.Arguments), status.Text.English);
        Assert.NotEqual(ProtocolMessages.Literal, status.Text.Key);
        Assert.Equal(status.LastError, status.ErrorText?.English);
        Assert.Equal(status.Warnings, status.WarningTexts.Select(w => w.English));
    }

    [Fact]
    public void Every_template_takes_as_many_arguments_as_it_has_placeholders()
    {
        foreach (var (key, template) in ProtocolMessages.All)
        {
            int count = Regex.Matches(template, @"\{(\d+)\}").Select(m => int.Parse(m.Groups[1].Value)).DefaultIfEmpty(-1).Max() + 1;
            var args = Enumerable.Range(0, count).Select(i => (object?)$"a{i}").ToArray();
            var text = ProtocolText.Create(key, args);
            for (int i = 0; i < count; i++) Assert.Contains($"a{i}", text.English, StringComparison.Ordinal);
            Assert.DoesNotContain("{", text.English, StringComparison.Ordinal);
        }
        Assert.Equal("x y", ProtocolMessages.Format("no.such.key", new[] { "x", "y" }));
        Assert.Equal("Stopped", ProtocolText.Create(ProtocolMessages.Stopped).English);
        Assert.Equal(ProtocolText.Create(ProtocolMessages.Stopped), ProtocolText.Create(ProtocolMessages.Stopped));
    }

    [Fact]
    public void Running_art_net_reports_endpoint_universes_and_a_keyed_message()
    {
        using var receiver = new ArtNetReceiver(new DmxInputHub());
        Assert.Equal(ProtocolMessages.Stopped, receiver.Status.Text.Key);
        Assert.True(receiver.Start(TestSupport.LoopbackOptions(1, 2, 3)));
        var status = receiver.Status;
        AssertConsistent(status);
        Assert.Equal(ProtocolState.Running, status.State);
        Assert.Equal(ProtocolMessages.ArtNetListeningAll, status.Text.Key);
        Assert.Equal(receiver.LocalEndPoint!.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), status.Text.Arguments[0]);
        Assert.Equal("3", status.Text.Arguments[1]);
        Assert.Equal(receiver.LocalEndPoint.ToString(), status.BoundEndpoint);
        Assert.Null(status.NetworkInterface);
        Assert.Equal(3, status.UniverseCount);
        Assert.Equal(ProtocolErrorCode.None, status.ErrorCode);

        receiver.Stop();
        status = receiver.Status;
        AssertConsistent(status);
        Assert.Equal(ProtocolMessages.Stopped, status.Text.Key);
        Assert.Null(status.BoundEndpoint);
        Assert.Equal(0, status.UniverseCount);
    }

    [Fact]
    public void A_selected_interface_is_named_in_the_status()
    {
        var loopback = ProtocolManager.GetNetworkInterfaces().FirstOrDefault(i => i.IsLoopback);
        if (loopback is null) return; // no loopback adapter listed on this machine
        using var receiver = new SacnReceiver(new DmxInputHub());
        Assert.True(receiver.Start(TestSupport.LoopbackOptions(1) with { NetworkInterface = loopback.Name, SacnJoinMulticast = false }));
        var status = receiver.Status;
        AssertConsistent(status);
        Assert.Equal(ProtocolMessages.SacnUnicast, status.Text.Key);
        Assert.Equal(loopback.Name, status.Text.Arguments[1]);
        Assert.Equal($"{loopback.Name} {loopback.Address}", status.NetworkInterface);
        Assert.Equal(1, status.UniverseCount);
    }

    [Fact]
    public void Errors_carry_a_code_and_a_keyed_text_that_survive_a_stop()
    {
        using var blocker = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        if (OperatingSystem.IsWindows()) blocker.ExclusiveAddressUse = true;
        blocker.Bind(new IPEndPoint(IPAddress.Any, 0));
        int port = ((IPEndPoint)blocker.LocalEndPoint!).Port;
        using var receiver = new ArtNetReceiver(new DmxInputHub());

        Assert.False(receiver.Start(TestSupport.LoopbackOptions() with { ArtNetPort = port }));
        var status = receiver.Status;
        AssertConsistent(status);
        Assert.Equal(ProtocolErrorCode.PortInUse, status.ErrorCode);
        Assert.Equal(ProtocolMessages.PortInUse, status.ErrorText!.Key);
        Assert.Equal(new[] { "Art-Net", port.ToString(System.Globalization.CultureInfo.InvariantCulture) }, status.ErrorText.Arguments);
        receiver.Stop();
        Assert.Equal(ProtocolErrorCode.PortInUse, receiver.Status.ErrorCode); // kept for the panel after a stop

        Assert.False(receiver.Start(TestSupport.LoopbackOptions() with { NetworkInterface = "no-such-adapter-42" }));
        status = receiver.Status;
        AssertConsistent(status);
        Assert.Equal(ProtocolErrorCode.InterfaceNotFound, status.ErrorCode);
        Assert.Equal("no-such-adapter-42", status.ErrorText!.Arguments[0]);

        Assert.False(receiver.Start(TestSupport.LoopbackOptions() with { ArtNetPort = 70000 }));
        Assert.Equal(ProtocolErrorCode.InvalidSettings, receiver.Status.ErrorCode);
        Assert.Equal(ProtocolMessages.InvalidPort, receiver.Status.ErrorText!.Key);
        Assert.Equal("Art-Net port 70000 is not a valid UDP port (0–65535).", receiver.Status.LastError);

        Assert.True(receiver.Start(TestSupport.LoopbackOptions()));
        Assert.Equal(ProtocolErrorCode.None, receiver.Status.ErrorCode);
        Assert.Null(receiver.Status.ErrorText);
    }

    [Fact]
    public void Port_conflicts_and_unavailable_protocols_have_their_codes()
    {
        using var manager = new ProtocolManager(new DmxInputHub());
        var options = TestSupport.LoopbackOptions() with { OscPort = 39124, EosOscPort = 39124 };
        var statuses = manager.Apply(new[] { ProtocolIds.Osc, ProtocolIds.EtcEos, ProtocolIds.Ndi }, options);
        var osc = statuses.Single(s => s.ProtocolId == ProtocolIds.Osc);
        AssertConsistent(osc);
        Assert.Equal(ProtocolErrorCode.PortConflict, osc.ErrorCode);
        Assert.Equal(ProtocolMessages.PortConflict, osc.ErrorText!.Key);
        Assert.Equal("39124", osc.ErrorText.Arguments[2]);
        var ndi = statuses.Single(s => s.ProtocolId == ProtocolIds.Ndi);
        AssertConsistent(ndi);
        Assert.Equal(ProtocolState.Unavailable, ndi.State);
        Assert.Equal(ProtocolErrorCode.Unavailable, ndi.ErrorCode);
        Assert.Equal(ProtocolMessages.Unavailable, ndi.Text.Key);
    }

    [Fact]
    public void Warnings_and_device_errors_are_keyed()
    {
        using (var osc = new OscReceiver(new DmxInputHub()))
        {
            Assert.True(osc.Start(TestSupport.LoopbackOptions() with { OscPatterns = new[] { "/ok/{channel}", "/fixed/{channel} universe=4", "broken" } }));
            var status = osc.Status;
            AssertConsistent(status);
            Assert.Equal(ProtocolMessages.OscPatternIgnored, Assert.Single(status.WarningTexts).Key);
            Assert.Equal(ProtocolMessages.ListeningAll, status.Text.Key);
        }

        using var widget = new EnttecDmxUsbPro(new DmxInputHub(), name => new FakeEnttecTransport(name) { FailOpen = true });
        Assert.False(widget.Start(new ProtocolOptions()));
        Assert.Equal(ProtocolErrorCode.InvalidSettings, widget.Status.ErrorCode);
        Assert.False(widget.Start(new ProtocolOptions { EnttecSerialPort = "COM42" }));
        AssertConsistent(widget.Status);
        Assert.Equal(ProtocolErrorCode.DeviceNotFound, widget.Status.ErrorCode);
        Assert.Equal(ProtocolMessages.EnttecOpenFailed, widget.Status.ErrorText!.Key);
        Assert.Equal("COM42", widget.Status.ErrorText.Arguments[0]);

        using var ok = new EnttecDmxUsbPro(new DmxInputHub(), name => new FakeEnttecTransport(name));
        Assert.True(ok.Start(new ProtocolOptions { EnttecSerialPort = "COM7", EnttecUniverse = 3 }));
        AssertConsistent(ok.Status);
        Assert.Equal(ProtocolMessages.EnttecReceiving, ok.Status.Text.Key);
        Assert.Equal("COM7", ok.Status.BoundEndpoint);
        Assert.Equal(1, ok.Status.UniverseCount);
    }

    [Fact]
    public void Psn_reports_its_trackers_with_a_key()
    {
        using var receiver = new PsnReceiver();
        Assert.True(receiver.Start(TestSupport.LoopbackOptions()));
        AssertConsistent(receiver.Status);
        Assert.Contains(receiver.Status.Text.Key, new[] { ProtocolMessages.PsnListeningAll, ProtocolMessages.ListeningAll });
        Assert.Equal(0, receiver.Status.UniverseCount);
    }

    [Fact]
    public async Task ArtTimeCode_names_its_sender_in_the_event_and_the_hub()
    {
        var hub = new DmxInputHub();
        var samples = new TaskCompletionSource<TimecodeSample>(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.TimecodeSampleReceived += s => samples.TrySetResult(s);
        using var receiver = new ArtNetReceiver(hub);
        var events = new TaskCompletionSource<ArtTimeCodeReceived>(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.TimeCodeReceivedFrom += e => events.TrySetResult(e);
        Assert.True(receiver.Start(TestSupport.LoopbackOptions()));
        using var sender = new ArtNetSender(IPAddress.Loopback);
        var timecode = new Timecode(1, 2, 3, 4, TimecodeType.Ebu25);
        sender.SendTimeCode(TestSupport.Loopback(receiver.LocalEndPoint!.Port), timecode);

        var received = await events.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var sample = await samples.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(timecode, received.Packet.ToTimecode());
        Assert.True(received.Sender.IsLoopback);
        Assert.Equal(timecode, sample.Timecode);
        Assert.Equal(ArtNetReceiver.SourceProtocol, sample.Source.Protocol);
        Assert.Equal(received.Sender.ToString(), sample.Source.Endpoint);
        Assert.Equal(sample, hub.LastTimecodeSample);
        Assert.Equal(sample.Source, hub.LastTimecodeSource);
    }
}
