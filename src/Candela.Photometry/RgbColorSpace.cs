using System.Numerics;

namespace Candela.Photometry;

/// <summary>
/// A linear RGB colour space defined by the CIE 1931 chromaticities of its primaries and white point. The RGB→XYZ
/// matrix is derived exactly (in double precision) from those four chromaticities, normalised so that RGB (1, 1, 1)
/// is the white point with luminance Y = 1 (SMPTE RP 177-1993 method).
/// </summary>
public sealed class RgbColorSpace
{
    // Kept local (not ColorSpaces.D65WhiteXy) so the static initialisers of the two classes never depend on each other.
    private static readonly Vector2 _d65White = new(0.3127f, 0.3290f);

    private readonly double[] _toXyz;
    private readonly double[] _fromXyz;

    /// <summary>Creates a colour space from primary and white-point chromaticities (CIE 1931 x, y).</summary>
    /// <exception cref="ArgumentException">The primaries are degenerate (collinear) or a y coordinate is not positive.</exception>
    public RgbColorSpace(string name, Vector2 red, Vector2 green, Vector2 blue, Vector2 white)
    {
        ArgumentNullException.ThrowIfNull(name);
        Name = name;
        Red = red;
        Green = green;
        Blue = blue;
        White = white;
        if (!(red.Y > 0 && green.Y > 0 && blue.Y > 0 && white.Y > 0))
            throw new ArgumentException($"Colour space '{name}': chromaticity y must be positive.");

        // Columns: XYZ of each primary with Y = 1.
        double[] p =
        {
            red.X / (double)red.Y, green.X / (double)green.Y, blue.X / (double)blue.Y,
            1, 1, 1,
            (1.0 - red.X - red.Y) / red.Y, (1.0 - green.X - green.Y) / green.Y, (1.0 - blue.X - blue.Y) / blue.Y,
        };
        double[] inverse;
        try
        {
            inverse = ColorMatrix3.Invert(p);
        }
        catch (InvalidOperationException e)
        {
            throw new ArgumentException($"Colour space '{name}': primaries are collinear.", e);
        }

        double wx = white.X / (double)white.Y, wz = (1.0 - white.X - white.Y) / white.Y;
        double sr = inverse[0] * wx + inverse[1] + inverse[2] * wz;
        double sg = inverse[3] * wx + inverse[4] + inverse[5] * wz;
        double sb = inverse[6] * wx + inverse[7] + inverse[8] * wz;
        _toXyz = new[]
        {
            p[0] * sr, p[1] * sg, p[2] * sb,
            p[3] * sr, p[4] * sg, p[5] * sb,
            p[6] * sr, p[7] * sg, p[8] * sb,
        };
        _fromXyz = ColorMatrix3.Invert(_toXyz);
        ToXyzMatrix = ColorMatrix3.FromDouble(_toXyz);
        FromXyzMatrix = ColorMatrix3.FromDouble(_fromXyz);
        LuminanceCoefficients = ToXyzMatrix.Row2;
    }

    /// <summary>
    /// ITU-R BT.709 / IEC 61966-2-1 sRGB primaries — R (0.640, 0.330), G (0.300, 0.600), B (0.150, 0.060) — with the
    /// D65 white (0.3127, 0.3290). RGB→XYZ:
    /// <code>
    /// 0.4123908 0.3575843 0.1804808
    /// 0.2126390 0.7151687 0.0721923
    /// 0.0193308 0.1191948 0.9505322
    /// </code>
    /// </summary>
    public static RgbColorSpace Rec709 { get; } = new("Rec.709",
        new Vector2(0.640f, 0.330f), new Vector2(0.300f, 0.600f), new Vector2(0.150f, 0.060f), _d65White);

    /// <summary>
    /// ITU-R BT.2020 primaries — R (0.708, 0.292), G (0.170, 0.797), B (0.131, 0.046) — with the D65 white
    /// (0.3127, 0.3290); the engine's working space. RGB→XYZ:
    /// <code>
    /// 0.6369580 0.1446169 0.1688810
    /// 0.2627002 0.6779981 0.0593017
    /// 0.0000000 0.0280727 1.0609851
    /// </code>
    /// </summary>
    public static RgbColorSpace Rec2020 { get; } = new("Rec.2020",
        new Vector2(0.708f, 0.292f), new Vector2(0.170f, 0.797f), new Vector2(0.131f, 0.046f), _d65White);

    /// <summary>Linear Rec.709 → linear Rec.2020 (declared after both spaces so it initialises after them).</summary>
    internal static ColorMatrix3 Rec709ToRec2020 { get; } = Rec709.ConversionTo(Rec2020);

    /// <summary>Linear Rec.2020 → linear Rec.709.</summary>
    internal static ColorMatrix3 Rec2020ToRec709 { get; } = Rec2020.ConversionTo(Rec709);

    /// <summary>Display name.</summary>
    public string Name { get; }

    /// <summary>Chromaticity (x, y) of the red primary.</summary>
    public Vector2 Red { get; }

    /// <summary>Chromaticity (x, y) of the green primary.</summary>
    public Vector2 Green { get; }

    /// <summary>Chromaticity (x, y) of the blue primary.</summary>
    public Vector2 Blue { get; }

    /// <summary>Chromaticity (x, y) of the white point (RGB = 1, 1, 1).</summary>
    public Vector2 White { get; }

    /// <summary>Linear RGB → CIE XYZ.</summary>
    public ColorMatrix3 ToXyzMatrix { get; }

    /// <summary>CIE XYZ → linear RGB.</summary>
    public ColorMatrix3 FromXyzMatrix { get; }

    /// <summary>Luminance (Y) weights of R, G and B — the second row of <see cref="ToXyzMatrix"/>; they sum to 1.</summary>
    public Vector3 LuminanceCoefficients { get; }

    /// <summary>Linear RGB → XYZ.</summary>
    public Vector3 ToXyz(Vector3 rgb) => ToXyzMatrix.Transform(rgb);

    /// <summary>XYZ → linear RGB (components may be negative for colours outside the gamut).</summary>
    public Vector3 FromXyz(Vector3 xyz) => FromXyzMatrix.Transform(xyz);

    /// <summary>Relative luminance Y of a linear RGB triple.</summary>
    public float Luminance(Vector3 rgb) => Vector3.Dot(LuminanceCoefficients, rgb);

    /// <summary>Matrix converting linear RGB of this space into linear RGB of <paramref name="target"/> (composed in double precision).</summary>
    public ColorMatrix3 ConversionTo(RgbColorSpace target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return ColorMatrix3.FromDouble(ColorMatrix3.Multiply(target._fromXyz, _toXyz));
    }

    /// <inheritdoc/>
    public override string ToString() => Name;
}
