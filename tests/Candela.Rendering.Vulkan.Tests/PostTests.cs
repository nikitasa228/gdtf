using System.Numerics;
using Candela.Core.Geometry;
using Candela.Core.Math;
using Candela.Photometry;

namespace Candela.Rendering.Vulkan.Tests;

/// <summary>
/// Post-processing: the bloom redistributes light without adding or losing energy, auto exposure converges to the
/// histogram's trimmed mean and follows the scene's brightness, exposure compensation, and the resolution scale
/// (internal images smaller, display and picking unchanged).
/// </summary>
public sealed class PostTests
{
    private const int Width = 320;
    private const int Height = 240;

    [SkippableFact]
    public void BloomSpreadsBrightLightAndConservesEnergy()
    {
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            var scene = new StageScene(renderer);
            BuildBloomScene(scene);
            using var target = renderer.CreateTarget(Width, Height);
            var camera = RenderCamera.LookAt(new Vector3(0f, -6f, 2.2f), new Vector3(0f, 0.8f, 1f), 45f);
            camera.Ev100 = 7f;
            var view = new ViewOptions { ShowGrid = false };
            var without = scene.Render(target, camera, view, new PreviewSettings { Bloom = false });
            var with = scene.Render(target, camera, view, new PreviewSettings { Bloom = true, BloomIntensity = 0.12f });
            var hdr = renderer.ReadHdr(target);
            var bloom = renderer.ReadBloom(target);
            TestVulkan.AssertNoValidationErrors(renderer.Context);
            GoldenImageTests.CompareWithGolden(with, "bloom");

            // Energy: level 0 is half the internal resolution, so each bloom pixel stands for four HDR pixels.
            Assert.Equal((Width / 2, Height / 2), (bloom.Width, bloom.Height));
            double hdrSum = 0, bloomSum = 0;
            for (int y = 0; y < hdr.Height; y++)
            {
                for (int x = 0; x < hdr.Width; x++)
                    hdrSum += hdr.LuminanceAt(x, y);
            }

            for (int y = 0; y < bloom.Height; y++)
            {
                for (int x = 0; x < bloom.Width; x++)
                    bloomSum += 4.0 * bloom.LuminanceAt(x, y);
            }

            Assert.InRange(bloomSum / hdrSum, 0.97, 1.03);

            // The glowing sphere: darker in its centre (part of its light moved out), a halo next to it on the dark
            // backdrop that is absent without bloom.
            var vp = camera.View * ReverseZ.Perspective(camera.FovY, Width / (float)Height, camera.Near);
            var (cx, cy) = PixelOf(vp, GlowCentre);
            var (hx, hy) = PixelOf(vp, GlowCentre + new Vector3(0.75f, 0f, 0f));
            int centre = (cy * Width + cx) * 4, halo = (hy * Width + hx) * 4;
            Assert.True(with.Pixels[halo + 1] > without.Pixels[halo + 1] + 15,
                $"halo {with.Pixels[halo + 1]} with bloom vs {without.Pixels[halo + 1]} without");
            Assert.True(with.Pixels[centre + 1] <= without.Pixels[centre + 1], "the source gives up the light the halo receives");
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableFact]
    public void AutoExposureConvergesToTheTrimmedMeanAndFollowsTheLight()
    {
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            var scene = new StageScene(renderer);
            scene.Floor(40f, scene.Lambert(new Vector3(0.5f)));
            var lights = new[] { StageScene.Spot(new Vector3(0f, 0f, 8f), Vector3.Zero, 20000f, 70f, 110f, shadows: false) };
            renderer.World.SetLights(lights);
            using var target = renderer.CreateTarget(160, 120);
            var camera = RenderCamera.LookAt(new Vector3(0f, -3f, 4f), new Vector3(0f, 0.5f, 0f), 50f);
            camera.Ev100 = 2f; // far too bright: auto exposure has to correct it
            var settings = new PreviewSettings { AutoExposure = true, Bloom = false, Volumetrics = false };
            var view = new ViewOptions { ShowGrid = false };

            float Converge()
            {
                for (int i = 0; i < 40; i++)
                    renderer.Render(target, camera, view, settings, 0.25f);
                return renderer.ReadHdr(target).Exposure;
            }

            float exposure = Converge();
            var hdr = renderer.ReadHdr(target);
            float expected = Units.ExposureFromEv100(Units.Ev100FromLuminance(MathF.Pow(2f, TrimmedMeanLog2(hdr))));
            Assert.InRange(MathF.Log2(exposure / expected), -0.2f, 0.2f);
            Assert.True(exposure < Units.ExposureFromEv100(camera.Ev100) / 8f, "the camera EV is not used");

            // Four times the light: the exposure drops two stops (all pixels are lit, so the histogram shifts by two stops).
            lights[0].PeakCandela *= 4f;
            renderer.World.SetLights(lights);
            for (int i = 0; i < 6; i++)
                renderer.Render(target, camera, view, settings, 0.1f); // the measurement arrives a few frames late
            float adapting = renderer.ReadHdr(target).Exposure;
            float bright = Converge();
            Assert.InRange(MathF.Log2(exposure / bright), 1.85f, 2.15f);
            Assert.True(adapting < exposure && adapting > bright * 1.05f, $"adapts over time: {exposure} -> {adapting} -> {bright}");

            // Compensation adds stops of exposure on top of the adapted value.
            settings.ExposureCompensation = 1f;
            float compensated = Converge();
            Assert.InRange(MathF.Log2(compensated / bright), 0.95f, 1.05f);
            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableFact]
    public void ResolutionScaleRendersFewerPixelsAndKeepsTheDisplayAndPicking()
    {
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            ProceduralTestScene.Build(renderer.World);
            var camera = ProceduralTestScene.CreateCamera();
            var view = new ViewOptions();
            using var target = renderer.CreateTarget(Width, Height);
            renderer.Render(target, camera, view, new PreviewSettings(), 0f);
            var full = renderer.ReadPixels(target);
            var fullPick = renderer.Pick(target, 160, 150);

            renderer.Render(target, camera, view, new PreviewSettings { ResolutionScale = 0.5f }, 0f);
            var half = renderer.ReadPixels(target);
            var hdr = renderer.ReadHdr(target);
            TestVulkan.AssertNoValidationErrors(renderer.Context);
            Assert.Equal((Width / 2, Height / 2), (hdr.Width, hdr.Height));
            Assert.Equal((Width, Height), (half.Width, half.Height));

            // Same picture, softer: mean absolute difference of a few 8-bit levels.
            double difference = 0;
            for (int i = 0; i < full.Pixels.Length; i += 4)
            {
                for (int c = 0; c < 3; c++)
                    difference += Math.Abs(full.Pixels[i + c] - half.Pixels[i + c]);
            }

            difference /= Width * Height * 3;
            Assert.InRange(difference, 0.0, 6.0);

            // Display coordinates still pick the same object at (nearly) the same point.
            var halfPick = renderer.Pick(target, 160, 150);
            Assert.NotNull(fullPick);
            Assert.NotNull(halfPick);
            Assert.Equal(fullPick!.Value.PickId, halfPick!.Value.PickId);
            Assert.InRange(Vector3.Distance(fullPick.Value.WorldPosition, halfPick.Value.WorldPosition), 0f, 0.01f);

            // Out-of-range scales are clamped to [0.5, 1].
            renderer.Render(target, camera, view, new PreviewSettings { ResolutionScale = 0.1f }, 0f);
            Assert.Equal(Width / 2, renderer.ReadHdr(target).Width);
            renderer.Render(target, camera, view, new PreviewSettings { ResolutionScale = float.NaN }, 0f);
            Assert.Equal(Width, renderer.ReadHdr(target).Width);
            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    private static readonly Vector3 GlowCentre = new(0.6f, 1.5f, 1.4f);

    private static void BuildBloomScene(StageScene scene)
    {
        var world = scene.World;
        scene.Floor(16f, scene.Lambert(new Vector3(0.25f)));
        scene.Box(new Vector3(8f, 0.2f, 4f), new Vector3(0f, 3.5f, 2f), scene.Lambert(new Vector3(0.05f)), InstanceFlags.None, 4);
        scene.Box(new Vector3(0.8f, 0.8f, 1.2f), new Vector3(-1.6f, 0.8f, 0.6f), scene.Lambert(new Vector3(0.6f, 0.6f, 0.65f)));
        var glow = world.CreateMaterial(new MaterialData
        {
            Name = "glow",
            BaseColorFactor = new Vector4(0f, 0f, 0f, 1f),
            EmissiveColor = new Vector3(1f, 0.8f, 0.55f),
            EmissiveLuminance = 30000f,
        }, null);
        scene.Sphere(0.18f, GlowCentre, glow, InstanceFlags.None, 5);
        world.SetLights(new[]
        {
            StageScene.Spot(new Vector3(-1f, -2f, 5f), new Vector3(-1.2f, 0.8f, 0f), 60000f, 18f, 26f),
        });
    }

    private static (int X, int Y) PixelOf(Matrix4x4 viewProjection, Vector3 p)
    {
        var clip = Vector4.Transform(new Vector4(p, 1f), viewProjection);
        return ((int)MathF.Floor((clip.X / clip.W * 0.5f + 0.5f) * Width), (int)MathF.Floor((clip.Y / clip.W * 0.5f + 0.5f) * Height));
    }

    /// <summary>CPU version of the exposure measurement: mean log2 luminance between the 50th and 95th percentile.</summary>
    private static float TrimmedMeanLog2(HdrReadback hdr)
    {
        var values = new float[hdr.Width * hdr.Height];
        for (int y = 0; y < hdr.Height; y++)
        {
            for (int x = 0; x < hdr.Width; x++)
                values[y * hdr.Width + x] = MathF.Log2(MathF.Max(hdr.LuminanceAt(x, y), 1e-12f));
        }

        Array.Sort(values);
        int lo = (int)(values.Length * 0.5f), hi = (int)(values.Length * 0.95f);
        double sum = 0;
        for (int i = lo; i < hi; i++)
            sum += values[i];
        return (float)(sum / (hi - lo));
    }
}
