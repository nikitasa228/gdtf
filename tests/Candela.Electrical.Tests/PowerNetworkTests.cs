using System.Numerics;
using Candela.Core.Diagnostics;
using Candela.Core.SceneGraph;

namespace Candela.Electrical.Tests;

public class PowerNetworkTests
{
    private static List<Entity> Aecos(SceneKit kit, int count, int firstId = 1) =>
        Enumerable.Range(0, count)
            .Select(i => kit.Fixture(TestData.Aeco20, $"AECO {i + 1}", firstId + i, new Vector3(i * 2f, 0, 6)))
            .ToList();

    [Fact]
    public void Three_high_power_fixtures_linked_from_one_16A_outlet_overload_it()
    {
        var kit = new SceneKit();
        var distro = kit.Source("Distro SL", new Vector3(0, -5, 0), outlets: SceneKit.Outlet("Outlet 1", "powerCON TRUE1", 16f));
        var fixtures = Aecos(kit, 3);
        var cables = kit.Chain("powercon-true1", "P", (distro, "Outlet 1"), fixtures, "Power In", "Power Out");

        var s = kit.Solve();
        // 3 × 1580 W at cos φ 0.95 on 230 V = 21.7 A
        var overload = s.Issues.Single(IssueCodes.PowerBreakerOverload);
        Assert.Equal(IssueSeverity.Error, overload.Severity);
        Assert.Contains("'Outlet 1' of 'Distro SL'", overload.Message);
        Assert.Contains("4740 W", overload.Message);
        Assert.Contains("21.7 A", overload.Message);
        Assert.Contains("16.0 A breaker", overload.Message);
        Assert.Contains("136 %", overload.Message);
        Assert.Contains("#1 'AECO 1'", overload.Message);
        Assert.Contains("#3 'AECO 3'", overload.Message);
        Assert.Equal(new[] { distro.Id }.Concat(fixtures.Select(f => f.Id)), overload.Entities);

        Assert.Contains("21.7 A", s.Issues.Single(IssueCodes.PowerInletOverload).Message); // the first fixture's TRUE1 inlet
        var cableIssue = s.Issues.Single(IssueCodes.PowerCableOverload);
        Assert.Contains("'P1'", cableIssue.Message);
        Assert.Contains(cables[0].Id, cableIssue.Entities);
        s.Issues.None(IssueCodes.PowerLinkOverload); // the first link carries 2 × 1580 W = 14.5 A

        var circuit = s.PowerCircuits.Single(c => c.SourceEntityId == distro.Id);
        Assert.Equal(4740, circuit.LoadW, 6);
        Assert.Equal(4740 / 0.95 / 230, circuit.CurrentA, 6);
        Assert.True(circuit.IsOverloaded);
        Assert.Equal(fixtures.Select(f => f.Id), circuit.Feeds.Select(f => f.DeviceEntityId));

        var feed = s.PowerFeedOf(fixtures[0].Id)!;
        Assert.Equal((1580.0, 4740.0, 0), (feed.OwnW, feed.TotalW, feed.Depth));
        Assert.Equal(fixtures[1].Id, s.PowerFeedOf(fixtures[2].Id)!.UpstreamEntityId);
        Assert.Equal(16f, s.FindCable(cables[0].Id)!.Type!.RatedCurrentA);
        Assert.Equal(4740 / 0.95 / 230, s.FindCable(cables[0].Id)!.PeakCurrentA!.Value, 6);

        var row = PowerReport.Build(s).Rows.Single();
        Assert.True(row.Overloaded);
        Assert.Equal(135.6, row.PercentOfBreaker!.Value, 1);
        Assert.Equal(3, row.Devices.Count);
        Assert.Empty(s.UnpoweredDevices);
    }

    [Fact]
    public void Two_fixtures_on_the_same_outlet_are_fine_and_link_limits_are_checked()
    {
        var kit = new SceneKit();
        var distro = kit.Source("Distro", new Vector3(0, -5, 0), outlets: [SceneKit.Outlet("A"), SceneKit.Outlet("B", "powerCON TRUE1", 32f)]);
        var two = Aecos(kit, 2);
        kit.Chain("powercon-true1", "P", (distro, "A"), two, "Power In", "Power Out");
        var four = Aecos(kit, 4, firstId: 11);
        kit.Chain("powercon-true1", "Q", (distro, "B"), four, "Power In", "Power Out");

        var s = kit.Solve();
        var a = s.PowerCircuits.Single(c => c.Outlet.Name == "A");
        Assert.Equal(3160, a.LoadW, 6);
        Assert.False(a.IsOverloaded);

        // Outlet B (32 A) holds 4 × 1580 W = 28.9 A, but the first fixture's TRUE1 link passes 3 × 1580 W = 21.7 A.
        Assert.False(s.PowerCircuits.Single(c => c.Outlet.Name == "B").IsOverloaded);
        var link = s.Issues.Single(IssueCodes.PowerLinkOverload);
        Assert.Contains("'Power Out'", link.Message);
        Assert.Contains("#11 'AECO 1'", link.Message);
        Assert.Contains("21.7 A", link.Message);
        Assert.Contains("3 linked devices", link.Message);
        Assert.Contains("16.0 A", link.Message);
        s.Issues.None(IssueCodes.PowerBreakerOverload);
    }

    [Fact]
    public void Dimmer_pack_loads_are_counted_once()
    {
        var kit = new SceneKit();
        var wall = kit.Source("Wall", new Vector3(0, -5, 0), outlets: SceneKit.Outlet("Socket", "Schuko", 16f));
        var dimmer = kit.Fixture(TestData.DimmerPack4, "Dimmer Pack", 50, new Vector3(0, -4, 0));
        kit.Cable("schuko", "Feed", (wall, "Socket"), (dimmer, "Power IN"));
        var pars = Enumerable.Range(0, 4).Select(i => kit.Fixture(TestData.CobCannon, $"PAR {i + 1}", 61 + i, new Vector3(i, 0, 6))).ToList();
        for (int i = 0; i < 4; i++) kit.Cable("schuko", $"Ch{i + 1}", (dimmer, $"Dimmer {i + 1}"), (pars[i], "PowerCon True1-In"));

        var s = kit.Solve();
        var circuit = s.PowerCircuits.Single(c => c.SourceEntityId == wall.Id);
        // 10 W own draw of the pack + 4 × 175 W — not maxInputPowerW (4400 W), not the loads twice
        Assert.Equal(10 + 4 * 175, circuit.LoadW, 6);
        Assert.Equal(5, circuit.Feeds.Count);
        var packFeed = s.PowerFeedOf(dimmer.Id)!;
        Assert.Equal((10.0, 710.0), (packFeed.OwnW, packFeed.TotalW));
        Assert.All(pars, p =>
        {
            var f = s.PowerFeedOf(p.Id)!;
            Assert.True(f.Dimmed);
            Assert.Equal(dimmer.Id, f.UpstreamEntityId);
            Assert.Equal(175, f.TotalW, 6);
        });
        Assert.Equal(4, s.Issues.WithCode(IssueCodes.PowerDimmedElectronicLoad).Count); // LED PARs on dimmed channels
        Assert.Equal(4, s.Issues.WithCode(IssueCodes.CableAdapterNeeded).Count); // Schuko cables into TRUE1 inlets
        s.Issues.None(IssueCodes.PowerChannelOverload);
        Assert.Equal(710, PowerReport.Build(s).TotalW, 6);
    }

    [Fact]
    public void Dimmer_channel_limit_is_checked()
    {
        var kit = new SceneKit();
        var wall = kit.Source("Wall", new Vector3(0, -5, 0), outlets: SceneKit.Outlet("Socket", "Schuko", 16f));
        var dimmer = kit.Fixture(TestData.DimmerPack4, "Dimmer Pack", 50, new Vector3(0, -4, 0));
        kit.Cable("schuko", "Feed", (wall, "Socket"), (dimmer, "Power IN"));
        var aecos = Aecos(kit, 2);
        kit.Chain("powercon-true1", "C", (dimmer, "Dimmer 1"), aecos, "Power In", "Power Out");

        var s = kit.Solve();
        var issue = s.Issues.Single(IssueCodes.PowerChannelOverload);
        Assert.Contains("'Dimmer 1'", issue.Message);
        Assert.Contains("3160 W", issue.Message);
        Assert.Contains("14.5 A", issue.Message);
        Assert.Contains("10.0 A", issue.Message);
        s.Issues.None(IssueCodes.PowerDistributionOverload);
        Assert.Equal(3170, s.PowerCircuits.Single().LoadW, 6);
    }

    [Fact]
    public void Voltage_outside_the_supported_networks_is_an_error()
    {
        var kit = new SceneKit();
        var us = kit.Source("US mains", new Vector3(0, -5, 0), voltage: 120f, frequency: 60f, outlets: SceneKit.Outlet("Edison", "powerCON", 20f));
        var eu = kit.Source("EU mains", new Vector3(5, -5, 0), outlets: SceneKit.Outlet("Socket", "powerCON", 16f));
        var blade = kit.Fixture(TestData.BmflBlade, "Blade", 31, new Vector3(0, 0, 6));
        var fine = kit.Fixture(TestData.BmflBlade, "Blade EU", 32, new Vector3(5, 0, 6));
        kit.Cable("powercon", "US", (us, "Edison"), (blade, "Power IN"));
        kit.Cable("powercon", "EU", (eu, "Socket"), (fine, "Power IN"));

        var s = kit.Solve();
        var issue = s.Issues.Single(IssueCodes.PowerVoltageMismatch);
        Assert.Equal(IssueSeverity.Error, issue.Severity);
        Assert.Contains("#31 'Blade'", issue.Message);
        Assert.Contains("120 V 60 Hz", issue.Message);
        Assert.Contains("220/230 V - 50 Hz", issue.Message);
        Assert.Contains(blade.Id, issue.Entities);
        Assert.DoesNotContain(fine.Id, issue.Entities);
        s.Issues.None(IssueCodes.CableAdapterNeeded);
    }

    [Fact]
    public void Fixture_without_a_power_cable_is_unpowered()
    {
        var kit = new SceneKit();
        var lonely = kit.Fixture(TestData.Naja, "Lonely", 5, new Vector3(0, 0, 6));
        var linked = kit.Fixture(TestData.Naja, "Linked", 6, new Vector3(2, 0, 6));
        kit.Cable("powercon-true1-top", "Dead", (lonely, "Power OUT"), (linked, "Power IN"));

        var s = kit.Solve();
        var issues = s.Issues.WithCode(IssueCodes.PowerUnpowered);
        Assert.Equal(2, issues.Count);
        Assert.Contains(issues, i => i.Message.Contains("#5 'Lonely'") && i.Message.Contains("700 W") && i.Message.Contains("nothing is plugged into 'Power IN'"));
        Assert.Contains(issues, i => i.Message.Contains("#6 'Linked'") && i.Message.Contains("does not lead back to a power source"));
        Assert.Equal([lonely.Id, linked.Id], s.UnpoweredDevices);
        Assert.Equal([lonely.Id, linked.Id], PowerReport.Build(s).UnpoweredDevices);
    }

    [Fact]
    public void Generator_feeds_a_three_phase_distro_and_imbalance_is_noted()
    {
        var kit = new SceneKit();
        var gen = kit.Source("Generator", new Vector3(-10, -10, 0), phases: 3, outlets: SceneKit.Outlet("CEE 63", "CEE 63A 3P", 63f));
        var distro = kit.Source("Distro", new Vector3(0, -5, 0), phases: 3, outlets:
        [
            SceneKit.Outlet("L1", "powerCON TRUE1", 16f, 1), SceneKit.Outlet("L2", "powerCON TRUE1", 16f, 2), SceneKit.Outlet("L3", "powerCON TRUE1", 16f, 3),
        ]);
        kit.Cable("cee63-3p", "Main", (gen, "CEE 63"), (distro, PortResolver.MainsInPortName));
        var l1 = Aecos(kit, 2);
        kit.Chain("powercon-true1", "A", (distro, "L1"), l1, "Power In", "Power Out");
        var l2 = Aecos(kit, 1, firstId: 10);
        kit.Cable("powercon-true1", "B1", (distro, "L2"), (l2[0], "Power In"));

        var s = kit.Solve();
        var genLoad = s.PowerSources.Single(p => p.SourceEntityId == gen.Id);
        Assert.Equal(4740, genLoad.TotalW, 6);
        double perFixtureA = 1580 / 0.95 / 230;
        Assert.Equal(2 * perFixtureA, genLoad.PhaseCurrentsA[0], 6);
        Assert.Equal(perFixtureA, genLoad.PhaseCurrentsA[1], 6);
        Assert.Equal(0, genLoad.PhaseCurrentsA[2], 6);
        Assert.Equal(0, s.PowerCircuits.Single(c => c.SourceEntityId == gen.Id).Phase); // three-phase outlet

        var distroLoad = s.PowerSources.Single(p => p.SourceEntityId == distro.Id);
        Assert.True(distroLoad.IsFed && distroLoad.IsPowered);
        Assert.Equal(gen.Id, s.PowerFeedOf(distro.Id)!.SourceEntityId);
        Assert.Equal(distro.Id, s.PowerFeedOf(l1[1].Id)!.SourceEntityId);
        Assert.Equal(2, s.PowerFeedOf(l2[0].Id)!.Phase);

        var notes = s.Issues.WithCode(IssueCodes.PowerPhaseImbalance);
        Assert.Equal(2, notes.Count); // the generator and the distro
        Assert.All(notes, n => Assert.Equal(IssueSeverity.Note, n.Severity));
        Assert.Contains(notes, n => n.Message.Contains("'Distro'") && n.Message.Contains("L1 14.5 A") && n.Message.Contains("L3 0.0 A"));
        Assert.Equal(4740, PowerReport.Build(s).TotalW, 6); // the distro is inside the generator's circuit
        Assert.Empty(s.UnpoweredDevices);
    }

    [Fact]
    public void Socapex_multicore_spreads_circuits_over_phases()
    {
        var kit = new SceneKit();
        var rack = kit.Source("Rack", new Vector3(0, -5, 0), phases: 3, outlets: SceneKit.Outlet("Soca 1", "Socapex", 16f, 1));
        var three = Aecos(kit, 3);
        var soca = kit.Cable("socapex", "S1", (rack, "Soca 1"), (three[0], "Power In"), (three[1], "Power In"), (three[2], "Power In"));

        var s = kit.Solve();
        s.Issues.None(IssueCodes.DataYSplit);
        s.Issues.None(IssueCodes.CableAdapterNeeded); // breakout tails into TRUE1
        var circuits = s.PowerCircuits.Where(c => c.SourceEntityId == rack.Id).ToList();
        Assert.Equal(6, circuits.Count);
        Assert.Equal([1, 2, 3, 1, 2, 3], circuits.Select(c => c.Phase));
        Assert.Equal([1580.0, 1580, 1580, 0, 0, 0], circuits.Select(c => Math.Round(c.LoadW)));
        Assert.Equal([1, 2, 3], three.Select(f => s.PowerFeedOf(f.Id)!.Circuit));
        s.Issues.None(IssueCodes.PowerPhaseImbalance);
        Assert.Equal(1580 / 0.95 / 230, s.FindCable(soca.Id)!.PeakCurrentA!.Value, 6);

        var kit2 = new SceneKit();
        var rack2 = kit2.Source("Rack", new Vector3(0, -5, 0), phases: 3, outlets: SceneKit.Outlet("Soca 1", "Socapex", 16f, 1));
        var seven = Aecos(kit2, 7);
        kit2.Cable("socapex", "S1", (rack2, "Soca 1"), [.. seven.Select(f => (f, "Power In"))]);
        Assert.Contains("7 tails but only 6 circuits", kit2.Solve().Issues.Single(IssueCodes.PowerMulticoreCircuits).Message);
    }

    [Fact]
    public void Back_feeding_cables_and_double_plugs_are_errors()
    {
        var kit = new SceneKit();
        var distro = kit.Source("Distro", new Vector3(0, -5, 0), outlets: [SceneKit.Outlet("A"), SceneKit.Outlet("B")]);
        var f = Aecos(kit, 3);
        kit.Cable("powercon-true1", "P1", (distro, "A"), (f[0], "Power In"));
        kit.Cable("powercon-true1", "P2", (distro, "B"), (f[1], "Power In"));
        kit.Cable("powercon-true1", "Back", (f[0], "Power Out"), (f[1], "Power Out"));
        kit.Cable("powercon-true1", "Twice", (distro, "A"), (f[2], "Power In"));

        var s = kit.Solve();
        Assert.Contains("'Back'", s.Issues.Single(IssueCodes.PowerBackfeed).Message);
        var occupied = s.Issues.Single(IssueCodes.CablePortOccupied);
        Assert.Contains("'A'", occupied.Message);
        Assert.Contains("'P1'", occupied.Message);
        Assert.Contains("'Twice'", occupied.Message);
        Assert.Null(s.PowerFeedOf(f[2].Id));
        Assert.NotNull(s.PowerFeedOf(f[0].Id));
        Assert.Equal("P1", s.CableAt(distro.Id, "a")!.Value.Cable.DisplayLabel);
    }

    [Fact]
    public void Dc_outputs_feed_dc_voltage_and_mains_fixtures_reject_it()
    {
        var profile = TestData.Profile(TestData.PowerPort2000);
        var output = profile.Ports.First(p => p.Role == PortRole.PowerDistribution);
        var kit = new SceneKit();
        var wall = kit.Source("Wall", new Vector3(0, -5, 0), outlets: SceneKit.Outlet("Socket"));
        var port = kit.Fixture(TestData.PowerPort2000, "PowerPort", 70, new Vector3(0, -4, 0));
        var inlet = profile.Ports.First(p => p.Role == PortRole.PowerIn).Name;
        kit.Cable("powercon-true1", "Feed", (wall, "Socket"), (port, inlet));
        var mains = kit.Fixture(TestData.Aeco20, "Mains fixture", 71, new Vector3(0, 0, 6));
        kit.Cable("powercon-true1", "Out", (port, output.Name), (mains, "Power In"));

        var s = kit.Solve();
        var feed = s.PowerFeedOf(mains.Id)!;
        Assert.True(feed.IsDc);
        Assert.Equal(48f, feed.VoltageV);
        Assert.Contains("48 V DC", s.Issues.Single(IssueCodes.PowerVoltageMismatch).Message);
        // Mains side: own 24 W + 1580 W × conversion losses (2240 − 24) / 1920
        double expected = 24 + 1580 * ((2240.0 - 24) / 1920);
        Assert.Equal(expected, s.PowerCircuits.Single(c => c.SourceEntityId == wall.Id).LoadW, 6);
    }
}
