namespace Candela.Gdtf;

/// <summary>Where a value of a <see cref="FixtureSpecs"/> record came from (<c>_source</c> in <c>_specs/SCHEMA.md</c>).</summary>
public enum SpecSource
{
    /// <summary>Unknown or not recorded.</summary>
    Unknown,

    /// <summary>Read directly from a GDTF field.</summary>
    Gdtf,

    /// <summary>Computed from GDTF data (DMX channels, wheels, wiring, geometry).</summary>
    GdtfDerived,

    /// <summary>From a manufacturer page, datasheet or manual (see <see cref="FixtureSpecs.Refs"/>).</summary>
    Manual,

    /// <summary>Approximation (same-platform model, typical value for the device class, …).</summary>
    Estimated,
}

/// <summary>A numeric range <c>{min, max}</c> of a spec record.</summary>
/// <param name="Min">Lower bound.</param>
/// <param name="Max">Upper bound.</param>
public readonly record struct SpecRange(double Min, double Max);

/// <summary>A DMX mode as listed in the specs: name and channel count.</summary>
/// <param name="Name">Mode name.</param>
/// <param name="Channels">Channel count as computed by the specs extractor (highest written offset per break).</param>
public sealed record SpecDmxMode(string Name, int Channels);

/// <summary>A gobo wheel summary.</summary>
/// <param name="Wheel">Wheel name.</param>
/// <param name="Slots">Number of gobo slots, if known.</param>
/// <param name="Rotating">Whether the gobos rotate.</param>
public sealed record SpecGoboWheel(string Wheel, int? Slots, bool Rotating);

/// <summary>A prism summary.</summary>
/// <param name="Name">Prism (wheel) name.</param>
/// <param name="Facets">Number of facets, if known.</param>
public sealed record SpecPrism(string Name, int? Facets);

/// <summary>
/// Researched physical data of one fixture (one record of <c>_specs/fixtures_index.json</c>, schema 1.0, documented
/// in <c>_specs/SCHEMA.md</c>): what GDTF files usually lack — category, real power draw, weight, maximum candela,
/// noise, connectors, load capacity of distribution devices — plus provenance per field. Units are in the property
/// names (W, kg, deg, K, cd, lm, dB, V, Hz, °C, A); null means unknown or not applicable.
/// </summary>
public sealed class FixtureSpecs
{
    // ---- identity
    /// <summary>Schema version of the record.</summary>
    public string SchemaVersion { get; init; } = "";

    /// <summary>GDTF file name the record belongs to.</summary>
    public string File { get; init; } = "";

    /// <summary>GDTF FixtureTypeID.</summary>
    public string? FixtureTypeId { get; init; }

    /// <summary>GDTF fixture type name.</summary>
    public string? GdtfName { get; init; }

    /// <summary>GDTF DataVersion.</summary>
    public string? GdtfDataVersion { get; init; }

    /// <summary>Model name without the brand (e.g. "Robin T1 Profile").</summary>
    public string LongName { get; init; } = "";

    /// <summary>Short label.</summary>
    public string ShortName { get; init; } = "";

    /// <summary>Brand plus model (e.g. "Robe Lighting Robin T1 Profile").</summary>
    public string FullName { get; init; } = "";

    /// <summary>Manufacturer as written in the GDTF file.</summary>
    public string Manufacturer { get; init; } = "";

    /// <summary>English description.</summary>
    public string Description { get; init; } = "";

    /// <summary>Original GDTF description when <see cref="Description"/> was generated.</summary>
    public string? GdtfDescription { get; init; }

    /// <summary>Library category.</summary>
    public FixtureCategory Category { get; init; } = FixtureCategory.Other;

    /// <summary>Category exactly as written in the record.</summary>
    public string CategoryText { get; init; } = "";

    /// <summary>Optional refinement ("Fresnel", "Battery uplight", …).</summary>
    public string? SubType { get; init; }

    // ---- electrical
    /// <summary>Supported mains networks (e.g. "220/230 V - 50 Hz").</summary>
    public IReadOnlyList<string> SupportedPowerNetworks { get; init; } = Array.Empty<string>();

    /// <summary>Mains input voltage range in V.</summary>
    public SpecRange? VoltageRangeV { get; init; }

    /// <summary>Mains frequency range in Hz.</summary>
    public SpecRange? FrequencyRangeHz { get; init; }

    /// <summary>Maximum mains draw of the device itself in W (own draw only for distribution devices).</summary>
    public double? PowerConsumptionW { get; init; }

    /// <summary>Input connector description, "+ LINK" when power can be daisy-chained.</summary>
    public string? PowerSupplyType { get; init; }

    /// <summary>Power input connector (powerCON TRUE1, Schuko, …).</summary>
    public string? PowerConnectorIn { get; init; }

    /// <summary>Power output (link) connector, or null.</summary>
    public string? PowerConnectorOut { get; init; }

    /// <summary>Whether a power output for daisy-chaining exists.</summary>
    public bool PowerLinkable { get; init; }

    /// <summary>Distribution devices: maximum input draw with all outputs loaded, in W.</summary>
    public double? MaxInputPowerW { get; init; }

    /// <summary>Distribution devices: maximum power deliverable to loads, in W.</summary>
    public double? MaxLoadW { get; init; }

    /// <summary>Distribution devices: total output current limit in A.</summary>
    public double? MaxLoadCurrentA { get; init; }

    /// <summary>Distribution devices: per-output current limit in A.</summary>
    public double? MaxLoadPerChannelA { get; init; }

    /// <summary>Distribution devices: number of outputs / dimmer channels.</summary>
    public int? OutputChannels { get; init; }

    /// <summary>Distribution devices: what the outputs supply (e.g. "48 VDC").</summary>
    public string? OutputVoltage { get; init; }

    /// <summary>Runs from an internal battery.</summary>
    public bool BatteryPowered { get; init; }

    // ---- physical
    /// <summary>Weight without packaging in kg.</summary>
    public double? WeightKg { get; init; }

    /// <summary>Noise in dB(A) at 1 m (0 = fanless).</summary>
    public double? NoiseLevelDb { get; init; }

    /// <summary>Ingress protection rating (IP20, IP65, …).</summary>
    public string? IpRating { get; init; }

    /// <summary>Operating ambient temperature range in °C.</summary>
    public SpecRange? OperatingTemperatureC { get; init; }

    // ---- movement
    /// <summary>Pan or tilt driven by a DMX channel.</summary>
    public bool MotorizedPanTilt { get; init; }

    /// <summary>Motorised pan range in degrees.</summary>
    public double? PanRangeDeg { get; init; }

    /// <summary>Motorised tilt range in degrees.</summary>
    public double? TiltRangeDeg { get; init; }

    /// <summary>Continuous pan rotation.</summary>
    public bool? InfinitePan { get; init; }

    /// <summary>Continuous tilt rotation.</summary>
    public bool? InfiniteTilt { get; init; }

    /// <summary>Manual pan adjustment range of static fixtures in degrees.</summary>
    public double? ManualPanRangeDeg { get; init; }

    /// <summary>Manual tilt adjustment range of static fixtures in degrees.</summary>
    public double? ManualTiltRangeDeg { get; init; }

    // ---- control
    /// <summary>Control signals (DMX512, RDM, Ethernet (Art-Net/sACN), …).</summary>
    public IReadOnlyList<string> ControlSignal { get; init; } = Array.Empty<string>();

    /// <summary>Data connectors (XLR3, XLR5, RJ45, …).</summary>
    public IReadOnlyList<string> DataConnectors { get; init; } = Array.Empty<string>();

    /// <summary>DMX modes with channel counts.</summary>
    public IReadOnlyList<SpecDmxMode> DmxModes { get; init; } = Array.Empty<SpecDmxMode>();

    // ---- light and optics
    /// <summary>LED, Discharge Lamp, Tungsten Lamp, Halogen Lamp or Laser.</summary>
    public string? LightSourceType { get; init; }

    /// <summary>Output flux in lm.</summary>
    public double? LuminousFluxLm { get; init; }

    /// <summary>On-axis intensity at full output and narrowest zoom in cd.</summary>
    public double? MaxLuminousIntensityCd { get; init; }

    /// <summary>Beam angle range (zoom) in degrees; min = max for a fixed lens.</summary>
    public SpecRange? BeamAngleDeg { get; init; }

    /// <summary>Field angle in degrees.</summary>
    public double? FieldAngleDeg { get; init; }

    /// <summary>Motorised zoom.</summary>
    public bool Zoom { get; init; }

    /// <summary>Lens type (Spot, Wash, Fresnel, PC, …).</summary>
    public string? LensType { get; init; }

    /// <summary>Total number of gobo slots.</summary>
    public int GoboSlots { get; init; }

    /// <summary>Gobo wheels.</summary>
    public IReadOnlyList<SpecGoboWheel> GoboWheels { get; init; } = Array.Empty<SpecGoboWheel>();

    /// <summary>Has framing shutters / barn doors.</summary>
    public bool FramingShutters { get; init; }

    /// <summary>Number of shutter blades.</summary>
    public int FramingShutterBlades { get; init; }

    /// <summary>Shutter assembly rotates.</summary>
    public bool FramingShutterRotation { get; init; }

    /// <summary>"Framing shutters" or "Barn doors".</summary>
    public string? FramingShutterType { get; init; }

    /// <summary>Colour mixing system (CMY, RGBW, …).</summary>
    public string? ColorMixing { get; init; }

    /// <summary>Has a colour wheel.</summary>
    public bool ColorWheel { get; init; }

    /// <summary>White point / CCT range in K.</summary>
    public SpecRange? ColorTemperatureK { get; init; }

    /// <summary>Has a prism.</summary>
    public bool Prism { get; init; }

    /// <summary>Prisms.</summary>
    public IReadOnlyList<SpecPrism> Prisms { get; init; } = Array.Empty<SpecPrism>();

    /// <summary>Has frost.</summary>
    public bool Frost { get; init; }

    /// <summary>Number of frost filters.</summary>
    public int FrostFilters { get; init; }

    /// <summary>Has an iris.</summary>
    public bool Iris { get; init; }

    /// <summary>Has motorised focus.</summary>
    public bool Focus { get; init; }

    /// <summary>Has an animation wheel.</summary>
    public bool AnimationWheel { get; init; }

    /// <summary>Number of GDTF beams (pixels).</summary>
    public int PixelCount { get; init; }

    // ---- provenance
    /// <summary>Source per field name (camelCase JSON names, e.g. "weightKg").</summary>
    public IReadOnlyDictionary<string, SpecSource> Source { get; init; } = new Dictionary<string, SpecSource>();

    /// <summary>Required fields that are still null.</summary>
    public IReadOnlyList<string> Missing { get; init; } = Array.Empty<string>();

    /// <summary>Fields that do not apply to the device.</summary>
    public IReadOnlyList<string> NotApplicable { get; init; } = Array.Empty<string>();

    /// <summary>Data quality notes.</summary>
    public IReadOnlyList<string> Issues { get; init; } = Array.Empty<string>();

    /// <summary>Manufacturer pages / datasheets the values come from.</summary>
    public IReadOnlyList<string> Refs { get; init; } = Array.Empty<string>();

    /// <summary>Source of one field (camelCase JSON name).</summary>
    public SpecSource SourceOf(string field) => Source.TryGetValue(field, out var s) ? s : SpecSource.Unknown;

    /// <inheritdoc />
    public override string ToString() => string.IsNullOrEmpty(FullName) ? File : FullName;
}
