using System.Numerics;

namespace Candela.Photometry.Tests;

public class AcousticsTests
{
    [Fact]
    public void TwoEqualSources_Add3Decibels()
    {
        Assert.Equal(63.0103, Acoustics.EnergeticSum(60f, 60f), 1e-3);
        Assert.Equal(70.0, Acoustics.EnergeticSum(Enumerable.Repeat(60f, 10).ToArray()), 1e-3);
        Assert.Equal(60.4139, Acoustics.EnergeticSum(60f, 50f), 1e-3);
    }

    [Fact]
    public void EnergeticSum_EdgeCases()
    {
        Assert.Equal(Acoustics.Silence, Acoustics.EnergeticSum());
        Assert.Equal(Acoustics.Silence, Acoustics.EnergeticSum(float.NegativeInfinity, float.NaN));
        Assert.Equal(60.0, Acoustics.EnergeticSum(60f, float.NaN, float.NegativeInfinity), 1e-4);
        Assert.Equal(403.0103, Acoustics.EnergeticSum(400f, 400f), 1e-3); // no overflow
        Assert.Equal(float.PositiveInfinity, Acoustics.EnergeticSum(float.PositiveInfinity, 3f));
        Assert.Equal(63.0103, Acoustics.EnergeticSum(new[] { 60f, 60f }.AsSpan()), 1e-3);
    }

    [Fact]
    public void FreeFieldSpreading()
    {
        Assert.Equal(90.0, Acoustics.SplAtDistance(90f, 1f), 1e-5);
        Assert.Equal(80.0, Acoustics.SplAtDistance(100f, 10f), 1e-5);
        Assert.Equal(90.0 - 6.0206, Acoustics.SplAtDistance(90f, 2f), 1e-4);
        Assert.Equal(110.0, Acoustics.SplAtDistance(90f, 0.01f), 1e-5); // clamped at 0.1 m
        Assert.Equal(110.0, Acoustics.SplAtDistance(90f, 0f), 1e-5);
        Assert.Equal(Acoustics.Silence, Acoustics.SplAtDistance(float.NaN, 2f));
        Assert.Equal(Acoustics.Silence, Acoustics.SplAtDistance(90f, float.PositiveInfinity));
    }

    [Fact]
    public void SplAt_SumsSourcesAtTheirDistances()
    {
        SoundSource[] sources = [new(new Vector3(2f, 0f, 0f), 60f), new(new Vector3(-2f, 0f, 0f), 60f)];
        Assert.Equal(60.0 - 6.0206 + 3.0103, Acoustics.SplAt(Vector3.Zero, sources), 1e-3);
        Assert.Equal(Acoustics.Silence, Acoustics.SplAt(Vector3.Zero, ReadOnlySpan<SoundSource>.Empty));
        Assert.Equal(60.0 - 6.0206, Acoustics.SplAt(Vector3.Zero, [sources[0], new SoundSource(Vector3.Zero, float.NaN)]), 1e-3);
    }
}
