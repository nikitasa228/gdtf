using System.Numerics;
using Candela.Core.Geometry;
using Candela.Photometry;
using Xunit.Abstractions;

namespace Candela.Rendering.Vulkan.Tests;

/// <summary>
/// Stage lights in the path tracer: coloured beams add up on a white floor (and match the photometric sum), a gobo is
/// projected like the real-time cookie, a rig of a few hundred lights converges to the CPU sum (light selection), and a
/// coloured gel tints the light passing through it.
/// </summary>
public sealed class PathTracerLightTests
{
    private readonly ITestOutputHelper _output;

    public PathTracerLightTests(ITestOutputHelper output) => _output = output;

    private static HdrImage RenderFloor(VulkanRenderer renderer, TopDownView view, SpotLight[] lights, int samples, uint seed = 0)
    {
        renderer.World.SetLights(lights);
        using var render = FinalRenderSupport.RenderToCompletion(renderer, view.Camera, FinalRenderSupport.Settings(view.Width, view.Height, samples, bounces: 1),
            new FinalRenderOptions { BackgroundLuminance = 0f, Seed = seed });
        return FinalRenderSupport.Luminance(render.RawResultHdr(), render.Exposure);
    }

    [SkippableFact]
    public void ColouredSpotsAddUpOnAWhiteFloor()
    {
        var renderer = FinalRenderSupport.CreateOrSkip();
        try
        {
            var view = new TopDownView(64, 48, 8f);
            FinalRenderSupport.Floor(renderer.World, TopDownView.WhiteLambertian());
            var red = ProceduralTestScene.Spot(new Vector3(-1.2f, 0f, 5f), new Vector3(0.6f, 0f, 0f), LightColor.FromRec709(new Vector3(1f, 0.05f, 0.02f)),
                20000f, 30f, 45f);
            var green = ProceduralTestScene.Spot(new Vector3(1.2f, 0.3f, 5f), new Vector3(-0.6f, 0f, 0f), LightColor.FromRec709(new Vector3(0.05f, 1f, 0.1f)),
                15000f, 30f, 45f);
            var a = RenderFloor(renderer, view, new[] { red }, 32, seed: 1);
            var b = RenderFloor(renderer, view, new[] { green }, 32, seed: 2);
            var both = RenderFloor(renderer, view, new[] { red, green }, 32, seed: 3);

            // The overlap at the centre: the sum of the two renders, and the photometric sum of both lights.
            var (cx, cy, _) = view.NearestPixel(Vector3.Zero);
            var sum = a.MeanColor(cx, cy, 2) + b.MeanColor(cx, cy, 2);
            var measured = both.MeanColor(cx, cy, 2);
            Assert.InRange(Vector3.Distance(measured, sum) / sum.Length(), 0f, 0.03f);
            // (over the same 5 x 5 pixels: the centre lies on the flanks of both beams)
            var expected = Vector3.Zero;
            for (int y = cy - 2; y <= cy + 2; y++)
            {
                for (int x = cx - 2; x <= cx + 2; x++)
                {
                    foreach (var light in new[] { red, green })
                    {
                        var beam = BeamDistribution.Create(BeamType.Wash, light.BeamAngle, light.FieldAngle);
                        expected += light.Color * PhotometricTests.FloorLuminance(light, beam.Relative, view.FloorPointOf(x, y)) / 25f;
                    }
                }
            }

            _output.WriteLine($"centre: both {measured}, sum of singles {sum}, photometric {expected}");
            Assert.InRange(Vector3.Distance(measured, expected) / expected.Length(), 0f, 0.04f);
            Assert.True(measured.X > 0.2f * measured.Y && measured.Y > 0.2f * measured.X, "both colours are present in the overlap");

            // Everywhere: linear superposition within the Monte Carlo noise of 32 samples.
            double error = 0, total = 0;
            for (int y = 0; y < view.Height; y++)
            {
                for (int x = 0; x < view.Width; x++)
                {
                    float s = a.LuminanceAt(x, y) + b.LuminanceAt(x, y);
                    error += MathF.Abs(both.LuminanceAt(x, y) - s);
                    total += s;
                }
            }

            Assert.InRange(error / total, 0, 0.05);
            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableFact]
    public void GoboProjectionMatchesTheRealTimeCookie()
    {
        var renderer = FinalRenderSupport.CreateOrSkip();
        try
        {
            const int width = 100, height = 75;
            var view = new TopDownView(width, height, 4f, ev100: 10.5f);
            var scene = new StageScene(renderer);
            scene.Floor(12f, scene.Lambert(new Vector3(0.8f)), InstanceFlags.None);
            var light = StageScene.Spot(new Vector3(0f, 0f, 5f), Vector3.Zero, 20000f, 20f, 26f, shadows: false, type: BeamType.Spot);
            light.Gobo = renderer.World.CreateTexture(StageScene.GoboImage(), srgb: true);
            light.GoboRotation = 0.4f;
            renderer.World.SetLights(new[] { light });

            HdrReadback realTime;
            using (var target = renderer.CreateTarget(width, height))
            {
                renderer.Render(target, view.Camera, new ViewOptions { ShowGrid = false, BackgroundLuminance = 0f },
                    new PreviewSettings { Indirect = IndirectLighting.Off, Bloom = false }, 0f);
                realTime = renderer.ReadHdr(target);
            }

            using var render = FinalRenderSupport.RenderToCompletion(renderer, view.Camera, FinalRenderSupport.Settings(width, height, 64, bounces: 1),
                new FinalRenderOptions { BackgroundLuminance = 0f });
            FinalRenderSupport.Save(render, "pt-gobo");
            var traced = FinalRenderSupport.Luminance(render.RawResultHdr(), render.Exposure);

            // The same pattern: normalised cross-correlation of the two luminance images, and their difference
            // relative to the peak (the edges differ by the real-time pixel-footprint filtering and the noise).
            double sumA = 0, sumB = 0, sumAA = 0, sumBB = 0, sumAB = 0, diff = 0;
            float peak = 0f;
            int n = width * height;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    double p = traced.LuminanceAt(x, y), q = realTime.LuminanceAt(x, y);
                    sumA += p;
                    sumB += q;
                    sumAA += p * p;
                    sumBB += q * q;
                    sumAB += p * q;
                    diff += Math.Abs(p - q);
                    peak = MathF.Max(peak, (float)q);
                }
            }

            double correlation = (sumAB - sumA * sumB / n) / Math.Sqrt((sumAA - sumA * sumA / n) * (sumBB - sumB * sumB / n));
            double meanDifference = diff / n / peak;
            _output.WriteLine($"correlation {correlation:0.0000}, mean |difference| {100 * meanDifference:0.00} % of the peak, " +
                              $"flux ratio {sumA / sumB:0.0000}");
            Assert.True(correlation > 0.98, $"correlation {correlation}");
            Assert.InRange(meanDifference, 0, 0.03);
            Assert.InRange(sumA / sumB, 0.97, 1.03);
            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableFact]
    public void HundredsOfLightsConvergeToThePhotometricSum()
    {
        var renderer = FinalRenderSupport.CreateOrSkip();
        try
        {
            var view = new TopDownView(48, 36, 16f);
            FinalRenderSupport.Floor(renderer.World, TopDownView.WhiteLambertian());
            var random = new Random(17);
            var lights = new SpotLight[300];
            for (int i = 0; i < lights.Length; i++)
            {
                var position = new Vector3((float)(random.NextDouble() * 24 - 12), (float)(random.NextDouble() * 18 - 9), (float)(4 + random.NextDouble() * 3));
                var target = new Vector3((float)(random.NextDouble() * 20 - 10), (float)(random.NextDouble() * 14 - 7), 0f);
                var colour = LightColor.FromRec709(new Vector3((float)random.NextDouble(), (float)random.NextDouble(), (float)random.NextDouble()) + new Vector3(0.1f));
                float beam = (float)(8 + random.NextDouble() * 30);
                lights[i] = ProceduralTestScene.Spot(position, target, colour, (float)(2000 + random.NextDouble() * 20000), beam, beam * 1.5f);
            }

            var hdr = RenderFloor(renderer, view, lights, 64);
            double error = 0, total = 0;
            for (int y = 2; y < view.Height; y += 4)
            {
                for (int x = 2; x < view.Width; x += 4)
                {
                    var p = view.FloorPointOf(x, y);
                    float expected = 0f;
                    foreach (var light in lights)
                    {
                        var beam = BeamDistribution.Create(BeamType.Wash, light.BeamAngle, light.FieldAngle);
                        expected += PhotometricTests.FloorLuminance(light, beam.Relative, p);
                    }

                    error += Math.Abs(hdr.MeanLuminance(x, y, 1) - expected);
                    total += expected;
                }
            }

            _output.WriteLine($"mean absolute error {100 * error / total:0.00} % of the mean luminance");
            Assert.InRange(error / total, 0, 0.04);
            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableFact]
    public void OnlyShadowCastersBlockLightAndUnshadowedLightsPassEverything()
    {
        var renderer = FinalRenderSupport.CreateOrSkip();
        try
        {
            var view = new TopDownView(48, 36, 4f);
            FinalRenderSupport.Floor(renderer.World, TopDownView.WhiteLambertian());
            // A spot 5 m from the floor's centre at 37° from the vertical, and a plate across its beam 1.25 m from the
            // lens (outside the camera's view of the floor).
            var light = ProceduralTestScene.Spot(new Vector3(-4f, 0f, 3f), Vector3.Zero, LightColor.White, 20000f, 20f, 30f);
            var (cx, cy, _) = view.NearestPixel(Vector3.Zero);
            float open = RenderFloor(renderer, view, new[] { light }, 16).MeanLuminance(cx, cy, 1);

            var mesh = renderer.World.CreateMesh(ProceduralMeshes.Plane(0.8f, 0.8f));
            var grey = renderer.World.CreateMaterial(new MaterialData { Name = "plate", BaseColorFactor = new Vector4(0.5f, 0.5f, 0.5f, 1f), DoubleSided = true },
                null);
            var plate = renderer.World.CreateInstance(mesh, new[] { grey }, Matrix4x4.CreateTranslation(-3f, 0f, 2.25f), InstanceFlags.CastShadow, 3);
            float shadowed = RenderFloor(renderer, view, new[] { light }, 16).MeanLuminance(cx, cy, 1);
            // the same plate without CastShadow (the real-time shadow maps leave it out too)
            renderer.World.UpdateInstance(plate, Matrix4x4.CreateTranslation(-3f, 0f, 2.25f), InstanceFlags.None);
            float noCaster = RenderFloor(renderer, view, new[] { light }, 16).MeanLuminance(cx, cy, 1);
            // a light without shadows (like the engine's flame light samples) passes a shadow caster
            renderer.World.UpdateInstance(plate, Matrix4x4.CreateTranslation(-3f, 0f, 2.25f), InstanceFlags.CastShadow);
            var unshadowed = light;
            unshadowed.CastShadows = false;
            float unshadowedLight = RenderFloor(renderer, view, new[] { unshadowed }, 16).MeanLuminance(cx, cy, 1);

            _output.WriteLine($"open {open:0.00}, shadowed {shadowed:0.00}, no caster {noCaster:0.00}, unshadowed light {unshadowedLight:0.00} cd/m²");
            Assert.True(open > 1f, $"the floor is not lit: {open}");
            Assert.InRange(shadowed / open, 0f, 0.02f);
            Assert.InRange(noCaster / open, 0.97f, 1.03f);
            Assert.InRange(unshadowedLight / open, 0.97f, 1.03f);
            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableFact]
    public void ColouredGelTintsTheLightPassingThroughIt()
    {
        var renderer = FinalRenderSupport.CreateOrSkip();
        try
        {
            var view = new TopDownView(48, 36, 6f);
            FinalRenderSupport.Floor(renderer.World, TopDownView.WhiteLambertian());
            var light = ProceduralTestScene.Spot(new Vector3(0f, 0f, 5f), Vector3.Zero, LightColor.White, 20000f, 40f, 60f);
            var open = RenderFloor(renderer, view, new[] { light }, 32);

            // A red gel (thin-walled, fully transmissive, IOR 1.5) 2.5 m above the floor over the half x > 0.
            var gel = renderer.World.CreateMaterial(new MaterialData
            {
                Name = "red gel",
                BaseColorFactor = new Vector4(1f, 0.1f, 0.1f, 1f),
                Transmission = 1f,
                Ior = 1.5f,
                Roughness = 0.02f,
                DoubleSided = true,
            }, null);
            var panel = renderer.World.CreateMesh(ProceduralMeshes.Plane(3f, 6f));
            renderer.World.CreateInstance(panel, new[] { gel }, Matrix4x4.CreateTranslation(1.5f, 0f, 2.5f), InstanceFlags.CastShadow, 2);
            var filtered = RenderFloor(renderer, view, new[] { light }, 32);

            // Under the gel the camera (looking down through it) sees the floor lit through it: tinted twice by
            // (1 - F) x transmission x colour at normal incidence (F0 = 0.04).
            var (gx, gy, _) = view.NearestPixel(new Vector3(1f, 0f, 0f));
            var tint = ColorSpaces.Rec709ToRec2020(new Vector3(1f, 0.1f, 0.1f)) * 0.96f;
            var expected = open.MeanColor(gx, gy, 1) * tint * tint;
            var measured = filtered.MeanColor(gx, gy, 1);
            _output.WriteLine($"through the gel {measured}, expected {expected}, open {open.MeanColor(gx, gy, 1)}");
            Assert.InRange(Vector3.Distance(measured, expected) / expected.Length(), 0f, 0.05f);
            // Beside the gel the light stays white.
            var (wx, wy, _) = view.NearestPixel(new Vector3(-1f, 0f, 0f));
            Assert.InRange(Vector3.Distance(filtered.MeanColor(wx, wy, 1), open.MeanColor(wx, wy, 1)) / open.MeanColor(wx, wy, 1).Length(), 0f, 0.05f);
            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }
}
