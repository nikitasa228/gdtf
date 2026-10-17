namespace Candela.Photometry.Tests;

public class BeamDistributionTests
{
    private const float Deg = MathF.PI / 180f;

    public static TheoryData<BeamType, float, float> Beams => new()
    {
        { BeamType.Wash, 20f, 40f },
        { BeamType.Wash, 2f, 3.6f },
        { BeamType.Spot, 20f, 30f },
        { BeamType.Spot, 4f, 4.5f },
        { BeamType.PC, 25f, 45f },
        { BeamType.Fresnel, 15f, 60f },
        { BeamType.Rectangle, 60f, 80f },
        { BeamType.Wash, 120f, 160f },
    };

    /// <summary>Composite Simpson rule with many intervals — an independent reference for the solid-angle integral.</summary>
    private static double BruteForceSolidAngle(BeamDistribution beam)
    {
        const int n = 200_000;
        double h = Math.PI / n, sum = 0;
        for (int i = 0; i <= n; i++)
        {
            double t = i * h;
            double f = beam.Relative((float)t) * Math.Sin(t);
            sum += f * (i == 0 || i == n ? 1 : i % 2 == 1 ? 4 : 2);
        }

        return 2 * Math.PI * sum * h / 3;
    }

    [Theory]
    [MemberData(nameof(Beams))]
    public void HalfIntensityAtHalfBeamAngle_AndTenPercentAtHalfFieldAngle(BeamType type, float beamDeg, float fieldDeg)
    {
        var beam = BeamDistribution.FromDegrees(type, beamDeg, fieldDeg);
        Assert.False(beam.FieldAngleEstimated);
        Assert.Equal(beamDeg * Deg, beam.BeamAngle, 1e-6);
        Assert.Equal(fieldDeg * Deg, beam.FieldAngle, 1e-6);
        Assert.Equal(1f, beam.Relative(0f));
        Assert.Equal(0.5, beam.Relative(0.5f * beamDeg * Deg), 0.005);
        Assert.Equal(0.1, beam.Relative(0.5f * fieldDeg * Deg), 0.001);
        Assert.Equal(0.5, beam.Relative(0.5f * beamDeg * Deg), 1e-5);
        Assert.Equal(0.1, beam.Relative(0.5f * fieldDeg * Deg), 1e-5);
    }

    [Theory]
    [MemberData(nameof(Beams))]
    public void Profile_IsMonotoneContinuousAndZeroBeyondCutoff(BeamType type, float beamDeg, float fieldDeg)
    {
        var beam = BeamDistribution.FromDegrees(type, beamDeg, fieldDeg);
        float previous = 1f;
        const int steps = 4000;
        for (int i = 1; i <= steps; i++)
        {
            float t = beam.CutoffAngle * i / steps;
            float r = beam.Relative(t);
            Assert.True(r <= previous + 1e-6f, $"not monotone at {t}");
            Assert.True(previous - r < 0.02f, $"jump at {t}: {previous} → {r}");
            previous = r;
        }

        Assert.Equal(0f, beam.Relative(beam.CutoffAngle));
        Assert.Equal(0f, beam.Relative(beam.CutoffAngle + 0.1f));
        Assert.Equal(0f, beam.Relative(MathF.PI));
        Assert.Equal(beam.Relative(0.1f), beam.Relative(-0.1f));
        Assert.Equal(0f, beam.Relative(float.NaN));
        Assert.True(beam.CutoffAngle > 0.5f * beam.FieldAngle);
    }

    [Theory]
    [MemberData(nameof(Beams))]
    public void SolidAngleIntegral_MatchesBruteForceIntegration(BeamType type, float beamDeg, float fieldDeg)
    {
        var beam = BeamDistribution.FromDegrees(type, beamDeg, fieldDeg);
        double omega = beam.SolidAngleIntegral();
        double reference = BruteForceSolidAngle(beam);
        Assert.Equal(reference, omega, reference * 1e-5);
    }

    [Theory]
    [InlineData(0.01f)]
    [InlineData(0.2f)]
    [InlineData(0.6f)]
    [InlineData(1.2f)]
    [InlineData(MathF.PI / 2f)]
    public void UniformCone_PeakIsFluxOverConeSolidAngle(float alpha)
    {
        var cone = BeamDistribution.UniformCone(alpha);
        const float flux = 1000f;
        double expected = flux / (2.0 * Math.PI * (1.0 - Math.Cos(alpha)));
        Assert.Equal(expected, cone.PeakIntensityFromFlux(flux), expected * 1e-6);
        Assert.Equal(1f, cone.Relative(alpha * 0.999f));
        Assert.Equal(0f, cone.Relative(alpha * 1.001f));
        Assert.Equal(2f * alpha, cone.BeamAngle, 1e-6);
        Assert.Equal(cone.BeamAngle, cone.FieldAngle);
        Assert.Equal(0f, cone.Exponent);
    }

    [Theory]
    [InlineData(BeamType.Glow)]
    [InlineData(BeamType.None)]
    public void GlowAndNone_AreLambertianEmitters(BeamType type)
    {
        var beam = BeamDistribution.FromDegrees(type, 10f, 20f);
        Assert.Equal(BeamShape.Lambertian, beam.Shape);
        Assert.Equal(type, beam.Type);
        Assert.Equal(Math.PI, beam.SolidAngleIntegral(), 1e-6);
        Assert.Equal(100.0 / Math.PI, beam.PeakIntensityFromFlux(100f), 1e-4);
        Assert.Equal(MathF.Cos(0.7f), beam.Relative(0.7f), 1e-6);
        Assert.Equal(0f, beam.Relative(MathF.PI / 2f + 0.01f));
        Assert.Equal(120.0, beam.BeamAngle / Deg, 1e-3);
        Assert.Equal(0.5, beam.Relative(beam.BeamAngle / 2f), 1e-6);
        Assert.Equal(0.1, beam.Relative(beam.FieldAngle / 2f), 1e-6);
    }

    [Fact]
    public void Default_IsALambertianEmitter()
    {
        BeamDistribution beam = default;
        Assert.Equal(BeamShape.Lambertian, beam.Shape);
        Assert.Equal(Math.PI, beam.SolidAngleIntegral(), 1e-6);
    }

    [Theory]
    [MemberData(nameof(Beams))]
    public void FluxAndPeakIntensity_RoundTrip(BeamType type, float beamDeg, float fieldDeg)
    {
        var beam = BeamDistribution.FromDegrees(type, beamDeg, fieldDeg);
        float peak = beam.PeakIntensityFromFlux(12_000f);
        Assert.True(peak > 0f);
        Assert.Equal(12_000.0, beam.FluxFromPeakIntensity(peak), 0.5);
        Assert.Equal(0f, beam.PeakIntensityFromFlux(float.NaN));
        Assert.Equal(0f, beam.PeakIntensityFromFlux(-1f));
        Assert.Equal(0f, beam.FluxFromPeakIntensity(float.PositiveInfinity));
    }

    [Fact]
    public void NarrowerBeams_HaveHigherPeakIntensity()
    {
        float previous = 0f;
        foreach (float angle in new[] { 60f, 40f, 20f, 10f, 5f, 2f })
        {
            float peak = BeamDistribution.FromDegrees(BeamType.Spot, angle, angle * 1.4f).PeakIntensityFromFlux(10_000f);
            Assert.True(peak > previous);
            previous = peak;
        }

        // A soft 20°/40° wash concentrates about 10 000 lm into ~ 70 000 cd, the order of magnitude of real washes.
        float wash = BeamDistribution.FromDegrees(BeamType.Wash, 20f, 40f).PeakIntensityFromFlux(10_000f);
        Assert.InRange(wash, 50_000f, 90_000f);
    }

    [Fact]
    public void BakeLut_SamplesRelativeEvenly()
    {
        var beam = BeamDistribution.FromDegrees(BeamType.Wash, 30f, 50f);
        var lut = beam.BakeLut(65, 0.5f);
        Assert.Equal(65, lut.Length);
        for (int i = 0; i < lut.Length; i++)
            Assert.Equal(beam.Relative(0.5f * i / 64f), lut[i]);
        var defaultRange = beam.BakeLut(2, float.NaN);
        Assert.Equal(beam.Relative(beam.CutoffAngle), defaultRange[1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => beam.BakeLut(1, 1f));
        Assert.Throws<ArgumentException>(() => beam.BakeLut(new float[1], 1f));
    }

    [Fact]
    public void MissingOrInvalidFieldAngle_IsEstimatedPerBeamType()
    {
        var wash = BeamDistribution.FromDegrees(BeamType.Wash, 25f, 25f); // GDTF default: field = beam
        Assert.True(wash.FieldAngleEstimated);
        Assert.Equal(2.0, wash.Exponent, 0.01); // a true Gaussian
        var spot = BeamDistribution.FromDegrees(BeamType.Spot, 20f, float.NaN);
        Assert.True(spot.FieldAngleEstimated);
        Assert.Equal(26.0, spot.FieldAngle / Deg, 0.01);
        var inverted = BeamDistribution.FromDegrees(BeamType.Fresnel, 30f, 10f);
        Assert.Equal(60.0, inverted.FieldAngle / Deg, 0.01);
    }

    [Fact]
    public void BadAngles_GiveUsableDistributions()
    {
        foreach (var (beamDeg, fieldDeg) in new[] { (0f, 0f), (-10f, 5f), (float.NaN, float.NaN), (float.PositiveInfinity, 30f), (400f, 500f), (10f, 1000f), (0.001f, 0.002f) })
        {
            var beam = BeamDistribution.FromDegrees(BeamType.Wash, beamDeg, fieldDeg);
            double omega = beam.SolidAngleIntegral();
            Assert.True(omega > 0 && double.IsFinite(omega), $"{beamDeg}/{fieldDeg}: {omega}");
            Assert.InRange(beam.Exponent, BeamDistribution.MinExponent, BeamDistribution.MaxExponent);
            Assert.True(beam.CutoffAngle <= MathF.PI);
            Assert.Equal(1f, beam.Relative(0f));
        }

        Assert.Equal(BeamType.Wash, BeamDistribution.Create((BeamType)42, 0.3f, 0.6f).Type);
    }

    [Fact]
    public void Zoom_KeepsTheProfileShape()
    {
        var beam = BeamDistribution.FromDegrees(BeamType.Spot, 10f, 14f);
        var zoomed = beam.Zoomed(40f * Deg);
        Assert.Equal(40.0, zoomed.BeamAngle / Deg, 1e-3);
        Assert.Equal(56.0, zoomed.FieldAngle / Deg, 1e-2);
        Assert.Equal(beam.Exponent, zoomed.Exponent, 1e-4);
        Assert.Equal(BeamType.Spot, zoomed.Type);
        var reshaped = beam.WithAngles(20f * Deg, 40f * Deg);
        Assert.Equal(40.0, reshaped.FieldAngle / Deg, 1e-3);
        Assert.Equal(beam, BeamDistribution.FromDegrees(BeamType.Spot, 10f, 14f));
        Assert.NotEqual(beam, zoomed);
        Assert.Equal(BeamDistribution.UniformCone(0.2f), BeamDistribution.UniformCone(0.1f).Zoomed(0.4f));
        var glow = BeamDistribution.Lambertian();
        Assert.Equal(glow, glow.Zoomed(0.3f));
        Assert.Contains("Spot", beam.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Wash", BeamType.Wash)]
    [InlineData("spot", BeamType.Spot)]
    [InlineData(" PC ", BeamType.PC)]
    [InlineData("Fresnel", BeamType.Fresnel)]
    [InlineData("Glow", BeamType.Glow)]
    [InlineData("None", BeamType.None)]
    [InlineData("RECTANGLE", BeamType.Rectangle)]
    [InlineData("", BeamType.Wash)]
    [InlineData(null, BeamType.Wash)]
    [InlineData("Laser", BeamType.Wash)]
    [InlineData("3", BeamType.Wash)]
    public void BeamTypes_ParseGdtfValues(string? value, BeamType expected) => Assert.Equal(expected, BeamTypes.Parse(value));

    [Fact]
    public void EveryBeamInTheBundledGdtfFiles_GivesAValidDistribution()
    {
        int count = 0;
        var types = new HashSet<BeamType>();
        foreach (var (file, typeName, beamDeg, fieldDeg) in RepoData.Beams())
        {
            var type = BeamTypes.Parse(typeName);
            types.Add(type);
            var beam = BeamDistribution.FromDegrees(type, beamDeg, fieldDeg);
            double omega = beam.SolidAngleIntegral();
            Assert.True(omega > 0 && double.IsFinite(omega), $"{file}: {typeName} {beamDeg}/{fieldDeg}");
            float peak = beam.PeakIntensityFromFlux(1000f);
            Assert.True(peak > 0f && float.IsFinite(peak), $"{file}: {typeName} {beamDeg}/{fieldDeg}");
            if (beam.Shape == BeamShape.GeneralizedGaussian)
                Assert.Equal(0.5, beam.Relative(beam.BeamAngle / 2f), 1e-4);
            count++;
        }

        Assert.True(count > 10_000, $"only {count} beams");
        Assert.Superset(new HashSet<BeamType> { BeamType.Wash, BeamType.Spot, BeamType.None, BeamType.Rectangle, BeamType.Glow, BeamType.Fresnel }, types);
    }

    [Fact]
    public void PeakIntensityFromSpecsFluxAndAngles_IsInTheRangeOfPublishedCandela()
    {
        // For fixtures whose _specs record has flux, beam and field angles and a peak intensity, the distribution
        // turns flux into peak candela. Real optics differ (published flux is often measured differently from the
        // peak), so only the bulk of the population is checked: the median ratio must be within a factor of two.
        var ratios = new List<double>();
        foreach (var f in RepoData.SpecFixtures)
        {
            if (!f.TryGetProperty("luminousFluxLm", out var flux) || flux.ValueKind != System.Text.Json.JsonValueKind.Number ||
                !f.TryGetProperty("maxLuminousIntensityCd", out var cd) || cd.ValueKind != System.Text.Json.JsonValueKind.Number ||
                !f.TryGetProperty("beamAngleDeg", out var beam) || beam.ValueKind != System.Text.Json.JsonValueKind.Object ||
                !f.TryGetProperty("fieldAngleDeg", out var field) || field.ValueKind != System.Text.Json.JsonValueKind.Number)
            {
                continue;
            }

            float beamMin = beam.GetProperty("min").GetSingle();
            var type = f.TryGetProperty("lensType", out var lens) && lens.ValueKind == System.Text.Json.JsonValueKind.String
                ? BeamTypes.Parse(lens.GetString())
                : BeamType.Wash;
            var distribution = BeamDistribution.FromDegrees(type == BeamType.Glow || type == BeamType.None ? BeamType.Wash : type,
                beamMin, field.GetSingle());
            float model = distribution.PeakIntensityFromFlux(flux.GetSingle());
            if (model > 0f && cd.GetDouble() > 0)
                ratios.Add(model / cd.GetDouble());
        }

        Assert.True(ratios.Count > 300, $"only {ratios.Count} fixtures");
        ratios.Sort();
        double median = ratios[ratios.Count / 2];
        Assert.InRange(median, 0.5, 2.0);
    }
}
