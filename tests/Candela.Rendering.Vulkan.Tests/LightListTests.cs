using System.Collections.Concurrent;
using System.Numerics;
using Candela.Core.Geometry;
using Candela.Photometry;

namespace Candela.Rendering.Vulkan.Tests;

/// <summary>
/// The clustered light lists (one bit per light and cluster) on dense rigs: Full shading and the volumetric light add
/// up every one of more than a thousand overlapping beams — the lists used to be cut at 512 lights per cluster, which
/// darkened whole screen tiles on Фокина — and lights beyond the renderer's clustered-light limit are reported in the
/// target's <see cref="IRenderTarget.LightLists"/>, the render statistics and the log.
/// </summary>
public sealed class LightListTests
{
    private const int Width = 240;
    private const int Height = 180;

    /// <summary>
    /// Full shading of a white Lambertian floor under 1320 overlapping beams equals the CPU sum over all of them
    /// (L = E / π) within 1 % — the answer to "does Full shading truncate at 512 lights per cluster": not any more (it
    /// did, like the lux overlay; the same lists serve both).
    /// </summary>
    [SkippableFact]
    public void FullShadingAddsUpEveryOneOfThirteenHundredOverlappingBeams()
    {
        const int count = 1320;
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            var world = renderer.World;
            var floor = world.CreateMesh(ProceduralMeshes.Plane(30f, 30f));
            var white = world.CreateMaterial(TopDownView.WhiteLambertian(), null);
            world.CreateInstance(floor, new[] { white }, Matrix4x4.Identity, InstanceFlags.None, 1);
            var rig = new OverlappingBeams(count);
            world.SetLights(rig.Lights);
            var view = new TopDownView(Width, Height, 10f, ev100: 12f);
            using var target = renderer.CreateTarget(Width, Height);
            var options = new ViewOptions { Shading = ViewShading.Full, ShowGrid = false, BackgroundLuminance = 0f };
            renderer.Render(target, view.Camera, options, new PreviewSettings(), 1f / 60f);
            var hdr = renderer.ReadHdr(target);
            var lists = renderer.ReadLightLists(target);
            TestVulkan.AssertNoValidationErrors(renderer.Context);

            Assert.Equal(new LightListDiagnostics(count, count, count, 0), lists);
            var samples = new List<(int X, int Y, float Expected)>();
            float maxExpected = 0f;
            for (float y = -4f; y <= 4f; y += 0.7f)
            {
                for (float x = -6f; x <= 6f; x += 0.8f)
                {
                    var (px, py, point) = view.NearestPixel(new Vector3(x, y, 0f));
                    float expected = rig.FloorLuminance(point);
                    samples.Add((px, py, expected));
                    maxExpected = MathF.Max(maxExpected, expected);
                }
            }

            Assert.True(maxExpected > 1000f, $"the floor should be brightly lit (max {maxExpected} cd/m²)");
            foreach (var (px, py, expected) in samples)
            {
                float measured = hdr.LuminanceAt(px, py);
                Assert.True(MathF.Abs(measured - expected) <= 0.01f * expected + 1e-3f * maxExpected,
                    $"pixel ({px}, {py}): {measured:F1} cd/m², the sum over all {count} lights {expected:F1} cd/m²");
            }
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    /// <summary>
    /// 1100 co-located copies of a spot light, each with 1/1100 of the intensity, render like the one light carrying
    /// their sum — surfaces and the in-scattered light of the haze (the froxel volume reads the same cluster lists; no
    /// ray-marched beams here) — instead of the 512/1100 that a truncated list would add up.
    /// </summary>
    [SkippableFact]
    public void ManyCoLocatedLightsInHazeRenderLikeOneLightCarryingTheirSum()
    {
        const int copies = 1100;
        const float intensity = 150000f;
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            var scene = new StageScene(renderer);
            scene.Floor(30f, scene.Lambert(new Vector3(0.6f)), InstanceFlags.None);
            renderer.World.SetAtmosphere(new AtmosphereState { BaseScattering = 0.05f, Anisotropy = 0.3f });
            var one = StageScene.Spot(new Vector3(0f, 0f, 6f), new Vector3(0f, 2f, 0f), intensity, 18f, 28f, shadows: false);
            var copy = one;
            copy.PeakCandela = intensity / copies;
            var many = Enumerable.Repeat(copy, copies).ToArray();

            var camera = RenderCamera.LookAt(new Vector3(0f, -9f, 3f), new Vector3(0f, 1.5f, 2f), 60f);
            camera.Ev100 = 9f;
            var view = new ViewOptions { Shading = ViewShading.Full, ShowGrid = false, BackgroundLuminance = 0f };
            var settings = new PreviewSettings
            {
                MaxRaymarchedBeams = 0,
                TemporalAccumulation = false,
                VolumetricSlices = 64,
                Bloom = false,
                Indirect = IndirectLighting.Off,
            };
            using var target = renderer.CreateTarget(Width, Height);
            renderer.World.SetLights(many);
            renderer.Render(target, camera, view, settings, 0f);
            var manyHdr = renderer.ReadHdr(target);
            var stats = renderer.LastStatistics;
            Assert.Equal(0, stats.RaymarchedBeams);
            Assert.Equal(copies, stats.FroxelLights);
            Assert.Equal(new LightListDiagnostics(copies, copies, copies, 0), renderer.ReadLightLists(target));

            renderer.World.SetLights(new[] { one });
            renderer.Render(target, camera, view, settings, 0f);
            var oneHdr = renderer.ReadHdr(target);
            TestVulkan.AssertNoValidationErrors(renderer.Context);

            double sumMany = 0, sumOne = 0;
            float max = 0f;
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                    max = MathF.Max(max, oneHdr.LuminanceAt(x, y));
            }

            // The air above the floor (top third of the image) shows only in-scattered light.
            float airMax = 0f;
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    float a = manyHdr.LuminanceAt(x, y), b = oneHdr.LuminanceAt(x, y);
                    sumMany += a;
                    sumOne += b;
                    if (y < Height / 3)
                        airMax = MathF.Max(airMax, b);
                    Assert.True(MathF.Abs(a - b) <= 0.01f * b + 2e-3f * max, $"pixel ({x}, {y}): {a:F4} cd/m² from {copies} lights, {b:F4} from one");
                }
            }

            Assert.True(airMax > 0.02f * max, $"the haze should glow in the beam (air {airMax}, max {max} cd/m²)");
            Assert.InRange(sumMany / sumOne, 0.99, 1.01);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    /// <summary>
    /// With a clustered-light limit below the light count the lights beyond it are not drawn: the lux overlay is the
    /// light meter of the listed lights; the overflow shows in <see cref="IRenderTarget.LightLists"/> (read back a frame
    /// or two later without waiting), in the render statistics and as one warning in the log — not once per frame — and
    /// a note when every light fits again. A view that needs no lights reports nothing.
    /// </summary>
    [SkippableFact]
    public void LightsBeyondTheClusteredLimitAreReportedInTheTargetAndTheLog()
    {
        const int count = 100;
        var log = new ConcurrentQueue<string>();
        var renderer = CreateOrSkip(log, maxClusteredLights: 50);
        try
        {
            var scene = new StageScene(renderer);
            scene.Floor(30f, scene.Lambert(new Vector3(0.5f)), InstanceFlags.None);
            var rig = new OverlappingBeams(count);
            renderer.World.SetLights(rig.Lights);
            var top = new TopDownView(Width, Height, 10f);
            var lux = new ViewOptions { ShowGrid = false, Overlay = OverlayMode.Lux };
            using var target = renderer.CreateTarget(Width, Height);
            renderer.Render(target, top.Camera, lux, new PreviewSettings(), 0f);
            var lists = renderer.ReadLightLists(target);
            var values = renderer.ReadAnalysis(target);
            TestVulkan.AssertNoValidationErrors(renderer.Context);

            Assert.Equal(count, lists.Lights);
            Assert.Equal(50, lists.ListedLights);
            Assert.Equal(50, lists.UnlistedLights);
            Assert.Equal(50, lists.MaxLightsInCluster);
            Assert.InRange(lists.OverflowedClusters, 1, (int)ShaderConstants.ClusterCount);
            Assert.True(lists.HasOverflow);
            // The light planner leaves the unlisted lights out as well (no shadow tile, beam or indirect light).
            var scores = renderer.Planner.Scores;
            Assert.True(scores[..50].ToArray().All(v => v > 0f), "every listed light lights the view");
            Assert.True(scores[50..100].ToArray().All(v => v == 0f), "the unlisted lights are not planned");
            var listed = rig.Reference.AsSpan(0, 50).ToArray();
            int lit = 0;
            for (float y = -3f; y <= 3f; y += 0.9f)
            {
                for (float x = -5f; x <= 5f; x += 0.9f)
                {
                    var (px, py, point) = top.NearestPixel(new Vector3(x, y, 0f));
                    float expected = LightMeter.IlluminanceAt(listed, point, Vector3.UnitZ);
                    if (expected < 5f)
                        continue;
                    lit++;
                    Assert.True(MathF.Abs(values[py * Width + px] - expected) <= 0.01f * expected,
                        $"({x:F1}, {y:F1}): {values[py * Width + px]:F1} lx, light meter of the listed lights {expected:F1} lx");
                }
            }

            Assert.True(lit > 30, $"{lit} lit sample points");

            // The non-blocking path: the statistics of a frame arrive when its frame slot comes round again.
            for (int i = 0; i < 6; i++)
                renderer.Render(target, top.Camera, lux, new PreviewSettings(), 1f / 60f);
            Assert.Equal(lists, target.LightLists);
            Assert.Equal(lists.OverflowedClusters, renderer.LastStatistics.OverflowedClusters);
            Assert.Equal(50, renderer.LastStatistics.MaxLightsInCluster);
            var warnings = log.Where(m => m.StartsWith("Warning:", StringComparison.Ordinal) && m.Contains("Light lists overflowed", StringComparison.Ordinal))
                .ToList();
            Assert.Single(warnings);
            Assert.Contains("50 of 100 lights", warnings[0], StringComparison.Ordinal);

            // Every light fits again: the overflow clears and the log says so once.
            renderer.World.SetLights(rig.Lights.AsSpan(0, 50));
            for (int i = 0; i < 4; i++)
                renderer.Render(target, top.Camera, lux, new PreviewSettings(), 1f / 60f);
            Assert.False(target.LightLists.HasOverflow);
            Assert.Equal(50, target.LightLists.Lights);
            Assert.Single(log, m => m.StartsWith("Info:", StringComparison.Ordinal) && m.Contains("hold every light again", StringComparison.Ordinal));

            // A view without lights (Colorless, no lux overlay) reports nothing, at once.
            renderer.World.SetLights(rig.Lights);
            renderer.Render(target, top.Camera, lux, new PreviewSettings(), 1f / 60f);
            renderer.Render(target, top.Camera, lux, new PreviewSettings(), 1f / 60f);
            renderer.Render(target, top.Camera, new ViewOptions { Shading = ViewShading.Colorless, ShowGrid = false }, new PreviewSettings(), 0f);
            Assert.Equal(default, target.LightLists);
            Assert.Equal(default, renderer.ReadLightLists(target));
            for (int i = 0; i < 3; i++)
                renderer.Render(target, top.Camera, new ViewOptions { Shading = ViewShading.Colorless, ShowGrid = false }, new PreviewSettings(), 0f);
            Assert.Equal(default, target.LightLists);
            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    /// <summary>The clustered-light limit is exact (not rounded to mask words) and clamped to 1 … 65 536.</summary>
    [SkippableTheory]
    [InlineData(0, 1)]
    [InlineData(-7, 1)]
    [InlineData(33, 33)]
    [InlineData(64, 64)]
    [InlineData(int.MaxValue, 100)]
    public void TheClusteredLightLimitIsExactAndClamped(int limit, int listed)
    {
        var renderer = CreateOrSkip(null, limit);
        try
        {
            new StageScene(renderer).Floor(10f, new StageScene(renderer).Lambert(new Vector3(0.5f)), InstanceFlags.None);
            renderer.World.SetLights(new OverlappingBeams(100).Lights);
            var top = new TopDownView(64, 48, 10f);
            using var target = renderer.CreateTarget(64, 48);
            renderer.Render(target, top.Camera, new ViewOptions { ShowGrid = false, Overlay = OverlayMode.Lux }, new PreviewSettings(), 0f);
            var lists = renderer.ReadLightLists(target);
            Assert.Equal(listed, lists.ListedLights);
            Assert.Equal(listed < 100, lists.HasOverflow);
            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    /// <summary>
    /// <c>CANDELA_MAX_CLUSTERED_LIGHTS</c> lowers the default limit (to try the overflow warning on a small scene); values
    /// that are not a positive whole number keep the default, larger ones are capped at 65 536.
    /// </summary>
    [Theory]
    [InlineData(null, 65536)]
    [InlineData("", 65536)]
    [InlineData("many", 65536)]
    [InlineData("0", 65536)]
    [InlineData("-3", 65536)]
    [InlineData("2.5", 65536)]
    [InlineData("2", 2)]
    [InlineData(" 512 ", 512)]
    [InlineData("1000000", 65536)]
    public void TheEnvironmentCanLowerTheDefaultLimit(string? value, int limit) =>
        Assert.Equal(limit, VulkanRendererOptions.ParseMaxClusteredLights(value));

    /// <summary>A validated renderer whose log also keeps notes (Info), with a clustered-light limit; skips without a device.</summary>
    private static VulkanRenderer CreateOrSkip(ConcurrentQueue<string>? log, int maxClusteredLights)
    {
        var options = TestVulkan.Options(log);
        options.MinimumLogLevel = VulkanLogLevel.Info;
        VulkanRenderer? renderer = null;
        string reason = "";
        try
        {
            renderer = VulkanRenderer.Create(options, new VulkanRendererOptions { MaxClusteredLights = maxClusteredLights });
        }
        catch (VulkanUnavailableException e)
        {
            reason = e.Message;
        }

        Skip.If(renderer is null, $"No Vulkan device available: {reason}");
        return renderer!;
    }
}
