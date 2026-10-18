namespace Candela.Electrical;

/// <summary>
/// A kind of cable the cable tool can draw (<c>CableComponent.CableTypeId</c> refers to <see cref="Id"/>).
/// <see cref="ConnectorA"/> is the source end (it plugs into an outlet, a console output or a link output);
/// <see cref="ConnectorB"/> is the load end (it plugs into a device input). Most cables have the same connector at
/// both ends; a legacy powerCON cable is grey → blue.
/// </summary>
public sealed record CableType
{
    /// <summary>Stable id stored in projects ("dmx-xlr5", "powercon-true1", …).</summary>
    public required string Id { get; init; }

    /// <summary>Name for the tool menu, legend and schedules.</summary>
    public required string DisplayName { get; init; }

    /// <summary>What the cable carries.</summary>
    public required SignalKind Signal { get; init; }

    /// <summary>Connector at the source end.</summary>
    public required ConnectorInfo ConnectorA { get; init; }

    /// <summary>Connector at the load end.</summary>
    public required ConnectorInfo ConnectorB { get; init; }

    /// <summary>Rated current per circuit in amperes; null for data cables.</summary>
    public float? RatedCurrentA { get; init; }

    /// <summary>Conductor cross-section in mm² (power conductors; the pair conductors of data cables).</summary>
    public float ConductorCrossSectionMm2 { get; init; }

    /// <summary>Mass per metre in kg/m (used for cable weight on trusses).</summary>
    public float WeightKgPerM { get; init; }

    /// <summary>Longest recommended run in metres (DMX 300 m per line, Ethernet 100 m per link), or null.</summary>
    public float? MaxRunM { get; init; }

    /// <summary>Overlay and legend colour as sRGB hex ("#RRGGBB").</summary>
    public required string ColorHex { get; init; }

    /// <summary>Icon key of the cable tool menu and the cursor.</summary>
    public required string IconKey { get; init; }

    /// <summary>Independent power circuits (6 for Socapex/Harting, 1 otherwise).</summary>
    public int Circuits { get; init; } = 1;

    /// <summary>Phases carried (3 for 5-pin CEE, 1 otherwise).</summary>
    public int Phases { get; init; } = 1;

    /// <summary>True for cables that deliver power.</summary>
    public bool IsPower => Signal.CarriesPower();

    /// <summary>True for cables that carry lighting data.</summary>
    public bool IsData => Signal.CarriesData();

    /// <summary>The legend colour as sRGB-encoded components in 0..1 (white when <see cref="ColorHex"/> is malformed).</summary>
    public (float R, float G, float B) ColorSrgb
    {
        get
        {
            var hex = ColorHex.AsSpan().TrimStart('#');
            if (hex.Length != 6 || !int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out int rgb)) return (1f, 1f, 1f);
            return (((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f);
        }
    }

    /// <inheritdoc />
    public override string ToString() => DisplayName;
}
