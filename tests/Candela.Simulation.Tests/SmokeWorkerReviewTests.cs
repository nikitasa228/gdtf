using System.Diagnostics;
using System.Numerics;
using Candela.Core.Math;
using Xunit.Abstractions;

namespace Candela.Simulation.Tests;

/// <summary>
/// Adversarial checks of the smoke worker: real time keeps up at a low frame rate when the solver has time to spare,
/// a frame the main thread holds never changes under it (no torn reads of the density handoff), a reconfigured
/// lockstep worker equals a fresh solver, and the adapted-cell note states the requested grid's cost.
/// </summary>
public sealed class SmokeWorkerReviewTests(ITestOutputHelper output)
{
    private static readonly SmokeEmitter[] Hazer = [SmokeEmitter.Hazer(new Vector3(-1.5f, -1.5f, 0.3f), Vector3.UnitZ, 1f)];

    private static readonly SmokeEmitter[] Emitters =
    [
        SmokeEmitter.FogMachine(new Vector3(-2f, 0f, 0.3f), new Vector3(1f, 0.2f, 0.1f), 0.8f),
        SmokeEmitter.Hazer(new Vector3(1.5f, 1.5f, 0.3f), Vector3.UnitZ, 1f),
        SmokeEmitter.Fan(new Vector3(-2.2f, -1.5f, 1f), new Vector3(1f, 0.5f, 0f), 0.7f),
    ];

    private static SmokeSimulationSettings ClosedBox(Vector3 size, float cellSize, int threads = -1) => new()
    {
        Bounds = new Aabb(new Vector3(-size.X / 2, -size.Y / 2, 0f), new Vector3(size.X / 2, size.Y / 2, size.Z)),
        CellSize = cellSize,
        OpenFaces = DomainFaces.None,
        DissipationPerSecond = 0f,
        TemperatureRelaxationRate = 0f,
        MaxDegreeOfParallelism = threads,
    };

    [Fact]
    public void Real_time_keeps_up_at_a_low_frame_rate_when_the_solver_has_time_to_spare()
    {
        // The editor's frame rate drops to 4 fps (a heavy viewport, a slow GPU): every frame carries 0.25 s, more
        // sub-steps than the synchronous per-call cap of 4 allows. The solver needs about a millisecond per sub-step
        // here, so it can simulate all of it — dropping time would only make the haze build up too slowly and show a
        // warning that blames the smoke.
        var settings = ClosedBox(new Vector3(4f, 4f, 3f), 0.25f);
        using var worker = new SmokeWorker(settings, new SmokeWorkerOptions
        {
            Timing = SmokeTiming.RealTime,
            AdaptCellSize = false,
            CostModel = new SmokeCostModel(),
        });
        worker.Synchronize();
        var clock = Stopwatch.StartNew();
        double last = 0, submitted = 0;
        for (int frame = 0; frame < 8; frame++)
        {
            Thread.Sleep(250);
            double now = clock.Elapsed.TotalSeconds;
            worker.Submit(now - last, Hazer);
            submitted += now - last;
            worker.AcquireLatest();
            last = now;
        }

        worker.Synchronize();
        var current = worker.Current;
        output.WriteLine($"submitted {submitted:F3} s in 8 frames: simulated {current.Time:F3} s, dropped {worker.DroppedTime:F3} s, " +
                         $"{current.MillisecondsPerStep:F2} ms per step");
        Assert.True(current.MillisecondsPerStep < 20, $"the test needs a cheap grid ({current.MillisecondsPerStep:F1} ms per step)");
        Assert.True(worker.DroppedTime < 0.05 * submitted, $"{worker.DroppedTime:F3} s of {submitted:F3} s dropped by a solver that had time to spare");
        Assert.False(worker.IsBehindRealTime);
        Assert.InRange(current.Time, submitted - 1.0 / 30 - 1e-9 - worker.DroppedTime, submitted + 1e-9);
        Assert.Equal(SmokeEmitter.DefaultHazerMassRate * current.Time, current.TotalSmokeMass, 1e-8);
    }

    [Fact]
    public void Real_time_still_bounds_the_latency_when_the_solver_is_slower_than_real_time()
    {
        // 1 ms sub-steps that cost far more than 1 ms: whatever frame time arrives, a call must not run for seconds
        // (the published smoke would trail the frames that drive it, and the merged frames would snowball).
        var settings = ClosedBox(new Vector3(10f, 10f, 5f), 0.25f) with { TimeStep = 0.001f };
        using var worker = new SmokeWorker(settings, new SmokeWorkerOptions
        {
            Timing = SmokeTiming.RealTime,
            AdaptCellSize = false,
            CostModel = new SmokeCostModel(),
        });
        worker.Synchronize();
        var clock = Stopwatch.StartNew();
        double last = 0, maxLag = 0;
        while (last < 2.0)
        {
            Thread.Sleep(200);
            double now = clock.Elapsed.TotalSeconds;
            worker.Submit(now - last, Hazer);
            worker.AcquireLatest();
            maxLag = Math.Max(maxLag, worker.Lag);
            last = now;
        }

        output.WriteLine($"max lag {maxLag:F3} s, dropped {worker.DroppedTime:F3} s, {worker.Current.MillisecondsPerStep:F1} ms per step");
        Assert.True(worker.Current.MillisecondsPerStep > 1, "the test needs a solver slower than real time");
        Assert.True(maxLag < 1.0, $"lag {maxLag:F3} s");
        Assert.True(worker.IsBehindRealTime);
    }

    [Fact]
    public void A_held_frame_never_changes_while_the_worker_publishes()
    {
        // Frames at about 200 Hz with their wall-clock time, so the worker publishes about once per sub-step; the main
        // thread takes each new frame, checks that its total mass matches its density (a torn copy would not), and
        // re-reads it after the worker has published more: nothing may have changed under it.
        var settings = ClosedBox(new Vector3(4f, 4f, 2f), 0.25f, threads: 2);
        using var worker = new SmokeWorker(settings, new SmokeWorkerOptions
        {
            Timing = SmokeTiming.RealTime,
            AdaptCellSize = false,
            CostModel = new SmokeCostModel(),
        });
        var buffer = new float[8192];
        int checkedFrames = 0;
        long lastVersion = -1;
        var clock = Stopwatch.StartNew();
        double last = 0;
        void SubmitWallTime()
        {
            Thread.Sleep(5);
            double now = clock.Elapsed.TotalSeconds;
            worker.Submit(now - last, Emitters);
            last = now;
        }

        while (clock.Elapsed.TotalSeconds < 4.0 && checkedFrames < 60)
        {
            SubmitWallTime();
            if (!worker.AcquireLatest())
                continue;

            var frame = worker.Current;
            Assert.True(frame.Version > lastVersion, "versions only grow");
            lastVersion = frame.Version;
            int cells = frame.CellCount;
            frame.CopyDensityTo(buffer);
            double time = frame.Time, mass = frame.TotalSmokeMass;
            long steps = frame.StepCount, version = frame.Version;
            double h = frame.CellSize, sum = 0;
            int slab = frame.SizeX * frame.SizeY;
            for (int k = 0; k < frame.SizeZ; k++)
            {
                double s = 0;
                for (int c = k * slab; c < (k + 1) * slab; c++)
                    s += buffer[c];
                sum += s;
            }

            Assert.Equal(mass, sum * h * h * h * frame.MassConcentrationAtFullDensity);

            // Let the worker publish a few more frames into its other buffers.
            for (int n = 0; n < 20; n++)
                SubmitWallTime();

            Assert.Equal(version, frame.Version);
            Assert.Equal(time, frame.Time);
            Assert.Equal(steps, frame.StepCount);
            Assert.Equal(mass, frame.TotalSmokeMass);
            Assert.True(frame.Density.SequenceEqual(buffer.AsSpan(0, cells)), "the held frame changed");
            checkedFrames++;
        }

        output.WriteLine($"{checkedFrames} frames checked, last version {lastVersion}");
        Assert.True(checkedFrames >= 20, $"only {checkedFrames} frames were checked");
    }

    [Fact]
    public void A_reconfigured_lockstep_worker_equals_a_fresh_solver_with_the_new_obstacles()
    {
        // The worker keeps its obstacles across a reconfiguration (the engine then sends the new ones): the result must
        // still be bit-identical to a fresh solver given only the new obstacles.
        var first = ClosedBox(new Vector3(6f, 6f, 3f), 0.2f);
        var second = ClosedBox(new Vector3(5f, 7f, 3f), 0.25f) with { OpenFaces = DomainFaces.ZMax };
        ObstacleBox[] oldBoxes = [ObstacleBox.FromAabb(new Aabb(new Vector3(-1f, -1f, 0f), new Vector3(1f, 1f, 2f)))];
        ObstacleBox[] newBoxes = [ObstacleBox.FromAabb(new Aabb(new Vector3(0.5f, -2f, 0f), new Vector3(1.5f, 2f, 1f)))];

        using var worker = new SmokeWorker(first);
        worker.SetObstacles(oldBoxes);
        for (int n = 0; n < 5; n++)
            worker.Submit(1.0 / 30, Emitters);
        worker.Reconfigure(second);
        worker.SetObstacles(newBoxes);

        var direct = new SmokeSimulation(second);
        direct.SetObstacles(newBoxes);
        for (int n = 0; n < 12; n++)
        {
            double dt = n % 3 == 0 ? 1.0 / 20 : 1.0 / 60;
            worker.Submit(dt, Emitters);
            direct.Step(dt, Emitters);
        }

        worker.Synchronize();
        Assert.True(worker.Current.Density.SequenceEqual(direct.Density), "density differs after a reconfiguration");
        Assert.Equal(direct.Time, worker.Current.Time);
        var a = direct.CaptureSnapshot();
        var b = worker.CaptureSnapshot();
        Assert.Equal(a.U, b.U);
        Assert.Equal(a.W, b.W);
        Assert.Equal(a.Solid, b.Solid);
        Assert.Equal(direct.Issues.Select(i => i.Code), worker.Issues.Select(i => i.Code));
    }

    [Fact]
    public void The_adapted_cell_note_states_the_cost_at_the_requested_size()
    {
        // A cost model of 1 µs per cell: the requested 6912 cells would take about 6.9 ms per step, over the 3.3 ms
        // budget, so the worker starts coarser; the note must quote the requested grid's cost, not the adapted one's.
        var costs = new SmokeCostModel();
        costs.Record(1, 1e-6);
        var settings = ClosedBox(new Vector3(6f, 6f, 3f), 0.25f, threads: 1);
        using var worker = new SmokeWorker(settings, new SmokeWorkerOptions
        {
            Timing = SmokeTiming.RealTime,
            RealTimeLoad = 0.1f,
            CostModel = costs,
        });
        worker.Synchronize();
        var note = Assert.Single(worker.Issues, i => i.Code == "simulation.cell-size-adapted");
        output.WriteLine(note.Message);
        Assert.True(worker.Current.CellSize > settings.CellSize);
        Assert.Contains("about 7 ms", note.Message, StringComparison.Ordinal);
    }
}
