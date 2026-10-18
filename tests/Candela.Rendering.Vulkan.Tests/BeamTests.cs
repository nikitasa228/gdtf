using System.Diagnostics;
using System.Numerics;
using Candela.Core.Geometry;
using Candela.Core.Math;
using Candela.Photometry;
using Xunit.Abstractions;

namespace Candela.Rendering.Vulkan.Tests;

/// <summary>
/// Volumetric beams (per-pixel single scattering, beam_scatter.comp): the scattering table, the in-scattering of a
/// wash and a narrow beam against the numerical integral of <see cref="BeamReference"/>, gobos and shadow casters in the
/// air, the profile across the beam edge, a panning beam that must not smear, statistics with hundreds to a thousand
/// beams, a beam showcase golden image, the stage metering of the auto exposure, lens glow (instance emission) and
/// several flame grids.
/// </summary>
public sealed class BeamTests
{
    private const int Width = 160;
    private const int Height = 120;
    private const float Haze = 0.02f;
    private const float Anisotropy = 0.7f;

    private static readonly Vector3 LightPosition = new(0f, 0f, 4f);
    private static readonly Vector3 CameraPosition = new(0f, -6f, 2f);

    private readonly ITestOutputHelper _output;

    public BeamTests(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData(1, 2f)]
    [InlineData(1, 6f)]
    [InlineData(0, 4f)]
    [InlineData(2, 2.5f)]
    [InlineData(3, 1.5f)]
    public void TableMatchesTheLineIntegralOfTheProfile(int family, float exponent)
    {
        // The table interpolates between exponent layers: check exactly on a layer and between two.
        float index = (MathF.Log2(exponent) - MathF.Log2(BeamScatteringTable.MinExponent)) /
                      (MathF.Log2(BeamScatteringTable.MaxExponent) - MathF.Log2(BeamScatteringTable.MinExponent)) * (BeamScatteringTable.ExponentSteps - 1);
        float layer = family * BeamScatteringTable.ExponentSteps + index;
        double total = BeamScatteringTable.Integral(family, exponent, 0.0, 1.0);
        Assert.True(total > 0.05, $"total {total}");
        foreach (double v in new[] { 0.0, 0.2, 0.45, 0.7 })
        {
            foreach (double y in new[] { 0.05, 0.3, 0.6, 1.0 })
            {
                double expected = BeamScatteringTable.Integral(family, exponent, v, y);
                double actual = BeamScatteringTable.Lookup(layer, v, y);
                Assert.True(Math.Abs(actual - expected) <= 0.01 * total, $"v {v}, y {y}: table {actual}, integral {expected} (total {total})");
            }
        }

        Assert.Equal(-BeamScatteringTable.Lookup(layer, 0.3, 0.5), BeamScatteringTable.Lookup(layer, 0.3, -0.5));
    }

    [SkippableTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void SingleScatteringMatchesTheIntegral(bool narrow, bool temporal)
    {
        // A 40° wash and a 6° beam crossed at right angles 2 m below the lens, in the volumetric-beam path (the narrow
        // beam seen from closer, so that it is about 20 pixels wide and the half-resolution samples resolve it).
        var light = narrow
            ? StageScene.Spot(LightPosition, Vector3.Zero, 100000f, 6f, 9f, shadows: false, type: BeamType.Spot)
            : StageScene.Spot(LightPosition, Vector3.Zero, 10000f, 40f, 60f, shadows: false);
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            renderer.World.SetLights(new[] { light });
            renderer.World.SetAtmosphere(new AtmosphereState { BaseScattering = Haze, Anisotropy = Anisotropy });
            var eye = narrow ? new Vector3(0f, -3f, 2f) : CameraPosition;
            float measured = RenderCentre(renderer, temporal ? 8 : 1, new PreviewSettings { TemporalAccumulation = temporal, Bloom = false }, eye,
                narrow ? 20f : 40f);
            Assert.Equal(1, renderer.LastStatistics.RaymarchedBeams);
            Assert.Equal(0, renderer.LastStatistics.FroxelLights);
            double expected = BeamReference.InScattering(light, eye, Vector3.UnitY, Haze, Anisotropy);
            _output.WriteLine($"measured {measured:0.0000} cd/m², reference {expected:0.0000} cd/m²");
            Assert.True(expected > 0.1, $"reference {expected}");
            Assert.InRange(Math.Abs(measured - expected) / expected, 0, 0.05);
            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableFact]
    public void GoboInTheAirMatchesTheIntegral()
    {
        // A gobo that opens only the half of the gate on the light's "up" side (+Y here): the view ray crosses the
        // beam along +Y, so only the far half of its path through the beam is lit.
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            var light = StageScene.Spot(LightPosition, Vector3.Zero, 30000f, 20f, 30f, shadows: false, type: BeamType.Spot);
            light.Gobo = renderer.World.CreateTexture(HalfGobo(), srgb: true);
            renderer.World.SetLights(new[] { light });
            renderer.World.SetAtmosphere(new AtmosphereState { BaseScattering = Haze, Anisotropy = Anisotropy });
            float measured = RenderCentre(renderer, 24, new PreviewSettings { Bloom = false });
            float gate = MathF.Tan(0.5f * light.FieldAngle);
            var right = Vector3.Cross(light.Direction, light.Up);
            double Cookie(Vector3 x, Vector3 w)
            {
                float z = Vector3.Dot(w, light.Direction);
                float px = Vector3.Dot(w, right) / z / gate, py = Vector3.Dot(w, light.Up) / z / gate;
                return py > 0f && MathF.Abs(px) <= 1f && py <= 1f ? 1.0 : 0.0;
            }

            double expected = BeamReference.InScattering(light, CameraPosition, Vector3.UnitY, Haze, Anisotropy, modulation: Cookie);
            double open = BeamReference.InScattering(light, CameraPosition, Vector3.UnitY, Haze, Anisotropy);
            _output.WriteLine($"measured {measured:0.0000}, reference {expected:0.0000}, without gobo {open:0.0000} cd/m²");
            Assert.True(expected < 0.7 * open && expected > 0.1 * open, "the gobo blocks a substantial part");
            Assert.InRange(Math.Abs(measured - expected) / expected, 0, 0.05);
            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableFact]
    public void ShadowCasterInTheBeamMatchesTheIntegral()
    {
        // A plate 1 m below the lens covers the near half of the beam (y from -1 to 0): the haze under it, on the
        // near half of the view ray, is in its shadow.
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            var scene = new StageScene(renderer);
            scene.Box(new Vector3(2f, 1f, 0.05f), new Vector3(0f, -0.5f, 3f), scene.Lambert(new Vector3(0.5f)));
            var light = StageScene.Spot(LightPosition, Vector3.Zero, 10000f, 40f, 60f);
            renderer.World.SetLights(new[] { light });
            renderer.World.SetAtmosphere(new AtmosphereState { BaseScattering = Haze, Anisotropy = Anisotropy });
            float measured = RenderCentre(renderer, 24, new PreviewSettings { Bloom = false, Indirect = IndirectLighting.Off });
            Assert.True(renderer.LastStatistics.ShadowedLights == 1);
            double Shadow(Vector3 x, Vector3 w)
            {
                // Where the path from the lens to x crosses the plate's plane (z = 3).
                float s = (LightPosition.Z - 3f) / (LightPosition.Z - x.Z);
                if (s <= 0f || s >= 1f)
                    return 1.0;
                var q = LightPosition + (x - LightPosition) * s;
                return q.X is >= -1f and <= 1f && q.Y is >= -1f and <= 0f ? 0.0 : 1.0;
            }

            double expected = BeamReference.InScattering(light, CameraPosition, Vector3.UnitY, Haze, Anisotropy, modulation: Shadow);
            double open = BeamReference.InScattering(light, CameraPosition, Vector3.UnitY, Haze, Anisotropy);
            _output.WriteLine($"measured {measured:0.0000}, reference {expected:0.0000}, unshadowed {open:0.0000} cd/m²");
            Assert.True(expected < 0.8 * open, "the plate shadows part of the path");
            Assert.InRange(Math.Abs(measured - expected) / expected, 0, 0.05);
            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableTheory]
    [InlineData(BeamType.Spot, 20f, 26f)]
    [InlineData(BeamType.Fresnel, 20f, 40f)]
    public void EdgeProfileFollowsTheDistribution(BeamType type, float beamDeg, float fieldDeg)
    {
        // Side view of a beam pointing down: across a row of pixels 2 m below the lens the in-scattering follows the
        // line integrals of the beam's distribution (sharp Spot edge, long Fresnel spill) pixel by pixel.
        const int width = 240, height = 120;
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            var light = StageScene.Spot(LightPosition, Vector3.Zero, 50000f, beamDeg, fieldDeg, shadows: false, type: type);
            renderer.World.SetLights(new[] { light });
            renderer.World.SetAtmosphere(new AtmosphereState { BaseScattering = Haze, Anisotropy = Anisotropy });
            using var target = renderer.CreateTarget(width, height);
            var camera = RenderCamera.LookAt(new Vector3(0f, -5f, 2f), new Vector3(0f, 0f, 2f), 20f);
            camera.Ev100 = 4f;
            renderer.Render(target, camera, new ViewOptions { ShowGrid = false, BackgroundLuminance = 0f },
                new PreviewSettings { TemporalAccumulation = false, Bloom = false }, 1f / 60f);
            var hdr = renderer.ReadHdr(target);
            TestVulkan.AssertNoValidationErrors(renderer.Context);
            int row = height / 2;
            var measured = new double[width];
            var expected = new double[width];
            for (int x = 0; x < width; x++)
            {
                // The image rows on either side of the centre line straddle z = 2 m: average them like the pixels.
                measured[x] = 0.5 * (hdr.LuminanceAt(x, row - 1) + hdr.LuminanceAt(x, row));
                var (o0, d0) = BeamReference.PixelRay(camera, width, height, x, row - 1);
                var (o1, d1) = BeamReference.PixelRay(camera, width, height, x, row);
                expected[x] = 0.5 * (BeamReference.InScattering(light, o0, d0, Haze, Anisotropy, 20, steps: 20000) +
                                     BeamReference.InScattering(light, o1, d1, Haze, Anisotropy, 20, steps: 20000));
            }

            double peak = expected.Max();
            Assert.True(peak > 0.1, $"peak {peak}");
            double worst = 0;
            for (int x = 0; x < width; x++)
                worst = Math.Max(worst, Math.Abs(measured[x] - expected[x]) / peak);
            _output.WriteLine($"peak {peak:0.000} cd/m², largest deviation {worst:P1} of the peak");
            Assert.InRange(worst, 0, 0.05);

            // The 50 % and 10 % crossings (the edge itself) agree within a pixel.
            foreach (double level in new[] { 0.5, 0.1 })
                Assert.InRange(Crossing(measured, level * measured.Max()) - Crossing(expected, level * peak), -1.0, 1.0);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableFact]
    public void PanningBeamDoesNotSmear()
    {
        // A gobo beam pans across the view (0.6° per frame) with temporal accumulation on: at the end it lies where a
        // fresh frame of the same state has it (the temporal pass accumulates only the gobo modulation, the beam's
        // envelope always comes from the current frame).
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            var gobo = renderer.World.CreateTexture(StageScene.GoboImage(), srgb: true);
            SpotLight Light(int frame)
            {
                float angle = -0.15f + 0.0105f * frame;
                var target = new Vector3(4f * MathF.Tan(angle), 0f, 0f);
                var light = StageScene.Spot(LightPosition, target, 40000f, 10f, 14f, shadows: false, type: BeamType.Spot);
                light.Gobo = gobo;
                return light;
            }

            renderer.World.SetAtmosphere(new AtmosphereState { BaseScattering = Haze, Anisotropy = Anisotropy });
            var camera = RenderCamera.LookAt(CameraPosition, CameraPosition + Vector3.UnitY, 40f);
            camera.Ev100 = 3f;
            var options = new ViewOptions { ShowGrid = false, BackgroundLuminance = 0f };
            using var target = renderer.CreateTarget(Width, Height);
            var settings = new PreviewSettings { Bloom = false, TemporalAccumulation = true };
            for (int i = 0; i <= 30; i++)
            {
                renderer.World.SetLights(new[] { Light(i) });
                renderer.Render(target, camera, options, settings, 1f / 60f);
            }

            var moving = renderer.ReadHdr(target);
            ImageFiles.WritePng(renderer.ReadPixels(target), Path.Combine(TestRenderers.OutputDirectory, "beam-panning-moving.png"));
            // The reference: the final state alone, converged (temporal accumulation of the still beam).
            using var fresh = renderer.CreateTarget(Width, Height);
            for (int i = 0; i < 16; i++)
                renderer.Render(fresh, camera, options, settings, 1f / 60f);
            var still = renderer.ReadHdr(fresh);
            ImageFiles.WritePng(renderer.ReadPixels(fresh), Path.Combine(TestRenderers.OutputDirectory, "beam-panning-still.png"));
            float movingCentroid = Centroid(moving), stillCentroid = Centroid(still);
            double brightness = TotalLuminance(moving) / TotalLuminance(still);
            _output.WriteLine($"centroid while panning {movingCentroid:0.00} px, still {stillCentroid:0.00} px, brightness {brightness:0.000}");
            Assert.InRange(movingCentroid - stillCentroid, -0.75f, 0.75f);
            Assert.InRange(brightness, 0.9, 1.1);
            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableTheory]
    [InlineData(300)]
    [InlineData(1000)]
    public void HundredsToAThousandBeamsReportStatistics(int count)
    {
        // A rig of narrow beams over a floor seen from the audience: every light is a volumetric beam, the GPU culling
        // reports the tile-beam pairs, and the result stays finite and validation-clean.
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            var scene = new StageScene(renderer);
            scene.Floor(40f, scene.Lambert(new Vector3(0.3f)));
            var lights = new SpotLight[count];
            int columns = count >= 1000 ? 50 : 30;
            for (int i = 0; i < count; i++)
            {
                int cx = i % columns, cy = i / columns;
                var p = new Vector3(-12f + 24f * cx / (columns - 1), -2f + 0.4f * cy, 7f);
                var aim = new Vector3(p.X * 0.6f + MathF.Sin(i * 0.7f), p.Y + 2f * MathF.Cos(i * 1.3f), 0f);
                var color = LightColor.FromRec709(new Vector3(0.5f + 0.5f * MathF.Sin(i), 0.5f + 0.5f * MathF.Sin(i * 2.1f), 0.8f));
                lights[i] = StageScene.Spot(p, aim, 20000f, 8f + i % 5, 12f + i % 5, color, shadows: false, type: BeamType.Spot);
            }

            renderer.World.SetLights(lights);
            renderer.World.SetAtmosphere(new AtmosphereState { BaseScattering = 0.03f, Anisotropy = 0.6f });
            using var target = renderer.CreateTarget(320, 180);
            var camera = RenderCamera.LookAt(new Vector3(0f, -16f, 3f), new Vector3(0f, 2f, 3.5f), 50f);
            camera.Ev100 = 6f;
            var settings = new PreviewSettings { Indirect = IndirectLighting.Off, Bloom = false };
            var view = new ViewOptions { ShowGrid = false };
            var watch = Stopwatch.StartNew();
            const int frames = 5;
            for (int i = 0; i < frames; i++)
                renderer.Render(target, camera, view, settings, 1f / 30f);
            double perFrame = watch.Elapsed.TotalMilliseconds / frames;
            var stats = renderer.LastStatistics;
            var hdr = renderer.ReadHdr(target);
            TestVulkan.AssertNoValidationErrors(renderer.Context);
            _output.WriteLine($"{count} beams at 320 × 180 on {renderer.Info.DeviceName}: {perFrame:0} ms per frame (wall)");
            _output.WriteLine(stats.ToString());
            foreach (var (name, ms) in renderer.Context.LastGpuTimings)
                _output.WriteLine($"  {name}: {ms:0.00} ms");

            Assert.Equal(count, stats.RaymarchedBeams);
            Assert.Equal(0, stats.FroxelLights);
            Assert.Equal(10 * 6, stats.BeamTiles);
            Assert.InRange(stats.BeamTilePairs, 1, count * stats.BeamTiles);
            Assert.InRange(stats.BeamMaxPerTile, 1, count);
            // Above the budget the per-pixel work is scaled down (load shedding), never below the floor.
            Assert.InRange(stats.BeamQuality, LightPlanner.MinBeamQuality, 1f);
            if (stats.BeamTilePairs / (float)stats.BeamTiles > LightPlanner.BeamBudget)
                Assert.True(stats.BeamQuality < 1f);
            foreach (float v in hdr.Rgba)
                Assert.True(float.IsFinite(v));
            Assert.True(TotalLuminance(hdr) > 0.0);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableFact]
    public void BeamStageMatchesGolden()
    {
        // Five fixtures over a floor seen from the audience: a gobo spot, a cool spot, a warm wash cut by a truss bar
        // (its shadow in the air), a three-facet prism and a narrow beam from a large lens, with glowing lenses.
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            BuildShowcase(renderer);
            using var target = renderer.CreateTarget(320, 180);
            var camera = RenderCamera.LookAt(new Vector3(0f, -11f, 1.2f), new Vector3(0f, 1f, 3.5f), 50f);
            camera.Ev100 = 7f;
            var image = new StageScene(renderer).Render(target, camera, new ViewOptions { ShowGrid = false },
                new PreviewSettings { Indirect = IndirectLighting.Off }, frames: 8);
            TestVulkan.AssertNoValidationErrors(renderer.Context);
            Assert.Equal(5, renderer.LastStatistics.RaymarchedBeams);
            GoldenImageTests.CompareWithGolden(image, "beams-stage");
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableFact]
    public void StageMeteringExposesTheLitPartBelowWhite()
    {
        // A dark stage with one bright pool: average metering lifts the dark venue (the pool clips), stage metering
        // meters the brightest 15 % (85th–99th percentile) and places it StageMeteringStops below the average point.
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            var scene = new StageScene(renderer);
            scene.Floor(40f, scene.Lambert(new Vector3(0.5f)));
            renderer.World.SetLights(new[] { StageScene.Spot(new Vector3(0f, 0f, 6f), Vector3.Zero, 20000f, 20f, 30f, shadows: false) });
            using var target = renderer.CreateTarget(160, 120);
            var camera = RenderCamera.LookAt(new Vector3(0f, -8f, 4f), new Vector3(0f, 0f, 0f), 50f);
            var view = new ViewOptions { ShowGrid = false, BackgroundLuminance = 0.02f };

            float Converge(ExposureMetering metering)
            {
                var settings = new PreviewSettings { AutoExposure = true, Metering = metering, Bloom = false, Volumetrics = false };
                for (int i = 0; i < 40; i++)
                    renderer.Render(target, camera, view, settings, 0.25f);
                return renderer.ReadHdr(target).Exposure;
            }

            float average = Converge(ExposureMetering.Average);
            float stage = Converge(ExposureMetering.Stage);
            var hdr = renderer.ReadHdr(target);
            float band = PercentileMeanLog2(hdr, 0.85f, 0.99f);
            float expected = Units.ExposureFromEv100(Units.Ev100FromLuminance(MathF.Pow(2f, band)) + VulkanRenderer.StageMeteringStops);
            _output.WriteLine($"exposure average {average:G4}, stage {stage:G4}, expected {expected:G4}");
            Assert.InRange(MathF.Log2(stage / expected), -0.2f, 0.2f);
            Assert.True(stage < average / 4f, "stage metering exposes the dark stage less than average metering");
            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableFact]
    public void InstanceEmissionShowsTheLensLuminance()
    {
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            var scene = new StageScene(renderer);
            var black = scene.Lambert(new Vector3(0f));
            var lens = scene.Box(new Vector3(1f, 0.1f, 1f), new Vector3(0f, 0f, 1f), black);
            using var target = renderer.CreateTarget(Width, Height);
            var camera = RenderCamera.LookAt(new Vector3(0f, -4f, 1f), new Vector3(0f, 0f, 1f), 30f);
            camera.Ev100 = 10f;
            var settings = new PreviewSettings { Bloom = false, Volumetrics = false, Indirect = IndirectLighting.Off };
            var view = new ViewOptions { ShowGrid = false, BackgroundLuminance = 0f };
            var luminance = LightColor.FromRec709(new Vector3(1f, 0.6f, 0.3f)).Rgb * 2000f;
            renderer.World.SetInstanceEmission(lens, luminance);
            renderer.Render(target, camera, view, settings, 1f / 60f);
            var lit = renderer.ReadHdr(target);
            Assert.InRange(lit.LuminanceAt(Width / 2, Height / 2), 1980f, 2020f);
            var xy = LightColor.FromRec2020(lit.At(Width / 2, Height / 2)).ToXy();
            Assert.InRange(Vector2.Distance(xy, LightColor.FromRec709(new Vector3(1f, 0.6f, 0.3f)).ToXy()), 0f, 0.005f);

            // Bloom spreads the glare around the lens.
            renderer.Render(target, camera, view, new PreviewSettings { Volumetrics = false, Indirect = IndirectLighting.Off, BloomIntensity = 0.2f }, 1f / 60f);
            var bloom = renderer.ReadBloom(target);
            Assert.True(bloom.LuminanceAt(bloom.Width / 2, bloom.Height / 10) > 0f, "glare outside the lens");

            // Directional (a lens): the full luminance along the axis, half at the half-angle, nothing from behind.
            float Centre(Vector3 axis)
            {
                renderer.World.SetInstanceEmission(lens, luminance, axis, 0.1f);
                renderer.Render(target, camera, view, settings, 1f / 60f);
                return renderer.ReadHdr(target).LuminanceAt(Width / 2, Height / 2);
            }

            Assert.InRange(Centre(-Vector3.UnitY), 1980f, 2020f);
            Assert.InRange(Centre(new Vector3(MathF.Sin(0.1f), -MathF.Cos(0.1f), 0f)), 950f, 1050f);
            Assert.InRange(Centre(Vector3.UnitY), 0f, 1f);
            Assert.InRange(Centre(Vector3.Zero), 1980f, 2020f); // no axis: uniform

            // Non-finite and negative components count as zero; zero turns the glow off.
            renderer.World.SetInstanceEmission(lens, new Vector3(float.NaN, -5f, float.PositiveInfinity));
            renderer.Render(target, camera, view, settings, 1f / 60f);
            Assert.True(float.IsFinite(renderer.ReadHdr(target).LuminanceAt(Width / 2, Height / 2)));
            renderer.World.SetInstanceEmission(lens, Vector3.Zero);
            renderer.Render(target, camera, view, settings, 1f / 60f);
            Assert.Equal(0f, renderer.ReadHdr(target).LuminanceAt(Width / 2, Height / 2));

            renderer.World.DestroyInstance(lens);
            Assert.Throws<ArgumentException>(() => renderer.World.SetInstanceEmission(lens, Vector3.One));
            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableFact]
    public void SeveralFlameGridsGlowEachWithItsOwnTemperature()
    {
        // Two optically thick flames in their own grids (different boxes, sizes and temperatures) and the single-grid
        // property together: each shows its blackbody luminance; where two boxes overlap the hotter flame wins.
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            static VolumeGrid Uniform(Vector3 min, Vector3 max, int n, float kelvin)
            {
                var values = new float[n * n * n];
                Array.Fill(values, kelvin);
                return new VolumeGrid { Min = min, Max = max, SizeX = n, SizeY = n, SizeZ = n, Values = values };
            }

            var left = Uniform(new Vector3(-3.5f, -1.5f, -0.5f), new Vector3(-0.5f, 1.5f, 2.5f), 8, 1500f);
            var right = Uniform(new Vector3(0.5f, -1.5f, -0.5f), new Vector3(3.5f, 1.5f, 2.5f), 6, 1800f);
            var cooler = Uniform(new Vector3(1f, -1f, 0f), new Vector3(3f, 1f, 2f), 4, 1000f); // inside the right one
            renderer.World.SetAtmosphere(new AtmosphereState { BaseScattering = 0f, FlameTemperature = left, FlameTemperatures = new[] { right, cooler } });
            using var target = renderer.CreateTarget(240, 120);
            var camera = RenderCamera.LookAt(new Vector3(0f, -8f, 1f), new Vector3(0f, 0f, 1f), 40f);
            camera.Ev100 = 15f;
            for (int i = 0; i < 4; i++)
                renderer.Render(target, camera, new ViewOptions { ShowGrid = false, BackgroundLuminance = 0f }, new PreviewSettings(), 1f / 30f);
            var hdr = renderer.ReadHdr(target);
            TestVulkan.AssertNoValidationErrors(renderer.Context);
            float Expected(float kelvin) => (float)BlackbodyRadiator.Luminance(kelvin) * (1f - MathF.Exp(-VulkanRenderer.FlameAbsorption * 3f));
            Vector2 Pixel(Vector3 p)
            {
                var clip = Vector4.Transform(new Vector4(p, 1f), camera.View * ReverseZ.Perspective(camera.FovY, 2f, camera.Near));
                return new Vector2((clip.X / clip.W * 0.5f + 0.5f) * 240f, (clip.Y / clip.W * 0.5f + 0.5f) * 120f);
            }

            var l = Pixel(new Vector3(-2f, -1.5f, 1f));
            var r = Pixel(new Vector3(2f, -1.5f, 1f));
            float measuredLeft = hdr.LuminanceAt((int)l.X, (int)l.Y), measuredRight = hdr.LuminanceAt((int)r.X, (int)r.Y);
            _output.WriteLine($"left {measuredLeft:G4} (expected {Expected(1500f):G4}), right {measuredRight:G4} (expected {Expected(1800f):G4})");
            Assert.InRange(MathF.Abs(measuredLeft - Expected(1500f)) / Expected(1500f), 0f, 0.05f);
            Assert.InRange(MathF.Abs(measuredRight - Expected(1800f)) / Expected(1800f), 0f, 0.05f);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    /// <summary>The showcase stage of <see cref="BeamStageMatchesGolden"/> (also used for the documentation images).</summary>
    internal static void BuildShowcase(VulkanRenderer renderer)
    {
        var scene = new StageScene(renderer);
        var floor = scene.Lambert(new Vector3(0.25f));
        var black = scene.Lambert(new Vector3(0.03f));
        scene.Floor(20f, floor);
        scene.Box(new Vector3(20f, 0.2f, 10f), new Vector3(0f, 6f, 5f), floor);
        scene.Box(new Vector3(8f, 0.3f, 0.3f), new Vector3(0f, 2f, 4.6f), black);
        var gobo = renderer.World.CreateTexture(StageScene.GoboImage(), srgb: true);
        var white = LightColor.FromCct(6500f);
        var lights = new[]
        {
            StageScene.Spot(new Vector3(-4f, 3f, 6f), new Vector3(-1.5f, 0f, 0f), 200000f, 6f, 9f, white, type: BeamType.Spot),
            StageScene.Spot(new Vector3(-2f, 2.5f, 6f), new Vector3(-0.5f, -1f, 0f), 60000f, 18f, 24f, LightColor.FromRec709(new Vector3(0.2f, 0.4f, 1f)),
                type: BeamType.Spot),
            StageScene.Spot(new Vector3(0f, 3f, 6.5f), Vector3.Zero, 30000f, 30f, 45f, LightColor.FromCct(3200f)),
            StageScene.Spot(new Vector3(2f, 2.5f, 6f), new Vector3(0.8f, -1f, 0f), 80000f, 12f, 16f, LightColor.FromRec709(new Vector3(1f, 0.2f, 0.6f)),
                type: BeamType.Spot),
            StageScene.Spot(new Vector3(4f, 3f, 6f), new Vector3(1.5f, 0f, 0f), 400000f, 2f, 3f, white, type: BeamType.Spot),
        };
        lights[0].Gobo = gobo;
        lights[0].GoboRotation = 0.3f;
        lights[3].PrismFacets = 3;
        lights[3].PrismSpread = 0.12f;
        lights[4].SourceRadius = 0.1f;
        foreach (var light in lights)
        {
            // A fixture body behind the lens, the lens glowing with peak candela / lens area.
            var body = scene.Box(new Vector3(0.35f), light.Position - light.Direction * 0.2f, black);
            var lensDisc = scene.Box(new Vector3(2f * light.SourceRadius, 2f * light.SourceRadius, 0.02f), light.Position, black,
                InstanceFlags.NotPickable);
            renderer.World.UpdateInstance(lensDisc, Matrix4x4.CreateScale(2f * light.SourceRadius, 2f * light.SourceRadius, 0.02f) *
                                                    Matrix4x4.CreateWorld(light.Position, light.Direction, light.Up), InstanceFlags.NotPickable);
            renderer.World.SetInstanceEmission(lensDisc, light.Color * (light.PeakCandela / (MathF.PI * light.SourceRadius * light.SourceRadius)));
            _ = body;
        }

        renderer.World.SetLights(lights);
        renderer.World.SetAtmosphere(new AtmosphereState { BaseScattering = 0.05f, Anisotropy = 0.6f });
    }

    /// <summary>A gobo whose top half (the light's "up" side of the gate) is open.</summary>
    private static ImageData HalfGobo()
    {
        const int size = 64;
        var pixels = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int i = (y * size + x) * 4;
                pixels[i] = pixels[i + 1] = pixels[i + 2] = 255;
                pixels[i + 3] = y < size / 2 ? (byte)255 : (byte)0;
            }
        }

        return new ImageData { Width = size, Height = size, Pixels = pixels };
    }

    /// <summary>Renders the haze from the side (camera at <paramref name="eye"/>, default <see cref="CameraPosition"/>, looking +Y) and returns the centre luminance.</summary>
    private static float RenderCentre(VulkanRenderer renderer, int frames, PreviewSettings settings, Vector3? eye = null, float fovY = 40f)
    {
        using var target = renderer.CreateTarget(Width, Height);
        var position = eye ?? CameraPosition;
        var camera = RenderCamera.LookAt(position, position + Vector3.UnitY, fovY);
        camera.Ev100 = 2f;
        for (int i = 0; i < frames; i++)
            renderer.Render(target, camera, new ViewOptions { ShowGrid = false, BackgroundLuminance = 0f }, settings, 1f / 60f);
        var hdr = renderer.ReadHdr(target);
        return 0.25f * (hdr.LuminanceAt(Width / 2 - 1, Height / 2 - 1) + hdr.LuminanceAt(Width / 2, Height / 2 - 1) +
                        hdr.LuminanceAt(Width / 2 - 1, Height / 2) + hdr.LuminanceAt(Width / 2, Height / 2));
    }

    /// <summary>Luminance-weighted mean column of the lower two thirds of the image, pixels.</summary>
    private static float Centroid(HdrReadback hdr)
    {
        double sum = 0, weighted = 0;
        for (int y = hdr.Height / 3; y < hdr.Height; y++)
        {
            for (int x = 0; x < hdr.Width; x++)
            {
                float l = hdr.LuminanceAt(x, y);
                sum += l;
                weighted += l * (x + 0.5);
            }
        }

        return (float)(weighted / sum);
    }

    private static double TotalLuminance(HdrReadback hdr)
    {
        double sum = 0;
        for (int y = 0; y < hdr.Height; y++)
        {
            for (int x = 0; x < hdr.Width; x++)
                sum += hdr.LuminanceAt(x, y);
        }

        return sum;
    }

    /// <summary>Position (pixels, interpolated) of the first rising crossing of <paramref name="level"/>.</summary>
    private static double Crossing(double[] values, double level)
    {
        for (int i = 1; i < values.Length; i++)
        {
            if (values[i - 1] < level && values[i] >= level)
                return i - 1 + (level - values[i - 1]) / (values[i] - values[i - 1]);
        }

        return double.NaN;
    }

    /// <summary>Mean log2 luminance of the pixels between two percentiles.</summary>
    private static float PercentileMeanLog2(HdrReadback hdr, float low, float high)
    {
        var values = new float[hdr.Width * hdr.Height];
        for (int y = 0; y < hdr.Height; y++)
        {
            for (int x = 0; x < hdr.Width; x++)
                values[y * hdr.Width + x] = MathF.Log2(MathF.Max(hdr.LuminanceAt(x, y), 1e-12f));
        }

        Array.Sort(values);
        int lo = (int)(values.Length * low), hi = (int)(values.Length * high);
        double sum = 0;
        for (int i = lo; i < hi; i++)
            sum += values[i];
        return (float)(sum / (hi - lo));
    }
}
