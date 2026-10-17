namespace Candela.Photometry.Tests;

public class SpectrumTests
{
    [Fact]
    public void Sample_InterpolatesLinearlyAndIsZeroOutside()
    {
        var s = new Spectrum(400f, 10f, [1f, 3f, 5f]);
        Assert.Equal(420f, s.EndNm);
        Assert.Equal(1f, s.Sample(400f));
        Assert.Equal(2f, s.Sample(405f), 1e-6);
        Assert.Equal(4.5f, s.Sample(417.5f), 1e-6);
        Assert.Equal(5f, s.Sample(420f));
        Assert.Equal(0f, s.Sample(399f));
        Assert.Equal(0f, s.Sample(421f));
        Assert.Equal(0f, s.Sample(float.NaN));
        Assert.Equal(1f, s.SampleClamped(300f));
        Assert.Equal(5f, s.SampleClamped(900f));
    }

    [Fact]
    public void Constructor_ReplacesNonFiniteValuesAndRejectsBadGrids()
    {
        var s = new Spectrum(400f, 10f, [1f, float.NaN, float.PositiveInfinity, 2f]);
        Assert.Equal([1f, 0f, 0f, 2f], s.ToArray());
        Assert.Throws<ArgumentOutOfRangeException>(() => new Spectrum(400f, 0f, [1f]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Spectrum(400f, -5f, [1f]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Spectrum(float.NaN, 5f, [1f]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Spectrum(400f, 5f, ReadOnlySpan<float>.Empty));
    }

    [Fact]
    public void Constructor_CopiesInput()
    {
        var values = new[] { 1f, 2f };
        var s = new Spectrum(400f, 10f, values);
        values[0] = 100f;
        Assert.Equal(1f, s[0]);
    }

    [Fact]
    public void Resample_PointSamplesOntoNewGrid()
    {
        var s = Spectrum.FromFunction(w => w, 400f, 10f, 11);
        var r = s.Resample(405f, 5f, 3);
        Assert.Equal([405f, 410f, 415f], r.ToArray());
        var beyond = s.Resample(490f, 10f, 3);
        Assert.Equal([490f, 500f, 0f], beyond.ToArray());
    }

    [Fact]
    public void Multiply_UsesThisGridAndExtendsTheFactorWithEdgeValues()
    {
        var source = Spectrum.Constant(2f, 360f, 5f, 95);
        var filter = new Spectrum(400f, 100f, [0.5f, 0.25f]);
        var filtered = source * filter;
        Assert.True(filtered.SameGrid(source));
        Assert.Equal(1f, filtered.Sample(360f));      // left edge value 0.5
        Assert.Equal(0.75f, filtered.Sample(450f), 6); // interpolated 0.375
        Assert.Equal(0.5f, filtered.Sample(830f));     // right edge value 0.25
    }

    [Fact]
    public void Add_OnDifferentGridsCoversTheUnion()
    {
        var a = new Spectrum(400f, 10f, [1f, 1f, 1f]);
        var b = new Spectrum(410f, 5f, [2f, 2f, 2f, 2f, 2f]);
        var sum = a + b;
        Assert.Equal(400f, sum.StartNm);
        Assert.Equal(5f, sum.StepNm);
        Assert.Equal(430f, sum.EndNm);
        Assert.Equal(1f, sum.Sample(400f));
        Assert.Equal(3f, sum.Sample(415f));
        Assert.Equal(2f, sum.Sample(430f));
        var same = a + a;
        Assert.Equal([2f, 2f, 2f], same.ToArray());
    }

    [Fact]
    public void ScaleAndOperators()
    {
        var s = new Spectrum(400f, 10f, [1f, 2f]);
        Assert.Equal([3f, 6f], (s * 3f).ToArray());
        Assert.Equal([3f, 6f], (3f * s).ToArray());
        Assert.Equal([0f, 0f], s.Scale(float.NaN).ToArray());
        Assert.Equal(2f, s.MaxValue());
        Assert.Equal([0.5f, 1f], s.NormalizedToPeak().ToArray());
        Assert.Equal(1f, s.NormalizedAt(410f).Sample(410f));
    }

    [Fact]
    public void Integrate_UsesSummationRuleOnFinerGrid()
    {
        var s = Spectrum.Constant(2f, 400f, 10f, 11);
        Assert.Equal(2.0 * 11 * 10, s.Integrate(), 1e-6);

        var weight = Spectrum.Constant(0.5f, 400f, 10f, 11);
        Assert.Equal(1.0 * 11 * 10, s.Integrate(weight), 1e-6);

        // Weighting on a finer grid that covers only 450–500 nm: 11 samples × 5 nm × (2 × 0.5).
        var fine = Spectrum.Constant(0.5f, 450f, 5f, 11);
        Assert.Equal(1.0 * 11 * 5, s.Integrate(fine), 1e-6);
        Assert.Equal(s.Integrate(fine), fine.Integrate(s), 1e-6);

        var disjoint = Spectrum.Constant(1f, 700f, 5f, 3);
        Assert.Equal(0.0, s.Integrate(disjoint));
    }

    [Fact]
    public void Blackbody_FollowsPlancksLaw()
    {
        var bb = Spectrum.Blackbody(3000f);
        Assert.Equal(1f, bb.Sample(560f), 1e-6);
        double expected = BlackbodyRadiator.SpectralRadiance(400e-9, 3000) / BlackbodyRadiator.SpectralRadiance(560e-9, 3000);
        Assert.Equal(expected, bb.Sample(400f), 1e-5);

        // Wien: at 5800 K the peak is near 500 nm, so 500 nm exceeds both 400 nm and 700 nm.
        var sun = Spectrum.Blackbody(5800f);
        Assert.True(sun.Sample(500f) > sun.Sample(400f));
        Assert.True(sun.Sample(500f) > sun.Sample(700f));
    }

    [Fact]
    public void Gaussian_HasRequestedPeakAndWidth()
    {
        var g = Spectrum.Gaussian(550f, 20f);
        Assert.Equal(1f, g.Sample(550f), 1e-6);
        Assert.Equal(0.5f, g.Sample(540f), 1e-4);
        Assert.Equal(0.5f, g.Sample(560f), 1e-4);
    }

    [Fact]
    public void ReferenceIlluminant_SwitchesFromPlanckianToDaylightAt5000K()
    {
        var warm = Spectrum.ReferenceIlluminant(3200f);
        Assert.Equal(Cct.PlanckianXy(3200f).X, Cie1931.ChromaticityFromSpectrum(warm).X, 1e-3);
        var cool = Spectrum.ReferenceIlluminant(6504f);
        Assert.Equal(Cie1931.ChromaticityFromSpectrum(Spectrum.Daylight(6504f)), Cie1931.ChromaticityFromSpectrum(cool));
    }

    [Fact]
    public void TryFromSamples_CleansMessyMeasurementData()
    {
        float[] wavelengths = [500f, 400f, float.NaN, 450f, 450f, -3f, 600f];
        float[] values = [5f, 1f, 9f, 2f, 4f, 9f, -1f];
        Assert.True(Spectrum.TryFromSamples(wavelengths, values, 50f, out var s));
        Assert.Equal(400f, s.StartNm);
        Assert.Equal(600f, s.EndNm);
        Assert.Equal([1f, 3f, 5f, 2.5f, 0f], s.ToArray());

        Assert.False(Spectrum.TryFromSamples([500f], [1f], 1f, out _));
        Assert.False(Spectrum.TryFromSamples([500f, 500f], [1f, 2f], 1f, out _));
        Assert.False(Spectrum.TryFromSamples([float.NaN, 400f], [1f, 2f], 1f, out _));
        Assert.True(Spectrum.TryFromSamples([400f, 410f], [1f, 2f], float.NaN, out var defaultStep));
        Assert.Equal(1f, defaultStep.StepNm);
    }
}
