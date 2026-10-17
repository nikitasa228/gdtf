using System.Text.Json;

namespace Candela.Gdtf;

/// <summary>
/// The researched fixture data of <c>_specs/fixtures_index.json</c>, indexed by GDTF file name. Loading is tolerant: a
/// value of an unexpected JSON type becomes null (and a warning) instead of failing the whole index.
/// </summary>
public sealed class FixtureSpecsIndex
{
    /// <summary>Relative path of the index from a repository or data root.</summary>
    public const string DefaultRelativePath = "_specs/fixtures_index.json";

    private readonly Dictionary<string, FixtureSpecs> _byFile;

    private FixtureSpecsIndex(List<FixtureSpecs> records, List<string> warnings)
    {
        All = records;
        Warnings = warnings;
        _byFile = new Dictionary<string, FixtureSpecs>(records.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var r in records)
            if (!string.IsNullOrEmpty(r.File)) _byFile.TryAdd(StripExtension(r.File), r);
    }

    /// <summary>An index without records.</summary>
    public static FixtureSpecsIndex Empty { get; } = new(new List<FixtureSpecs>(), new List<string>());

    /// <summary>All records in file order.</summary>
    public IReadOnlyList<FixtureSpecs> All { get; }

    /// <summary>Number of records.</summary>
    public int Count => All.Count;

    /// <summary>Problems found while loading (wrong value types, records without file name).</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>Loads an index file.</summary>
    public static FixtureSpecsIndex Load(string path)
    {
        using var stream = File.OpenRead(path);
        return Load(stream);
    }

    /// <summary>Loads an index from a stream (UTF-8 JSON array of records).</summary>
    /// <exception cref="InvalidDataException">The content is not JSON or not an array.</exception>
    public static FixtureSpecsIndex Load(Stream stream)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(stream, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"Fixture specs index is not valid JSON: {e.Message}", e);
        }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Fixture specs index must be a JSON array of records.");
            var records = new List<FixtureSpecs>(doc.RootElement.GetArrayLength());
            var warnings = new List<string>();
            int i = 0;
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                if (e.ValueKind == JsonValueKind.Object)
                {
                    var r = SpecsJson.Read(e, warnings);
                    if (string.IsNullOrEmpty(r.File)) warnings.Add($"Record {i} has no file name; skipped.");
                    else records.Add(r);
                }
                else
                {
                    warnings.Add($"Record {i} is not an object; skipped.");
                }
                i++;
            }
            return new FixtureSpecsIndex(records, warnings);
        }
    }

    /// <summary>
    /// Walks up from <paramref name="startDirectory"/> looking for <see cref="DefaultRelativePath"/> and loads it;
    /// null when none is found or it cannot be read.
    /// </summary>
    public static FixtureSpecsIndex? TryLoadNear(string startDirectory)
    {
        for (var dir = new DirectoryInfo(startDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, DefaultRelativePath);
            if (!File.Exists(candidate)) continue;
            try
            {
                return Load(candidate);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                return null;
            }
        }
        return null;
    }

    /// <summary>Record for a GDTF file name (with or without directory and <c>.gdtf</c> extension), or null.</summary>
    public FixtureSpecs? Find(string? gdtfFileName)
    {
        if (string.IsNullOrWhiteSpace(gdtfFileName)) return null;
        return _byFile.TryGetValue(StripExtension(Path.GetFileName(gdtfFileName)), out var r) ? r : null;
    }

    private static string StripExtension(string name) =>
        name.EndsWith(".gdtf", StringComparison.OrdinalIgnoreCase) ? name[..^5] : name;
}

/// <summary>Tolerant mapping of one JSON record to <see cref="FixtureSpecs"/>.</summary>
internal static class SpecsJson
{
    public static FixtureSpecs Read(JsonElement e, List<string> warnings)
    {
        string file = Str(e, "file", warnings, "") ?? "";
        string ctx = string.IsNullOrEmpty(file) ? "record" : file;
        string Ctx(string field) => $"{ctx}: {field}";
        void W(string field, JsonElement v) => warnings.Add($"{Ctx(field)} has unexpected type {v.ValueKind}; ignored.");

        string? S(string name) => Str(e, name, warnings, ctx);
        double? D(string name)
        {
            if (!e.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return null;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double d) && double.IsFinite(d)) return d;
            if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out d) && double.IsFinite(d)) return d;
            W(name, v);
            return null;
        }
        int? I(string name)
        {
            var d = D(name);
            return d is { } x && x >= int.MinValue && x <= int.MaxValue ? (int)Math.Round(x) : null;
        }
        bool? B(string name)
        {
            if (!e.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return null;
            if (v.ValueKind == JsonValueKind.True) return true;
            if (v.ValueKind == JsonValueKind.False) return false;
            W(name, v);
            return null;
        }
        SpecRange? R(string name)
        {
            if (!e.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return null;
            if (v.ValueKind == JsonValueKind.Object && Num(v, "min") is { } lo && Num(v, "max") is { } hi) return new SpecRange(lo, hi);
            if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double single)) return new SpecRange(single, single);
            W(name, v);
            return null;
        }
        IReadOnlyList<string> L(string name)
        {
            if (!e.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return Array.Empty<string>();
            if (v.ValueKind == JsonValueKind.String) return new[] { v.GetString() ?? "" };
            if (v.ValueKind != JsonValueKind.Array)
            {
                W(name, v);
                return Array.Empty<string>();
            }
            var list = new List<string>(v.GetArrayLength());
            foreach (var x in v.EnumerateArray())
                if (x.ValueKind == JsonValueKind.String) list.Add(x.GetString() ?? "");
                else if (x.ValueKind != JsonValueKind.Null) list.Add(x.ToString());
            return list;
        }

        string categoryText = S("category") ?? "";
        var category = FixtureCategory.Other;
        if (categoryText.Length > 0 && !FixtureCategories.TryParse(categoryText, out category))
            warnings.Add($"{Ctx("category")} \"{categoryText}\" is not a known category; OTHER used.");

        return new FixtureSpecs
        {
            SchemaVersion = S("schemaVersion") ?? "",
            File = file,
            FixtureTypeId = S("fixtureTypeId"),
            GdtfName = S("gdtfName"),
            GdtfDataVersion = S("gdtfDataVersion"),
            LongName = S("longName") ?? "",
            ShortName = S("shortName") ?? "",
            FullName = S("fullName") ?? "",
            Manufacturer = S("manufacturer") ?? "",
            Description = S("description") ?? "",
            GdtfDescription = S("gdtfDescription"),
            Category = category,
            CategoryText = categoryText,
            SubType = S("subType"),
            SupportedPowerNetworks = L("supportedPowerNetworks"),
            VoltageRangeV = R("voltageRangeV"),
            FrequencyRangeHz = R("frequencyRangeHz"),
            PowerConsumptionW = D("powerConsumptionW"),
            PowerSupplyType = S("powerSupplyType"),
            PowerConnectorIn = S("powerConnectorIn"),
            PowerConnectorOut = S("powerConnectorOut"),
            PowerLinkable = B("powerLinkable") ?? false,
            MaxInputPowerW = D("maxInputPowerW"),
            MaxLoadW = D("maxLoadW"),
            MaxLoadCurrentA = D("maxLoadCurrentA"),
            MaxLoadPerChannelA = D("maxLoadPerChannelA"),
            OutputChannels = I("outputChannels"),
            OutputVoltage = S("outputVoltage"),
            BatteryPowered = B("batteryPowered") ?? false,
            WeightKg = D("weightKg"),
            NoiseLevelDb = D("noiseLevelDb"),
            IpRating = S("ipRating"),
            OperatingTemperatureC = R("operatingTemperatureC"),
            MotorizedPanTilt = B("motorizedPanTilt") ?? false,
            PanRangeDeg = D("panRangeDeg"),
            TiltRangeDeg = D("tiltRangeDeg"),
            InfinitePan = B("infinitePan"),
            InfiniteTilt = B("infiniteTilt"),
            ManualPanRangeDeg = D("manualPanRangeDeg"),
            ManualTiltRangeDeg = D("manualTiltRangeDeg"),
            ControlSignal = L("controlSignal"),
            DataConnectors = L("dataConnectors"),
            DmxModes = Objects(e, "dmxModes", warnings, ctx, o => new SpecDmxMode(Str(o, "name", warnings, ctx) ?? "", (int)(Num(o, "channels") ?? 0))),
            LightSourceType = S("lightSourceType"),
            LuminousFluxLm = D("luminousFluxLm"),
            MaxLuminousIntensityCd = D("maxLuminousIntensityCd"),
            BeamAngleDeg = R("beamAngleDeg"),
            FieldAngleDeg = D("fieldAngleDeg"),
            Zoom = B("zoom") ?? false,
            LensType = S("lensType"),
            GoboSlots = I("goboSlots") ?? 0,
            GoboWheels = Objects(e, "goboWheels", warnings, ctx, o => new SpecGoboWheel(
                Str(o, "wheel", warnings, ctx) ?? "", Num(o, "slots") is { } n ? (int)n : null,
                o.TryGetProperty("rotating", out var rot) && rot.ValueKind == JsonValueKind.True)),
            FramingShutters = B("framingShutters") ?? false,
            FramingShutterBlades = I("framingShutterBlades") ?? 0,
            FramingShutterRotation = B("framingShutterRotation") ?? false,
            FramingShutterType = S("framingShutterType"),
            ColorMixing = S("colorMixing"),
            ColorWheel = B("colorWheel") ?? false,
            ColorTemperatureK = R("colorTemperatureK"),
            Prism = B("prism") ?? false,
            Prisms = Objects(e, "prisms", warnings, ctx, o => new SpecPrism(Str(o, "name", warnings, ctx) ?? "", Num(o, "facets") is { } f ? (int)f : null)),
            Frost = B("frost") ?? false,
            FrostFilters = I("frostFilters") ?? 0,
            Iris = B("iris") ?? false,
            Focus = B("focus") ?? false,
            AnimationWheel = B("animationWheel") ?? false,
            PixelCount = I("pixelCount") ?? 0,
            Source = Sources(e, warnings, ctx),
            Missing = L("_missing"),
            NotApplicable = L("_notApplicable"),
            Issues = L("_issues"),
            Refs = L("_refs"),
        };
    }

    private static string? Str(JsonElement e, string name, List<string> warnings, string ctx)
    {
        if (!e.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return null;
        if (v.ValueKind == JsonValueKind.String) return v.GetString();
        if (v.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False) return v.ToString();
        warnings.Add($"{ctx}: {name} has unexpected type {v.ValueKind}; ignored.");
        return null;
    }

    private static double? Num(JsonElement o, string name)
    {
        if (!o.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double d) && double.IsFinite(d)) return d;
        return null;
    }

    private static IReadOnlyList<T> Objects<T>(JsonElement e, string name, List<string> warnings, string ctx, Func<JsonElement, T> map)
    {
        if (!e.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return Array.Empty<T>();
        if (v.ValueKind != JsonValueKind.Array)
        {
            warnings.Add($"{ctx}: {name} has unexpected type {v.ValueKind}; ignored.");
            return Array.Empty<T>();
        }
        var list = new List<T>(v.GetArrayLength());
        foreach (var o in v.EnumerateArray())
            if (o.ValueKind == JsonValueKind.Object) list.Add(map(o));
        return list;
    }

    private static IReadOnlyDictionary<string, SpecSource> Sources(JsonElement e, List<string> warnings, string ctx)
    {
        var d = new Dictionary<string, SpecSource>(StringComparer.Ordinal);
        if (!e.TryGetProperty("_source", out var v) || v.ValueKind != JsonValueKind.Object) return d;
        foreach (var p in v.EnumerateObject())
        {
            var text = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : null;
            var source = text switch
            {
                "gdtf" => SpecSource.Gdtf,
                "gdtf-derived" => SpecSource.GdtfDerived,
                "manual" => SpecSource.Manual,
                "estimated" => SpecSource.Estimated,
                _ => SpecSource.Unknown,
            };
            if (source == SpecSource.Unknown) warnings.Add($"{ctx}: _source.{p.Name} \"{text}\" is not a known source.");
            d[p.Name] = source;
        }
        return d;
    }
}
