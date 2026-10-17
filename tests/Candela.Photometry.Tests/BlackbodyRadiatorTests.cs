namespace Candela.Photometry.Tests;

public class BlackbodyRadiatorTests
{
    [Fact]
    public void PlancksLaw_IntegratesToStefanBoltzmann()
    {
        // ∫ M_λ dλ over 50 nm … 1 mm by the trapezoid rule on a logarithmic grid.
        const double t = 3000;
        double sum = 0, previousW = 50, previousM = BlackbodyRadiator.SpectralExitancePerNm(50, t);
        for (double w = 50 * 1.001; w <= 1e6; w *= 1.001)
        {
            double m = BlackbodyRadiator.SpectralExitancePerNm(w, t);
            sum += 0.5 * (m + previousM) * (w - previousW);
            previousW = w;
            previousM = m;
        }

        Assert.Equal(BlackbodyRadiator.RadiantExitance(t), sum, sum * 1e-4);
        Assert.Equal(5.670374419e-8 * Math.Pow(t, 4), BlackbodyRadiator.RadiantExitance(t), 1e-3);
    }

    [Theory]
    [InlineData(4000.0, 54.0)] // "ideal black-body radiator" efficacies, e.g. Wikipedia "Luminous efficacy"
    [InlineData(7000.0, 95.0)]
    public void LuminousEfficacy_MatchesPublishedValues(double kelvin, double lumensPerWatt) =>
        Assert.Equal(lumensPerWatt, BlackbodyRadiator.LuminousEfficacy(kelvin), 1.5);

    [Fact]
    public void LuminousEfficacy_PeaksNear6600K()
    {
        double best = 0, bestT = 0;
        for (double t = 3000; t <= 12000; t += 100)
        {
            double k = BlackbodyRadiator.LuminousEfficacy(t);
            if (k > best)
            {
                best = k;
                bestT = t;
            }
        }

        Assert.InRange(bestT, 6200, 7000);
        Assert.InRange(best, 94, 97);
        Assert.InRange(BlackbodyRadiator.LuminousEfficacy(1500), 0.0, 1.0); // a 1500 K flame is mostly infrared
    }

    [Fact]
    public void LuminanceAtPlatinumFreezingPoint_IsTheOldCandela()
    {
        // 1948–1979 candela: 1/600 000 m² of a black body at the freezing point of platinum (2041.4 K, ITS-90) → 600 000 cd/m².
        Assert.Equal(600_000, BlackbodyRadiator.Luminance(2041.4), 600_000 * 0.02);
    }

    [Fact]
    public void WienPeak()
    {
        Assert.Equal(502.0, BlackbodyRadiator.PeakWavelengthNm(5772), 0.5);
        Assert.Equal(966.0, BlackbodyRadiator.PeakWavelengthNm(3000), 0.5);
    }

    [Fact]
    public void FireColor_IsDefinedBelowTheCctTable()
    {
        foreach (float t in new[] { 300f, 800f, 1000f, 1200f, 1500f, 2000f })
        {
            var xy = BlackbodyRadiator.Chromaticity(t);
            Assert.True(xy.X > 0.5f && xy.X < 0.74f, $"{t} K: {xy}");
            var color = BlackbodyRadiator.Color(t);
            Assert.True(color.Rgb.X > color.Rgb.Y && color.Rgb.X > color.Rgb.Z, $"{t} K: {color}");
        }

        Assert.True(BlackbodyRadiator.Chromaticity(800f).X > BlackbodyRadiator.Chromaticity(1500f).X);
        Assert.Equal(BlackbodyRadiator.Chromaticity(6500f), BlackbodyRadiator.Chromaticity(float.NaN));
        Assert.Equal(BlackbodyRadiator.Chromaticity(300f), BlackbodyRadiator.Chromaticity(10f));
    }

    [Fact]
    public void SpectrumChromaticity_MatchesDirectComputation()
    {
        var xy = Cie1931.ChromaticityFromSpectrum(Spectrum.Blackbody(1800f));
        var direct = BlackbodyRadiator.Chromaticity(1800f);
        Assert.Equal(direct.X, xy.X, 1e-5);
        Assert.Equal(direct.Y, xy.Y, 1e-5);
        Assert.Equal(0.0, BlackbodyRadiator.SpectralRadiance(-1e-9, 3000));
        Assert.Equal(0.0, BlackbodyRadiator.SpectralRadiance(1e-9, 300)); // e^(c₂/λT) overflows → 0, not NaN
    }
}
