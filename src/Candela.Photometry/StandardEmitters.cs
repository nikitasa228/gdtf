using System.Numerics;

namespace Candela.Photometry;

/// <summary>A catalogue entry of <see cref="StandardEmitters"/>.</summary>
/// <param name="Code">Colour code as used in <c>_specs</c> <c>colorMixing</c> strings ("R", "G", "WW", "UV", …).</param>
/// <param name="Name">Display name.</param>
/// <param name="Chromaticity">Typical CIE 1931 (x, y).</param>
/// <param name="RelativeFlux">Typical full-level luminous flux relative to the R+G+B white of the same engine (R+G+B = 1).</param>
public sealed record StandardEmitter(string Code, string Name, Vector2 Chromaticity, float RelativeFlux)
{
    /// <summary>An <see cref="Emitter"/> of this colour with full-level flux <paramref name="maxFluxLm"/>.</summary>
    public Emitter ToEmitter(float maxFluxLm) => new(Name, Chromaticity, maxFluxLm);
}

/// <summary>
/// Typical LED emitters for fixtures whose GDTF file has no emitter data, and a builder for the emitter set of a
/// <c>_specs</c> <c>colorMixing</c> code such as "RGBW", "RGBAL+UV" or "Tunable White (CW/WW)".
/// </summary>
/// <remarks>
/// <para>Chromaticities are typical values for high-power stage-lighting LEDs (manufacturer datasheets of AlInGaP /
/// InGaN and phosphor-converted dies), consistent with the emitter data found in the bundled GDTF files:</para>
/// <list type="table">
/// <listheader><term>Code</term><description>Emitter, CIE 1931 (x, y), relative flux</description></listheader>
/// <item><term>R</term><description>red, ≈ 625 nm dominant: (0.700, 0.299); R, G and B fluxes are solved so that R+G+B at
/// full mix to D65 — about 0.26 : 0.71 : 0.04</description></item>
/// <item><term>G</term><description>green, ≈ 525 nm: (0.170, 0.700)</description></item>
/// <item><term>B</term><description>blue, ≈ 455 nm: (0.150, 0.030)</description></item>
/// <item><term>W</term><description>white 6500 K on the Planckian locus: (0.3135, 0.3236), 0.80</description></item>
/// <item><term>A</term><description>amber, ≈ 590 nm: (0.575, 0.424), 0.30</description></item>
/// <item><term>L</term><description>phosphor-converted lime: (0.405, 0.545), 0.55</description></item>
/// <item><term>C</term><description>cyan, ≈ 500 nm: (0.060, 0.520), 0.25</description></item>
/// <item><term>I</term><description>indigo / royal blue, ≈ 445 nm: (0.160, 0.016), 0.03</description></item>
/// <item><term>V</term><description>violet, ≈ 420 nm: (0.170, 0.006), 0.01</description></item>
/// <item><term>BG</term><description>blue-green, ≈ 490 nm: (0.075, 0.330), 0.15</description></item>
/// <item><term>UV</term><description>near UV, ≈ 405 nm: (0.172, 0.005), 0.002 (almost no luminous output)</description></item>
/// <item><term>WW</term><description>warm white 3000 K: (0.4369, 0.4041), 0.75</description></item>
/// <item><term>NW</term><description>neutral white 4000 K: (0.3805, 0.3768), 0.80</description></item>
/// <item><term>CW</term><description>cool white 6500 K: (0.3135, 0.3236), 0.80</description></item>
/// </list>
/// <para>The relative fluxes describe a typical multi-chip engine (each colour's die at the same drive current) and
/// only serve to split a fixture's published total flux among its emitters.</para>
/// </remarks>
public static class StandardEmitters
{
    private static readonly char[] _listSeparators = ['/', ',', '+', ' '];
    private static readonly StandardEmitter[] _catalog = BuildCatalog();
    private static readonly Dictionary<string, StandardEmitter> _byCode =
        _catalog.ToDictionary(e => e.Code, StringComparer.OrdinalIgnoreCase);

    /// <summary>All standard emitters.</summary>
    public static IReadOnlyList<StandardEmitter> Catalog => _catalog;

    /// <summary>Typical red LED chromaticity (≈ 625 nm dominant).</summary>
    public static Vector2 Red => _byCode["R"].Chromaticity;

    /// <summary>Typical green LED chromaticity (≈ 525 nm).</summary>
    public static Vector2 Green => _byCode["G"].Chromaticity;

    /// <summary>Typical blue LED chromaticity (≈ 455 nm).</summary>
    public static Vector2 Blue => _byCode["B"].Chromaticity;

    /// <summary>Typical white LED chromaticity (6500 K).</summary>
    public static Vector2 White => _byCode["W"].Chromaticity;

    /// <summary>Typical amber LED chromaticity (≈ 590 nm).</summary>
    public static Vector2 Amber => _byCode["A"].Chromaticity;

    /// <summary>Typical phosphor-converted lime LED chromaticity.</summary>
    public static Vector2 Lime => _byCode["L"].Chromaticity;

    /// <summary>Typical cyan LED chromaticity (≈ 500 nm).</summary>
    public static Vector2 Cyan => _byCode["C"].Chromaticity;

    /// <summary>Looks up a catalogue entry by code (case-insensitive).</summary>
    public static bool TryGet(string code, out StandardEmitter emitter)
    {
        ArgumentNullException.ThrowIfNull(code);
        if (_byCode.TryGetValue(code.Trim(), out var found))
        {
            emitter = found;
            return true;
        }

        emitter = null!;
        return false;
    }

    /// <summary>Red, green and blue emitters sharing <paramref name="totalFluxLm"/> so that all at full give D65 white.</summary>
    public static Emitter[] Rgb(float totalFluxLm) => FromColorMixing("RGB", totalFluxLm);

    /// <summary>RGBW emitters sharing <paramref name="totalFluxLm"/> (all at full).</summary>
    public static Emitter[] Rgbw(float totalFluxLm) => FromColorMixing("RGBW", totalFluxLm);

    /// <summary>RGBA emitters sharing <paramref name="totalFluxLm"/> (all at full).</summary>
    public static Emitter[] Rgba(float totalFluxLm) => FromColorMixing("RGBA", totalFluxLm);

    /// <summary>RGBAL emitters sharing <paramref name="totalFluxLm"/> (all at full).</summary>
    public static Emitter[] Rgbal(float totalFluxLm) => FromColorMixing("RGBAL", totalFluxLm);

    /// <summary>
    /// Emitters for a <c>_specs</c> <c>colorMixing</c> code, in the order the code lists them, sharing
    /// <paramref name="totalFluxLm"/> (the fixture's output with every emitter at full) in proportion to their typical
    /// relative flux.
    /// </summary>
    /// <remarks>
    /// Grammar: an optional leading run of single-letter codes (R G B W A L C I V), then "+"-separated codes (WW, CW,
    /// NW, UV, BG or single letters); "Tunable White (CW/WW)" lists the codes in parentheses (CW + WW when there are
    /// none). "CMY" (subtractive), "None" and empty strings give no emitters; a trailing "CMY" after LED letters
    /// ("RGBLCMY") is the subtractive part and is skipped. Unknown codes are skipped and reported through
    /// <paramref name="unrecognized"/>, never thrown.
    /// </remarks>
    /// <param name="colorMixing">The code, e.g. "RGBW", "RGBWA+UV", "RGB+WW", "Tunable White (CW/WW)".</param>
    /// <param name="totalFluxLm">Total luminous flux with every emitter at full, lm.</param>
    /// <param name="unrecognized">Receives codes that could not be interpreted (optional).</param>
    public static Emitter[] FromColorMixing(string? colorMixing, float totalFluxLm, ICollection<string>? unrecognized = null)
    {
        var codes = ParseCodes(colorMixing, unrecognized);
        if (codes.Count == 0)
            return [];
        float total = float.IsFinite(totalFluxLm) && totalFluxLm > 0f ? totalFluxLm : 0f;
        double weightSum = 0;
        foreach (var c in codes)
            weightSum += c.RelativeFlux;
        var result = new Emitter[codes.Count];
        for (int i = 0; i < codes.Count; i++)
            result[i] = codes[i].ToEmitter(weightSum > 0 ? (float)(total * codes[i].RelativeFlux / weightSum) : 0f);
        return result;
    }

    private static List<StandardEmitter> ParseCodes(string? colorMixing, ICollection<string>? unrecognized)
    {
        var result = new List<StandardEmitter>();
        string code = (colorMixing ?? "").Trim().ToUpperInvariant();
        if (code.Length == 0 || code is "NONE" or "CMY" or "N/A" or "NA")
            return result;

        if (code.Contains("TUNABLE", StringComparison.Ordinal))
        {
            int open = code.IndexOf('('), close = code.LastIndexOf(')');
            if (open >= 0 && close > open)
            {
                foreach (var token in code[(open + 1)..close].Split(_listSeparators, StringSplitOptions.RemoveEmptyEntries))
                    AddToken(token, result, unrecognized);
            }

            if (result.Count == 0)
            {
                result.Add(_byCode["CW"]);
                result.Add(_byCode["WW"]);
            }

            return result;
        }

        var parts = code.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (int p = 0; p < parts.Length; p++)
        {
            string part = parts[p];
            if (p == 0 && part.Length > 3 && part.EndsWith("CMY", StringComparison.Ordinal))
                part = part[..^3];
            AddToken(part, result, unrecognized);
        }

        return result;
    }

    private static void AddToken(string token, List<StandardEmitter> result, ICollection<string>? unrecognized)
    {
        if (_byCode.TryGetValue(token, out var multi))
        {
            result.Add(multi);
            return;
        }

        foreach (char c in token)
        {
            if (char.IsWhiteSpace(c))
                continue;
            if (_byCode.TryGetValue(c.ToString(), out var single))
                result.Add(single);
            else
                unrecognized?.Add(c.ToString());
        }
    }

    private static StandardEmitter[] BuildCatalog()
    {
        var red = new Vector2(0.700f, 0.299f);
        var green = new Vector2(0.170f, 0.700f);
        var blue = new Vector2(0.150f, 0.030f);

        // R, G, B fluxes (lumens per lumen of white) such that R+G+B at full is D65.
        var r = new Emitter("Red", red, 1f);
        var g = new Emitter("Green", green, 1f);
        var b = new Emitter("Blue", blue, 1f);
        float fr = 0.26f, fg = 0.70f, fb = 0.04f;
        if (ColorMixing.TrySolveLevels(r, g, b, ColorSpaces.D65WhiteXy, out var levels))
        {
            float sum = levels.X + levels.Y + levels.Z;
            (fr, fg, fb) = (levels.X / sum, levels.Y / sum, levels.Z / sum);
        }

        var w6500 = Cct.PlanckianXy(6500f);
        return
        [
            new("R", "Red", red, fr),
            new("G", "Green", green, fg),
            new("B", "Blue", blue, fb),
            new("W", "White", w6500, 0.80f),
            new("A", "Amber", new Vector2(0.575f, 0.424f), 0.30f),
            new("L", "Lime", new Vector2(0.405f, 0.545f), 0.55f),
            new("C", "Cyan", new Vector2(0.060f, 0.520f), 0.25f),
            new("I", "Indigo", new Vector2(0.160f, 0.016f), 0.03f),
            new("V", "Violet", new Vector2(0.170f, 0.006f), 0.01f),
            new("BG", "Blue-green", new Vector2(0.075f, 0.330f), 0.15f),
            new("UV", "Ultraviolet", new Vector2(0.172f, 0.005f), 0.002f),
            new("WW", "Warm white", Cct.PlanckianXy(3000f), 0.75f),
            new("NW", "Neutral white", Cct.PlanckianXy(4000f), 0.80f),
            new("CW", "Cool white", w6500, 0.80f),
        ];
    }
}
