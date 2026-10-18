using System.Numerics;
using Candela.Core.Diagnostics;
using Candela.Core.SceneGraph;
using Candela.Gdtf;

namespace Candela.Electrical.Tests;

/// <summary>Regression tests for defects found in review: DMX topology, port ratings from GDTF, mains voltage, rigging statics.</summary>
public class ReviewRegressionTests
{
    // Real library fixtures with GDTF quirks.
    private const string Cobra100 = TestData.Cobra100;                                       // DMX thru on XLR 5-pin and XLR 3-pin
    private const string Ignite740 = "BeamZ_Pro@Ignite_740_Wash@rev100_RELEASE.gdtf";        // TRUE1 thru with FuseCurrent = 240
    private const string DotBarBat1 = "BeamZ_Pro@DOT_BAR_Bat1_IP65@rev100_RELEASE.gdtf";     // "Power OUT 2" typed Input
    private const string BsStrobe = "BeamZ@BS_1200_Combi_Strobe@rev100_RELEASE.gdtf";        // "Power OUT 1" typed Input

    private static List<Entity> Row(SceneKit kit, string spec, int count, int universe = 1, float spacing = 1.5f, int firstId = 1, int channels = 20) =>
        Enumerable.Range(0, count)
            .Select(i => kit.Fixture(spec, $"Fx {firstId + i}", firstId + i, new Vector3(i * spacing, 0, 6), universe, 1 + i * channels))
            .ToList();

    // ------------------------------------------------------------------ DMX topology

    [Fact]
    public void Fixtures_with_xlr3_and_xlr5_thru_are_not_splitters_so_long_chains_are_still_flagged()
    {
        var p = TestData.Profile(Cobra100);
        Assert.Equal(2, p.Ports.Count(x => x.Signal == SignalKind.Dmx512 && x.Direction == PortDirection.Output));

        var kit = new SceneKit();
        var desk = kit.Console("Desk", new Vector3(0, -12, 0), SceneKit.DmxOutput("DMX A", 1), SceneKit.DmxOutput("DMX B", 2));
        var spots = Row(kit, Cobra100, 34, channels: 15);
        kit.Chain("dmx-xlr5", "A", (desk, "DMX A"), spots, "DMX 5Pin IN", "DMX 5Pin OUT");
        var far = Enumerable.Range(0, 3).Select(i => kit.Fixture(Cobra100, $"Far {i + 1}", 101 + i, new Vector3(0, 150f * (i + 1), 6), 2, 1 + i * 20)).ToList();
        kit.Chain("dmx-xlr5", "B", (desk, "DMX B"), far, "DMX 5Pin IN", "DMX 5Pin OUT");

        var s = kit.Solve();
        var tooMany = s.Issues.Single(IssueCodes.DataChainTooLong);
        Assert.Contains("#33 'Fx 33'", tooMany.Message);
        Assert.Contains("'Far 2'", s.Issues.Single(IssueCodes.DataRunTooLong).Message);
        Assert.Equal(Enumerable.Range(1, 34), s.DataLines[0].Feeds.Select(f => f.ChainIndex));
    }

    [Fact]
    public void Using_both_the_3_pin_and_5_pin_thru_of_a_fixture_is_a_y_split()
    {
        var kit = new SceneKit();
        var desk = kit.Console("Desk", new Vector3(0, -12, 0), SceneKit.DmxOutput("DMX A", 1));
        var spots = Row(kit, Cobra100, 3, channels: 15);
        kit.Cable("dmx-xlr5", "1", (desk, "DMX A"), (spots[0], "DMX 5Pin IN"));
        kit.Cable("dmx-xlr5", "2", (spots[0], "DMX 5Pin OUT"), (spots[1], "DMX 5Pin IN"));
        kit.Cable("dmx-xlr3", "3", (spots[0], "DMX 3Pin OUT"), (spots[2], "DMX 3Pin IN"));

        var s = kit.Solve();
        var issue = s.Issues.Single(IssueCodes.DataYSplit);
        Assert.Equal(IssueSeverity.Warning, issue.Severity);
        Assert.Contains("#1 'Fx 1'", issue.Message);
        Assert.Contains("'DMX 5Pin OUT'", issue.Message);
        Assert.Contains("'DMX 3Pin OUT'", issue.Message);
        Assert.All(spots, f => Assert.True(s.IsConsoleDriven(f.Id)));

        // One thru in use is a plain daisy chain.
        var kit2 = new SceneKit();
        var desk2 = kit2.Console("Desk", new Vector3(0, -12, 0), SceneKit.DmxOutput("DMX A", 1));
        kit2.Chain("dmx-xlr3", "D", (desk2, "DMX A"), Row(kit2, Cobra100, 3, channels: 15), "DMX 3Pin IN", "DMX 3Pin OUT");
        var s2 = kit2.Solve();
        s2.Issues.None(IssueCodes.DataYSplit);
        Assert.Contains("XLR 3-pin to 5-pin", s2.Issues.Single(IssueCodes.CableAdapterNeeded).Message); // only at the desk's 5-pin output
    }

    [Fact]
    public void A_real_dmx_splitter_starts_new_segments()
    {
        var head = TestData.Profile(TestData.Naja);
        var splitter = new FixtureProfile
        {
            GdtfSpec = "Test@Splitter",
            DisplayName = "Test opto-splitter 1 in 4 out",
            Category = FixtureCategory.Other,
            Ports =
            [
                Data("DMX In", PortDirection.Input),
                Data("DMX Out 1", PortDirection.Output),
                Data("DMX Out 2", PortDirection.Output),
                Data("DMX Out 3", PortDirection.Output),
                Data("DMX Out 4", PortDirection.Output),
            ],
        };
        var profiles = new InMemoryFixtureProfileProvider().Add(TestData.Naja, head).Add("Test@Splitter", splitter);
        var kit = new SceneKit(profiles);
        var desk = kit.Console("Desk", new Vector3(0, -12, 0), SceneKit.DmxOutput("DMX A", 1));
        var before = Row(kit, TestData.Naja, 20, channels: 20);
        var split = kit.Fixture("Test@Splitter", "Splitter", 0, new Vector3(0, -2, 6));
        var after = Row(kit, TestData.Naja, 20, firstId: 101, channels: 20);
        kit.Chain("dmx-xlr5", "A", (desk, "DMX A"), before, "XLR IN", "XLR OUT");
        kit.Cable("dmx-xlr5", "A21", (before[^1], "XLR OUT"), (split, "DMX In"));
        kit.Chain("dmx-xlr5", "S", (split, "DMX Out 1"), after, "XLR IN", "XLR OUT");

        var s = kit.Solve();
        s.Issues.None(IssueCodes.DataChainTooLong); // 21 + 20 devices, but in two segments
        Assert.Equal(41, s.DataLines[0].Feeds.Count);
        Assert.All(after, f => Assert.True(s.IsConsoleDriven(f.Id)));

        static Port Data(string name, PortDirection direction) => new()
        {
            Name = name,
            Direction = direction,
            Signal = SignalKind.Dmx512,
            Connector = ConnectorCatalog.Resolve("XLR5"),
            Role = direction == PortDirection.Input ? PortRole.DataIn : PortRole.DataOut,
            Origin = PortOrigin.Specs,
        };
    }

    [Fact]
    public void A_dmx_thru_repeats_only_the_break_arriving_at_its_input()
    {
        static Port Data(string name, PortDirection direction, int dmxBreak) => new()
        {
            Name = name,
            Direction = direction,
            Signal = SignalKind.Dmx512,
            Connector = ConnectorCatalog.Resolve("XLR5"),
            Role = direction == PortDirection.Input ? PortRole.DataIn : PortRole.DataOut,
            Origin = PortOrigin.GdtfConnector,
            DmxBreak = dmxBreak,
        };
        var twoBreaks = new FixtureProfile
        {
            GdtfSpec = "Test@TwoBreaks",
            DisplayName = "Two-break LED wall",
            Category = FixtureCategory.LedBar,
            Ports = [Data("DMX In 1", PortDirection.Input, 1), Data("DMX Out 1", PortDirection.Output, 1), Data("DMX In 2", PortDirection.Input, 2), Data("DMX Out 2", PortDirection.Output, 2)],
        };
        var profiles = new InMemoryFixtureProfileProvider().Add("Test@TwoBreaks", twoBreaks);
        var kit = new SceneKit(profiles);
        var desk = kit.Console("Desk", new Vector3(0, -12, 0), SceneKit.DmxOutput("DMX A", 1));
        var a = kit.Fixture("Test@TwoBreaks", "A", 1, new Vector3(0, 0, 6), 1, 1);
        var b = kit.Fixture("Test@TwoBreaks", "B", 2, new Vector3(2, 0, 6), 1, 101);
        var c = kit.Fixture("Test@TwoBreaks", "C", 3, new Vector3(4, 0, 6), 1, 201);
        kit.Cable("dmx-xlr5", "1", (desk, "DMX A"), (a, "DMX In 1"));
        kit.Cable("dmx-xlr5", "2", (a, "DMX Out 1"), (b, "DMX In 1"));
        kit.Cable("dmx-xlr5", "3", (a, "DMX Out 2"), (c, "DMX In 1")); // break 2 thru carries nothing from break 1

        var s = kit.Solve();
        Assert.True(s.IsConsoleDriven(b.Id));
        Assert.False(s.IsConsoleDriven(c.Id));
    }

    // ------------------------------------------------------------------ port ratings and directions from GDTF

    [Fact]
    public void A_gdtf_fuse_value_never_lifts_a_link_above_its_connector_rating()
    {
        var link = TestData.Profile(Ignite740).Ports.Single(p => p.Role == PortRole.PowerLink);
        Assert.Equal("Power OUT", link.Name);
        Assert.Equal(16f, link.MaxThroughCurrentA); // the file writes FuseCurrent = 240 on its TRUE1 thru

        var kit = new SceneKit();
        var distro = kit.Source("Distro", new Vector3(0, -5, 0), outlets: SceneKit.Outlet("A", "powerCON TRUE1", 32f));
        var washes = Row(kit, Ignite740, 15, universe: 0);
        kit.Chain("powercon-true1", "P", (distro, "A"), washes, "Power IN", "Power OUT");

        var s = kit.Solve();
        // 14 × 264 W behind the first fixture's thru at cos φ 0.96 on 230 V = 16.7 A
        var overload = s.Issues.WithCode(IssueCodes.PowerLinkOverload).Single(i => i.Message.Contains("#1 'Fx 1'"));
        Assert.Contains("16.7 A", overload.Message);
        Assert.Contains("rated 16.0 A", overload.Message);
        Assert.Contains("14 linked devices", overload.Message);
    }

    [Fact]
    public void An_inlet_keeps_its_connector_rating_whatever_fuse_the_gdtf_gives_the_device()
    {
        // The Robin 100 LEDBeam's powerCON inlet is a Consumer with FuseCurrent = 240: neither the internal fuse nor
        // that bogus value limits (or lifts) what the inlet and its thru may carry.
        var gdtf = TestData.FixtureType("Robe_Lighting@Robin_100_LEDBeam@2024-02-09__3D_models_with_robe_color_revision.gdtf");
        var inlet = PortResolver.FromWiringObjects(gdtf, null).Single(p => p.Role == PortRole.PowerIn);
        Assert.Null(inlet.MaxThroughCurrentA);
        Assert.Equal(20f, inlet.Connector.RatedCurrentA);
    }

    [Fact]
    public void Wiring_object_names_saying_in_or_out_win_over_a_wrong_component_type()
    {
        var bar = TestData.Profile(DotBarBat1);
        Assert.Equal(PortRole.PowerLink, bar.FindPort("Power OUT 2")!.Role);
        Assert.Equal(PortRole.PowerIn, bar.FindPort("Power IN 2")!.Role);
        Assert.Equal(PortDirection.Input, bar.FindPort("Network IN 2")!.Direction);

        var strobe = TestData.Profile(BsStrobe);
        Assert.Equal(PortRole.PowerLink, strobe.FindPort("Power OUT 1")!.Role);
        Assert.Single(strobe.Ports, p => p.Role == PortRole.PowerLink); // no extra specs "Power Out" next to it

        var kit = new SceneKit();
        var wall = kit.Source("Wall", new Vector3(0, -5, 0), outlets: SceneKit.Outlet("Socket", "powerCON TRUE1", 16f));
        var strobes = Row(kit, BsStrobe, 3, universe: 0);
        kit.Chain("powercon-true1", "P", (wall, "Socket"), strobes, "Power IN 1", "Power OUT 1");
        var s = kit.Solve();
        Assert.Empty(s.UnpoweredDevices);
        Assert.Equal(180, s.PowerCircuits.Single().LoadW, 6);
    }

    // ------------------------------------------------------------------ mains

    [Fact]
    public void A_three_phase_source_entered_as_400_V_supplies_230_V_per_phase()
    {
        var kit = new SceneKit();
        var gen = kit.Source("Generator", new Vector3(-10, -10, 0), voltage: 400f, phases: 3, outlets: SceneKit.Outlet("CEE 63", "CEE 63A 3P", 63f));
        var distro = kit.Source("Distro", new Vector3(0, -5, 0), voltage: 400f, phases: 3, outlets:
            [SceneKit.Outlet("L1", "powerCON TRUE1", 16f, 1), SceneKit.Outlet("L2", "powerCON TRUE1", 16f, 2)]);
        kit.Cable("cee63-3p", "Main", (gen, "CEE 63"), (distro, PortResolver.MainsInPortName));
        var blade = kit.Fixture(TestData.BmflBlade, "Blade", 1, new Vector3(0, 0, 6));
        kit.Cable("powercon-true1", "B", (distro, "L1"), (blade, "Power IN"));

        var s = kit.Solve();
        s.Issues.None(IssueCodes.PowerVoltageMismatch);
        var feed = s.PowerFeedOf(blade.Id)!;
        Assert.Equal(400 / MathF.Sqrt(3), feed.VoltageV, 3);
        double own = TestData.Profile(TestData.BmflBlade).PowerW!.Value;
        double pf = TestData.Profile(TestData.BmflBlade).PowerFactor;
        Assert.Equal(own / pf / (400 / Math.Sqrt(3)), s.PowerCircuits.Single(c => c.Outlet.Name == "L1").CurrentA, 3);
        Assert.Equal(own / pf / (400 / Math.Sqrt(3)), s.PowerSources.Single(p => p.SourceEntityId == gen.Id).PhaseCurrentsA[0], 3);

        // 230 V entered on a three-phase source is already the phase voltage
        Assert.Equal(230f, NetworkSolver.PhaseVoltage(230f, 3));
        Assert.Equal(400f, NetworkSolver.PhaseVoltage(400f, 1));
    }

    [Fact]
    public void A_three_phase_distro_on_a_single_phase_feed_is_an_error()
    {
        var kit = new SceneKit();
        var gen = kit.Source("Generator", new Vector3(-10, -10, 0), voltage: 400f, phases: 3, outlets:
            [SceneKit.Outlet("CEE 32", "CEE 32A 3P", 32f), SceneKit.Outlet("Schuko", "Schuko", 16f, 2)]);
        var good = kit.Source("Good distro", new Vector3(0, -5, 0), phases: 3, outlets: SceneKit.Outlet("L1"));
        var bad = kit.Source("Bad distro", new Vector3(5, -5, 0), phases: 3, outlets: SceneKit.Outlet("L1"));
        var lead = kit.Source("Lead distro", new Vector3(10, -5, 0), phases: 3, outlets: SceneKit.Outlet("L1"));
        kit.Cable("cee32-3p", "Main", (gen, "CEE 32"), (good, PortResolver.MainsInPortName));
        kit.Cable("schuko", "Wrong", (gen, "Schuko"), (bad, PortResolver.MainsInPortName));
        var cee = kit.Source("Second generator", new Vector3(-20, -10, 0), phases: 3, outlets: SceneKit.Outlet("CEE 32", "CEE 32A 3P", 32f));
        kit.Cable("cee16", "Single", (cee, "CEE 32"), (lead, PortResolver.MainsInPortName)); // single-phase lead on a 5-pin outlet

        var s = kit.Solve();
        var issues = s.Issues.WithCode(IssueCodes.PowerPhaseMismatch);
        Assert.Equal(2, issues.Count);
        Assert.Contains(issues, i => i.Message.Contains("'Bad distro'") && i.Message.Contains("L2"));
        Assert.Contains(issues, i => i.Message.Contains("'Lead distro'"));
        Assert.DoesNotContain(issues, i => i.Entities.Contains(good.Id));
        Assert.All(issues, i => Assert.Equal(IssueSeverity.Error, i.Severity));
    }

    [Fact]
    public void A_distro_whose_mains_in_is_cabled_to_nothing_valid_is_not_a_source()
    {
        var kit = new SceneKit();
        var other = kit.Fixture(TestData.Aeco20, "Other", 1, new Vector3(5, 0, 0));
        var distro = kit.Source("Floor distro", new Vector3(0, -5, 0), outlets: SceneKit.Outlet("A"));
        kit.Cable("powercon-true1", "Wrong", (other, "Power In"), (distro, PortResolver.MainsInPortName)); // two inputs
        var load = kit.Fixture(TestData.Aeco20, "Load", 2, new Vector3(2, 0, 0));
        kit.Cable("powercon-true1", "P", (distro, "A"), (load, "Power In"));

        var s = kit.Solve();
        Assert.Contains(distro.Id, s.UnpoweredDevices);
        Assert.Contains(load.Id, s.UnpoweredDevices);
        Assert.Null(s.PowerFeedOf(load.Id));
        Assert.Contains(s.Issues.WithCode(IssueCodes.PowerUnpowered), i => i.Message.Contains("Distro 'Floor distro'"));
    }

    [Fact]
    public void Cable_overload_names_the_devices_it_feeds()
    {
        var kit = new SceneKit();
        var wall = kit.Source("Wall", new Vector3(0, -5, 0), outlets: SceneKit.Outlet("Socket", "Schuko", 32f));
        var aecos = Enumerable.Range(0, 3).Select(i => kit.Fixture(TestData.Aeco20, $"AECO {i + 1}", i + 1, new Vector3(i * 2f, 0, 6))).ToList();
        kit.Cable("iec", "Lead", (wall, "Socket"), (aecos[0], "Power In"));
        kit.Chain("powercon-true1", "L", (aecos[0], "Power Out"), aecos.Skip(1).ToList(), "Power In", "Power Out");

        var issue = kit.Solve().Issues.Single(IssueCodes.PowerCableOverload);
        Assert.Contains("'Lead'", issue.Message);
        Assert.Contains("rated 10.0 A", issue.Message);
        Assert.Contains("#1 'AECO 1'", issue.Message);
        Assert.Contains("#3 'AECO 3'", issue.Message);
        Assert.Contains(aecos[2].Id, issue.Entities);
    }

    // ------------------------------------------------------------------ rigging statics

    [Fact]
    public void Two_fixtures_near_mid_span_overload_the_point_rating_although_each_is_below_it()
    {
        var kit = new SceneKit();
        var truss = kit.Truss("Front truss", new Vector3(0, 0, 6), 6f, maxUniformKg: 500f, maxPointKg: 30f);
        var a = kit.Fixture(TestData.Face600, "Left", 1, new Vector3(2.9f, 0, 5.6f), mountedOn: truss.Id);
        var b = kit.Fixture(TestData.Face600, "Right", 2, new Vector3(3.1f, 0, 5.6f), mountedOn: truss.Id);

        var r = new RiggingAnalyzer(TestData.Profiles).Analyze(kit.Scene);
        var load = r.Trusses.Single();
        Assert.Equal(25.5, load.HeaviestPointLoadKg, 6);
        // R = 25.5 kg, M = 25.5 × 2.9 = 73.95 kg·m, equivalent mid-span load 4M/L = 49.3 kg
        Assert.Equal(73.95, load.MaxBendingMomentKgM, 3);
        Assert.Equal(49.3, load.EquivalentCentrePointLoadKg, 1);
        var issue = r.Issues.Single(IssueCodes.RiggingTrussPointOverload);
        Assert.Equal(IssueSeverity.Error, issue.Severity);
        Assert.Contains("49.3 kg", issue.Message);
        Assert.Contains("30.0 kg", issue.Message);
        Assert.Contains("#1 'Left'", issue.Message);
        Assert.Contains("#2 'Right'", issue.Message);
        Assert.Contains(a.Id, issue.Entities);
        Assert.Contains(b.Id, issue.Entities);
        r.Issues.None(IssueCodes.RiggingTrussOverload);

        // The same two fixtures at the ends bend nothing.
        var kit2 = new SceneKit();
        var t2 = kit2.Truss("Front truss", new Vector3(0, 0, 6), 6f, maxUniformKg: 500f, maxPointKg: 30f);
        kit2.Fixture(TestData.Face600, "Left", 1, new Vector3(0.1f, 0, 5.6f), mountedOn: t2.Id);
        kit2.Fixture(TestData.Face600, "Right", 2, new Vector3(5.9f, 0, 5.6f), mountedOn: t2.Id);
        new RiggingAnalyzer(TestData.Profiles).Analyze(kit2.Scene).Issues.None(IssueCodes.RiggingTrussPointOverload);
    }

    [Fact]
    public void Concentrated_loads_are_checked_against_the_uniform_rating_when_no_point_rating_is_given()
    {
        var kit = new SceneKit();
        var truss = kit.Truss("Pipe", new Vector3(0, 0, 6), 6f, maxUniformKg: 80f);
        kit.Fixture(TestData.Face600, "Left", 1, new Vector3(2.9f, 0, 5.6f), mountedOn: truss.Id);
        kit.Fixture(TestData.Face600, "Right", 2, new Vector3(3.1f, 0, 5.6f), mountedOn: truss.Id);

        var r = new RiggingAnalyzer(TestData.Profiles).Analyze(kit.Scene);
        Assert.Equal(51, r.Trusses.Single().LoadKg, 6); // below 80 kg in total …
        var issue = r.Issues.Single(IssueCodes.RiggingTrussOverload); // … but 98.6 kg as an equivalent uniform load
        Assert.Contains("98.6 kg", issue.Message);
        Assert.Contains("80.0 kg", issue.Message);
        Assert.Contains("#1 'Left'", issue.Message);
    }

    [Fact]
    public void A_fixture_placed_under_a_hoist_in_the_hierarchy_hangs_from_it()
    {
        var kit = new SceneKit();
        var hoist = new Entity("Hoist") { Local = new Core.Math.Transform(new Vector3(0, 0, 8)) };
        hoist.Add(new Core.SceneGraph.Components.RiggingPointComponent { CapacityKg = 20f });
        kit.Scene.Add(hoist);
        var face = kit.Fixture(TestData.Face600, "Hung", 4, new Vector3(0, 0, -0.5f), parent: hoist);

        var r = new RiggingAnalyzer(TestData.Profiles).Analyze(kit.Scene);
        r.Issues.None(IssueCodes.RiggingNotMounted);
        var point = r.Points.Single();
        Assert.Equal(25.5, point.LoadKg, 6);
        var issue = r.Issues.Single(IssueCodes.RiggingPointOverload);
        Assert.Contains("#4 'Hung' 25.5 kg", issue.Message);
        Assert.Contains(face.Id, issue.Entities);
    }

    [Fact]
    public void Uniform_overload_message_names_the_heaviest_fixtures()
    {
        var kit = new SceneKit();
        var truss = kit.Truss("Weak", new Vector3(0, 0, 6), 6f, maxUniformKg: 40f);
        kit.Fixture(TestData.Face600, "Spot A", 7, new Vector3(1, 0, 5.6f), mountedOn: truss.Id);
        kit.Fixture(TestData.Face600, "Spot B", 8, new Vector3(5, 0, 5.6f), mountedOn: truss.Id);
        var issue = new RiggingAnalyzer(TestData.Profiles).Analyze(kit.Scene).Issues.Single(IssueCodes.RiggingTrussOverload);
        Assert.Contains("#7 'Spot A'", issue.Message);
        Assert.Contains("#8 'Spot B'", issue.Message);
        Assert.Contains("51.0 kg", issue.Message);
    }
}
