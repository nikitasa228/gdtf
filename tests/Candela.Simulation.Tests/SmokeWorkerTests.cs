using System.Diagnostics;
using System.Numerics;
using Candela.Core.Math;
using Xunit.Abstractions;

namespace Candela.Simulation.Tests;

/// <summary>
/// The smoke solver on its own thread: lockstep results bit-identical to the synchronous solver (conservation and
/// determinism unchanged), real time that keeps up with wall-clock time or reports what it drops, the time-budget cell
/// size and its adaptation with conserved smoke and heat, a main thread that never waits, and robustness.
/// </summary>
public sealed class SmokeWorkerTests(ITestOutputHelper output)
{
    private static readonly SmokeEmitter[] Emitters =
    [
        SmokeEmitter.FogMachine(new Vector3(-2f, 0f, 0.3f), new Vector3(1f, 0.2f, 0.1f), 0.8f),
        SmokeEmitter.Hazer(new Vector3(1.5f, 1.5f, 0.3f), Vector3.UnitZ, 1f),
        SmokeEmitter.Fan(new Vector3(-2.2f, -1.5f, 1f), new Vector3(1f, 0.5f, 0f), 0.7f),
    ];

    private static readonly ObstacleBox[] Riser = [ObstacleBox.FromAabb(new Aabb(new Vector3(0f, -0.6f, 0f), new Vector3(0.6f, 0.6f, 1f)))];

    /// <summary>A 6 × 6 × 3 m room with an open ceiling (30 × 30 × 15 cells: parallel slices).</summary>
    private static SmokeSimulationSettings Room(int threads = -1) => new()
    {
        Bounds = new Aabb(new Vector3(-3f, -3f, 0f), new Vector3(3f, 3f, 3f)),
        CellSize = 0.2f,
        OpenFaces = DomainFaces.ZMax,
        MaxDegreeOfParallelism = threads,
    };

    /// <summary>A closed box without dissipation or cooling: everything emitted stays.</summary>
    private static SmokeSimulationSettings ClosedBox(Vector3 size, float cellSize, int threads = -1) => new()
    {
        Bounds = new Aabb(new Vector3(-size.X / 2, -size.Y / 2, 0f), new Vector3(size.X / 2, size.Y / 2, size.Z)),
        CellSize = cellSize,
        OpenFaces = DomainFaces.None,
        DissipationPerSecond = 0f,
        TemperatureRelaxationRate = 0f,
        MaxDegreeOfParallelism = threads,
    };

    private static SmokeWorkerOptions RealTime(SmokeCostModel? costs = null) => new()
    {
        Timing = SmokeTiming.RealTime,
        CostModel = costs ?? new SmokeCostModel(),
    };

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void Lockstep_results_are_bit_identical_to_the_synchronous_solver(int threads)
    {
        var settings = Room(threads);
        var direct = new SmokeSimulation(settings);
        direct.SetObstacles(Riser);
        using var worker = new SmokeWorker(settings);
        Assert.Equal(SmokeTiming.Lockstep, worker.Timing);
        worker.SetObstacles(Riser);

        // Frame times of a jittery editor, plus a stall that exceeds the sub-step cap (time is dropped the same way).
        double[] times = [1.0 / 60, 1.0 / 45, 0.0, 1.0 / 30, 0.25, 1.0 / 60, 1.0 / 90];
        double dropped = 0;
        for (int n = 0; n < 40; n++)
        {
            double dt = times[n % times.Length];
            direct.Step(dt, Emitters);
            dropped += direct.LastStatistics.DroppedTime;
            worker.Submit(dt, Emitters);
        }

        // The last frame runs a sub-step, so the published statistics are those of the same call.
        direct.Step(1.0 / 30, Emitters);
        worker.Submit(1.0 / 30, Emitters);
        worker.Synchronize();
        var frame = worker.Current;

        Assert.True(frame.Density.SequenceEqual(direct.Density), "density differs");
        Assert.Equal(direct.Time, frame.Time);
        Assert.Equal(direct.StepCount, frame.StepCount);
        Assert.Equal(direct.TotalSmokeMass(), frame.TotalSmokeMass);
        Assert.Equal(direct.LastStatistics, frame.Statistics);
        Assert.Equal(dropped, worker.DroppedTime, 12);
        Assert.True(dropped > 0, "the stall should have exceeded the sub-step cap");
        Assert.Equal(direct.CellSize, frame.CellSize);
        Assert.Equal(direct.Bounds.Min, frame.Bounds.Min);

        var a = direct.CaptureSnapshot();
        var b = worker.CaptureSnapshot();
        Assert.Equal(a.U, b.U);
        Assert.Equal(a.V, b.V);
        Assert.Equal(a.W, b.W);
        Assert.Equal(a.Temperature, b.Temperature);
        Assert.Equal(a.Pressure, b.Pressure);
        Assert.Equal(a.Solid, b.Solid);
        Assert.False(worker.IsBehindRealTime, "lockstep is never 'behind real time'");
        Assert.Equal(0.0, worker.DroppedFraction);
    }

    [Fact]
    public void Closed_room_probe_holds_the_hazers_output_after_40_s()
    {
        // The audit's probe: a hazer at full output in a closed room without dissipation, 40 s at 60 frames per second.
        var settings = ClosedBox(new Vector3(6f, 6f, 3f), 0.25f);
        var hazer = new[] { SmokeEmitter.Hazer(new Vector3(-2.5f, -2.5f, 0.3f), new Vector3(1f, 1f, 0.3f), 1f) };
        using var worker = new SmokeWorker(settings);
        var direct = new SmokeSimulation(settings);
        for (int frame = 0; frame < 2400; frame++)
        {
            worker.Submit(1.0 / 60, hazer);
            direct.Step(1.0 / 60, hazer);
        }

        worker.Synchronize();
        double grams = worker.Current.TotalSmokeMass * 1000;
        double time = worker.Current.Time;
        output.WriteLine($"after {time:F4} s ({worker.Current.StepCount} steps): {grams:F4} g " +
                         $"(hazer: {SmokeEmitter.DefaultHazerMassRate * 1000 * time:F4} g in that time)");
        // 2400 frames of 1/60 s sum to a hair under 40 s: the 1200th sub-step is still carried to the next frame.
        Assert.InRange(time, 40.0 - 1.0 / 30 - 1e-9, 40.0);
        Assert.Equal(0.0, worker.DroppedTime);
        Assert.Equal(1.599, grams, 3);
        Assert.Equal(SmokeEmitter.DefaultHazerMassRate * 1000 * time, grams, 1e-4);
        Assert.Equal(direct.TotalSmokeMass(), worker.Current.TotalSmokeMass);
        Assert.True(worker.Current.Density.SequenceEqual(direct.Density));
    }

    [Fact]
    public void Commands_apply_in_order_with_the_frames()
    {
        var settings = Room();
        var direct = new SmokeSimulation(settings);
        using var worker = new SmokeWorker(settings);

        void Both(Action<SmokeSimulation> onDirect, Action<SmokeWorker> onWorker)
        {
            onDirect(direct);
            onWorker(worker);
        }

        Both(s => s.Fill(0.2f), w => w.Fill(0.2f));
        Both(s => s.Step(0.1, Emitters), w => w.Submit(0.1, Emitters));
        Both(s => s.SetObstacles(Riser), w => w.SetObstacles(Riser));
        Both(s => s.AddSmoke(new Vector3(1f, -1f, 1.5f), 0.8f, 2f, 40f), w => w.AddSmoke(new Vector3(1f, -1f, 1.5f), 0.8f, 2f, 40f));
        Both(s => s.Step(0.2, Emitters), w => w.Submit(0.2, Emitters));
        worker.Synchronize();
        Assert.True(worker.Current.Density.SequenceEqual(direct.Density));
        Assert.True(worker.Current.IsSolid(15, 14, 2));

        // The published frame samples like the simulation (solid corners skipped), bit for bit.
        var random = new Random(7);
        for (int n = 0; n < 500; n++)
        {
            var p = new Vector3((float)random.NextDouble() * 7f - 3.5f, (float)random.NextDouble() * 7f - 3.5f, (float)random.NextDouble() * 3.4f - 0.2f);
            Assert.Equal(direct.DensityAt(p), worker.Current.DensityAt(p));
        }

        Both(s => s.Reset(), w => w.Reset());
        Both(s => s.Step(0.1, Emitters), w => w.Submit(0.1, Emitters));
        worker.Synchronize();
        Assert.True(worker.Current.Density.SequenceEqual(direct.Density));
        Assert.Equal(direct.Time, worker.Current.Time);
    }

    [Fact]
    public void Snapshots_restore_through_the_worker()
    {
        using var worker = new SmokeWorker(Room());
        worker.SetObstacles(Riser);
        for (int n = 0; n < 10; n++)
            worker.Submit(1.0 / 30, Emitters);
        var snapshot = worker.CaptureSnapshot();
        for (int n = 0; n < 10; n++)
            worker.Submit(1.0 / 30, Emitters);
        worker.Synchronize();
        var first = worker.Current.Density.ToArray();
        double time = worker.Current.Time;

        worker.RestoreSnapshot(snapshot);
        Assert.Equal(snapshot.Time, worker.Current.Time);
        for (int n = 0; n < 10; n++)
            worker.Submit(1.0 / 30, Emitters);
        worker.Synchronize();
        Assert.Equal(time, worker.Current.Time);
        Assert.True(worker.Current.Density.SequenceEqual(first), "re-simulating from the snapshot differs");
    }

    [Fact]
    public void Main_thread_calls_return_at_once_while_the_solver_works()
    {
        // A grid far too big for real time (and no adaptation): every step takes tens of milliseconds.
        var settings = ClosedBox(new Vector3(16f, 16f, 8f), 0.25f);
        using var worker = new SmokeWorker(settings, RealTime() with { AdaptCellSize = false });
        worker.Synchronize();
        var calls = new List<double>();
        for (int n = 0; n < 300; n++)
        {
            long t0 = Stopwatch.GetTimestamp();
            worker.Submit(1.0 / 60, Emitters);
            worker.AcquireLatest();
            _ = worker.IsBehindRealTime;
            calls.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
            Thread.SpinWait(2000);
        }

        calls.Sort();
        output.WriteLine($"Submit + AcquireLatest: median {calls[150]:F4} ms, p95 {calls[285]:F4} ms, max {calls[^1]:F3} ms; " +
                         $"solver {worker.Current.MillisecondsPerStep:F1} ms per step");
        Assert.True(calls[150] < 0.05, $"median {calls[150]:F4} ms");
        Assert.True(calls[285] < 1.0, $"95th percentile {calls[285]:F4} ms");
    }

    [Fact]
    public void Real_time_keeps_up_with_wall_clock_time_when_the_grid_fits_the_budget()
    {
        var settings = ClosedBox(new Vector3(4f, 4f, 3f), 0.25f);
        var hazer = new[] { SmokeEmitter.Hazer(new Vector3(-1.5f, -1.5f, 0.3f), Vector3.UnitZ, 1f) };
        using var worker = new SmokeWorker(settings, RealTime());
        var clock = Stopwatch.StartNew();
        double last = 0;
        while (last < 1.5)
        {
            Thread.Sleep(15);
            double now = clock.Elapsed.TotalSeconds;
            worker.Submit(now - last, hazer);
            worker.AcquireLatest();
            last = now;
        }

        worker.Synchronize();
        var frame = worker.Current;
        output.WriteLine($"wall {last:F3} s, simulated {frame.Time:F3} s, dropped {worker.DroppedTime:F3} s, " +
                         $"{frame.MillisecondsPerStep:F2} ms per step at {frame.CellSize} m");
        Assert.Equal(settings.CellSize, frame.CellSize);
        Assert.True(worker.DroppedTime <= 0.1 * last, $"dropped {worker.DroppedTime:F3} s of {last:F3} s");
        // Simulated + dropped time is the wall time up to the remainder carried to the next step.
        Assert.InRange(frame.Time + worker.DroppedTime, last - 1.0 / 30 - 1e-9, last + 1e-9);
        Assert.True(worker.Lag < 1.0 / 30 + 1e-9);
        if (worker.DroppedTime == 0)
            Assert.False(worker.IsBehindRealTime);
        // Everything simulated was emitted at the hazer's rate.
        Assert.Equal(SmokeEmitter.DefaultHazerMassRate * frame.Time, frame.TotalSmokeMass, 1e-8);
    }

    [Fact]
    public void Real_time_drops_what_it_cannot_simulate_and_says_so()
    {
        // 1 ms sub-steps on 40 × 40 × 20 cells: no machine keeps up, and adaptation is off.
        var settings = ClosedBox(new Vector3(10f, 10f, 5f), 0.25f) with { TimeStep = 0.001f };
        var hazer = new[] { SmokeEmitter.Hazer(new Vector3(-1.5f, -1.5f, 0.3f), Vector3.UnitZ, 1f) };
        using var worker = new SmokeWorker(settings, RealTime() with { AdaptCellSize = false });
        worker.Synchronize();
        var clock = Stopwatch.StartNew();
        double last = 0;
        double maxLag = 0;
        while (last < 1.5)
        {
            Thread.Sleep(15);
            double now = clock.Elapsed.TotalSeconds;
            worker.Submit(now - last, hazer);
            worker.AcquireLatest();
            maxLag = Math.Max(maxLag, worker.Lag);
            last = now;
        }

        var frame = worker.Current;
        output.WriteLine($"wall {last:F3} s, simulated {frame.Time:F3} s, dropped {worker.DroppedTime:F3} s " +
                         $"({worker.DroppedFraction:P0} recently), lag ≤ {maxLag * 1000:F0} ms, {frame.MillisecondsPerStep:F1} ms per step");
        Assert.True(worker.DroppedTime > 0.3, $"dropped {worker.DroppedTime:F3} s");
        Assert.True(worker.IsBehindRealTime);
        Assert.True(worker.DroppedFraction > 0.5, $"dropped share {worker.DroppedFraction:P0}");
        Assert.Equal(settings.CellSize, frame.CellSize);
        // The latency cap keeps the published smoke close to the frames that drive it.
        Assert.True(maxLag < 0.5, $"lag {maxLag:F3} s");
        Assert.Equal(SmokeEmitter.DefaultHazerMassRate * frame.Time, frame.TotalSmokeMass, 1e-8);

        // Stopping the frames lets the warning fade (it averages over the last seconds).
        worker.Synchronize();
        Assert.True(worker.DroppedFraction > 0);
    }

    [Fact]
    public void Real_time_starts_with_a_cell_size_chosen_from_the_time_budget()
    {
        // 108 000 cells against a budget of 1.7 ms per step: far coarser than requested before the first step.
        var settings = Room() with { Bounds = new Aabb(new Vector3(-6f, -6f, 0f), new Vector3(6f, 6f, 6f)) };
        using var worker = new SmokeWorker(settings, RealTime() with { RealTimeLoad = 0.05f });
        worker.Synchronize();
        var frame = worker.Current;
        output.WriteLine($"{frame.SizeX} × {frame.SizeY} × {frame.SizeZ} cells of {frame.CellSize:F3} m");
        Assert.True(frame.CellSize > 1.5f * settings.CellSize);
        Assert.True(frame.CellCount < 108_000 / 8);
        Assert.Contains(worker.Issues, i => i.Code == "simulation.cell-size-adapted");

        // A lockstep worker ignores the budget.
        using var lockstep = new SmokeWorker(settings);
        lockstep.Synchronize();
        Assert.Equal(settings.CellSize, lockstep.Current.CellSize);
        Assert.DoesNotContain(lockstep.Issues, i => i.Code == "simulation.cell-size-adapted");
    }

    [Fact]
    public void Real_time_coarsens_a_grid_that_is_too_slow_and_keeps_the_smoke()
    {
        // The cost model believes the solver is free, so the worker starts at the requested 64 × 64 × 32 cells; the
        // measured steps then exceed the 3.3 ms budget on any machine.
        var costs = new SmokeCostModel();
        costs.Record(2, 1e-12);
        var settings = ClosedBox(new Vector3(16f, 16f, 8f), 0.25f, threads: 2);
        var hazer = new[] { SmokeEmitter.Hazer(new Vector3(-1.5f, -1.5f, 0.3f), Vector3.UnitZ, 1f) };
        using var worker = new SmokeWorker(settings, RealTime(costs) with { RealTimeLoad = 0.1f, CoarsenInterval = 0.2 });
        worker.Synchronize();
        Assert.Equal(settings.CellSize, worker.Current.CellSize);

        var clock = Stopwatch.StartNew();
        double last = 0;
        while (worker.Current.CellSize == settings.CellSize && last < 20)
        {
            Thread.Sleep(15);
            double now = clock.Elapsed.TotalSeconds;
            worker.Submit(now - last, hazer);
            worker.AcquireLatest();
            last = now;
        }

        worker.Synchronize();
        var frame = worker.Current;
        output.WriteLine($"after {last:F2} s: {frame.CellSize:F3} m cells ({frame.CellCount}), {frame.MillisecondsPerStep:F2} ms per step, " +
                         $"simulated {frame.Time:F2} s, dropped {worker.DroppedTime:F2} s");
        Assert.True(frame.CellSize > settings.CellSize, "the grid was never coarsened");
        Assert.Contains(worker.Issues, i => i.Code == "simulation.cell-size-adapted");
        // Mass carried to the new grid: what the hazer emitted while simulated (no dissipation in a closed box).
        Assert.Equal(SmokeEmitter.DefaultHazerMassRate * frame.Time, frame.TotalSmokeMass, 1e-7);
        Assert.True(frame.TotalSmokeMass > 0);

        // Back to lockstep: the requested resolution again, the smoke moves along.
        double mass = frame.TotalSmokeMass;
        worker.Timing = SmokeTiming.Lockstep;
        worker.Synchronize();
        Assert.Equal(settings.CellSize, worker.Current.CellSize);
        Assert.Equal(mass, worker.Current.TotalSmokeMass, 1e-9);
        Assert.DoesNotContain(worker.Issues, i => i.Code == "simulation.cell-size-adapted");
    }

    [Fact]
    public void Real_time_refines_again_when_the_steps_are_cheap()
    {
        // A pessimistic cost model starts the worker at its coarsest level; the measured cost then allows refining.
        var costs = new SmokeCostModel();
        costs.Record(2, 1e-3);
        var settings = ClosedBox(new Vector3(8f, 8f, 4f), 0.25f, threads: 2);
        using var worker = new SmokeWorker(settings, RealTime(costs) with { RefineInterval = 0.1 });
        worker.Synchronize();
        float coarsest = worker.Current.CellSize;
        Assert.True(coarsest > settings.CellSize);

        var clock = Stopwatch.StartNew();
        double last = 0;
        while (worker.Current.CellSize != settings.CellSize && last < 30)
        {
            Thread.Sleep(10);
            double now = clock.Elapsed.TotalSeconds;
            worker.Submit(now - last, Emitters);
            worker.AcquireLatest();
            last = now;
        }

        output.WriteLine($"{coarsest:F3} m → {worker.Current.CellSize:F3} m in {last:F2} s");
        Assert.Equal(settings.CellSize, worker.Current.CellSize);
        Assert.DoesNotContain(worker.Issues, i => i.Code == "simulation.cell-size-adapted");
    }

    [Theory]
    [InlineData(0.2f, 0.252f)]
    [InlineData(0.252f, 0.2f)]
    public void Moving_to_another_grid_conserves_smoke_and_heat(float from, float to)
    {
        var settings = ClosedBox(new Vector3(6f, 6f, 3f), from) with { TemperatureRelaxationRate = 0.05f };
        var source = new SmokeSimulation(settings);
        source.SetObstacles(Riser);
        var plume = SmokeEmitter.Plume(new Vector3(-1.5f, 1f, 0.3f), Vector3.UnitZ, 0.3f, 2f, 2e-4f, 150f);
        for (int n = 0; n < 40; n++)
            source.StepOnce([.. Emitters, plume]);

        var target = new SmokeSimulation(settings with { CellSize = to });
        target.SetObstacles(Riser);
        target.TransferStateFrom(source);

        double heat(SmokeSimulation s) => s.TemperatureExcess.ToArray().Sum(t => (double)t) * Math.Pow(s.CellSize, 3);
        output.WriteLine($"{source.SizeX}×{source.SizeY}×{source.SizeZ} → {target.SizeX}×{target.SizeY}×{target.SizeZ}: mass " +
                         $"{source.TotalSmokeMass() * 1e3:F5} → {target.TotalSmokeMass() * 1e3:F5} g, heat {heat(source):F3} → {heat(target):F3} K·m³, " +
                         $"divergence {target.MaxDivergence():E2} 1/s");
        Assert.Equal(source.TotalSmokeMass(), target.TotalSmokeMass(), source.TotalSmokeMass() * 1e-5);
        Assert.Equal(heat(source), heat(target), Math.Abs(heat(source)) * 1e-3);
        Assert.Equal(source.Time, target.Time);
        Assert.Equal(source.StepCount, target.StepCount);
        Assert.True(target.MaxDivergence() < 1e-2, $"divergence {target.MaxDivergence()}");
        Assert.True(target.Density.ToArray().Min() >= 0f);

        // The smoke is where it was (smooth fields: the grids agree to a fraction of the local density).
        var centre = source.SmokeCentreOfMass();
        Assert.True(Vector3.Distance(centre, target.SmokeCentreOfMass()) < 0.5f * Math.Max(from, to));
        // Velocity carried over at the fan.
        var probe = new Vector3(-1.4f, -1.2f, 1f);
        Assert.True(Vector3.Distance(source.VelocityAt(probe), target.VelocityAt(probe)) < 0.5f * source.VelocityAt(probe).Length() + 0.05f);

        // And it simply goes on.
        for (int n = 0; n < 10; n++)
            target.StepOnce(Emitters);
        Assert.True(double.IsFinite(target.TotalSmokeMass()) && target.TotalSmokeMass() > 0);
    }

    [Fact]
    public void A_failing_solver_is_reported_and_restarted_not_thrown()
    {
        using var worker = new SmokeWorker(Room());
        worker.Submit(0.1, Emitters);
        worker.FailForTesting();
        worker.Synchronize();
        Assert.IsType<InvalidOperationException>(worker.Fault);
        Assert.Contains(worker.Issues, i => i.Code == "simulation.smoke-failed");
        worker.Submit(0.5, Emitters);
        worker.Synchronize();
        Assert.Throws<InvalidOperationException>(() => worker.CaptureSnapshot());

        worker.Reconfigure(Room());
        worker.Submit(0.1, Emitters);
        worker.Synchronize();
        Assert.Null(worker.Fault);
        Assert.DoesNotContain(worker.Issues, i => i.Code == "simulation.smoke-failed");
        Assert.True(worker.Current.Time > 0);
        Assert.True(worker.Current.Version > 0);
    }

    [Fact]
    public void Invalid_settings_are_reported_like_the_simulation_reports_them()
    {
        var settings = Room() with { CellSize = float.NaN, TimeStep = -1f };
        using var worker = new SmokeWorker(settings);
        Assert.Contains(worker.Issues, i => i.Code == "simulation.invalid-setting");
        worker.Synchronize();
        var direct = new SmokeSimulation(settings);
        Assert.Equal(direct.Issues.Select(i => i.Message), worker.Issues.Select(i => i.Message));
        Assert.Equal(direct.CellSize, worker.Current.CellSize);
    }

    [Fact]
    public void Steady_frames_do_not_allocate_on_the_calling_thread()
    {
        using var realTime = new SmokeWorker(Room(), RealTime());
        using var lockstep = new SmokeWorker(Room());
        for (int n = 0; n < 20; n++)
        {
            realTime.Submit(1.0 / 60, Emitters);
            realTime.AcquireLatest();
            lockstep.Submit(1.0 / 60, Emitters);
            lockstep.Synchronize();
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int n = 0; n < 60; n++)
        {
            realTime.Submit(1.0 / 60, Emitters);
            realTime.AcquireLatest();
            _ = realTime.IsBehindRealTime;
            lockstep.Submit(1.0 / 60, Emitters);
            lockstep.Synchronize();
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated == 0, $"{allocated} bytes allocated in 60 frames");
    }

    [Fact]
    public void A_disposed_worker_refuses_work()
    {
        var worker = new SmokeWorker(Room());
        worker.Submit(0.1, Emitters);
        worker.Dispose();
        worker.Dispose();
        Assert.Throws<ObjectDisposedException>(() => worker.Submit(0.1, Emitters));
        Assert.Throws<ObjectDisposedException>(() => worker.Synchronize());
    }
}
