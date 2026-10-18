using System.Diagnostics;
using System.Numerics;
using Candela.Core.Math;
using Xunit.Abstractions;

namespace Candela.Simulation.Tests;

/// <summary>
/// Throughput of the CPU reference at the 64³ target size. Prints the numbers (run with a detailed console logger to
/// see them); the assertion only guards against pathological regressions so the test cannot flake on a busy machine.
/// </summary>
public sealed class SmokePerformanceTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void Grid64_RunsAtInteractiveRates(int threads)
    {
        var sim = new SmokeSimulation(new SmokeSimulationSettings
        {
            Bounds = new Aabb(new Vector3(-8f, -8f, 0f), new Vector3(8f, 8f, 16f)),
            CellSize = 0.25f,
            OpenFaces = DomainFaces.AllButFloor,
            MaxDegreeOfParallelism = threads,
        });
        Assert.Equal((64, 64, 64), (sim.SizeX, sim.SizeY, sim.SizeZ));
        var emitters = new[]
        {
            SmokeEmitter.FogMachine(new Vector3(-6f, 0f, 0.3f), new Vector3(1f, 0f, 0.2f), 1f),
            SmokeEmitter.Hazer(new Vector3(6f, 3f, 0.5f), Vector3.UnitZ, 1f),
            SmokeEmitter.Fan(new Vector3(-7f, -4f, 1f), new Vector3(1f, 0.5f, 0f), 0.8f),
            SmokeEmitter.Plume(new Vector3(0f, 0f, 2f), Vector3.UnitZ, 0.5f, 6f, 1e-3f, 150f),
        };
        sim.AddSmoke(new Vector3(0f, 0f, 3f), 3f, 1f, 10f);

        // Warm up the JIT and the flow (the pressure solver is warm-started, so later steps are representative).
        for (int n = 0; n < 10; n++)
            sim.StepOnce(emitters);

        const int steps = 30;
        int iterations = 0;
        float sources = 0, forces = 0, velocity = 0, projection = 0, scalars = 0;
        var cpu0 = Process.GetCurrentProcess().TotalProcessorTime;
        var watch = Stopwatch.StartNew();
        for (int n = 0; n < steps; n++)
        {
            sim.StepOnce(emitters);
            iterations += sim.LastStatistics.PressureIterations;
            var t = sim.LastTimings;
            (sources, forces, velocity, projection, scalars) =
                (sources + t.Sources, forces + t.Forces, velocity + t.VelocityAdvection, projection + t.Projection, scalars + t.ScalarAdvection);
        }

        watch.Stop();
        double cpuPerStep = (Process.GetCurrentProcess().TotalProcessorTime - cpu0).TotalMilliseconds / steps;
        double msPerStep = watch.Elapsed.TotalMilliseconds / steps;
        output.WriteLine($"64³ ({sim.CellCount} cells), {(threads > 0 ? threads : Environment.ProcessorCount)} threads: {msPerStep:F1} ms/step " +
            $"= {1000 / msPerStep:F1} steps/s, {iterations / (double)steps:F1} CG iterations/step, " +
            $"divergence {sim.LastStatistics.MaxDivergenceBefore:E2} → {sim.LastStatistics.MaxDivergenceAfter:E2} 1/s, " +
            $"process CPU {cpuPerStep:F1} ms/step");
        output.WriteLine($"stages (ms/step): sources {sources / steps:F1}, forces {forces / steps:F1}, velocity advection " +
            $"{velocity / steps:F1}, projection {projection / steps:F1}, scalar advection {scalars / steps:F1}");
        Assert.True(msPerStep < 2000, $"{msPerStep:F0} ms per step");
    }
}
