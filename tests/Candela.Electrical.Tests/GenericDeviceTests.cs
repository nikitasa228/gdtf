using System.Numerics;
using Candela.Core.SceneGraph.Components;
using Candela.Gdtf;

namespace Candela.Electrical.Tests;

/// <summary>
/// The generic devices of the cable tool (DMX splitters, Art-Net nodes, a dimmer pack): their GDTF files parse, their
/// ports and built-in specs give the right profiles, and the network solver treats them as splitters, converters and
/// distribution devices. Also the cable types added for mains supplies (CEE 32 A single phase, CEE 16/125 A three
/// phase, Powerlock).
/// </summary>
public sealed class GenericDeviceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "candela-generic-" + Guid.NewGuid().ToString("N"));
    private readonly FixtureLibrary _library;
    private readonly LibraryFixtureProfileProvider _profiles;

    public GenericDeviceTests()
    {
        Directory.CreateDirectory(_dir);
        foreach (var device in GenericDevices.All)
            File.WriteAllBytes(Path.Combine(_dir, device.FileName), GenericDevices.GdtfBytes(device));
        _library = FixtureLibrary.Scan(_dir, TestData.Specs);
        _library.AddFiles([Path.Combine(TestData.RepoRoot, TestData.Naja), Path.Combine(TestData.RepoRoot, TestData.Aeco20)]);
        _profiles = new LibraryFixtureProfileProvider(_library);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Every_generic_device_is_a_valid_gdtf_with_its_ports_and_mode()
    {
        Assert.True(GenericDevices.All.Count >= 5);
        Assert.Equal(GenericDevices.All.Count, GenericDevices.All.Select(d => d.FileName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var device in GenericDevices.All)
        {
            var entry = _library.FindByFileName(device.FileName);
            Assert.NotNull(entry);
            Assert.True(entry!.FollowsNamingPattern, device.FileName);
            var type = _library.GetFixtureType(entry);
            Assert.Equal(device.FixtureTypeId, type.FixtureTypeId);
            Assert.Equal(GenericDevices.Manufacturer, type.Manufacturer);
            var mode = Assert.Single(type.DmxModes);
            Assert.Equal(device.ModeName, mode.Name);
            Assert.Equal(device.DmxChannels, mode.Channels.Count);

            var profile = _profiles.GetProfile(device.FileName, null);
            Assert.NotNull(profile);
            Assert.Equal(FixtureCategory.Other, profile!.Category);
            Assert.Equal(device.PowerW, profile.PowerW);
            Assert.Equal(device.WeightKg, profile.WeightKg);
            Assert.Equal(device.Ports.Select(p => p.Name), profile.Ports.Select(p => p.Name));
            Assert.All(profile.Ports, p => Assert.True(p.Connector.IsKnown, $"{device.Model}: {p.Name} has an unknown connector"));
            Assert.All(profile.Ports, p => Assert.NotNull(p.LocalPosition));
            Assert.True(profile.NeedsPower);
            Assert.True(profile.AcceptsSupply(230f, 50f, false, out _));
        }
    }

    [Fact]
    public void The_gdtf_bytes_are_deterministic_and_lookups_ignore_folder_case_and_extension()
    {
        var splitter = GenericDevices.All[0];
        Assert.Equal(GenericDevices.GdtfBytes(splitter), GenericDevices.GdtfBytes(splitter));
        Assert.Same(splitter, GenericDevices.Find(Path.Combine("some", "folder", splitter.FileName.ToUpperInvariant())));
        Assert.Same(splitter, GenericDevices.Find(Path.GetFileNameWithoutExtension(splitter.FileName)));
        Assert.NotNull(GenericDevices.FindSpecs(splitter.FileName));
        Assert.Null(GenericDevices.Find(TestData.Naja));
        Assert.Null(GenericDevices.Find(""));
        Assert.Null(GenericDevices.Find(null));
        Assert.Null(TestData.Specs.Find(splitter.FileName));
    }

    [Fact]
    public void A_splitter_starts_a_new_dmx_line_on_every_output()
    {
        var kit = new SceneKit(_profiles);
        var desk = kit.Console("Desk", Vector3.Zero, SceneKit.DmxOutput("DMX A", 1));
        var splitter = kit.Fixture(GenericDevices.All.Single(d => d.Id == "dmx-splitter-6").FileName, "Splitter", 0, new Vector3(2, 0, 0));
        var heads = Enumerable.Range(0, 3).Select(i => kit.Fixture(TestData.Naja, $"Naja {i + 1}", i + 1, new Vector3(4, i, 5), 1, 1 + i * 40)).ToList();
        kit.Cable(CableCatalog.Ids.DmxXlr5, "A1", (desk, "DMX A"), (splitter, "DMX In"));
        var najaIn = _profiles.GetProfile(TestData.Naja, null)!.Ports.First(p => p.Signal == SignalKind.Dmx512 && p.Direction == PortDirection.Input).Name;
        for (int i = 0; i < heads.Count; i++)
            kit.Cable(CableCatalog.Ids.DmxXlr5, $"S{i + 1}", (splitter, $"DMX Out {i + 1}"), (heads[i], najaIn));

        var solution = kit.Solve();
        foreach (var head in heads)
        {
            Assert.True(solution.IsConsoleDriven(head.Id));
            var feed = solution.DataFeedOf(head.Id)!;
            Assert.Equal(2, feed.ChainIndex);
            Assert.Equal(2, feed.CablePath.Count);
            Assert.Equal(splitter.Id, feed.UpstreamEntityId);
        }
        solution.Issues.None(IssueCodes.DataYSplit);
        solution.Issues.None(IssueCodes.DataUniverseMismatch);
        Assert.False(solution.IsConsoleDriven(splitter.Id) && splitter.Get<FixtureComponent>()!.Addresses.Count > 0);
    }

    [Fact]
    public void An_artnet_node_sends_one_universe_per_dmx_output()
    {
        var kit = new SceneKit(_profiles);
        var desk = kit.Console("Network desk", Vector3.Zero, SceneKit.EthernetOutput("Ethernet", 1, 16));
        var node = kit.Fixture(GenericDevices.All.Single(d => d.Id == "artnet-node-2").FileName, "Node", 0, new Vector3(2, 0, 4));
        var a = kit.Fixture(TestData.Naja, "Naja A", 1, new Vector3(3, 0, 5), universe: 1);
        var b = kit.Fixture(TestData.Naja, "Naja B", 2, new Vector3(5, 0, 5), universe: 2);
        var wrong = kit.Fixture(TestData.Naja, "Naja wrong", 3, new Vector3(7, 0, 5), universe: 1, address: 100);
        string najaIn = _profiles.GetProfile(TestData.Naja, null)!.Ports.First(p => p.Signal == SignalKind.Dmx512 && p.Direction == PortDirection.Input).Name;
        string najaOut = _profiles.GetProfile(TestData.Naja, null)!.Ports.First(p => p.Signal == SignalKind.Dmx512 && p.Direction == PortDirection.Output).Name;
        kit.Cable(CableCatalog.Ids.Ethernet, "E1", (desk, "Ethernet"), (node, "Ethernet In"));
        kit.Cable(CableCatalog.Ids.DmxXlr5, "A1", (node, "DMX A"), (a, najaIn));
        kit.Cable(CableCatalog.Ids.DmxXlr5, "B1", (node, "DMX B"), (b, najaIn));
        kit.Cable(CableCatalog.Ids.DmxXlr5, "B2", (b, najaOut), (wrong, najaIn));

        var solution = kit.Solve();
        Assert.Equal(1, solution.DataFeedOf(a.Id)!.FirstUniverse);
        Assert.Equal(2, solution.DataFeedOf(b.Id)!.FirstUniverse);
        Assert.Equal(SignalKind.Dmx512, solution.DataFeedOf(b.Id)!.Signal);
        Assert.Equal("Ethernet", solution.DataFeedOf(b.Id)!.ConsolePort);
        Assert.True(solution.IsConsoleDriven(a.Id) && solution.IsConsoleDriven(b.Id));
        // Patched on universe 1 but the line behind DMX B carries universe 2.
        var mismatch = solution.Issues.Single(IssueCodes.DataUniverseMismatch);
        Assert.Contains(wrong.Id, mismatch.Entities);
    }

    [Fact]
    public void A_dimmer_pack_distributes_dimmed_power_with_channel_limits()
    {
        var dimmer = GenericDevices.All.Single(d => d.Kind == GenericDeviceKind.Dimmer);
        var profile = _profiles.GetProfile(dimmer.FileName, null)!;
        Assert.NotNull(profile.Distribution);
        Assert.True(profile.Distribution!.OutputIsDimmed);
        var outputs = profile.Ports.Where(p => p.Role == PortRole.PowerDistribution).ToList();
        Assert.Equal(4, outputs.Count);
        Assert.Equal(new[] { 1, 2, 3, 4 }, outputs.Select(p => p.Channel));
        Assert.All(outputs, p => Assert.Equal(10f, p.MaxThroughCurrentA));
        Assert.Equal("CEE 32A", profile.Ports.Single(p => p.Role == PortRole.PowerIn).Connector.Id);

        var kit = new SceneKit(_profiles);
        var mains = kit.Source("Mains", Vector3.Zero, outlets: SceneKit.Outlet("CEE 32", "CEE 32A", 32f));
        var pack = kit.Fixture(dimmer.FileName, "Dimmers", 1, new Vector3(1, 0, 0), universe: 1);
        var loads = Enumerable.Range(0, 3).Select(i => kit.Fixture(TestData.Aeco20, $"AECO {i + 1}", 10 + i, new Vector3(3, i, 5))).ToList();
        kit.Cable(CableCatalog.Ids.Cee32, "Feed", (mains, "CEE 32"), (pack, "Power In"));
        string aecoIn = _profiles.GetProfile(TestData.Aeco20, null)!.Ports.First(p => p.Role == PortRole.PowerIn).Name;
        // Two AECO 20 on one 10 A channel overload it; the third sits alone on channel 2.
        kit.Cable(CableCatalog.Ids.Schuko, "D1", (pack, "Dimmer 1"), (loads[0], aecoIn), (loads[1], aecoIn));
        kit.Cable(CableCatalog.Ids.Schuko, "D2", (pack, "Dimmer 2"), (loads[2], aecoIn));

        var solution = kit.Solve();
        Assert.All(loads, l => Assert.True(solution.PowerFeedOf(l.Id)!.Dimmed));
        Assert.Equal(pack.Id, solution.PowerFeedOf(loads[2].Id)!.UpstreamEntityId);
        var channel = solution.Issues.Single(IssueCodes.PowerChannelOverload);
        Assert.Contains(pack.Id, channel.Entities);
        Assert.Contains(IssueCodes.PowerDimmedElectronicLoad, solution.Issues.Select(i => i.Code));
    }

    [Fact]
    public void Mains_supply_cables_cover_cee_125_powerlock_and_single_phase_32()
    {
        var catalog = CableCatalog.Default;
        foreach (var (id, connector, amps, phases) in new[]
                 {
                     (CableCatalog.Ids.Cee32, "CEE 32A", 32f, 1), (CableCatalog.Ids.Cee16ThreePhase, "CEE 16A 3P", 16f, 3),
                     (CableCatalog.Ids.Cee125ThreePhase, "CEE 125A 3P", 125f, 3), (CableCatalog.Ids.Powerlock, "Powerlock", 400f, 3),
                 })
        {
            var type = catalog.Find(id);
            Assert.NotNull(type);
            Assert.Equal(connector, type!.ConnectorA.Id);
            Assert.Equal(amps, type.RatedCurrentA);
            Assert.Equal(phases, type.Phases);
            Assert.Equal(phases, type.ConnectorA.Phases);
            Assert.True(type.WeightKgPerM > 0);
        }

        Assert.Equal("Powerlock", ConnectorCatalog.Resolve("powerlock 400A").Id);
        Assert.Equal("CEE 125A 3P", ConnectorCatalog.Resolve("CEE125").Id);

        // A 125 A supply feeding a three-phase distro through a Powerlock-to-CEE path is not a phase mismatch.
        var kit = new SceneKit(_profiles);
        var supply = kit.Source("Supply", Vector3.Zero, 400f, 50f, 3, SceneKit.Outlet("CEE 125", "CEE 125A 3P", 125f));
        var distro = kit.Source("Distro", new Vector3(5, 0, 0), 400f, 50f, 3, SceneKit.Outlet("Out 1", "CEE 32A 3P", 32f));
        kit.Cable(CableCatalog.Ids.Cee125ThreePhase, "Feeder", (supply, "CEE 125"), (distro, PortResolver.MainsInPortName));
        var solution = kit.Solve();
        solution.Issues.None(IssueCodes.PowerPhaseMismatch);
        Assert.True(solution.PowerSources.Single(s => s.SourceEntityId == distro.Id).IsPowered);
    }
}
