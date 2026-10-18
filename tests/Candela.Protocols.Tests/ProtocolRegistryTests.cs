using Candela.Dmx.Input;
using Candela.Protocols.ArtNet;
using Candela.Protocols.Enttec;
using Candela.Protocols.Osc;
using Candela.Protocols.Psn;
using Candela.Protocols.Sacn;

namespace Candela.Protocols.Tests;

public class ProtocolRegistryTests
{
    [Fact]
    public void Registry_lists_exactly_the_15_required_protocols_in_order()
    {
        var expected = new[]
        {
            "Art-Net", "BlackTrax", "CITP", "Compulite VC", "ETC Eos", "EntTec DMX USB Pro", "Kinesys", "LasergraphDSP",
            "MA-Net2", "MA-Net3", "NDI", "OSC", "PangolinLD", "PosiStageNet", "sACN",
        };
        Assert.Equal(expected, ProtocolRegistry.All.Select(p => p.DisplayName));
        Assert.Equal(15, ProtocolRegistry.All.Select(p => p.Id).Distinct().Count());
        Assert.All(ProtocolRegistry.All, p =>
        {
            Assert.False(string.IsNullOrWhiteSpace(p.Description));
            Assert.False(string.IsNullOrWhiteSpace(p.SupportReason));
        });
    }

    [Fact]
    public void Support_levels_are_honest()
    {
        var level = ProtocolRegistry.All.ToDictionary(p => p.Id, p => p.SupportLevel);
        foreach (var id in new[] { ProtocolIds.ArtNet, ProtocolIds.Sacn, ProtocolIds.Osc, ProtocolIds.PosiStageNet, ProtocolIds.EnttecDmxUsbPro })
            Assert.Equal(ProtocolSupportLevel.Implemented, level[id]);
        Assert.Equal(ProtocolSupportLevel.Partial, level[ProtocolIds.EtcEos]);
        Assert.Contains("OSC", ProtocolRegistry.Get(ProtocolIds.EtcEos).SupportReason);
        foreach (var id in new[] { ProtocolIds.Ndi, ProtocolIds.MaNet2, ProtocolIds.MaNet3 })
            Assert.Equal(ProtocolSupportLevel.RequiresVendorSdk, level[id]);
        Assert.Contains("NDI SDK", ProtocolRegistry.Get(ProtocolIds.Ndi).SupportReason);
        Assert.Contains("Art-Net", ProtocolRegistry.Get(ProtocolIds.MaNet3).SupportReason);
        foreach (var id in new[] { ProtocolIds.Citp, ProtocolIds.BlackTrax, ProtocolIds.Kinesys, ProtocolIds.PangolinLd, ProtocolIds.LasergraphDsp, ProtocolIds.CompuliteVc })
            Assert.Equal(ProtocolSupportLevel.Planned, level[id]);
    }

    [Fact]
    public void CreateAll_builds_the_right_implementations_in_order()
    {
        var all = ProtocolRegistry.CreateAll(new DmxInputHub());
        try
        {
            Assert.Equal(ProtocolRegistry.All.Select(d => d.Id), all.Select(p => p.Id));
            Assert.IsType<ArtNetReceiver>(all[0]);
            Assert.IsType<EtcEosReceiver>(all[4]);
            Assert.IsType<EnttecDmxUsbPro>(all[5]);
            Assert.IsType<OscReceiver>(all[11]);
            Assert.IsType<PsnReceiver>(all[13]);
            Assert.IsType<SacnReceiver>(all[14]);
            Assert.IsType<PlannedProtocol>(all[10]);
        }
        finally
        {
            foreach (var p in all) p.Dispose();
        }
    }

    [Fact]
    public void Planned_protocols_report_unavailable_without_throwing()
    {
        using var ndi = ProtocolRegistry.Create(ProtocolIds.Ndi, new DmxInputHub());
        ProtocolStatus? raised = null;
        ndi.StatusChanged += (_, e) => raised = e.Status;
        Assert.False(ndi.Start(new ProtocolOptions()));
        Assert.False(ndi.IsRunning);
        Assert.Equal(ProtocolState.Unavailable, ndi.Status.State);
        Assert.Contains("NDI SDK", ndi.Status.Message);
        Assert.NotNull(raised);
        ndi.Stop();
        Assert.Equal(ProtocolState.Stopped, ndi.Status.State);
    }

    [Fact]
    public void Unknown_ids_are_reported()
    {
        Assert.Null(ProtocolRegistry.Find("dmx-over-carrier-pigeon"));
        Assert.Throws<KeyNotFoundException>(() => ProtocolRegistry.Get("nope"));
        Assert.NotNull(ProtocolRegistry.Find("ARTNET"));
    }
}
