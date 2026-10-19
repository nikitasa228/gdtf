using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;
using Candela.Gdtf;

namespace Candela.Electrical.Tests;

/// <summary>
/// <see cref="DeviceCatalog"/>, the devices of the editor's Devices section: unique ids in five sections, fresh
/// components per placement, consoles whose outputs carry consecutive universes, mains supplies and distros whose
/// outlets have known connectors, breakers and phases, GDTF devices with ports, the power-distribution devices of the
/// fixture library, and the section of a scene entity.
/// </summary>
public sealed class DeviceCatalogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "candela-devices-" + Guid.NewGuid().ToString("N"));
    private readonly LibraryFixtureProfileProvider _profiles;

    public DeviceCatalogTests()
    {
        Directory.CreateDirectory(_dir);
        foreach (var device in GenericDevices.All)
            File.WriteAllBytes(Path.Combine(_dir, device.FileName), GenericDevices.GdtfBytes(device));
        _profiles = new LibraryFixtureProfileProvider(FixtureLibrary.Scan(_dir, TestData.Specs));
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
    public void Built_in_devices_have_unique_ids_in_every_section_and_are_found_in_any_case()
    {
        var all = DeviceCatalog.BuiltIn;
        Assert.Equal(all.Count, all.Select(d => d.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(Enum.GetValues<DeviceGroup>(), all.Select(d => d.Group).Distinct().Order());
        // Library order: the sections one after the other.
        Assert.Equal(all.Select(d => d.Group).Order(), all.Select(d => d.Group));
        Assert.Same(DeviceCatalog.Find("console-dmx-4"), DeviceCatalog.Find("CONSOLE-DMX-4"));
        Assert.Null(DeviceCatalog.Find(null));
        Assert.Null(DeviceCatalog.Find(" "));
        Assert.Null(DeviceCatalog.Find("console-dmx-3"));
        Assert.All(all, d =>
        {
            Assert.False(string.IsNullOrWhiteSpace(d.Name));
            Assert.False(string.IsNullOrWhiteSpace(d.ShortName));
            Assert.False(string.IsNullOrWhiteSpace(d.Description));
            Assert.True(d.SizeM.X > 0f && d.SizeM.Y > 0f && d.SizeM.Z > 0f, d.Id);
            Assert.Equal(d.Kind == DevicePresetKind.Gdtf, d.GdtfFile is not null);
            Assert.False(d.FromLibrary);
        });
    }

    [Fact]
    public void Every_placement_gets_its_own_component()
    {
        var preset = DeviceCatalog.Find("distro-32-schuko")!;
        var a = Assert.IsType<PowerSourceComponent>(preset.CreateComponent());
        var b = Assert.IsType<PowerSourceComponent>(preset.CreateComponent());
        Assert.NotSame(a, b);
        a.Outlets[0].BreakerA = 10f;
        Assert.Equal(16f, b.Outlets[0].BreakerA);
        var splitter = DeviceCatalog.Find("dmx-splitter-6")!;
        var gdtf = Assert.IsType<FixtureComponent>(splitter.CreateComponent());
        Assert.Equal(splitter.GdtfFile, gdtf.GdtfSpec);
        Assert.Equal(GenericDeviceKind.DmxSplitter, GenericDevices.Find(gdtf.GdtfSpec)!.Kind);
    }

    [Fact]
    public void Consoles_send_consecutive_universes_and_network_consoles_sixteen_over_ethercon()
    {
        foreach (int n in new[] { 2, 4, 8 })
        {
            var preset = DeviceCatalog.Find($"console-dmx-{n}")!;
            var console = Assert.IsType<ConsoleComponent>(preset.CreateComponent());
            Assert.Equal(Enumerable.Range(1, n), console.Outputs.Select(o => o.FirstUniverse));
            Assert.Equal(Enumerable.Range(0, n).Select(i => $"DMX {(char)('A' + i)}"), console.Outputs.Select(o => o.PortName));
            var ports = preset.Ports(null);
            Assert.All(ports, p => Assert.Equal(("XLR5", PortDirection.Output, SignalKind.Dmx512), (p.Connector.Id, p.Direction, p.Signal)));
        }

        foreach (var (id, protocol) in new[] { ("console-artnet-16", "artnet"), ("console-sacn-16", "sacn") })
        {
            var preset = DeviceCatalog.Find(id)!;
            var console = Assert.IsType<ConsoleComponent>(preset.CreateComponent());
            Assert.Equal(protocol, console.ProtocolId);
            var port = Assert.Single(preset.Ports(null));
            Assert.Equal((SignalKind.Ethernet, 1, 16), (port.Signal, port.Universe, port.UniverseCount));
            Assert.Equal("etherCON", port.Connector.Id);
        }
    }

    [Fact]
    public void Mains_and_distros_have_known_outlets_with_breakers_phases_and_a_mains_input()
    {
        foreach (var preset in DeviceCatalog.BuiltIn.Where(d => d.Kind == DevicePresetKind.PowerSource))
        {
            var source = Assert.IsType<PowerSourceComponent>(preset.CreateComponent());
            Assert.True(source.Phases is 1 or 3, preset.Id);
            Assert.NotEmpty(source.Outlets);
            Assert.All(source.Outlets, o =>
            {
                Assert.True(ConnectorCatalog.Resolve(o.Connector).IsKnown, $"{preset.Id}: {o.Connector}");
                Assert.True(o.BreakerA > 0f, preset.Id);
                Assert.InRange(o.Phase, 1, source.Phases);
            });
            var ports = preset.Ports(null);
            Assert.Single(ports, p => p.Name == PortResolver.MainsInPortName && p.Direction == PortDirection.Input);
            Assert.Equal(source.Outlets.Count, ports.Count(p => p.Role == PortRole.PowerOutlet));
            if (preset.Group == DeviceGroup.Mains)
                Assert.Single(source.Outlets);
        }

        // Single-phase outlets of a three-phase distro are spread over L1–L3.
        var schuko = Assert.IsType<PowerSourceComponent>(DeviceCatalog.Find("distro-32-schuko")!.CreateComponent());
        Assert.Equal(new[] { 1, 2, 3, 1, 2, 3 }, schuko.Outlets.Select(o => o.Phase));
    }

    [Fact]
    public void Gdtf_devices_have_ports_from_their_files()
    {
        foreach (var preset in DeviceCatalog.BuiltIn.Where(d => d.Kind == DevicePresetKind.Gdtf))
        {
            Assert.True(GenericDevices.IsGeneric(preset.GdtfFile), preset.Id);
            var ports = preset.Ports(_profiles);
            Assert.NotEmpty(ports);
            Assert.All(ports, p => Assert.True(p.Connector.IsKnown, $"{preset.Id}: {p.Name}"));
        }

        Assert.Empty(DeviceCatalog.Find("artnet-node-2")!.Ports(null)); // no profiles: unknown
        var dimmer = DeviceCatalog.Find("dimmer-4x10")!;
        Assert.Equal(DeviceGroup.Dimmer, dimmer.Group);
        Assert.Equal(4, dimmer.Ports(_profiles).Count(p => p.Role == PortRole.PowerDistribution));
    }

    [Fact]
    public void Power_distribution_devices_of_the_fixture_library_are_listed_newest_revision_first_by_maker()
    {
        Assert.Empty(DeviceCatalog.LibraryDevices(null));
        var devices = DeviceCatalog.LibraryDevices(TestData.Library);
        Assert.NotEmpty(devices);
        Assert.Contains(devices, d => d.GdtfFile == "PRstage@Dimmer_Pack_4@Ver_1_02.gdtf" && d.IconKey == "dimmer");
        Assert.All(devices, d =>
        {
            Assert.StartsWith(DeviceCatalog.LibraryIdPrefix, d.Id, StringComparison.Ordinal);
            Assert.Equal(DeviceGroup.Dimmer, d.Group);
            Assert.True(d.FromLibrary);
            Assert.False(GenericDevices.IsGeneric(d.GdtfFile));
            var entry = TestData.Library.FindByFileName(d.GdtfFile!)!;
            Assert.True(TestData.Library.IsLatestRevision(entry));
        });
        Assert.Equal(devices.Count, devices.Select(d => d.Id).Distinct().Count());
        var keys = devices.Select(d => (d.Manufacturer, d.Name)).ToList();
        var sorted = keys.OrderBy(k => k.Manufacturer, StringComparer.OrdinalIgnoreCase).ThenBy(k => k.Name, StringComparer.OrdinalIgnoreCase).ToList();
        Assert.Equal(sorted, keys);
    }

    [Fact]
    public void The_section_of_a_scene_entity_follows_its_components()
    {
        static Entity With(Component c)
        {
            var e = new Entity("e");
            e.Add(c);
            return e;
        }

        Assert.Equal(DeviceGroup.Console, DeviceCatalog.GroupOf(With(DeviceCatalog.Find("console-dmx-2")!.CreateComponent())));
        Assert.Equal(DeviceGroup.Mains, DeviceCatalog.GroupOf(With(DeviceCatalog.Find("mains-cee32-3p")!.CreateComponent())));
        Assert.Equal(DeviceGroup.Distro, DeviceCatalog.GroupOf(With(DeviceCatalog.Find("distro-125")!.CreateComponent())));
        Assert.Equal(DeviceGroup.DataDistribution, DeviceCatalog.GroupOf(With(DeviceCatalog.Find("artnet-node-8")!.CreateComponent())));
        Assert.Equal(DeviceGroup.Dimmer, DeviceCatalog.GroupOf(With(DeviceCatalog.Find("dimmer-4x10")!.CreateComponent())));
        Assert.Null(DeviceCatalog.GroupOf(With(new FixtureComponent { GdtfSpec = "Robe@Robin_T1@v1.gdtf" })));
        Assert.Null(DeviceCatalog.GroupOf(new Entity("empty")));
    }
}
