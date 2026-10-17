using System.Numerics;

namespace Candela.Photometry.Tests;

public class PhotometricsTests
{
    private static readonly BeamDistribution Wash = BeamDistribution.FromDegrees(BeamType.Wash, 30f, 50f);

    [Fact]
    public void InverseSquareLaw()
    {
        float e5 = Photometrics.Illuminance(10_000f, 5f, 1f);
        Assert.Equal(400.0, e5, 1e-3);
        Assert.Equal(e5 / 4.0, Photometrics.Illuminance(10_000f, 10f, 1f), 1e-4);
        Assert.Equal(e5 / 9.0, Photometrics.Illuminance(10_000f, 15f, 1f), 1e-4);
    }

    [Fact]
    public void CosineLawAndBackFacingSurfaces()
    {
        Assert.Equal(200.0, Photometrics.Illuminance(10_000f, 5f, MathF.Cos(MathF.PI / 3f)), 1e-3);
        Assert.Equal(0f, Photometrics.Illuminance(10_000f, 5f, -0.5f));
        Assert.Equal(0f, Photometrics.Illuminance(10_000f, 5f, float.NaN));
        Assert.Equal(400.0, Photometrics.Illuminance(10_000f, 5f, 1.5f), 1e-3);
        Assert.Equal(0f, Photometrics.Illuminance(-1f, 5f, 1f));
        Assert.Equal(0f, Photometrics.Illuminance(1000f, float.NaN, 1f));
        Assert.Equal(1000f / (Photometrics.MinDistance * Photometrics.MinDistance), Photometrics.Illuminance(1000f, 0f, 1f));
    }

    [Fact]
    public void IlluminanceWithDistribution_UsesRelativeIntensity()
    {
        float theta = 15f * MathF.PI / 180f; // half the beam angle → 50 %
        float e = Photometrics.Illuminance(20_000f, Wash, theta, 4f, 0.8f);
        Assert.Equal(20_000.0 * 0.5 * 0.8 / 16.0, e, 0.01);
    }

    [Fact]
    public void ConversionsAndLambertianLuminance()
    {
        Assert.Equal(10.7639, Photometrics.FootCandlesToLux(1f), 1e-4);
        Assert.Equal(1.0, Photometrics.LuxToFootCandles(10.7639104f), 1e-6);
        Assert.Equal(100.0 * 0.5 / Math.PI, Photometrics.LuminanceLambertian(0.5f, 100f), 1e-4);
        Assert.Equal(0f, Photometrics.LuminanceLambertian(0.5f, -3f));
        Assert.Equal(100.0 / Math.PI, Photometrics.LuminanceLambertian(2f, 100f), 1e-4);
        Assert.Equal(300_000.0, Photometrics.IntensityFromIlluminance(12_000f, 5f), 0.5);
        Assert.Equal(0f, Photometrics.IntensityFromIlluminance(float.NaN, 5f));
        Assert.Equal(10.0, Photometrics.BeamDiameter(2f * MathF.Atan(0.5f), 10f), 1e-4);
    }

    [Fact]
    public void IlluminanceFromSpectralIrradiance()
    {
        var flat = Spectrum.Constant(0.01f); // 0.01 W·m⁻²·nm⁻¹ over 360–830 nm
        var (lux, color) = Photometrics.IlluminanceFromSpectralIrradiance(flat);
        double sumV = 0;
        foreach (float v in Cie1931.YBar)
            sumV += v;
        Assert.Equal(683.002 * 0.01 * 5 * sumV, lux, 0.01);
        var xy = color.ToXy();
        Assert.Equal(1.0 / 3, xy.X, 5e-4);
    }

    [Fact]
    public void LightMeter_SingleLightOverhead()
    {
        var light = new SpotLight(new Vector3(0f, 0f, 5f), -Vector3.UnitZ, 10_000f, Wash);
        Assert.Equal(400.0, LightMeter.IlluminanceAt([light], Vector3.Zero, Vector3.UnitZ), 1e-3);
        Assert.Equal(400.0, Photometrics.IlluminanceAt([light], Vector3.Zero, Vector3.UnitZ), 1e-3);

        // Off-axis point on the floor: θ = atan(x/h), d² = x² + h², cos α = cos θ.
        const float x = 2f;
        float theta = MathF.Atan(x / 5f);
        double expected = 10_000.0 * Wash.Relative(theta) * MathF.Cos(theta) / (x * x + 25.0);
        Assert.Equal(expected, LightMeter.IlluminanceAt([light], new Vector3(x, 0f, 0f), Vector3.UnitZ), expected * 1e-5);
        Assert.Equal(expected, light.IntensityToward(new Vector3(x, 0f, 0f)) * MathF.Cos(theta) / (x * x + 25.0), expected * 1e-5);
    }

    [Fact]
    public void LightMeter_InverseSquareAlongTheAxis()
    {
        var light = new SpotLight(Vector3.Zero, Vector3.UnitY, 50_000f, Wash);
        float near = LightMeter.IlluminanceAt([light], new Vector3(0f, 3f, 0f), -Vector3.UnitY);
        float far = LightMeter.IlluminanceAt([light], new Vector3(0f, 6f, 0f), -Vector3.UnitY);
        Assert.Equal(near / 4.0, far, near * 1e-6);
    }

    [Fact]
    public void LightMeter_IgnoresLightsBehindTheSurfaceAndOutsideTheBeam()
    {
        var light = new SpotLight(new Vector3(0f, 0f, 5f), -Vector3.UnitZ, 10_000f, Wash);
        Assert.Equal(0f, LightMeter.IlluminanceAt([light], Vector3.Zero, -Vector3.UnitZ));
        Assert.Equal(0f, LightMeter.IlluminanceAt([light], new Vector3(20f, 0f, 0f), Vector3.UnitZ));
        Assert.Equal(0f, LightMeter.IlluminanceAt([light], light.Position, Vector3.UnitZ));
        Assert.Equal(0f, LightMeter.IlluminanceAt(ReadOnlySpan<SpotLight>.Empty, Vector3.Zero, Vector3.UnitZ));
    }

    [Fact]
    public void LightMeter_ZeroNormalMeasuresNormalToEachRay()
    {
        var a = new SpotLight(new Vector3(-3f, 0f, 4f), new Vector3(3f, 0f, -4f), 1000f, Wash);
        var b = new SpotLight(new Vector3(3f, 0f, 4f), new Vector3(-3f, 0f, -4f), 1000f, Wash);
        Assert.Equal(2.0 * 1000 / 25, LightMeter.IlluminanceAt([a, b], Vector3.Zero, Vector3.Zero), 1e-4);
        Assert.Equal(2.0 * 1000 / 25 * 0.8, LightMeter.IlluminanceAt([a, b], Vector3.Zero, Vector3.UnitZ), 1e-4);
    }

    [Fact]
    public void LightMeter_SumsLightsAndReportsColor()
    {
        var red = new SpotLight(new Vector3(-1f, 0f, 4f), -Vector3.UnitZ, 8000f, Wash, LightColor.FromXy(0.68f, 0.31f));
        var blue = new SpotLight(new Vector3(1f, 0f, 4f), -Vector3.UnitZ, 3000f, Wash, LightColor.FromXy(0.15f, 0.06f));
        var p = new Vector3(0.2f, 0.1f, 0f);
        float eRed = LightMeter.IlluminanceAt([red], p, Vector3.UnitZ);
        float eBlue = LightMeter.IlluminanceAt([blue], p, Vector3.UnitZ);
        float sum = eRed + eBlue;
        Assert.Equal(sum, LightMeter.IlluminanceAt([red, blue], p, Vector3.UnitZ), sum * 1e-6);

        var rgb = LightMeter.IlluminanceRgbAt([red, blue], p, Vector3.UnitZ);
        Assert.Equal(sum, ColorSpaces.LuminanceRec2020(rgb), sum * 1e-4);

        // The colour of the mixed light is the illuminance-weighted XYZ sum of the two colours.
        var xyz = red.Color.ToXyz() * eRed + blue.Color.ToXyz() * eBlue;
        var expected = new Vector2(xyz.X, xyz.Y) / (xyz.X + xyz.Y + xyz.Z);
        var mixed = LightColor.FromRec2020(rgb).ToXy();
        Assert.True(Vector2.Distance(expected, mixed) < 1e-4f, $"{expected} vs {mixed}");
    }

    [Fact]
    public void LightMeter_BatchAndGridMatchSinglePointQueries()
    {
        SpotLight[] lights =
        [
            new(new Vector3(0f, 0f, 6f), new Vector3(0.2f, 0.1f, -1f), 30_000f, Wash),
            new(new Vector3(4f, -2f, 5f), new Vector3(-0.5f, 0.3f, -1f), 12_000f, BeamDistribution.FromDegrees(BeamType.Spot, 15f, 20f)),
        ];
        var origin = new Vector3(-3f, -3f, 0f);
        var stepU = new Vector3(0.5f, 0f, 0f);
        var stepV = new Vector3(0f, 0.5f, 0f);
        const int nu = 13, nv = 11;
        var grid = new float[nu * nv];
        LightMeter.IlluminanceOnGrid(lights, origin, stepU, stepV, nu, nv, grid);
        var points = new Vector3[nu * nv];
        for (int v = 0; v < nv; v++)
        {
            for (int u = 0; u < nu; u++)
            {
                var p = origin + u * stepU + v * stepV;
                points[v * nu + u] = p;
                Assert.Equal(LightMeter.IlluminanceAt(lights, p, Vector3.UnitZ), grid[v * nu + u]);
            }
        }

        var batch = new float[points.Length];
        LightMeter.IlluminanceAt(lights, points, Vector3.UnitZ, batch);
        Assert.Equal(grid, batch);
        Assert.Throws<ArgumentException>(() => LightMeter.IlluminanceAt(lights, points, Vector3.UnitZ, new float[3]));
        Assert.Throws<ArgumentException>(() => LightMeter.IlluminanceOnGrid(lights, origin, stepU, stepV, nu, nv, new float[3]));
        Assert.Throws<ArgumentOutOfRangeException>(() => LightMeter.IlluminanceOnGrid(lights, origin, stepU, stepV, -1, nv, grid));
    }

    [Fact]
    public void BakedLut_ReproducesTheReferenceMeterWithinOnePercent()
    {
        // What the GPU overlay does: look the profile up in a linearly filtered 256-entry LUT over [0, cutoff].
        var beam = BeamDistribution.FromDegrees(BeamType.Spot, 12f, 18f);
        var lut = beam.BakeLut(256, beam.CutoffAngle);
        var light = new SpotLight(new Vector3(0f, 0f, 8f), -Vector3.UnitZ, 100_000f, beam);
        for (float x = 0f; x < 3f; x += 0.05f)
        {
            var p = new Vector3(x, 0f, 0f);
            float reference = LightMeter.IlluminanceAt([light], p, Vector3.UnitZ);
            float theta = MathF.Atan(x / 8f);
            float f = theta / beam.CutoffAngle * (lut.Length - 1);
            float rel = 0f;
            if (f < lut.Length - 1)
            {
                int i = (int)f;
                rel = lut[i] + (lut[i + 1] - lut[i]) * (f - i);
            }

            float gpu = 100_000f * rel * MathF.Cos(theta) / (x * x + 64f);
            Assert.True(MathF.Abs(gpu - reference) <= 0.01f * reference + 0.01f, $"x {x}: {gpu} vs {reference}");
        }
    }

    [Fact]
    public void SpotLight_SanitisesInput()
    {
        var light = new SpotLight(Vector3.One, Vector3.Zero, float.NaN, Wash);
        Assert.Equal(-Vector3.UnitZ, light.Direction);
        Assert.Equal(0f, light.PeakCandela);
        Assert.Equal(LightColor.White, light.Color);
        var normalized = new SpotLight(Vector3.Zero, new Vector3(0f, 3f, 4f), 10f, Wash);
        Assert.Equal(1.0, normalized.Direction.Length(), 1e-6);
        Assert.Equal(0f, normalized.IntensityToward(Vector3.Zero));
    }
}
