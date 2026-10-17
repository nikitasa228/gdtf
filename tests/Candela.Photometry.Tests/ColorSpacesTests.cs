using System.Numerics;

namespace Candela.Photometry.Tests;

public class ColorSpacesTests
{
    private static void AssertMatrix(float[] expected, ColorMatrix3 actual, float tolerance)
    {
        float[] values = [actual.M11, actual.M12, actual.M13, actual.M21, actual.M22, actual.M23, actual.M31, actual.M32, actual.M33];
        for (int i = 0; i < 9; i++)
            Assert.InRange(values[i], expected[i] - tolerance, expected[i] + tolerance);
    }

    [Fact]
    public void Rec709Matrices_MatchIec61966()
    {
        AssertMatrix([0.4123908f, 0.3575843f, 0.1804808f, 0.2126390f, 0.7151687f, 0.0721923f, 0.0193308f, 0.1191948f, 0.9505322f],
            RgbColorSpace.Rec709.ToXyzMatrix, 2e-6f);
        AssertMatrix([3.2409699f, -1.5373832f, -0.4986108f, -0.9692436f, 1.8759675f, 0.0415551f, 0.0556301f, -0.2039770f, 1.0569715f],
            RgbColorSpace.Rec709.FromXyzMatrix, 5e-6f);
    }

    [Fact]
    public void Rec2020Matrices_MatchBt2020()
    {
        AssertMatrix([0.6369580f, 0.1446169f, 0.1688810f, 0.2627002f, 0.6779981f, 0.0593017f, 0f, 0.0280727f, 1.0609851f],
            RgbColorSpace.Rec2020.ToXyzMatrix, 2e-6f);
        AssertMatrix([1.7166512f, -0.3556708f, -0.2533663f, -0.6666844f, 1.6164812f, 0.0157685f, 0.0176399f, -0.0427706f, 0.9421031f],
            RgbColorSpace.Rec2020.FromXyzMatrix, 5e-6f);
    }

    [Fact]
    public void Rec709ToRec2020_MatchesBt2087()
    {
        AssertMatrix([0.6274f, 0.3293f, 0.0433f, 0.0691f, 0.9195f, 0.0114f, 0.0164f, 0.0880f, 0.8956f], ColorSpaces.Rec709ToRec2020Matrix, 1e-4f);
        var roundTrip = ColorSpaces.Rec2020ToRec709Matrix * ColorSpaces.Rec709ToRec2020Matrix;
        AssertMatrix([1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f], roundTrip, 1e-6f);
    }

    [Fact]
    public void LuminanceCoefficients_AreThePublishedOnes()
    {
        var c = ColorSpaces.Rec2020LuminanceCoefficients;
        Assert.Equal(0.2627f, c.X, 1e-4);
        Assert.Equal(0.6780f, c.Y, 1e-4);
        Assert.Equal(0.0593f, c.Z, 1e-4);
        Assert.Equal(1f, c.X + c.Y + c.Z, 1e-6);
        Assert.Equal(0.2627f * 0.2f + 0.6780f * 0.5f + 0.0593f * 0.9f, ColorSpaces.LuminanceRec2020(new Vector3(0.2f, 0.5f, 0.9f)), 1e-4);
        var c709 = ColorSpaces.Rec709LuminanceCoefficients;
        Assert.Equal(0.2126f, c709.X, 1e-4);
        Assert.Equal(0.7152f, c709.Y, 1e-4);
        Assert.Equal(0.0722f, c709.Z, 1e-4);
    }

    [Theory]
    [InlineData(1f, 0f, 0f, 0.708f, 0.292f)]
    [InlineData(0f, 1f, 0f, 0.170f, 0.797f)]
    [InlineData(0f, 0f, 1f, 0.131f, 0.046f)]
    [InlineData(1f, 1f, 1f, 0.3127f, 0.3290f)]
    public void Rec2020PrimariesAndWhite_RoundTripThroughXy(float r, float g, float b, float x, float y)
    {
        var xyY = ColorSpaces.XyzToXyY(ColorSpaces.Rec2020ToXyz(new Vector3(r, g, b)));
        Assert.Equal(x, xyY.X, 1e-5);
        Assert.Equal(y, xyY.Y, 1e-5);

        // xy → RGB (via LightColor, luminance 1) → xy.
        var color = LightColor.FromXy(x, y);
        var back = color.ToXy();
        Assert.Equal(x, back.X, 1e-4);
        Assert.Equal(y, back.Y, 1e-4);
        var expected = new Vector3(r, g, b) / ColorSpaces.LuminanceRec2020(new Vector3(r, g, b));
        Assert.True(Vector3.Distance(expected, color.Rgb) < 1e-3f * expected.Length(), $"{color.Rgb} vs {expected}");
    }

    [Fact]
    public void XyzRgbConversions_AreInverse()
    {
        var xyz = new Vector3(0.4f, 0.35f, 0.2f);
        Assert.True(Vector3.Distance(xyz, ColorSpaces.Rec2020ToXyz(ColorSpaces.XyzToRec2020(xyz))) < 1e-6f);
        Assert.True(Vector3.Distance(xyz, ColorSpaces.Rec709ToXyz(ColorSpaces.XyzToRec709(xyz))) < 1e-6f);
        var rgb = new Vector3(0.3f, 0.6f, 0.1f);
        Assert.True(Vector3.Distance(rgb, ColorSpaces.Rec2020ToRec709(ColorSpaces.Rec709ToRec2020(rgb))) < 1e-6f);
        Assert.Equal(ColorSpaces.LuminanceRec709(rgb), ColorSpaces.LuminanceRec2020(ColorSpaces.Rec709ToRec2020(rgb)), 1e-5);
    }

    [Fact]
    public void XyY_ConversionsHandleBlackAndInvalidInput()
    {
        var xyY = ColorSpaces.XyzToXyY(new Vector3(0.9505f, 1f, 1.089f));
        Assert.Equal(0.3127f, xyY.X, 1e-3);
        Assert.Equal(1f, xyY.Z);
        Assert.Equal(new Vector3(ColorSpaces.D65WhiteXy, 0f), ColorSpaces.XyzToXyY(Vector3.Zero));
        Assert.Equal(Vector3.Zero, ColorSpaces.XyYToXyz(new Vector3(0.3f, 0f, 1f)));
        Assert.Equal(Vector3.Zero, ColorSpaces.XyYToXyz(new Vector3(float.NaN, 0.3f, 1f)));
        var xyz = ColorSpaces.XyToXyz(new Vector2(0.3127f, 0.3290f), 2f);
        Assert.Equal(2f, xyz.Y);
        Assert.Equal(0.3127f / 0.3290f * 2f, xyz.X, 1e-5);
    }

    [Fact]
    public void Ucs_ConversionsMatchPublishedD65AndInvert()
    {
        var uv = ColorSpaces.XyToUv1960(ColorSpaces.D65WhiteXy);
        Assert.Equal(0.19783, uv.X, 1e-4);
        Assert.Equal(0.31221, uv.Y, 1e-4);
        var xy = ColorSpaces.Uv1960ToXy(uv);
        Assert.Equal(0.3127f, xy.X, 1e-5);
        Assert.Equal(0.3290f, xy.Y, 1e-5);
        var uvPrime = ColorSpaces.XyToUvPrime(ColorSpaces.D65WhiteXy);
        Assert.Equal(0.46832, uvPrime.Y, 1e-4);
    }

    [Fact]
    public void SrgbTransfer_EncodeAndDecodeAreInverse()
    {
        for (int i = 0; i <= 1000; i++)
        {
            float v = i / 1000f;
            Assert.Equal(v, ColorSpaces.SrgbDecode(ColorSpaces.SrgbEncode(v)), 1e-5);
            Assert.Equal(v, ColorSpaces.SrgbEncode(ColorSpaces.SrgbDecode(v)), 1e-5);
        }
    }

    [Fact]
    public void SrgbTransfer_MatchesReferencePointsAndIsSafe()
    {
        Assert.Equal(0.4613561, ColorSpaces.SrgbEncode(0.18f), 1e-6); // 18 % grey
        Assert.Equal(0.21404f, ColorSpaces.SrgbDecode(0.5f), 1e-4);
        Assert.Equal(12.92f * 0.002f, ColorSpaces.SrgbEncode(0.002f), 1e-6);
        Assert.Equal(1f, ColorSpaces.SrgbEncode(1f), 1e-6);
        Assert.Equal(-ColorSpaces.SrgbEncode(0.25f), ColorSpaces.SrgbEncode(-0.25f));
        Assert.Equal(0f, ColorSpaces.SrgbEncode(float.NaN));
        Assert.Equal(0f, ColorSpaces.SrgbDecode(float.NaN));

        // Continuity at the thresholds of the piecewise definition.
        Assert.Equal(ColorSpaces.SrgbEncode(0.0031308f), ColorSpaces.SrgbEncode(0.0031309f), 1e-4);
        Assert.Equal(ColorSpaces.SrgbDecode(0.04045f), ColorSpaces.SrgbDecode(0.04046f), 1e-4);

        var white = ColorSpaces.SrgbToLinearRec2020(Vector3.One);
        Assert.True(Vector3.Distance(Vector3.One, white) < 1e-5f);
    }

    [Fact]
    public void ColorMatrix3_InverseToMatrix4x4AndEquality()
    {
        var m = RgbColorSpace.Rec2020.ToXyzMatrix;
        var identity = m * m.Inverse();
        AssertMatrix([1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f], identity, 1e-6f);
        var v = new Vector3(0.2f, 0.4f, 0.6f);
        Assert.True(Vector3.Distance(m.Transform(v), Vector3.Transform(v, m.ToMatrix4x4())) < 1e-6f);
        Assert.Equal(m, RgbColorSpace.Rec2020.ToXyzMatrix);
        Assert.True(m != ColorMatrix3.Identity);
        Assert.Throws<InvalidOperationException>(() => new ColorMatrix3(1, 2, 3, 2, 4, 6, 0, 0, 1).Inverse());
    }

    [Fact]
    public void RgbColorSpace_RejectsDegeneratePrimaries()
    {
        Assert.Throws<ArgumentException>(() => new RgbColorSpace("bad", new Vector2(0.1f, 0.1f), new Vector2(0.2f, 0.2f),
            new Vector2(0.3f, 0.3f), ColorSpaces.D65WhiteXy));
        Assert.Throws<ArgumentException>(() => new RgbColorSpace("bad", new Vector2(0.6f, 0f), new Vector2(0.3f, 0.6f),
            new Vector2(0.15f, 0.06f), ColorSpaces.D65WhiteXy));
    }
}
