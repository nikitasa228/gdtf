using System.Diagnostics;
using System.Numerics;
using Candela.Core.Geometry;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;
using Candela.Rendering;
using Candela.Simulation;
using Xunit.Abstractions;

namespace Candela.Engine.Tests;

/// <summary>
/// The smoke solver off the main thread (WP-07): with a hazer running on <c>Фокина.mvr</c> the engine tick stays
/// cheap and the simulated smoke keeps up with wall-clock time (or says it does not); offline runs stay bit-stable.
/// </summary>
public sealed class SmokeSolverThreadTests(ITestOutputHelper output)
{
    private const double Frame = 1.0 / 60;

    private static StageEngine CreateEngine(string name, bool realTime, int threads = 2) => new(new StageEngineOptions
    {
        Library = TestEnvironment.Library,
        CacheDirectory = TestEnvironment.NewTempDirectory(name + "-cache"),
        DmxParallelism = threads,
        SimulationParallelism = realTime ? -1 : threads,
        RealTimeSimulation = realTime,
    });

    /// <summary>A hazer at full output (and its fan) standing on the floor in the middle of the scene's content.</summary>
    private static Entity AddHazer(StageEngine engine)
    {
        var bounds = SceneBounds.Compute(engine.Scene);
        var at = bounds.IsEmpty ? new Vector3(0f, 0f, 0.3f) : new Vector3(bounds.Center.X, bounds.Center.Y, MathF.Max(bounds.Min.Z, 0f) + 0.3f);
        var hazer = SceneBuilder.Fixture(engine.Scene, SceneBuilder.EntourHaze, "4 Channel Mode", at);
        hazer.Get<FixtureComponent>()!.ManualAttributes = new Dictionary<string, float> { ["Haze1"] = 100f, ["Fan1"] = 100f };
        return hazer;
    }

    /// <summary>Ticks like the editor (wall-clock frame times, about 60 per second) and returns each tick's duration, ms.</summary>
    private static List<double> RunAtSixtyHertz(StageEngine engine, double seconds, ref double submitted)
    {
        var durations = new List<double>();
        var clock = Stopwatch.StartNew();
        double last = 0;
        while (last < seconds)
        {
            double next = last + Frame;
            int wait = (int)((next - clock.Elapsed.TotalSeconds) * 1000);
            if (wait > 0) Thread.Sleep(wait);
            double now = clock.Elapsed.TotalSeconds;
            long start = Stopwatch.GetTimestamp();
            engine.Tick(now - last);
            durations.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            submitted += now - last;
            last = now;
        }
        return durations;
    }

    private static double Percentile(List<double> values, double p)
    {
        var sorted = values.OrderBy(v => v).ToList();
        return sorted[Math.Clamp((int)Math.Round(p * (sorted.Count - 1)), 0, sorted.Count - 1)];
    }

    [Fact]
    public void A_hazer_on_Fokina_costs_the_main_thread_almost_nothing_and_the_smoke_keeps_up()
    {
        using var engine = CreateEngine("fokina-hazer", realTime: true);
        Assert.True(engine.Open(TestEnvironment.Fokina).Success);
        StageLooks.ApplyOpenWhite(engine, new OpenWhiteOptions { Undoable = false });
        var sync = engine.AttachRenderWorld(new NullRenderWorld(), loadFixtureModelsInBackground: false);
        engine.Tick(0);
        sync.CompletePendingLoads(TimeSpan.FromMinutes(2));

        double ignored = 0;
        RunAtSixtyHertz(engine, 0.5, ref ignored);
        var baseline = RunAtSixtyHertz(engine, 1.5, ref ignored);
        Assert.Null(engine.Simulation.Smoke);

        AddHazer(engine);
        double submitted = 0;
        engine.Tick(Frame);
        submitted += Frame;
        var smoke = engine.Simulation.Smoke;
        Assert.NotNull(smoke);
        Assert.Equal(SmokeTiming.RealTime, smoke!.Timing);
        RunAtSixtyHertz(engine, 1.0, ref submitted);
        var stepMs = new List<double>();
        var hazing = new List<double>();
        var clock = Stopwatch.StartNew();
        double windowSubmitted = 0, windowSimulated = 0;
        while (clock.Elapsed.TotalSeconds < 3.0)
        {
            if (windowSubmitted == 0 && clock.Elapsed.TotalSeconds >= 1.5)
                (windowSubmitted, windowSimulated) = (submitted, smoke.Current.Time);
            hazing.AddRange(RunAtSixtyHertz(engine, 0.25, ref submitted));
            stepMs.Add(engine.Simulation.LastStepMilliseconds);
        }

        var frame = smoke.Current;
        double baseMedian = Percentile(baseline, 0.5), median = Percentile(hazing, 0.5), p90 = Percentile(hazing, 0.9);
        output.WriteLine($"tick without smoke: median {baseMedian:F3} ms, p90 {Percentile(baseline, 0.9):F3} ms");
        output.WriteLine($"tick with the hazer: median {median:F3} ms, p90 {p90:F3} ms, max {hazing.Max():F1} ms; " +
                         $"simulation part {Percentile(stepMs, 0.5):F3} ms");
        output.WriteLine($"smoke: {frame.SizeX}×{frame.SizeY}×{frame.SizeZ} cells of {frame.CellSize:F3} m, {frame.MillisecondsPerStep:F1} ms per " +
                         $"step on {smoke.Parallelism} threads; submitted {submitted:F2} s, simulated {frame.Time:F2} s, dropped " +
                         $"{smoke.DroppedTime:F2} s ({smoke.DroppedFraction:P0} recently), behind: {engine.Simulation.IsSmokeBehindRealTime}");

        // The main thread hands the frame over and takes the newest grid: the tick stays under 2 ms — unless the tick
        // without smoke is already close to that (a Debug build on a loaded machine), when the smoke adds next to nothing.
        Assert.True(median < Math.Max(2.0, baseMedian + 0.5), $"median tick {median:F3} ms (without smoke {baseMedian:F3} ms)");
        Assert.True(Percentile(stepMs, 0.5) < 1.0, $"simulation part {Percentile(stepMs, 0.5):F3} ms");

        // Simulated time keeps up with wall-clock time over the last 1.5 s, or the user is told that it does not.
        double wall = submitted - windowSubmitted, simulated = frame.Time - windowSimulated;
        output.WriteLine($"last {wall:F2} s of frames: {simulated:F2} s simulated");
        bool keepsUp = simulated >= 0.9 * wall;
        Assert.True(keepsUp || engine.Simulation.IsSmokeBehindRealTime,
            $"{simulated:F3} s simulated in {wall:F3} s of frames without a warning");
        Assert.True(frame.Time > 1.0, $"only {frame.Time:F2} s of smoke simulated");

        // The haze reaches the renderer.
        Assert.True(frame.TotalSmokeMass > 0);
    }

    [Fact]
    public void Cli_resimulation_of_haze_is_bit_stable()
    {
        float[] Simulate(int threads)
        {
            using var engine = CreateEngine("cli-haze", realTime: false, threads);
            SceneBuilder.Floor(engine.Scene, 12f);
            AddHazer(engine);
            Cli.RenderCommand.Simulate(engine, 0, 2.0);
            var smoke = engine.Simulation.Smoke!;
            Assert.Equal(SmokeTiming.Lockstep, smoke.Timing);
            Assert.Equal(2.0, engine.Time, 9);
            var density = new float[smoke.Current.CellCount];
            smoke.Current.CopyDensityTo(density);
            return density;
        }

        var first = Simulate(2);
        var second = Simulate(2);
        var single = Simulate(1);
        Assert.Contains(first, d => d > 0.01f);
        Assert.Equal(first, second);
        Assert.Equal(first, single);
    }

    [Fact]
    public void Switching_real_time_off_simulates_every_frame_again_at_the_requested_cell_size()
    {
        using var engine = CreateEngine("switch", realTime: true);
        SceneBuilder.Floor(engine.Scene, 12f);
        AddHazer(engine);
        double submitted = 0;
        RunAtSixtyHertz(engine, 0.5, ref submitted);
        var smoke = engine.Simulation.Smoke!;
        Assert.True(engine.Simulation.RealTime);

        engine.Simulation.RealTime = false;
        Assert.Equal(SmokeTiming.Lockstep, smoke.Timing);
        Assert.Equal(engine.Settings.AutomaticAtmosphereCellSize, smoke.Current.CellSize, 4);
        double before = smoke.Current.Time;
        long steps = smoke.Current.StepCount;
        for (int i = 0; i < 30; i++) engine.Tick(1.0 / 30);
        Assert.Equal(steps + 30, smoke.Current.StepCount);
        Assert.Equal(before + 1.0, smoke.Current.Time, 6);
        Assert.False(engine.Simulation.IsSmokeBehindRealTime);

        engine.Simulation.RealTime = true;
        Assert.Equal(SmokeTiming.RealTime, smoke.Timing);
    }

    [Fact]
    public void Real_time_smoke_ticks_do_not_allocate_on_the_main_thread()
    {
        using var engine = CreateEngine("smoke-alloc", realTime: true, threads: 1);
        Assert.True(engine.Open(TestEnvironment.Fokina).Success);
        var sync = engine.AttachRenderWorld(new NullRenderWorld(), loadFixtureModelsInBackground: false);
        AddHazer(engine);
        engine.Tick(0);
        sync.CompletePendingLoads(TimeSpan.FromMinutes(2));
        for (int i = 0; i < 60; i++)
        {
            engine.Tick(Frame);
            Thread.Sleep(5);
        }

        // A window in which the solver adapted its grid reallocates the renderer's copy once; take another one then.
        long allocated = -1;
        for (int attempt = 0; attempt < 5 && allocated != 0; attempt++)
        {
            float cell = engine.Simulation.Smoke!.Current.CellSize;
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 30; i++)
            {
                engine.Tick(Frame);
                _ = engine.Simulation.IsSmokeBehindRealTime;
                Thread.Sleep(5);
            }
            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            if (engine.Simulation.Smoke!.Current.CellSize != cell) allocated = -1;
        }

        Assert.True(allocated == 0, $"{allocated} bytes allocated in 30 frames with real-time smoke");
        Assert.True(engine.Simulation.Smoke!.Current.Version > 0);
    }

    [Fact]
    public void Disposing_the_engine_stops_the_smoke_solver()
    {
        var engine = CreateEngine("dispose", realTime: true);
        SceneBuilder.Floor(engine.Scene, 12f);
        AddHazer(engine);
        engine.Tick(Frame);
        var smoke = engine.Simulation.Smoke!;
        engine.Dispose();
        Assert.Null(engine.Simulation.Smoke);
        Assert.Throws<ObjectDisposedException>(() => smoke.Submit(Frame, ReadOnlySpan<SmokeEmitter>.Empty));
    }

    /// <summary>A render world that accepts everything and keeps nothing.</summary>
    private sealed class NullRenderWorld : IRenderWorld
    {
        private int _next = 1;

        public MeshHandle CreateMesh(MeshData mesh) => new(_next++);
        public void DestroyMesh(MeshHandle mesh) { }
        public TextureHandle CreateTexture(ImageData image, bool srgb) => new(_next++);
        public void DestroyTexture(TextureHandle texture) { }
        public MaterialHandle CreateMaterial(MaterialData material, IImageSource? images) => new(_next++);
        public void UpdateMaterial(MaterialHandle handle, MaterialData material, IImageSource? images) { }
        public void DestroyMaterial(MaterialHandle material) { }
        public InstanceHandle CreateInstance(MeshHandle mesh, ReadOnlySpan<MaterialHandle> materials, Matrix4x4 world, InstanceFlags flags, uint pickId) => new(_next++);
        public void UpdateInstance(InstanceHandle instance, Matrix4x4 world, InstanceFlags flags) { }
        public void SetInstanceMaterials(InstanceHandle instance, ReadOnlySpan<MaterialHandle> materials) { }
        public void DestroyInstance(InstanceHandle instance) { }
        public void SetLights(ReadOnlySpan<SpotLight> lights) { }
        public void SetLines(ReadOnlySpan<LineVertex> segments) { }
        public void SetAtmosphere(AtmosphereState atmosphere) { }
        public void SetNoiseSources(ReadOnlySpan<NoiseSource> sources) { }
    }
}
