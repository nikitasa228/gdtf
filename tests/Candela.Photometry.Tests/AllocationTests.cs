using System.Numerics;

namespace Candela.Photometry.Tests;

/// <summary>The per-frame paths (DMX evaluation, light meter, overlays) must not allocate.</summary>
public class AllocationTests
{
    [Fact]
    public void PerFrameOperations_DoNotAllocate()
    {
        var emitters = StandardEmitters.Rgbw(5000f);
        float[] levels = [0.2f, 0.7f, 0.1f, 0.5f];
        var model = SubtractiveFilterModel.ForCct(6500f);
        SpotLight[] lights =
        [
            new(new Vector3(0f, 0f, 6f), -Vector3.UnitZ, 30_000f, BeamDistribution.FromDegrees(BeamType.Wash, 20f, 40f)),
            new(new Vector3(2f, 0f, 6f), -Vector3.UnitZ, 30_000f, BeamDistribution.FromDegrees(BeamType.Spot, 10f, 14f)),
        ];
        SoundSource[] sources = [new(Vector3.Zero, 40f), new(Vector3.One, 45f)];
        Span<float> lut = stackalloc float[64];
        float sink = 0f;

        void Frame(int i)
        {
            var beam = BeamDistribution.FromDegrees(BeamType.Wash, 10f + i % 30, 30f + i % 30);
            sink += beam.PeakIntensityFromFlux(10_000f);
            sink += beam.Relative(0.1f);
            sink += ColorMixing.Mix(emitters, levels).Flux;
            sink += model.Evaluate(0.1f, 0.2f, 0.3f, 3200f).LuminousTransmission;
            sink += LightColor.FromCct(2700f + i, 0.002f).Rgb.X;
            sink += LightColor.FromXy(0.17f, 0.005f).Rgb.Z;
            sink += Cct.FromXy(0.4f, 0.39f).Kelvin;
            sink += Filters.FromWheelSlot(0.64f, 0.33f, 21f).LuminousTransmission;
            sink += LightMeter.IlluminanceAt(lights, new Vector3(i * 0.01f, 0f, 0f), Vector3.UnitZ);
            sink += Acoustics.SplAt(Vector3.UnitX, sources);
        }

        for (int i = 0; i < 100; i++)
            Frame(i); // warm-up: static tables, cached delegates, JIT

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            Frame(i);
            BeamDistribution.FromDegrees(BeamType.Spot, 15f, 20f).BakeLut(lut, 0.4f);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(float.IsFinite(sink));
        Assert.Equal(0, allocated);
    }
}
