using System.Globalization;
using System.Numerics;
using Candela.Core.SceneGraph.Components;

namespace Candela.Photometry;

/// <summary>
/// The colour of a light, independent of how bright it is: a chromaticity expressed as <b>linear Rec.2020 RGB
/// normalised to luminance Y = 1</b> (0.2627·R + 0.6780·G + 0.0593·B = 1). The magnitude lives elsewhere (candela,
/// lumen, lux), so the renderer's radiometric RGB of a light is simply <c>color.Rgb * candela</c> (see
/// <see cref="Scaled"/>).
/// </summary>
/// <remarks>
/// <para><b>Gamut mapping.</b> Saturated LED and laser colours can lie outside the Rec.2020 triangle and would need
/// negative components. They are brought inside by a hue-preserving desaturation toward the D65 white point:
/// <c>rgb′ = 1 + t·(rgb − 1)</c> with the largest t ∈ (0, 1] for which every component is ≥ 0. Because white
/// (1, 1, 1) and the colour both have Y = 1 this keeps Y = 1, and because it is a straight line in XYZ it keeps the
/// dominant wavelength (the result lies on the line from the white point through the original chromaticity, on the
/// gamut boundary). No input — including NaN, infinities, y ≤ 0 or black — produces NaN or negative components:
/// invalid input yields <see cref="White"/>.</para>
/// <para><b>Default value.</b> <c>default(LightColor)</c> is <see cref="White"/> (the struct stores RGB − 1), so an
/// uninitialised colour in a component array renders as neutral light rather than black or NaN.</para>
/// <para>All factory methods are allocation-free and cheap (a 3×3 matrix and a few comparisons), so DMX evaluation can
/// build colours every frame.</para>
/// </remarks>
public readonly struct LightColor : IEquatable<LightColor>
{
    /// <summary>Smallest chromaticity y accepted; smaller values are clamped (y → 0 would mean infinite X and Z).</summary>
    private const float MinChromaticityY = 1e-4f;

    private readonly Vector3 _offset;

    private LightColor(Vector3 rgb) => _offset = rgb - Vector3.One;

    /// <summary>D65 white, RGB (1, 1, 1). Equal to <c>default(LightColor)</c>.</summary>
    public static LightColor White => default;

    /// <summary>Linear Rec.2020 RGB with luminance 1; every component is ≥ 0.</summary>
    public Vector3 Rgb => _offset + Vector3.One;

    /// <summary>Colour of CIE 1931 chromaticity (<paramref name="x"/>, <paramref name="y"/>); gamut-mapped into Rec.2020.</summary>
    /// <remarks>Non-finite input gives <see cref="White"/>; x and y are clamped to [0, 1] with y ≥ 10⁻⁴ and x + y ≤ 1.</remarks>
    public static LightColor FromXy(float x, float y)
    {
        if (!float.IsFinite(x) || !float.IsFinite(y))
            return White;
        x = Math.Clamp(x, 0f, 1f);
        y = Math.Clamp(y, MinChromaticityY, 1f);
        if (x + y > 1f)
            x = 1f - y;
        var xyz = new Vector3(x / y, 1f, (1f - x - y) / y);
        return FromUnitLuminanceRgb(ColorSpaces.XyzToRec2020(xyz));
    }

    /// <summary>Colour of CIE 1931 chromaticity <paramref name="xy"/>; see <see cref="FromXy(float, float)"/>.</summary>
    public static LightColor FromXy(Vector2 xy) => FromXy(xy.X, xy.Y);

    /// <summary>
    /// Colour of a CIE xyY value (x, y, Y). Y is a magnitude, which a light colour does not carry: it is ignored here
    /// and should be used by the caller (e.g. as a transmission factor or relative output).
    /// </summary>
    public static LightColor FromXyY(Vector3 xyY) => FromXy(xyY.X, xyY.Y);

    /// <summary>Colour of an MVR/GDTF CIE colour (x, y, Y); Y is ignored, see <see cref="FromXyY(Vector3)"/>.</summary>
    public static LightColor FromXyY(CieColor color) => FromXy(color.X, color.Y);

    /// <summary>
    /// Colour of tristimulus values XYZ of any magnitude. Black, negative-luminance or non-finite input gives
    /// <see cref="White"/>.
    /// </summary>
    public static LightColor FromXyz(Vector3 xyz)
    {
        if (!float.IsFinite(xyz.X) || !float.IsFinite(xyz.Y) || !float.IsFinite(xyz.Z))
            return White;
        if (xyz.Y > 0f)
            return FromUnitLuminanceRgb(ColorSpaces.XyzToRec2020(xyz / xyz.Y));
        float sum = xyz.X + xyz.Y + xyz.Z;
        return sum > 0f ? FromXy(xyz.X / sum, xyz.Y / sum) : White;
    }

    /// <summary>
    /// Colour of correlated colour temperature <paramref name="kelvin"/> with an optional offset
    /// <paramref name="duv"/> from the Planckian locus (positive = greenish); see <see cref="Cct.XyFromCct"/>.
    /// </summary>
    public static LightColor FromCct(float kelvin, float duv = 0f) => FromXy(Cct.XyFromCct(kelvin, duv));

    /// <summary>Colour of a spectral power distribution (CIE 1931 2° observer).</summary>
    public static LightColor FromSpectrum(Spectrum spectrum) => FromXyz(Cie1931.XyzFromSpectrum(spectrum));

    /// <summary>Colour of monochromatic light, e.g. a laser line (clamped to 360–830 nm).</summary>
    public static LightColor FromWavelength(float wavelengthNm) => FromXy(Cie1931.SpectralLocus(wavelengthNm));

    /// <summary>
    /// Colour of a linear Rec.709/sRGB triple of any magnitude (decode sRGB-encoded values first with
    /// <see cref="ColorSpaces.SrgbDecode(Vector3)"/>). Black or non-finite input gives <see cref="White"/>.
    /// </summary>
    public static LightColor FromRec709(Vector3 linearRgb) => FromRec2020(ColorSpaces.Rec709ToRec2020(linearRgb));

    /// <summary>Colour of a linear Rec.2020 triple of any magnitude. Black or non-finite input gives <see cref="White"/>.</summary>
    public static LightColor FromRec2020(Vector3 linearRgb)
    {
        float luminance = ColorSpaces.LuminanceRec2020(linearRgb);
        if (!(luminance > 0f) || !float.IsFinite(luminance) || !float.IsFinite(linearRgb.X + linearRgb.Y + linearRgb.Z))
            return White;
        return new LightColor(ClipToGamut(linearRgb / luminance));
    }

    /// <summary>
    /// Additive mix of two light colours weighted by their luminous magnitudes (lm, cd or lx — any photometric quantity,
    /// the same for both). Exact for colours that were not gamut-clipped; for spectral or flux-based mixing of emitters use
    /// <see cref="ColorMixing"/>, which mixes in XYZ before any clipping.
    /// </summary>
    public static LightColor Mix(LightColor a, float magnitudeA, LightColor b, float magnitudeB)
    {
        float wa = float.IsFinite(magnitudeA) ? MathF.Max(magnitudeA, 0f) : 0f;
        float wb = float.IsFinite(magnitudeB) ? MathF.Max(magnitudeB, 0f) : 0f;
        return FromRec2020(a.Rgb * wa + b.Rgb * wb);
    }

    /// <summary>CIE XYZ of this colour (Y = 1).</summary>
    public Vector3 ToXyz() => ColorSpaces.Rec2020ToXyz(Rgb);

    /// <summary>CIE 1931 chromaticity (x, y).</summary>
    public Vector2 ToXy()
    {
        var xyz = ToXyz();
        float sum = xyz.X + xyz.Y + xyz.Z;
        return sum > 0f ? new Vector2(xyz.X / sum, xyz.Y / sum) : ColorSpaces.D65WhiteXy;
    }

    /// <summary>CIE xyY of this colour: (x, y, 1).</summary>
    public Vector3 ToXyY() => new(ToXy(), 1f);

    /// <summary>Linear Rec.709 RGB with luminance 1 (components may be negative for colours outside Rec.709).</summary>
    public Vector3 ToRec709() => ColorSpaces.Rec2020ToRec709(Rgb);

    /// <summary>Correlated colour temperature and Δuv of this colour.</summary>
    public CctResult ToCct() => Cct.FromXy(ToXy());

    /// <summary>
    /// Radiometric-style RGB of a light of this colour with photometric magnitude <paramref name="magnitude"/>
    /// (cd, lm, lx …): <c>Rgb · magnitude</c>, whose Rec.2020 luminance equals the magnitude.
    /// </summary>
    public Vector3 Scaled(float magnitude) => Rgb * magnitude;

    /// <inheritdoc/>
    public bool Equals(LightColor other) => _offset.Equals(other._offset);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is LightColor other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => _offset.GetHashCode();

    /// <summary>Exact equality of the stored RGB.</summary>
    public static bool operator ==(LightColor left, LightColor right) => left.Equals(right);

    /// <summary>Inequality of the stored RGB.</summary>
    public static bool operator !=(LightColor left, LightColor right) => !left.Equals(right);

    /// <inheritdoc/>
    public override string ToString()
    {
        var xy = ToXy();
        var rgb = Rgb;
        return string.Create(CultureInfo.InvariantCulture,
            $"LightColor(x={xy.X:F4}, y={xy.Y:F4}; Rec.2020 {rgb.X:F4}, {rgb.Y:F4}, {rgb.Z:F4})");
    }

    /// <summary>
    /// Desaturates a Y = 1 Rec.2020 triple toward white (1, 1, 1) just enough to make every component non-negative.
    /// </summary>
    internal static Vector3 ClipToGamut(Vector3 rgb)
    {
        float min = MathF.Min(rgb.X, MathF.Min(rgb.Y, rgb.Z));
        if (min >= 0f)
            return rgb;
        float t = 1f / (1f - min);
        return Vector3.Max(Vector3.One + (rgb - Vector3.One) * t, Vector3.Zero);
    }

    private static LightColor FromUnitLuminanceRgb(Vector3 rgb)
    {
        // Renormalise away float rounding of the matrix product before clipping.
        float luminance = ColorSpaces.LuminanceRec2020(rgb);
        if (!(luminance > 0f) || !float.IsFinite(luminance))
            return White;
        return new LightColor(ClipToGamut(rgb / luminance));
    }
}
