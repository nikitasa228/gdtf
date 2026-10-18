using System.Globalization;
using System.Text.RegularExpressions;
using Candela.Gdtf;

namespace Candela.Electrical;

/// <summary>Where a fixture's power draw comes from, from most to least reliable.</summary>
public enum PowerDataSource
{
    /// <summary>Not known: the fixture is treated as drawing nothing.</summary>
    Unknown,

    /// <summary><c>_specs</c> <c>powerConsumptionW</c> (researched maximum mains draw).</summary>
    Specs,

    /// <summary>GDTF 1.2 <c>PowerConsumption</c> (Value × PowerFactor).</summary>
    GdtfPowerConsumption,

    /// <summary>GDTF WiringObject consumers' <c>ElectricalPayLoad</c>.</summary>
    GdtfWiring,

    /// <summary>Sum of the GDTF beams' <c>PowerConsumption</c> (an estimate: lamp power, not mains draw).</summary>
    GdtfBeams,
}

/// <summary>Where a fixture's weight comes from.</summary>
public enum WeightDataSource
{
    /// <summary>Not known.</summary>
    Unknown,

    /// <summary><c>_specs</c> <c>weightKg</c>.</summary>
    Specs,

    /// <summary>GDTF <c>Properties/Weight</c>.</summary>
    Gdtf,
}

/// <summary>One entry of <c>_specs</c> <c>supportedPowerNetworks</c>, e.g. "220/230 V - 50 Hz".</summary>
/// <param name="NominalVoltagesV">Nominal voltages of the entry (220 and 230 for "220/230 V").</param>
/// <param name="FrequencyHz">Mains frequency.</param>
/// <param name="Text">The entry as written.</param>
public sealed record PowerNetworkSpec(IReadOnlyList<float> NominalVoltagesV, float FrequencyHz, string Text)
{
    /// <summary>Relative voltage tolerance when matching a supply against a nominal voltage (±6 %).</summary>
    public const float VoltageTolerance = 0.06f;

    private static readonly Regex Pattern = new(@"^\s*(?<v>[\d./\s]+)\s*V\s*-\s*(?<f>[\d.]+)\s*Hz\s*$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>Parses an entry; null when the text does not have the "V1/V2 V - F Hz" form.</summary>
    public static PowerNetworkSpec? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = Pattern.Match(text);
        if (!m.Success || !float.TryParse(m.Groups["f"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float f)) return null;
        var volts = new List<float>(2);
        foreach (var part in m.Groups["v"].Value.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (float.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) && v > 0) volts.Add(v);
        return volts.Count == 0 ? null : new PowerNetworkSpec(volts, f, text.Trim());
    }

    /// <summary>True when a supply of <paramref name="voltageV"/> / <paramref name="frequencyHz"/> is this network.</summary>
    public bool Matches(float voltageV, float frequencyHz)
    {
        if (MathF.Abs(frequencyHz - FrequencyHz) > 1f) return false;
        foreach (var v in NominalVoltagesV)
            if (MathF.Abs(voltageV - v) <= VoltageTolerance * v) return true;
        return false;
    }
}

/// <summary>
/// Ratings of a power-distribution device (dimmer pack, DC power supply, P3 PowerPort, feeder) from
/// <c>_specs</c> (see "power distribution" in <c>_specs/SCHEMA.md</c>). The loads behind it are their own devices;
/// <see cref="OwnPowerW"/> is the device's own draw only.
/// </summary>
public sealed record DistributionInfo
{
    /// <summary>Own draw in W (<c>powerConsumptionW</c>), without the loads.</summary>
    public double? OwnPowerW { get; init; }

    /// <summary>Worst-case mains draw with all outputs fully loaded, in W.</summary>
    public double? MaxInputPowerW { get; init; }

    /// <summary>Maximum power deliverable to the loads, in W.</summary>
    public double? MaxLoadW { get; init; }

    /// <summary>Total output current limit in A.</summary>
    public double? MaxLoadCurrentA { get; init; }

    /// <summary>Per-output current limit in A.</summary>
    public double? MaxLoadPerChannelA { get; init; }

    /// <summary>Number of outputs / dimmer channels (at least 1).</summary>
    public int OutputChannels { get; init; } = 1;

    /// <summary>What the outputs supply as written ("48 VDC", "230 VAC (dimmed / switched)").</summary>
    public string? OutputVoltageText { get; init; }

    /// <summary>Output voltage in V for DC outputs; null for AC outputs (they pass the supply voltage on).</summary>
    public float? OutputVoltageDcV { get; init; }

    /// <summary>True when the outputs are dimmed mains (phase-cut dimmer channels).</summary>
    public bool OutputIsDimmed { get; init; }

    /// <summary>True when the outputs supply DC.</summary>
    public bool OutputIsDc => OutputVoltageDcV is not null;

    /// <summary>
    /// Conversion losses: mains power per watt delivered to the loads, ≥ 1. Derived from
    /// (<see cref="MaxInputPowerW"/> − <see cref="OwnPowerW"/>) / <see cref="MaxLoadW"/> when all three are known
    /// (e.g. 1.15 for a P3 PowerPort 2000), otherwise 1.
    /// </summary>
    public double LossFactor
    {
        get
        {
            if (MaxInputPowerW is not { } maxIn || MaxLoadW is not { } maxLoad || maxLoad <= 0) return 1.0;
            double f = (maxIn - (OwnPowerW ?? 0)) / maxLoad;
            return double.IsFinite(f) ? Math.Clamp(f, 1.0, 2.0) : 1.0;
        }
    }

    private static readonly Regex VoltagePattern = new(@"(?<v>\d+(\.\d+)?)\s*V\s*(?<kind>DC|AC)?", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>
    /// Distribution data of a specs record, or null when the record describes an ordinary fixture (no
    /// maxLoadW / maxInputPowerW / outputChannels / outputVoltage).
    /// </summary>
    public static DistributionInfo? FromSpecs(FixtureSpecs? specs)
    {
        if (specs is null) return null;
        if (specs.MaxLoadW is null && specs.MaxInputPowerW is null && specs.OutputChannels is null && specs.OutputVoltage is null) return null;
        float? dc = null;
        bool dimmed = false;
        if (specs.OutputVoltage is { } text)
        {
            var m = VoltagePattern.Match(text);
            if (m.Success && m.Groups["kind"].Value.Equals("DC", StringComparison.OrdinalIgnoreCase) &&
                float.TryParse(m.Groups["v"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) && v > 0)
                dc = v;
            dimmed = text.Contains("dimmed", StringComparison.OrdinalIgnoreCase);
        }
        return new DistributionInfo
        {
            OwnPowerW = Positive(specs.PowerConsumptionW),
            MaxInputPowerW = Positive(specs.MaxInputPowerW),
            MaxLoadW = Positive(specs.MaxLoadW),
            MaxLoadCurrentA = Positive(specs.MaxLoadCurrentA),
            MaxLoadPerChannelA = Positive(specs.MaxLoadPerChannelA),
            OutputChannels = Math.Max(1, specs.OutputChannels ?? 1),
            OutputVoltageText = specs.OutputVoltage,
            OutputVoltageDcV = dc,
            OutputIsDimmed = dimmed,
        };
    }

    private static double? Positive(double? v) => v is { } x && double.IsFinite(x) && x > 0 ? x : null;
}

/// <summary>
/// Everything the electrical and rigging checks need to know about one fixture type (in one DMX mode): its ports,
/// power draw, power factor, weight, accepted mains networks and distribution ratings. Built once per
/// GDTF spec + mode from the parsed GDTF and the <c>_specs</c> record and then shared (immutable).
/// </summary>
public sealed class FixtureProfile
{
    /// <summary>Power factor assumed when neither GDTF nor specs give one.</summary>
    public const float DefaultPowerFactor = 0.95f;

    /// <summary>The GDTF spec the profile was requested for (as stored in <c>FixtureComponent.GdtfSpec</c>).</summary>
    public required string GdtfSpec { get; init; }

    /// <summary>DMX mode the ports were taken from (null = first mode).</summary>
    public string? Mode { get; init; }

    /// <summary>Library file that was used, when the spec was resolved through a library.</summary>
    public string? ResolvedFile { get; init; }

    /// <summary>True when the library returned a substitute model (the requested GDTF is not in the library).</summary>
    public bool IsSubstitute { get; init; }

    /// <summary>Manufacturer and model for messages ("Robe Lighting Robin T1 Profile").</summary>
    public required string DisplayName { get; init; }

    /// <summary>Library category.</summary>
    public FixtureCategory Category { get; init; } = FixtureCategory.Other;

    /// <summary>LED, Discharge Lamp, Tungsten Lamp, Halogen Lamp or Laser (from specs), or null.</summary>
    public string? LightSourceType { get; init; }

    /// <summary>The device's ports.</summary>
    public required IReadOnlyList<Port> Ports { get; init; }

    /// <summary>Maximum own power draw in W (for distribution devices: own draw only), or null when unknown.</summary>
    public double? PowerW { get; init; }

    /// <summary>Where <see cref="PowerW"/> comes from.</summary>
    public PowerDataSource PowerSource { get; init; }

    /// <summary>Power factor (cos φ) used to turn W into VA and amperes.</summary>
    public float PowerFactor { get; init; } = DefaultPowerFactor;

    /// <summary>Weight in kg, or null when unknown.</summary>
    public double? WeightKg { get; init; }

    /// <summary>Where <see cref="WeightKg"/> comes from.</summary>
    public WeightDataSource WeightSource { get; init; }

    /// <summary>Accepted mains networks (from specs).</summary>
    public IReadOnlyList<PowerNetworkSpec> SupportedNetworks { get; init; } = Array.Empty<PowerNetworkSpec>();

    /// <summary>Accepted input voltage range in V (specs, else GDTF), or null.</summary>
    public SpecRange? VoltageRangeV { get; init; }

    /// <summary>Accepted mains frequency range in Hz (specs, else GDTF), or null.</summary>
    public SpecRange? FrequencyRangeHz { get; init; }

    /// <summary>Ratings of a power-distribution device, or null for ordinary fixtures.</summary>
    public DistributionInfo? Distribution { get; init; }

    /// <summary>Runs from an internal battery (no mains connection needed).</summary>
    public bool BatteryPowered { get; init; }

    /// <summary>Problems met while building the profile (GDTF could not be read, …).</summary>
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

    /// <summary>True when the fixture needs mains power to work.</summary>
    public bool NeedsPower => !BatteryPowered && (Ports.Any(p => p.Role == PortRole.PowerIn) || (PowerW ?? 0) > 0);

    /// <summary>Port by name (case-insensitive), or null.</summary>
    public Port? FindPort(string? name) =>
        name is null ? null : Ports.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Checks whether the fixture accepts a supply. AC: the supply must match one of the
    /// <see cref="SupportedNetworks"/> (nominal voltage ±6 %, frequency ±1 Hz) or lie inside the voltage and
    /// frequency ranges (±3 % / ±1 Hz); low-voltage devices (range below 90 V) are judged by their range only.
    /// DC: the voltage must lie inside the voltage range (±5 %). Unknown data accepts everything.
    /// </summary>
    /// <param name="voltageV">Supply voltage in V.</param>
    /// <param name="frequencyHz">Supply frequency in Hz (ignored for DC).</param>
    /// <param name="isDc">True for a DC supply.</param>
    /// <param name="accepted">Human-readable list of what the fixture accepts, for messages.</param>
    public bool AcceptsSupply(float voltageV, float frequencyHz, bool isDc, out string accepted)
    {
        var range = VoltageRangeV;
        accepted = DescribeAccepted();
        if (isDc)
        {
            if (range is not { } r) return true;
            return voltageV >= r.Min * 0.95 && voltageV <= r.Max * 1.05;
        }
        bool lowVoltageDevice = range is { } lv && lv.Max < 90;
        if (!lowVoltageDevice)
            foreach (var n in SupportedNetworks)
                if (n.Matches(voltageV, frequencyHz)) return true;
        if (range is { } rr)
        {
            bool vOk = voltageV >= rr.Min * 0.97 && voltageV <= rr.Max * 1.03;
            bool fOk = FrequencyRangeHz is not { } fr || (frequencyHz >= fr.Min - 1 && frequencyHz <= fr.Max + 1);
            if (vOk && fOk) return true;
            return false;
        }
        return SupportedNetworks.Count == 0;
    }

    private string DescribeAccepted()
    {
        if (SupportedNetworks.Count > 0) return string.Join(", ", SupportedNetworks.Select(n => n.Text));
        if (VoltageRangeV is { } r)
        {
            string v = r.Min == r.Max ? $"{r.Min:0.#} V" : $"{r.Min:0.#}–{r.Max:0.#} V";
            return FrequencyRangeHz is { } f ? $"{v}, {f.Min:0.#}–{f.Max:0.#} Hz" : v;
        }
        return "unknown";
    }

    /// <inheritdoc />
    public override string ToString() => DisplayName;

    // ------------------------------------------------------------------ building

    /// <summary>
    /// Builds a profile from a parsed GDTF and/or a specs record (either may be null). Ports come from the GDTF
    /// (WiringObjects, else legacy connectors) completed from the specs (see <see cref="PortResolver.FromFixture"/>);
    /// power from specs, else GDTF PowerConsumption, consumer payload or beam power; weight from specs, else GDTF.
    /// </summary>
    public static FixtureProfile Build(string gdtfSpec, string? mode, FixtureType? type, FixtureSpecs? specs,
        string? resolvedFile = null, bool isSubstitute = false, IReadOnlyList<string>? notes = null)
    {
        ArgumentNullException.ThrowIfNull(gdtfSpec);
        var distribution = DistributionInfo.FromSpecs(specs);
        var ports = PortResolver.FromFixture(type, mode, specs);

        double? power = Positive(specs?.PowerConsumptionW);
        var powerSource = power is null ? PowerDataSource.Unknown : PowerDataSource.Specs;
        float pf = DefaultPowerFactor;
        SpecRange? vRange = specs?.VoltageRangeV, fRange = specs?.FrequencyRangeHz;
        double? weight = Positive(specs?.WeightKg);
        var weightSource = weight is null ? WeightDataSource.Unknown : WeightDataSource.Specs;

        if (type is not null)
        {
            var props = type.PhysicalDescriptions.Properties;
            var consumptions = props.PowerConsumptions;
            double gdtfPower = 0;
            foreach (var pc in consumptions)
            {
                float f = pc.PowerFactor is > 0f and < 0.999f ? pc.PowerFactor : 1f;
                if (pc.Value > 0 && float.IsFinite(pc.Value)) gdtfPower += pc.Value * f;
                if (pc.PowerFactor is > 0f and < 0.999f) pf = pc.PowerFactor;
            }
            // A "consumer" named as an output is a mistyped thru socket, not a second load.
            var consumers = WiringObjects(type, mode)
                .Where(w => w.ComponentType == WiringComponentType.Consumer && PortResolver.DirectionFromName(w.Name) != PortDirection.Output)
                .ToList();
            if (pf == DefaultPowerFactor)
                foreach (var c in consumers)
                    if (c.CosPhi is > 0f and <= 1f && c.CosPhi < 0.999f)
                    {
                        pf = c.CosPhi;
                        break;
                    }
            if (power is null && gdtfPower > 0)
            {
                power = gdtfPower;
                powerSource = PowerDataSource.GdtfPowerConsumption;
            }
            if (power is null)
            {
                double payload = consumers.Where(c => c.ElectricalPayLoad > 0 && float.IsFinite(c.ElectricalPayLoad)).Sum(c => (double)c.ElectricalPayLoad);
                if (payload > 0)
                {
                    power = payload;
                    powerSource = PowerDataSource.GdtfWiring;
                }
            }
            if (power is null && BeamPower(type, mode) is { } beams)
            {
                power = beams;
                powerSource = PowerDataSource.GdtfBeams;
            }
            if (weight is null && props.Weight is { } gw && gw > 0 && float.IsFinite(gw))
            {
                weight = gw;
                weightSource = WeightDataSource.Gdtf;
            }
            if (vRange is null)
            {
                if (consumptions.Count > 0)
                {
                    var pc = consumptions[0];
                    if (pc.VoltageHigh > 0) vRange = new SpecRange(pc.VoltageLow, pc.VoltageHigh);
                    if (fRange is null && pc.FrequencyHigh > 0) fRange = new SpecRange(pc.FrequencyLow, pc.FrequencyHigh);
                }
                else if (consumers.FirstOrDefault(c => c.VoltageRangeMax > 0) is { } wc)
                {
                    vRange = new SpecRange(wc.VoltageRangeMin, wc.VoltageRangeMax);
                    if (fRange is null && wc.FrequencyRangeMax > 0) fRange = new SpecRange(wc.FrequencyRangeMin, wc.FrequencyRangeMax);
                }
            }
        }

        var networks = new List<PowerNetworkSpec>();
        if (specs is not null)
            foreach (var n in specs.SupportedPowerNetworks)
                if (PowerNetworkSpec.TryParse(n) is { } parsed) networks.Add(parsed);

        string display = specs is { FullName.Length: > 0 } ? specs.FullName
            : type is not null ? $"{type.Manufacturer} {(type.LongName.Length > 0 ? type.LongName : type.Name)}".Trim()
            : gdtfSpec;

        return new FixtureProfile
        {
            GdtfSpec = gdtfSpec,
            Mode = mode,
            ResolvedFile = resolvedFile,
            IsSubstitute = isSubstitute,
            DisplayName = display,
            Category = specs?.Category ?? FixtureCategory.Other,
            LightSourceType = specs?.LightSourceType,
            Ports = ports,
            PowerW = power,
            PowerSource = powerSource,
            PowerFactor = pf,
            WeightKg = weight,
            WeightSource = weightSource,
            SupportedNetworks = networks,
            VoltageRangeV = vRange,
            FrequencyRangeHz = fRange,
            Distribution = distribution,
            BatteryPowered = specs?.BatteryPowered ?? false,
            Notes = notes ?? Array.Empty<string>(),
        };
    }

    /// <summary>The WiringObjects of a mode's geometry tree (all trees when the mode has none of its own).</summary>
    internal static IEnumerable<WiringObjectGeometry> WiringObjects(FixtureType type, string? mode)
    {
        var root = type.FindMode(mode)?.Geometry;
        if (root is not null)
        {
            bool any = false;
            foreach (var g in root.DescendantsAndSelf())
                if (g is WiringObjectGeometry w)
                {
                    any = true;
                    yield return w;
                }
            if (any) yield break;
        }
        foreach (var g in type.AllGeometries())
            if (g is WiringObjectGeometry w) yield return w;
    }

    /// <summary>
    /// Sum of beam power in the mode's tree, GeometryReferences expanded; null when there are no beams or every beam
    /// carries the GDTF default of 1000 W (the attribute was most likely not written).
    /// </summary>
    private static double? BeamPower(FixtureType type, string? mode)
    {
        var root = type.FindMode(mode)?.Geometry ?? (type.Geometries.Count > 0 ? type.Geometries[0] : null);
        if (root is null) return null;
        double sum = 0;
        int count = 0, defaults = 0;
        var stack = new Stack<(GdtfGeometry Node, int Depth)>();
        stack.Push((root, 0));
        while (stack.Count > 0)
        {
            var (node, depth) = stack.Pop();
            foreach (var g in node.DescendantsAndSelf())
            {
                if (g is BeamGeometry b && float.IsFinite(b.PowerConsumption) && b.PowerConsumption > 0)
                {
                    sum += b.PowerConsumption;
                    count++;
                    if (b.PowerConsumption == 1000f) defaults++;
                }
                else if (g is GeometryReference r && r.ReferencedGeometry is { } target && depth < 4 && !ReferenceEquals(target, node))
                {
                    stack.Push((target, depth + 1));
                }
            }
        }
        if (count == 0 || defaults == count) return null;
        return sum;
    }

    private static double? Positive(double? v) => v is { } x && double.IsFinite(x) && x > 0 ? x : null;
}
