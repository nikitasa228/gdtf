using System.Globalization;
using System.Text.RegularExpressions;
using Candela.Core.SceneGraph.Components;

namespace Candela.Assets;

/// <summary>Cross-section shape of a truss or pipe.</summary>
public enum TrussShape
{
    /// <summary>A single tube (scaffold pipe, single-chord truss).</summary>
    Pipe,

    /// <summary>Two chords in a vertical plane with zig-zag bracing (F32, X30L).</summary>
    Ladder,

    /// <summary>Three chords on an equilateral triangle, two on top and one at the bottom (F33, X30D).</summary>
    Triangle,

    /// <summary>Four chords on a square (F34, F44, X30V).</summary>
    Square,
}

/// <summary>
/// Geometry of a truss cross-section in metres. <see cref="Size"/> is the outer dimension over the chords (the
/// catalogue figure: 290 mm for F34) — the width of the generated mesh across the truss.
/// </summary>
/// <param name="Name">Normalised label (e.g. "F34").</param>
/// <param name="Shape">Number and arrangement of chords.</param>
/// <param name="Size">Outer dimension in metres (pipes: the outer diameter).</param>
/// <param name="ChordDiameter">Main tube outer diameter in metres.</param>
/// <param name="BraceDiameter">Diagonal tube outer diameter in metres.</param>
/// <param name="IsRecognised">False when the label was not understood and a default profile was substituted.</param>
public sealed record TrussProfile(string Name, TrussShape Shape, float Size, float ChordDiameter, float BraceDiameter, bool IsRecognised = true)
{
    /// <summary>Standard scaffold tube (EN 39): Ø48.3 mm.</summary>
    public const float ScaffoldTubeDiameter = 0.0483f;

    // Outer dimensions and tube sizes of common aluminium truss series (manufacturer catalogues: Global Truss F2x/F3x/
    // F4x/F5x, Prolyte X30/H30/H40). Chords 35/50 mm, diagonals 16–30 mm.
    private static readonly Dictionary<string, (TrussShape Shape, float Size, float Chord, float Brace)> s_known =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["F31"] = (TrussShape.Pipe, 0.050f, 0.050f, 0.020f),
            ["F32"] = (TrussShape.Ladder, 0.290f, 0.050f, 0.020f),
            ["F33"] = (TrussShape.Triangle, 0.290f, 0.050f, 0.020f),
            ["F34"] = (TrussShape.Square, 0.290f, 0.050f, 0.020f),
            ["F22"] = (TrussShape.Ladder, 0.220f, 0.035f, 0.016f),
            ["F23"] = (TrussShape.Triangle, 0.220f, 0.035f, 0.016f),
            ["F24"] = (TrussShape.Square, 0.220f, 0.035f, 0.016f),
            ["F42"] = (TrussShape.Ladder, 0.400f, 0.050f, 0.025f),
            ["F43"] = (TrussShape.Triangle, 0.400f, 0.050f, 0.025f),
            ["F44"] = (TrussShape.Square, 0.400f, 0.050f, 0.025f),
            ["F52"] = (TrussShape.Ladder, 0.520f, 0.050f, 0.030f),
            ["F53"] = (TrussShape.Triangle, 0.520f, 0.050f, 0.030f),
            ["F54"] = (TrussShape.Square, 0.520f, 0.050f, 0.030f),
            ["X30L"] = (TrussShape.Ladder, 0.290f, 0.048f, 0.016f),
            ["X30D"] = (TrussShape.Triangle, 0.290f, 0.048f, 0.016f),
            ["X30V"] = (TrussShape.Square, 0.290f, 0.048f, 0.016f),
            ["H30L"] = (TrussShape.Ladder, 0.290f, 0.051f, 0.016f),
            ["H30D"] = (TrussShape.Triangle, 0.290f, 0.051f, 0.016f),
            ["H30V"] = (TrussShape.Square, 0.290f, 0.051f, 0.016f),
            ["H40L"] = (TrussShape.Ladder, 0.390f, 0.051f, 0.016f),
            ["H40D"] = (TrussShape.Triangle, 0.390f, 0.051f, 0.016f),
            ["H40V"] = (TrussShape.Square, 0.390f, 0.051f, 0.016f),
        };

    private static readonly Regex s_number = new(@"(\d+(?:[.,]\d+)?)\s*(mm|cm|m)?", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>Profile labels with catalogue dimensions (for pickers in the UI).</summary>
    public static IReadOnlyList<string> KnownProfiles { get; } = s_known.Keys.Order(StringComparer.Ordinal).ToArray();

    /// <summary>
    /// Interprets a profile label for a truss of the given kind. Accepts catalogue names ("F34", "H30V"), pipe labels
    /// ("Ø48 pipe", "pipe 50 mm") and free sizes ("square 300 mm", "triangle 0.4 m"; bare numbers above 5 are
    /// millimetres). <see cref="TrussKind.Pipe"/> always yields a pipe, <see cref="TrussKind.Ladder"/> a ladder, and
    /// <see cref="TrussKind.Tower"/> a square or triangular section. Unknown labels fall back to F34 (pipes: Ø48.3 mm)
    /// with <see cref="IsRecognised"/> = false.
    /// </summary>
    public static TrussProfile Parse(string? label, TrussKind kind = TrussKind.Truss)
    {
        var text = (label ?? "").Trim();
        TrussProfile? parsed = null;
        var code = text.Replace(" ", "", StringComparison.Ordinal);
        if (s_known.TryGetValue(code, out var k)) parsed = new TrussProfile(code.ToUpperInvariant(), k.Shape, k.Size, k.Chord, k.Brace);
        else if (text.Length > 0) parsed = ParseFree(text);

        // An empty label simply means "the default for this kind"; anything else that does not fit is reported.
        bool emptyLabel = text.Length == 0;
        return kind switch
        {
            TrussKind.Pipe => parsed is { Shape: TrussShape.Pipe }
                ? parsed
                : new TrussProfile("Ø48.3 pipe", TrussShape.Pipe, ScaffoldTubeDiameter, ScaffoldTubeDiameter, 0.02f, emptyLabel),
            TrussKind.Ladder => parsed is { Shape: not TrussShape.Pipe }
                ? parsed with { Shape = TrussShape.Ladder }
                : Default("F32", emptyLabel),
            TrussKind.Tower => parsed is { Shape: TrussShape.Square or TrussShape.Triangle }
                ? parsed
                : Default("F34", emptyLabel),
            _ => parsed ?? Default("F34", emptyLabel),
        };
    }

    private static TrussProfile Default(string code, bool recognised)
    {
        var k = s_known[code];
        return new TrussProfile(code, k.Shape, k.Size, k.Chord, k.Brace, recognised);
    }

    private static TrussProfile? ParseFree(string text)
    {
        var lower = text.ToLowerInvariant();
        TrussShape? shape = null;
        if (lower.Contains('ø') || lower.Contains("pipe") || lower.Contains("tube") || lower.Contains("труба")) shape = TrussShape.Pipe;
        else if (lower.Contains("ladder") || lower.Contains("лестн")) shape = TrussShape.Ladder;
        else if (lower.Contains("tri") || lower.Contains("треуг")) shape = TrussShape.Triangle;
        else if (lower.Contains("square") || lower.Contains("box") || lower.Contains("квадр")) shape = TrussShape.Square;

        var m = s_number.Match(lower);
        if (!m.Success) return null;
        if (!float.TryParse(m.Groups[1].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out float value) || !(value > 0))
            return null;
        float metres = m.Groups[2].Value switch
        {
            "mm" => value / 1000f,
            "cm" => value / 100f,
            "m" => value,
            _ => value > 5f ? value / 1000f : value,
        };
        if (!(metres >= 0.005f && metres <= 5f)) return null;
        var s = shape ?? TrussShape.Square;
        if (s == TrussShape.Pipe) return new TrussProfile($"Ø{metres * 1000:0.#} pipe", s, metres, metres, 0.02f);
        // Chords scale with the section: 35 mm below 250 mm, otherwise 50 mm (never more than a quarter of the size).
        float chord = System.Math.Min(metres < 0.25f ? 0.035f : 0.050f, metres / 4f);
        float brace = chord * 0.45f;
        return new TrussProfile($"{s} {metres * 1000:0} mm", s, metres, chord, brace);
    }
}
