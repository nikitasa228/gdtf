namespace Candela.Electrical;

/// <summary>
/// The cable types offered by the cable tool. <see cref="Default"/> holds the built-in stage cables; a custom catalogue
/// can extend it (ids are case-insensitive and must be unique). Ratings are those of the cable assembly — the lower of
/// connector and conductor rating — and weights are typical rubber-sheathed touring cable (H07RN-F, DMX 110 Ω pair
/// cable, tour-grade Cat6).
/// </summary>
public sealed class CableCatalog
{
    private readonly Dictionary<string, CableType> _byId;
    private readonly List<CableType> _all;

    /// <summary>Creates a catalogue from cable types.</summary>
    /// <exception cref="ArgumentException">Two types share an id.</exception>
    public CableCatalog(IEnumerable<CableType> types)
    {
        ArgumentNullException.ThrowIfNull(types);
        _all = new List<CableType>();
        _byId = new Dictionary<string, CableType>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in types)
        {
            if (!_byId.TryAdd(t.Id, t)) throw new ArgumentException($"Duplicate cable type id '{t.Id}'.", nameof(types));
            _all.Add(t);
        }
    }

    /// <summary>The built-in catalogue (DMX XLR3/XLR5, etherCON Cat6, powerCON, TRUE1, TRUE1 TOP, Schuko, IEC, CEE 16/32/63 A, Socapex, Harting 16).</summary>
    public static CableCatalog Default { get; } = new(BuiltIn());

    /// <summary>All types in menu order.</summary>
    public IReadOnlyList<CableType> All => _all;

    /// <summary>Type by id (case-insensitive), or null.</summary>
    public CableType? Find(string? id) => id is not null && _byId.TryGetValue(id, out var t) ? t : null;

    /// <summary>Types of one signal, in menu order.</summary>
    public IEnumerable<CableType> OfSignal(SignalKind signal) => _all.Where(t => t.Signal == signal);

    /// <summary>A new catalogue with these types added (or replacing built-in types with the same id).</summary>
    public CableCatalog With(IEnumerable<CableType> extra)
    {
        ArgumentNullException.ThrowIfNull(extra);
        var merged = new List<CableType>(_all);
        foreach (var t in extra)
        {
            int i = merged.FindIndex(m => string.Equals(m.Id, t.Id, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) merged[i] = t;
            else merged.Add(t);
        }
        return new CableCatalog(merged);
    }

    /// <summary>Well-known ids of the built-in types.</summary>
    public static class Ids
    {
        /// <summary>DMX cable, XLR 3-pin.</summary>
        public const string DmxXlr3 = "dmx-xlr3";

        /// <summary>DMX cable, XLR 5-pin.</summary>
        public const string DmxXlr5 = "dmx-xlr5";

        /// <summary>Ethernet, etherCON Cat6.</summary>
        public const string Ethernet = "ethernet";

        /// <summary>Legacy powerCON (grey → blue), 20 A.</summary>
        public const string Powercon = "powercon";

        /// <summary>powerCON TRUE1, 16 A.</summary>
        public const string PowerconTrue1 = "powercon-true1";

        /// <summary>powerCON TRUE1 TOP, 16 A.</summary>
        public const string PowerconTrue1Top = "powercon-true1-top";

        /// <summary>Schuko extension, 16 A.</summary>
        public const string Schuko = "schuko";

        /// <summary>IEC mains lead (Schuko plug → C13), 10 A.</summary>
        public const string Iec = "iec";

        /// <summary>CEE 16 A single phase.</summary>
        public const string Cee16 = "cee16";

        /// <summary>CEE 32 A three phase (5-pin).</summary>
        public const string Cee32ThreePhase = "cee32-3p";

        /// <summary>CEE 63 A three phase (5-pin).</summary>
        public const string Cee63ThreePhase = "cee63-3p";

        /// <summary>Socapex 19-pin multicore, 6 circuits.</summary>
        public const string Socapex = "socapex";

        /// <summary>Harting 16-pin multicore, 6 circuits.</summary>
        public const string Harting16 = "harting16";
    }

    private static IEnumerable<CableType> BuiltIn()
    {
        static ConnectorInfo C(string name) => ConnectorCatalog.Find(name) ?? throw new InvalidOperationException($"Connector '{name}' missing from the catalogue.");

        yield return new CableType
        {
            Id = Ids.DmxXlr3,
            DisplayName = "DMX cable XLR 3-pin",
            Signal = SignalKind.Dmx512,
            ConnectorA = C("XLR3"),
            ConnectorB = C("XLR3"),
            ConductorCrossSectionMm2 = 0.22f,
            WeightKgPerM = 0.045f,
            MaxRunM = 300f,
            ColorHex = "#A855F7",
            IconKey = "cable-dmx-xlr3",
        };
        yield return new CableType
        {
            Id = Ids.DmxXlr5,
            DisplayName = "DMX cable XLR 5-pin",
            Signal = SignalKind.Dmx512,
            ConnectorA = C("XLR5"),
            ConnectorB = C("XLR5"),
            ConductorCrossSectionMm2 = 0.22f,
            WeightKgPerM = 0.055f,
            MaxRunM = 300f,
            ColorHex = "#7C3AED",
            IconKey = "cable-dmx-xlr5",
        };
        yield return new CableType
        {
            Id = Ids.Ethernet,
            DisplayName = "Ethernet etherCON Cat6",
            Signal = SignalKind.Ethernet,
            ConnectorA = C("etherCON"),
            ConnectorB = C("etherCON"),
            ConductorCrossSectionMm2 = 0.2f,
            WeightKgPerM = 0.065f,
            MaxRunM = 100f,
            ColorHex = "#10B981",
            IconKey = "cable-ethernet",
        };
        yield return new CableType
        {
            Id = Ids.Powercon,
            DisplayName = "powerCON link (grey → blue) 20 A",
            Signal = SignalKind.Power,
            ConnectorA = C("powerCON grey"),
            ConnectorB = C("powerCON blue"),
            RatedCurrentA = 20f,
            ConductorCrossSectionMm2 = 2.5f,
            WeightKgPerM = 0.2f,
            ColorHex = "#1E3A8A",
            IconKey = "cable-powercon",
        };
        yield return new CableType
        {
            Id = Ids.PowerconTrue1,
            DisplayName = "powerCON TRUE1 16 A",
            Signal = SignalKind.Power,
            ConnectorA = C("powerCON TRUE1"),
            ConnectorB = C("powerCON TRUE1"),
            RatedCurrentA = 16f,
            ConductorCrossSectionMm2 = 2.5f,
            WeightKgPerM = 0.2f,
            ColorHex = "#FACC15",
            IconKey = "cable-true1",
        };
        yield return new CableType
        {
            Id = Ids.PowerconTrue1Top,
            DisplayName = "powerCON TRUE1 TOP 16 A",
            Signal = SignalKind.Power,
            ConnectorA = C("powerCON TRUE1 TOP"),
            ConnectorB = C("powerCON TRUE1 TOP"),
            RatedCurrentA = 16f,
            ConductorCrossSectionMm2 = 2.5f,
            WeightKgPerM = 0.21f,
            ColorHex = "#CA8A04",
            IconKey = "cable-true1-top",
        };
        yield return new CableType
        {
            Id = Ids.Schuko,
            DisplayName = "Schuko extension 16 A",
            Signal = SignalKind.Power,
            ConnectorA = C("Schuko"),
            ConnectorB = C("Schuko"),
            RatedCurrentA = 16f,
            ConductorCrossSectionMm2 = 1.5f,
            WeightKgPerM = 0.13f,
            ColorHex = "#14B8A6",
            IconKey = "cable-schuko",
        };
        yield return new CableType
        {
            Id = Ids.Iec,
            DisplayName = "IEC mains lead (Schuko → C13) 10 A",
            Signal = SignalKind.Power,
            ConnectorA = C("Schuko"),
            ConnectorB = C("IEC C14"),
            RatedCurrentA = 10f,
            ConductorCrossSectionMm2 = 1.0f,
            WeightKgPerM = 0.08f,
            ColorHex = "#EC4899",
            IconKey = "cable-iec",
        };
        yield return new CableType
        {
            Id = Ids.Cee16,
            DisplayName = "CEE 16 A single phase",
            Signal = SignalKind.Power,
            ConnectorA = C("CEE 16A"),
            ConnectorB = C("CEE 16A"),
            RatedCurrentA = 16f,
            ConductorCrossSectionMm2 = 2.5f,
            WeightKgPerM = 0.2f,
            ColorHex = "#3B82F6",
            IconKey = "cable-cee16",
        };
        yield return new CableType
        {
            Id = Ids.Cee32ThreePhase,
            DisplayName = "CEE 32 A three phase",
            Signal = SignalKind.Power,
            ConnectorA = C("CEE 32A 3P"),
            ConnectorB = C("CEE 32A 3P"),
            RatedCurrentA = 32f,
            ConductorCrossSectionMm2 = 6f,
            WeightKgPerM = 0.55f,
            ColorHex = "#EF4444",
            IconKey = "cable-cee32-3p",
            Phases = 3,
        };
        yield return new CableType
        {
            Id = Ids.Cee63ThreePhase,
            DisplayName = "CEE 63 A three phase",
            Signal = SignalKind.Power,
            ConnectorA = C("CEE 63A 3P"),
            ConnectorB = C("CEE 63A 3P"),
            RatedCurrentA = 63f,
            ConductorCrossSectionMm2 = 16f,
            WeightKgPerM = 1.25f,
            ColorHex = "#991B1B",
            IconKey = "cable-cee63-3p",
            Phases = 3,
        };
        yield return new CableType
        {
            Id = Ids.Socapex,
            DisplayName = "Socapex 19-pin multicore (6 × 16 A)",
            Signal = SignalKind.Multicore,
            ConnectorA = C("Socapex"),
            ConnectorB = C("Socapex"),
            RatedCurrentA = 16f,
            ConductorCrossSectionMm2 = 2.5f,
            WeightKgPerM = 0.95f,
            ColorHex = "#84CC16",
            IconKey = "cable-socapex",
            Circuits = 6,
        };
        yield return new CableType
        {
            Id = Ids.Harting16,
            DisplayName = "Harting 16-pin multicore (6 × 16 A)",
            Signal = SignalKind.Multicore,
            ConnectorA = C("Harting 16"),
            ConnectorB = C("Harting 16"),
            RatedCurrentA = 16f,
            ConductorCrossSectionMm2 = 2.5f,
            WeightKgPerM = 0.85f,
            ColorHex = "#65A30D",
            IconKey = "cable-harting16",
            Circuits = 6,
        };
    }
}
