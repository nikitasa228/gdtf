using System.Numerics;
using Candela.Core.Math;

namespace Candela.Simulation.Tests;

/// <summary>Determinism, reset, snapshots, time stepping and robustness against bad input.</summary>
public sealed class SmokeStateTests
{
    private static readonly SmokeEmitter[] Emitters =
    [
        SmokeEmitter.FogMachine(new Vector3(-1.5f, 0f, 0.3f), new Vector3(1f, 0.2f, 0.1f), 0.8f),
        SmokeEmitter.Hazer(new Vector3(1.5f, 1.5f, 0.3f), Vector3.UnitZ, 1f),
        SmokeEmitter.Fan(new Vector3(-1.8f, -1.5f, 1f), new Vector3(1f, 0.5f, 0f), 0.7f),
    ];

    private static SmokeSimulation Scene(int threads = -1)
    {
        var sim = new SmokeSimulation(new SmokeSimulationSettings
        {
            Bounds = new Aabb(new Vector3(-2f, -2f, 0f), new Vector3(2f, 2f, 3f)),
            CellSize = 0.125f,
            OpenFaces = DomainFaces.AllButFloor,
            MaxDegreeOfParallelism = threads,
        });
        sim.SetObstacles([ObstacleBox.FromAabb(new Aabb(new Vector3(0f, -0.5f, 0f), new Vector3(0.5f, 0.5f, 1f)))]);
        return sim;
    }

    private static void AssertSameState(SmokeSimulation a, SmokeSimulation b)
    {
        Assert.True(a.Density.SequenceEqual(b.Density), "density differs");
        Assert.True(a.TemperatureExcess.SequenceEqual(b.TemperatureExcess), "temperature differs");
        Assert.Equal(a.Time, b.Time);
        Assert.Equal(a.StepCount, b.StepCount);
        var probe = new Vector3(0.3f, 0.7f, 1.3f);
        Assert.Equal(a.VelocityAt(probe), b.VelocityAt(probe));
    }

    [Fact]
    public void SameInputsAndTimeSteps_GiveIdenticalResults()
    {
        var a = Scene();
        var b = Scene();
        double[] frames = [0.016, 0.033, 0.05, 0.02, 0.1, 0.0167, 0.04];
        for (int n = 0; n < 4; n++)
        {
            foreach (double dt in frames)
            {
                a.Step(dt, Emitters);
                b.Step(dt, Emitters);
            }
        }

        AssertSameState(a, b);
        Assert.True(a.TotalSmokeMass() > 0);
    }

    [Fact]
    public void Results_DoNotDependOnTheThreadCount()
    {
        var parallel = Scene(-1);
        var serial = Scene(1);
        var two = Scene(2);
        for (int n = 0; n < 20; n++)
        {
            parallel.StepOnce(Emitters);
            serial.StepOnce(Emitters);
            two.StepOnce(Emitters);
        }

        AssertSameState(parallel, serial);
        AssertSameState(parallel, two);
    }

    [Fact]
    public void Reset_ReturnsToTheInitialStateAndReproducesTheRun()
    {
        var a = Scene();
        SmokeTestScenes.Run(a, 10, Emitters);
        a.Reset();
        Assert.Equal(0, a.Time);
        Assert.Equal(0, a.StepCount);
        Assert.Equal(0, a.TotalSmokeMass());
        Assert.Equal(0f, a.MaxDivergence());
        Assert.Equal(1, a.ObstacleCount); // obstacles survive a reset
        SmokeTestScenes.Run(a, 10, Emitters);

        var b = Scene();
        SmokeTestScenes.Run(b, 10, Emitters);
        AssertSameState(a, b);
    }

    [Fact]
    public void Snapshot_RestoresTheFullStateForScrubbing()
    {
        var sim = Scene();
        SmokeTestScenes.Run(sim, 12, Emitters);
        sim.Step(0.02, Emitters); // leaves a partial sub-step in the accumulator
        var snapshot = sim.CaptureSnapshot();
        Assert.Equal((sim.SizeX, sim.SizeY, sim.SizeZ), snapshot.Dimensions);
        Assert.Equal(sim.Time, snapshot.Time);

        for (int n = 0; n < 8; n++)
            sim.Step(0.025, Emitters);
        var reference = sim.Density.ToArray();
        double referenceTime = sim.Time;

        // Scrub back: change the world in between (obstacles removed, more smoke), then restore.
        sim.SetObstacles([]);
        sim.AddSmoke(new Vector3(0f, 0f, 1f), 1f, 3f, 50f);
        sim.RestoreSnapshot(snapshot);
        Assert.Equal(snapshot.Time, sim.Time);
        Assert.True(sim.IsSolid(17, 16, 0), "the snapshot brings the obstacle back");
        for (int n = 0; n < 8; n++)
            sim.Step(0.025, Emitters);

        Assert.Equal(referenceTime, sim.Time);
        Assert.True(sim.Density.SequenceEqual(reference));

        // Re-capturing into the same snapshot object does not allocate new arrays and still round-trips.
        sim.CaptureSnapshot(snapshot);
        var copy = Scene();
        copy.RestoreSnapshot(snapshot);
        AssertSameState(sim, copy);
    }

    [Fact]
    public void Snapshot_FromADifferentGrid_IsRejected()
    {
        var small = new SmokeSimulation(new SmokeSimulationSettings { Bounds = new Aabb(Vector3.Zero, Vector3.One), CellSize = 0.25f });
        var snapshot = Scene().CaptureSnapshot();
        Assert.Throws<ArgumentException>(() => small.RestoreSnapshot(snapshot));
        Assert.Throws<ArgumentNullException>(() => small.RestoreSnapshot(null!));
    }

    [Fact]
    public void Step_RunsFixedSubStepsAndCarriesTheRemainder()
    {
        var sim = Scene();
        Assert.Equal(0, sim.Step(0.02, []));        // 20 ms < one 33.3 ms sub-step
        Assert.Equal(1, sim.Step(0.02, []));        // 40 ms accumulated
        Assert.Equal(sim.TimeStep, sim.Time);
        int total = 0;
        for (int n = 0; n < 30; n++)
            total += sim.Step(1.0 / 30.0, []);
        Assert.Equal(30, total);

        // A hitch is capped at MaxSubStepsPerCall and the rest is dropped, not accumulated.
        int steps = sim.Step(1.0, []);
        Assert.Equal(sim.Settings.MaxSubStepsPerCall, steps);
        Assert.True(sim.LastStatistics.DroppedTime > 0.8);
        Assert.Equal(0, sim.Step(double.NaN, []));
        Assert.Equal(0, sim.Step(-1, []));
        Assert.Equal(0, sim.Step(double.PositiveInfinity, []));
    }

    [Fact]
    public void InvalidSettings_AreReplacedAndReported()
    {
        var sim = new SmokeSimulation(new SmokeSimulationSettings
        {
            Bounds = new Aabb(new Vector3(float.NaN), new Vector3(1f)),
            CellSize = -1f,
            TimeStep = float.NaN,
            DissipationPerSecond = float.PositiveInfinity,
            MaxPressureIterations = 0,
            Airflow = new Vector3(float.NaN, 0f, 0f),
            ScalarAdvection = (ScalarAdvectionScheme)42,
        });
        Assert.True(sim.Issues.Count >= 5);
        Assert.All(sim.Issues, i => Assert.Equal("Scene", i.Category));
        Assert.True(sim.CellCount > 0);
        Assert.True(sim.TimeStep > 0);
        sim.AddSmoke(sim.Bounds.Center, 2f, 1f, 20f);
        SmokeTestScenes.Run(sim, 3, Emitters);
        Assert.True(double.IsFinite(sim.TotalSmokeMass()));
    }

    [Fact]
    public void LargeDomains_AreCoarsenedToTheCellBudget()
    {
        var sim = new SmokeSimulation(new SmokeSimulationSettings
        {
            Bounds = new Aabb(new Vector3(-50f, -50f, 0f), new Vector3(50f, 50f, 20f)),
            CellSize = 0.05f,
            MaxCellCount = 64 * 64 * 32,
            MaxCellsPerAxis = 64,
        });
        Assert.True(sim.CellCount <= 64 * 64 * 32);
        Assert.True(Math.Max(sim.SizeX, Math.Max(sim.SizeY, sim.SizeZ)) <= 64);
        Assert.True(sim.CellSize >= 100f / 64f);
        Assert.Contains(sim.Issues, i => i.Code == "simulation.cell-size-increased");
        // The grid still covers the requested box.
        Assert.True(sim.Bounds.Min.X <= -50f && sim.Bounds.Max.Z >= 20f);
    }

    [Fact]
    public void BadEmittersAndObstacles_AreSkippedNotFatal()
    {
        var sim = Scene();
        var bad = new[]
        {
            SmokeEmitter.FogMachine(new Vector3(float.NaN, 0f, 1f), Vector3.UnitX, 1f),
            SmokeEmitter.Hazer(new Vector3(100f, 0f, 1f), Vector3.UnitX, 1f),              // outside the box
            SmokeEmitter.Hazer(new Vector3(0.25f, 0f, 0.5f), new Vector3(0f, 0f, 0f), 1f) with { NozzleDiameter = 0.01f }, // inside the riser
            new SmokeEmitter { Kind = (SmokeEmitterKind)99, Position = Vector3.Zero, Output = 1f },
            SmokeEmitter.Hazer(new Vector3(-1f, 0f, 1f), new Vector3(float.NaN), float.NaN), // NaN output → off
            SmokeEmitter.FogMachine(new Vector3(-1f, 1f, 1f), Vector3.UnitX, 1f, heaterPowerW: float.NaN) with { MassRate = float.PositiveInfinity },
            SmokeEmitter.Fan(new Vector3(-1f, -1f, 1f), Vector3.UnitY, 1f, diameter: -1f, maxExitSpeed: 1e9f),
        };
        sim.StepOnce(bad);
        Assert.Equal(4, sim.LastStatistics.IgnoredEmitters);
        Assert.All(sim.Density.ToArray(), d => Assert.True(float.IsFinite(d)));
        Assert.True(float.IsFinite(sim.MaxDivergence()));

        sim.SetObstacles([
            new ObstacleBox(new Vector3(float.NaN), Vector3.One, Quaternion.Identity),
            new ObstacleBox(Vector3.Zero, new Vector3(-1f), Quaternion.Identity),
            new ObstacleBox(Vector3.Zero, Vector3.One, default),
            ObstacleBox.FromAabb(new Aabb(new Vector3(50f), new Vector3(51f))),
            ObstacleBox.FromAabb(new Aabb(new Vector3(-0.5f), new Vector3(0.5f))),
        ]);
        Assert.Equal(1, sim.ObstacleCount);
        Assert.Contains(sim.Issues, i => i.Code == "simulation.obstacle-invalid");
        Assert.Contains(sim.Issues, i => i.Code == "simulation.obstacle-outside");
        sim.StepOnce(Emitters);

        // A later, clean obstacle list clears the obstacle issues again.
        sim.SetObstacles([]);
        Assert.DoesNotContain(sim.Issues, i => i.Code.StartsWith("simulation.obstacle", StringComparison.Ordinal));
    }

    [Fact]
    public void ObstacleFillingTheWholeBox_LeavesAValidEmptySimulation()
    {
        var sim = Scene();
        sim.SetObstacles([ObstacleBox.FromAabb(new Aabb(new Vector3(-10f), new Vector3(10f)))]);
        SmokeTestScenes.Run(sim, 3, Emitters);
        Assert.Equal(0, sim.TotalSmokeMass());
        Assert.Equal(0, sim.AverageDensity());
        Assert.Equal(sim.Bounds.Center, sim.SmokeCentreOfMass());
    }

    [Fact]
    public void Stepping_DoesNotAllocate()
    {
        // Single-threaded, so Parallel.For's own bookkeeping does not count; the solver itself must not allocate.
        var sim = Scene(threads: 1);
        SmokeTestScenes.Run(sim, 3, Emitters);
        long before = GC.GetAllocatedBytesForCurrentThread();
        SmokeTestScenes.Run(sim, 5, Emitters);
        sim.Step(0.05, Emitters);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 512, $"{allocated} bytes allocated in 6 steps");
    }

    [Fact]
    public void CopyDensityTo_ExportsTheRawGrid()
    {
        var sim = Scene();
        SmokeTestScenes.Run(sim, 5, Emitters);
        var buffer = new float[sim.CellCount];
        sim.CopyDensityTo(buffer);
        Assert.True(buffer.AsSpan().SequenceEqual(sim.Density));
        Assert.Throws<ArgumentException>(() => sim.CopyDensityTo(new float[10]));
    }
}
