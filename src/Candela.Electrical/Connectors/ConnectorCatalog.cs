using System.Collections.Frozen;
using System.Text;

namespace Candela.Electrical;

/// <summary>
/// The connector vocabulary. It covers every <c>powerConnectorIn</c>/<c>powerConnectorOut</c>/<c>dataConnectors</c>
/// value of <c>_specs/fixtures_index.json</c> (see <c>_specs/SCHEMA.md</c>) and every WiringObject
/// <c>ConnectorType</c> and legacy <c>Connector</c> <c>Type</c> found in the 1091 library GDTF files (PowerconTRUE1,
/// powerCONTRUE1TOP, NAC3FCA/NAC3FCB, NAC3MPA/NAC3MPB, PowerConBlue, CEE 7/7, IEC 60320-C13/14, XLR3/XLR5, RJ45/RJ-45,
/// etherCON, Martin PD/PDE/DCE/8-pin, DC Power Connector (…), Loose End, TBLK, PWB-CAB, Edison, …). Lookup ignores
/// case, spaces and punctuation; names that are not recognised resolve to an <see cref="ConnectorDomain.Unknown"/>
/// connector carrying the name as written, against which no compatibility checks are made.
/// </summary>
public static class ConnectorCatalog
{
    /// <summary>Family of <see cref="GenericXlr"/>: DMX on XLR with an unknown pin count.</summary>
    public const string GenericXlrFamily = "XLR";

    private static readonly ConnectorInfo[] Entries =
    [
        // ---- legacy powerCON (20 A): blue = power in, grey = power out
        Power("powerCON", "Neutrik powerCON", "powerCON", 20f, 3, "connector-powercon",
            "powercon", "power con", "neutrik powercon", "nac3"),
        Power("powerCON blue", "powerCON blue (power in)", "powerCON", 20f, 3, "connector-powercon-blue",
            "PowerConBlue", "powercon in", "NAC3FCA", "NAC3MPA", "NAC3MPA-1", "NAC3FCA blue") with { Keying = PowerconKeying.Blue },
        Power("powerCON grey", "powerCON grey (power out)", "powerCON", 20f, 3, "connector-powercon-grey",
            "PowerConGrey", "powercon gray", "PowerConWhite", "powercon out", "NAC3FCB", "NAC3MPB", "NAC3MPB-1") with { Keying = PowerconKeying.Grey },

        // ---- powerCON TRUE1 (16 A, mates under load); TRUE1 TOP is the IP65 version and mates TRUE1
        Power("powerCON TRUE1", "Neutrik powerCON TRUE1", "powerCON TRUE1", 16f, 3, "connector-true1",
            "PowerconTRUE1", "Powercon TRUE1", "TRUE1", "true 1", "NAC3MX-W", "NAC3FX-W", "NAC3MPX", "NAC3FPX"),
        Power("powerCON TRUE1 TOP", "Neutrik powerCON TRUE1 TOP", "powerCON TRUE1", 16f, 3, "connector-true1-top",
            "powerCONTRUE1TOP", "TRUE1 TOP", "true1top", "NAC3MX-W-TOP", "NAC3FX-W-TOP", "NAC3MPX-TOP", "NAC3FPX-TOP", "Seetronic"),

        // ---- mains plugs
        Power("Schuko", "Schuko (CEE 7/7)", "Schuko", 16f, 3, "connector-schuko",
            "CEE 7/7", "CEE 7/4", "CEE7", "Schuko-Stecker", "Schuko plug", "Type F"),
        Power("IEC C14", "IEC 60320 C13/C14", "IEC C14", 10f, 3, "connector-iec-c14",
            "IEC 60320-C13/14", "IEC 60320 C14", "IEC C13", "IEC C13/C14", "C13", "C14", "IEC", "kettle"),
        Power("IEC C20", "IEC 60320 C19/C20", "IEC C20", 16f, 3, "connector-iec-c20",
            "IEC 60320-C19/20", "IEC C19", "IEC C19/C20", "C19", "C20"),
        Power("CEE 16A", "CEE 16 A 230 V (3-pin)", "CEE 16A", 16f, 3, "connector-cee16",
            "CEE16", "CEE 16 A", "CEE 16A 1P", "CEE 16A 3-pin", "CEE 16A 230V", "IEC 60309 16A"),
        Power("CEE 32A", "CEE 32 A 230 V (3-pin)", "CEE 32A", 32f, 3, "connector-cee32",
            "CEE32", "CEE 32 A", "CEE 32A 1P", "CEE 32A 3-pin", "CEE 32A 230V"),
        Power("CEE 16A 3P", "CEE 16 A 400 V (5-pin)", "CEE 16A 3P", 16f, 5, "connector-cee16-3p",
            "CEE16 3P", "CEE 16A 5P", "CEE 16A 5-pin", "CEE 16A 400V", "cee16-3p") with { Phases = 3 },
        Power("CEE 32A 3P", "CEE 32 A 400 V (5-pin)", "CEE 32A 3P", 32f, 5, "connector-cee32-3p",
            "CEE32 3P", "CEE 32A 5P", "CEE 32A 5-pin", "CEE 32A 400V", "cee32-3p") with { Phases = 3 },
        Power("CEE 63A 3P", "CEE 63 A 400 V (5-pin)", "CEE 63A 3P", 63f, 5, "connector-cee63-3p",
            "CEE63", "CEE 63A", "CEE63 3P", "CEE 63A 5P", "CEE 63A 5-pin", "CEE 63A 400V", "cee63-3p") with { Phases = 3 },

        // ---- multicores (6 circuits)
        new ConnectorInfo
        {
            Id = "Socapex", DisplayName = "Socapex 19-pin (6 circuits)", Family = "Socapex", Domain = ConnectorDomain.Multicore,
            RatedCurrentA = 16f, Pins = 19, Circuits = 6, IllustrationKey = "connector-socapex",
            Aliases = ["Socapex 19", "Socapex 19-pin", "LSC19", "19-pin", "Socapex 19 pin", "socapex19"],
        },
        new ConnectorInfo
        {
            Id = "Harting 16", DisplayName = "Harting Han 16E (6 circuits)", Family = "Harting 16", Domain = ConnectorDomain.Multicore,
            RatedCurrentA = 16f, Pins = 16, Circuits = 6, IllustrationKey = "connector-harting16",
            Aliases = ["Harting16", "Harting 16-pin", "Han 16E", "Han16E", "HAN16", "Harting"],
        },

        // ---- terminations that take whatever plug or wiring the site has
        Any("Fixed Cable", "Fixed cable (captive lead)", "connector-fixed-cable", "fixed lead", "captive cable", "hardwired"),
        Any("Terminal Block", "Terminal block", "connector-terminal-block", "TBLK", "terminal", "terminals"),
        Any("Loose End", "Loose end", "connector-loose-end", "LooseEnd", "open end", "bare wires"),

        // ---- manufacturer specific power / hybrid connectors
        Power("IP moulded connector", "IP moulded connector", "IP moulded connector", null, 0, "connector-ip-moulded",
            "IP molded connector", "IP moulded", "IP molded"),
        Hybrid("Martin proprietary (P3/PDE)", "Martin P3/PDE (power + data)", "connector-martin-pde",
            "Martin PD", "Martin PDE", "Martin P3", "Martin proprietary", "Martin P3/PDE"),
        Hybrid("Martin DCE", "Martin DCE (power + data)", "connector-martin-dce", "Martin DCE connector"),
        Hybrid("Martin 8-pin", "Martin 8-pin (power + data)", "connector-martin-8pin", "Martin 8 pin", "Martin8pin"),
        Hybrid("4-pin power/data connector", "4-pin power/data connector", "connector-hybrid-4pin", "4 pin power data connector"),
        Hybrid("PWB-CAB Connector", "Astera PWB-CAB connector", "connector-pwb-cab", "PWB CAB Connector", "PWB-CAB"),
        Power("DC Jack", "DC jack", "DC Jack", 5f, 2, "connector-dc-jack",
            "DC Power Connector (5,5mm x 2,1mm)", "DC Power Connector (5,5mm x 2,5mm)", "DC Power Connector (3,8mm x 1,35mm)",
            "DC (external PSU)", "DC jack", "barrel connector", "DC Power Connector") with { IsDc = true },
        Power("DIN3 (low-voltage DC)", "DIN 3-pin (low-voltage DC)", "DIN3 (low-voltage DC)", 5f, 3, "connector-din3",
            "DIN3", "DIN 3", "DIN 3-pin") with { IsDc = true },
        Power("XLR4 (DC)", "XLR 4-pin (DC)", "XLR4 (DC)", 10f, 4, "connector-xlr4",
            "XLR4", "XLR 4", "XLR 4-pin", "4-pin XLR") with { IsDc = true },
        Power("E27 lamp socket", "E27 lamp socket", "E27 lamp socket", 4f, 2, "connector-e27", "Edison", "E27", "Edison screw"),
        Power("Track adapter", "Track adapter", "Track adapter", 16f, 0, "connector-track-adapter", "track", "3-circuit track adapter"),
        new ConnectorInfo
        {
            Id = "Battery (internal)", DisplayName = "Internal battery", Family = "Battery (internal)", Domain = ConnectorDomain.Power,
            IsInternal = true, IllustrationKey = "connector-battery", Aliases = ["Battery", "internal battery", "From Battery"],
        },

        // ---- data
        Data("XLR5", "XLR 5-pin (DMX)", "XLR5", ConnectorDomain.Dmx, 5, "connector-xlr5", "XLR 5", "XLR 5-pin", "5-pin XLR", "DMX5", "XLR5 DMX"),
        Data("XLR3", "XLR 3-pin (DMX)", "XLR3", ConnectorDomain.Dmx, 3, "connector-xlr3", "XLR 3", "XLR 3-pin", "3-pin XLR", "DMX3", "XLR3 DMX"),
        Data("XLR", "XLR (DMX, pin count unknown)", GenericXlrFamily, ConnectorDomain.Dmx, 0, "connector-xlr", "DMX XLR", "XLR DMX"),
        Data("RJ45", "RJ45", "RJ45", ConnectorDomain.Ethernet, 8, "connector-rj45", "RJ-45", "RJ 45", "8P8C", "Ethernet"),
        Data("etherCON", "Neutrik etherCON (RJ45)", "RJ45", ConnectorDomain.Ethernet, 8, "connector-ethercon",
            "EtherCon", "ethercon cat6", "ethercon cat5", "NE8FAV", "NE8MC"),
        Data("BNC", "BNC (video)", "BNC", ConnectorDomain.Other, 2, "connector-bnc", "BNC video"),
        Data("USB", "USB (service)", "USB", ConnectorDomain.Other, 4, "connector-usb", "USB-A", "USB-B", "USB-C", "micro USB"),
    ];

    private static readonly FrozenDictionary<string, ConnectorInfo> ByKey = BuildIndex();

    /// <summary>Every catalogue connector.</summary>
    public static IReadOnlyList<ConnectorInfo> All => Entries;

    /// <summary>Generic DMX XLR connector used when only "DMX512" is known (accepts XLR3 and XLR5 without warnings).</summary>
    public static ConnectorInfo GenericXlr { get; } = Find("XLR")!;

    /// <summary>A connector that carries any power plug (fixed cable / terminal block semantics).</summary>
    public static ConnectorInfo AnyTermination { get; } = Find("Loose End")!;

    /// <summary>Unknown connector with no name.</summary>
    public static ConnectorInfo Unspecified { get; } = UnknownConnector("");

    /// <summary>Catalogue connector for a name (canonical, alias or GDTF spelling), or null when not recognised.</summary>
    public static ConnectorInfo? Find(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        return ByKey.TryGetValue(Key(name), out var c) ? c : null;
    }

    /// <summary>
    /// Connector for a name as written in GDTF, <c>_specs</c> or a power outlet. Unrecognised names give an
    /// <see cref="ConnectorDomain.Unknown"/> connector whose <see cref="ConnectorInfo.Id"/> is the trimmed name.
    /// For a <c>_specs</c> list value such as "powerCON / powerCON TRUE1", the first alternative is returned; use
    /// <see cref="ResolveAll"/> to get every one.
    /// </summary>
    public static ConnectorInfo Resolve(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return Unspecified;
        var direct = Find(name);
        if (direct is not null) return direct;
        var parts = SplitAlternatives(name);
        if (parts.Count > 1) return Resolve(parts[0]);
        return UnknownConnector(name.Trim());
    }

    /// <summary>
    /// Every connector named by a <c>_specs</c> value: "A / B" lists alternatives (either fits), "A + B" lists
    /// connectors that are all present. Empty for null or blank text.
    /// </summary>
    public static IReadOnlyList<ConnectorInfo> ResolveAll(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<ConnectorInfo>();
        if (Find(text) is { } whole) return [whole];
        var parts = SplitAlternatives(text);
        var result = new List<ConnectorInfo>(parts.Count);
        foreach (var p in parts) result.Add(Find(p) ?? UnknownConnector(p));
        return result;
    }

    /// <summary>Splits a <c>_specs</c> connector value on " / " and " + " (the two list forms of SCHEMA.md).</summary>
    public static IReadOnlyList<string> SplitAlternatives(string text)
    {
        var parts = text.Split([" / ", " + "], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 0 ? [text.Trim()] : parts;
    }

    /// <summary>True when a <c>_specs</c> connector value lists several connectors that are all present ("A + B").</summary>
    public static bool ListsSeveralPresent(string? text) => text is not null && text.Contains(" + ", StringComparison.Ordinal);

    /// <summary>Default current limit of a port with this connector (A), or null for data/unknown connectors.</summary>
    public static float? DefaultCurrentLimitA(ConnectorInfo connector) => connector.RatedCurrentA;

    private static ConnectorInfo UnknownConnector(string name) => new()
    {
        Id = name,
        DisplayName = name.Length == 0 ? "unspecified" : name,
        Family = name,
        Domain = ConnectorDomain.Unknown,
    };

    private static ConnectorInfo Power(string id, string display, string family, float? ratedA, int pins, string illustration, params string[] aliases) =>
        new()
        {
            Id = id, DisplayName = display, Family = family, Domain = ConnectorDomain.Power, RatedCurrentA = ratedA, Pins = pins,
            IllustrationKey = illustration, Aliases = aliases,
        };

    private static ConnectorInfo Hybrid(string id, string display, string illustration, params string[] aliases) =>
        new() { Id = id, DisplayName = display, Family = id, Domain = ConnectorDomain.Hybrid, IllustrationKey = illustration, Aliases = aliases };

    private static ConnectorInfo Any(string id, string display, string illustration, params string[] aliases) =>
        new() { Id = id, DisplayName = display, Family = id, Domain = ConnectorDomain.Any, IllustrationKey = illustration, Aliases = aliases };

    private static ConnectorInfo Data(string id, string display, string family, ConnectorDomain domain, int pins, string illustration, params string[] aliases) =>
        new() { Id = id, DisplayName = display, Family = family, Domain = domain, Pins = pins, IllustrationKey = illustration, Aliases = aliases };

    private static FrozenDictionary<string, ConnectorInfo> BuildIndex()
    {
        var d = new Dictionary<string, ConnectorInfo>(StringComparer.Ordinal);
        foreach (var c in Entries)
        {
            d.TryAdd(Key(c.Id), c);
            d.TryAdd(Key(c.DisplayName), c);
            foreach (var a in c.Aliases) d.TryAdd(Key(a), c);
        }
        return d.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <summary>Lookup key: lower-case letters and digits only ("powerCON TRUE1 TOP" = "powerCONTRUE1TOP").</summary>
    internal static string Key(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char ch in text)
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        return sb.ToString();
    }
}
