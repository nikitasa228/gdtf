using System.Numerics;

namespace Candela.Photometry.Tests;

/// <summary>
/// Tests that measure the allocations of the current thread. They run on their own, after the parallel tests, so that
/// garbage collections triggered by other tests (the GDTF scan allocates hundreds of megabytes) do not disturb the
/// per-thread allocation counter.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AllocationMeasurementCollection
{
    /// <summary>Collection name.</summary>
    public const string Name = "Allocation measurement";
}

/// <summary>The per-frame paths (DMX evaluation, light meter, overlays) must not allocate.</summary>
[Collection(AllocationMeasurementCollection.Name)]
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
        float[] lut = new float[64];
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
            BeamDistribution.FromDegrees(BeamType.Spot, 15f, 20f).BakeLut(lut, 0.4f);
        }

        for (int i = 0; i < 100; i++)
            Frame(i); // warm-up: static tables, cached delegates, JIT

        // A per-frame allocation shows up in every round (≥ 1000 objects), whereas the runtime itself now and then
        // charges a few kilobytes to a thread that allocates nothing (observed around garbage collections triggered by
        // other threads). So the paths are allocation-free when at least one round of 1000 frames allocated nothing.
        var rounds = new List<long>();
        for (int round = 0; round < 5; round++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++)
                Frame(i);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            if (allocated == 0)
                break;
            rounds.Add(allocated);
        }

        Assert.True(float.IsFinite(sink));
        Assert.True(rounds.Count < 5, "bytes allocated per round of 1000 frames: " + string.Join(", ", rounds));
    }
}
