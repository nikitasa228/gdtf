using System.Net;
using Candela.Core.Settings;
using Candela.Dmx.Input;
using Candela.Protocols.ArtNet;
using Candela.Protocols.Networking;
using Candela.Protocols.Sacn;

namespace Candela.Protocols.Tests;

public class ProtocolManagerTests
{
    [Fact]
    public void Manager_holds_all_protocols_and_applies_a_selection()
    {
        var hub = new DmxInputHub();
        using var manager = new ProtocolManager(hub, name => new FakeEnttecTransport(name));
        Assert.Equal(ProtocolRegistry.All.Select(d => d.Id), manager.Protocols.Select(p => p.Id));
        var changes = new List<string>();
        manager.StatusChanged += (_, e) =>
        {
            lock (changes) changes.Add(e.Protocol.Id);
        };

        var options = TestSupport.LoopbackOptions() with { EnttecSerialPort = "fake0" };
        var statuses = manager.Apply(new[] { ProtocolIds.ArtNet, ProtocolIds.Sacn, ProtocolIds.Osc, ProtocolIds.EnttecDmxUsbPro, ProtocolIds.Ndi }, options);
        Assert.Equal(15, statuses.Count);
        Assert.Equal(new[] { ProtocolIds.ArtNet, ProtocolIds.EnttecDmxUsbPro, ProtocolIds.Osc, ProtocolIds.Sacn }, manager.RunningProtocolIds);
        Assert.Equal(ProtocolState.Unavailable, statuses.Single(s => s.ProtocolId == ProtocolIds.Ndi).State);
        Assert.Equal(ProtocolState.Stopped, statuses.Single(s => s.ProtocolId == ProtocolIds.Citp).State);
        Assert.Contains(ProtocolIds.ArtNet, changes);

        // Unchanged options keep running protocols untouched (same socket, same counters).
        var artNet = manager.Get<ArtNetReceiver>()!;
        var endPoint = artNet.LocalEndPoint;
        using (var sender = new ArtNetSender(IPAddress.Loopback))
            sender.SendDmx(TestSupport.Loopback(endPoint!.Port), 0, new byte[] { 1, 2 });
        Assert.True(TestSupport.WaitUntil(() => artNet.Status.Packets == 1));
        manager.Apply(new[] { ProtocolIds.ArtNet, ProtocolIds.Sacn }, options);
        Assert.Equal(endPoint, artNet.LocalEndPoint);
        Assert.Equal(1, artNet.Status.Packets);
        Assert.Equal(new[] { ProtocolIds.ArtNet, ProtocolIds.Sacn }, manager.RunningProtocolIds);

        // Changed options restart.
        manager.Apply(new[] { ProtocolIds.ArtNet }, options with { ArtNetUniverseOffset = 0 });
        Assert.Equal(0, artNet.Status.Packets);
        Assert.Equal(0, artNet.Options!.ArtNetUniverseOffset);

        manager.StopAll();
        Assert.Empty(manager.RunningProtocolIds);
    }

    [Fact]
    public void Two_protocols_on_one_port_are_refused()
    {
        using var manager = new ProtocolManager(new DmxInputHub());
        var options = TestSupport.LoopbackOptions() with { OscPort = 39123, EosOscPort = 39123 };
        var statuses = manager.Apply(new[] { ProtocolIds.Osc, ProtocolIds.EtcEos }, options);
        // ETC Eos comes first in display order, so it gets the port and OSC is refused.
        var eos = statuses.Single(s => s.ProtocolId == ProtocolIds.EtcEos);
        var osc = statuses.Single(s => s.ProtocolId == ProtocolIds.Osc);
        Assert.Equal(ProtocolState.Error, osc.State);
        Assert.Contains("39123", osc.LastError);
        Assert.Contains("ETC Eos", osc.LastError);
        Assert.Contains(eos.State, new[] { ProtocolState.Running, ProtocolState.Error });
    }

    [Fact]
    public void Start_and_stop_single_protocols_and_unknown_ids()
    {
        using var manager = new ProtocolManager(new DmxInputHub());
        manager.Apply(Array.Empty<string>(), TestSupport.LoopbackOptions());
        Assert.True(manager.Start(ProtocolIds.Sacn));
        Assert.True(manager.Find(ProtocolIds.Sacn)!.IsRunning);
        Assert.NotNull(manager.Get<SacnReceiver>()!.LocalEndPoint);
        manager.Stop(ProtocolIds.Sacn);
        Assert.False(manager.Find(ProtocolIds.Sacn)!.IsRunning);
        Assert.False(manager.Start("unknown"));
        manager.Stop("unknown");
        Assert.Null(manager.Find("unknown"));
    }

    [Fact]
    public void Disposed_manager_releases_ports()
    {
        var manager = new ProtocolManager(new DmxInputHub());
        manager.Apply(new[] { ProtocolIds.ArtNet }, TestSupport.LoopbackOptions());
        var artNet = manager.Get<ArtNetReceiver>()!;
        Assert.NotNull(artNet.LocalEndPoint);
        manager.Dispose();
        Assert.Null(artNet.LocalEndPoint);
        Assert.Throws<ObjectDisposedException>(() => manager.Apply(new[] { ProtocolIds.ArtNet }, TestSupport.LoopbackOptions()));
    }

    [Fact]
    public void Network_interfaces_enumerate_and_resolve()
    {
        var interfaces = ProtocolManager.GetNetworkInterfaces();
        Assert.NotNull(interfaces);
        Assert.All(interfaces, i => Assert.Equal(System.Net.Sockets.AddressFamily.InterNetwork, i.Address.AddressFamily));
        Assert.True(NetworkInterfaceCatalog.TryResolve("", interfaces, out var any));
        Assert.Null(any);
        Assert.True(NetworkInterfaceCatalog.TryResolve("0.0.0.0", interfaces, out _));
        Assert.True(NetworkInterfaceCatalog.TryResolve("127.0.0.1", interfaces, out var loopback));
        Assert.True(loopback!.IsLoopback);
        Assert.Equal(IPAddress.Parse("127.255.255.255"), loopback.Broadcast);
        Assert.False(NetworkInterfaceCatalog.TryResolve("adapter-that-does-not-exist", interfaces, out _));
        foreach (var i in interfaces)
        {
            Assert.True(NetworkInterfaceCatalog.TryResolve(i.Address.ToString(), interfaces, out var byIp));
            Assert.Equal(i.Address, byIp!.Address);
            Assert.True(i.Contains(i.AddressValue));
        }
        Assert.NotNull(ProtocolManager.GetSerialPorts());
    }

    [Fact]
    public void Options_come_from_app_settings()
    {
        var options = ProtocolOptions.FromSettings(new AppSettings { NetworkInterface = "eth0", ArtNetUniverseOffset = 0 });
        Assert.Equal("eth0", options.NetworkInterface);
        Assert.Equal(0, options.ArtNetUniverseOffset);
        Assert.Equal(6454, options.ArtNetPort);
        Assert.Equal(5568, options.SacnPort);
        Assert.Equal(56565, options.PsnPort);
        Assert.Equal("236.10.10.10", options.PsnMulticastGroup);
        Assert.Equal(new[] { "/dmx/{universe}/{channel}", "/dmx/{universe}" }, options.OscPatterns);
    }

    [Fact]
    public void Invalid_port_is_an_error_status()
    {
        using var receiver = new ArtNetReceiver(new DmxInputHub());
        Assert.False(receiver.Start(TestSupport.LoopbackOptions() with { ArtNetPort = 70000 }));
        Assert.Contains("70000", receiver.Status.LastError);
    }
}
