using System.Numerics;
using Candela.Core.Geometry;
using Candela.Photometry;
using Xunit.Abstractions;

namespace Candela.Rendering.Vulkan.Tests;

/// <summary>
/// Single scattering in the harder geometries: a camera standing inside a beam aimed at it (strongly forward-scattering
/// haze, rays passing close to the light), and a prism splitting a beam into facets. Both against the numerical
/// integral of σ·HG·I/d²·T over the view ray.
/// </summary>
public sealed class PathTracerBeamProbeTests
{
    private const int Width = 24;
    private const int Height = 18;
    private const float Haze = 0.02f;
    private const float Anisotropy = 0.7f;

    private readonly ITestOutputHelper _output;

    public PathTracerBeamProbeTests(ITestOutputHelper output) => _output = output;

    private (float Measured, double Expected) RenderCentre(VulkanRenderer renderer, RenderCamera camera, int samples, Func<Vector3, Vector3, double> reference,
        string name)
    {
        var settings = FinalRenderSupport.Settings(Width, Height, samples, bounces: 0, volumeBounces: 1);
        using var render = FinalRenderSupport.RenderToCompletion(renderer, camera, settings, new FinalRenderOptions { BackgroundLuminance = 0f, Seed = 2 });
        FinalRenderSupport.Save(render, name);
        var hdr = FinalRenderSupport.Luminance(render.RawResultHdr(), render.Exposure);
        float measured = 0f;
        double expected = 0;
        for (int y = Height / 2 - 2; y < Height / 2 + 2; y++)
        {
            for (int x = Width / 2 - 2; x < Width / 2 + 2; x++)
            {
                measured += hdr.LuminanceAt(x, y) / 16f;
                // the mean over the pixel (2 x 2 rays), as the path tracer's jittered samples see it
                for (int j = 0; j < 2; j++)
                {
                    for (int i = 0; i < 2; i++)
                    {
                        var (o, d) = BeamReference.PixelRay(camera, Width, Height, x - 0.25f + 0.5f * i, y - 0.25f + 0.5f * j);
                        expected += reference(o, d) / 64.0;
                    }
                }
            }
        }

        return (measured, expected);
    }

    [SkippableFact]
    public void CameraInsideABeamAimedAtItSeesTheIntegral()
    {
        var renderer = FinalRenderSupport.CreateOrSkip();
        try
        {
            var eye = new Vector3(0f, -6f, 2f);
            var light = StageScene.Spot(new Vector3(0f, 0f, 3f), eye, 20000f, 30f, 45f, shadows: false);
            light.SourceRadius = 0.001f;
            renderer.World.SetLights(new[] { light });
            renderer.World.SetAtmosphere(new AtmosphereState { BaseScattering = Haze, Anisotropy = Anisotropy });
            // looking past the light, 0.4 m beside it: the view rays run inside the cone towards its apex
            var camera = RenderCamera.LookAt(eye, new Vector3(0.4f, 0f, 3f), 6f);
            camera.Ev100 = 4f;
            var (measured, expected) = RenderCentre(renderer, camera, 512,
                (o, d) => BeamReference.InScattering(light, o, d, Haze, Anisotropy, tMax: 20, steps: 40000), "pt-beam-towards-camera");
            _output.WriteLine($"camera inside the beam: path tracer {measured:0.000} cd/m², integral {expected:0.000} cd/m²");
            Assert.True(expected > 1.0, $"reference {expected}");
            Assert.InRange(Math.Abs(measured - expected) / expected, 0, 0.03);
            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableFact]
    public void PrismBeamsInHazeMatchTheIntegral()
    {
        var renderer = FinalRenderSupport.CreateOrSkip();
        try
        {
            var light = StageScene.Spot(new Vector3(0f, 0f, 4f), Vector3.Zero, 60000f, 8f, 11f, shadows: false, type: BeamType.Spot);
            light.SourceRadius = 0.001f;
            light.PrismFacets = 3;
            light.PrismSpread = 0.2f;
            light.PrismRotation = 0.3f;
            renderer.World.SetLights(new[] { light });
            renderer.World.SetAtmosphere(new AtmosphereState { BaseScattering = Haze, Anisotropy = Anisotropy });
            var eye = new Vector3(0f, -6f, 2f);
            var camera = RenderCamera.LookAt(eye, new Vector3(0f, 0f, 2f), 30f);
            camera.Ev100 = 2f;
            var (measured, expected) = RenderCentre(renderer, camera, 512,
                (o, d) => BeamReference.InScatteringPacked(light, o, d, Haze, Anisotropy, tMax: 20, steps: 20000), "pt-prism-haze");
            _output.WriteLine($"prism: path tracer {measured:0.0000} cd/m², integral {expected:0.0000} cd/m²");
            Assert.True(expected > 0.01, $"reference {expected}");
            Assert.InRange(Math.Abs(measured - expected) / expected, 0, 0.04);
            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }
}
