using System.Numerics;
using Candela.Core.Geometry;
using Candela.Photometry;
using Xunit.Abstractions;

namespace Candela.Rendering.Vulkan.Tests;

public sealed class ReviewProbeTests
{
    private readonly ITestOutputHelper _out;

    public ReviewProbeTests(ITestOutputHelper output) => _out = output;

    private static Photometry.SpotLight[] Reference(IEnumerable<SpotLight> lights) => lights.Select(l => new Photometry.SpotLight(l.Position, l.Direction,
        l.PeakCandela, BeamDistribution.Create(l.BeamType, l.BeamAngle, l.FieldAngle), LightColor.FromRec2020(l.Color))).ToArray();

    [SkippableFact]
    public void GrazingLightOnShadowCastingFloorMatchesLightMeter()
    {
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            var scene = new StageScene(renderer);
            scene.Floor(30f, scene.Lambert(new Vector3(0.5f)), InstanceFlags.CastShadow);
            var lights = new[]
            {
                StageScene.Spot(new Vector3(-8f, 0f, 1.5f), new Vector3(0f, 0f, 0f), 200000f, 20f, 30f),
                StageScene.Spot(new Vector3(0f, -9f, 3f), new Vector3(0f, 1f, 0f), 100000f, 30f, 45f),
            };
            lights[0].SourceRadius = 0.1f;
            lights[1].SourceRadius = 0.05f;
            renderer.World.SetLights(lights);
            var view = new TopDownView(240, 180, 12f);
            using var target = renderer.CreateTarget(240, 180);
            renderer.Render(target, view.Camera, new ViewOptions { ShowGrid = false, Overlay = OverlayMode.Lux }, new PreviewSettings(), 0f);
            var lux = renderer.ReadAnalysis(target);
            TestVulkan.AssertNoValidationErrors(renderer.Context);
            var reference = Reference(lights);
            int bad = 0, n = 0;
            var report = new List<string>();
            for (int py = 0; py < 180; py += 3)
            {
                for (int px = 0; px < 240; px += 3)
                {
                    var p = view.FloorPointOf(px, py);
                    float expected = LightMeter.IlluminanceAt(reference, p, Vector3.UnitZ);
                    if (expected < 20f)
                        continue;
                    n++;
                    float measured = lux[py * 240 + px];
                    if (MathF.Abs(measured - expected) > 0.01f * expected)
                    {
                        bad++;
                        if (report.Count < 20)
                            report.Add($"{p}: {measured:F1} vs {expected:F1}");
                    }
                }
            }

            _out.WriteLine($"{bad} of {n} off");
            Assert.True(bad == 0, $"{bad} of {n} points off by >1%: " + string.Join("; ", report));
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableFact]
    public void TwoViewsInOneFrameKeepTheirShadowTilesCached()
    {
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            var scene = new StageScene(renderer);
            var grey = scene.Lambert(new Vector3(0.6f));
            scene.Floor(40f, grey);
            scene.Box(Vector3.One, new Vector3(0f, 0f, 0.5f), grey);
            var lights = new SpotLight[6];
            for (int i = 0; i < lights.Length; i++)
            {
                var p = new Vector3(-10f + 4f * i, 0f, 6f);
                lights[i] = StageScene.Spot(p, p with { Z = 0f }, 20000f, 30f, 45f);
            }

            renderer.World.SetLights(lights);
            using var a = renderer.CreateTarget(96, 72);
            using var b = renderer.CreateTarget(96, 72);
            var camA = RenderCamera.LookAt(new Vector3(-10f, -3f, 3f), new Vector3(-10f, 0f, 0f), 50f);
            var camB = RenderCamera.LookAt(new Vector3(10f, -3f, 3f), new Vector3(10f, 0f, 0f), 50f);
            var options = new ViewOptions { ShowGrid = false };
            var settings = new PreviewSettings { Indirect = IndirectLighting.Off, Volumetrics = false };
            int tilesA = 0, tilesB = 0;
            for (int frame = 0; frame < 8; frame++)
            {
                var f = renderer.Context.BeginFrame();
                renderer.Render(a, camA, options, settings, 1f / 60f);
                tilesA = renderer.LastStatistics.ShadowTilesRendered;
                renderer.Render(b, camB, options, settings, 1f / 60f);
                tilesB = renderer.LastStatistics.ShadowTilesRendered;
                renderer.Context.SubmitFrame(f);
                _out.WriteLine($"frame {frame}: A {tilesA} tiles, B {tilesB} tiles");
            }

            Assert.Equal(0, tilesA);
            Assert.Equal(0, tilesB);
            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableFact]
    public void FrostAndPrismConserveTheFluxOnTheFloor()
    {
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            var scene = new StageScene(renderer);
            scene.Floor(40f, scene.Lambert(new Vector3(0.5f)), InstanceFlags.None);
            var view = new TopDownView(200, 200, 16f);
            using var target = renderer.CreateTarget(200, 200);
            double Flux(SpotLight light)
            {
                renderer.World.SetLights(new[] { light });
                renderer.Render(target, view.Camera, new ViewOptions { ShowGrid = false, Overlay = OverlayMode.Lux }, new PreviewSettings(), 0f);
                var lux = renderer.ReadAnalysis(target);
                double pixelArea = (16.0 / 200) * (16.0 / 200);
                return lux.Sum(v => (double)v) * pixelArea;
            }

            var basic = StageScene.Spot(new Vector3(0f, 0f, 4f), Vector3.Zero, 20000f, 20f, 30f, shadows: false);
            double open = Flux(basic);
            var frosted = basic;
            frosted.Frost = 1f;
            double frost = Flux(frosted);
            var prism = basic;
            prism.PrismFacets = 3;
            prism.PrismSpread = 0.3f;
            double prismFlux = Flux(prism);
            var prism1 = basic;
            prism1.PrismFacets = 1;
            prism1.PrismSpread = 0f;
            double prism1Flux = Flux(prism1);
            _out.WriteLine($"open {open:F1} lm, frost {frost:F1}, prism {prismFlux:F1}, prism1 {prism1Flux:F1}");
            // Expected flux on the floor plane from the beam distribution (CPU): integrate LightMeter over the same grid.
            var reference = Reference(new[] { basic });
            double cpu = 0;
            for (int y = 0; y < 200; y++)
            {
                for (int x = 0; x < 200; x++)
                    cpu += LightMeter.IlluminanceAt(reference, view.FloorPointOf(x, y), Vector3.UnitZ);
            }

            cpu *= (16.0 / 200) * (16.0 / 200);
            _out.WriteLine($"cpu {cpu:F1}");
            Assert.InRange(open / cpu, 0.99, 1.01);
            Assert.InRange(frost / open, 0.95, 1.05);
            Assert.InRange(prismFlux / open, 0.95, 1.05);
            Assert.InRange(prism1Flux / open, 0.99, 1.01);
            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableFact]
    public void PrismFacetsLandWhereTheGeometrySays()
    {
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            var scene = new StageScene(renderer);
            scene.Floor(40f, scene.Lambert(new Vector3(0.5f)), InstanceFlags.None);
            var view = new TopDownView(240, 240, 10f);
            using var target = renderer.CreateTarget(240, 240);
            var light = StageScene.Spot(new Vector3(0f, 0f, 5f), Vector3.Zero, 30000f, 6f, 9f, shadows: false);
            light.Up = Vector3.UnitY;
            light.PrismFacets = 3;
            light.PrismSpread = 0.35f;
            light.PrismRotation = 0.4f;
            renderer.World.SetLights(new[] { light });
            renderer.Render(target, view.Camera, new ViewOptions { ShowGrid = false, Overlay = OverlayMode.Lux }, new PreviewSettings(), 0f);
            var lux = renderer.ReadAnalysis(target);
            TestVulkan.AssertNoValidationErrors(renderer.Context);
            // right = direction x up = (0,0,-1) x (0,1,0) = (1,0,0)
            var right = Vector3.Cross(light.Direction, light.Up);
            for (int k = 0; k < 3; k++)
            {
                float phi = light.PrismRotation + 2f * MathF.PI * k / 3f;
                var azimuth = right * MathF.Cos(phi) + light.Up * MathF.Sin(phi);
                var axis = light.Direction * MathF.Cos(light.PrismSpread) + azimuth * MathF.Sin(light.PrismSpread);
                float t = -light.Position.Z / axis.Z;
                var hit = light.Position + axis * t;
                // find the brightest pixel near the hit
                var (hx, hy, _) = view.NearestPixel(hit);
                float best = 0f;
                int bx = hx, by = hy;
                for (int dy = -20; dy <= 20; dy++)
                {
                    for (int dx = -20; dx <= 20; dx++)
                    {
                        int x = Math.Clamp(hx + dx, 0, 239), y = Math.Clamp(hy + dy, 0, 239);
                        if (lux[y * 240 + x] > best)
                        {
                            best = lux[y * 240 + x];
                            bx = x;
                            by = y;
                        }
                    }
                }

                var peak = view.FloorPointOf(bx, by);
                float d = (light.Position - hit).Length();
                float expectedPeak = light.PeakCandela / 3f / (d * d) * MathF.Abs(axis.Z);
                _out.WriteLine($"facet {k}: hit {hit}, brightest {peak} ({best:F1} lx, expected ~{expectedPeak:F1})");
                Assert.True(Vector3.Distance(peak, hit) < 0.15f, $"facet {k}: peak at {peak}, expected {hit}");
                Assert.InRange(best / expectedPeak, 0.9f, 1.1f);
            }
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableFact]
    public void IrisApertureIsIrisTimesFieldAngle()
    {
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            var scene = new StageScene(renderer);
            scene.Floor(40f, scene.Lambert(new Vector3(0.5f)), InstanceFlags.None);
            var view = new TopDownView(240, 240, 10f);
            using var target = renderer.CreateTarget(240, 240);
            var light = StageScene.Spot(new Vector3(0f, 0f, 5f), Vector3.Zero, 30000f, 30f, 40f, shadows: false);
            light.Iris = 0.5f;
            renderer.World.SetLights(new[] { light });
            renderer.Render(target, view.Camera, new ViewOptions { ShowGrid = false, Overlay = OverlayMode.Lux }, new PreviewSettings(), 0f);
            var lux = renderer.ReadAnalysis(target);
            var reference = Reference(new[] { light });
            float apertureHalf = 0.5f * light.Iris * light.FieldAngle;
            foreach (float frac in new[] { 0.5f, 0.85f, 1.1f, 1.3f })
            {
                float r = 5f * MathF.Tan(apertureHalf * frac);
                var (x, y, c) = view.NearestPixel(new Vector3(r, 0f, 0f));
                float unclipped = LightMeter.IlluminanceAt(reference, c, Vector3.UnitZ);
                _out.WriteLine($"{frac}: {lux[y * 240 + x]:F1} lx (unclipped {unclipped:F1})");
                if (frac < 0.88f)
                    Assert.InRange(lux[y * 240 + x] / unclipped, 0.97f, 1.03f);
                else
                    Assert.True(lux[y * 240 + x] < 0.02f * unclipped);
            }

            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void VolumetricShadowBlocksTheBeam(bool narrow)
    {
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            var scene = new StageScene(renderer);
            var grey = scene.Lambert(new Vector3(0.5f));
            // A plate 1 m below the light blocks the middle of the beam.
            scene.Box(new Vector3(2f, 2f, 0.05f), new Vector3(0f, 0f, 3f), grey);
            var light = narrow
                ? StageScene.Spot(new Vector3(0f, 0f, 4f), Vector3.Zero, 40000f, 8f, 12f)
                : StageScene.Spot(new Vector3(0f, 0f, 4f), Vector3.Zero, 10000f, 40f, 60f);
            renderer.World.SetLights(new[] { light });
            renderer.World.SetAtmosphere(new AtmosphereState { BaseScattering = 0.02f, Anisotropy = 0.7f });
            using var target = renderer.CreateTarget(160, 120);
            var camera = RenderCamera.LookAt(new Vector3(0f, -6f, 2f), new Vector3(0f, 0f, 2f), 40f);
            camera.Ev100 = 2f;
            var settings = new PreviewSettings { TemporalAccumulation = false, VolumetricSlices = 128, Bloom = false, Indirect = IndirectLighting.Off };
            renderer.Render(target, camera, new ViewOptions { ShowGrid = false, BackgroundLuminance = 0f }, settings, 1f / 60f);
            var shadowed = renderer.ReadHdr(target).LuminanceAt(80, 60);
            settings.Shadows = false;
            renderer.Render(target, camera, new ViewOptions { ShowGrid = false, BackgroundLuminance = 0f }, settings, 1f / 60f);
            var lit = renderer.ReadHdr(target).LuminanceAt(80, 60);
            _out.WriteLine($"narrow {narrow}: shadowed {shadowed}, lit {lit}, raymarched {renderer.LastStatistics.RaymarchedBeams}");
            Assert.True(lit > 0.01f);
            Assert.True(shadowed < 0.05f * lit, $"shadowed {shadowed} vs lit {lit}");
            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void FroxelHistoryIsReprojectedWhenTheCameraMoves(bool rotate)
    {
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            var light = StageScene.Spot(new Vector3(0f, 0f, 4f), Vector3.Zero, 10000f, 40f, 60f, shadows: false);
            renderer.World.SetLights(new[] { light });
            renderer.World.SetAtmosphere(new AtmosphereState { BaseScattering = 0.02f, Anisotropy = 0.7f });
            using var target = renderer.CreateTarget(160, 120);
            var settings = new PreviewSettings { TemporalAccumulation = true, VolumetricSlices = 64, Bloom = false, MaxRaymarchedBeams = 0 };
            RenderCamera Camera(int i)
            {
                var eye = rotate ? new Vector3(0f, -6f, 2f) : new Vector3(-1f + 0.05f * i, -6f, 2f);
                float yaw = rotate ? 0.15f - 0.0075f * i : 0f;
                var cam = RenderCamera.LookAt(eye, eye + new Vector3(MathF.Sin(yaw), MathF.Cos(yaw), 0f), 40f);
                cam.Ev100 = 2f;
                return cam;
            }

            var opts = new ViewOptions { ShowGrid = false, BackgroundLuminance = 0f };
            for (int i = 0; i <= 20; i++)
                renderer.Render(target, Camera(i), opts, settings, 1f / 60f);
            float moving = Centroid(renderer.ReadHdr(target));
            for (int i = 0; i < 30; i++)
                renderer.Render(target, Camera(20), opts, settings, 1f / 60f);
            float still = Centroid(renderer.ReadHdr(target));
            _out.WriteLine($"rotate {rotate}: beam centroid {moving:F2} px moving, {still:F2} px still");
            Assert.InRange(moving - still, -0.75f, 0.75f);
            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    private static float Centroid(HdrReadback hdr)
    {
        double sum = 0, weighted = 0;
        for (int y = 40; y < 110; y++)
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

    [SkippableFact]
    public void RobustToDegenerateSettingsAndInputs()
    {
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            var scene = new StageScene(renderer);
            var grey = scene.Lambert(new Vector3(0.5f));
            scene.Floor(20f, grey);
            scene.Box(Vector3.One, new Vector3(0f, 0f, 0.5f), grey, InstanceFlags.CastShadow | InstanceFlags.Selected);
            using var target = renderer.CreateTarget(64, 48);
            var camera = RenderCamera.LookAt(new Vector3(0f, -6f, 3f), Vector3.Zero, 50f);
            var everything = new PreviewSettings { AutoExposure = true, Indirect = IndirectLighting.RayTraced };
            void All(PreviewSettings s)
            {
                foreach (var overlay in new[] { OverlayMode.None, OverlayMode.Lux, OverlayMode.Noise, OverlayMode.Cables })
                {
                    foreach (var shading in new[] { ViewShading.Full, ViewShading.Colorless, ViewShading.Wireframe })
                        renderer.Render(target, camera, new ViewOptions { Overlay = overlay, Shading = shading, ShowBeamOutlines = true }, s, 1f / 60f);
                }
            }

            // No lights, haze + smoke + flame + noise field.
            var grid = new VolumeGrid { Min = new Vector3(-2f), Max = new Vector3(2f), SizeX = 4, SizeY = 4, SizeZ = 4, Values = Enumerable.Repeat(0.5f, 64).ToArray() };
            var flame = new VolumeGrid { Min = new Vector3(-1f), Max = new Vector3(1f), SizeX = 2, SizeY = 2, SizeZ = 2, Values = Enumerable.Repeat(1500f, 8).ToArray() };
            renderer.World.SetAtmosphere(new AtmosphereState { SmokeDensity = grid, FlameTemperature = flame, NoiseField = grid });
            All(everything);
            // Degenerate settings.
            All(new PreviewSettings
            {
                ResolutionScale = float.NaN, ShadowMapResolution = -5, MaxShadowedLights = int.MaxValue, VolumetricSlices = int.MaxValue,
                MaxRaymarchedBeams = int.MaxValue, BloomIntensity = float.PositiveInfinity, ExposureCompensation = float.NaN, AutoExposure = true,
                Indirect = (IndirectLighting)77, ToneMapper = (ToneMapper)55,
            });
            All(new PreviewSettings { ShadowMapResolution = 1 << 20, VolumetricSlices = -1, MaxRaymarchedBeams = -3, MaxShadowedLights = -1, BloomIntensity = -1f });
            // A light with NaN everything, a zero direction, a light at the camera.
            var lights = new[]
            {
                new SpotLight { Position = new Vector3(float.NaN), Direction = new Vector3(float.NaN), PeakCandela = float.NaN, BeamAngle = float.NaN, FieldAngle = float.NaN, CastShadows = true },
                new SpotLight { Position = new Vector3(0f, 0f, 3f), Direction = Vector3.Zero, Up = Vector3.Zero, PeakCandela = 1000f, BeamAngle = 0.3f, FieldAngle = 0.4f, CastShadows = true, PrismFacets = 1000, PrismSpread = 10f, Iris = 1e-9f, BladeInsertion = new Vector4(1f) },
                new SpotLight { Position = camera.Position, Direction = -Vector3.UnitZ, PeakCandela = 1e9f, BeamAngle = 3.1f, FieldAngle = 6f, CastShadows = true, SourceRadius = 100f, Frost = 5f },
                new SpotLight { Position = new Vector3(0f, 0f, 0.5f), Direction = Vector3.UnitZ, PeakCandela = 1000f, BeamAngle = 1e-7f, FieldAngle = 1e-7f, CastShadows = true },
            };
            renderer.World.SetLights(lights);
            All(everything);
            // Smoke grids: empty, short values, beyond the 3D limit, NaN/inf values, 1x1x1.
            renderer.World.SetAtmosphere(new AtmosphereState { SmokeDensity = new VolumeGrid() });
            All(everything);
            renderer.World.SetAtmosphere(new AtmosphereState { SmokeDensity = new VolumeGrid { Min = Vector3.Zero, Max = Vector3.One, SizeX = 2, SizeY = 2, SizeZ = 2, Values = new float[3] } });
            All(everything);
            int big = (int)renderer.Context.Capabilities.MaxImageDimension3D + 1;
            renderer.World.SetAtmosphere(new AtmosphereState { SmokeDensity = new VolumeGrid { Min = Vector3.Zero, Max = Vector3.One, SizeX = big, SizeY = 1, SizeZ = 1, Values = new float[big] } });
            All(everything);
            renderer.World.SetAtmosphere(new AtmosphereState
            {
                SmokeDensity = new VolumeGrid { Min = new Vector3(-3f), Max = new Vector3(3f), SizeX = 2, SizeY = 1, SizeZ = 1, Values = new[] { float.NaN, float.PositiveInfinity } },
                FlameTemperature = new VolumeGrid { Min = new Vector3(-3f), Max = new Vector3(3f), SizeX = 1, SizeY = 1, SizeZ = 1, Values = new[] { 1e9f } },
                BaseScattering = float.NaN, Anisotropy = float.NaN, SmokeScatteringAtFullDensity = float.PositiveInfinity,
            });
            All(everything);
            renderer.World.SetAtmosphere(null!);
            All(everything);
            var hdr = renderer.ReadHdr(target);
            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableFact]
    public void ThousandShadowedLightsWithEveryFeature()
    {
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            var scene = new StageScene(renderer);
            var grey = scene.Lambert(new Vector3(0.5f));
            scene.Floor(40f, grey);
            scene.Box(Vector3.One, new Vector3(0f, 0f, 0.5f), grey);
            var random = new Random(7);
            var lights = new SpotLight[1000];
            for (int i = 0; i < lights.Length; i++)
            {
                var p = new Vector3(-10f + 20f * random.NextSingle(), -10f + 20f * random.NextSingle(), 3f + 3f * random.NextSingle());
                float beam = 5f + 30f * random.NextSingle();
                lights[i] = StageScene.Spot(p, p with { Z = 0f } + new Vector3(random.NextSingle() - 0.5f, random.NextSingle() - 0.5f, 0f), 2000f + 20000f * random.NextSingle(), beam, beam * 1.5f);
            }

            renderer.World.SetLights(lights);
            renderer.World.SetAtmosphere(new AtmosphereState { BaseScattering = 0.01f });
            using var target = renderer.CreateTarget(96, 72);
            var camera = RenderCamera.LookAt(new Vector3(0f, -14f, 6f), Vector3.Zero, 60f);
            var settings = new PreviewSettings { MaxShadowedLights = 256, MaxRaymarchedBeams = 256, VolumetricSlices = 32 };
            for (int i = 0; i < 3; i++)
                renderer.Render(target, camera, new ViewOptions(), settings, 1f / 60f);
            _out.WriteLine($"{renderer.LastStatistics}");
            settings.Indirect = IndirectLighting.RayTraced;
            for (int i = 0; i < 2; i++)
                renderer.Render(target, camera, new ViewOptions(), settings, 1f / 60f);
            _out.WriteLine($"{renderer.LastStatistics}");
            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }
}

public sealed class ReviewBeamProbes
{
    private readonly Xunit.Abstractions.ITestOutputHelper _out;

    public ReviewBeamProbes(Xunit.Abstractions.ITestOutputHelper output) => _out = output;

    [SkippableTheory]
    [InlineData(true, 16)]
    [InlineData(true, 0)]
    [InlineData(false, 16)]
    public void LookingUpTheBeamMatchesTheIntegral(bool narrow, int maxRaymarched)
    {
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            var light = narrow
                ? StageScene.Spot(new Vector3(0f, 0f, 4f), Vector3.Zero, 40000f, 8f, 12f, shadows: false)
                : StageScene.Spot(new Vector3(0f, 0f, 4f), Vector3.Zero, 10000f, 40f, 60f, shadows: false);
            var eye = narrow ? new Vector3(0f, -0.1f, 1f) : new Vector3(0f, -0.5f, 1f);
            var look = narrow ? new Vector3(0f, 0.3f, 3.5f) : new Vector3(0f, 0.4f, 3.5f);
            renderer.World.SetLights(new[] { light });
            renderer.World.SetAtmosphere(new AtmosphereState { BaseScattering = 0.02f, Anisotropy = 0.7f });
            using var target = renderer.CreateTarget(160, 120);
            var camera = RenderCamera.LookAt(eye, look, 40f);
            camera.Ev100 = 6f;
            var settings = new PreviewSettings { TemporalAccumulation = false, VolumetricSlices = 128, Bloom = false, MaxRaymarchedBeams = maxRaymarched };
            renderer.Render(target, camera, new ViewOptions { ShowGrid = false, BackgroundLuminance = 0f }, settings, 1f / 60f);
            var hdr = renderer.ReadHdr(target);
            float measured = 0.25f * (hdr.LuminanceAt(79, 59) + hdr.LuminanceAt(80, 59) + hdr.LuminanceAt(79, 60) + hdr.LuminanceAt(80, 60));
            var direction = Vector3.Normalize(look - eye);
            var beam = BeamDistribution.Create(light.BeamType, light.BeamAngle, light.FieldAngle);
            double sum = 0;
            const int steps = 400000;
            const double length = 60;
            double dl = length / steps;
            for (int i = 0; i < steps; i++)
            {
                double l = (i + 0.5) * dl;
                var x = eye + direction * (float)l;
                var toPoint = x - light.Position;
                float r = toPoint.Length();
                float theta = MathF.Atan2(Vector3.Cross(light.Direction, toPoint).Length(), Vector3.Dot(light.Direction, toPoint));
                float intensity = light.PeakCandela * beam.Relative(theta);
                if (intensity <= 0f)
                    continue;
                float cos = Vector3.Dot(toPoint / r, -direction);
                double g = 0.7;
                double phase = (1 - g * g) / (4 * Math.PI * Math.Pow(1 + g * g - 2 * g * cos, 1.5));
                sum += 0.02 * phase * intensity * Math.Exp(-0.02 * r) / (r * r) * Math.Exp(-0.02 * l) * dl;
            }

            _out.WriteLine($"narrow {narrow}, raymarched {renderer.LastStatistics.RaymarchedBeams}: measured {measured:F3}, reference {sum:F3}, ratio {measured / sum:F3}");
            ImageFiles.WritePng(renderer.ReadPixels(target), Path.Combine(TestRenderers.OutputDirectory, $"up-beam-{narrow}-{maxRaymarched}.png"));
            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }
}

public sealed class ReviewShadowProbes
{
    private readonly Xunit.Abstractions.ITestOutputHelper _out;

    public ReviewShadowProbes(Xunit.Abstractions.ITestOutputHelper output) => _out = output;

    [SkippableTheory]
    [InlineData(0f)]
    [InlineData(0.15f)]
    public void LitSphereHasNoShadowAcne(float sourceRadius)
    {
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            var scene = new StageScene(renderer);
            var grey = scene.Lambert(new Vector3(0.5f));
            scene.Floor(20f, grey);
            var centre = new Vector3(0f, 0f, 1f);
            const float radius = 1f;
            scene.Sphere(radius, centre, grey);
            var light = StageScene.Spot(new Vector3(-3f, -1f, 6f), centre, 50000f, 40f, 60f);
            light.SourceRadius = sourceRadius;
            renderer.World.SetLights(new[] { light });
            var view = new TopDownView(200, 200, 3f);
            using var target = renderer.CreateTarget(200, 200);
            renderer.Render(target, view.Camera, new ViewOptions { ShowGrid = false, Overlay = OverlayMode.Lux }, new PreviewSettings(), 0f);
            var lux = renderer.ReadAnalysis(target);
            TestVulkan.AssertNoValidationErrors(renderer.Context);
            var reference = new[] { new Photometry.SpotLight(light.Position, light.Direction, light.PeakCandela,
                BeamDistribution.Create(light.BeamType, light.BeamAngle, light.FieldAngle)) };
            int n = 0, bad = 0;
            double worst = 0;
            for (int y = 0; y < 200; y += 2)
            {
                for (int x = 0; x < 200; x += 2)
                {
                    var floor = view.FloorPointOf(x, y);
                    float r2 = (floor.X - centre.X) * (floor.X - centre.X) + (floor.Y - centre.Y) * (floor.Y - centre.Y);
                    if (r2 > 0.8f * 0.8f)
                        continue;
                    var p = new Vector3(floor.X, floor.Y, centre.Z + MathF.Sqrt(radius * radius - r2));
                    var normal = Vector3.Normalize(p - centre);
                    var toLight = Vector3.Normalize(light.Position - p);
                    if (Vector3.Dot(normal, toLight) < 0.35f)
                        continue;
                    float expected = LightMeter.IlluminanceAt(reference, p, normal);
                    float measured = lux[y * 200 + x];
                    double rel = Math.Abs(measured - expected) / expected;
                    worst = Math.Max(worst, rel);
                    n++;
                    if (rel > 0.04)
                        bad++;
                }
            }

            _out.WriteLine($"source radius {sourceRadius}: {bad} of {n} off by > 4 %, worst {worst:P1}");
            Assert.True(n > 100);
            Assert.True(bad == 0, $"{bad} of {n} lit sphere points off by more than 4 % (worst {worst:P1})");
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }
}

public sealed class ReviewExperiments
{
    private readonly Xunit.Abstractions.ITestOutputHelper _out;

    public ReviewExperiments(Xunit.Abstractions.ITestOutputHelper output) => _out = output;

    [SkippableTheory]
    [InlineData(0.0f)]
    [InlineData(0.001f)]
    [InlineData(0.005f)]
    [InlineData(0.02f)]
    public void RotationSpeedVsError(float speed)
    {
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            var light = StageScene.Spot(new Vector3(0f, 0f, 4f), Vector3.Zero, 10000f, 40f, 60f, shadows: false);
            renderer.World.SetLights(new[] { light });
            renderer.World.SetAtmosphere(new AtmosphereState { BaseScattering = 0.02f, Anisotropy = 0.7f });
            using var target = renderer.CreateTarget(160, 120);
            var settings = new PreviewSettings { TemporalAccumulation = true, VolumetricSlices = 128, Bloom = false, MaxRaymarchedBeams = 0 };
            var still = new PreviewSettings { TemporalAccumulation = false, VolumetricSlices = 128, Bloom = false, MaxRaymarchedBeams = 0 };
            RenderCamera Camera(int i)
            {
                var eye = new Vector3(0f, -6f, 2f);
                float a = 15 * speed - speed * i;
                var cam = RenderCamera.LookAt(eye, eye + new Vector3(MathF.Sin(a), MathF.Cos(a), 0f), 40f);
                cam.Ev100 = 2f;
                return cam;
            }

            var opts = new ViewOptions { ShowGrid = false, BackgroundLuminance = 0f };
            for (int i = 0; i <= 15; i++)
                renderer.Render(target, Camera(i), opts, settings, 1f / 60f);
            var moving = renderer.ReadHdr(target);
            ImageFiles.WritePng(renderer.ReadPixels(target), Path.Combine(TestRenderers.OutputDirectory, $"rot-{speed}-moving.png"));
            renderer.Render(target, Camera(15), opts, still, 1f / 60f);
            var reference = renderer.ReadHdr(target);
            ImageFiles.WritePng(renderer.ReadPixels(target), Path.Combine(TestRenderers.OutputDirectory, $"rot-{speed}-still.png"));
            double sumM = 0, sumR = 0;
            for (int y = 0; y < 120; y++)
            {
                for (int x = 0; x < 160; x++)
                {
                    sumM += moving.LuminanceAt(x, y);
                    sumR += reference.LuminanceAt(x, y);
                }
            }

            var row = new System.Text.StringBuilder();
            for (int x = 0; x < 160; x += 8)
                row.Append($"{moving.LuminanceAt(x, 60):F2}/{reference.LuminanceAt(x, 60):F2} ");
            _out.WriteLine($"speed {speed}: ratio {sumM / sumR:F3}; row: {row}");
            var col = new System.Text.StringBuilder();
            for (int y = 0; y < 120; y += 8)
                col.Append($"{moving.LuminanceAt(80, y):F2}/{reference.LuminanceAt(80, y):F2} ");
            _out.WriteLine($"col: {col}");
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }
}
