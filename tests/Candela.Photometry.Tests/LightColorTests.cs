using System.Numerics;
using Candela.Core.SceneGraph.Components;

namespace Candela.Photometry.Tests;

public class LightColorTests
{
    private static void AssertValid(LightColor color, string context = "")
    {
        var rgb = color.Rgb;
        Assert.True(float.IsFinite(rgb.X) && float.IsFinite(rgb.Y) && float.IsFinite(rgb.Z), $"{context}: {rgb}");
        Assert.True(rgb.X >= 0f && rgb.Y >= 0f && rgb.Z >= 0f, $"{context}: {rgb}");
        Assert.Equal(1.0, ColorSpaces.LuminanceRec2020(rgb), 1e-4);
    }

    [Fact]
    public void Default_IsD65White()
    {
        Assert.Equal(LightColor.White, default);
        Assert.Equal(Vector3.One, default(LightColor).Rgb);
        var xy = LightColor.White.ToXy();
        Assert.Equal(0.3127, xy.X, 1e-4);
        Assert.Equal(0.3290, xy.Y, 1e-4);
        Assert.Equal(new Vector3(xy, 1f), LightColor.White.ToXyY());
    }

    [Theory]
    [InlineData(0.4f, 0.4f)]
    [InlineData(0.25f, 0.30f)]
    [InlineData(0.55f, 0.40f)]
    [InlineData(0.3f, 0.6f)]
    [InlineData(0.18f, 0.10f)]
    public void InGamutChromaticities_RoundTripExactly(float x, float y)
    {
        var color = LightColor.FromXy(x, y);
        AssertValid(color);
        var back = color.ToXy();
        Assert.Equal(x, back.X, 2e-5);
        Assert.Equal(y, back.Y, 2e-5);
        Assert.Equal(1.0, color.ToXyz().Y, 1e-5);
    }

    [Fact]
    public void SpectralLocus_ProducesValidColorsWithoutNaN()
    {
        for (float nm = 360f; nm <= 830f; nm += 1f)
            AssertValid(LightColor.FromWavelength(nm), $"{nm} nm");
    }

    [Theory]
    [InlineData(0.17f, 0.005f)]   // violet LED
    [InlineData(0.0082f, 0.5384f)] // 500 nm
    [InlineData(0.06f, 0.52f)]    // cyan LED
    [InlineData(0.735f, 0.265f)]  // deep red laser
    [InlineData(0.0f, 0.9f)]      // outside the spectral locus
    public void OutOfGamutColors_AreDesaturatedTowardWhiteKeepingHue(float x, float y)
    {
        var color = LightColor.FromXy(x, y);
        AssertValid(color);
        Assert.True(MathF.Min(color.Rgb.X, MathF.Min(color.Rgb.Y, color.Rgb.Z)) < 1e-4f, "should sit on the gamut boundary");

        // Hue preserved: the result lies on the segment from the white point toward the requested chromaticity.
        var w = ColorSpaces.D65WhiteXy;
        var requested = new Vector2(x, y) - w;
        var got = color.ToXy() - w;
        float cross = requested.X * got.Y - requested.Y * got.X;
        Assert.True(MathF.Abs(cross) < 2e-4f * requested.Length(), $"cross {cross}");
        Assert.True(Vector2.Dot(requested, got) > 0f);
        Assert.True(got.Length() <= requested.Length() + 1e-5f);
    }

    [Fact]
    public void InvalidInput_GivesWhite()
    {
        Assert.Equal(LightColor.White, LightColor.FromXy(float.NaN, 0.3f));
        Assert.Equal(LightColor.White, LightColor.FromXy(0.3f, float.PositiveInfinity));
        Assert.Equal(LightColor.White, LightColor.FromXyz(Vector3.Zero));
        Assert.Equal(LightColor.White, LightColor.FromXyz(new Vector3(-1f, -1f, -1f)));
        Assert.Equal(LightColor.White, LightColor.FromXyz(new Vector3(float.NaN, 1f, 1f)));
        Assert.Equal(LightColor.White, LightColor.FromRec2020(Vector3.Zero));
        Assert.Equal(LightColor.White, LightColor.FromRec709(new Vector3(float.NaN, 0f, 0f)));
        AssertValid(LightColor.FromXy(0.3f, 0f));
        AssertValid(LightColor.FromXy(-1f, 2f));
        AssertValid(LightColor.FromXyz(new Vector3(1f, 0f, 1f)));
        AssertValid(LightColor.FromRec2020(new Vector3(-0.5f, 2f, 0f)));
    }

    [Fact]
    public void FromRec709_PrimariesKeepTheirChromaticity()
    {
        var red = LightColor.FromRec709(new Vector3(1f, 0f, 0f)).ToXy();
        Assert.Equal(0.640, red.X, 1e-4);
        Assert.Equal(0.330, red.Y, 1e-4);
        var white = LightColor.FromRec709(new Vector3(3f, 3f, 3f));
        Assert.True(Vector3.Distance(Vector3.One, white.Rgb) < 1e-5f);
        var linear = LightColor.FromRec709(new Vector3(0.2f, 0.5f, 0.1f));
        var back = linear.ToRec709();
        Assert.Equal(1.0, ColorSpaces.LuminanceRec709(back), 1e-5);
        Assert.Equal(0.4, back.X / back.Y, 1e-4);
    }

    [Fact]
    public void FromCct_FollowsTheLocusAndDuv()
    {
        var c = LightColor.FromCct(3200f);
        var xy = c.ToXy();
        Assert.Equal(0.4234, xy.X, 0.001);
        Assert.Equal(0.3990, xy.Y, 0.001);
        var tinted = LightColor.FromCct(4500f, -0.008f).ToCct();
        Assert.Equal(4500.0, tinted.Kelvin, 1.0);
        Assert.Equal(-0.008, tinted.Duv, 2e-5);
        AssertValid(LightColor.FromCct(float.NaN));
    }

    [Fact]
    public void FromSpectrum_AndFromXyY()
    {
        var d65 = LightColor.FromSpectrum(Spectrum.IlluminantD65);
        Assert.True(Vector3.Distance(Vector3.One, d65.Rgb) < 2e-3f, d65.ToString());
        var a = LightColor.FromXyY(new Vector3(0.5f, 0.4f, 37f));
        Assert.Equal(LightColor.FromXy(0.5f, 0.4f), a);
        var mvr = LightColor.FromXyY(new CieColor(0.5f, 0.4f, 12f));
        Assert.Equal(a, mvr);
    }

    [Fact]
    public void ScaledAndMix()
    {
        var c = LightColor.FromXy(0.5f, 0.4f);
        Assert.Equal(250.0, ColorSpaces.LuminanceRec2020(c.Scaled(250f)), 1e-2);

        // Mixing two in-gamut colours by luminance equals mixing their XYZ.
        var a = LightColor.FromXy(0.45f, 0.40f);
        var b = LightColor.FromXy(0.25f, 0.28f);
        var mixed = LightColor.Mix(a, 300f, b, 100f);
        var xyz = a.ToXyz() * 300f + b.ToXyz() * 100f;
        var expected = new Vector2(xyz.X, xyz.Y) / (xyz.X + xyz.Y + xyz.Z);
        Assert.True(Vector2.Distance(expected, mixed.ToXy()) < 1e-5f);
        Assert.Equal(a, LightColor.Mix(a, 1f, b, float.NaN));
    }

    [Fact]
    public void EqualityAndToString()
    {
        var a = LightColor.FromXy(0.4f, 0.4f);
        var b = LightColor.FromXy(0.4f, 0.4f);
        Assert.True(a == b);
        Assert.False(a != b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.True(a.Equals((object)b));
        Assert.Contains("x=0.4000", a.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void EveryEmitterFilterAndSlotColorInTheBundledGdtfFiles_GivesAValidColor()
    {
        int count = 0;
        foreach (string element in new[] { "Emitter", "Filter", "Slot" })
        {
            foreach (var (file, raw, x, y, _) in RepoData.Colors(element))
            {
                AssertValid(LightColor.FromXy(x, y), $"{file} {element} Color=\"{raw}\"");
                count++;
            }
        }

        Assert.True(count > 5000, $"only {count} colours found");
    }
}
