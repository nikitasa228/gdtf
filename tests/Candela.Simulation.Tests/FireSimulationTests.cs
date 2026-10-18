using System.Numerics;
using Candela.Core.Math;
using Candela.Photometry;
using Xunit.Abstractions;

namespace Candela.Simulation.Tests;

/// <summary>Flame model magnitudes, ignition/extinction transients, light samples, volumes, smoke coupling and state.</summary>
public sealed class FireSimulationTests(ITestOutputHelper output)
{
    private static readonly Guid FlameId = new("6f1e2d3c-4b5a-4968-8776-655443322110");
    private const double Frame = 1.0 / 30.0;

    private static FlameEmitter Flame(float output, float height = 5f, float burnTime = 0f, Vector3? direction = null) =>
        FlameEmitter.Create(FlameId, new Vector3(1f, 2f, 0.5f), direction ?? Vector3.UnitZ, output, height, burnTime);

    private static void Run(FireSimulation fire, double seconds, params FlameEmitter[] emitters)
    {
        for (double t = 0; t < seconds - 1e-9; t += Frame)
            fire.Step(Frame, emitters);
    }

    private static float SampleIntensity(FireSimulation fire)
    {
        float sum = 0f;
        foreach (var s in fire.LightSamples)
            sum += s.IntensityCandela;
        return sum;
    }

    [Theory]
    [InlineData(3f, 0.5e6f, 0.7e6f, 6e3f, 9e3f)]
    [InlineData(5f, 1.9e6f, 2.3e6f, 22e3f, 30e3f)]
    [InlineData(8f, 6.0e6f, 7.5e6f, 75e3f, 95e3f)]
    public void FlameModel_GivesPlausibleMagnitudesForPropaneColumns(float height, float minQ, float maxQ, float minCd, float maxCd)
    {
        float q = FlameModel.HeatReleaseRateForHeight(height, FlameEmitter.DefaultNozzleDiameter);
        float flux = FlameModel.LuminousFlux(q);
        float intensity = FlameModel.MeanIntensity(flux);
        output.WriteLine($"{height} m: Q = {q / 1e6:F2} MW, propane {FlameModel.FuelMassRate(q) * 1e3:F0} g/s, " +
            $"Φ = {flux / 1e3:F0} klm, I = {intensity / 1e3:F1} kcd, tip {FlameModel.FlameTipVelocity(0.75f * q):F1} m/s");
        Assert.InRange(q, minQ, maxQ);
        Assert.InRange(intensity, minCd, maxCd);
        Assert.Equal(height, FlameModel.FlameHeight(q, FlameEmitter.DefaultNozzleDiameter), 3);
        // ≈ 0.15 lm per watt of heat (χ_r = 0.25 × 0.62 lm/W at 1800 K).
        Assert.InRange(flux / q, 0.14f, 0.17f);
    }

    [Fact]
    public void FlameModel_ReproducesACandle()
    {
        // A candle releases ≈ 80 W and is (by the definition's history) about one candela; its flame is ≈ 5 cm tall.
        float intensity = FlameModel.MeanIntensity(FlameModel.LuminousFlux(80f));
        Assert.InRange(intensity, 0.7f, 1.4f);
        Assert.InRange(FlameModel.FlameHeight(80f, 0.002f), 0.03f, 0.12f);
        Assert.Equal(0f, FlameModel.LuminousFlux(float.NaN));
        Assert.Equal(0f, FlameModel.FlameHeight(-5f, 0.1f));
        Assert.Equal(0f, FlameModel.HeatReleaseRateForHeight(float.PositiveInfinity, 0.1f));
        Assert.InRange(FlameModel.PulsationFrequency(1f), 1.4f, 1.6f);
    }

    [Fact]
    public void LightOutput_OnlyWhileBurning()
    {
        var fire = new FireSimulation();
        Run(fire, 0.5, Flame(0f));
        Assert.Equal(0, fire.LightSamples.Length);
        Assert.Equal(0f, fire.TotalLuminousFlux);
        Assert.Empty(fire.Volumes);
        Assert.Equal(0, fire.SmokeEmitters.Length);

        Run(fire, 1.0, Flame(1f));
        Assert.True(fire.LightSamples.Length > 0);
        float steady = fire.TotalLuminousFlux;
        float expected = FlameModel.LuminousFlux(FlameModel.HeatReleaseRateForHeight(5f, FlameEmitter.DefaultNozzleDiameter));
        output.WriteLine($"steady flux {steady / 1e3:F0} klm (model {expected / 1e3:F0} klm), samples {SampleIntensity(fire) / 1e3:F1} kcd");
        Assert.InRange(steady, 0.9f * expected, 1.1f * expected);
        Assert.Equal(FlameModel.MeanIntensity(steady), SampleIntensity(fire), steady * 1e-6f);
        Assert.All(fire.LightSamples.ToArray(), s =>
        {
            Assert.Equal(FlameId, s.FlameId);
            Assert.True(s.IntensityCandela > 0f && float.IsFinite(s.IntensityCandela));
            Assert.InRange(s.Position.Z, 0.5f, 6.5f);
        });

        // Valve closed: the flame dies within a few tenths of a second and then nothing is left.
        var times = new List<(double T, float Flux)>();
        for (int n = 0; n < 18; n++)
        {
            fire.Step(Frame, [Flame(0f)]);
            times.Add((n * Frame, fire.TotalLuminousFlux));
        }

        output.WriteLine("extinction: " + string.Join(" ", times.Select(t => $"{t.Flux / steady:F2}")));
        Assert.True(times[5].Flux < 0.25f * steady, "most of the light is gone 0.2 s after the valve closes");
        Assert.Equal(0f, fire.TotalLuminousFlux);
        Assert.Equal(0, fire.LightSamples.Length);
        Assert.Empty(fire.Volumes);
        Assert.Equal(0, fire.SmokeEmitters.Length);
        Assert.False(fire.TryGetStatus(FlameId, out _));
    }

    [Fact]
    public void Ignition_GrowsTheFlameOverAboutTwoTenthsOfASecond()
    {
        var fire = new FireSimulation();
        var heights = new List<float>();
        for (int n = 0; n < 15; n++)
        {
            fire.Step(Frame, [Flame(1f)]);
            Assert.True(fire.TryGetStatus(FlameId, out var status));
            heights.Add(status.Height);
        }

        output.WriteLine("height: " + string.Join(" ", heights.Select(h => h.ToString("F2"))));
        Assert.InRange(heights[0], 0.05f, 2f);           // 33 ms: just lit
        Assert.InRange(heights[2], 1f, 3.5f);            // 0.1 s: growing
        Assert.True(heights[8] > 0.9f * 5f);             // 0.3 s: (nearly) full height
        Assert.InRange(heights[^1], 4.5f, 5.3f);
        for (int n = 1; n < 6; n++)
            Assert.True(heights[n] > heights[n - 1]);
    }

    [Fact]
    public void FlameColour_IsThatOfA1500To2200KelvinBlackBody()
    {
        var fire = new FireSimulation();
        Run(fire, 0.8, Flame(1f));
        float total = fire.TotalLuminousFlux;
        Vector3 xyz = Vector3.Zero;
        foreach (var s in fire.LightSamples)
        {
            var cct = s.Color.ToCct();
            output.WriteLine($"sample at {s.Position.Z:F2} m: {s.IntensityCandela / 1e3:F1} kcd, T {s.Temperature:F0} K, CCT {cct.Kelvin:F0} K, Δuv {cct.Duv:F4}");
            if (s.LuminousFlux > 0.05f * total)
                Assert.InRange(cct.Kelvin, 1500f, 2200f);
            Assert.True(MathF.Abs(cct.Duv) < 0.003f, "a black-body colour lies on the Planckian locus");
            xyz += s.Color.ToXyz() * s.LuminousFlux;
        }

        var overall = LightColor.FromXyz(xyz).ToCct();
        output.WriteLine($"flame colour: CCT {overall.Kelvin:F0} K");
        Assert.InRange(overall.Kelvin, 1500f, 2200f);
    }

    [Fact]
    public void BurnTime_ClosesTheValveUntilTheOutputIsReleased()
    {
        var fire = new FireSimulation();
        Run(fire, 0.5, Flame(1f, burnTime: 0.3f));
        // 0.2 s after the 0.3 s shot ended the flame is (almost) gone although the output is still up.
        Assert.True(fire.TryGetStatus(FlameId, out var status));
        Assert.False(status.Burning);
        Assert.Equal(0f, status.Valve, 2);
        Run(fire, 0.5, Flame(1f, burnTime: 0.3f));
        Assert.Equal(0f, fire.TotalLuminousFlux);
        Assert.True(fire.TryGetStatus(FlameId, out _), "the device waits for the output to be released");

        // Release and fire again: a new shot.
        Run(fire, 0.1, Flame(0f, burnTime: 0.3f));
        Run(fire, 0.25, Flame(1f, burnTime: 0.3f));
        Assert.True(fire.TotalLuminousFlux > 0f);
        Assert.True(fire.TryGetStatus(FlameId, out status) && status.Burning);
    }

    [Fact]
    public void Output_ScalesHeatAndHeight()
    {
        var full = new FireSimulation();
        var half = new FireSimulation();
        Run(full, 1.0, Flame(1f));
        Run(half, 1.0, Flame(0.5f));
        full.TryGetStatus(FlameId, out var f);
        half.TryGetStatus(FlameId, out var h);
        output.WriteLine($"full: {f.HeatRelease / 1e6:F2} MW, {f.Height:F2} m; half: {h.HeatRelease / 1e6:F2} MW, {h.Height:F2} m");
        Assert.InRange(h.HeatRelease / f.HeatRelease, 0.45f, 0.55f);
        // Heskestad: L ∝ Q^(2/5) → 0.5^0.4 = 0.76.
        Assert.InRange(h.Height / f.Height, 0.70f, 0.82f);
    }

    [Fact]
    public void TiltedNozzle_BendsTheFlameUpward()
    {
        var fire = new FireSimulation();
        Run(fire, 0.8, Flame(1f, height: 4f, direction: Vector3.UnitX));
        var tip = fire.SmokeEmitters.ToArray().MaxBy(e => e.Position.X).Position;
        output.WriteLine($"top section of a horizontal 4 m flame from (1, 2, 0.5): {tip}");
        Assert.True(tip.X > 1f + 2.5f, "it fires along the nozzle");
        Assert.True(tip.Z > 0.5f + 1f, "buoyancy lifts it");
    }

    [Fact]
    public void Volume_HoldsTheFlameTemperature()
    {
        var fire = new FireSimulation();
        Run(fire, 0.8, Flame(1f));
        var volume = Assert.Single(fire.Volumes);
        Assert.Equal(FlameId, volume.FlameId);
        Assert.InRange(volume.PeakTemperature, 1700f, 1801f);
        Assert.True(volume.Bounds.Contains(new Vector3(1f, 2f, 3f)));
        Assert.True(volume.SizeX <= 48 && volume.SizeY <= 48 && volume.SizeZ <= 48);
        Assert.InRange(fire.TemperatureAt(new Vector3(1f, 2f, 2.5f)), 1200f, 1801f); // inside the column
        Assert.Equal(0f, fire.TemperatureAt(new Vector3(5f, 2f, 2.5f)));           // well beside it
        Assert.Equal(0f, fire.TemperatureAt(new Vector3(1f, 2f, 9f)));             // above the tip

        // The emission coefficient makes the volume's black-body emission equal the flame's flux (independent check
        // with the exact Planck integration of Candela.Photometry instead of the simulation's table).
        double emission = 0;
        foreach (float t in volume.Temperature)
            emission += t > 0f ? BlackbodyRadiator.Luminance(t) : 0.0;
        emission *= 4.0 * Math.PI * Math.Pow(volume.CellSize, 3) * volume.EmissionCoefficient;
        Assert.Equal(1.0, emission / volume.LuminousFlux, 2);

        // One global grid for the renderer.
        var box = fire.CombinedBounds;
        var grid = new float[24 * 24 * 48];
        fire.RasterizeTemperature(box, 24, 24, 48, grid);
        Assert.InRange(grid.Max(), 1500f, 1801f);
        Assert.Throws<ArgumentException>(() => fire.RasterizeTemperature(box, 24, 24, 48, new float[10]));
    }

    [Fact]
    public void Fire_InjectsSmokeAndHeatIntoTheSmokeSimulation()
    {
        var fire = new FireSimulation();
        var smoke = new SmokeSimulation(new SmokeSimulationSettings
        {
            Bounds = new Aabb(new Vector3(-4f, -3f, 0f), new Vector3(6f, 7f, 12f)),
            CellSize = 0.4f,
            OpenFaces = DomainFaces.AllButFloor,
            DissipationPerSecond = 0f,
        });
        var emitters = new List<SmokeEmitter>();
        for (int n = 0; n < 45; n++)
        {
            fire.Step(Frame, [Flame(1f)]);
            emitters.Clear();
            emitters.AddRange(fire.SmokeEmitters.ToArray());
            smoke.Step(Frame, emitters.ToArray());
        }

        var plumes = fire.SmokeEmitters.ToArray();
        Assert.Equal(fire.Settings.LightSamplesPerFlame, plumes.Length);
        Assert.All(plumes, p => Assert.Equal(SmokeEmitterKind.Plume, p.Kind));
        fire.TryGetStatus(FlameId, out var status);
        // Heat and soot of the plumes add up to the flame's convective heat and soot production.
        double heat = plumes.Sum(p => SimulationConstants.AirDensity * SimulationConstants.AirSpecificHeat * p.ExitSpeed
            * Math.PI * p.NozzleDiameter * p.NozzleDiameter / 4 * p.TemperatureExcess);
        double sootRate = plumes.Sum(p => p.MassRate);
        Assert.Equal(1.0, heat / ((1 - fire.Settings.RadiantFraction) * status.HeatRelease), 3);
        Assert.Equal(1.0, sootRate / (fire.Settings.SootYield * FlameModel.FuelMassRate(status.HeatRelease)), 3);
        Assert.InRange(plumes.Max(p => p.Position.Z), 4.5f, 6.5f);   // the top section of the 5 m flame
        Assert.InRange(plumes.Min(p => p.Position.Z), 0.5f, 2f);     // the bottom section

        double soot = smoke.TotalSmokeMass();
        float rise = smoke.VelocityAt(new Vector3(1f, 2f, 8f)).Z;
        float excess = smoke.TemperatureAt(new Vector3(1f, 2f, 8f)) - SimulationConstants.AmbientTemperature;
        output.WriteLine($"after 1.5 s: soot in the box {soot * 1e3:F2} g (sources {sootRate * 1e3:F2} g/s), " +
            $"air 2.5 m above the flame tip {excess:F0} K warmer, rising at {rise:F1} m/s; plume sources: " +
            string.Join(", ", plumes.Select(p => $"z {p.Position.Z:F1} m r {p.NozzleDiameter / 2:F2} m ΔT {p.TemperatureExcess:F0} K")));
        Assert.InRange(sootRate, 0.5e-3, 2e-3);   // 2.4 % of ≈ 46 g/s propane
        Assert.True(soot > 0.5 * sootRate * 1.0);
        // Heskestad's plume correlation gives ≈ 180 K and ≈ 6 m/s on the centre line there. A 0.4 m grid without a
        // sub-grid turbulence model entrains too little ambient air, so the resolved plume is hotter and faster (a
        // documented limit) — but it must stay well clear of the 1000 K safety clamp now that heat is conserved.
        Assert.InRange(excess, 100f, 900f);
        Assert.InRange(rise, 2f, 20f);
        Assert.True(smoke.SmokeCentreOfMass().Z > 5f);
    }

    [Fact]
    public void SameInputs_GiveIdenticalFlames_AndSnapshotsRestoreThem()
    {
        var other = Guid.NewGuid();
        FlameEmitter[] Inputs(int frame) =>
        [
            Flame(frame < 40 ? 1f : 0f, burnTime: 1f),
            FlameEmitter.Create(other, new Vector3(-2f, 0f, 0f), new Vector3(0.3f, 0f, 1f), frame % 20 < 12 ? 0.8f : 0f, 3f),
        ];
        double[] dts = [0.016, 0.033, 0.02, 0.05, 0.0167];

        var a = new FireSimulation();
        var b = new FireSimulation();
        FireSimulationSnapshot? snapshot = null;
        var reference = new List<FlameLightSample[]>();
        for (int n = 0; n < 60; n++)
        {
            a.Step(dts[n % dts.Length], Inputs(n));
            b.Step(dts[n % dts.Length], Inputs(n));
            Assert.True(a.LightSamples.SequenceEqual(b.LightSamples));
            if (n == 25)
                snapshot = a.CaptureSnapshot();
            if (n > 25)
                reference.Add(a.LightSamples.ToArray());
        }

        Assert.NotNull(snapshot);
        Assert.Equal(2, snapshot!.FlameCount);
        a.RestoreSnapshot(snapshot);
        Assert.Equal(snapshot.Time, a.Time);
        for (int n = 26; n < 60; n++)
        {
            a.Step(dts[n % dts.Length], Inputs(n));
            Assert.True(a.LightSamples.SequenceEqual(reference[n - 26]), $"frame {n} differs after restoring");
        }

        a.Reset();
        Assert.Equal(0, a.Time);
        Assert.Equal(0, a.LightSamples.Length);
        Assert.Throws<ArgumentException>(() => new FireSimulation(new FireSimulationSettings { Seed = 7 }).RestoreSnapshot(snapshot));
    }

    [Fact]
    public void BadInput_IsSanitisedNotFatal()
    {
        var fire = new FireSimulation(new FireSimulationSettings
        {
            TimeStep = float.NaN,
            IgnitionTime = -1f,
            RadiationTemperature = 99999f,
            ParticlesPerSubStep = 0,
            SootYield = float.NaN,
        });
        Assert.Equal(5, fire.Issues.Count);
        var weird = new[]
        {
            new FlameEmitter { Id = Guid.NewGuid(), Position = new Vector3(float.NaN), Output = 1f },
            new FlameEmitter { Id = FlameId, Position = Vector3.Zero, Direction = new Vector3(float.NaN), Output = float.PositiveInfinity,
                NominalHeight = -3f, BurnTime = float.NaN, NozzleDiameter = float.NaN, HeatReleaseRate = float.NegativeInfinity },
            new FlameEmitter { Id = FlameId, Position = Vector3.One, Output = 1f },   // duplicate id
        };
        Run(fire, 0.5, weird);
        Assert.Equal(2, fire.LastStatistics.IgnoredEmitters);
        Assert.True(fire.TotalLuminousFlux > 0f);       // the sanitised device burns with the default 5 m flame
        Assert.True(fire.TryGetStatus(FlameId, out var status));
        Assert.InRange(status.Height, 3.5f, 5.5f);
        Assert.All(fire.LightSamples.ToArray(), s => Assert.True(float.IsFinite(s.IntensityCandela) && float.IsFinite(s.Position.X)));
        Assert.Equal(0, fire.Step(double.NaN, weird));
        Assert.Equal(0, fire.Step(-1, weird));
    }

    [Fact]
    public void Stepping_DoesNotAllocateInSteadyState()
    {
        var fire = new FireSimulation();
        var emitters = new[] { Flame(1f), FlameEmitter.Create(Guid.NewGuid(), new Vector3(-3f, 0f, 0f), Vector3.UnitZ, 1f, 8f) };
        Run(fire, 1.0, emitters);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int n = 0; n < 30; n++)
            fire.Step(Frame, emitters);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 1024, $"{allocated} bytes allocated in 30 steps");
    }
}
