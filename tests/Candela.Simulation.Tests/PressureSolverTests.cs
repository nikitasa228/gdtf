using System.Numerics;
using Candela.Core.Math;
using Xunit.Abstractions;

namespace Candela.Simulation.Tests;

/// <summary>The pressure projection: both preconditioners, odd grid sizes, obstacles and sealed fluid pockets.</summary>
public sealed class PressureSolverTests(ITestOutputHelper output)
{
    private static SmokeSimulation OddScene(PressurePreconditioner preconditioner, DomainFaces open) => new(new SmokeSimulationSettings
    {
        // 21 × 17 × 13 cells: every level of the multigrid hierarchy has odd sizes.
        Bounds = new Aabb(new Vector3(0f, 0f, 0f), new Vector3(5.25f, 4.25f, 3.25f)),
        CellSize = 0.25f,
        OpenFaces = open,
        DissipationPerSecond = 0f,
        PressurePreconditioner = preconditioner,
        PressureTolerance = 1e-5f,
    });

    private static readonly ObstacleBox[] Obstacles =
    [
        ObstacleBox.FromAabb(new Aabb(new Vector3(2.4f, -1f, -1f), new Vector3(2.6f, 3f, 2.2f))),          // wall with a gap
        new(new Vector3(4f, 2f, 1f), new Vector3(0.4f, 0.8f, 0.3f), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.6f)),
    ];

    private static readonly SmokeEmitter[] Emitters =
    [
        SmokeEmitter.Fan(new Vector3(0.3f, 2f, 1f), new Vector3(1f, 0.2f, 0.1f), 1f, 0.3f, 6f),
        SmokeEmitter.Plume(new Vector3(1.2f, 1f, 0.3f), Vector3.UnitZ, 0.2f, 1f, 2e-4f, 120f),
    ];

    [Theory]
    [InlineData(DomainFaces.None)]
    [InlineData(DomainFaces.AllButFloor)]
    public void MultigridAndJacobi_GiveTheSameFlow(DomainFaces open)
    {
        var mg = OddScene(PressurePreconditioner.Multigrid, open);
        var jacobi = OddScene(PressurePreconditioner.Jacobi, open);
        Assert.Equal((21, 17, 13), (mg.SizeX, mg.SizeY, mg.SizeZ));
        mg.SetObstacles(Obstacles);
        jacobi.SetObstacles(Obstacles);
        int mgIterations = 0, jacobiIterations = 0;
        for (int n = 0; n < 20; n++)
        {
            mg.StepOnce(Emitters);
            jacobi.StepOnce(Emitters);
            mgIterations += mg.LastStatistics.PressureIterations;
            jacobiIterations += jacobi.LastStatistics.PressureIterations;
            Assert.True(mg.LastStatistics.MaxDivergenceAfter <= 1e-4f * mg.LastStatistics.MaxDivergenceBefore + 1e-6f);
            Assert.True(jacobi.LastStatistics.MaxDivergenceAfter <= 1e-4f * jacobi.LastStatistics.MaxDivergenceBefore + 1e-6f);
        }

        float maxDensity = mg.Density.ToArray().Max();
        float maxDifference = 0f;
        for (int c = 0; c < mg.CellCount; c++)
            maxDifference = MathF.Max(maxDifference, MathF.Abs(mg.Density[c] - jacobi.Density[c]));
        output.WriteLine($"{open}: iterations multigrid {mgIterations / 20.0:F1}, Jacobi {jacobiIterations / 20.0:F1}; " +
            $"max density difference {maxDifference:E2} of {maxDensity:F3}");
        Assert.True(maxDifference < 2e-3f * maxDensity);
        Assert.True(mgIterations * 3 < jacobiIterations, "multigrid needs far fewer iterations");
    }

    [Fact]
    public void SealedPocket_IsSolvedWithoutLeaksOrBlowUp()
    {
        var sim = new SmokeSimulation(new SmokeSimulationSettings
        {
            Bounds = new Aabb(new Vector3(-2f, -2f, 0f), new Vector3(2f, 2f, 4f)),
            CellSize = 0.2f,
            OpenFaces = DomainFaces.AllButFloor,
            DissipationPerSecond = 0f,
            TemperatureRelaxationRate = 0f,
        });
        // A hollow box (1.6 m outside, 0.2 m walls): its inside is a fluid region with no open face.
        var walls = new List<ObstacleBox>();
        var c = new Vector3(0f, 0f, 2f);
        walls.Add(ObstacleBox.FromAabb(new Aabb(c + new Vector3(-0.8f, -0.8f, -0.8f), c + new Vector3(0.8f, 0.8f, -0.6f))));
        walls.Add(ObstacleBox.FromAabb(new Aabb(c + new Vector3(-0.8f, -0.8f, 0.6f), c + new Vector3(0.8f, 0.8f, 0.8f))));
        walls.Add(ObstacleBox.FromAabb(new Aabb(c + new Vector3(-0.8f, -0.8f, -0.8f), c + new Vector3(-0.6f, 0.8f, 0.8f))));
        walls.Add(ObstacleBox.FromAabb(new Aabb(c + new Vector3(0.6f, -0.8f, -0.8f), c + new Vector3(0.8f, 0.8f, 0.8f))));
        walls.Add(ObstacleBox.FromAabb(new Aabb(c + new Vector3(-0.8f, -0.8f, -0.8f), c + new Vector3(0.8f, -0.6f, 0.8f))));
        walls.Add(ObstacleBox.FromAabb(new Aabb(c + new Vector3(-0.8f, 0.6f, -0.8f), c + new Vector3(0.8f, 0.8f, 0.8f))));
        sim.SetObstacles(walls.ToArray());
        sim.AddSmoke(c + new Vector3(0f, 0f, -0.3f), 0.35f, 1f, 40f);  // warm smoke inside the pocket
        double inside = sim.TotalSmokeMass();
        Assert.True(inside > 0);

        var fan = SmokeEmitter.Fan(new Vector3(-1.8f, 0f, 1f), Vector3.UnitX, 1f);
        for (int n = 0; n < 40; n++)
        {
            sim.StepOnce([fan]);
            Assert.True(float.IsFinite(sim.LastStatistics.MaxDivergenceAfter));
            Assert.True(sim.LastStatistics.MaxDivergenceAfter <= 1e-3f * sim.LastStatistics.MaxDivergenceBefore + 1e-6f);
        }

        // Nothing leaks out of the sealed box, nothing comes in, and the warm air inside has moved up.
        Assert.Equal(inside, sim.TotalSmokeMass(), inside * 1e-4);
        Assert.Equal(0f, sim.DensityAt(new Vector3(1.2f, 0f, 2f)));
        Assert.True(sim.SmokeCentreOfMass().Z > c.Z - 0.3f);
    }

    [Theory]
    [InlineData(PressurePreconditioner.Multigrid, DomainFaces.None)]
    [InlineData(PressurePreconditioner.Multigrid, DomainFaces.AllButFloor)]
    [InlineData(PressurePreconditioner.Jacobi, DomainFaces.None)]
    public void TightTolerance_InAClosedBox_NeverBreaksDown(PressurePreconditioner preconditioner, DomainFaces open)
    {
        // Regression: in a closed box (singular Poisson system) float rounding let a constant creep into the CG
        // residual; the multigrid preconditioner amplified it until the iteration broke down after a few steps and left
        // up to 20 % of the divergence in the flow (and later velocities of 10⁹ m/s). A tolerance near float precision
        // makes every solve run long enough to expose it.
        var sim = new SmokeSimulation(new SmokeSimulationSettings
        {
            Bounds = new Aabb(new Vector3(-2f, -2f, 0f), new Vector3(2f, 2f, 4f)),
            CellSize = 4f / 24f,
            OpenFaces = open,
            DissipationPerSecond = 0f,
            TemperatureRelaxationRate = 0f,
            PressureTolerance = 1e-6f,
            PressurePreconditioner = preconditioner,
        });
        // A sealed pocket as well, so an open box also has a singular region.
        var c = new Vector3(0.5f, 0.5f, 2f);
        sim.SetObstacles([
            ObstacleBox.FromAabb(new Aabb(c - new Vector3(0.8f), c + new Vector3(0.8f, 0.8f, -0.6f))),
            ObstacleBox.FromAabb(new Aabb(c + new Vector3(-0.8f, -0.8f, 0.6f), c + new Vector3(0.8f))),
            ObstacleBox.FromAabb(new Aabb(c - new Vector3(0.8f), c + new Vector3(-0.6f, 0.8f, 0.8f))),
            ObstacleBox.FromAabb(new Aabb(c + new Vector3(0.6f, -0.8f, -0.8f), c + new Vector3(0.8f))),
            ObstacleBox.FromAabb(new Aabb(c - new Vector3(0.8f), c + new Vector3(0.8f, -0.6f, 0.8f))),
            ObstacleBox.FromAabb(new Aabb(c + new Vector3(-0.8f, 0.6f, -0.8f), c + new Vector3(0.8f))),
        ]);
        sim.AddSmoke(c, 0.4f, 1f, 40f);
        sim.AddSmoke(new Vector3(-0.5f, 0f, 1f), 0.8f, 1f, 10f);
        var fan = SmokeEmitter.Fan(new Vector3(-1.8f, -1.2f, 0.6f), new Vector3(1f, 0.6f, 0.1f), 0.6f, 0.3f, 6f);
        float worst = 0f;
        for (int n = 0; n < 30; n++)
        {
            sim.StepOnce([fan]);
            float before = sim.LastStatistics.MaxDivergenceBefore;
            worst = MathF.Max(worst, sim.MaxDivergence() / before);
            Assert.True(sim.LastStatistics.PressureIterations < sim.Settings.MaxPressureIterations);
        }

        output.WriteLine($"{preconditioner} {open}: worst measured divergence after/before {worst:E2}");
        Assert.True(worst < 1e-4f, $"divergence after/before reached {worst:E2}");
        Assert.True(sim.VelocityAt(new Vector3(0f, 0f, 1f)).Length() < 10f);
    }

    [Fact]
    public void TinyGrids_Work()
    {
        var sim = new SmokeSimulation(new SmokeSimulationSettings
        {
            Bounds = new Aabb(Vector3.Zero, new Vector3(0.5f, 0.75f, 1f)),
            CellSize = 0.25f,
            OpenFaces = DomainFaces.ZMax,
        });
        Assert.Equal((2, 3, 4), (sim.SizeX, sim.SizeY, sim.SizeZ));
        sim.AddSmoke(new Vector3(0.25f, 0.4f, 0.3f), 0.3f, 1f, 30f);
        for (int n = 0; n < 10; n++)
            sim.StepOnce([]);
        Assert.True(sim.MaxDivergence() < 1e-3f);
        Assert.True(sim.SmokeCentreOfMass().Z > 0.3f);
    }
}
