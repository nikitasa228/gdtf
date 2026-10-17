namespace Candela.Gdtf;

/// <summary>
/// Library category of a fixture (the sub-tabs of the editor's fixture library). Display names are fixed strings used
/// by <c>_specs/fixtures_index.json</c> and the UI; see <see cref="FixtureCategories.DisplayName"/>.
/// </summary>
public enum FixtureCategory
{
    /// <summary>PROFILE — ellipsoidal / framing profile spots.</summary>
    Profile,

    /// <summary>WASH.</summary>
    Wash,

    /// <summary>WASH BEAM — hybrid wash/beam fixtures.</summary>
    WashBeam,

    /// <summary>BEAM.</summary>
    Beam,

    /// <summary>SPOT.</summary>
    Spot,

    /// <summary>FOLLOW SPOT.</summary>
    FollowSpot,

    /// <summary>LED BAR — battens and linear fixtures.</summary>
    LedBar,

    /// <summary>PAR.</summary>
    Par,

    /// <summary>HAZE — hazers.</summary>
    Haze,

    /// <summary>FOG — fog machines.</summary>
    Fog,

    /// <summary>FAN — effect fans.</summary>
    Fan,

    /// <summary>SCANER — mirror scanners (display spelling as used by the project).</summary>
    Scanner,

    /// <summary>LASER.</summary>
    Laser,

    /// <summary>STROBE.</summary>
    Strobe,

    /// <summary>BLINDER.</summary>
    Blinder,

    /// <summary>UV — ultraviolet fixtures.</summary>
    Uv,

    /// <summary>BSW — beam/spot/wash hybrids.</summary>
    Bsw,

    /// <summary>GENERATOR FIRE — flame generators.</summary>
    GeneratorFire,

    /// <summary>OTHER — not a light (brackets, distributors, controllers, …) or unknown.</summary>
    Other,
}

/// <summary>Display names, ordering and parsing of <see cref="FixtureCategory"/>.</summary>
public static class FixtureCategories
{
    private static readonly string[] Names =
    {
        "PROFILE", "WASH", "WASH BEAM", "BEAM", "SPOT", "FOLLOW SPOT", "LED BAR", "PAR", "HAZE", "FOG", "FAN", "SCANER",
        "LASER", "STROBE", "BLINDER", "UV", "BSW", "GENERATOR FIRE", "OTHER",
    };

    private static readonly Dictionary<string, FixtureCategory> Lookup = BuildLookup();

    /// <summary>All categories in library tab order.</summary>
    public static IReadOnlyList<FixtureCategory> All { get; } = Enum.GetValues<FixtureCategory>();

    /// <summary>Display name exactly as used by the project (e.g. "WASH BEAM", "SCANER", "GENERATOR FIRE").</summary>
    public static string DisplayName(this FixtureCategory category) =>
        (uint)category < (uint)Names.Length ? Names[(int)category] : "OTHER";

    /// <summary>
    /// Parses a display name (as in <c>_specs</c>), an enum member name, or a spelling variant ("Wash/Beam", "scanner",
    /// "follow_spot"), case-insensitively.
    /// </summary>
    public static bool TryParse(string? text, out FixtureCategory category)
    {
        category = FixtureCategory.Other;
        if (string.IsNullOrWhiteSpace(text)) return false;
        return Lookup.TryGetValue(Key(text), out category);
    }

    /// <summary>Like <see cref="TryParse"/> but returns <see cref="FixtureCategory.Other"/> for unknown text.</summary>
    public static FixtureCategory Parse(string? text) => TryParse(text, out var c) ? c : FixtureCategory.Other;

    private static string Key(string text)
    {
        Span<char> buffer = stackalloc char[text.Length];
        int n = 0;
        foreach (char c in text)
            if (char.IsLetterOrDigit(c)) buffer[n++] = char.ToUpperInvariant(c);
        return new string(buffer[..n]);
    }

    private static Dictionary<string, FixtureCategory> BuildLookup()
    {
        var d = new Dictionary<string, FixtureCategory>(StringComparer.Ordinal);
        foreach (var c in Enum.GetValues<FixtureCategory>())
        {
            d[Key(c.ToString())] = c;
            d[Key(c.DisplayName())] = c;
        }
        d["SCANNER"] = FixtureCategory.Scanner;
        d["FOLLOWSPOTS"] = FixtureCategory.FollowSpot;
        d["LEDBARS"] = FixtureCategory.LedBar;
        d["BATTEN"] = FixtureCategory.LedBar;
        d["HAZER"] = FixtureCategory.Haze;
        d["FOGGER"] = FixtureCategory.Fog;
        d["FLAME"] = FixtureCategory.GeneratorFire;
        d["FIRE"] = FixtureCategory.GeneratorFire;
        return d;
    }
}
