using System.Diagnostics;
using System.Numerics;
using Candela.Core.Geometry;
using Candela.Core.Math;
using Candela.Photometry;
using Xunit.Abstractions;

namespace Candela.Rendering.Vulkan.Tests;

/// <summary>
/// The final render as a job: the error falls like 1/√N, the same seed gives the same image, cancellation and time
/// limits stop it with its samples kept, the denoiser lowers the error, the preview uses the real-time display transform
/// and the auto exposure meters like the real-time view, bad input stays finite, and devices without ray query get a
/// clear "not supported".
/// </summary>
public sealed class PathTracerSessionTests
{
    private readonly ITestOutputHelper _output;

    public PathTracerSessionTests(ITestOutputHelper output) => _output = output;

    /// <summary>A small room-like scene with indirect light: a floor, a red wall and a white box under a spot.</summary>
    private static RenderCamera BuildRoom(VulkanRenderer renderer)
    {
        var scene = new StageScene(renderer);
        scene.Floor(10f, scene.Lambert(new Vector3(0.7f)));
        scene.Box(new Vector3(0.2f, 6f, 4f), new Vector3(-2.5f, 0f, 2f), scene.Lambert(new Vector3(0.8f, 0.1f, 0.1f), "red wall"));
        scene.Box(new Vector3(1f, 1f, 1f), new Vector3(0.5f, 0.5f, 0.5f), scene.Lambert(new Vector3(0.8f), "box"));
        renderer.World.SetLights(new[] { StageScene.Spot(new Vector3(0.5f, -1f, 4.5f), new Vector3(-0.5f, 0.5f, 0f), 15000f, 50f, 70f) });
        var camera = RenderCamera.LookAt(new Vector3(3f, -5f, 3f), new Vector3(-0.5f, 0.5f, 0.8f), 50f);
        camera.Ev100 = 6f;
        return camera;
    }

    private static float[] Luminances(ImageData hdr, float exposure)
    {
        var image = FinalRenderSupport.Luminance(hdr, exposure);
        var values = new float[image.Width * image.Height];
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
                values[y * image.Width + x] = image.LuminanceAt(x, y);
        }

        return values;
    }

    private static double Rmse(float[] a, float[] b)
    {
        double sum = 0;
        for (int i = 0; i < a.Length; i++)
            sum += (a[i] - b[i]) * (double)(a[i] - b[i]);
        return Math.Sqrt(sum / a.Length);
    }

    [SkippableFact]
    public void ErrorFallsLikeOneOverTheSquareRootOfTheSamples()
    {
        var renderer = FinalRenderSupport.CreateOrSkip();
        try
        {
            var camera = BuildRoom(renderer);
            float[] Render(int samples, uint seed)
            {
                using var render = FinalRenderSupport.RenderToCompletion(renderer, camera, FinalRenderSupport.Settings(40, 30, samples),
                    new FinalRenderOptions { Seed = seed, BackgroundLuminance = 0f });
                return Luminances(render.RawResultHdr(), render.Exposure);
            }

            var reference = Render(2048, 99);
            var errors = new[] { 4, 16, 64 }.Select(n => Rmse(Render(n, 1), reference)).ToArray();
            _output.WriteLine($"RMSE at 4, 16, 64 samples: {string.Join(", ", errors.Select(e => e.ToString("0.000")))} cd/m²; " +
                              $"ratios {errors[0] / errors[1]:0.00}, {errors[1] / errors[2]:0.00} (1/√N: 2)");
            Assert.InRange(errors[0] / errors[1], 1.6, 2.5);
            Assert.InRange(errors[1] / errors[2], 1.6, 2.5);
            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableFact]
    public void SameSeedGivesTheSameImage()
    {
        var renderer = FinalRenderSupport.CreateOrSkip();
        try
        {
            var camera = BuildRoom(renderer);
            byte[] Render(uint seed)
            {
                using var render = FinalRenderSupport.RenderToCompletion(renderer, camera, FinalRenderSupport.Settings(32, 24, 4),
                    new FinalRenderOptions { Seed = seed });
                return render.RawResultHdr().Pixels;
            }

            var a = Render(7);
            Assert.Equal(a, Render(7));
            Assert.NotEqual(a, Render(8));
            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableFact]
    public void CancellationStopsTheRenderAndKeepsItsSamples()
    {
        var renderer = FinalRenderSupport.CreateOrSkip();
        try
        {
            var camera = BuildRoom(renderer);
            using var render = renderer.StartFinalRender(camera, FinalRenderSupport.Settings(64, 48, FinalRenderSettingsMax), new FinalRenderOptions());
            render.Advance(TimeSpan.FromSeconds(0.5));
            Assert.True(render.SamplesDone > 0 && !render.IsComplete);

            // A token cancelled while Advance runs ends it promptly.
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            var watch = Stopwatch.StartNew();
            render.Advance(TimeSpan.FromSeconds(60), cancel.Token);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"cancellation took {watch.Elapsed}");
            Assert.True(render.IsCancelled && render.IsComplete);
            int done = render.SamplesDone;
            render.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal(done, render.SamplesDone);

            // The samples rendered so far stay available.
            var hdr = FinalRenderSupport.Luminance(render.ResultHdr(), render.Exposure);
            Assert.All(hdr.Rgba, v => Assert.True(float.IsFinite(v)));
            Assert.True(hdr.MeanLuminance(32, 24, 10) > 0f);
            var preview = render.Preview();
            Assert.Equal(64 * 48 * 4, preview.Pixels.Length);
            render.Dispose();
            Assert.Throws<ObjectDisposedException>(() => render.Advance(TimeSpan.FromSeconds(1)));
            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    private const int FinalRenderSettingsMax = VulkanFinalRender.MaxSamples;

    [SkippableFact]
    public void TimeLimitCompletesTheRenderAtAWholeSample()
    {
        var renderer = FinalRenderSupport.CreateOrSkip();
        try
        {
            var camera = BuildRoom(renderer);
            var options = new FinalRenderOptions { TimeLimit = TimeSpan.FromSeconds(1) };
            using var render = renderer.StartFinalRender(camera, FinalRenderSupport.Settings(48, 36, FinalRenderSettingsMax), options);
            var watch = Stopwatch.StartNew();
            while (!render.IsComplete && watch.Elapsed < TimeSpan.FromSeconds(60))
                render.Advance(TimeSpan.FromSeconds(0.25));
            Assert.True(render.IsComplete && !render.IsCancelled);
            Assert.InRange(render.SamplesDone, 1, FinalRenderSettingsMax - 1);
            Assert.True(render.Elapsed >= TimeSpan.FromSeconds(1) && render.Elapsed < TimeSpan.FromSeconds(30), $"elapsed {render.Elapsed}");
            // every pixel has the same number of samples (the pass in progress was finished)
            var raw = render.RawResultHdr();
            Assert.All(FinalRenderSupport.Luminance(raw, render.Exposure).Rgba, v => Assert.True(float.IsFinite(v)));
            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableFact]
    public void TimeLimitStopsAtThePassBoundaryWhenAPassTakesSeveralDispatches()
    {
        var renderer = FinalRenderSupport.CreateOrSkip();
        try
        {
            var camera = BuildRoom(renderer);
            var options = new FinalRenderOptions { TimeLimit = TimeSpan.FromSeconds(0.5) };
            using var render = renderer.StartFinalRender(camera, FinalRenderSupport.Settings(48, 160, FinalRenderSettingsMax), options);
            render.MaxRowsPerDispatch = 8; // 20 bands per pass: submissions end anywhere in a pass unless they stop at its end
            var watch = Stopwatch.StartNew();
            while (!render.IsComplete && watch.Elapsed < TimeSpan.FromSeconds(60))
                render.Advance(TimeSpan.FromSeconds(0.25));
            Assert.True(render.IsComplete && !render.IsCancelled);
            // one pass of this image takes milliseconds: the render ends right after the time is up
            Assert.True(render.Elapsed >= TimeSpan.FromSeconds(0.5) && render.Elapsed < TimeSpan.FromSeconds(1.5), $"elapsed {render.Elapsed}");
            Assert.InRange(render.SamplesDone, 2, FinalRenderSettingsMax - 1);
            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    /// <summary>
    /// Every pixel gets exactly one sample per pass, whatever the width: the bands of rows are sized from a pixel budget
    /// (65 536 / 1000 → 64 rows, 65 536 / 1100 → 56 rows here), so the last band of the 300 rows (44 or 20 rows) is not a
    /// multiple of the 8-row workgroup, and the rows its last workgroup overhangs must not be traced (twice).
    /// </summary>
    [SkippableFact]
    public void EveryPixelGetsOneSamplePerPassWhateverTheBandSize()
    {
        var renderer = FinalRenderSupport.CreateOrSkip();
        try
        {
            var camera = BuildRoom(renderer);
            foreach (int width in new[] { 1000, 1100 })
            {
                using var render = FinalRenderSupport.RenderToCompletion(renderer, camera, FinalRenderSupport.Settings(width, 300, 2, bounces: 1, volumeBounces: 0),
                    new FinalRenderOptions { BackgroundLuminance = 0f });
                Assert.Equal((2, 2), render.SampleCountRange());
                Assert.Equal(2, render.SamplesDone);
            }

            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    /// <summary>
    /// The editor's way: <c>Advance</c> called while the caller records its own frames adds work to them. The image is
    /// the same as rendered on its own (the samples depend only on pixel, sample index and seed), frames that also draw
    /// the real-time view stay validation-clean, and a time limit counts the wall-clock time the frames take.
    /// </summary>
    [SkippableFact]
    public void AdvanceInsideTheCallersFramesRendersTheSameImageAndKeepsTheTimeLimit()
    {
        var renderer = FinalRenderSupport.CreateOrSkip();
        try
        {
            var camera = BuildRoom(renderer);
            var settings = FinalRenderSupport.Settings(40, 30, 6);
            var options = new FinalRenderOptions { Seed = 9, BackgroundLuminance = 0f };
            byte[] alone;
            using (var render = FinalRenderSupport.RenderToCompletion(renderer, camera, settings, options))
                alone = render.RawResultHdr().Pixels;

            using var target = renderer.CreateTarget(40, 30);
            using (var render = renderer.StartFinalRender(camera, settings, options))
            {
                render.MaxRowsPerDispatch = 8;
                for (int frame = 0; frame < 1000 && !render.IsComplete; frame++)
                {
                    var open = renderer.Context.BeginFrame();
                    if (frame % 3 == 0)
                        renderer.Render(target, camera, new ViewOptions { ShowGrid = false }, new PreviewSettings(), 1f / 30f);
                    render.Advance(TimeSpan.FromMilliseconds(10));
                    renderer.Context.SubmitFrame(open);
                }

                Assert.True(render.IsComplete);
                Assert.Equal((6, 6), render.SampleCountRange());
                Assert.Equal(alone, render.RawResultHdr().Pixels);
            }

            using (var render = renderer.StartFinalRender(camera, FinalRenderSupport.Settings(40, 30, FinalRenderSettingsMax),
                       new FinalRenderOptions { TimeLimit = TimeSpan.FromSeconds(0.5) }))
            {
                var watch = Stopwatch.StartNew();
                while (!render.IsComplete && watch.Elapsed < TimeSpan.FromSeconds(30))
                {
                    var open = renderer.Context.BeginFrame();
                    render.Advance(TimeSpan.FromMilliseconds(10));
                    renderer.Context.SubmitFrame(open);
                    Thread.Sleep(5); // the rest of the editor's frame
                }

                Assert.True(render.IsComplete && !render.IsCancelled, $"still rendering after {watch.Elapsed} ({render.SamplesDone} samples)");
                Assert.True(render.Elapsed >= TimeSpan.FromSeconds(0.5) && render.Elapsed < TimeSpan.FromSeconds(5), $"elapsed {render.Elapsed}");
                Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"wall clock {watch.Elapsed}");
            }

            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    /// <summary>
    /// The editor advances a final render inside each of its frames, next to the real-time view: once warmed up, those
    /// frames allocate no managed memory (like the real-time renderer's steady state).
    /// </summary>
    [SkippableFact]
    public void AdvancingInsideTheCallersFramesAllocatesNothing()
    {
        var renderer = FinalRenderSupport.CreateOrSkip();
        try
        {
            var camera = BuildRoom(renderer);
            using var target = renderer.CreateTarget(32, 24);
            using var render = renderer.StartFinalRender(camera, FinalRenderSupport.Settings(32, 24, VulkanFinalRender.MaxSamples),
                new FinalRenderOptions { TimeLimit = TimeSpan.FromHours(1) });
            render.MaxRowsPerDispatch = 8;
            var view = new ViewOptions { ShowGrid = false };
            var settings = new PreviewSettings();
            void Frame()
            {
                var open = renderer.Context.BeginFrame();
                renderer.Render(target, camera, view, settings, 1f / 60f);
                render.Advance(TimeSpan.FromMilliseconds(5));
                renderer.Context.SubmitFrame(open);
            }

            for (int i = 0; i < 40; i++)
                Frame(); // warm-up: pipelines, descriptor sets, staging, JIT
            renderer.Context.WaitForSubmittedFrames();

            // one allocation-free window of 30 frames proves the steady state (one-off runtime events do not recur)
            var windows = new List<long>();
            for (int window = 0; window < 6; window++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 30; i++)
                    Frame();
                long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                if (allocated == 0)
                    break;
                windows.Add(allocated);
            }

            Assert.True(windows.Count < 6, "managed memory allocated in every window of 30 frames: " + string.Join(", ", windows) + " bytes");
            Assert.False(render.IsComplete);
            Assert.True(render.SamplesDone > 0);
            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableFact]
    public void DenoiserLowersTheErrorAndKeepsTheRawResult()
    {
        var renderer = FinalRenderSupport.CreateOrSkip();
        try
        {
            const int width = 64, height = 48;
            var camera = BuildRoom(renderer);
            float[] reference, guide;
            using (var render = FinalRenderSupport.RenderToCompletion(renderer, camera, FinalRenderSupport.Settings(width, height, 1024),
                       new FinalRenderOptions { Seed = 5, BackgroundLuminance = 0f }))
            {
                reference = Luminances(render.RawResultHdr(), render.Exposure);
                var normalDepth = render.ReadGuides().NormalDepth;
                guide = new float[width * height * 4];
                Buffer.BlockCopy(normalDepth.Pixels, 0, guide, 0, normalDepth.Pixels.Length);
            }

            var settings = FinalRenderSupport.Settings(width, height, 8);
            settings.Denoise = true;
            using var noisy = FinalRenderSupport.RenderToCompletion(renderer, camera, settings, new FinalRenderOptions { Seed = 1, BackgroundLuminance = 0f });
            var raw = Luminances(noisy.RawResultHdr(), noisy.Exposure);
            var denoised = Luminances(noisy.ResultHdr(), noisy.Exposure);
            Assert.Equal(denoised, Luminances(noisy.DenoisedResultHdr(), noisy.Exposure));
            FinalRenderSupport.Save(noisy, "pt-denoised");

            // Pixels inside surfaces (3 x 3 neighbourhood on one plane): the denoiser averages along surfaces only, so
            // that is where it must remove the noise; at silhouettes it keeps the (anti-aliasing) samples.
            bool Inside(int x, int y)
            {
                if (x < 1 || y < 1 || x >= width - 1 || y >= height - 1)
                    return false;
                int c = 4 * (y * width + x);
                var n = new Vector3(guide[c], guide[c + 1], guide[c + 2]);
                if (guide[c + 3] > 1e4f)
                    return false;
                for (int j = -1; j <= 1; j++)
                {
                    for (int i = -1; i <= 1; i++)
                    {
                        int k = 4 * ((y + j) * width + x + i);
                        if (Vector3.Dot(new Vector3(guide[k], guide[k + 1], guide[k + 2]), n) < 0.98f || MathF.Abs(guide[k + 3] - guide[c + 3]) > 0.03f * guide[c + 3])
                            return false;
                    }
                }

                return true;
            }

            double rawInside = 0, denoisedInside = 0;
            int count = 0;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    if (!Inside(x, y))
                        continue;
                    int i = y * width + x;
                    rawInside += (raw[i] - reference[i]) * (double)(raw[i] - reference[i]);
                    denoisedInside += (denoised[i] - reference[i]) * (double)(denoised[i] - reference[i]);
                    count++;
                }
            }

            rawInside = Math.Sqrt(rawInside / count);
            denoisedInside = Math.Sqrt(denoisedInside / count);
            double rawError = Rmse(raw, reference), denoisedError = Rmse(denoised, reference);
            _output.WriteLine($"RMSE inside surfaces ({count} px) raw {rawInside:0.000}, denoised {denoisedInside:0.000}; " +
                              $"whole image raw {rawError:0.000}, denoised {denoisedError:0.000} cd/m²");
            Assert.True(count > width * height / 4);
            Assert.True(denoisedInside < 0.5 * rawInside, $"denoised {denoisedInside}, raw {rawInside}");
            Assert.True(denoisedError < rawError);
            // The denoiser keeps the image's energy.
            Assert.InRange(denoised.Sum() / raw.Sum(), 0.97, 1.03);
            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableFact]
    public void PreviewAndAutoExposureFollowTheRealTimeView()
    {
        var renderer = FinalRenderSupport.CreateOrSkip();
        try
        {
            // An emissive panel filling the view: no noise, so the final render's display equals the real-time one.
            var world = renderer.World;
            var panel = world.CreateMesh(ProceduralMeshes.Plane(20f, 20f));
            var led = world.CreateMaterial(new MaterialData
            {
                Name = "LED wall",
                BaseColorFactor = new Vector4(0f, 0f, 0f, 1f),
                Roughness = 1f,
                Ior = 1f,
                EmissiveColor = new Vector3(1f, 0.5f, 0.25f),
                EmissiveLuminance = 150f,
            }, null);
            world.CreateInstance(panel, new[] { led }, Matrix4x4.Identity, InstanceFlags.None, 1);
            var view = new TopDownView(32, 24, 8f, ev100: 9f);
            ImageData realTime;
            using (var target = renderer.CreateTarget(32, 24))
            {
                renderer.Render(target, view.Camera, new ViewOptions { ShowGrid = false, ShowSelectionOutline = false },
                    new PreviewSettings { Bloom = false }, 0f);
                realTime = renderer.ReadPixels(target);
            }

            using (var render = FinalRenderSupport.RenderToCompletion(renderer, view.Camera, FinalRenderSupport.Settings(32, 24, 2)))
            {
                Assert.Equal(Units.ExposureFromEv100(9f), render.Exposure, 6);
                var preview = render.Preview();
                for (int i = 0; i < preview.Pixels.Length; i++)
                    Assert.InRange(Math.Abs(preview.Pixels[i] - realTime.Pixels[i]), 0, 2);
            }

            // Auto exposure: the stage metering of the real-time view (1.5 stops below middle grey for the lit part).
            using (var render = FinalRenderSupport.RenderToCompletion(renderer, view.Camera, FinalRenderSupport.Settings(32, 24, 2),
                       new FinalRenderOptions { AutoExposure = true, ExposureCompensation = 1f }))
            {
                render.Preview();
                float luminance = 150f * ColorSpaces.LuminanceRec2020(ColorSpaces.Rec709ToRec2020(new Vector3(1f, 0.5f, 0.25f)));
                float expected = Units.ExposureFromEv100(Units.Ev100FromLuminance(luminance) + VulkanRenderer.StageMeteringStops - 1f);
                _output.WriteLine($"auto exposure {render.Exposure:0.000000}, expected {expected:0.000000}");
                Assert.InRange(MathF.Abs(MathF.Log2(render.Exposure / expected)), 0f, 0.15f);
            }

            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    /// <summary>
    /// A panel may show a render's preview before its first sample (and after a cancellation before it): every result is
    /// a black image of the render's size, the denoised one included, without GPU errors; the render then proceeds.
    /// </summary>
    [SkippableFact]
    public void ResultsBeforeTheFirstSampleAreBlackAndValidationClean()
    {
        var renderer = FinalRenderSupport.CreateOrSkip();
        try
        {
            var camera = BuildRoom(renderer);
            var settings = FinalRenderSupport.Settings(24, 16, 2);
            settings.Denoise = true;
            using var render = renderer.StartFinalRender(camera, settings, new FinalRenderOptions { AutoExposure = true });
            var preview = render.Preview();
            Assert.Equal((24, 16), (preview.Width, preview.Height));
            foreach (var hdr in new[] { render.ResultHdr(), render.RawResultHdr(), render.DenoisedResultHdr(), render.ReadGuides().Albedo })
                Assert.All(FinalRenderSupport.Luminance(hdr, 1f).Rgba.Where((_, i) => (i & 3) != 3), v => Assert.Equal(0f, v));
            Assert.Equal(0, render.SamplesDone);
            TestVulkan.AssertNoValidationErrors(renderer.Context);

            while (!render.IsComplete)
                render.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal(2, render.SamplesDone);
            Assert.True(FinalRenderSupport.Luminance(render.ResultHdr(), render.Exposure).MeanLuminance(12, 8, 12) > 0f);
            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableFact]
    public void BadInputStaysFiniteAndValidationClean()
    {
        var renderer = FinalRenderSupport.CreateOrSkip();
        try
        {
            var world = renderer.World;
            // an empty world
            using (var render = FinalRenderSupport.RenderToCompletion(renderer, new RenderCamera(), FinalRenderSupport.Settings(8, 8, 2)))
                Assert.All(FinalRenderSupport.Luminance(render.ResultHdr(), render.Exposure).Rgba, v => Assert.True(float.IsFinite(v)));

            BuildRoom(renderer);
            var bad = StageScene.Spot(new Vector3(0f, 0f, 4f), Vector3.Zero, 1e30f, 30f, 40f);
            var nan = bad;
            nan.Position = new Vector3(float.NaN, 0f, 4f);
            var inf = bad;
            inf.PeakCandela = float.PositiveInfinity;
            var zero = bad;
            zero.Direction = Vector3.Zero;
            zero.SourceRadius = float.NaN;
            var weird = bad;
            weird.PeakCandela = 5000f;
            weird.PrismFacets = 1000;
            weird.Iris = -3f;
            weird.Frost = float.NaN;
            weird.SourceRadius = 1e6f;
            world.SetLights(new[] { bad, nan, inf, zero, weird });
            world.SetAtmosphere(new AtmosphereState
            {
                BaseScattering = float.NaN,
                Anisotropy = 7f,
                SmokeDensity = new VolumeGrid { Min = new Vector3(-1f), Max = new Vector3(1f), SizeX = 2, SizeY = 2, SizeZ = 2, Values = new[] { 1f, float.NaN, float.PositiveInfinity, -5f, 1e30f, 0f, 2f, 3f } },
                SmokeScatteringAtFullDensity = float.PositiveInfinity,
            });
            var camera = new RenderCamera { View = Matrix4x4.CreateLookAt(new Vector3(0f, -5f, 2f), Vector3.Zero, Vector3.UnitZ), FovY = float.NaN, Near = -1f, Ev100 = float.NaN };
            var settings = new FinalRenderSettings { Width = -5, Height = 100000, SamplesPerPixel = 0, MaxBounces = -3, MaxVolumeBounces = 1000, Denoise = true };
            using (var render = renderer.StartFinalRender(camera, settings, new FinalRenderOptions
            {
                FireflyClamp = float.NaN,
                ExposureCompensation = float.PositiveInfinity,
                BackgroundLuminance = -1f,
                DenoiseIterations = 99,
                TimeLimit = TimeSpan.FromSeconds(-1),
            }))
            {
                Assert.Equal(1, render.Width);
                while (!render.IsComplete)
                    render.Advance(TimeSpan.FromSeconds(1));
                Assert.All(FinalRenderSupport.Luminance(render.ResultHdr(), render.Exposure).Rgba, v => Assert.True(float.IsFinite(v)));
                Assert.All(FinalRenderSupport.Luminance(render.RawResultHdr(), render.Exposure).Rgba, v => Assert.True(float.IsFinite(v)));
                render.Preview();
            }

            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableFact]
    public void DevicesWithoutRayQueryReportThatTheFinalRenderIsNotSupported()
    {
        var options = TestVulkan.Options();
        options.EnableRayTracing = false;
        VulkanRenderer? renderer = null;
        try
        {
            renderer = VulkanRenderer.Create(options);
        }
        catch (VulkanUnavailableException e)
        {
            Skip.If(true, $"No Vulkan device available: {e.Message}");
        }

        try
        {
            Assert.False(renderer!.Info.SupportsRayTracing);
            var e = Assert.Throws<NotSupportedException>(() => renderer.StartFinalRender(new RenderCamera(), new FinalRenderSettings()));
            Assert.Contains("not supported on this GPU", e.Message);
            Assert.Contains(renderer.Info.DeviceName, e.Message);
            Assert.Throws<NotSupportedException>(() => ((IRenderer)renderer).StartFinalRender(new RenderCamera(), new FinalRenderSettings(), new FinalRenderOptions()));
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer!);
        }
    }
}
