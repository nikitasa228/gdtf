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
    /// "follow_spot"), case-insensitively. Unknown text gives false and <see cref="FixtureCategory.Other"/>.
    /// </summary>
    public static bool TryParse(string? text, out FixtureCategory category)
    {
        if (!string.IsNullOrWhiteSpace(text) && Lookup.TryGetValue(Key(text), out category)) return true;
        category = FixtureCategory.Other;
        return false;
    }

    /// <summary>Like <see cref="TryParse"/> but returns <see cref="FixtureCategory.Other"/> for unknown text.</summary>
    public static FixtureCategory Parse(string? text) => TryParse(text, out var c) ? c : FixtureCategory.Other;

    /// <summary>
    /// Best guess of the category from words of a model name, for files without a specs record ("Robin T1 Profile" →
    /// PROFILE, "COLORado Solo Bar 6" → LED BAR, "Hazer 1000" → HAZE). Returns <see cref="FixtureCategory.Other"/> when
    /// no word gives a hint. The order of the checks resolves names with several hints ("Beam Wash" → WASH BEAM).
    /// </summary>
    public static FixtureCategory Guess(string? modelName)
    {
        var words = new HashSet<string>(FixtureNames.Words(modelName), StringComparer.Ordinal);
        if (words.Count == 0) return FixtureCategory.Other;
        bool Any(params string[] w)
        {
            foreach (var x in w)
                if (words.Contains(x)) return true;
            return false;
        }
        if (Any("follow", "followspot")) return FixtureCategory.FollowSpot;
        if (Any("laser", "lasers")) return FixtureCategory.Laser;
        if (Any("haze", "hazer", "hazers")) return FixtureCategory.Haze;
        if (Any("fog", "fogger", "smoke", "fogmachine")) return FixtureCategory.Fog;
        if (Any("flame", "fire", "flamer")) return FixtureCategory.GeneratorFire;
        if (Any("fan", "fans", "wind")) return FixtureCategory.Fan;
        if (Any("blinder", "molefay")) return FixtureCategory.Blinder;
        if (Any("strobe", "strobes")) return FixtureCategory.Strobe;
        if (Any("uv", "blacklight")) return FixtureCategory.Uv;
        if (Any("scanner", "scaner", "scan")) return FixtureCategory.Scanner;
        if (Any("bsw", "hybrid")) return FixtureCategory.Bsw;
        if (Any("bar", "batten", "tube", "pixelbar", "linear", "strip")) return FixtureCategory.LedBar;
        if (Any("par", "pars", "parcan")) return FixtureCategory.Par;
        if (Any("profile", "ellipsoidal", "leko", "framing")) return FixtureCategory.Profile;
        if (Any("beam") && Any("wash")) return FixtureCategory.WashBeam;
        if (Any("spot")) return FixtureCategory.Spot;
        if (Any("beam")) return FixtureCategory.Beam;
        if (Any("wash", "fresnel", "cyc", "panel")) return FixtureCategory.Wash;
        return FixtureCategory.Other;
    }

    private static string Key(string text) => FixtureNames.Key(text);

    private static Dictionary<string, FixtureCategory> BuildLookup()
    {
        var d = new Dictionary<string, FixtureCategory>(StringComparer.Ordinal);
        foreach (var c in Enum.GetValues<FixtureCategory>())
        {
            d[Key(c.ToString())] = c;
            d[Key(c.DisplayName())] = c;
        }
        d["scanner"] = FixtureCategory.Scanner;
        d["followspots"] = FixtureCategory.FollowSpot;
        d["ledbars"] = FixtureCategory.LedBar;
        d["batten"] = FixtureCategory.LedBar;
        d["hazer"] = FixtureCategory.Haze;
        d["fogger"] = FixtureCategory.Fog;
        d["flame"] = FixtureCategory.GeneratorFire;
        d["fire"] = FixtureCategory.GeneratorFire;
        return d;
    }
}
