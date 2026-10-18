using System.Numerics;
using Candela.Core.Math;
using Xunit.Abstractions;

namespace Candela.Simulation.Tests;

/// <summary>
/// Physical behaviour of the smoke solver: conservation, incompressibility, buoyancy, obstacles, fans, hazers, fog,
/// airflow and dissipation. Grids are small (≤ 32 cells per axis) so the whole class runs in a few seconds.
/// </summary>
public sealed class SmokePhysicsTests(ITestOutputHelper output)
{
    [Fact]
    public void ClosedBoxWithoutDissipation_ConservesSmokeMassOver200Steps()
    {
        var sim = SmokeTestScenes.ClosedBox(new Vector3(4f, 4f, 4f), 0.125f);
        // A warm puff rises, hits the ceiling and spreads, while a fan stirs the box: a demanding flow for advection.
        sim.AddSmoke(new Vector3(-0.5f, 0f, 1f), 0.8f, 1f, 25f);
        double initial = sim.TotalSmokeMass();
        var fan = SmokeEmitter.Fan(new Vector3(-1.8f, -1.2f, 0.6f), new Vector3(1f, 0.6f, 0.1f), 0.6f, 0.3f, 6f);

        SmokeTestScenes.Run(sim, 200, fan);

        double final = sim.TotalSmokeMass();
        double change = (final - initial) / initial;
        output.WriteLine($"mass {initial:E4} → {final:E4} kg ({change * 100:F3} %) after {sim.Time:F2} s");
        Assert.True(Math.Abs(change) < 0.03, $"mass changed by {change * 100:F2} %");
        Assert.All(sim.Density.ToArray(), d => Assert.True(d >= 0f && float.IsFinite(d)));
    }

    [Fact]
    public void Projection_RemovesNearlyAllDivergence()
    {
        var sim = SmokeTestScenes.OpenStage(new Vector3(6f, 6f, 4f), 0.2f);
        var riser = new ObstacleBox(new Vector3(0.5f, 0f, 0.5f), new Vector3(1f, 0.6f, 0.5f), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.4f));
        sim.SetObstacles([riser]);
        var emitters = new[]
        {
            SmokeEmitter.Fan(new Vector3(-2.5f, 0f, 1.2f), Vector3.UnitX, 1f),
            SmokeEmitter.FogMachine(new Vector3(-2f, 1.5f, 0.3f), new Vector3(1f, -0.3f, 0f), 1f),
            SmokeEmitter.Plume(new Vector3(2f, -2f, 0.5f), Vector3.UnitZ, 0.3f, 4f, 1e-4f, 200f),
        };

        for (int n = 0; n < 15; n++)
        {
            sim.StepOnce(emitters);
            var stats = sim.LastStatistics;
            Assert.True(stats.MaxDivergenceBefore > 0.1f, $"step {n}: forcing should create divergence ({stats.MaxDivergenceBefore})");
            Assert.True(stats.MaxDivergenceAfter <= 2e-3f * stats.MaxDivergenceBefore,
                $"step {n}: divergence {stats.MaxDivergenceBefore:E2} → {stats.MaxDivergenceAfter:E2} 1/s");
        }

        // The independent diagnostic agrees with the solver's own residual.
        float measured = sim.MaxDivergence();
        output.WriteLine($"divergence {sim.LastStatistics.MaxDivergenceBefore:E2} → {measured:E2} 1/s in " +
            $"{sim.LastStatistics.PressureIterations} CG iterations");
        Assert.True(measured <= 2e-3f * sim.LastStatistics.MaxDivergenceBefore);
    }

    [Fact]
    public void WarmSmoke_Rises()
    {
        var sim = SmokeTestScenes.ClosedBox(new Vector3(4f, 4f, 6f), 0.2f);
        // Mean excess of the (1 − q²)² puff ≈ 0.23 × 30 K ≈ 7 K: reduced gravity ≈ 0.23 m/s², so the thermal should
        // accelerate to a few tenths of a metre per second (rise ≈ 0.3–0.6 m in the first 2 s).
        sim.AddSmoke(new Vector3(0f, 0f, 1.2f), 0.8f, 1f, 30f);
        float z0 = sim.SmokeCentreOfMass().Z;
        var heights = new List<float> { z0 };
        for (int n = 0; n < 4; n++)
        {
            SmokeTestScenes.Run(sim, 15);
            heights.Add(sim.SmokeCentreOfMass().Z);
        }

        output.WriteLine("centre of mass z: " + string.Join(" → ", heights.Select(h => h.ToString("F2"))));
        for (int n = 1; n < heights.Count; n++)
            Assert.True(heights[n] > heights[n - 1], "warm smoke must keep rising");
        Assert.InRange(heights[^1] - z0, 0.3f, 1.2f);
        // Rising faster and faster (accelerating thermal).
        Assert.True(heights[^1] - heights[^2] > heights[1] - heights[0]);
    }

    [Fact]
    public void WarmSource_LiftsItsSmokeHigherThanANeutralOne()
    {
        var hot = SmokeTestScenes.ClosedBox(new Vector3(4f, 4f, 6f), 0.2f);
        var neutral = SmokeTestScenes.ClosedBox(new Vector3(4f, 4f, 6f), 0.2f);
        // A brazier-sized source: 0.2 m disc, 0.5 m/s, 100 K above ambient (≈ 1.9 kW), 0.1 g/s of smoke. Heskestad's
        // plume correlation gives a steady centre-line velocity of ≈ 1.3 m/s one metre above it; a starting plume's
        // front travels at roughly half of that.
        var warm = SmokeEmitter.Plume(new Vector3(0f, 0f, 0.3f), Vector3.UnitZ, 0.1f, 0.5f, 1e-4f, 100f);
        SmokeTestScenes.Run(hot, 90, warm);
        SmokeTestScenes.Run(neutral, 90, warm with { TemperatureExcess = 0f });
        float zHot = hot.SmokeCentreOfMass().Z, zNeutral = neutral.SmokeCentreOfMass().Z;
        float w = hot.VelocityAt(new Vector3(0f, 0f, 1.3f)).Z;
        output.WriteLine($"centre of mass after 3 s: warm {zHot:F2} m, neutral {zNeutral:F2} m, w(1.3 m) = {w:F2} m/s");
        Assert.True(zHot > zNeutral + 0.25f);
        Assert.InRange(w, 0.5f, 2f);
        Assert.True(hot.TemperatureAt(new Vector3(0f, 0f, 1.3f)) > SimulationConstants.AmbientTemperature + 5f);
        Assert.True(hot.DensityAt(new Vector3(0f, 0f, 1.3f)) > 10f * neutral.DensityAt(new Vector3(0f, 0f, 1.3f)));
    }

    [Fact]
    public void NeutralSmokeInStillAir_StaysPut()
    {
        var sim = SmokeTestScenes.ClosedBox(new Vector3(4f, 4f, 4f), 0.2f);
        sim.AddSmoke(new Vector3(0f, 0f, 2f), 1f, 0.2f);
        var c0 = sim.SmokeCentreOfMass();
        SmokeTestScenes.Run(sim, 60);
        var c1 = sim.SmokeCentreOfMass();
        // Haze-level aerosol loading is ~3e-5 kg/m³: it settles, but by millimetres, not metres.
        Assert.True(Vector3.Distance(c0, c1) < 0.05f, $"moved {Vector3.Distance(c0, c1):F3} m");
    }

    [Fact]
    public void WallObstacle_KeepsTheFarSideNearlyCleanForAWhile()
    {
        var sim = SmokeTestScenes.ClosedBox(new Vector3(8f, 4f, 4f), 0.25f);
        // A 3 m wall across the whole width, 2 cm thick (much thinner than a cell): voxelisation must not leak.
        sim.SetObstacles([ObstacleBox.FromAabb(new Aabb(new Vector3(-0.01f, -2.5f, -0.1f), new Vector3(0.01f, 2.5f, 3f)))]);
        var fog = SmokeEmitter.FogMachine(new Vector3(-3.5f, 0f, 0.4f), Vector3.UnitX, 1f);

        SmokeTestScenes.Run(sim, 45, fog);

        double near = 0, far = 0;
        var density = sim.Density;
        for (int k = 0; k < sim.SizeZ; k++)
        {
            for (int j = 0; j < sim.SizeY; j++)
            {
                for (int i = 0; i < sim.SizeX; i++)
                {
                    double d = density[i + sim.SizeX * (j + sim.SizeY * k)];
                    if (i < sim.SizeX / 2)
                        near += d;
                    else
                        far += d;
                }
            }
        }

        output.WriteLine($"after {sim.Time:F1} s: near side {near:E3}, far side {far:E3} ({far / near * 100:F2} %)");
        Assert.True(near > 0);
        Assert.True(far < 0.02 * near, $"far side holds {far / near * 100:F2} % of the near side");
        for (int k = 0; k < sim.SizeZ; k++)
        {
            for (int j = 0; j < sim.SizeY; j++)
                Assert.True(sim.IsSolid(sim.SizeX / 2 - 1, j, k) || sim.IsSolid(sim.SizeX / 2, j, k) || k >= 12);
        }
    }

    [Fact]
    public void FullHeightWall_SeparatesTwoRoomsCompletely()
    {
        var sim = SmokeTestScenes.ClosedBox(new Vector3(6f, 3f, 3f), 0.25f);
        sim.SetObstacles([ObstacleBox.FromAabb(new Aabb(new Vector3(-0.05f, -2f, -1f), new Vector3(0.05f, 2f, 4f)))]);
        var fog = SmokeEmitter.FogMachine(new Vector3(-2.5f, 0f, 0.4f), Vector3.UnitX, 1f);
        SmokeTestScenes.Run(sim, 60, fog);
        Assert.Equal(0f, sim.DensityAt(new Vector3(1.5f, 0f, 1f)));
        Assert.Equal(0f, sim.DensityAt(new Vector3(2.8f, 1.2f, 0.2f)));
        Assert.True(sim.DensityAt(new Vector3(-1.5f, 0f, 0.5f)) > 0f);
    }

    [Fact]
    public void Fan_MovesSmokeAlongItsDirection()
    {
        var sim = SmokeTestScenes.ClosedBox(new Vector3(8f, 4f, 4f), 0.25f);
        sim.AddSmoke(new Vector3(-1.5f, 0f, 1.5f), 1f, 0.5f);
        var c0 = sim.SmokeCentreOfMass();
        var fan = SmokeEmitter.Fan(new Vector3(-3.6f, 0f, 1.5f), Vector3.UnitX, 1f);

        SmokeTestScenes.Run(sim, 45, fan);

        var c1 = sim.SmokeCentreOfMass();
        output.WriteLine($"centre of mass {c0} → {c1}");
        Assert.True(c1.X - c0.X > 0.8f, $"moved only {c1.X - c0.X:F2} m along the fan axis");
        Assert.True(MathF.Abs(c1.Y - c0.Y) < 0.3f * (c1.X - c0.X), "the drift should follow the fan direction");
        Assert.True(sim.VelocityAt(new Vector3(-2.5f, 0f, 1.5f)).X > 3f, "air in front of the fan must move at metres per second");
    }

    [Fact]
    public void FanAtZeroSpeed_DoesNothing()
    {
        var withFan = SmokeTestScenes.ClosedBox(new Vector3(4f, 4f, 4f), 0.25f);
        var without = SmokeTestScenes.ClosedBox(new Vector3(4f, 4f, 4f), 0.25f);
        withFan.AddSmoke(new Vector3(0f, 0f, 2f), 1f, 0.5f);
        without.AddSmoke(new Vector3(0f, 0f, 2f), 1f, 0.5f);
        SmokeTestScenes.Run(withFan, 5, SmokeEmitter.Fan(new Vector3(-1.5f, 0f, 2f), Vector3.UnitX, 0f));
        SmokeTestScenes.Run(without, 5);
        Assert.True(withFan.Density.SequenceEqual(without.Density));
        Assert.Equal(without.VelocityAt(new Vector3(-1f, 0f, 2f)), withFan.VelocityAt(new Vector3(-1f, 0f, 2f)));
    }

    [Fact]
    public void Hazer_GraduallyRaisesTheAverageDensityOfAClosedRoom()
    {
        var sim = SmokeTestScenes.ClosedBox(new Vector3(6f, 6f, 3f), 0.25f);
        var hazer = SmokeEmitter.Hazer(new Vector3(-2.5f, -2.5f, 0.3f), new Vector3(1f, 1f, 0.3f), 1f);
        double volume = 6.0 * 6.0 * 3.0;
        var averages = new List<double> { sim.AverageDensity() };
        for (int n = 0; n < 6; n++)
        {
            SmokeTestScenes.Run(sim, 15, hazer);
            averages.Add(sim.AverageDensity());
        }

        output.WriteLine("average density: " + string.Join(" → ", averages.Select(a => a.ToString("E3"))));
        for (int n = 1; n < averages.Count; n++)
            Assert.True(averages[n] > averages[n - 1]);

        // Mass balance: everything the hazer produced is in the room (no dissipation in this scene).
        double expected = SmokeEmitter.DefaultHazerMassRate * sim.Time / (volume * sim.MassConcentrationAtFullDensity);
        Assert.InRange(averages[^1], 0.95 * expected, 1.05 * expected);
        // A slow, even fill: the increments are (nearly) equal from interval to interval.
        double first = averages[1] - averages[0], last = averages[^1] - averages[^2];
        Assert.InRange(last / first, 0.9, 1.1);
    }

    [Fact]
    public void FogMachine_InjectsItsMassRate()
    {
        var sim = SmokeTestScenes.ClosedBox(new Vector3(6f, 4f, 3f), 0.25f);
        var fog = SmokeEmitter.FogMachine(new Vector3(-2.5f, 0f, 0.4f), Vector3.UnitX, 0.5f);
        SmokeTestScenes.Run(sim, 30, fog);
        double expected = fog.MassRate * 0.5 * sim.Time;
        double actual = sim.TotalSmokeMass();
        output.WriteLine($"injected {actual * 1000:F3} g, expected {expected * 1000:F3} g");
        Assert.InRange(actual, 0.97 * expected, 1.03 * expected);
    }

    [Fact]
    public void FogMachineJet_ThrowsFogForward()
    {
        var sim = SmokeTestScenes.ClosedBox(new Vector3(8f, 4f, 3f), 0.2f);
        var fog = SmokeEmitter.FogMachine(new Vector3(-3.5f, 0f, 0.6f), Vector3.UnitX, 1f);
        SmokeTestScenes.Run(sim, 60, fog);
        float ahead = sim.DensityAt(new Vector3(-2.5f, 0f, 0.7f));
        float behind = sim.DensityAt(new Vector3(-3.8f, 0f, 0.7f));
        float speed = sim.VelocityAt(new Vector3(-2f, 0f, 0.6f)).X;
        output.WriteLine($"after 2 s: density 1 m ahead {ahead:F2}, 0.3 m behind {behind:F3}; air 1.5 m ahead {speed:F2} m/s; " +
            $"centre {sim.SmokeCentreOfMass()}");
        Assert.True(ahead > 1f, "fog is thrown at least a metre");
        Assert.True(ahead > 20f * behind);
        Assert.InRange(speed, 0.2f, 2f); // grid-filtered round jet of a 20 mm, 10 m/s nozzle (≈ 0.6–0.8 m/s at 1.5 m)
        Assert.True(sim.SmokeCentreOfMass().X > -3.3f);
    }

    [Fact]
    public void LowFog_SinksAndSpreadsOnTheFloor()
    {
        var sim = SmokeTestScenes.ClosedBox(new Vector3(8f, 6f, 3f), 0.2f);
        var warm = SmokeTestScenes.ClosedBox(new Vector3(8f, 6f, 3f), 0.2f);
        var nozzle = new Vector3(-3.5f, 0f, 0.4f);
        SmokeTestScenes.Run(sim, 90, SmokeEmitter.FogMachine(nozzle, Vector3.UnitX, 1f, lowFog: true));
        SmokeTestScenes.Run(warm, 90, SmokeEmitter.FogMachine(nozzle, Vector3.UnitX, 1f));

        var cold = sim.SmokeCentreOfMass();
        var hot = warm.SmokeCentreOfMass();
        output.WriteLine($"after 3 s: low fog centre {cold}, warm fog centre {hot}");
        Assert.True(cold.Z < nozzle.Z, $"low fog centre at {cold.Z:F2} m");
        Assert.True(hot.Z > cold.Z + 0.1f, "warm fog rises above chilled fog");
        Assert.True(sim.DensityAt(new Vector3(-2.5f, 0f, 0.1f)) > 5f * sim.DensityAt(new Vector3(-2.5f, 0f, 0.9f)),
            "low fog hugs the floor");
        Assert.True(sim.TemperatureAt(new Vector3(-3f, 0f, 0.3f)) < SimulationConstants.AmbientTemperature);
    }

    [Fact]
    public void Airflow_CarriesSmokeThroughAnOpenRoom()
    {
        var settings = new SmokeSimulationSettings
        {
            Bounds = new Aabb(new Vector3(-4f, -2f, 0f), new Vector3(4f, 2f, 3f)),
            CellSize = 0.25f,
            OpenFaces = DomainFaces.XMin | DomainFaces.XMax,
            Airflow = new Vector3(0.5f, 0f, 0f),
            AirflowResponseTime = 0.5f,
            DissipationPerSecond = 0f,
        };
        var sim = new SmokeSimulation(settings);
        sim.AddSmoke(new Vector3(-2f, 0f, 1.5f), 0.8f, 1f);
        var c0 = sim.SmokeCentreOfMass();
        SmokeTestScenes.Run(sim, 90);
        var c1 = sim.SmokeCentreOfMass();
        // 3 s at 0.5 m/s minus the 0.5 s ramp ≈ 1.25 m.
        output.WriteLine($"centre {c0.X:F2} → {c1.X:F2} m, air speed {sim.VelocityAt(new Vector3(0f, 0f, 1.5f))}");
        Assert.InRange(c1.X - c0.X, 0.9f, 1.5f);
        Assert.InRange(sim.VelocityAt(new Vector3(0f, 0f, 1.5f)).X, 0.45f, 0.55f);
        Assert.Empty(sim.Issues);
    }

    [Fact]
    public void Airflow_IntoAClosedBox_IsReportedAndIgnored()
    {
        var sim = new SmokeSimulation(new SmokeSimulationSettings
        {
            Bounds = new Aabb(Vector3.Zero, new Vector3(2f, 2f, 2f)),
            CellSize = 0.25f,
            OpenFaces = DomainFaces.None,
            Airflow = new Vector3(1f, 0f, 0f),
        });
        Assert.Contains(sim.Issues, i => i.Code == "simulation.airflow-closed");
        SmokeTestScenes.Run(sim, 10);
        Assert.Equal(Vector3.Zero, sim.VelocityAt(new Vector3(1f, 1f, 1f)));
    }

    [Fact]
    public void SmokeLeavesThroughAnOpenCeiling()
    {
        var sim = new SmokeSimulation(new SmokeSimulationSettings
        {
            Bounds = new Aabb(new Vector3(-1.5f, -1.5f, 0f), new Vector3(1.5f, 1.5f, 3f)),
            CellSize = 0.15f,
            OpenFaces = DomainFaces.ZMax,
            DissipationPerSecond = 0f,
            TemperatureRelaxationRate = 0f,
        });
        sim.AddSmoke(new Vector3(0f, 0f, 1.8f), 0.7f, 1f, 60f);
        double m0 = sim.TotalSmokeMass();
        SmokeTestScenes.Run(sim, 90);
        Assert.True(sim.TotalSmokeMass() < 0.7 * m0, "hot smoke escapes through the open ceiling");
    }

    [Fact]
    public void Dissipation_DecaysDensityExponentially()
    {
        var sim = new SmokeSimulation(new SmokeSimulationSettings
        {
            Bounds = new Aabb(Vector3.Zero, new Vector3(2f, 2f, 2f)),
            CellSize = 0.25f,
            OpenFaces = DomainFaces.None,
            DissipationPerSecond = 0.2f,
        });
        sim.Fill(1f);
        SmokeTestScenes.Run(sim, 60); // 2 s
        Assert.Equal(0.8 * 0.8, sim.AverageDensity(), 1e-3);
    }

    [Fact]
    public void Temperature_RelaxesTowardAmbient()
    {
        var sim = new SmokeSimulation(new SmokeSimulationSettings
        {
            Bounds = new Aabb(Vector3.Zero, new Vector3(2f, 2f, 2f)),
            CellSize = 0.25f,
            OpenFaces = DomainFaces.None,
            TemperatureRelaxationRate = 0.5f,
        });
        sim.AddSmoke(new Vector3(1f, 1f, 1f), 2.5f, 0f, 10f);
        float before = sim.TemperatureExcess.ToArray().Max();
        SmokeTestScenes.Run(sim, 30);
        float after = sim.TemperatureExcess.ToArray().Max();
        Assert.InRange(after / before, 0.4f, 0.75f); // e^(−0.5) = 0.61, plus mixing
    }

    [Fact]
    public void Viscosity_AndDiffusion_StayStableAndConservative()
    {
        var sim = new SmokeSimulation(new SmokeSimulationSettings
        {
            Bounds = new Aabb(new Vector3(-2f, -2f, 0f), new Vector3(2f, 2f, 4f)),
            CellSize = 0.2f,
            OpenFaces = DomainFaces.None,
            DissipationPerSecond = 0f,
            Viscosity = 0.01f,
            Diffusivity = 0.01f,
            ScalarAdvection = ScalarAdvectionScheme.SemiLagrangian,
        });
        sim.AddSmoke(new Vector3(0f, 0f, 1f), 0.8f, 1f, 20f);
        double m0 = sim.TotalSmokeMass();
        float peak0 = sim.Density.ToArray().Max();
        SmokeTestScenes.Run(sim, 60, SmokeEmitter.Fan(new Vector3(-1.8f, 0f, 1f), Vector3.UnitX, 0.5f));
        Assert.InRange(sim.TotalSmokeMass() / m0, 0.97, 1.03);
        Assert.True(sim.Density.ToArray().Max() < peak0);
        Assert.True(sim.MaxDivergence() < 1e-2f);
    }

    [Fact]
    public void StrongDiffusion_ConservesSmokeAndHeat()
    {
        // Regression: the diffusion ran after the conservation fixer, and its 8 Jacobi sweeps (not converged for
        // λ = D·dt/h² ≫ 1) gained 4 % of the smoke and heat in 60 steps at D = 2 m²/s.
        var sim = new SmokeSimulation(new SmokeSimulationSettings
        {
            Bounds = new Aabb(new Vector3(-2f, -2f, 0f), new Vector3(2f, 2f, 4f)),
            CellSize = 0.2f,
            OpenFaces = DomainFaces.None,
            DissipationPerSecond = 0f,
            TemperatureRelaxationRate = 0f,
            Diffusivity = 2f,
            Viscosity = 2f,
        });
        sim.SetObstacles([ObstacleBox.FromAabb(new Aabb(new Vector3(-0.5f, -2f, 0f), new Vector3(0.5f, 0.5f, 2f)))]);
        sim.AddSmoke(new Vector3(-1f, 1f, 1f), 0.6f, 1f, 30f);
        double m0 = sim.TotalSmokeMass();
        double h0 = sim.TemperatureExcess.ToArray().Sum(t => (double)t);
        float peak0 = sim.Density.ToArray().Max();
        SmokeTestScenes.Run(sim, 60);
        double h1 = sim.TemperatureExcess.ToArray().Sum(t => (double)t);
        Assert.Equal(1.0, sim.TotalSmokeMass() / m0, 1e-4);
        Assert.Equal(1.0, h1 / h0, 1e-4);
        Assert.True(sim.Density.ToArray().Max() < 0.1f * peak0, "the puff has spread");
    }

    [Fact]
    public void DensityAt_InterpolatesCellValuesTrilinearly()
    {
        var sim = SmokeTestScenes.ClosedBox(new Vector3(4f, 4f, 4f), 0.25f);
        sim.Fill(0.25f);
        Assert.Equal(0.25f, sim.DensityAt(new Vector3(0.3f, -1.1f, 2.7f)), 1e-5f);
        Assert.Equal(0f, sim.DensityAt(new Vector3(0f, 0f, -0.5f)));
        Assert.Equal(0f, sim.DensityAt(new Vector3(float.NaN, 0f, 1f)));

        sim.Reset();
        sim.AddSmoke(new Vector3(0f, 0f, 2f), 1.2f, 1f);
        var density = sim.Density;
        // At a cell centre the sample is the cell value; halfway between two centres it is their mean.
        var centre = sim.Bounds.Min + new Vector3(8.5f, 8.5f, 8.5f) * sim.CellSize;
        int c = 8 + sim.SizeX * (8 + sim.SizeY * 8);
        Assert.Equal(density[c], sim.DensityAt(centre), 1e-5f);
        var mid = centre + new Vector3(0.5f * sim.CellSize, 0f, 0f);
        Assert.Equal(0.5f * (density[c] + density[c + 1]), sim.DensityAt(mid), 1e-5f);
    }

    [Fact]
    public void Obstacles_AreVoxelisedFromOrientedBoxes()
    {
        var sim = SmokeTestScenes.ClosedBox(new Vector3(4f, 4f, 4f), 0.1f);
        var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 4f);
        sim.SetObstacles([new ObstacleBox(new Vector3(0f, 0f, 1f), new Vector3(1f, 0.1f, 1f), rotation)]);
        Assert.Equal(1, sim.ObstacleCount);
        bool SolidAt(Vector3 p)
        {
            var g = (p - sim.Bounds.Min) / sim.CellSize;
            return sim.IsSolid((int)g.X, (int)g.Y, (int)g.Z);
        }

        Assert.True(SolidAt(new Vector3(0.5f, 0.5f, 1f)));   // along the rotated long axis
        Assert.True(SolidAt(new Vector3(-0.6f, -0.6f, 0.5f)));
        Assert.False(SolidAt(new Vector3(0.5f, -0.5f, 1f)));  // across it
        Assert.False(SolidAt(new Vector3(0f, 0f, 2.5f)));     // above it

        // Matrix-placed box: unit cube scaled to 2 × 0.2 × 1 m and moved.
        var world = Matrix4x4.CreateScale(2f, 0.2f, 1f) * Matrix4x4.CreateTranslation(0f, 1.5f, 2.5f);
        var placed = ObstacleBox.FromLocalBox(new Vector3(-0.5f), new Vector3(0.5f), world);
        Assert.Equal(new Vector3(1f, 0.1f, 0.5f), placed.HalfExtents);
        Assert.Equal(new Vector3(0f, 1.5f, 2.5f), placed.Center);
    }

    [Fact]
    public void SetObstacles_RemovesSmokeInsideNewSolids()
    {
        var sim = SmokeTestScenes.ClosedBox(new Vector3(4f, 4f, 4f), 0.25f);
        sim.Fill(1f);
        sim.SetObstacles([ObstacleBox.FromAabb(new Aabb(new Vector3(-1f, -1f, 0f), new Vector3(1f, 1f, 1f)))]);
        Assert.Equal(0f, sim.DensityAt(new Vector3(0f, 0f, 0.5f)));
        Assert.Equal(1f, sim.DensityAt(new Vector3(0f, 0f, 2.5f)), 1e-5f);
        sim.SetObstacles([]);
        Assert.Equal(0, sim.ObstacleCount);
        Assert.False(sim.IsSolid(8, 8, 1));
    }

    [Fact]
    public void FromComponent_UsesTheAtmosphereVolume()
    {
        var component = new Candela.Core.SceneGraph.Components.AtmosphereVolumeComponent
        {
            Size = new Vector3(12f, 8f, 6f),
            CellSize = 0.5f,
            Dissipation = 0.05f,
            ScatteringAtFullDensity = 0.8f,
            Airflow = new Vector3(0.2f, 0f, 0f),
        };
        var settings = SmokeSimulationSettings.FromComponent(component, new Vector3(1f, 2f, 3f));
        var sim = new SmokeSimulation(settings);
        Assert.Equal((24, 16, 12), (sim.SizeX, sim.SizeY, sim.SizeZ));
        Assert.Equal(new Vector3(-5f, -2f, 0f), sim.Bounds.Min);
        Assert.Equal(0.05f, sim.Settings.DissipationPerSecond);
        Assert.Equal(0.8f, sim.Settings.ScatteringAtFullDensity);
        Assert.Equal(new Vector3(0.2f, 0f, 0f), sim.Settings.Airflow);
    }
}
