using System.Diagnostics;
using System.Reflection;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;
using Candela.Dmx.Input;
using Xunit.Abstractions;

namespace Candela.Dmx.Tests;

/// <summary>Timing tests run alone (not next to the library-wide sweep, which loads every core).</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PerformanceCollection
{
    /// <summary>Collection name.</summary>
    public const string Name = "Performance";
}

/// <summary>
/// The per-frame cost of a large rig: 2000 fixtures of a realistic mix (spots, profiles, washes, pixel battens, LED
/// panels, a hazer) whose every DMX slot changes every frame — the worst case a console can produce.
/// </summary>
[Collection(PerformanceCollection.Name)]
public class PerformanceTests(ITestOutputHelper output)
{
    private const int FixtureCount = 2000;

    private static readonly (string Prefix, string? Mode)[] Mix =
    [
        ("Martin_Professional@MAC_Viper_Profile", "Basic"),
        ("Robe_Lighting@Robin_Esprite@", "Mode 1 - Standard 16 bit"),
        ("Prolights@ArcPod27Q", "17ch"),
        ("Robe_Lighting@Robin_Tetra1", "Mode 4 - Pixel RGBW"),
        ("Martin_Professional@MAC_Encore_Two", "Basic"),
        ("Clay_Paky@Arolla_Aqua_LT", null),
        ("Elation@Paladin_Panel", null),
        ("ADJ@Entour_Haze_PRO", "4 Channel Mode"),
    ];

    private static readonly bool Optimized =
        typeof(FixtureEvaluator).Assembly.GetCustomAttribute<DebuggableAttribute>()?.IsJITOptimizerDisabled != true;

    /// <summary>Median of the frame times of the best of three runs (other processes on a shared machine cause outliers).</summary>
    private static double BestMedian(Func<double> frame, int warmup, int frames)
    {
        double best = double.PositiveInfinity;
        for (int run = 0; run < 3; run++)
        {
            for (int i = 0; i < warmup; i++) frame();
            var times = new double[frames];
            for (int i = 0; i < frames; i++) times[i] = frame();
            Array.Sort(times);
            best = Math.Min(best, times[frames / 2]);
        }
        return best;
    }

    [Fact]
    public void Two_thousand_fixtures_update_within_the_frame_budget()
    {
        // scene: the mix patched back to back across as many universes as needed
        var profiles = Mix.Select(m => TestData.Profile(m.Prefix, m.Mode)).ToArray();
        var scene = new Scene();
        int universes = 1, address = 1;
        for (int i = 0; i < FixtureCount; i++)
        {
            var p = profiles[i % profiles.Length];
            if (address + p.Footprint - 1 > 512)
            {
                universes++;
                address = 1;
            }
            var entity = new Entity($"Fixture {i + 1}");
            entity.Add(new FixtureComponent
            {
                GdtfSpec = TestData.Entry(Mix[i % Mix.Length].Prefix).FileName,
                GdtfMode = p.Mode.Name,
                FixtureId = i + 1,
                Addresses = { new DmxAddress(universes, address) },
            });
            scene.Add(entity);
            address += p.Footprint;
        }
        var hub = new DmxInputHub();
        var source = new DmxSourceId("test", "busy console");
        using var engine = new DmxEngine(scene, TestData.Profiles, hub);
        var rng = new Random(12345);
        var data = new byte[512];
        double t = 0;

        double EngineFrame()
        {
            for (int u = 1; u <= universes; u++)
            {
                rng.NextBytes(data);
                hub.Submit(source, u, data);
            }
            t += 1.0 / 60.0;
            long start = Stopwatch.GetTimestamp();
            engine.Update(t);
            return Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }

        double engineMs = BestMedian(EngineFrame, 10, 60);
        Assert.Equal(FixtureCount, engine.Changed.Count);
        long before = GC.GetAllocatedBytesForCurrentThread();
        EngineFrame(); // a whole frame: network input, merge, copy, parallel evaluation
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        int beams = engine.States.Sum(s => s.Value.Beams.Count), channels = engine.States.Sum(s => s.Value.ChannelCount);

        // the evaluator alone on one thread, for reference
        var states = engine.States.Select(kv => kv.Value).ToArray();
        var buffers = states.Select(s => new FixtureDmxBuffer(s.Profile)).ToArray();
        double SingleThreadFrame()
        {
            foreach (var b in buffers)
                for (int k = 0; k < b.BreakCount; k++) rng.NextBytes(b.GetBreak(k));
            t += 1.0 / 60.0;
            long start = Stopwatch.GetTimestamp();
            for (int i = 0; i < states.Length; i++) FixtureEvaluator.Evaluate(states[i], buffers[i], t, null, applyDynamics: true);
            return Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
        double singleMs = BestMedian(SingleThreadFrame, 5, 30);

        output.WriteLine($"{FixtureCount} fixtures ({beams} beams, {channels} channels, {universes} universes), every slot changing every frame, " +
            $"{(Optimized ? "optimised" : "debug")} build, {Environment.ProcessorCount} cores:");
        output.WriteLine($"  DmxEngine.Update (universe merge + copy + evaluation on {engine.MaxDegreeOfParallelism} threads): median {engineMs:F2} ms");
        output.WriteLine($"  FixtureEvaluator on one thread: median {singleMs:F2} ms ({singleMs * 1000.0 / FixtureCount:F2} µs per fixture)");

        // budget: 5 ms per frame for the optimised build; the unoptimised JIT is several times slower
        Assert.True(engineMs < (Optimized ? 5.0 : 30.0), $"engine frame {engineMs:F2} ms");

        before = GC.GetAllocatedBytesForCurrentThread();
        SingleThreadFrame();
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
