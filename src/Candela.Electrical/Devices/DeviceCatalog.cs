using System.Numerics;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;
using Candela.Gdtf;

namespace Candela.Electrical;

/// <summary>Groups of the Devices section of the library (the order is the order of the section).</summary>
public enum DeviceGroup
{
    /// <summary>Lighting consoles: DMX outputs, or a network port carrying many universes (Art-Net, sACN).</summary>
    Console,

    /// <summary>Art-Net / sACN nodes and DMX splitters / opto-boosters.</summary>
    DataDistribution,

    /// <summary>Mains supplies: a wall socket, CEE outlets, a Powerlock tie-in or generator.</summary>
    Mains,

    /// <summary>Power distros: fed through their mains input, they split one supply into protected outlets.</summary>
    Distro,

    /// <summary>Dimmers, power supplies and power/data injectors (distribution devices with their own GDTF).</summary>
    Dimmer,
}

/// <summary>How a <see cref="DevicePreset"/> becomes a scene entity.</summary>
public enum DevicePresetKind
{
    /// <summary>A <see cref="ConsoleComponent"/> (outputs → universes).</summary>
    Console,

    /// <summary>A <see cref="PowerSourceComponent"/> (outlets with breakers; mains supplies and distros).</summary>
    PowerSource,

    /// <summary>A <see cref="FixtureComponent"/> of a GDTF file (generic devices and the library's power devices).</summary>
    Gdtf,
}

/// <summary>
/// One entry of the Devices section of the library: what it is (<see cref="Group"/>, <see cref="Kind"/>), a name and a
/// description (English; the editor translates them by <see cref="Id"/>), the box it is drawn and placed as, and a
/// factory for the component that makes an entity the device. Consoles and power sources are Core components; nodes,
/// splitters, dimmers and PSUs are GDTF devices (<see cref="GenericDevices"/> or library files with
/// distribution data), so the DMX engine, the renderer and the electrical solver treat them like any fixture.
/// Units: metres, volts, hertz, amperes.
/// </summary>
public sealed record DevicePreset
{
    /// <summary>Stable id ("console-dmx-4", "mains-cee32-3p", "gdtf:PRstage@Dimmer_Pack_4@Ver_1_02.gdtf").</summary>
    public required string Id { get; init; }

    /// <summary>Section of the library.</summary>
    public required DeviceGroup Group { get; init; }

    /// <summary>How the device is represented in the scene.</summary>
    public required DevicePresetKind Kind { get; init; }

    /// <summary>English name (also the base of the entity name).</summary>
    public required string Name { get; init; }

    /// <summary>Short English name for new entities ("Console", "Distro").</summary>
    public required string ShortName { get; init; }

    /// <summary>One-line English description.</summary>
    public required string Description { get; init; }

    /// <summary>Icon key for the library tile ("console", "network-console", "node", "splitter", "mains", "distro", "dimmer", "psu").</summary>
    public required string IconKey { get; init; }

    /// <summary>Size of the device's box in metres (X × Y × Z), for the placement preview.</summary>
    public required Vector3 SizeM { get; init; }

    /// <summary>GDTF file of a <see cref="DevicePresetKind.Gdtf"/> device (the library file name), else null.</summary>
    public string? GdtfFile { get; init; }

    /// <summary>Manufacturer shown on the tile (Candela for the built-in devices).</summary>
    public string Manufacturer { get; init; } = GenericDevices.Manufacturer;

    /// <summary>True for devices the user's library provides (power devices with a manufacturer GDTF).</summary>
    public bool FromLibrary { get; init; }

    internal Func<Component>? Factory { get; init; }

    /// <summary>
    /// A new component that makes an entity this device: a <see cref="ConsoleComponent"/>, a
    /// <see cref="PowerSourceComponent"/>, or a <see cref="FixtureComponent"/> of <see cref="GdtfFile"/> (mode and patch
    /// are left to the caller). Every call returns a fresh instance.
    /// </summary>
    public Component CreateComponent() =>
        Factory?.Invoke() ?? new FixtureComponent { GdtfSpec = GdtfFile ?? "" };

    /// <summary>
    /// The ports the device will have: console outputs and power outlets from the component, a GDTF device's from its
    /// electrical profile (<paramref name="profiles"/>; empty when it cannot be resolved).
    /// </summary>
    public IReadOnlyList<Port> Ports(IFixtureProfileProvider? profiles)
    {
        var component = CreateComponent();
        return component switch
        {
            ConsoleComponent console => PortResolver.FromConsole(console),
            PowerSourceComponent source => PortResolver.FromPowerSource(source),
            FixtureComponent fixture when profiles is not null && !string.IsNullOrWhiteSpace(fixture.GdtfSpec) =>
                profiles.GetProfile(fixture.GdtfSpec, fixture.GdtfMode)?.Ports ?? Array.Empty<Port>(),
            _ => Array.Empty<Port>(),
        };
    }
}

/// <summary>
/// The devices the cable tool connects, as offered by the Devices section of the library (<see cref="BuiltIn"/>):
/// consoles with 2/4/8 DMX outputs and network consoles (Art-Net, sACN) whose etherCON port carries 16 universes; the
/// generic Art-Net nodes and DMX splitters of <see cref="GenericDevices"/>; mains supplies (Schuko 16 A, CEE 16 A,
/// CEE 32/63/125 A three phase, Powerlock 400 A); power distros fed through their <c>Mains In</c> (outlets with
/// breakers, single-phase outlets spread over L1–L3); the generic dimmer pack; and — from the user's fixture library —
/// every device whose <c>_specs</c> record carries distribution ratings (<see cref="LibraryDevices"/>: dimmer packs,
/// P3 PowerPorts, PSUs, feeders).
/// </summary>
public static class DeviceCatalog
{
    /// <summary>Size of a console desk in metres (drawn by the engine as a box of this size standing on its origin).</summary>
    public static readonly Vector3 ConsoleSize = new(1.2f, 0.7f, 0.25f);

    /// <summary>Size of a power source / distro box in metres (drawn by the engine standing on its origin).</summary>
    public static readonly Vector3 PowerSourceSize = new(0.45f, 0.3f, 0.6f);

    /// <summary>Prefix of the ids of library devices ("gdtf:" + file name).</summary>
    public const string LibraryIdPrefix = "gdtf:";

    private static readonly IReadOnlyList<DevicePreset> s_builtIn = Build().ToArray();

    /// <summary>The built-in devices in library order (consoles, nodes and splitters, mains, distros, dimmers).</summary>
    public static IReadOnlyList<DevicePreset> BuiltIn => s_builtIn;

    /// <summary>A built-in device by id (any case), or null.</summary>
    public static DevicePreset? Find(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        foreach (var p in s_builtIn)
            if (string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)) return p;
        return null;
    }

    /// <summary>
    /// Power-distribution devices of the fixture library: the newest revision of every model whose specs record has
    /// <c>maxLoadW</c>, <c>maxInputPowerW</c> or <c>outputChannels</c> (dimmer packs, P3 PowerPorts, PSUs, feeders),
    /// sorted by manufacturer and model. Generic devices are left out (they are in <see cref="BuiltIn"/>).
    /// </summary>
    public static IReadOnlyList<DevicePreset> LibraryDevices(FixtureLibrary? library)
    {
        var list = new List<DevicePreset>();
        if (library is null) return list;
        foreach (var entry in library.Entries)
        {
            if (entry is null || GenericDevices.IsGeneric(entry.FileName) || !library.IsLatestRevision(entry)) continue;
            var specs = entry.Specs;
            if (specs is null || (specs.MaxLoadW is null && specs.MaxInputPowerW is null && specs.OutputChannels is null)) continue;
            string sub = specs.SubType ?? "";
            bool dimmer = sub.Contains("dimmer", StringComparison.OrdinalIgnoreCase);
            string file = entry.FileName;
            list.Add(new DevicePreset
            {
                Id = LibraryIdPrefix + file,
                Group = DeviceGroup.Dimmer,
                Kind = DevicePresetKind.Gdtf,
                Name = entry.ModelName,
                ShortName = entry.ModelName,
                Description = string.IsNullOrWhiteSpace(sub) ? "Power distribution device" : sub,
                IconKey = dimmer ? "dimmer" : "psu",
                SizeM = new Vector3(0.48f, 0.3f, 0.09f),
                GdtfFile = file,
                Manufacturer = entry.Manufacturer,
                FromLibrary = true,
            });
        }
        list.Sort((a, b) =>
        {
            int c = string.Compare(a.Manufacturer, b.Manufacturer, StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });
        return list;
    }

    /// <summary>
    /// The library section a scene entity belongs to, judged by its components (for icons): consoles, mains (one
    /// outlet) or distros (several), generic nodes/splitters and dimmers; null for anything else.
    /// </summary>
    public static DeviceGroup? GroupOf(Entity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (entity.Has<ConsoleComponent>()) return DeviceGroup.Console;
        if (entity.Get<PowerSourceComponent>() is { } source)
            return source.Outlets.Count > 1 ? DeviceGroup.Distro : DeviceGroup.Mains;
        if (entity.Get<FixtureComponent>() is { } fixture && GenericDevices.Find(fixture.GdtfSpec) is { } generic)
            return generic.Kind == GenericDeviceKind.Dimmer ? DeviceGroup.Dimmer : DeviceGroup.DataDistribution;
        return null;
    }

    private static IEnumerable<DevicePreset> Build()
    {
        // ---- consoles
        yield return DmxConsole("console-dmx-2", 2, "Small DMX console with two XLR 5-pin outputs (universes 1–2), fed by Art-Net from the external control input.");
        yield return DmxConsole("console-dmx-4", 4, "DMX console with four XLR 5-pin outputs (universes 1–4), fed by Art-Net from the external control input.");
        yield return DmxConsole("console-dmx-8", 8, "Large DMX console with eight XLR 5-pin outputs (universes 1–8), fed by Art-Net from the external control input.");
        yield return NetworkConsole("console-artnet-16", "artnet", "Network console (Art-Net)",
            "Console whose etherCON port sends 16 universes as Art-Net; Art-Net nodes behind it turn them into DMX lines.");
        yield return NetworkConsole("console-sacn-16", "sacn", "Network console (sACN)",
            "Console whose etherCON port sends 16 universes as sACN (E1.31); nodes behind it turn them into DMX lines.");

        // ---- nodes and splitters (generic GDTF devices)
        foreach (var d in GenericDevices.All)
        {
            if (d.Kind == GenericDeviceKind.Dimmer) continue;
            bool node = d.Kind == GenericDeviceKind.NetworkNode;
            yield return Generic(d, DeviceGroup.DataDistribution, node ? "node" : "splitter", node ? "Node" : "Splitter");
        }

        // ---- mains supplies
        yield return Mains("mains-schuko-16", "Wall socket Schuko 16 A", "Socket",
            "A 230 V wall socket on a 16 A breaker.", 1, ("Socket", "Schuko", 16f, 1));
        yield return Mains("mains-cee16", "Mains CEE 16 A (230 V)", "Mains",
            "A blue CEE 16 A outlet: 230 V single phase on a 16 A breaker.", 1, ("CEE 16 A", "CEE 16A", 16f, 1));
        yield return Mains("mains-cee32", "Mains CEE 32 A (230 V)", "Mains",
            "A blue CEE 32 A outlet: 230 V single phase on a 32 A breaker.", 1, ("CEE 32 A", "CEE 32A", 32f, 1));
        yield return Mains("mains-cee32-3p", "Mains CEE 32 A three phase", "Mains",
            "A red CEE 32 A outlet: 400 V three phase (230 V to neutral) on a 32 A breaker.", 3, ("CEE 32 A 3~", "CEE 32A 3P", 32f, 1));
        yield return Mains("mains-cee63-3p", "Mains CEE 63 A three phase", "Mains",
            "A red CEE 63 A outlet: 400 V three phase on a 63 A breaker.", 3, ("CEE 63 A 3~", "CEE 63A 3P", 63f, 1));
        yield return Mains("mains-cee125-3p", "Mains CEE 125 A three phase", "Mains",
            "A red CEE 125 A outlet: 400 V three phase on a 125 A breaker.", 3, ("CEE 125 A 3~", "CEE 125A 3P", 125f, 1));
        yield return Mains("mains-powerlock-400", "Powerlock 400 A supply", "Supply",
            "A 400 A three-phase tie-in or generator on Powerlock single-pole connectors.", 3, ("Powerlock", "Powerlock", 400f, 1));

        // ---- distros
        yield return Distro("distro-16-true1", "Distro 16 A → 4 × TRUE1", 1,
            "Single-phase distro for a truss: 16 A in, four powerCON TRUE1 outlets on 10 A breakers.",
            Outlets("TRUE1", "powerCON TRUE1", 10f, 4, phases: 1));
        yield return Distro("distro-32-schuko", "Distro 32 A 3~ → 6 × Schuko", 3,
            "Three-phase distro: CEE 32 A in, six Schuko outlets on 16 A breakers spread over L1–L3.",
            Outlets("Schuko", "Schuko", 16f, 6, phases: 3));
        yield return Distro("distro-32-true1", "Distro 32 A 3~ → 6 × TRUE1", 3,
            "Three-phase distro: CEE 32 A in, six powerCON TRUE1 outlets on 16 A breakers spread over L1–L3.",
            Outlets("TRUE1", "powerCON TRUE1", 16f, 6, phases: 3));
        yield return Distro("distro-63-socapex", "Distro 63 A 3~ → 2 × Socapex + 3 × CEE 16 A", 3,
            "Three-phase dimmer-room distro: CEE 63 A in, two Socapex outlets (6 circuits of 16 A each) and three CEE 16 A outlets.",
            [.. Outlets("Socapex", "Socapex", 16f, 2, phases: 1), .. Outlets("CEE 16 A", "CEE 16A", 16f, 3, phases: 3)]);
        yield return Distro("distro-125", "Distro 125 A 3~ → 2 × CEE 63 A + 2 × CEE 32 A", 3,
            "Main distro: CEE 125 A in, two CEE 63 A and two CEE 32 A three-phase outlets for the next distros.",
            [.. Outlets("CEE 63 A 3~", "CEE 63A 3P", 63f, 2, phases: 1), .. Outlets("CEE 32 A 3~", "CEE 32A 3P", 32f, 2, phases: 1)]);
        yield return Distro("distro-powerlock-400", "Distro 400 A → 2 × CEE 125 A + 2 × CEE 63 A", 3,
            "Generator distro: Powerlock 400 A in, two CEE 125 A and two CEE 63 A three-phase outlets.",
            [.. Outlets("CEE 125 A 3~", "CEE 125A 3P", 125f, 2, phases: 1), .. Outlets("CEE 63 A 3~", "CEE 63A 3P", 63f, 2, phases: 1)]);

        // ---- dimmers (generic)
        foreach (var d in GenericDevices.All)
        {
            if (d.Kind == GenericDeviceKind.Dimmer)
                yield return Generic(d, DeviceGroup.Dimmer, "dimmer", "Dimmer");
        }
    }

    private static DevicePreset DmxConsole(string id, int outputs, string description) => new()
    {
        Id = id,
        Group = DeviceGroup.Console,
        Kind = DevicePresetKind.Console,
        Name = $"DMX console, {outputs} universes",
        ShortName = "Console",
        Description = description,
        IconKey = "console",
        SizeM = ConsoleSize,
        Factory = () =>
        {
            var console = new ConsoleComponent { Model = $"DMX console, {outputs} universes", ProtocolId = "artnet", Outputs = new List<ConsoleOutput>(outputs) };
            for (int i = 0; i < outputs; i++)
                console.Outputs.Add(new ConsoleOutput { PortName = $"DMX {(char)('A' + i)}", Signal = "DMX512", FirstUniverse = i + 1, UniverseCount = 1 });
            return console;
        },
    };

    private static DevicePreset NetworkConsole(string id, string protocol, string name, string description) => new()
    {
        Id = id,
        Group = DeviceGroup.Console,
        Kind = DevicePresetKind.Console,
        Name = name,
        ShortName = "Console",
        Description = description,
        IconKey = "network-console",
        SizeM = ConsoleSize,
        Factory = () => new ConsoleComponent
        {
            Model = name,
            ProtocolId = protocol,
            Outputs = new List<ConsoleOutput>
            {
                new() { PortName = "Network", Signal = "Ethernet", FirstUniverse = 1, UniverseCount = 16 },
            },
        },
    };

    private static DevicePreset Generic(GenericDevice d, DeviceGroup group, string icon, string shortName) => new()
    {
        Id = d.Id,
        Group = group,
        Kind = DevicePresetKind.Gdtf,
        Name = d.Model,
        ShortName = shortName,
        Description = d.Description,
        IconKey = icon,
        SizeM = d.SizeM,
        GdtfFile = d.FileName,
        Factory = () => new FixtureComponent { GdtfSpec = d.FileName, GdtfMode = d.ModeName },
    };

    private static DevicePreset Mains(string id, string name, string shortName, string description, int phases,
        (string Port, string Connector, float Breaker, int Phase) outlet) => new()
    {
        Id = id,
        Group = DeviceGroup.Mains,
        Kind = DevicePresetKind.PowerSource,
        Name = name,
        ShortName = shortName,
        Description = description,
        IconKey = "mains",
        SizeM = PowerSourceSize,
        Factory = () => new PowerSourceComponent
        {
            VoltageV = 230f,
            FrequencyHz = 50f,
            Phases = phases,
            Outlets = new List<PowerOutlet>
            {
                new() { PortName = outlet.Port, Connector = outlet.Connector, BreakerA = outlet.Breaker, Phase = outlet.Phase },
            },
        },
    };

    private static DevicePreset Distro(string id, string name, int phases, string description, (string, string, float, int)[] outlets) => new()
    {
        Id = id,
        Group = DeviceGroup.Distro,
        Kind = DevicePresetKind.PowerSource,
        Name = name,
        ShortName = "Distro",
        Description = description,
        IconKey = "distro",
        SizeM = PowerSourceSize,
        Factory = () =>
        {
            var source = new PowerSourceComponent { VoltageV = 230f, FrequencyHz = 50f, Phases = phases, Outlets = new List<PowerOutlet>(outlets.Length) };
            foreach (var (port, connector, breaker, phase) in outlets)
                source.Outlets.Add(new PowerOutlet { PortName = port, Connector = connector, BreakerA = breaker, Phase = phase });
            return source;
        },
    };

    /// <summary>
    /// <paramref name="count"/> outlets named "<paramref name="name"/> n"; with three <paramref name="phases"/> they
    /// rotate over L1–L3 (single-phase loads spread evenly), otherwise they are all on L1. Numbering continues across
    /// calls only through the names, so callers pass distinct names.
    /// </summary>
    private static (string, string, float, int)[] Outlets(string name, string connector, float breaker, int count, int phases)
    {
        var list = new (string, string, float, int)[count];
        for (int i = 0; i < count; i++)
            list[i] = ($"{name} {i + 1}", connector, breaker, phases == 3 ? i % 3 + 1 : 1);
        return list;
    }
}
