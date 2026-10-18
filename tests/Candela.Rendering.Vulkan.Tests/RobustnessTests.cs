using System.Numerics;

namespace Candela.Rendering.Vulkan.Tests;

/// <summary>
/// Inputs at and beyond the edges: degenerate settings, lights and atmosphere grids in every view and overlay, a
/// thousand shadow-casting lights with every feature on, and light bright enough to overflow half floats.
/// </summary>
public sealed class RobustnessTests
{
    [SkippableFact]
    public void DegenerateSettingsLightsAndGridsRenderCleanly()
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
            void EveryView(PreviewSettings settings)
            {
                foreach (var overlay in new[] { OverlayMode.None, OverlayMode.Lux, OverlayMode.Noise, OverlayMode.Cables })
                {
                    foreach (var shading in new[] { ViewShading.Full, ViewShading.Colorless, ViewShading.Wireframe })
                        renderer.Render(target, camera, new ViewOptions { Overlay = overlay, Shading = shading, ShowBeamOutlines = true }, settings, 1f / 60f);
                }
            }

            // No lights, but haze, smoke, a flame and a noise field.
            var grid = new VolumeGrid { Min = new Vector3(-2f), Max = new Vector3(2f), SizeX = 4, SizeY = 4, SizeZ = 4, Values = Enumerable.Repeat(0.5f, 64).ToArray() };
            var flame = new VolumeGrid { Min = new Vector3(-1f), Max = new Vector3(1f), SizeX = 2, SizeY = 2, SizeZ = 2, Values = Enumerable.Repeat(1500f, 8).ToArray() };
            renderer.World.SetAtmosphere(new AtmosphereState { SmokeDensity = grid, FlameTemperature = flame, NoiseField = grid });
            EveryView(everything);

            // Settings out of range or not finite.
            EveryView(new PreviewSettings
            {
                ResolutionScale = float.NaN,
                ShadowMapResolution = -5,
                MaxShadowedLights = int.MaxValue,
                VolumetricSlices = int.MaxValue,
                MaxRaymarchedBeams = int.MaxValue,
                BloomIntensity = float.PositiveInfinity,
                ExposureCompensation = float.NaN,
                AutoExposure = true,
                Indirect = (IndirectLighting)77,
                ToneMapper = (ToneMapper)55,
            });
            EveryView(new PreviewSettings { ShadowMapResolution = 1 << 20, VolumetricSlices = -1, MaxRaymarchedBeams = -3, MaxShadowedLights = -1, BloomIntensity = -1f });

            // Lights: all NaN, a zero direction with every cookie at its limit, one at the camera, a degenerate cone.
            renderer.World.SetLights(new[]
            {
                new SpotLight
                {
                    Position = new Vector3(float.NaN), Direction = new Vector3(float.NaN), PeakCandela = float.NaN, BeamAngle = float.NaN,
                    FieldAngle = float.NaN, CastShadows = true,
                },
                new SpotLight
                {
                    Position = new Vector3(0f, 0f, 3f), Direction = Vector3.Zero, Up = Vector3.Zero, PeakCandela = 1000f, BeamAngle = 0.3f, FieldAngle = 0.4f,
                    CastShadows = true, PrismFacets = 1000, PrismSpread = 10f, Iris = 1e-9f, BladeInsertion = new Vector4(1f), GoboBlur = 7f,
                },
                new SpotLight
                {
                    Position = camera.Position, Direction = -Vector3.UnitZ, PeakCandela = 1e9f, BeamAngle = 3.1f, FieldAngle = 6f, CastShadows = true,
                    SourceRadius = 100f, Frost = 5f,
                },
                new SpotLight { Position = new Vector3(0f, 0f, 0.5f), Direction = Vector3.UnitZ, PeakCandela = 1000f, BeamAngle = 1e-7f, FieldAngle = 1e-7f, CastShadows = true },
            });
            EveryView(everything);

            // Grids: empty, too few values, beyond the device's 3D image limit, NaN and infinite values, 1 x 1 x 1.
            renderer.World.SetAtmosphere(new AtmosphereState { SmokeDensity = new VolumeGrid() });
            EveryView(everything);
            renderer.World.SetAtmosphere(new AtmosphereState
            {
                SmokeDensity = new VolumeGrid { Min = Vector3.Zero, Max = Vector3.One, SizeX = 2, SizeY = 2, SizeZ = 2, Values = new float[3] },
            });
            EveryView(everything);
            int big = (int)renderer.Context.Capabilities.MaxImageDimension3D + 1;
            renderer.World.SetAtmosphere(new AtmosphereState
            {
                SmokeDensity = new VolumeGrid { Min = Vector3.Zero, Max = Vector3.One, SizeX = big, SizeY = 1, SizeZ = 1, Values = new float[big] },
            });
            EveryView(everything);
            renderer.World.SetAtmosphere(new AtmosphereState
            {
                SmokeDensity = new VolumeGrid { Min = new Vector3(-3f), Max = new Vector3(3f), SizeX = 2, SizeY = 1, SizeZ = 1, Values = new[] { float.NaN, float.PositiveInfinity } },
                FlameTemperature = new VolumeGrid { Min = new Vector3(-3f), Max = new Vector3(3f), SizeX = 1, SizeY = 1, SizeZ = 1, Values = new[] { 1e9f } },
                BaseScattering = float.NaN,
                Anisotropy = float.NaN,
                SmokeScatteringAtFullDensity = float.PositiveInfinity,
            });
            EveryView(everything);
            renderer.World.SetAtmosphere(null!);
            EveryView(everything);

            renderer.Render(target, camera, new ViewOptions(), everything, 1f / 60f);
            Assert.All(renderer.ReadHdr(target).Rgba, v => Assert.True(float.IsFinite(v)));
            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableFact]
    public void ThousandShadowCastingLightsWithEveryFeature()
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
                var aim = p with { Z = 0f } + new Vector3(random.NextSingle() - 0.5f, random.NextSingle() - 0.5f, 0f);
                lights[i] = StageScene.Spot(p, aim, 2000f + 20000f * random.NextSingle(), beam, beam * 1.5f);
            }

            renderer.World.SetLights(lights);
            renderer.World.SetAtmosphere(new AtmosphereState { BaseScattering = 0.01f });
            using var target = renderer.CreateTarget(96, 72);
            var camera = RenderCamera.LookAt(new Vector3(0f, -14f, 6f), Vector3.Zero, 60f);
            var settings = new PreviewSettings { MaxShadowedLights = 256, MaxRaymarchedBeams = 256, VolumetricSlices = 32 };
            for (int i = 0; i < 2; i++)
                renderer.Render(target, camera, new ViewOptions(), settings, 1f / 60f);
            var stats = renderer.LastStatistics;
            Assert.Equal(256, stats.ShadowedLights); // the atlas holds every planned tile
            Assert.InRange(stats.VirtualPointLights, 1, LightPlanner.VplBudget);
            Assert.True(stats.RaymarchedBeams > 0);
            settings.Indirect = IndirectLighting.RayTraced;
            renderer.Render(target, camera, new ViewOptions(), settings, 1f / 60f);
            Assert.Equal(256, renderer.LastStatistics.ShadowedLights);
            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableTheory]
    [InlineData(25f, -2f)]
    [InlineData(40f, 0f)]
    public void LightBrightEnoughToOverflowHalfFloatsStaysFinite(float beamDeg, float ev)
    {
        // 2 Mcd aimed at the camera through dense haze at a low exposure: the in-scattering near the lens exceeds
        // the half-float range. Every light image is clamped below it (an overflow may become +infinity on GPUs and
        // then NaN in blending, the bloom pyramid and the temporal histories).
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            var scene = new StageScene(renderer);
            scene.Floor(30f, scene.Lambert(new Vector3(0.3f)));
            var light = StageScene.Spot(new Vector3(0f, 6f, 3f), new Vector3(0f, -2f, 1.6f), 2e6f, beamDeg, beamDeg * 1.5f);
            renderer.World.SetLights(new[] { light });
            renderer.World.SetAtmosphere(new AtmosphereState { BaseScattering = 0.15f, Anisotropy = 0.7f });
            using var target = renderer.CreateTarget(160, 120);
            var camera = RenderCamera.LookAt(new Vector3(0f, -2f, 1.6f), light.Position, 50f);
            camera.Ev100 = ev;
            var settings = new PreviewSettings { VolumetricSlices = 64, AutoExposure = false };
            for (int i = 0; i < 4; i++)
                renderer.Render(target, camera, new ViewOptions { ShowGrid = false }, settings, 1f / 60f);
            var hdr = renderer.ReadHdr(target);
            float maxExposed = hdr.Rgba.Where((_, i) => (i & 3) != 3).Max() * hdr.Exposure;
            Assert.True(maxExposed < 65000f, $"pre-exposed HDR reaches {maxExposed} (half-float limit 65504)");
            Assert.All(hdr.Rgba, v => Assert.True(float.IsFinite(v)));
            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }
}
