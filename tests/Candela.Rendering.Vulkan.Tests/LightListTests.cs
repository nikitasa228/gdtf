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

    /// <summary>
    /// 4500 lights — more than the 2048 one work group's threads cover with one mask word each, so every thread builds
    /// several words and a cluster record has five summary words — where the first 2048 lights all point up (no floor
    /// cluster lists them: its first two summary words stay empty) and every third light after that is one of 818
    /// beams overlapping around the origin, up to the very last light. The lux overlay equals the light meter of the
    /// listed lights within 1 %; with a limit of 3000 (inside a mask word) exactly the first 3000 lights are summed and
    /// the beams beyond them are reported as missing.
    /// </summary>
    [SkippableTheory]
    [InlineData(65536)]
    [InlineData(3000)]
    public void LightsBeyondTwoThousandIndicesAreSummedAndALimitInsideAMaskWordIsExact(int limit)
    {
        const int count = 4500, firstBeam = 2048;
        var renderer = CreateOrSkip(null, limit);
        try
        {
            var scene = new StageScene(renderer);
            scene.Floor(30f, scene.Lambert(new Vector3(0.5f)), InstanceFlags.None);
            var beams = new OverlappingBeams((count - firstBeam + 2) / 3, seed: 977);
            var lights = new SpotLight[count];
            var reference = new Photometry.SpotLight[count];
            var random = new Random(31);
            int beam = 0;
            for (int i = 0; i < count; i++)
            {
                if (i >= firstBeam && (i - firstBeam) % 3 == 0)
                {
                    lights[i] = beams.Lights[beam];
                    reference[i] = beams.Reference[beam++];
                    continue;
                }

                // Up-lights over the stage: in the view, but they light nothing below them.
                var position = new Vector3(12f * random.NextSingle() - 6f, 8f * random.NextSingle() - 4f, 3f + 5f * random.NextSingle());
                lights[i] = StageScene.Spot(position, position + Vector3.UnitZ, 500f + 1500f * random.NextSingle(), 12f, 18f, shadows: false);
                reference[i] = new Photometry.SpotLight(lights[i].Position, lights[i].Direction, lights[i].PeakCandela,
                    BeamDistribution.Create(lights[i].BeamType, lights[i].BeamAngle, lights[i].FieldAngle), LightColor.FromRec2020(lights[i].Color));
            }

            Assert.Equal(beams.Lights.Length, beam);
            Assert.Equal(count - 1, firstBeam + 3 * (beam - 1)); // the last light is a beam
            renderer.World.SetLights(lights);
            var top = new TopDownView(Width, Height, 10f);
            using var target = renderer.CreateTarget(Width, Height);
            renderer.Render(target, top.Camera, new ViewOptions { ShowGrid = false, Overlay = OverlayMode.Lux }, new PreviewSettings(), 0f);
            var lux = renderer.ReadAnalysis(target);
            var lists = renderer.ReadLightLists(target);
            TestVulkan.AssertNoValidationErrors(renderer.Context);

            int listed = Math.Min(limit, count);
            Assert.Equal(count, lists.Lights);
            Assert.Equal(listed, lists.ListedLights);
            Assert.Equal(listed < count, lists.HasOverflow);
            int beamsAtOrigin = beams.LightsReaching(Vector3.Zero);
            if (listed == count)
                Assert.True(lists.MaxLightsInCluster >= beamsAtOrigin, $"busiest cluster {lists.MaxLightsInCluster}, {beamsAtOrigin} beams reach the origin");

            var drawn = reference.AsSpan(0, listed).ToArray();
            int lit = 0;
            for (float y = -4f; y <= 4f; y += 0.61f)
            {
                for (float x = -6f; x <= 6f; x += 0.73f)
                {
                    var (px, py, point) = top.NearestPixel(new Vector3(x, y, 0f));
                    float expected = LightMeter.IlluminanceAt(drawn, point, Vector3.UnitZ);
                    if (expected < 5f)
                        continue;
                    lit++;
                    Assert.True(MathF.Abs(lux[py * Width + px] - expected) <= 0.01f * expected,
                        $"({x:F2}, {y:F2}): {lux[py * Width + px]:F1} lx, light meter of the first {listed} lights {expected:F1} lx");
                }
            }

            Assert.True(lit > 100, $"{lit} lit sample points");
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    /// <summary>
    /// Dark lights (dimmed to zero) light nothing, so they are neither listed in the clusters nor reported as missing
    /// when they are beyond the clustered-light limit: a blacked-out fixture beyond the limit raises no warning. Once it
    /// is lit, it is missing and reported.
    /// </summary>
    [SkippableFact]
    public void DarkLightsBeyondTheLimitAreNotReportedAsMissing()
    {
        var renderer = CreateOrSkip(null, maxClusteredLights: 1);
        try
        {
            var scene = new StageScene(renderer);
            scene.Floor(30f, scene.Lambert(new Vector3(0.5f)), InstanceFlags.None);
            var lit = StageScene.Spot(new Vector3(0f, 0f, 5f), Vector3.Zero, 2000f, 30f, 40f, shadows: false);
            var dark = StageScene.Spot(new Vector3(1f, 0.5f, 5f), new Vector3(1f, 0.5f, 0f), 2000f, 30f, 40f, shadows: false);
            dark.PeakCandela = 0f;
            var top = new TopDownView(Width, Height, 10f);
            var lux = new ViewOptions { ShowGrid = false, Overlay = OverlayMode.Lux };
            var full = new ViewOptions { ShowGrid = false };
            using var target = renderer.CreateTarget(Width, Height);

            // The dark light sits at the lit one's position too (the same cluster), and inside the view.
            var darkAtLit = lit;
            darkAtLit.PeakCandela = 0f;
            renderer.World.SetLights(new[] { lit, dark, darkAtLit });
            foreach (var view in new[] { lux, full })
            {
                renderer.Render(target, top.Camera, view, new PreviewSettings(), 0f);
                var lists = renderer.ReadLightLists(target);
                Assert.Equal(new LightListDiagnostics(3, 1, 1, 0), lists);
                Assert.False(lists.HasOverflow);
            }

            // Lit, the second light is missing from the view and reported.
            dark.PeakCandela = 2000f;
            renderer.World.SetLights(new[] { lit, dark, darkAtLit });
            renderer.Render(target, top.Camera, lux, new PreviewSettings(), 0f);
            Assert.True(renderer.ReadLightLists(target).HasOverflow);
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
