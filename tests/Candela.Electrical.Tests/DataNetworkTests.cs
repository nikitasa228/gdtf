using System.Numerics;
using Candela.Core.Diagnostics;
using Candela.Core.Math;
using Candela.Core.SceneGraph;

namespace Candela.Electrical.Tests;

public class DataNetworkTests
{
    private static List<Entity> Heads(SceneKit kit, int count, int universe = 1, int firstId = 101, float spacing = 1.5f, string spec = TestData.Naja) =>
        Enumerable.Range(0, count)
            .Select(i => kit.Fixture(spec, $"Head {i + 1}", firstId + i, new Vector3(i * spacing, 0, 6), universe, 1 + i * 40))
            .ToList();

    [Fact]
    public void Console_output_daisy_chained_to_ten_moving_heads_gives_chain_order_universe_and_console_drive()
    {
        var kit = new SceneKit();
        var desk = kit.Console("Desk", new Vector3(0, -12, 0), SceneKit.DmxOutput("DMX A", 1), SceneKit.DmxOutput("DMX B", 2));
        var heads = Heads(kit, 10);
        var order = heads.AsEnumerable().Reverse().ToList(); // cabling order differs from scene order
        var cables = kit.Chain("dmx-xlr5", "D", (desk, "DMX A"), order, "XLR IN", "XLR OUT");

        var s = kit.Solve();
        var line = s.DataLines.Single(l => l.Output.Name == "DMX A");
        Assert.Equal(order.Select(h => h.Id), line.Feeds.Select(f => f.DeviceEntityId));
        Assert.Equal(Enumerable.Range(1, 10), line.Feeds.Select(f => f.ChainIndex));
        Assert.All(line.Feeds, f => Assert.Equal(1, f.FirstUniverse));
        Assert.All(line.Feeds, f => Assert.Equal("XLR IN", f.InputPort));
        Assert.Null(line.Feeds[0].UpstreamEntityId);
        Assert.Equal("DMX A", line.Feeds[0].UpstreamPort);
        for (int i = 1; i < 10; i++)
        {
            Assert.Equal(order[i - 1].Id, line.Feeds[i].UpstreamEntityId);
            Assert.Equal(cables.Take(i + 1).Select(c => c.Id), line.Feeds[i].CablePath);
            Assert.True(line.Feeds[i].RunLengthM > line.Feeds[i - 1].RunLengthM);
        }
        Assert.Empty(s.DataLines.Single(l => l.Output.Name == "DMX B").Feeds);

        Assert.All(heads, h => Assert.True(s.IsConsoleDriven(h.Id)));
        Assert.Equal(10, s.ConsoleDrivenFixtures.Count);
        Assert.False(s.IsConsoleDriven(desk.Id));
        Assert.DoesNotContain(s.Issues, i => i.Category == IssueCategory.Data && i.Severity > IssueSeverity.Note);

        var report = DataReport.Build(s);
        var chain = report.Outputs.Single(o => o.Port == "DMX A").Chain;
        Assert.Equal(order.Select(h => h.Name), chain.Select(c => c.Name));
        Assert.All(chain, c => Assert.True(c.PatchMatches));
        Assert.Equal(110, chain[0].FixtureId);
        Assert.Equal("D1", chain[0].CableLabel);
        Assert.Empty(report.PatchedFixturesWithoutData);

        var view = CableLinkView.Build(s, cables[4].Id)!;
        Assert.Equal(11, view.Devices.Count);
        Assert.Equal(desk.Id, view.Devices[0].EntityId);
        Assert.Equal(order.Select(h => h.Id), view.Devices.Skip(1).Select(d => d.EntityId));
        Assert.Equal(cables[4].Id, view.Connections.Single(c => c.IsSelected).CableEntityId);
        Assert.Equal([order[3].Id, order[4].Id], view.Devices.Where(d => d.OnSelectedCable).Select(d => d.EntityId));
        Assert.Contains("universe 1", view.SourceDescription);
    }

    [Fact]
    public void Fixture_patched_to_another_universe_than_its_chain_is_an_error()
    {
        var kit = new SceneKit();
        var desk = kit.Console("Desk", new Vector3(0, -12, 0), SceneKit.DmxOutput("DMX A", 1));
        var heads = Heads(kit, 3);
        var wrong = kit.Fixture(TestData.Naja, "Head 4", 104, new Vector3(6, 0, 6), universe: 2, address: 1);
        kit.Chain("dmx-xlr5", "D", (desk, "DMX A"), [.. heads, wrong], "XLR IN", "XLR OUT");

        var s = kit.Solve();
        var issue = s.Issues.Single(IssueCodes.DataUniverseMismatch);
        Assert.Equal(IssueSeverity.Error, issue.Severity);
        Assert.Contains("#104 'Head 4'", issue.Message);
        Assert.Contains("universe 2", issue.Message);
        Assert.Contains("carries universe 1", issue.Message);
        Assert.Contains(wrong.Id, issue.Entities);
        Assert.True(s.IsConsoleDriven(wrong.Id)); // still cabled to the console
        Assert.False(DataReport.Build(s).Outputs[0].Chain[3].PatchMatches);
    }

    [Fact]
    public void Y_split_of_a_dmx_cable_is_a_warning_but_both_branches_get_data()
    {
        var kit = new SceneKit();
        var desk = kit.Console("Desk", new Vector3(0, -12, 0), SceneKit.DmxOutput("DMX A", 1));
        var heads = Heads(kit, 2);
        var y = kit.Cable("dmx-xlr5", "Y", (desk, "DMX A"), (heads[0], "XLR IN"), (heads[1], "XLR IN"));

        var s = kit.Solve();
        var issue = s.Issues.Single(IssueCodes.DataYSplit);
        Assert.Equal(IssueSeverity.Warning, issue.Severity);
        Assert.Contains("#101 'Head 1'", issue.Message);
        Assert.Contains("#102 'Head 2'", issue.Message);
        Assert.Contains(y.Id, issue.Entities);
        Assert.All(heads, h => Assert.True(s.IsConsoleDriven(h.Id)));
        Assert.All(s.DataLines[0].Feeds, f => Assert.Equal(1, f.ChainIndex));
    }

    [Fact]
    public void Patched_fixture_without_data_is_a_warning_and_unpatched_one_is_not()
    {
        var kit = new SceneKit();
        kit.Console("Desk", new Vector3(0, -12, 0), SceneKit.DmxOutput("DMX A", 1));
        var patched = kit.Fixture(TestData.Naja, "Lonely", 7, new Vector3(0, 0, 6), universe: 3, address: 101);
        kit.Fixture(TestData.Naja, "Spare", 8, new Vector3(2, 0, 6));
        var dangling = kit.Fixture(TestData.Naja, "Dangling", 9, new Vector3(4, 0, 6), universe: 3, address: 201);
        kit.Cable("dmx-xlr5", "Loose", (dangling, "XLR IN"), (patched, "XLR IN")); // two inputs, no source

        var s = kit.Solve();
        var issues = s.Issues.WithCode(IssueCodes.DataNoSignal);
        Assert.Equal(2, issues.Count);
        Assert.Contains(issues, i => i.Message.Contains("#7 'Lonely'") && i.Message.Contains("3.101"));
        Assert.Contains(issues, i => i.Message.Contains("#9 'Dangling'") && i.Message.Contains("does not lead back to a console"));
        Assert.Empty(s.ConsoleDrivenFixtures);
        Assert.Equal(2, DataReport.Build(s).PatchedFixturesWithoutData.Count);
    }

    [Fact]
    public void Without_consoles_or_data_cables_patched_fixtures_are_not_reported_as_receiving_no_data()
    {
        // an imported MVR: 40 patched fixtures, no console, no cables — they follow live universes or their manual values
        var kit = new SceneKit();
        var heads = Heads(kit, 40);
        var s = kit.Solve();
        Assert.Empty(s.Issues.WithCode(IssueCodes.DataNoSignal));
        Assert.Empty(s.ConsoleDrivenFixtures);
        Assert.Equal(40, DataReport.Build(s).PatchedFixturesWithoutData.Count); // the report still lists them

        // a power cable is not a data network either
        var fresh = new SceneKit();
        var two = Heads(fresh, 2);
        fresh.Cable("powercon-true1", "P", (two[0], "Power OUT"), (two[1], "Power IN"));
        Assert.Empty(fresh.Solve().Issues.WithCode(IssueCodes.DataNoSignal));

        // the first data cable makes it one: the fixtures it does not reach from a console are reported
        var cabled = new SceneKit();
        var three = Heads(cabled, 3);
        cabled.Cable("dmx-xlr5", "Loose", (three[0], "XLR OUT"), (three[1], "XLR IN"));
        Assert.Equal(3, cabled.Solve().Issues.WithCode(IssueCodes.DataNoSignal).Count);
    }

    [Fact]
    public void Chains_longer_than_32_devices_and_300_m_are_warnings()
    {
        var kit = new SceneKit();
        var desk = kit.Console("Desk", new Vector3(0, -12, 0), SceneKit.DmxOutput("DMX A", 1), SceneKit.DmxOutput("DMX B", 2));
        var many = Heads(kit, 34, universe: 1);
        kit.Chain("dmx-xlr5", "A", (desk, "DMX A"), many, "XLR IN", "XLR OUT");
        var far = Enumerable.Range(0, 3).Select(i => kit.Fixture(TestData.Naja, $"Far {i + 1}", 201 + i, new Vector3(0, 150f * (i + 1), 6), 2, 1 + i * 40)).ToList();
        kit.Chain("dmx-xlr5", "B", (desk, "DMX B"), far, "XLR IN", "XLR OUT");

        var s = kit.Solve();
        var tooMany = s.Issues.Single(IssueCodes.DataChainTooLong);
        Assert.Contains("#133 'Head 33'", tooMany.Message);
        Assert.Contains("more than 32 devices", tooMany.Message);
        var tooLong = s.Issues.Single(IssueCodes.DataRunTooLong);
        Assert.Contains("'Far 2'", tooLong.Message);
        Assert.Contains("300.0 m", tooLong.Message);
        Assert.Equal(34, s.DataLines[0].Feeds.Count);
    }

    [Fact]
    public void Data_loops_and_connected_outputs_are_errors()
    {
        var kit = new SceneKit();
        var desk = kit.Console("Desk", new Vector3(0, -12, 0), SceneKit.DmxOutput("DMX A", 1), SceneKit.DmxOutput("DMX B", 2));
        var a = kit.Fixture(TestData.Cobra100, "A", 1, new Vector3(0, 0, 6), 1, 1);
        var b = kit.Fixture(TestData.Cobra100, "B", 2, new Vector3(2, 0, 6), 1, 21);
        kit.Cable("dmx-xlr5", "L1", (desk, "DMX A"), (a, "DMX 5Pin IN"));
        kit.Cable("dmx-xlr5", "L2", (a, "DMX 5Pin OUT"), (b, "DMX 5Pin IN"));
        kit.Cable("dmx-xlr3", "L3", (b, "DMX 3Pin OUT"), (a, "DMX 3Pin IN")); // back into A

        var c = kit.Fixture(TestData.Naja, "C", 3, new Vector3(4, 0, 6), 2, 1);
        var d = kit.Fixture(TestData.Naja, "D", 4, new Vector3(6, 0, 6), 2, 41);
        kit.Cable("dmx-xlr5", "O1", (desk, "DMX B"), (c, "XLR IN"));
        kit.Cable("dmx-xlr5", "O2", (c, "XLR OUT"), (d, "XLR OUT"));

        var s = kit.Solve();
        var loop = s.Issues.Single(IssueCodes.DataLoop);
        Assert.Contains("#1 'A'", loop.Message);
        Assert.Contains("'L3'", loop.Message);
        var conflict = s.Issues.Single(IssueCodes.DataOutputConflict);
        Assert.Contains("#4 'D'", conflict.Message);
        Assert.Equal(IssueSeverity.Error, conflict.Severity);
        Assert.False(s.IsConsoleDriven(d.Id));
        Assert.Equal([a.Id, b.Id], s.DataLines[0].Feeds.Select(f => f.DeviceEntityId));
    }

    [Fact]
    public void Wrong_cables_and_adapters_are_reported_per_plug()
    {
        var kit = new SceneKit();
        var desk = kit.Console("Desk", new Vector3(0, -12, 0), SceneKit.DmxOutput("DMX A", 1));
        var head = kit.Fixture(TestData.Naja, "Head", 1, new Vector3(0, 0, 6), 1, 1);
        kit.Cable("dmx-xlr3", "D3", (desk, "DMX A"), (head, "XLR IN"));
        var other = kit.Fixture(TestData.Naja, "Other", 2, new Vector3(2, 0, 6), 1, 41);
        var wrong = kit.Cable("dmx-xlr5", "Wrong", (head, "XLR OUT"), (other, "Power IN"));

        var s = kit.Solve();
        var adapters = s.Issues.WithCode(IssueCodes.CableAdapterNeeded);
        Assert.Equal(2, adapters.Count); // console XLR5 output and the head's XLR5 input
        Assert.All(adapters, a => Assert.Contains("3-pin to 5-pin", a.Message));
        Assert.True(s.IsConsoleDriven(head.Id)); // an adapter works
        var error = s.Issues.Single(IssueCodes.CableWrongConnector);
        Assert.Equal(IssueSeverity.Error, error.Severity);
        Assert.Contains("'Power IN'", error.Message);
        Assert.Contains(wrong.Id, error.Entities);
        Assert.False(s.IsConsoleDriven(other.Id));
    }

    [Fact]
    public void Ethernet_outputs_carry_several_universes_through_switched_ports()
    {
        var kit = new SceneKit();
        var desk = kit.Console("Desk", new Vector3(0, -12, 0), SceneKit.EthernetOutput("Net 1", 1, 4));
        var good = kit.Fixture(TestData.Naja, "Good", 1, new Vector3(0, 0, 6), universe: 3, address: 1);
        var bad = kit.Fixture(TestData.Naja, "Bad", 2, new Vector3(2, 0, 6), universe: 9, address: 1);
        kit.Cable("ethernet", "E1", (desk, "Net 1"), (good, "ETH IN"));
        kit.Cable("ethernet", "E2", (good, "ETH OUT"), (bad, "ETH IN"));

        var s = kit.Solve();
        Assert.True(s.IsConsoleDriven(good.Id) && s.IsConsoleDriven(bad.Id));
        var feed = s.DataFeedOf(bad.Id)!;
        Assert.Equal((SignalKind.Ethernet, 1, 4, 2), (feed.Signal, feed.FirstUniverse, feed.UniverseCount, feed.ChainIndex));
        var mismatch = s.Issues.Single(IssueCodes.DataUniverseMismatch);
        Assert.Contains("#2 'Bad'", mismatch.Message);
        Assert.Contains("universes 1–4", mismatch.Message);
    }

    [Fact]
    public void Ethernet_to_dmx_converter_sends_the_universes_of_its_ethernet_range()
    {
        const string feederFile = "Martin_Professional@Exterior_AC-Feeder@20260602.gdtf";
        var feederProfile = TestData.Profile(feederFile);
        Assert.DoesNotContain(feederProfile.Ports, p => p.Origin == PortOrigin.Specs && p.Signal.CarriesData()); // GDTF data wiring is complete

        var kit = new SceneKit();
        var desk = kit.Console("Desk", new Vector3(0, -12, 0), SceneKit.EthernetOutput("Net 1", 5, 2));
        var feeder = kit.Fixture(feederFile, "Feeder", 90, new Vector3(0, -6, 0));
        var head = kit.Fixture(TestData.Naja, "Head", 1, new Vector3(0, 0, 6), universe: 5, address: 1);
        var stray = kit.Fixture(TestData.Naja, "Stray", 2, new Vector3(2, 0, 6), universe: 6, address: 1);
        kit.Cable("ethernet", "E", (desk, "Net 1"), (feeder, "Ethernet IN"));
        kit.Cable("dmx-xlr5", "D1", (feeder, "Data OUT"), (head, "XLR IN"));
        kit.Cable("dmx-xlr5", "D2", (head, "XLR OUT"), (stray, "XLR IN"));

        var s = kit.Solve();
        var feed = s.DataFeedOf(head.Id)!;
        Assert.Equal((SignalKind.Dmx512, 5, 1, 2), (feed.Signal, feed.FirstUniverse, feed.UniverseCount, feed.ChainIndex));
        Assert.Equal(feeder.Id, feed.UpstreamEntityId);
        Assert.True(s.IsConsoleDriven(head.Id));
        var mismatch = s.Issues.Single(IssueCodes.DataUniverseMismatch);
        Assert.Contains("#2 'Stray'", mismatch.Message);
        Assert.Contains("carries universe 5", mismatch.Message);
        Assert.Contains(s.Issues, i => i.Code == IssueCodes.CableAdapterNeeded && i.Message.Contains("Martin 8-pin"));
    }

    [Fact]
    public void A_fixture_fed_from_two_console_outputs_is_a_warning()
    {
        var kit = new SceneKit();
        var desk = kit.Console("Desk", new Vector3(0, -12, 0), SceneKit.DmxOutput("DMX A", 1), SceneKit.EthernetOutput("Net 1", 1, 2));
        var head = kit.Fixture(TestData.Naja, "Head", 1, new Vector3(0, 0, 6), 1, 1);
        kit.Cable("dmx-xlr5", "D", (desk, "DMX A"), (head, "XLR IN"));
        kit.Cable("ethernet", "E", (desk, "Net 1"), (head, "ETH IN"));

        var s = kit.Solve();
        Assert.Contains("2 console outputs", s.Issues.Single(IssueCodes.DataMultipleSources).Message);
        Assert.Equal(2, s.DataFeedsByDevice[head.Id].Count);
    }

    [Fact]
    public void GetOrSolve_reuses_the_solution_until_the_scene_changes()
    {
        var kit = new SceneKit();
        var desk = kit.Console("Desk", new Vector3(0, -12, 0), SceneKit.DmxOutput("DMX A", 1));
        var head = kit.Fixture(TestData.Naja, "Head", 1, new Vector3(0, 0, 6), 1, 1);
        kit.Cable("dmx-xlr5", "D", (desk, "DMX A"), (head, "XLR IN"));

        var first = kit.Solver.GetOrSolve(kit.Scene);
        Assert.Same(first, kit.Solver.GetOrSolve(kit.Scene));
        Assert.True(first.IsConsoleDriven(head.Id));

        head.Local = new Transform(new Vector3(0, 0, 7));
        var second = kit.Solver.GetOrSolve(kit.Scene);
        Assert.NotSame(first, second);
        Assert.Equal(kit.Scene.Version, second.SceneVersion);
    }
}
