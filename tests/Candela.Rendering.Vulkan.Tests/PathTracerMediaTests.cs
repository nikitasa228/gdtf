using System.Numerics;
using Candela.Core.Geometry;
using Candela.Photometry;
using Xunit.Abstractions;

namespace Candela.Rendering.Vulkan.Tests;

/// <summary>
/// Light in haze, smoke and flames: in the single-scattering limit a beam's in-scattered luminance equals the numerical
/// integral of σ·HG·I/d²·T (and the real-time volumetric beam); a gobo cuts the beam in the air like the reference;
/// smoke in the density grid scatters like haze of the same coefficient (delta / ratio tracking against the analytic
/// haze); multiple scattering in dense haze adds light (a halo around the beam) without creating energy; an optically
/// thick flame shows its blackbody luminance and colour.
/// </summary>
public sealed class PathTracerMediaTests
{
    private const int Width = 32;
    private const int Height = 24;
    private const float Haze = 0.02f;
    private const float Anisotropy = 0.7f;
    private static readonly Vector3 LightPosition = new(0f, 0f, 4f);
    private static readonly Vector3 Eye = new(0f, -6f, 2f);

    private readonly ITestOutputHelper _output;

    public PathTracerMediaTests(ITestOutputHelper output) => _output = output;

    private static RenderCamera SideCamera(float fovY = 40f)
    {
        var camera = RenderCamera.LookAt(Eye, Eye + Vector3.UnitY, fovY);
        camera.Ev100 = 2f;
        return camera;
    }

    /// <summary>Mean luminance of the four centre pixels and the matching mean of the reference over their centre rays.</summary>
    private static (float Measured, double Expected) Centre(HdrImage hdr, RenderCamera camera, Func<Vector3, Vector3, double> reference)
    {
        float measured = 0f;
        double expected = 0;
        for (int y = Height / 2 - 1; y <= Height / 2; y++)
        {
            for (int x = Width / 2 - 1; x <= Width / 2; x++)
            {
                measured += 0.25f * hdr.LuminanceAt(x, y);
                var (o, d) = BeamReference.PixelRay(camera, Width, Height, x, y);
                expected += 0.25 * reference(o, d);
            }
        }

        return (measured, expected);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void BeamInHazeMatchesTheSingleScatteringIntegral(bool narrow)
    {
        var light = narrow
            ? StageScene.Spot(LightPosition, Vector3.Zero, 100000f, 6f, 9f, shadows: false, type: BeamType.Spot)
            : StageScene.Spot(LightPosition, Vector3.Zero, 10000f, 40f, 60f, shadows: false);
        var renderer = FinalRenderSupport.CreateOrSkip();
        try
        {
            renderer.World.SetLights(new[] { light });
            renderer.World.SetAtmosphere(new AtmosphereState { BaseScattering = Haze, Anisotropy = Anisotropy });
            var camera = SideCamera(narrow ? 10f : 40f);
            var settings = FinalRenderSupport.Settings(Width, Height, narrow ? 512 : 256, bounces: 0, volumeBounces: 1);
            using var render = FinalRenderSupport.RenderToCompletion(renderer, camera, settings, new FinalRenderOptions { BackgroundLuminance = 0f });
            var hdr = FinalRenderSupport.Luminance(render.RawResultHdr(), render.Exposure);
            var (measured, expected) = Centre(hdr, camera, (o, d) => BeamReference.InScattering(light, o, d, Haze, Anisotropy, steps: 20000));

            // The real-time volumetric beam of the same light (centre of a 160 x 120 view of the same camera).
            float realTime;
            using (var target = renderer.CreateTarget(160, 120))
            {
                renderer.Render(target, camera, new ViewOptions { ShowGrid = false, BackgroundLuminance = 0f },
                    new PreviewSettings { TemporalAccumulation = false, Bloom = false }, 0f);
                var rt = renderer.ReadHdr(target);
                realTime = 0.25f * (rt.LuminanceAt(79, 59) + rt.LuminanceAt(80, 59) + rt.LuminanceAt(79, 60) + rt.LuminanceAt(80, 60));
            }

            _output.WriteLine($"path tracer {measured:0.0000} cd/m², integral {expected:0.0000} cd/m², real time {realTime:0.0000} cd/m²");
            Assert.True(expected > 0.05, $"reference {expected}");
            Assert.InRange(Math.Abs(measured - expected) / expected, 0, 0.03);
            Assert.InRange(Math.Abs(measured - realTime) / realTime, 0, 0.06);
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
        var renderer = FinalRenderSupport.CreateOrSkip();
        try
        {
            var light = StageScene.Spot(LightPosition, Vector3.Zero, 30000f, 20f, 30f, shadows: false, type: BeamType.Spot);
            light.Gobo = renderer.World.CreateTexture(HalfGobo(), srgb: true);
            renderer.World.SetLights(new[] { light });
            renderer.World.SetAtmosphere(new AtmosphereState { BaseScattering = Haze, Anisotropy = Anisotropy });
            var camera = SideCamera();
            using var render = FinalRenderSupport.RenderToCompletion(renderer, camera, FinalRenderSupport.Settings(Width, Height, 1024, 0, 1),
                new FinalRenderOptions { BackgroundLuminance = 0f });
            var hdr = FinalRenderSupport.Luminance(render.RawResultHdr(), render.Exposure);
            float gate = MathF.Tan(0.5f * light.FieldAngle);
            var right = Vector3.Cross(light.Direction, light.Up);
            double Cookie(Vector3 x, Vector3 w)
            {
                float z = Vector3.Dot(w, light.Direction);
                float px = Vector3.Dot(w, right) / z / gate, py = Vector3.Dot(w, light.Up) / z / gate;
                return py > 0f && MathF.Abs(px) <= 1f && py <= 1f ? 1.0 : 0.0;
            }

            var (measured, expected) = Centre(hdr, camera, (o, d) => BeamReference.InScattering(light, o, d, Haze, Anisotropy, modulation: Cookie, steps: 20000));
            var (_, open) = Centre(hdr, camera, (o, d) => BeamReference.InScattering(light, o, d, Haze, Anisotropy, steps: 20000));
            _output.WriteLine($"path tracer {measured:0.0000}, integral {expected:0.0000}, without gobo {open:0.0000} cd/m²");
            Assert.True(expected < 0.7 * open && expected > 0.1 * open, "the gobo blocks a substantial part");
            Assert.InRange(Math.Abs(measured - expected) / expected, 0, 0.04);
            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableFact]
    public void SmokeScattersLikeHazeOfTheSameCoefficient()
    {
        var renderer = FinalRenderSupport.CreateOrSkip();
        try
        {
            var light = StageScene.Spot(LightPosition, Vector3.Zero, 10000f, 40f, 60f, shadows: false);
            renderer.World.SetLights(new[] { light });
            var camera = SideCamera();
            var settings = FinalRenderSupport.Settings(Width, Height, 1024, 0, 1);
            var options = new FinalRenderOptions { BackgroundLuminance = 0f };
            const float sigma = 0.05f;
            renderer.World.SetAtmosphere(new AtmosphereState { BaseScattering = sigma, Anisotropy = Anisotropy });
            float haze;
            using (var render = FinalRenderSupport.RenderToCompletion(renderer, camera, settings, options))
                haze = Centre(FinalRenderSupport.Luminance(render.RawResultHdr(), render.Exposure), camera, (_, _) => 0).Measured;

            // The same coefficient as a smoke grid of density 0.5 (σ_smoke = 0.1 /m) around the camera and the beam.
            var grid = new VolumeGrid
            {
                Min = new Vector3(-8f, -8f, -1f),
                Max = new Vector3(8f, 8f, 6f),
                SizeX = 4,
                SizeY = 4,
                SizeZ = 4,
                Values = Enumerable.Repeat(0.5f, 64).ToArray(),
            };
            renderer.World.SetAtmosphere(new AtmosphereState
            {
                BaseScattering = 0f,
                Anisotropy = Anisotropy,
                SmokeDensity = grid,
                SmokeScatteringAtFullDensity = 2f * sigma,
            });
            float smoke;
            using (var render = FinalRenderSupport.RenderToCompletion(renderer, camera, settings, options))
                smoke = Centre(FinalRenderSupport.Luminance(render.RawResultHdr(), render.Exposure), camera, (_, _) => 0).Measured;

            double expected = 0;
            for (int y = Height / 2 - 1; y <= Height / 2; y++)
            {
                for (int x = Width / 2 - 1; x <= Width / 2; x++)
                {
                    var (o, d) = BeamReference.PixelRay(camera, Width, Height, x, y);
                    expected += 0.25 * BeamReference.InScattering(light, o, d, sigma, Anisotropy, steps: 20000);
                }
            }

            _output.WriteLine($"haze {haze:0.0000}, smoke {smoke:0.0000}, integral {expected:0.0000} cd/m²");
            Assert.InRange(Math.Abs(haze - expected) / expected, 0, 0.03);
            Assert.InRange(Math.Abs(smoke - expected) / expected, 0, 0.04);
            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableFact]
    public void MultipleScatteringAddsAHaloInDenseSmoke()
    {
        var renderer = FinalRenderSupport.CreateOrSkip();
        try
        {
            // A 10° beam through a dense cloud of smoke (4 x 4 x 5 m, mean free path 1.7 m) in clear air, onto a black
            // floor, seen from the side.
            var light = StageScene.Spot(new Vector3(0f, 0f, 6f), Vector3.Zero, 50000f, 10f, 15f, shadows: false, type: BeamType.Spot);
            renderer.World.SetLights(new[] { light });
            FinalRenderSupport.Floor(renderer.World, new MaterialData { Name = "black", BaseColorFactor = new Vector4(0f, 0f, 0f, 1f), Ior = 1f, Roughness = 1f });
            var cloud = new VolumeGrid
            {
                Min = new Vector3(-2f, -2f, 0f),
                Max = new Vector3(2f, 2f, 5f),
                SizeX = 4,
                SizeY = 4,
                SizeZ = 4,
                Values = Enumerable.Repeat(1f, 64).ToArray(),
            };
            renderer.World.SetAtmosphere(new AtmosphereState { BaseScattering = 0f, Anisotropy = 0.5f, SmokeDensity = cloud, SmokeScatteringAtFullDensity = 0.6f });
            var camera = RenderCamera.LookAt(new Vector3(0f, -9f, 2.5f), new Vector3(0f, 0f, 2.5f), 50f);
            camera.Ev100 = 4f;
            var options = new FinalRenderOptions { BackgroundLuminance = 0f };
            HdrImage Render(int volumeBounces)
            {
                using var render = FinalRenderSupport.RenderToCompletion(renderer, camera, FinalRenderSupport.Settings(48, 36, 128, 0, volumeBounces), options);
                FinalRenderSupport.Save(render, $"pt-dense-smoke-{volumeBounces}");
                return FinalRenderSupport.Luminance(render.RawResultHdr(), render.Exposure);
            }

            var single = Render(1);
            var two = Render(2);
            var three = Render(3);
            var multiple = Render(16);
            double Total(HdrImage image)
            {
                double sum = 0;
                for (int y = 0; y < image.Height; y++)
                {
                    for (int x = 0; x < image.Width; x++)
                        sum += image.LuminanceAt(x, y);
                }

                return sum;
            }

            double t1 = Total(single), t2 = Total(two), t3 = Total(three), t16 = Total(multiple);
            // In the smoke beside the beam (1.5 m to the side, at the camera's height) only multiply scattered light arrives.
            float beside = 0.5f * (single.MeanLuminance(17, 18, 1) + single.MeanLuminance(30, 18, 1));
            float besideMultiple = 0.5f * (multiple.MeanLuminance(17, 18, 1) + multiple.MeanLuminance(30, 18, 1));
            float axis = single.MeanLuminance(24, 18, 1);
            _output.WriteLine($"image totals for 1, 2, 3, 16 scattering events: {t1:0.0}, {t2:0.0}, {t3:0.0}, {t16:0.0}; " +
                              $"beside the beam {beside:0.0000} -> {besideMultiple:0.0000}; axis {axis:0.000}");
            Assert.True(beside < 0.02f * axis, "single scattering stays inside the cone");
            Assert.True(besideMultiple > 3f * MathF.Max(beside, 1e-6f) && besideMultiple > 0.01f * axis, "multiple scattering lights the smoke around the beam");
            // Every further order of scattering adds light, and less than the order before (light leaks out of the
            // cloud into the clear air and the black floor): a converging series, no energy is created.
            Assert.True(t2 > t1 * 1.05 && t3 > t2 && t16 > t3, "more scattering events add light");
            Assert.True(t3 - t2 < t2 - t1, "each order of scattering adds less than the one before");
            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableFact]
    public void OpticallyThickFlameShowsItsBlackbodyLuminance()
    {
        var renderer = FinalRenderSupport.CreateOrSkip();
        try
        {
            const float kelvin = 1500f;
            var values = new float[8 * 8 * 8];
            Array.Fill(values, kelvin);
            var grid = new VolumeGrid { Min = new Vector3(-1.5f, -1.5f, -0.5f), Max = new Vector3(1.5f, 1.5f, 2.5f), SizeX = 8, SizeY = 8, SizeZ = 8, Values = values };
            renderer.World.SetAtmosphere(new AtmosphereState { BaseScattering = 0f, FlameTemperature = grid });
            var camera = RenderCamera.LookAt(new Vector3(0f, -6f, 1f), new Vector3(0f, 0f, 1f), 30f);
            camera.Ev100 = 15f;
            using var render = FinalRenderSupport.RenderToCompletion(renderer, camera, FinalRenderSupport.Settings(Width, Height, 64),
                new FinalRenderOptions { BackgroundLuminance = 0f });
            var hdr = FinalRenderSupport.Luminance(render.RawResultHdr(), render.Exposure);
            // 3 m of flame with kappa = 2 /m: 1 - e^-6 of the blackbody luminance, with its colour.
            float expected = (float)BlackbodyRadiator.Luminance(kelvin) * (1f - MathF.Exp(-VulkanRenderer.FlameAbsorption * 3f));
            float measured = hdr.MeanLuminance(Width / 2, Height / 2, 1);
            _output.WriteLine($"flame {measured:0.0} cd/m², blackbody {expected:0.0} cd/m²");
            Assert.InRange(MathF.Abs(measured - expected) / expected, 0f, 0.03f);
            var xy = LightColor.FromRec2020(hdr.MeanColor(Width / 2, Height / 2, 1)).ToXy();
            Assert.InRange(Vector2.Distance(xy, BlackbodyRadiator.Chromaticity(kelvin)), 0f, 0.01f);
            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    /// <summary>
    /// Thin smoke filling a 20 m hall with one very dense cell near a fog nozzle (density 200, far from the view ray):
    /// the grid's majorant is set by that cell, so tracking takes thousands of tentative collisions along the ray, and
    /// the transmittance must still be that of the whole 20 m of thin smoke (no light leaking through what the tracking
    /// did not reach), with and without volume bounces.
    /// </summary>
    [SkippableTheory]
    [InlineData(0)]
    [InlineData(1)]
    public void SparseDenseSmokeDimsTheViewAlongItsWholeLength(int volumeBounces)
    {
        var renderer = FinalRenderSupport.CreateOrSkip();
        try
        {
            const int n = 8;
            var values = Enumerable.Repeat(0.05f, n * n * n).ToArray();
            values[0] = 200f; // the corner cell at (-10, -4, -2)
            var grid = new VolumeGrid { Min = new Vector3(-10f, -4f, -2f), Max = new Vector3(10f, 4f, 6f), SizeX = n, SizeY = n, SizeZ = n, Values = values };
            const float sigma = 0.5f;
            renderer.World.SetAtmosphere(new AtmosphereState { BaseScattering = 0f, Anisotropy = 0f, SmokeDensity = grid, SmokeScatteringAtFullDensity = sigma });
            // a bright emissive panel (1.5 m square) behind the hall, seen along the hall's length
            var scene = new StageScene(renderer);
            var panel = renderer.World.CreateMaterial(new MaterialData
            {
                Name = "panel",
                BaseColorFactor = new Vector4(0f, 0f, 0f, 1f),
                EmissiveLuminance = 100f,
                Ior = 1f,
                Roughness = 1f,
            }, null);
            scene.Box(new Vector3(0.1f, 1.5f, 1.5f), new Vector3(11f, 0f, 2f), panel);
            var camera = RenderCamera.LookAt(new Vector3(-10.5f, 0f, 2f), new Vector3(11f, 0f, 2f), 2f);
            camera.Ev100 = 6f;
            using var render = FinalRenderSupport.RenderToCompletion(renderer, camera, FinalRenderSupport.Settings(8, 8, 64, bounces: 0, volumeBounces: volumeBounces),
                new FinalRenderOptions { BackgroundLuminance = 0f });
            var hdr = FinalRenderSupport.Luminance(render.RawResultHdr(), render.Exposure);
            float measured = hdr.MeanLuminance(4, 4, 2);
            // 20 m of smoke with σ = 0.05 x 0.5 /m: the panel is seen through e^-0.5 (scattering out of the view ray; with
            // a volume bounce, isotropic scattering of the panel's light back into the view ray adds a little)
            float expected = 100f * MathF.Exp(-0.05f * sigma * 20f);
            _output.WriteLine($"{volumeBounces} volume bounces: {measured:0.00} cd/m², transmitted {expected:0.00} cd/m²");
            Assert.InRange(measured, expected * 0.97f, expected * (volumeBounces == 0 ? 1.03f : 1.06f));
            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

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
}
