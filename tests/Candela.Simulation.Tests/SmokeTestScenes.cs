using System.Numerics;
using Candela.Core.Math;

namespace Candela.Simulation.Tests;

/// <summary>Small, fast simulation set-ups shared by the smoke tests.</summary>
internal static class SmokeTestScenes
{
    /// <summary>A closed box (all faces walls) with no dissipation and no cooling.</summary>
    public static SmokeSimulation ClosedBox(Vector3 size, float cellSize, int threads = -1) => new(new SmokeSimulationSettings
    {
        Bounds = new Aabb(new Vector3(-size.X / 2, -size.Y / 2, 0f), new Vector3(size.X / 2, size.Y / 2, size.Z)),
        CellSize = cellSize,
        OpenFaces = DomainFaces.None,
        DissipationPerSecond = 0f,
        TemperatureRelaxationRate = 0f,
        MaxDegreeOfParallelism = threads,
    });

    /// <summary>A box with a solid floor, open elsewhere, no dissipation.</summary>
    public static SmokeSimulation OpenStage(Vector3 size, float cellSize) => new(new SmokeSimulationSettings
    {
        Bounds = new Aabb(new Vector3(-size.X / 2, -size.Y / 2, 0f), new Vector3(size.X / 2, size.Y / 2, size.Z)),
        CellSize = cellSize,
        OpenFaces = DomainFaces.AllButFloor,
        DissipationPerSecond = 0f,
    });

    public static void Run(SmokeSimulation sim, int steps, params SmokeEmitter[] emitters)
    {
        for (int n = 0; n < steps; n++)
            sim.StepOnce(emitters);
    }
}
