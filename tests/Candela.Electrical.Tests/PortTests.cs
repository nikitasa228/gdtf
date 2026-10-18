using System.Collections.Concurrent;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;

namespace Candela.Electrical.Tests;

public class PortTests
{
    [Fact]
    public void Ports_come_from_gdtf_wiring_objects_when_the_fixture_has_them()
    {
        var profile = TestData.Profile(TestData.Naja);
        var ports = profile.Ports;
        Assert.All(ports, p => Assert.Equal(PortOrigin.GdtfWiringObject, p.Origin));

        var powerIn = profile.FindPort("Power IN")!;
        Assert.Equal(PortDirection.Input, powerIn.Direction);
        Assert.Equal(SignalKind.Power, powerIn.Signal);
        Assert.Equal(PortRole.PowerIn, powerIn.Role);
        Assert.Equal("powerCON TRUE1 TOP", powerIn.Connector.Id);
        Assert.Equal(16f, powerIn.Connector.RatedCurrentA);

        var powerOut = profile.FindPort("Power OUT")!;
        Assert.Equal(PortRole.PowerLink, powerOut.Role);

        var xlrIn = profile.FindPort("xlr in")!; // case-insensitive
        Assert.Equal(SignalKind.Dmx512, xlrIn.Signal);
        Assert.Equal(PortDirection.Input, xlrIn.Direction);
        Assert.Equal("XLR5", xlrIn.Connector.Id);
        Assert.Equal(PortDirection.Output, profile.FindPort("XLR OUT")!.Direction);

        var ethIn = profile.FindPort("ETH IN")!;
        Assert.Equal(SignalKind.Ethernet, ethIn.Signal);
        Assert.Equal("RJ45", ethIn.Connector.Id);
        Assert.NotNull(ethIn.LocalPosition);

        Assert.Equal(700, profile.PowerW);
        Assert.Equal(PowerDataSource.Specs, profile.PowerSource);
        Assert.Equal(31.5, profile.WeightKg);
    }

    [Fact]
    public void Ports_come_from_specs_when_the_gdtf_has_no_connectors()
    {
        var type = TestData.FixtureType(TestData.Aeco20);
        Assert.Empty(type.AllGeometries().OfType<Candela.Gdtf.WiringObjectGeometry>());
        Assert.Empty(type.PhysicalDescriptions.Connectors);

        var profile = TestData.Profile(TestData.Aeco20);
        Assert.All(profile.Ports, p => Assert.Equal(PortOrigin.Specs, p.Origin));
        Assert.Equal(["Power In", "Power Out", "DMX In", "DMX Out", "Ethernet"], profile.Ports.Select(p => p.Name));

        var powerIn = profile.FindPort("Power In")!;
        Assert.Equal(PortRole.PowerIn, powerIn.Role);
        Assert.Equal("powerCON TRUE1", powerIn.Connector.Id);
        var powerOut = profile.FindPort("Power Out")!;
        Assert.Equal(PortRole.PowerLink, powerOut.Role);
        Assert.Equal(16f, powerOut.MaxThroughCurrentA);
        var dmxIn = profile.FindPort("DMX In")!;
        Assert.Equal(SignalKind.Dmx512, dmxIn.Signal);
        Assert.True(dmxIn.Connector.IsWildcard); // specs only say "DMX512": any XLR fits without a warning
        Assert.Equal(PortDirection.Bidirectional, profile.FindPort("Ethernet")!.Direction);
        Assert.Equal(1580, profile.PowerW);
    }

    [Fact]
    public void Legacy_gdtf_connectors_get_their_direction_from_the_name()
    {
        // The ADJ file gives the TRUE1 inlet gender +1 and the outlet −1 (the reverse of its DMX connectors).
        var profile = TestData.Profile(TestData.CobCannon);
        Assert.All(profile.Ports, p => Assert.Equal(PortOrigin.GdtfConnector, p.Origin));
        Assert.Equal(PortDirection.Input, profile.FindPort("DMX-In 5pin")!.Direction);
        Assert.Equal(PortDirection.Output, profile.FindPort("DMX-Out 3pin")!.Direction);
        var inlet = profile.FindPort("PowerCon True1-In")!;
        Assert.Equal(PortRole.PowerIn, inlet.Role);
        Assert.Equal("powerCON TRUE1", inlet.Connector.Id);
        var link = profile.FindPort("PowerCon True1-Out")!;
        Assert.Equal(PortRole.PowerLink, link.Role);
        Assert.Equal("powerCON TRUE1 TOP", link.Connector.Id);
    }

    [Fact]
    public void Dimmer_pack_gets_specs_connector_and_four_dimmer_outputs()
    {
        var profile = TestData.Profile(TestData.DimmerPack4);
        var inlet = profile.Ports.Single(p => p.Role == PortRole.PowerIn);
        Assert.Equal("Power IN", inlet.Name);
        Assert.Equal("Schuko", inlet.Connector.Id); // blank ConnectorType in the GDTF, filled from _specs
        var dimmers = profile.Ports.Where(p => p.Role == PortRole.PowerDistribution).ToList();
        Assert.Equal(["Dimmer 1", "Dimmer 2", "Dimmer 3", "Dimmer 4"], dimmers.Select(p => p.Name));
        Assert.All(dimmers, d => Assert.Equal(10f, d.MaxThroughCurrentA));
        Assert.Equal([1, 2, 3, 4], dimmers.Select(d => d.Channel));
        Assert.Equal("XLR3", profile.FindPort("DMX 3Pin IN")!.Connector.Id);

        var dist = profile.Distribution!;
        Assert.True(dist.OutputIsDimmed);
        Assert.False(dist.OutputIsDc);
        Assert.Equal(4400, dist.MaxLoadW);
        Assert.Equal(1.0, dist.LossFactor);
        Assert.Equal(10, profile.PowerW); // own draw only
    }

    [Fact]
    public void Dc_power_supply_has_dc_outputs_and_losses()
    {
        var profile = TestData.Profile(TestData.PowerPort2000);
        var dist = profile.Distribution!;
        Assert.True(dist.OutputIsDc);
        Assert.Equal(48f, dist.OutputVoltageDcV);
        Assert.InRange(dist.LossFactor, 1.14, 1.17); // (2240 − 24) / 1920
        Assert.Contains(profile.Ports, p => p.Role == PortRole.PowerDistribution);
    }

    [Fact]
    public void Consoles_and_power_sources_expose_their_outputs()
    {
        var console = new ConsoleComponent();
        console.Outputs.Add(new ConsoleOutput { PortName = "Net 1", Signal = "Ethernet", FirstUniverse = 5, UniverseCount = 8 });
        var ports = PortResolver.FromConsole(console);
        Assert.Equal(5, ports.Count);
        Assert.Equal(("DMX A", 1, SignalKind.Dmx512, "XLR5"), (ports[0].Name, ports[0].Universe!.Value, ports[0].Signal, ports[0].Connector.Id));
        Assert.Equal((5, 8, SignalKind.Ethernet), (ports[4].Universe!.Value, ports[4].UniverseCount, ports[4].Signal));
        Assert.All(ports, p => Assert.Equal(PortRole.ConsoleOutput, p.Role));

        var source = new PowerSourceComponent { Phases = 3 };
        source.Outlets.Clear();
        source.Outlets.Add(new PowerOutlet { PortName = "L1", Connector = "CEE 16A", BreakerA = 16, Phase = 1 });
        source.Outlets.Add(new PowerOutlet { PortName = "Soca", Connector = "Socapex", BreakerA = 16, Phase = 2 });
        var outlets = PortResolver.FromPowerSource(source);
        Assert.Equal(["L1", "Soca", PortResolver.MainsInPortName], outlets.Select(p => p.Name));
        Assert.Equal(SignalKind.Multicore, outlets[1].Signal);
        Assert.Equal(6, outlets[1].Circuits);
        Assert.Equal(16f, outlets[0].BreakerA);
        Assert.Equal(PortRole.PowerIn, outlets[2].Role);
    }

    [Fact]
    public void Resolver_finds_ports_of_scene_entities_and_ranks_them_for_the_port_picker()
    {
        var resolver = new PortResolver(TestData.Profiles);
        var e = new Entity("Naja");
        e.Add(new FixtureComponent { GdtfSpec = TestData.Naja });
        Assert.NotNull(resolver.FindPort(e, "power in"));
        Assert.Null(resolver.FindPort(e, "nope"));

        var picks = resolver.CompatiblePorts(e, CableCatalog.Default.Find("dmx-xlr5")!);
        Assert.Equal(["XLR IN", "XLR OUT"], picks.Select(p => p.Port.Name));
        var true1 = resolver.CompatiblePorts(e, CableCatalog.Default.Find("powercon-true1")!);
        Assert.Equal(["Power IN", "Power OUT"], true1.Select(p => p.Port.Name));
        Assert.All(true1, p => Assert.Equal(ConnectorMatch.Compatible, p.Check.Match)); // TRUE1 cable on TRUE1 TOP ports

        var unknown = new Entity("Mystery");
        unknown.Add(new FixtureComponent { GdtfSpec = "Nobody@Nothing@1.gdtf" });
        Assert.Empty(resolver.GetPorts(unknown));
    }

    [Fact]
    public void Every_library_fixture_that_needs_power_and_dmx_gets_matching_ports()
    {
        // A private library with a tiny parse cache: profiles keep no reference to the parsed GDTF, so memory stays
        // flat while all 1091 files are parsed.
        var library = new Candela.Gdtf.FixtureLibrary(TestData.Specs, cacheCapacity: 2);
        Assert.Equal(1091, library.AddFolder(TestData.RepoRoot));
        var profiles = new LibraryFixtureProfileProvider(library);
        var missingPower = new ConcurrentBag<string>();
        var missingData = new ConcurrentBag<string>();
        Parallel.ForEach(library.Entries, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(2, Environment.ProcessorCount) }, entry =>
        {
            var profile = profiles.GetProfile(entry.FileName, null);
            Assert.NotNull(profile);
            var specs = entry.Specs!;
            bool battery = specs.BatteryPowered && (specs.PowerConnectorIn is null || specs.PowerConnectorIn.StartsWith("Battery", StringComparison.Ordinal));
            if (!battery && specs.PowerConnectorIn is not null && !profile!.Ports.Any(p => p.Role == PortRole.PowerIn))
                missingPower.Add(entry.FileName);
            bool wiredDmx = specs.ControlSignal.Contains("DMX512");
            if (wiredDmx && !profile!.Ports.Any(p => p.Signal.CarriesData() && p.Direction != PortDirection.Output))
                missingData.Add(entry.FileName);
        });
        Assert.True(missingPower.IsEmpty, "No power inlet: " + string.Join(", ", missingPower.Take(10)));
        Assert.True(missingData.IsEmpty, "No data input: " + string.Join(", ", missingData.Take(10)));
    }
}
