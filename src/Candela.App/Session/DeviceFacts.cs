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

/// <summary>One line of the manufacturer data: a localised label, the value text and an icon.</summary>
internal readonly record struct FactRow(StageStr Label, string Value, string Icon);

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
    private DeviceFacts(string title, string subtitle, string? description, IReadOnlyList<FactRow> rows, IReadOnlyList<PortRow> ports,
        string? note)
    {
        Title = title;
        Subtitle = subtitle;
        Description = description;
        Rows = rows;
        Ports = ports;
        Note = note;
    }

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
        return new DeviceFacts(title, subtitle, description, rows, ports, note);
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
            rows.Add(new(StageStr.FactManufacturer, manufacturer, Icons.House));
        string? model = FirstNonEmpty(profile?.FixtureType.LongName, profile?.FixtureType.Name, specs?.LongName);
        if (model is not null)
            rows.Add(new(StageStr.FactModel, model, Icons.Lightbulb));
        var category = specs?.Category ?? profile?.Category;
        if (category is { } cat)
            rows.Add(new(StageStr.FactCategory, cat.DisplayName(), Icons.Tag));
        if (FirstNonEmpty(specs?.LightSourceType) is { } source)
            rows.Add(new(StageStr.FactLightSource, source, Icons.Sun));

        double? weight = electrical?.WeightKg ?? specs?.WeightKg;
        if (weight is > 0)
            rows.Add(new(StageStr.FactWeight, string.Create(inv, $"{weight:0.##} kg"), StageIcons.Weight));
        double? power = electrical?.PowerW ?? specs?.PowerConsumptionW;
        if (power is > 0)
        {
            string text = string.Create(inv, $"{power:0.#} W");
            if (electrical is { PowerFactor: < 0.999f and > 0f } e)
                text += string.Create(inv, $" · cos φ {e.PowerFactor:0.00}");
            rows.Add(new(StageStr.FactPower, text, Icons.Zap));
        }

        if (specs?.MaxLuminousIntensityCd is > 0 and var cd)
            rows.Add(new(StageStr.FactIntensity, FormatCandela(cd), Icons.SunMedium));
        double? flux = specs?.LuminousFluxLm;
        if (flux is null && profile is not null)
        {
            double sum = 0;
            foreach (var beam in profile.Beams)
                sum += float.IsFinite(beam.ReferenceFlux) ? beam.ReferenceFlux : 0;
            flux = sum > 0 ? sum : null;
        }

        if (flux is > 0)
            rows.Add(new(StageStr.FactFlux, string.Create(inv, $"{flux:#,0} lm").Replace(',', ' '), Icons.Sparkle));
        if (specs?.BeamAngleDeg is { } beamAngle)
            rows.Add(new(StageStr.FactBeamAngle, FormatRange(beamAngle, "°", "0.#"), StageIcons.Aperture));
        else if (profile is { Beams.Count: > 0 })
            rows.Add(new(StageStr.FactBeamAngle, string.Create(inv, $"{profile.Beams[0].NativeBeamAngle * 180f / MathF.PI:0.#}°"), StageIcons.Aperture));
        if (specs?.FieldAngleDeg is > 0 and var field)
            rows.Add(new(StageStr.FactFieldAngle, string.Create(inv, $"{field:0.#}°"), StageIcons.Aperture));
        if (specs?.ColorTemperatureK is { } cct)
            rows.Add(new(StageStr.FactCct, FormatRange(cct, " K", "0"), StageIcons.Thermometer));
        else if (profile is { Beams.Count: > 0 } && float.IsFinite(profile.Beams[0].SourceKelvin) && profile.Beams[0].SourceKelvin > 0)
            rows.Add(new(StageStr.FactCct, string.Create(inv, $"{profile.Beams[0].SourceKelvin:0} K"), StageIcons.Thermometer));
        if (profile is { Beams.Count: > 0 } && profile.Beams[0].Geometry.IsColorRenderingIndexSpecified)
            rows.Add(new(StageStr.FactCri, string.Create(inv, $"{profile.Beams[0].Geometry.ColorRenderingIndex:0}"), Icons.Palette));
        string? mixing = FirstNonEmpty(specs?.ColorMixing);
        if (mixing is not null || specs?.ColorWheel == true)
        {
            string text = mixing ?? "";
            if (specs?.ColorWheel == true)
                text = text.Length > 0 ? text + " · " + StageLoc.Get(StageStr.FactColorWheel) : StageLoc.Get(StageStr.FactColorWheel);
            rows.Add(new(StageStr.FactColorMixing, text, Icons.Palette));
        }

        if (specs?.NoiseLevelDb is > 0 and var noise)
            rows.Add(new(StageStr.FactNoise, string.Create(inv, $"{noise:0.#} dB(A)"), StageIcons.Volume));
        if (FirstNonEmpty(specs?.IpRating) is { } ip)
            rows.Add(new(StageStr.FactIpRating, ip, Icons.CloudFog));
        string? supply = SupplyText(specs, electrical);
        if (supply is not null)
            rows.Add(new(StageStr.FactSupply, supply, StageIcons.PlugZap));
        if (specs is { PanRangeDeg: > 0 } || specs is { TiltRangeDeg: > 0 })
        {
            string pan = specs.InfinitePan == true ? "∞" : specs.PanRangeDeg is { } p ? string.Create(inv, $"{p:0.#}°") : "—";
            string tilt = specs.InfiniteTilt == true ? "∞" : specs.TiltRangeDeg is { } t ? string.Create(inv, $"{t:0.#}°") : "—";
            rows.Add(new(StageStr.FactPanTiltRange, $"{pan} / {tilt}", Icons.Orbit));
        }

        if (specs?.OperatingTemperatureC is { } temp)
            rows.Add(new(StageStr.FactOperatingTemperature, FormatRange(temp, " °C", "0"), StageIcons.Thermometer));
        if (Features(specs) is { Length: > 0 } features)
            rows.Add(new(StageStr.FactFeatures, features, Icons.Sparkles));
        if (specs is { ControlSignal.Count: > 0 })
            rows.Add(new(StageStr.FactControl, string.Join(", ", specs.ControlSignal), Icons.Network));
        if (profile is not null)
        {
            int modes = profile.FixtureType.DmxModes.Count;
            rows.Add(new(StageStr.FactModes, string.Create(inv, $"{modes}"), Icons.List));
        }
    }

    private static string? SupplyText(FixtureSpecs? specs, ElectricalProfile? electrical)
    {
        var inv = CultureInfo.InvariantCulture;
        var parts = new List<string>();
        var range = electrical?.VoltageRangeV ?? specs?.VoltageRangeV;
        if (range is { } r)
            parts.Add(FormatRange(r, " V", "0"));
        var networks = specs?.SupportedPowerNetworks;
        if (networks is { Count: > 0 } && range is null)
            parts.Add(string.Join(", ", networks));
        var frequency = electrical?.FrequencyRangeHz ?? specs?.FrequencyRangeHz;
        if (frequency is { } f)
            parts.Add(FormatRange(f, " Hz", "0"));
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
                detail.Append(inv, $" · {amps:0.#} A");
            if (port.BreakerA is { } breaker)
                detail.Append(inv, $" · {StageLoc.Get(StageStr.PortBreaker)} {breaker:0} A");

            Guid? cableId = null;
            string? cableText = null;
            var color = new Vector4(0f, 0f, 0f, 0f);
            if (solution?.CableAt(entity.Id, port.Name) is { } plugged)
            {
                var cable = plugged.Cable;
                cableId = cable.EntityId;
                string typeName = cable.Type?.DisplayName ?? cable.TypeId;
                cableText = string.Create(inv, $"{cable.DisplayLabel} · {typeName} · {cable.LengthM:0.0} m");
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

    /// <summary>A candela value with thin-space thousands separators ("760 000 cd").</summary>
    internal static string FormatCandela(double cd) =>
        cd >= 1e6 ? string.Create(CultureInfo.InvariantCulture, $"{cd / 1e6:0.##} Mcd") : string.Create(CultureInfo.InvariantCulture, $"{cd:#,0} cd").Replace(',', ' ');

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
