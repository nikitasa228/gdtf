using System.Globalization;
using System.Numerics;
using System.Text;
using Candela.App.Ui;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;
using Candela.Electrical;
using Candela.Engine;
using Candela.Gdtf;
using DmxProfile = Candela.Dmx.FixtureProfile;
using ElectricalProfile = Candela.Electrical.FixtureProfile;

namespace Candela.App.Session;

/// <summary>
/// One line of the manufacturer data: a localised label, the value text, an icon and where the value comes from (the
/// <c>_source</c> of the <c>_specs</c> record, or the GDTF file). Estimated values start with "≈".
/// </summary>
internal readonly record struct FactRow(StageStr Label, string Value, string Icon, SpecSource Source = SpecSource.Unknown);

/// <summary>
/// A port of a device as the properties panel shows it: the connector (with its illustration key), the direction and
/// signal, and the cable plugged into it (label, type, colour, length) from the engine's network solution.
/// </summary>
internal sealed record PortRow(
    Port Port,
    string Title,
    string Detail,
    Guid? CableId,
    string? CableText,
    Vector4 CableColor);

/// <summary>
/// The manufacturer part of the properties panel for one device: what the GDTF file and the researched
/// <c>_specs</c> data say about the fixture type (manufacturer, model, category, weight, power, candela and flux, beam
/// and field angles, CCT range, CRI, colour mixing, noise, IP rating, supply, pan/tilt range, features), and the ports
/// with their connectors and the cables plugged into them. Built off the frame path (when the device, its GDTF, the
/// network or the language changes); plain data afterwards.
/// </summary>
internal sealed class DeviceFacts
{
    /// <summary>Prefix of an estimated value ("≈" and a no-break space).</summary>
    public const string EstimatedMark = "≈\u00a0";

    private DeviceFacts(string title, string subtitle, string? description, IReadOnlyList<FactRow> rows, IReadOnlyList<PortRow> ports,
        string? note, IReadOnlyList<string> references)
    {
        Title = title;
        Subtitle = subtitle;
        Description = description;
        Rows = rows;
        Ports = ports;
        Note = note;
        References = references;
        HasEstimates = rows.Any(r => r.Source == SpecSource.Estimated);
    }

    /// <summary>Datasheets and product pages the researched values come from (<c>_refs</c> of the specs record), or empty.</summary>
    public IReadOnlyList<string> References { get; }

    /// <summary>True when at least one row is an estimate (the panel explains the "≈" mark).</summary>
    public bool HasEstimates { get; }

    /// <summary>Manufacturer and model.</summary>
    public string Title { get; }

    /// <summary>GDTF file, mode and footprint.</summary>
    public string Subtitle { get; }

    /// <summary>Description of the fixture type (from <c>_specs</c> or the GDTF), or null.</summary>
    public string? Description { get; }

    /// <summary>The data rows (only values that are known).</summary>
    public IReadOnlyList<FactRow> Rows { get; }

    /// <summary>The device's ports in GDTF order.</summary>
    public IReadOnlyList<PortRow> Ports { get; }

    /// <summary>A remark: a stand-in GDTF is used, the GDTF is missing, …; or null.</summary>
    public string? Note { get; }

    /// <summary>
    /// Builds the facts of <paramref name="entity"/>: a fixture's type data and ports, or the ports of a console or
    /// power source. Never throws for missing or partial data (unknown values are left out).
    /// </summary>
    public static DeviceFacts Build(StageEngine engine, Entity entity)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(entity);
        var rows = new List<FactRow>();
        string title = entity.Name, subtitle = "";
        string? description = null, note = null;
        IReadOnlyList<string> references = Array.Empty<string>();
        var fixture = entity.Get<FixtureComponent>();
        if (fixture is not null)
        {
            var state = engine.Dmx.GetState(entity.Id);
            var resolution = engine.Dmx.GetResolution(entity.Id);
            var profile = state?.Profile ?? resolution?.Profile;
            ElectricalProfile? electrical = null;
            try
            {
                electrical = engine.ElectricalProfiles.GetProfile(fixture.GdtfSpec, fixture.GdtfMode);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or FormatException)
            {
                note = e.Message;
            }

            var specs = profile?.Specs ?? resolution?.Entry?.Specs ?? engine.Specs.Find(fixture.GdtfSpec);
            (title, subtitle) = Titles(fixture, profile, specs);
            description = !string.IsNullOrWhiteSpace(specs?.Description) ? specs!.Description.Trim()
                : profile is not null && !string.IsNullOrWhiteSpace(profile.FixtureType.Description) ? profile.FixtureType.Description.Trim()
                : null;
            FixtureRows(rows, profile, specs, electrical);
            if (specs is { Refs.Count: > 0 })
                references = specs.Refs.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.Trim()).Distinct(StringComparer.Ordinal).ToArray();
            if (resolution is { Success: true, MatchKind: { } kind } && kind is not (FixtureMatchKind.ExactFileName))
            {
                note = StageLoc.Format(kind is FixtureMatchKind.SameModel or FixtureMatchKind.ModelOnly ? StageStr.FactsMatched : StageStr.FactsSubstitute,
                    fixture.GdtfSpec, resolution.Entry?.FileName ?? "?");
            }
            else if (profile is null)
            {
                note = StageLoc.Format(StageStr.FactsUnresolved, fixture.GdtfSpec, resolution?.Error ?? "");
            }
        }

        var ports = PortRows(engine, entity);
        return new DeviceFacts(title, subtitle, description, rows, ports, note, references);
    }

    private static (string Title, string Subtitle) Titles(FixtureComponent fixture, DmxProfile? profile, FixtureSpecs? specs)
    {
        string manufacturer = FirstNonEmpty(specs?.Manufacturer, profile?.FixtureType.Manufacturer) ?? "";
        string model = FirstNonEmpty(profile?.FixtureType.LongName, profile?.FixtureType.Name, specs?.LongName, specs?.FullName) ?? fixture.GdtfSpec;
        string title = manufacturer.Length > 0 && !model.StartsWith(manufacturer, StringComparison.OrdinalIgnoreCase) ? $"{manufacturer} {model}" : model;
        var sb = new StringBuilder(Path.GetFileName(fixture.GdtfSpec));
        if (profile is not null)
            sb.Append(CultureInfo.InvariantCulture, $" · {profile.Mode.Name} · {StageLoc.Format(StageStr.FactsChannels, profile.Footprint)}");
        else if (!string.IsNullOrWhiteSpace(fixture.GdtfMode))
            sb.Append(" · ").Append(fixture.GdtfMode);
        return (title, sb.ToString());
    }

    private static void FixtureRows(List<FactRow> rows, DmxProfile? profile, FixtureSpecs? specs, ElectricalProfile? electrical)
    {
        var inv = CultureInfo.InvariantCulture;
        string? manufacturer = FirstNonEmpty(specs?.Manufacturer, profile?.FixtureType.Manufacturer);
        if (manufacturer is not null)
            Add(rows, StageStr.FactManufacturer, manufacturer, Icons.House, Of(specs, "manufacturer", SpecSource.Gdtf));
        string? model = FirstNonEmpty(profile?.FixtureType.LongName, profile?.FixtureType.Name, specs?.LongName);
        if (model is not null)
            Add(rows, StageStr.FactModel, model, Icons.Lightbulb, profile is not null ? SpecSource.Gdtf : Of(specs, "longName", SpecSource.Gdtf));
        var category = specs?.Category ?? profile?.Category;
        if (category is { } cat)
            Add(rows, StageStr.FactCategory, cat.DisplayName(), Icons.Tag, Of(specs, "category", SpecSource.GdtfDerived));
        if (FirstNonEmpty(specs?.LightSourceType) is { } source)
            Add(rows, StageStr.FactLightSource, source, Icons.Sun, Of(specs, "lightSourceType", SpecSource.Unknown));

        double? weight = electrical?.WeightKg ?? specs?.WeightKg;
        if (weight is > 0)
        {
            var weightSource = electrical is { WeightKg: > 0, WeightSource: WeightDataSource.Gdtf } ? SpecSource.Gdtf
                : Of(specs, "weightKg", SpecSource.Unknown);
            Add(rows, StageStr.FactWeight, string.Create(inv, $"{weight:0.##} {StageUnits.Current.Kilogram}"), StageIcons.Weight, weightSource);
        }

        double? power = electrical?.PowerW ?? specs?.PowerConsumptionW;
        if (power is > 0)
        {
            string text = string.Create(inv, $"{power:0.#} {StageUnits.Current.Watt}");
            // The electrical model assumes 0.95 when neither the GDTF nor the specs give a power factor: that is not data.
            if (electrical is { PowerFactor: < 0.999f and > 0f } e && e.PowerFactor != ElectricalProfile.DefaultPowerFactor)
                text += string.Create(inv, $" · cos φ {e.PowerFactor:0.00}");
            var powerSource = electrical is { PowerW: > 0 }
                ? electrical.PowerSource switch
                {
                    PowerDataSource.Specs => Of(specs, "powerConsumptionW", SpecSource.Unknown),
                    PowerDataSource.GdtfPowerConsumption or PowerDataSource.GdtfWiring => SpecSource.Gdtf,
                    // The lamp power of the beams, not the mains draw of the fixture.
                    PowerDataSource.GdtfBeams => SpecSource.Estimated,
                    _ => SpecSource.Unknown,
                }
                : Of(specs, "powerConsumptionW", SpecSource.Unknown);
            Add(rows, StageStr.FactPower, text, Icons.Zap, powerSource);
        }

        if (specs?.MaxLuminousIntensityCd is > 0 and var cd)
            Add(rows, StageStr.FactIntensity, FormatCandela(cd), Icons.SunMedium, Of(specs, "maxLuminousIntensityCd", SpecSource.Unknown));
        double? flux = specs?.LuminousFluxLm;
        var fluxSource = Of(specs, "luminousFluxLm", SpecSource.Unknown);
        if (flux is null && profile is not null)
        {
            double sum = 0;
            foreach (var beam in profile.Beams)
                sum += float.IsFinite(beam.ReferenceFlux) ? beam.ReferenceFlux : 0;
            flux = sum > 0 ? sum : null;
            fluxSource = SpecSource.GdtfDerived;
        }

        if (flux is > 0)
            Add(rows, StageStr.FactFlux, string.Create(inv, $"{flux:#,0} {StageUnits.Current.Lumen}").Replace(',', ' '), Icons.Sparkle, fluxSource);
        if (specs?.BeamAngleDeg is { } beamAngle)
            Add(rows, StageStr.FactBeamAngle, FormatRange(beamAngle, "°", "0.#"), StageIcons.Aperture, Of(specs, "beamAngleDeg", SpecSource.Unknown));
        else if (profile is { Beams.Count: > 0 })
            Add(rows, StageStr.FactBeamAngle, string.Create(inv, $"{profile.Beams[0].NativeBeamAngle * 180f / MathF.PI:0.#}°"), StageIcons.Aperture,
                SpecSource.Gdtf);
        if (specs?.FieldAngleDeg is > 0 and var field)
            Add(rows, StageStr.FactFieldAngle, string.Create(inv, $"{field:0.#}°"), StageIcons.Aperture, Of(specs, "fieldAngleDeg", SpecSource.Unknown));
        if (specs?.ColorTemperatureK is { } cct)
            Add(rows, StageStr.FactCct, FormatRange(cct, " K", "0"), StageIcons.Thermometer, Of(specs, "colorTemperatureK", SpecSource.Unknown));
        else if (profile is { Beams.Count: > 0 } && float.IsFinite(profile.Beams[0].SourceKelvin) && profile.Beams[0].SourceKelvin > 0)
            Add(rows, StageStr.FactCct, string.Create(inv, $"{profile.Beams[0].SourceKelvin:0} K"), StageIcons.Thermometer, SpecSource.Gdtf);
        if (profile is { Beams.Count: > 0 } && profile.Beams[0].Geometry.IsColorRenderingIndexSpecified)
            Add(rows, StageStr.FactCri, string.Create(inv, $"{profile.Beams[0].Geometry.ColorRenderingIndex:0}"), Icons.Palette, SpecSource.Gdtf);
        string? mixing = FirstNonEmpty(specs?.ColorMixing);
        if (mixing is not null || specs?.ColorWheel == true)
        {
            string text = mixing ?? "";
            if (specs?.ColorWheel == true)
                text = text.Length > 0 ? text + " · " + StageLoc.Get(StageStr.FactColorWheel) : StageLoc.Get(StageStr.FactColorWheel);
            Add(rows, StageStr.FactColorMixing, text, Icons.Palette, Of(specs, mixing is not null ? "colorMixing" : "colorWheel", SpecSource.Unknown));
        }

        if (specs?.NoiseLevelDb is > 0 and var noise)
        {
            Add(rows, StageStr.FactNoise, string.Create(inv, $"{noise:0.#} {StageUnits.Current.DecibelA}"), StageIcons.Volume,
                Of(specs, "noiseLevelDb", SpecSource.Unknown));
        }

        if (FirstNonEmpty(specs?.IpRating) is { } ip)
            Add(rows, StageStr.FactIpRating, ip, Icons.CloudFog, Of(specs, "ipRating", SpecSource.Unknown));
        string? supply = SupplyText(specs, electrical);
        if (supply is not null)
        {
            var supplySource = specs?.VoltageRangeV is not null ? Of(specs, "voltageRangeV", SpecSource.Unknown)
                : electrical?.VoltageRangeV is not null ? SpecSource.Gdtf
                : Of(specs, "supportedPowerNetworks", SpecSource.Unknown);
            Add(rows, StageStr.FactSupply, supply, StageIcons.PlugZap, supplySource);
        }

        if (specs is { PanRangeDeg: > 0 } || specs is { TiltRangeDeg: > 0 })
        {
            string pan = specs.InfinitePan == true ? "∞" : specs.PanRangeDeg is { } p ? string.Create(inv, $"{p:0.#}°") : "—";
            string tilt = specs.InfiniteTilt == true ? "∞" : specs.TiltRangeDeg is { } t ? string.Create(inv, $"{t:0.#}°") : "—";
            Add(rows, StageStr.FactPanTiltRange, $"{pan} / {tilt}", Icons.Orbit,
                Weakest(Of(specs, "panRangeDeg", SpecSource.Unknown), Of(specs, "tiltRangeDeg", SpecSource.Unknown)));
        }

        if (specs?.OperatingTemperatureC is { } temp)
        {
            Add(rows, StageStr.FactOperatingTemperature, FormatRange(temp, " °C", "0"), StageIcons.Thermometer,
                Of(specs, "operatingTemperatureC", SpecSource.Unknown));
        }

        if (Features(specs) is { Length: > 0 } features)
            Add(rows, StageStr.FactFeatures, features, Icons.Sparkles, SpecSource.GdtfDerived);
        if (specs is { ControlSignal.Count: > 0 })
            Add(rows, StageStr.FactControl, string.Join(", ", specs.ControlSignal), Icons.Network, Of(specs, "controlSignal", SpecSource.Unknown));
        if (profile is not null)
        {
            int modes = profile.FixtureType.DmxModes.Count;
            Add(rows, StageStr.FactModes, string.Create(inv, $"{modes}"), Icons.List, SpecSource.Gdtf);
        }
    }

    /// <summary>Adds a row; an estimated value starts with <see cref="EstimatedMark"/>.</summary>
    private static void Add(List<FactRow> rows, StageStr label, string value, string icon, SpecSource source) =>
        rows.Add(new FactRow(label, source == SpecSource.Estimated ? EstimatedMark + value : value, icon, source));

    /// <summary>Where a field of the specs record comes from (<paramref name="fallback"/> without a record or an entry).</summary>
    private static SpecSource Of(FixtureSpecs? specs, string field, SpecSource fallback)
    {
        if (specs is null)
            return fallback;
        var source = specs.SourceOf(field);
        return source == SpecSource.Unknown ? fallback : source;
    }

    /// <summary>The less reliable of two sources (an estimate taints a combined value).</summary>
    private static SpecSource Weakest(SpecSource a, SpecSource b) =>
        a == SpecSource.Estimated || b == SpecSource.Estimated ? SpecSource.Estimated : a != SpecSource.Unknown ? a : b;

    /// <summary>The tooltip text of a value's source, or null when the source is not recorded.</summary>
    internal static StageStr? SourceText(SpecSource source) => source switch
    {
        SpecSource.Gdtf => StageStr.FactSourceGdtf,
        SpecSource.GdtfDerived => StageStr.FactSourceDerived,
        SpecSource.Manual => StageStr.FactSourceDatasheet,
        SpecSource.Estimated => StageStr.FactSourceEstimated,
        _ => null,
    };

    private static string? SupplyText(FixtureSpecs? specs, ElectricalProfile? electrical)
    {
        var inv = CultureInfo.InvariantCulture;
        var parts = new List<string>();
        var range = electrical?.VoltageRangeV ?? specs?.VoltageRangeV;
        if (range is { } r)
            parts.Add(FormatRange(r, " " + StageUnits.Current.Volt, "0"));
        var networks = specs?.SupportedPowerNetworks;
        if (networks is { Count: > 0 } && range is null)
            parts.Add(string.Join(", ", networks));
        var frequency = electrical?.FrequencyRangeHz ?? specs?.FrequencyRangeHz;
        if (frequency is { } f)
            parts.Add(FormatRange(f, " " + StageUnits.Current.Hertz, "0"));
        if (specs?.BatteryPowered == true)
            parts.Add(StageLoc.Get(StageStr.FactBattery));
        if (FirstNonEmpty(specs?.PowerSupplyType) is { } psu)
            parts.Add(psu);
        _ = inv;
        return parts.Count > 0 ? string.Join(" · ", parts) : null;
    }

    private static string Features(FixtureSpecs? specs)
    {
        if (specs is null)
            return "";
        var list = new List<string>();
        if (specs.Zoom)
            list.Add(StageLoc.Get(StageStr.FeatureZoom));
        if (specs.Focus)
            list.Add(StageLoc.Get(StageStr.FeatureFocus));
        if (specs.Iris)
            list.Add(StageLoc.Get(StageStr.FeatureIris));
        if (specs.Frost)
            list.Add(StageLoc.Get(StageStr.FeatureFrost));
        if (specs.GoboWheels.Count == 1)
            list.Add(StageLoc.Get(StageStr.FeatureGoboWheel));
        else if (specs.GoboWheels.Count > 1)
            list.Add(StageLoc.Format(StageStr.FeatureGoboWheels, specs.GoboWheels.Count));
        else if (specs.GoboSlots > 0)
            list.Add(StageLoc.Format(StageStr.FeatureGobos, specs.GoboSlots));
        if (specs.Prism)
            list.Add(StageLoc.Get(StageStr.FeaturePrism));
        if (specs.AnimationWheel)
            list.Add(StageLoc.Get(StageStr.FeatureAnimation));
        if (specs.FramingShutters)
            list.Add(StageLoc.Get(StageStr.FeatureFraming));
        if (specs.PixelCount > 1)
            list.Add(StageLoc.Format(StageStr.FeaturePixels, specs.PixelCount));
        return string.Join(", ", list);
    }

    /// <summary>The ports of a fixture, console or power source with the cables plugged into them.</summary>
    internal static IReadOnlyList<PortRow> PortRows(StageEngine engine, Entity entity)
    {
        IReadOnlyList<Port> ports;
        NetworkSolution? solution = null;
        try
        {
            ports = engine.Network.Ports.GetPorts(entity);
            if (ports.Count > 0)
                solution = engine.NetworkSolution;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or FormatException)
        {
            return Array.Empty<PortRow>();
        }

        var rows = new List<PortRow>(ports.Count);
        var inv = CultureInfo.InvariantCulture;
        foreach (var port in ports)
        {
            string direction = StageLoc.Get(port.Direction switch
            {
                PortDirection.Input => StageStr.PortInput,
                PortDirection.Output => StageStr.PortOutput,
                _ => StageStr.PortBidirectional,
            });
            string signal = port.Signal switch
            {
                SignalKind.Power => StageLoc.Get(StageStr.SignalPower),
                SignalKind.Dmx512 => "DMX512",
                SignalKind.Ethernet => "Ethernet",
                SignalKind.Multicore => StageLoc.Get(StageStr.SignalMulticore),
                SignalKind.Hybrid => StageLoc.Get(StageStr.SignalHybrid),
                _ => StageLoc.Get(StageStr.SignalOther),
            };
            var detail = new StringBuilder();
            detail.Append(port.Connector.DisplayName).Append(" · ").Append(signal).Append(' ').Append(direction);
            if (port.Universe is { } universe)
                detail.Append(inv, $" · U{universe}{(port.UniverseCount > 1 ? $"–{universe + port.UniverseCount - 1}" : "")}");
            if (port.MaxThroughCurrentA is { } amps && port.Role is PortRole.PowerLink or PortRole.PowerDistribution or PortRole.PowerOutlet)
                detail.Append(inv, $" · {amps:0.#} {StageUnits.Current.Ampere}");
            if (port.BreakerA is { } breaker)
                detail.Append(inv, $" · {StageLoc.Get(StageStr.PortBreaker)} {breaker:0} {StageUnits.Current.Ampere}");

            Guid? cableId = null;
            string? cableText = null;
            var color = new Vector4(0f, 0f, 0f, 0f);
            if (solution?.CableAt(entity.Id, port.Name) is { } plugged)
            {
                var cable = plugged.Cable;
                cableId = cable.EntityId;
                string typeName = cable.Type?.DisplayName ?? cable.TypeId;
                cableText = string.Create(inv, $"{cable.DisplayLabel} · {typeName} · {cable.LengthM:0.0} {StageUnits.Current.Metre}");
                if (cable.Type is { } type)
                {
                    var (r, g, b) = type.ColorSrgb;
                    color = new Vector4(r, g, b, 1f);
                }
            }

            rows.Add(new PortRow(port, port.Name, detail.ToString(), cableId, cableText, color));
        }

        return rows;
    }

    /// <summary>
    /// A candela value in the UI language's symbols with narrow no-break thousands separators ("760 000 cd", "1.48 Mcd").
    /// </summary>
    internal static string FormatCandela(double cd)
    {
        var units = StageUnits.Current;
        return cd >= 1e6
            ? string.Create(CultureInfo.InvariantCulture, $"{cd / 1e6:0.##} {units.Megacandela}")
            : string.Create(CultureInfo.InvariantCulture, $"{cd:#,0} {units.Candela}").Replace(',', '\u202f');
    }

    private static string FormatRange(SpecRange range, string unit, string format)
    {
        var inv = CultureInfo.InvariantCulture;
        string min = range.Min.ToString(format, inv), max = range.Max.ToString(format, inv);
        return Math.Abs(range.Max - range.Min) < 1e-9 ? min + unit : $"{min}–{max}{unit}";
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values)
        {
            if (!string.IsNullOrWhiteSpace(v))
                return v.Trim();
        }

        return null;
    }
}
