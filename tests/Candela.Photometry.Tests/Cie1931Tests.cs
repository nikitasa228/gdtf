using System.Numerics;

namespace Candela.Photometry.Tests;

public class Cie1931Tests
{
    private static void AssertXy(Vector2 expected, Vector2 actual, float tolerance)
    {
        Assert.InRange(actual.X, expected.X - tolerance, expected.X + tolerance);
        Assert.InRange(actual.Y, expected.Y - tolerance, expected.Y + tolerance);
    }

    [Fact]
    public void Table_HasOfficialGridAndKeyValues()
    {
        Assert.Equal(95, Cie1931.XBar.Length);
        Assert.Equal(95, Cie1931.YBar.Length);
        Assert.Equal(95, Cie1931.ZBar.Length);
        Assert.Equal(830f, Cie1931.EndNm);

        // CIE 015:2004 Table T.4 entries.
        Assert.Equal(1.0f, Cie1931.YBar[(555 - 360) / 5]);
        Assert.Equal(1.0622f, Cie1931.XBar[(600 - 360) / 5]); // x̄ peaks at 600 nm
        Assert.Equal(0.0001299f, Cie1931.XBar[0]);
        Assert.Equal(0.2257499f, Cie1931.XBar[(535 - 360) / 5]);
        Assert.Equal(0.9148501f, Cie1931.YBar[(535 - 360) / 5]);
        Assert.Equal(0.000001251141f, Cie1931.XBar[94]);
    }

    [Fact]
    public void Table_IntegralsMatchEqualEnergyWhiteAndPhotopicArea()
    {
        double sx = 0, sy = 0, sz = 0;
        for (int i = 0; i < Cie1931.Count; i++)
        {
            sx += Cie1931.XBar[i];
            sy += Cie1931.YBar[i];
            sz += Cie1931.ZBar[i];
        }

        // ∫V(λ)dλ = 106.857 nm; the three functions have equal areas (illuminant E is at 1/3, 1/3).
        Assert.InRange(sy * 5, 106.80, 106.92);
        Assert.InRange(sx / sy, 0.999, 1.001);
        Assert.InRange(sz / sy, 0.999, 1.001);
        AssertXy(new Vector2(1f / 3f, 1f / 3f), Cie1931.ChromaticityFromSpectrum(Spectrum.Constant(1f)), 0.0005f);
    }

    [Fact]
    public void D65_ChromaticityMatchesPublishedValue()
    {
        var xy = Cie1931.ChromaticityFromSpectrum(Spectrum.IlluminantD65);
        AssertXy(new Vector2(0.3127f, 0.3290f), xy, 0.0005f);

        // CIE 015:2004 Table T.3: 0.31272, 0.32903.
        AssertXy(new Vector2(0.31272f, 0.32903f), xy, 0.00005f);
    }

    [Fact]
    public void IlluminantA_ChromaticityMatchesPublishedValue()
    {
        var a = Spectrum.IlluminantA;
        Assert.Equal(100f, a.Sample(560f), 1e-3);
        var xy = Cie1931.ChromaticityFromSpectrum(a);
        AssertXy(new Vector2(0.4476f, 0.4074f), xy, 0.0005f);
        AssertXy(new Vector2(0.44757f, 0.40745f), xy, 0.00005f);
    }

    [Theory]
    [InlineData(5003f, 0.34567f, 0.35850f)] // D50
    [InlineData(5503f, 0.33242f, 0.34743f)] // D55
    [InlineData(6504f, 0.31271f, 0.32902f)] // D65
    [InlineData(7504f, 0.29902f, 0.31485f)] // D75
    public void DaylightSeries_MatchesNamedIlluminants(float kelvin, float x, float y)
    {
        var spectrum = Spectrum.Daylight(kelvin);
        Assert.Equal(100f, spectrum.Sample(560f), 1e-3);
        AssertXy(new Vector2(x, y), Cie1931.ChromaticityFromSpectrum(spectrum), 0.0001f);
    }

    [Fact]
    public void DaylightAt6504K_ReproducesTabulatedD65()
    {
        var computed = Spectrum.Daylight(6504f);
        var table = Spectrum.IlluminantD65;
        for (int i = 0; i < table.Count; i++)
        {
            float w = table.WavelengthAt(i);
            if (w < 360f)
                continue;
            Assert.InRange(computed.Sample(w), table[i] * 0.99f - 0.05f, table[i] * 1.01f + 0.05f);
        }
    }

    [Fact]
    public void Daylight_ClampsOutOfRangeTemperatures()
    {
        var low = Cie1931.ChromaticityFromSpectrum(Spectrum.Daylight(1000f));
        var at4000 = Cie1931.ChromaticityFromSpectrum(Spectrum.Daylight(4000f));
        Assert.Equal(at4000, low);
        var nan = Cie1931.ChromaticityFromSpectrum(Spectrum.Daylight(float.NaN));
        AssertXy(new Vector2(0.3127f, 0.3290f), nan, 0.0002f);
    }

    [Theory]
    [InlineData(520f, 0.07430f, 0.83380f)]
    [InlineData(580f, 0.51249f, 0.48670f)]
    [InlineData(700f, 0.73469f, 0.26531f)]
    public void SpectralLocus_MatchesPublishedCoordinates(float nm, float x, float y) =>
        AssertXy(new Vector2(x, y), Cie1931.SpectralLocus(nm), 0.0002f);

    [Fact]
    public void ColorMatchingFunctions_InterpolateAndVanishOutsideRange()
    {
        var at555 = Cie1931.ColorMatchingFunctions(555f);
        Assert.Equal(1f, at555.Y);
        var between = Cie1931.ColorMatchingFunctions(557.5f);
        float expected = 0.5f * (Cie1931.YBar[39] + Cie1931.YBar[40]);
        Assert.Equal(expected, between.Y, 1e-5);
        Assert.Equal(Vector3.Zero, Cie1931.ColorMatchingFunctions(300f));
        Assert.Equal(Vector3.Zero, Cie1931.ColorMatchingFunctions(900f));
        Assert.Equal(Vector3.Zero, Cie1931.ColorMatchingFunctions(float.NaN));
    }

    [Fact]
    public void PhotopicLuminousFlux_OneWattAt555nmIs683Lumens()
    {
        // 1 W concentrated in the 555 nm sample of a 5 nm grid: 0.2 W/nm × 5 nm.
        var values = new float[95];
        values[39] = 0.2f;
        var spectrum = new Spectrum(360f, 5f, values);
        Assert.Equal(683.002, Cie1931.PhotopicLuminousFlux(spectrum), 1e-3);
        Assert.Equal(683.002, Cie1931.LuminousEfficacyOfRadiation(spectrum), 1e-3);
    }

    [Fact]
    public void XyzFromSpectrum_FineGridIntegratesOnSpectrumGrid()
    {
        // A narrow line (FWHM 4 nm) needs the 1 nm grid: point-sampling it at 5 nm would miss most of it.
        var line = Spectrum.Gaussian(552f, 4f, 360f, 0.5f, 941);
        var xyz = Cie1931.XyzFromSpectrum(line);
        double area = line.Integrate();
        var cmf = Cie1931.ColorMatchingFunctions(552f);
        Assert.InRange(xyz.Y / area, cmf.Y * 0.995, cmf.Y * 1.005);
        var xy = Cie1931.ChromaticityFromSpectrum(line);
        AssertXy(Cie1931.SpectralLocus(552f), xy, 0.002f);
    }

    [Fact]
    public void ChromaticityFromSpectrum_OfInvisibleSpectrumIsWhite()
    {
        var infrared = new Spectrum(900f, 5f, [1f, 2f, 3f]);
        Assert.Equal(ColorSpaces.D65WhiteXy, Cie1931.ChromaticityFromSpectrum(infrared));
        Assert.Equal(0.0, Cie1931.LuminousEfficacyOfRadiation(Spectrum.Constant(0f)));
    }
}
