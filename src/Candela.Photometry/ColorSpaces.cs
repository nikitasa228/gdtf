using System.Numerics;

namespace Candela.Photometry;

/// <summary>
/// Conversions between CIE XYZ, CIE xyY, CIE 1960 UCS (u, v), CIE 1976 (u′, v′), linear Rec.709/sRGB and linear
/// Rec.2020, plus the sRGB transfer function. All RGB values are <em>linear</em> unless a method says "encoded";
/// <see cref="Vector3"/> carries (R, G, B), (X, Y, Z) or (x, y, Y).
/// </summary>
/// <remarks>
/// Matrices are derived from the primaries and the D65 white (0.3127, 0.3290) by <see cref="RgbColorSpace"/>:
/// <code>
/// XYZ → Rec.709                    XYZ → Rec.2020
///  3.2409699 -1.5373832 -0.4986108  1.7166512 -0.3556708 -0.2533663
/// -0.9692436  1.8759675  0.0415551 -0.6666844  1.6164812  0.0157685
///  0.0556301 -0.2039770  1.0569715  0.0176399 -0.0427706  0.9421031
///
/// Rec.709 → Rec.2020 (ITU-R BT.2087)  Rec.2020 → Rec.709
///  0.6274040 0.3292820 0.0433136     1.6604910 -0.5876411 -0.0728499
///  0.0690970 0.9195400 0.0113612    -0.1245505  1.1328999 -0.0083494
///  0.0163916 0.0880132 0.8955950    -0.0181508 -0.1005789  1.1187297
/// </code>
/// </remarks>
public static class ColorSpaces
{
    /// <summary>CIE 1931 chromaticity of the D65 white point as used by ITU-R BT.709, BT.2020 and sRGB.</summary>
    public static readonly Vector2 D65WhiteXy = new(0.3127f, 0.3290f);

    /// <summary>Rec.2020 luminance coefficients (0.2627, 0.6780, 0.0593): Y = 0.2627·R + 0.6780·G + 0.0593·B.</summary>
    public static Vector3 Rec2020LuminanceCoefficients => RgbColorSpace.Rec2020.LuminanceCoefficients;

    /// <summary>Rec.709/sRGB luminance coefficients (0.2126, 0.7152, 0.0722).</summary>
    public static Vector3 Rec709LuminanceCoefficients => RgbColorSpace.Rec709.LuminanceCoefficients;

    /// <summary>Linear Rec.709 → linear Rec.2020 (ITU-R BT.2087).</summary>
    public static ColorMatrix3 Rec709ToRec2020Matrix => RgbColorSpace.Rec709ToRec2020;

    /// <summary>Linear Rec.2020 → linear Rec.709.</summary>
    public static ColorMatrix3 Rec2020ToRec709Matrix => RgbColorSpace.Rec2020ToRec709;

    /// <summary>XYZ → (x, y, Y). Black (X+Y+Z ≤ 0) maps to the D65 chromaticity with Y = 0.</summary>
    public static Vector3 XyzToXyY(Vector3 xyz)
    {
        float sum = xyz.X + xyz.Y + xyz.Z;
        if (!(sum > 0f) || !float.IsFinite(sum))
            return new Vector3(D65WhiteXy, 0f);
        return new Vector3(xyz.X / sum, xyz.Y / sum, xyz.Y);
    }

    /// <summary>(x, y, Y) → XYZ. A chromaticity with y ≤ 0 (or non-finite input) gives black.</summary>
    public static Vector3 XyYToXyz(Vector3 xyY)
    {
        if (!(xyY.Y > 0f) || !float.IsFinite(xyY.X) || !float.IsFinite(xyY.Y) || !float.IsFinite(xyY.Z))
            return Vector3.Zero;
        float k = xyY.Z / xyY.Y;
        return new Vector3(xyY.X * k, xyY.Z, (1f - xyY.X - xyY.Y) * k);
    }

    /// <summary>XYZ of chromaticity <paramref name="xy"/> with luminance <paramref name="luminance"/> (default 1).</summary>
    public static Vector3 XyToXyz(Vector2 xy, float luminance = 1f) => XyYToXyz(new Vector3(xy, luminance));

    /// <summary>CIE 1931 (x, y) → CIE 1960 UCS (u, v): u = 4x/(−2x+12y+3), v = 6y/(−2x+12y+3).</summary>
    public static Vector2 XyToUv1960(Vector2 xy)
    {
        float d = -2f * xy.X + 12f * xy.Y + 3f;
        return new Vector2(4f * xy.X / d, 6f * xy.Y / d);
    }

    /// <summary>CIE 1960 UCS (u, v) → CIE 1931 (x, y): x = 3u/(2u−8v+4), y = 2v/(2u−8v+4).</summary>
    public static Vector2 Uv1960ToXy(Vector2 uv)
    {
        float d = 2f * uv.X - 8f * uv.Y + 4f;
        return new Vector2(3f * uv.X / d, 2f * uv.Y / d);
    }

    /// <summary>CIE 1931 (x, y) → CIE 1976 UCS (u′, v′) = (u, 1.5·v).</summary>
    public static Vector2 XyToUvPrime(Vector2 xy)
    {
        var uv = XyToUv1960(xy);
        return new Vector2(uv.X, 1.5f * uv.Y);
    }

    /// <summary>XYZ → linear Rec.709/sRGB.</summary>
    public static Vector3 XyzToRec709(Vector3 xyz) => RgbColorSpace.Rec709.FromXyz(xyz);

    /// <summary>Linear Rec.709/sRGB → XYZ.</summary>
    public static Vector3 Rec709ToXyz(Vector3 rgb) => RgbColorSpace.Rec709.ToXyz(rgb);

    /// <summary>XYZ → linear Rec.2020.</summary>
    public static Vector3 XyzToRec2020(Vector3 xyz) => RgbColorSpace.Rec2020.FromXyz(xyz);

    /// <summary>Linear Rec.2020 → XYZ.</summary>
    public static Vector3 Rec2020ToXyz(Vector3 rgb) => RgbColorSpace.Rec2020.ToXyz(rgb);

    /// <summary>Linear Rec.709 → linear Rec.2020 (Rec.709 lies inside Rec.2020, so non-negative input stays non-negative).</summary>
    public static Vector3 Rec709ToRec2020(Vector3 rgb) => Rec709ToRec2020Matrix.Transform(rgb);

    /// <summary>Linear Rec.2020 → linear Rec.709 (components may become negative for colours outside Rec.709).</summary>
    public static Vector3 Rec2020ToRec709(Vector3 rgb) => Rec2020ToRec709Matrix.Transform(rgb);

    /// <summary>Relative luminance of linear Rec.2020 RGB: Y = 0.2627·R + 0.6780·G + 0.0593·B.</summary>
    public static float LuminanceRec2020(Vector3 rgb) => Vector3.Dot(RgbColorSpace.Rec2020.LuminanceCoefficients, rgb);

    /// <summary>Relative luminance of linear Rec.709/sRGB: Y = 0.2126·R + 0.7152·G + 0.0722·B.</summary>
    public static float LuminanceRec709(Vector3 rgb) => Vector3.Dot(RgbColorSpace.Rec709.LuminanceCoefficients, rgb);

    /// <summary>
    /// sRGB encoding (IEC 61966-2-1): 12.92·L for L ≤ 0.0031308, otherwise 1.055·L^(1/2.4) − 0.055. Negative input is
    /// mirrored (odd extension) so out-of-gamut values stay finite; NaN gives 0.
    /// </summary>
    public static float SrgbEncode(float linear)
    {
        if (float.IsNaN(linear))
            return 0f;
        float a = MathF.Abs(linear);
        float e = a <= 0.0031308f ? 12.92f * a : 1.055f * MathF.Pow(a, 1f / 2.4f) - 0.055f;
        return linear < 0f ? -e : e;
    }

    /// <summary>
    /// sRGB decoding (IEC 61966-2-1): V/12.92 for V ≤ 0.04045, otherwise ((V + 0.055)/1.055)^2.4. Negative input is
    /// mirrored; NaN gives 0.
    /// </summary>
    public static float SrgbDecode(float encoded)
    {
        if (float.IsNaN(encoded))
            return 0f;
        float a = MathF.Abs(encoded);
        float l = a <= 0.04045f ? a / 12.92f : MathF.Pow((a + 0.055f) / 1.055f, 2.4f);
        return encoded < 0f ? -l : l;
    }

    /// <summary>Component-wise <see cref="SrgbEncode(float)"/>.</summary>
    public static Vector3 SrgbEncode(Vector3 linear) => new(SrgbEncode(linear.X), SrgbEncode(linear.Y), SrgbEncode(linear.Z));

    /// <summary>Component-wise <see cref="SrgbDecode(float)"/>.</summary>
    public static Vector3 SrgbDecode(Vector3 encoded) => new(SrgbDecode(encoded.X), SrgbDecode(encoded.Y), SrgbDecode(encoded.Z));

    /// <summary>sRGB-encoded colour (e.g. an albedo texel or a UI colour) → linear Rec.2020, the engine's working space.</summary>
    public static Vector3 SrgbToLinearRec2020(Vector3 encoded) => Rec709ToRec2020(SrgbDecode(encoded));
}
