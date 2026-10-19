using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Security;
using System.Text;
using Candela.Gdtf;

namespace Candela.Electrical;

/// <summary>What a generic cabling device does.</summary>
public enum GenericDeviceKind
{
    /// <summary>A DMX splitter / opto-booster: one DMX input repeated on several isolated outputs (each starts a new DMX segment).</summary>
    DmxSplitter,

    /// <summary>An Art-Net / sACN node: an Ethernet input whose universes leave on DMX outputs (one universe per output).</summary>
    NetworkNode,

    /// <summary>A dimmer pack: mains in, dimmed mains channels out, controlled by DMX.</summary>
    Dimmer,
}

/// <summary>One connector of a <see cref="GenericDevice"/>, written into its GDTF as a WiringObject.</summary>
/// <param name="Name">Port name ("DMX In", "Dimmer 3", …), unique per device.</param>
/// <param name="Connector">Connector name of the <see cref="ConnectorCatalog"/> ("XLR5", "etherCON", "Schuko", …).</param>
/// <param name="Component">GDTF component type (input, output, consumer, power source, network input/output).</param>
/// <param name="Signal">GDTF SignalType text ("DMX512", "Ethernet", "Power").</param>
/// <param name="FuseCurrentA">Fuse of a power output in amperes, or 0.</param>
public sealed record GenericDevicePort(string Name, string Connector, WiringComponentType Component, string Signal, float FuseCurrentA = 0f);

/// <summary>
/// A generic device for the cable tool that no manufacturer GDTF describes: a DMX splitter, an Art-Net / sACN node or
/// a dimmer pack. Each is a real (small) GDTF file — a box with WiringObjects for its connectors and, for dimmers, one
/// DMX channel per dimmer channel — plus a researched-style <see cref="FixtureSpecs"/> record of typical values (power,
/// weight, distribution ratings), so the library, the DMX engine, the renderer and the electrical solver treat it like
/// any other device. Units: metres, watts, kilograms, amperes.
/// </summary>
public sealed record GenericDevice
{
    /// <summary>Stable id ("dmx-splitter-6").</summary>
    public required string Id { get; init; }

    /// <summary>GDTF file name in the Manufacturer@Model@Revision pattern ("Candela@Generic_DMX_Splitter_1x6@v1.gdtf").</summary>
    public required string FileName { get; init; }

    /// <summary>Model name ("DMX opto splitter 1 → 6").</summary>
    public required string Model { get; init; }

    /// <summary>Short label (GDTF ShortName).</summary>
    public required string ShortName { get; init; }

    /// <summary>What the device does.</summary>
    public required GenericDeviceKind Kind { get; init; }

    /// <summary>One-line English description.</summary>
    public required string Description { get; init; }

    /// <summary>Box size in metres: length (X) × width (Y) × height (Z).</summary>
    public required Vector3 SizeM { get; init; }

    /// <summary>Connectors in panel order (inputs first).</summary>
    public required IReadOnlyList<GenericDevicePort> Ports { get; init; }

    /// <summary>Own mains draw in W (for a dimmer: without its loads).</summary>
    public required double PowerW { get; init; }

    /// <summary>Weight in kg.</summary>
    public required double WeightKg { get; init; }

    /// <summary>DMX channels (one per dimmer channel; 0 for splitters and nodes, which are not patched).</summary>
    public int DmxChannels { get; init; }

    /// <summary>Dimmers: power the outputs may deliver in total, W.</summary>
    public double? MaxLoadW { get; init; }

    /// <summary>Dimmers: current limit per output channel, A.</summary>
    public double? MaxLoadPerChannelA { get; init; }

    /// <summary>Dimmers: total output current limit, A.</summary>
    public double? MaxLoadCurrentA { get; init; }

    /// <summary>Accepted mains networks (<c>_specs</c> vocabulary).</summary>
    public IReadOnlyList<string> PowerNetworks { get; init; } = GenericDevices.UniversalMains;

    /// <summary>Accepted input voltage range, V.</summary>
    public SpecRange VoltageRangeV { get; init; } = new(100, 240);

    /// <summary>GDTF FixtureTypeID (fixed per device, so projects refer to the same type everywhere).</summary>
    public required Guid FixtureTypeId { get; init; }

    /// <summary>The DMX mode's name.</summary>
    public string ModeName => DmxChannels > 0 ? $"{DmxChannels.ToString(CultureInfo.InvariantCulture)} channels" : "Default";

    /// <summary>Name of the mains input port.</summary>
    public string? PowerInputPort => Ports.FirstOrDefault(p => p.Component == WiringComponentType.Consumer)?.Name;
}

/// <summary>
/// The generic cabling devices (<see cref="All"/>): their GDTF files (<see cref="GdtfBytes"/>, written into a library
/// folder by the engine) and their specs records (<see cref="FindSpecs"/>, used by
/// <see cref="LibraryFixtureProfileProvider"/> when the specs index has no record of the file).
/// </summary>
public static class GenericDevices
{
    /// <summary>Manufacturer part of the generic devices' file names and GDTF Manufacturer attribute.</summary>
    public const string Manufacturer = "Candela";

    /// <summary>Mains networks of a universal (100–240 V) power supply.</summary>
    public static readonly IReadOnlyList<string> UniversalMains =
    [
        "100 V - 50 Hz", "100 V - 60 Hz", "110/120 V - 60 Hz", "110/120 V - 50 Hz", "220/230 V - 50 Hz", "220/230 V - 60 Hz", "240/245 V - 50 Hz",
    ];

    private static readonly IReadOnlyList<string> EuropeanMains = ["220/230 V - 50 Hz", "220/230 V - 60 Hz", "240/245 V - 50 Hz"];

    private static readonly DateTimeOffset ArchiveTime = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly Dictionary<string, (GenericDevice Device, FixtureSpecs Specs)> ByFile = Build();

    /// <summary>Every generic device, in library order (splitters, nodes, dimmers).</summary>
    public static IReadOnlyList<GenericDevice> All { get; } = ByFile.Values.Select(v => v.Device).ToArray();

    /// <summary>
    /// The generic device of a GDTF spec (file name with or without folder and <c>.gdtf</c>, any case), or null.
    /// </summary>
    public static GenericDevice? Find(string? gdtfSpec) => Lookup(gdtfSpec)?.Device;

    /// <summary>The specs record of a generic device's GDTF spec, or null for every other file.</summary>
    public static FixtureSpecs? FindSpecs(string? gdtfSpec) => Lookup(gdtfSpec)?.Specs;

    /// <summary>True when <paramref name="gdtfSpec"/> names a generic device.</summary>
    public static bool IsGeneric(string? gdtfSpec) => Lookup(gdtfSpec) is not null;

    /// <summary>The device's GDTF archive (description.xml only), byte for byte the same on every call.</summary>
    public static byte[] GdtfBytes(GenericDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("description.xml", CompressionLevel.Optimal);
            entry.LastWriteTime = ArchiveTime;
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(DescriptionXml(device));
        }
        return memory.ToArray();
    }

    /// <summary>
    /// The GDTF 1.2 <c>description.xml</c> of a device: a box body hanging below its origin (the GDTF convention, so the
    /// placement code stands it on floors like any fixture), one WiringObject per connector on its rear face, and one
    /// DMX mode (no channels for splitters and nodes; one Dimmer channel per output for dimmers).
    /// </summary>
    public static string DescriptionXml(GenericDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        var inv = CultureInfo.InvariantCulture;
        var size = device.SizeM;
        var sb = new StringBuilder(4096);
        sb.Append("""<?xml version="1.0" encoding="UTF-8" standalone="no"?>""").Append('\n');
        sb.Append("""<GDTF DataVersion="1.2">""").Append('\n');
        sb.Append(inv, $"""  <FixtureType Name="{Xml(device.Model)}" ShortName="{Xml(device.ShortName)}" LongName="{Xml(device.Model)}" Manufacturer="{Manufacturer}" Description="{Xml(device.Description)}" FixtureTypeID="{device.FixtureTypeId.ToString("D").ToUpperInvariant()}" Thumbnail="" RefFT="" CanHaveChildren="No">""").Append('\n');
        sb.Append("    <AttributeDefinitions>\n      <ActivationGroups/>\n");
        sb.Append("""      <FeatureGroups><FeatureGroup Name="Dimmer" Pretty="Dimmer"><Feature Name="Dimmer"/></FeatureGroup></FeatureGroups>""").Append('\n');
        sb.Append("""      <Attributes><Attribute Name="Dimmer" Pretty="Dim" Feature="Dimmer.Dimmer" PhysicalUnit="LuminousIntensity"/></Attributes>""").Append('\n');
        sb.Append("    </AttributeDefinitions>\n    <Wheels/>\n    <PhysicalDescriptions>\n");
        sb.Append(inv, $"""      <Properties><Weight Value="{device.WeightKg:0.###}"/><OperatingTemperature Low="0" High="40"/></Properties>""").Append('\n');
        sb.Append("    </PhysicalDescriptions>\n");
        sb.Append(inv, $"""    <Models><Model Name="Body" Length="{size.X:0.####}" Width="{size.Y:0.####}" Height="{size.Z:0.####}" PrimitiveType="Cube"/></Models>""").Append('\n');
        sb.Append("    <Geometries>\n");
        sb.Append(inv, $"""      <Geometry Name="Body" Model="Body" Position="{GdtfMatrix.Format(Matrix4x4.CreateTranslation(0f, 0f, -size.Z / 2f))}">""").Append('\n');
        for (int i = 0; i < device.Ports.Count; i++)
        {
            var p = device.Ports[i];
            // Connectors in a row on the rear face (−Y), inputs on the left.
            float x = device.Ports.Count == 1 ? 0f : (-0.42f + 0.84f * i / (device.Ports.Count - 1)) * size.X;
            var position = Matrix4x4.CreateTranslation(x, -size.Y / 2f, 0f);
            sb.Append(inv, $"""        <WiringObject Name="{Xml(p.Name)}" ConnectorType="{Xml(p.Connector)}" ComponentType="{p.Component}" SignalType="{Xml(p.Signal)}" PinCount="{Pins(p.Connector)}" Position="{GdtfMatrix.Format(position)}" """);
            if (p.Component == WiringComponentType.Consumer)
                sb.Append(inv, $"""ElectricalPayLoad="{device.PowerW:0.#}" VoltageRangeMin="{device.VoltageRangeV.Min:0}" VoltageRangeMax="{device.VoltageRangeV.Max:0}" FrequencyRangeMin="50" FrequencyRangeMax="60" CosPhi="0.95" """);
            if (p.FuseCurrentA > 0)
                sb.Append(inv, $"""FuseCurrent="{p.FuseCurrentA:0.#}" Voltage="230" """);
            sb.Append("/>\n");
        }
        for (int c = 1; c <= device.DmxChannels; c++)
            sb.Append(inv, $"""        <Geometry Name="Channel {c}" Position="{GdtfMatrix.Format(Matrix4x4.Identity)}"/>""").Append('\n');
        sb.Append("      </Geometry>\n    </Geometries>\n    <DMXModes>\n");
        sb.Append(inv, $"""      <DMXMode Name="{Xml(device.ModeName)}" Geometry="Body">""").Append('\n');
        sb.Append("        <DMXChannels>\n");
        for (int c = 1; c <= device.DmxChannels; c++)
        {
            sb.Append(inv, $"""          <DMXChannel DMXBreak="1" Offset="{c}" Highlight="255/1" Geometry="Channel {c}">""");
            sb.Append(inv, $"""<LogicalChannel Attribute="Dimmer"><ChannelFunction Name="Dimmer {c}" Attribute="Dimmer" DMXFrom="0/1" Default="0/1" PhysicalFrom="0" PhysicalTo="1"/></LogicalChannel></DMXChannel>""").Append('\n');
        }
        sb.Append("        </DMXChannels>\n        <Relations/>\n        <FTMacros/>\n      </DMXMode>\n    </DMXModes>\n");
        sb.Append("    <Revisions><Revision Text=\"Generic device of the Candela cable tool\" Date=\"2026-01-01T00:00:00\" UserID=\"0\"/></Revisions>\n");
        sb.Append("    <FTPresets/>\n    <Protocols/>\n  </FixtureType>\n</GDTF>\n");
        return sb.ToString();
    }

    private static int Pins(string connector) => ConnectorCatalog.Find(connector)?.Pins ?? 0;

    private static string Xml(string text) => SecurityElement.Escape(text) ?? "";

    private static (GenericDevice Device, FixtureSpecs Specs)? Lookup(string? gdtfSpec)
    {
        if (string.IsNullOrWhiteSpace(gdtfSpec)) return null;
        string name = Path.GetFileName(gdtfSpec.Trim());
        if (!name.EndsWith(".gdtf", StringComparison.OrdinalIgnoreCase)) name += ".gdtf";
        return ByFile.TryGetValue(name, out var hit) ? hit : null;
    }

    private static Dictionary<string, (GenericDevice, FixtureSpecs)> Build()
    {
        var map = new Dictionary<string, (GenericDevice, FixtureSpecs)>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in Definitions())
            map.Add(d.FileName, (d, SpecsOf(d)));
        return map;
    }

    private static IEnumerable<GenericDevice> Definitions()
    {
        const WiringComponentType In = WiringComponentType.Input, Out = WiringComponentType.Output;
        yield return new GenericDevice
        {
            Id = "dmx-splitter-6",
            FileName = "Candela@Generic_DMX_Splitter_1x6@v1.gdtf",
            Model = "DMX opto splitter 1 → 6",
            ShortName = "SPLIT6",
            Kind = GenericDeviceKind.DmxSplitter,
            Description = "19\" 1U opto-isolated DMX splitter / booster: one DMX input with thru, six isolated XLR 5-pin outputs, each the start of a new DMX line.",
            SizeM = new Vector3(0.483f, 0.2f, 0.044f),
            Ports =
            [
                new("Power In", "IEC C14", WiringComponentType.Consumer, "Power"),
                new("DMX In", "XLR5", In, "DMX512"),
                new("DMX Thru", "XLR5", Out, "DMX512"),
                new("DMX Out 1", "XLR5", Out, "DMX512"),
                new("DMX Out 2", "XLR5", Out, "DMX512"),
                new("DMX Out 3", "XLR5", Out, "DMX512"),
                new("DMX Out 4", "XLR5", Out, "DMX512"),
                new("DMX Out 5", "XLR5", Out, "DMX512"),
                new("DMX Out 6", "XLR5", Out, "DMX512"),
            ],
            PowerW = 15,
            WeightKg = 2.6,
            FixtureTypeId = new Guid("5B0E2C64-6F0A-4F7C-9A41-2C1D0E7A0601"),
        };
        yield return new GenericDevice
        {
            Id = "dmx-splitter-4-xlr3",
            FileName = "Candela@Generic_DMX_Splitter_1x4_XLR3@v1.gdtf",
            Model = "DMX splitter 1 → 4 (XLR 3-pin)",
            ShortName = "SPLIT4",
            Kind = GenericDeviceKind.DmxSplitter,
            Description = "Portable DMX splitter / opto-booster for truss or floor: XLR 3-pin input with thru, four isolated XLR 3-pin outputs, powerCON TRUE1 in and thru.",
            SizeM = new Vector3(0.24f, 0.12f, 0.06f),
            Ports =
            [
                new("Power In", "powerCON TRUE1", WiringComponentType.Consumer, "Power"),
                new("Power Thru", "powerCON TRUE1", WiringComponentType.PowerSource, "Power", 16f),
                new("DMX In", "XLR3", In, "DMX512"),
                new("DMX Thru", "XLR3", Out, "DMX512"),
                new("DMX Out 1", "XLR3", Out, "DMX512"),
                new("DMX Out 2", "XLR3", Out, "DMX512"),
                new("DMX Out 3", "XLR3", Out, "DMX512"),
                new("DMX Out 4", "XLR3", Out, "DMX512"),
            ],
            PowerW = 8,
            WeightKg = 1.2,
            FixtureTypeId = new Guid("5B0E2C64-6F0A-4F7C-9A41-2C1D0E7A0602"),
        };
        yield return new GenericDevice
        {
            Id = "artnet-node-2",
            FileName = "Candela@Generic_ArtNet_Node_2@v1.gdtf",
            Model = "Art-Net / sACN node 2 × DMX",
            ShortName = "NODE2",
            Kind = GenericDeviceKind.NetworkNode,
            Description = "Ethernet to DMX converter: the first two universes arriving on its etherCON input leave on DMX A and DMX B (XLR 5-pin); a second etherCON passes the network on.",
            SizeM = new Vector3(0.2f, 0.12f, 0.05f),
            Ports =
            [
                new("Power In", "powerCON TRUE1", WiringComponentType.Consumer, "Power"),
                new("Power Thru", "powerCON TRUE1", WiringComponentType.PowerSource, "Power", 16f),
                new("Ethernet In", "etherCON", WiringComponentType.NetworkInput, "Ethernet"),
                new("Ethernet Thru", "etherCON", WiringComponentType.NetworkOutput, "Ethernet"),
                new("DMX A", "XLR5", Out, "DMX512"),
                new("DMX B", "XLR5", Out, "DMX512"),
            ],
            PowerW = 8,
            WeightKg = 0.9,
            FixtureTypeId = new Guid("5B0E2C64-6F0A-4F7C-9A41-2C1D0E7A0603"),
        };
        yield return new GenericDevice
        {
            Id = "artnet-node-8",
            FileName = "Candela@Generic_ArtNet_Node_8@v1.gdtf",
            Model = "Art-Net / sACN node 8 × DMX",
            ShortName = "NODE8",
            Kind = GenericDeviceKind.NetworkNode,
            Description = "19\" 1U Ethernet to DMX converter: the first eight universes arriving on its etherCON input leave on DMX A … H (XLR 5-pin).",
            SizeM = new Vector3(0.483f, 0.22f, 0.044f),
            Ports =
            [
                new("Power In", "IEC C14", WiringComponentType.Consumer, "Power"),
                new("Ethernet In", "etherCON", WiringComponentType.NetworkInput, "Ethernet"),
                new("Ethernet Thru", "etherCON", WiringComponentType.NetworkOutput, "Ethernet"),
                new("DMX A", "XLR5", Out, "DMX512"),
                new("DMX B", "XLR5", Out, "DMX512"),
                new("DMX C", "XLR5", Out, "DMX512"),
                new("DMX D", "XLR5", Out, "DMX512"),
                new("DMX E", "XLR5", Out, "DMX512"),
                new("DMX F", "XLR5", Out, "DMX512"),
                new("DMX G", "XLR5", Out, "DMX512"),
                new("DMX H", "XLR5", Out, "DMX512"),
            ],
            PowerW = 20,
            WeightKg = 3.1,
            FixtureTypeId = new Guid("5B0E2C64-6F0A-4F7C-9A41-2C1D0E7A0604"),
        };
        yield return new GenericDevice
        {
            Id = "dimmer-4x10",
            FileName = "Candela@Generic_Dimmer_Pack_4x10A@v1.gdtf",
            Model = "Dimmer pack 4 × 10 A",
            ShortName = "DIM4",
            Kind = GenericDeviceKind.Dimmer,
            Description = "Four-channel phase-cut dimmer pack on a single-phase CEE 32 A supply: four Schuko outputs of 10 A (2.3 kW) each, 7.36 kW in total, one DMX channel per output.",
            SizeM = new Vector3(0.483f, 0.32f, 0.089f),
            Ports =
            [
                new("Power In", "CEE 32A", WiringComponentType.Consumer, "Power"),
                new("DMX In", "XLR5", In, "DMX512"),
                new("DMX Thru", "XLR5", Out, "DMX512"),
                new("Dimmer 1", "Schuko", WiringComponentType.PowerSource, "Power", 10f),
                new("Dimmer 2", "Schuko", WiringComponentType.PowerSource, "Power", 10f),
                new("Dimmer 3", "Schuko", WiringComponentType.PowerSource, "Power", 10f),
                new("Dimmer 4", "Schuko", WiringComponentType.PowerSource, "Power", 10f),
            ],
            PowerW = 25,
            WeightKg = 9.5,
            DmxChannels = 4,
            MaxLoadW = 7360,
            MaxLoadPerChannelA = 10,
            MaxLoadCurrentA = 32,
            PowerNetworks = EuropeanMains,
            VoltageRangeV = new SpecRange(200, 250),
            FixtureTypeId = new Guid("5B0E2C64-6F0A-4F7C-9A41-2C1D0E7A0605"),
        };
    }

    /// <summary>The specs record of a generic device: typical values of the device class, marked as estimates.</summary>
    private static FixtureSpecs SpecsOf(GenericDevice d)
    {
        var powerIn = d.Ports.FirstOrDefault(p => p.Component == WiringComponentType.Consumer)?.Connector;
        var powerThru = d.Ports.FirstOrDefault(p => p.Component == WiringComponentType.PowerSource && d.Kind != GenericDeviceKind.Dimmer)?.Connector;
        var data = d.Ports.Where(p => p.Signal is "DMX512" or "Ethernet").Select(p => p.Connector == "etherCON" ? "RJ45" : p.Connector).Distinct().ToArray();
        var control = new List<string>();
        if (d.Ports.Any(p => p.Signal == "Ethernet")) control.Add("Ethernet (Art-Net/sACN)");
        control.Add("DMX512");
        if (d.Kind != GenericDeviceKind.NetworkNode) control.Add("RDM");
        var estimated = new Dictionary<string, SpecSource>(StringComparer.Ordinal)
        {
            ["powerConsumptionW"] = SpecSource.Estimated,
            ["weightKg"] = SpecSource.Estimated,
            ["supportedPowerNetworks"] = SpecSource.Estimated,
            ["voltageRangeV"] = SpecSource.Estimated,
        };
        if (d.MaxLoadW is not null)
            foreach (var f in new[] { "maxLoadW", "maxLoadPerChannelA", "maxLoadCurrentA", "outputChannels", "outputVoltage" })
                estimated[f] = SpecSource.Estimated;
        return new FixtureSpecs
        {
            SchemaVersion = "1.0",
            File = d.FileName,
            FixtureTypeId = d.FixtureTypeId.ToString("D").ToUpperInvariant(),
            GdtfName = d.Model,
            LongName = d.Model,
            ShortName = d.ShortName,
            FullName = $"Generic {d.Model}",
            Manufacturer = Manufacturer,
            Description = d.Description,
            Category = FixtureCategory.Other,
            CategoryText = "OTHER",
            SubType = d.Kind switch
            {
                GenericDeviceKind.DmxSplitter => "DMX splitter / booster",
                GenericDeviceKind.NetworkNode => "Art-Net / sACN node",
                _ => "Dimmer pack",
            },
            SupportedPowerNetworks = d.PowerNetworks,
            VoltageRangeV = d.VoltageRangeV,
            FrequencyRangeHz = new SpecRange(50, 60),
            PowerConsumptionW = d.PowerW,
            PowerSupplyType = powerThru is null ? powerIn : $"{powerIn} + LINK",
            PowerConnectorIn = powerIn,
            PowerConnectorOut = powerThru,
            PowerLinkable = powerThru is not null,
            MaxInputPowerW = d.MaxLoadW is { } load ? load + d.PowerW : null,
            MaxLoadW = d.MaxLoadW,
            MaxLoadCurrentA = d.MaxLoadCurrentA,
            MaxLoadPerChannelA = d.MaxLoadPerChannelA,
            OutputChannels = d.Kind == GenericDeviceKind.Dimmer ? d.DmxChannels : null,
            OutputVoltage = d.Kind == GenericDeviceKind.Dimmer ? "230 VAC (dimmed / switched)" : null,
            WeightKg = d.WeightKg,
            NoiseLevelDb = 0,
            IpRating = "IP20",
            OperatingTemperatureC = new SpecRange(0, 40),
            ControlSignal = control,
            DataConnectors = data,
            DmxModes = [new SpecDmxMode(d.ModeName, d.DmxChannels)],
            Source = estimated,
            Issues = ["Generic device: typical values of the device class, not a manufacturer's datasheet."],
        };
    }
}
