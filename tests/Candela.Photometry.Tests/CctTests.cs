using System.Numerics;

namespace Candela.Photometry.Tests;

public class CctTests
{
    [Fact]
    public void PlanckianLocus_At3200K_MatchesPublishedChromaticity()
    {
        var xy = Cct.PlanckianXy(3200f);
        Assert.Equal(0.4234, xy.X, 0.001);
        Assert.Equal(0.3990, xy.Y, 0.001);
    }

    [Theory]
    [InlineData(2856f, 0.44757f, 0.40745f)] // illuminant A (c₂ = 1.4388e-2 gives 2855.5 K)
    [InlineData(6500f, 0.31352f, 0.32363f)]
    [InlineData(2000f, 0.52669f, 0.41331f)]
    public void PlanckianLocus_MatchesTabulatedValues(float kelvin, float x, float y)
    {
        var xy = Cct.PlanckianXy(kelvin);
        Assert.Equal(x, xy.X, 0.0002);
        Assert.Equal(y, xy.Y, 0.0002);
    }

    [Fact]
    public void PlanckianTable_AgreesWithDirectIntegration()
    {
        for (float t = 1000f; t <= 25000f; t *= 1.0137f)
        {
            var table = Cct.PlanckianXy(t);
            var direct = BlackbodyRadiator.Chromaticity(t);
            Assert.True(Vector2.Distance(table, direct) < 2e-6f, $"{t} K: {table} vs {direct}");
        }
    }

    [Fact]
    public void CctOfD65_Is6504K()
    {
        var result = Cct.FromXy(0.3127f, 0.3290f);
        Assert.InRange(result.Kelvin, 6504f - 15f, 6504f + 15f);
        Assert.InRange(result.Kelvin, 6502f, 6506f);
        Assert.Equal(0.0032, result.Duv, 0.0002); // D65 lies slightly above the locus
        Assert.True(result.IsMeaningful);
    }

    [Fact]
    public void CctOfPlanckian2700K_IsWithin5K()
    {
        var result = Cct.FromXy(Cct.PlanckianXy(2700f));
        Assert.InRange(result.Kelvin, 2695f, 2705f);
        Assert.InRange(result.Kelvin, 2699.5f, 2700.5f);
        Assert.Equal(0.0, result.Duv, 1e-5);
    }

    [Fact]
    public void CctOfIlluminantASpectrum_Is2856K()
    {
        var result = Cct.FromXy(Cie1931.ChromaticityFromSpectrum(Spectrum.IlluminantA));
        Assert.InRange(result.Kelvin, 2855f, 2857f);
        Assert.Equal(0.0, result.Duv, 2e-5);
    }

    [Fact]
    public void XyFromCct_AndFromXy_RoundTripAcrossRangeAndDuv()
    {
        for (float t = 1000f; t <= 25000f; t *= 1.09f)
        {
            foreach (float duv in new[] { -0.04f, -0.02f, -0.005f, -0.001f, 0f, 0.001f, 0.005f, 0.02f, 0.04f })
            {
                var result = Cct.FromXy(Cct.XyFromCct(t, duv));
                Assert.True(MathF.Abs(result.Kelvin - t) < 0.5f, $"T {t} duv {duv}: {result}");
                Assert.True(MathF.Abs(result.Duv - duv) < 2e-5f, $"T {t} duv {duv}: {result}");
            }
        }
    }

    [Fact]
    public void CoarseToFineSearch_FindsTheSameNodeAsAFullScan()
    {
        int n = PlanckianLocus.TableLength;
        for (float t = 1000f; t <= 25000f; t *= 1.031f)
        {
            for (float duv = -0.06f; duv <= 0.06f; duv += 0.01f)
            {
                var uv = ColorSpaces.XyToUv1960(Cct.XyFromCct(t, duv));
                int full = Cct.NearestNode(uv.X, uv.Y, 0, n - 1, 1);
                int coarse = Cct.NearestNode(uv.X, uv.Y, 0, n - 1, 8);
                int fine = Cct.NearestNode(uv.X, uv.Y, Math.Max(0, coarse - 8), Math.Min(n - 1, coarse + 8), 1);
                Assert.Equal(full, fine);
            }
        }
    }

    [Fact]
    public void McCamy_IsCloseNearTheLocusBetweenAAndD65()
    {
        Assert.Equal(6504.0, Cct.FromXyMcCamy(0.3127f, 0.3290f), 3.0);
        Assert.Equal(2856.0, Cct.FromXyMcCamy(0.44757f, 0.40745f), 3.0);
        Assert.True(float.IsNaN(Cct.FromXyMcCamy(float.NaN, 0.3f)));
    }

    [Fact]
    public void InvalidOrFarChromaticities_AreReported()
    {
        Assert.False(Cct.FromXy(float.NaN, 0.3f).IsValid);
        Assert.False(Cct.FromXy(0.3f, 0f).IsValid);
        Assert.False(Cct.FromXy(0.3f, -0.1f).IsValid);
        Assert.False(CctResult.Invalid.IsMeaningful);

        var green = Cct.FromXy(0.17f, 0.70f);
        Assert.True(green.IsValid);
        Assert.False(green.IsMeaningful);
        Assert.True(green.Duv > 0.05f);
    }

    [Fact]
    public void Temperatures_AreClampedToTheTableRange()
    {
        Assert.Equal(Cct.PlanckianXy(1000f), Cct.PlanckianXy(400f));
        Assert.Equal(Cct.PlanckianXy(25000f), Cct.PlanckianXy(1e6f));
        Assert.Equal(Cct.PlanckianXy(6500f), Cct.PlanckianXy(float.NaN));
        Assert.Equal(1000f, Cct.FromXy(BlackbodyRadiator.Chromaticity(800f)).Kelvin);
        Assert.Equal(25000f, Cct.FromXy(BlackbodyRadiator.Chromaticity(60000f)).Kelvin);
        Assert.Equal(Cct.XyFromCct(4000f), Cct.XyFromCct(4000f, float.NaN));
    }

    [Fact]
    public void PositiveDuv_IsAboveTheLocus()
    {
        var on = Cct.XyFromCct(4000f);
        var above = Cct.XyFromCct(4000f, 0.01f);
        var below = Cct.XyFromCct(4000f, -0.01f);
        Assert.True(above.Y > on.Y);
        Assert.True(below.Y < on.Y);
        float distance = Vector2.Distance(ColorSpaces.XyToUv1960(above), ColorSpaces.XyToUv1960(on));
        Assert.Equal(0.01, distance, 1e-5);
    }

    [Fact]
    public void Mired_Conversions()
    {
        Assert.Equal(312.5, Cct.ToMired(3200f), 1e-3);
        Assert.Equal(3200.0, Cct.FromMired(312.5f), 1e-2);
        Assert.Equal(158.65, Cct.MiredShift(6500f, 3200f), 0.01); // full CTO
        Assert.True(Cct.MiredShift(3200f, 5600f) < 0f);          // CTB
    }
}
