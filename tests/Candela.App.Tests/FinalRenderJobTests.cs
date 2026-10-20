using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Candela.App.Panels;
using Candela.App.Session;
using Candela.App.Ui;
using Candela.Core.Math;
using Candela.Core.SceneGraph.Components;
using Candela.Engine;
using Candela.Rendering;
using Candela.Rendering.Vulkan;
using Hexa.NET.ImGui;
using StbImageSharp;

namespace Candela.App.Tests;

/// <summary>
/// The path-traced final render in the editor (WP-05): <c>Фокина.mvr</c> rendered by the Render panel's job and by
/// <c>candela render --final</c> with the same camera and samples gives the same PNG and EXR within the noise; samples,
/// bounces, volume bounces and denoising each change the output the way they should; the job stays within its frame
/// budget, shows progress images and stops within one frame on Cancel; without ray queries it renders in the Fast mode
/// and the panel says so; F12 and File → Render start a render with the panel's settings. Rendered on the headless
/// Vulkan device (lavapipe in CI, which has ray queries); skipped without one.
/// </summary>
[Collection(UiStateCollection.Name)]
public sealed class FinalRenderJobTests : IClassFixture<FinalRenderJobTests.BoxStage>
{
    private readonly BoxStage _box;

    public FinalRenderJobTests(BoxStage box) => _box = box;

    // ------------------------------------------------------------------------------------------------ the stage

    /// <summary>
    /// A small lit room shared by the tests of one renderer: a 10 × 10 m floor, a white back wall 4 m behind the centre,
    /// a moving head 4.5 m above the floor at full open white pointing down, and haze of 0.05 m⁻¹. Its pool on the floor
    /// lights the wall only by bounced light, and the beam is visible only through light scattered in the haze.
    /// </summary>
    public sealed class BoxStage : IDisposable
    {
        private readonly TempDir _dir = new();
        private VulkanRenderer? _renderer;
        private StageEngine? _engine;
        private string? _skip;
        private bool _created;

        /// <summary>The camera of the room's renders: the floor in front, the wall behind the beam.</summary>
        public RenderCamera Camera { get; } = CreateCamera();

        /// <summary>Folder for the files of a test.</summary>
        internal TempDir Dir => _dir;

        /// <summary>The renderer and the stage, or the test is skipped (no Vulkan device, no ray queries).</summary>
        public (VulkanRenderer Renderer, StageEngine Engine) GetOrSkip()
        {
            if (!_created)
            {
                _created = true;
                Create();
            }

            Skip.If(_skip is not null, _skip);
            return (_renderer!, _engine!);
        }

        private static RenderCamera CreateCamera()
        {
            var camera = RenderCamera.LookAt(new Vector3(0f, -6f, 2.2f), new Vector3(0f, 1.5f, 1.4f), 55f);
            camera.Ev100 = 8f;
            return camera;
        }

        private void Create()
        {
            try
            {
                _renderer = VulkanRenderer.Create(new VulkanContextOptions { CacheDirectory = Path.Combine(AppContext.BaseDirectory, "vk-cache") });
            }
            catch (VulkanUnavailableException e)
            {
                _skip = $"No Vulkan device available: {e.Message}";
                return;
            }

            if (!_renderer.Info.SupportsRayTracing)
            {
                _skip = $"{_renderer.Info.DeviceName} has no ray queries (the path tracer needs them).";
                return;
            }

            var engine = StageTestSupport.CreateEngine(_dir);
            StageTestSupport.AddWall(engine, "Floor", 10, 10, WallKind.Floor);
            var wall = StageTestSupport.AddWall(engine, "Back wall", 10, 5);
            wall.Local = new Transform(new Vector3(0f, 4f, 0f));
            var head = StageTestSupport.AddFixture(engine, "Spot", 1, new Vector3(0f, 0f, 4.5f));
            var fixture = head.Get<FixtureComponent>()!;
            fixture.ManualAttributes["Dimmer"] = 1f;
            fixture.ManualAttributes["Shutter1"] = 1f;
            fixture.NotifyChanged();
            var sync = engine.AttachRenderWorld(_renderer.World, loadFixtureModelsInBackground: false);
            sync.HazeOverride = 0.05f;
            for (int i = 0; i < 3; i++)
                engine.Tick(1.0 / 30);
            _engine = engine;
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            _engine?.Dispose();
            _renderer?.Dispose();
            _dir.Dispose();
        }
    }

    // ------------------------------------------------------------------------------------------------ helpers

    /// <summary>A path-traced request of the room's camera with these settings (manual exposure, seed 0).</summary>
    private static RenderRequest PathTraced(RenderCamera camera, string path, int width, int height, int samples, int bounces = 4,
        int volumeBounces = 2, bool denoise = false, bool exr = true)
    {
        var final = new FinalRenderSettings
        {
            Width = width,
            Height = height,
            SamplesPerPixel = samples,
            MaxBounces = bounces,
            MaxVolumeBounces = volumeBounces,
            Denoise = denoise,
            Format = exr ? ImageFileFormat.Exr : ImageFileFormat.Png,
            OutputPath = path,
        };
        var (ss, passes) = RenderJobRunner.Plan(width, height, samples);
        return new RenderRequest(camera, new ViewOptions { ShowGrid = false }, RenderPanel.HighQuality(new PreviewSettings(), final), width, height, ss,
            passes, path, null)
        {
            Mode = RenderMode.PathTraced,
            Final = final,
            FinalOptions = new FinalRenderOptions { AutoExposure = false },
            WriteExr = exr,
        };
    }

    /// <summary>Steps the job like the editor's frames until it ends (at most <paramref name="timeout"/>).</summary>
    private static void RunToEnd(RenderJobRunner job, StageEngine? engine, IRenderer renderer, TimeSpan? timeout = null)
    {
        var watch = Stopwatch.StartNew();
        var limit = timeout ?? TimeSpan.FromSeconds(240);
        for (int i = 0; job.State == RenderJobState.Running; i++)
        {
            Assert.True(watch.Elapsed < limit, $"the render did not end within {limit.TotalSeconds:0} s");
            job.Step(engine, renderer, i / 60.0);
        }
    }

    /// <summary>Renders <paramref name="request"/> with a new job and returns the EXR's luminance (cd/m² × exposure).</summary>
    private Luminance Render(RenderRequest request)
    {
        var (renderer, engine) = _box.GetOrSkip();
        using var job = new RenderJobRunner();
        Assert.True(job.Start(request, engine), job.Message);
        Assert.Equal(RenderMode.PathTraced, job.Mode);
        Assert.True(job.HoldsEngineClock, "the stage stands still under the samples");
        RunToEnd(job, engine, renderer);
        Assert.True(job.State == RenderJobState.Completed, job.Message);
        Assert.False(job.FellBack);
        Assert.Equal(request.Final.SamplesPerPixel, job.LastSamples);
        Assert.Equal(request.OutputPath, job.LastOutput);
        Assert.Equal(RenderJobRunner.ExrPath(request.OutputPath), job.LastExr);
        var png = LoadPng(request.OutputPath);
        Assert.Equal((request.Width, request.Height), (png.Width, png.Height));
        return Luminance.Read(job.LastExr!);
    }

    private static ImageResult LoadPng(string path)
    {
        Assert.True(File.Exists(path), $"{path} was not written");
        using var stream = File.OpenRead(path);
        return ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
    }

    /// <summary>Linear luminance of an EXR written by the job (Rec.2020 Y of R, G, B), with its layers.</summary>
    internal sealed record Luminance(int Width, int Height, float[] Y, IReadOnlyList<string> Channels)
    {
        public static Luminance Read(string path)
        {
            var exr = OpenExr.Read(path);
            var r = exr.Channel("R")!.Values;
            var g = exr.Channel("G")!.Values;
            var b = exr.Channel("B")!.Values;
            var y = new float[exr.Width * exr.Height];
            for (int i = 0; i < y.Length; i++)
                y[i] = 0.2627f * r[i] + 0.6780f * g[i] + 0.0593f * b[i];
            return new Luminance(exr.Width, exr.Height, y, exr.Channels.Select(c => c.Name).ToList());
        }

        /// <summary>Mean luminance of the rows [<paramref name="y0"/>, <paramref name="y1"/>) (fractions of the height).</summary>
        public double Mean(double y0 = 0, double y1 = 1)
        {
            int a = (int)(y0 * Height), b = Math.Max(a + 1, (int)(y1 * Height));
            double sum = 0;
            for (int y = a; y < b; y++)
            {
                for (int x = 0; x < Width; x++)
                    sum += Y[y * Width + x];
            }

            return sum / ((b - a) * Width);
        }

        /// <summary>
        /// Pixel noise: the mean absolute difference between each pixel and the average of its four neighbours, relative
        /// to the mean luminance (a smooth image gives about 0, Monte Carlo noise much more).
        /// </summary>
        public double Noise()
        {
            double diff = 0, sum = 0;
            int n = 0;
            for (int y = 1; y < Height - 1; y++)
            {
                for (int x = 1; x < Width - 1; x++)
                {
                    int i = y * Width + x;
                    double around = (Y[i - 1] + Y[i + 1] + Y[i - Width] + Y[i + Width]) / 4.0;
                    diff += Math.Abs(Y[i] - around);
                    sum += Y[i];
                    n++;
                }
            }

            return diff / Math.Max(sum, 1e-12);
        }
    }

    // ------------------------------------------------------------------------------------------------ CLI parity

    [SkippableFact]
    public void Fokina_rendered_in_the_editor_matches_candela_render_final()
    {
        Loc.SetLanguage("en");
        using var dir = new TempDir();
        using var renderer = FinalRenderSupportForApp.CreateOrSkip();
        const int Width = 96, Height = 54, Samples = 4;
        var eye = new Vector3(5.89f, -4.4f, 9.47f);
        var target = new Vector3(11.26f, 10.57f, 4.35f);
        const float Fov = 45f, Ev = 9f;

        // The command line: open white on every fixture (its default look), no floor added, a fixed camera and exposure.
        string cliPng = dir.File("cli.png"), cliExr = dir.File("cli.exr");
        var cli = CandelaCli.Run(dir, "render", StageTestSupport.Fokina, "--final", "--samples", Samples.ToString(CultureInfo.InvariantCulture),
            "--width", Width.ToString(CultureInfo.InvariantCulture), "--height", Height.ToString(CultureInfo.InvariantCulture), "--floor", "none",
            "--look", "open-white", "--camera", Vector(eye), "--target", Vector(target), "--fov", "45", "--ev", "9", "--library", StageTestSupport.Root,
            "--cache", dir.File("cli-cache"), "--out", cliPng, "--exr", cliExr);
        Assert.True(cli.ExitCode == 0, cli.Output);

        // The editor: the stage opened like File → Open, the same look applied through the engine, the panel's request
        // with the same settings, and the job stepped like the editor's frames.
        using var engine = StageTestSupport.CreateEngine(dir);
        var opened = engine.Open(StageTestSupport.Fokina);
        Assert.True(opened.Success, opened.Summary);
        StageLooks.ApplyOpenWhite(engine, new OpenWhiteOptions { Undoable = false });
        var sync = engine.AttachRenderWorld(renderer.World, loadFixtureModelsInBackground: false);
        engine.Tick(0);
        engine.Tick(0);
        sync.CompletePendingLoads(TimeSpan.FromMinutes(5));
        using var tools = StageTestSupport.CreateTools();
        tools.RendererInfo = renderer.Info;
        var panel = new RenderPanel(StagePanelContext.ForEngine(engine, tools), _ => { });
        var s = tools.FinalRender.Live;
        s.Width = Width;
        s.Height = Height;
        s.SamplesPerPixel = Samples;
        s.MaxBounces = 6;
        s.MaxVolumeBounces = 2;
        s.Denoise = false;
        s.Format = ImageFileFormat.Exr;
        s.OutputPath = dir.File("editor.png");
        tools.Preview.Live.AutoExposure = false;
        var camera = RenderCamera.LookAt(eye, target, Fov);
        camera.Ev100 = Ev;
        var request = panel.BuildRequest(s, null) with { Camera = camera };
        Assert.Equal(RenderMode.PathTraced, request.Mode);
        Assert.True(request.WriteExr);
        Assert.True(tools.Render.Start(request, engine), tools.Render.Message);
        RunToEnd(tools.Render, engine, renderer);
        Assert.True(tools.Render.State == RenderJobState.Completed, tools.Render.Message);
        Assert.Equal(Samples, tools.Render.LastSamples);
        Assert.Equal(dir.File("editor.exr"), tools.Render.LastExr);

        // PNG: the same picture (8-bit sRGB after the same display transform), block by block within the noise.
        var a = LoadPng(cliPng);
        var b = LoadPng(dir.File("editor.png"));
        Assert.Equal((a.Width, a.Height), (b.Width, b.Height));
        double meanA = MeanByte(a), meanB = MeanByte(b);
        Assert.True(meanA > 2, $"the beams show in the command line's picture (mean {meanA:0.00})");
        Assert.True(Math.Abs(meanA - meanB) <= 0.03 * meanA + 0.5, $"mean {meanA:0.00} (CLI) vs {meanB:0.00} (editor)");
        double blocks = BlockDifference(a, b, 8);
        Assert.True(blocks < 0.08, $"8 × 8 blocks differ by {blocks:P1} of the mean on average");

        // EXR: the same layers and the same linear values (cd/m² × the same exposure) within the noise.
        var ea = OpenExr.Read(cliExr);
        var eb = OpenExr.Read(dir.File("editor.exr"));
        Assert.Equal(ea.Channels.Select(c => c.Name).Order(), eb.Channels.Select(c => c.Name).Order());
        Assert.Contains("albedo.R", eb.Channels.Select(c => c.Name));
        Assert.Contains("Z", eb.Channels.Select(c => c.Name));
        Assert.Equal(ea.Colorimetry?.WhiteLuminance, eb.Colorimetry?.WhiteLuminance);
        var la = Luminance.Read(cliExr);
        var lb = Luminance.Read(dir.File("editor.exr"));
        double ma = la.Mean(), mb = lb.Mean();
        Assert.True(ma > 0, "the command line's EXR holds light");
        Assert.True(Math.Abs(ma - mb) <= 0.03 * ma, $"EXR mean {ma:0.####} (CLI) vs {mb:0.####} (editor)");
    }

    private static string Vector(Vector3 v) => string.Create(CultureInfo.InvariantCulture, $"{v.X},{v.Y},{v.Z}");

    private static double MeanByte(ImageResult image)
    {
        double sum = 0;
        for (int i = 0; i < image.Data.Length; i += 4)
            sum += 0.2126 * image.Data[i] + 0.7152 * image.Data[i + 1] + 0.0722 * image.Data[i + 2];
        return sum / (image.Width * image.Height);
    }

    /// <summary>Mean absolute difference of the blocks' mean luminance, relative to the image's mean.</summary>
    private static double BlockDifference(ImageResult a, ImageResult b, int block)
    {
        double diff = 0, total = 0;
        for (int by = 0; by + block <= a.Height; by += block)
        {
            for (int bx = 0; bx + block <= a.Width; bx += block)
            {
                double sa = 0, sb = 0;
                for (int y = by; y < by + block; y++)
                {
                    for (int x = bx; x < bx + block; x++)
                    {
                        int i = (y * a.Width + x) * 4;
                        sa += 0.2126 * a.Data[i] + 0.7152 * a.Data[i + 1] + 0.0722 * a.Data[i + 2];
                        sb += 0.2126 * b.Data[i] + 0.7152 * b.Data[i + 1] + 0.0722 * b.Data[i + 2];
                    }
                }

                diff += Math.Abs(sa - sb);
                total += sa;
            }
        }

        return diff / Math.Max(total, 1e-9);
    }

    // ------------------------------------------------------------------------------------------------ one test per setting

    [SkippableFact]
    public void More_samples_per_pixel_give_less_noise()
    {
        _box.GetOrSkip();
        var few = Render(PathTraced(_box.Camera, _box.Dir.File("spp-2.png"), 64, 48, 2));
        var many = Render(PathTraced(_box.Camera, _box.Dir.File("spp-32.png"), 64, 48, 32));
        Assert.True(few.Mean() > 0, "the room is lit");
        double ratio = many.Noise() / few.Noise();
        Assert.True(ratio < 0.6, $"32 samples per pixel are {ratio:P0} as noisy as 2 (expected about 1/4)");
        // Both estimate the same image: the means agree within the noise of the two-sample render.
        Assert.True(Math.Abs(many.Mean() - few.Mean()) < 0.25 * many.Mean(), $"mean {few.Mean():0.###} (2 spp) vs {many.Mean():0.###} (32 spp)");
    }

    [SkippableFact]
    public void Surface_bounces_light_the_wall_with_bounced_light()
    {
        _box.GetOrSkip();
        // No light scattered in the haze in either render: the wall outside the beam gets light only from bounces.
        var direct = Render(PathTraced(_box.Camera, _box.Dir.File("bounces-1.png"), 64, 48, 8, bounces: 1, volumeBounces: 0));
        var bounced = Render(PathTraced(_box.Camera, _box.Dir.File("bounces-4.png"), 64, 48, 8, bounces: 4, volumeBounces: 0));
        double wallDirect = direct.Mean(0, 0.3), wallBounced = bounced.Mean(0, 0.3);
        Assert.True(wallBounced > 1.3 * wallDirect + 1e-6,
            $"the upper part of the picture (the wall) is {wallBounced:0.####} with 4 bounces and {wallDirect:0.####} with direct light only");
        Assert.True(bounced.Mean() > direct.Mean(), "bounced light adds to the whole picture");
    }

    [SkippableFact]
    public void Volume_bounces_make_the_beam_visible_in_the_haze()
    {
        _box.GetOrSkip();
        var none = Render(PathTraced(_box.Camera, _box.Dir.File("volume-0.png"), 64, 48, 8, bounces: 1, volumeBounces: 0));
        var scattered = Render(PathTraced(_box.Camera, _box.Dir.File("volume-2.png"), 64, 48, 8, bounces: 1, volumeBounces: 2));
        // The upper part of the picture looks through the air the beam crosses: with scattering it glows.
        double air0 = none.Mean(0, 0.3), air2 = scattered.Mean(0, 0.3);
        Assert.True(air2 > 1.5 * air0 + 1e-6, $"the air is {air2:0.####} with 2 volume bounces and {air0:0.####} without");
        Assert.True(scattered.Mean() > none.Mean(), "scattered light adds to the picture");
    }

    [SkippableFact]
    public void Denoising_smooths_the_picture_and_keeps_the_raw_result_in_the_exr()
    {
        _box.GetOrSkip();
        var raw = Render(PathTraced(_box.Camera, _box.Dir.File("denoise-off.png"), 64, 48, 16));
        var denoised = Render(PathTraced(_box.Camera, _box.Dir.File("denoise-on.png"), 64, 48, 16, denoise: true));
        double ratio = denoised.Noise() / raw.Noise();
        Assert.True(ratio < 0.7, $"the denoised picture is {ratio:P0} as noisy as the raw one");
        Assert.Contains("raw.R", denoised.Channels);
        Assert.DoesNotContain("raw.R", raw.Channels);
        Assert.True(Math.Abs(denoised.Mean() - raw.Mean()) < 0.25 * raw.Mean(),
            $"denoising keeps the brightness: mean {raw.Mean():0.#####} raw, {denoised.Mean():0.#####} denoised; noise {raw.Noise():0.###} raw, {denoised.Noise():0.###} denoised");
    }

    // ------------------------------------------------------------------------------------------------ responsiveness, cancel

    [SkippableFact]
    public void The_editor_stays_responsive_the_progress_image_updates_and_cancel_stops_within_one_frame()
    {
        Loc.SetLanguage("en");
        var (renderer, engine) = _box.GetOrSkip();
        string path = _box.Dir.File("long.png");
        using var job = new RenderJobRunner();
        // Far more samples than the test waits for: it only ends by Cancel.
        Assert.True(job.Start(PathTraced(_box.Camera, path, 96, 72, 4096), engine), job.Message);
        Assert.Null(job.PreviewImage);
        var versions = new HashSet<int>();
        var slowest = TimeSpan.Zero;
        var watch = Stopwatch.StartNew();
        long firstSample = 0;
        int lastSamples = 0;
        for (int frame = 0; job.State == RenderJobState.Running; frame++)
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(120), "the render made no progress");
            long start = Stopwatch.GetTimestamp();
            job.Step(engine, renderer, frame / 60.0);
            var took = Stopwatch.GetElapsedTime(start);
            Assert.True(job.SamplesDone >= lastSamples, "samples only grow");
            lastSamples = job.SamplesDone;
            if (job.SamplesDone == 0)
                continue; // starting the path tracer (pipelines, acceleration structures) is not a sampling frame

            if (firstSample == 0)
            {
                firstSample = Stopwatch.GetTimestamp();
            }
            else
            {
                // A sampling frame: the budget of 60 ms, plus at most one dispatch (planned to stay below 0.25 s) and the
                // read-back of a progress image.
                slowest = took > slowest ? took : slowest;
            }

            Assert.NotNull(job.PreviewImage);
            versions.Add(job.PreviewVersion);
            if (versions.Count >= 3 && Stopwatch.GetElapsedTime(firstSample) > TimeSpan.FromSeconds(2.2))
                break;
        }

        Assert.Equal(RenderJobState.Running, job.State);
        Assert.True(slowest < TimeSpan.FromSeconds(0.6), $"the slowest sampling frame took {slowest.TotalMilliseconds:0} ms");
        Assert.True(versions.Count >= 3, $"the progress image changed {versions.Count} times");
        Assert.InRange(job.Progress, 1e-6, 0.5);
        Assert.Equal((96, 72), (job.PreviewImage!.Width, job.PreviewImage.Height));

        // Cancel: the job ends at once, gives the engine clock back, issues no more GPU work and writes nothing.
        long cancelStart = Stopwatch.GetTimestamp();
        job.Cancel();
        var cancelTook = Stopwatch.GetElapsedTime(cancelStart);
        Assert.Equal(RenderJobState.Cancelled, job.State);
        Assert.False(job.HoldsEngineClock);
        Assert.Null(job.WorkLightInUse);
        Assert.True(cancelTook < TimeSpan.FromSeconds(1), $"Cancel took {cancelTook.TotalMilliseconds:0} ms");
        int samples = job.SamplesDone;
        long next = Stopwatch.GetTimestamp();
        job.Step(engine, renderer, 1000);
        Assert.True(Stopwatch.GetElapsedTime(next) < TimeSpan.FromMilliseconds(50), "a cancelled job does no work in the next frame");
        Assert.Equal(samples, job.SamplesDone);
        Assert.False(File.Exists(path), "a cancelled frame is not written");
        Assert.Null(job.LastOutput);
        Assert.Contains("Cancelled", job.Message, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void Stop_and_save_writes_the_samples_done_so_far()
    {
        Loc.SetLanguage("en");
        var (renderer, engine) = _box.GetOrSkip();
        string path = _box.Dir.File("stopped.png");
        using var job = new RenderJobRunner();
        Assert.True(job.Start(PathTraced(_box.Camera, path, 48, 36, 4096), engine), job.Message);
        for (int frame = 0; job.SamplesDone < 2; frame++)
        {
            Assert.True(frame < 10_000 && job.State == RenderJobState.Running, job.Message);
            job.Step(engine, renderer, frame / 60.0);
        }

        Assert.True(job.StopAndSave());
        Assert.Equal(RenderJobState.Completed, job.State);
        Assert.True(job.LastSamples >= 2);
        Assert.Equal(path, job.LastOutput);
        Assert.True(File.Exists(RenderJobRunner.ExrPath(path)));
        Assert.Contains("Stopped at", job.Message, StringComparison.Ordinal);
        Assert.Equal((48, 36), (LoadPng(path).Width, LoadPng(path).Height));
    }

    // ------------------------------------------------------------------------------------------------ no ray queries

    [SkippableFact]
    public void Without_ray_queries_the_render_falls_back_to_fast_and_says_so()
    {
        Loc.SetLanguage("en");
        using var dir = new TempDir();
        using var engine = StageTestSupport.CreateEngine(dir);
        StageTestSupport.AddWall(engine, "Floor", 6, 6, WallKind.Floor);
        var renderer = new MemoryRenderer();
        Assert.False(renderer.Info.SupportsRayTracing);
        engine.AttachRenderWorld(renderer.World, loadFixtureModelsInBackground: false);
        engine.Tick(0);
        using var tools = StageTestSupport.CreateTools();
        var panel = new RenderPanel(StagePanelContext.ForEngine(engine, tools), _ => { });
        var s = tools.FinalRender.Live;
        s.Width = 64;
        s.Height = 36;
        s.SamplesPerPixel = 4;
        s.Format = ImageFileFormat.Exr;
        s.OutputPath = dir.File("fallback.png");

        // Before the renderer is known the panel promises the path tracer; once it is, it says what will happen.
        Assert.Null(panel.RendererNote);
        Assert.StartsWith("Path traced, 4 samples per pixel", panel.PlanText(s), StringComparison.Ordinal);
        tools.RendererInfo = renderer.Info;
        Assert.True(panel.PathTracerUnavailable);
        Assert.Contains("no ray queries", panel.RendererNote, StringComparison.Ordinal);
        Assert.Contains("memory", panel.RendererNote, StringComparison.Ordinal);
        Assert.Contains("supersampling", panel.PlanText(s), StringComparison.Ordinal);
        using (var ui = new HeadlessImGui(new Vector2(900, 1400)))
        {
            string text = SafeTextTests.Capture(ui, () =>
            {
                ImGui.SetNextWindowPos(Vector2.Zero);
                ImGui.SetNextWindowSize(new Vector2(460, 1300));
                ImGui.Begin("render-host");
                panel.Draw();
                ImGui.End();
            });
            Assert.Contains("no ray queries", text, StringComparison.Ordinal);
            Assert.Contains("Fast renders with the real-time renderer", text, StringComparison.Ordinal);
        }

        // The panel's request asks for the path tracer; the job renders it in the Fast mode and says so.
        Assert.True(panel.StartFromCommand(), tools.Render.Message);
        var job = tools.Render;
        RunToEnd(job, engine, renderer, TimeSpan.FromSeconds(60));
        Assert.True(job.State == RenderJobState.Completed, job.Message);
        Assert.True(job.FellBack);
        Assert.Equal(RenderMode.Fast, job.Mode);
        Assert.Equal(dir.File("fallback.png"), job.LastOutput);
        Assert.Null(job.LastExr);
        Assert.False(File.Exists(dir.File("fallback.exr")), "the Fast mode writes no EXR");
        Assert.Contains("rendered in the Fast real-time mode", job.Message, StringComparison.Ordinal);
        Assert.Equal((64, 36), (LoadPng(dir.File("fallback.png")).Width, LoadPng(dir.File("fallback.png")).Height));
        Assert.True(renderer.RenderedLights.Count >= 1, "the real-time renderer rendered the frame");
    }

    // ------------------------------------------------------------------------------------------------ F12 / File → Render

    [Fact]
    public void Render_command_starts_a_render_with_the_panels_settings_and_leaves_a_running_one_alone()
    {
        Loc.SetLanguage("en");
        using var dir = new TempDir();
        using var engine = StageTestSupport.CreateEngine(dir);
        using var tools = StageTestSupport.CreateTools();
        var reports = new List<SessionResult>();
        var panel = new RenderPanel(StagePanelContext.ForEngine(engine, tools), reports.Add)
        {
            Mode = RenderMode.PathTraced,
            WorkLight = WorkLightMode.On,
        };
        var s = tools.FinalRender.Live;
        s.Width = 320;
        s.Height = 200;
        s.SamplesPerPixel = 48;
        s.MaxBounces = 3;
        s.MaxVolumeBounces = 5;
        s.Denoise = true;
        s.ToneMapper = ToneMapper.Aces;
        s.Format = ImageFileFormat.Exr;
        s.OutputPath = dir.File(Path.Combine("shots", "f12.png"));
        tools.Preview.Live.AutoExposure = true;
        tools.Preview.Live.ExposureCompensation = 1.5f;

        Assert.True(panel.StartFromCommand());
        var job = tools.Render;
        Assert.Equal(RenderJobState.Running, job.State);
        Assert.Equal(dir.File(Path.Combine("shots", "f12.png")), job.OutputPath);
        Assert.Equal(RenderMode.PathTraced, job.Mode);
        Assert.Equal(48, job.SamplesTotal);
        Assert.Equal(WorkLightMode.On, job.WorkLightInUse);
        Assert.True(Directory.Exists(dir.File("shots")), "the output folder is created when the render starts");
        Assert.Contains(reports, r => r.Message.Contains("320 × 200", StringComparison.Ordinal));
        var request = panel.BuildRequest(s, null);
        Assert.Equal((3, 5, true, ToneMapper.Aces), (request.Final.MaxBounces, request.Final.MaxVolumeBounces, request.Final.Denoise, request.Final.ToneMapper));
        Assert.True(request.WriteExr);
        Assert.True(request.FinalOptions.AutoExposure);
        Assert.Equal(1.5f, request.FinalOptions.ExposureCompensation);

        // F12 again while it runs: the render goes on (it is not restarted).
        Assert.False(panel.StartFromCommand());
        Assert.Equal(RenderJobState.Running, job.State);
        job.Cancel();

        // Fast mode and zero bounces are taken as they are.
        panel.Mode = RenderMode.Fast;
        s.MaxBounces = 0;
        Assert.True(panel.StartFromCommand());
        Assert.Equal(RenderMode.Fast, job.Mode);
        Assert.Equal(0, panel.BuildRequest(s, null).Final.MaxBounces);
        job.Cancel();
    }

    [Fact]
    public void F12_is_the_render_key_and_the_file_menu_has_render()
    {
        var bindings = Candela.Core.Settings.KeyBindingSet.CreateDefault();
        Assert.Contains(bindings.ChordsFor(Candela.Core.Settings.EditorActions.Render), c => c.ToString() == "F12");
        Assert.Equal(new[] { Candela.Core.Settings.EditorActions.Render },
            bindings.ActionsFor(bindings.ChordsFor(Candela.Core.Settings.EditorActions.Render).First()));
        Loc.SetLanguage("en");
        Assert.Equal("Render", Loc.Get(Str.FileRender));
        Loc.SetLanguage("ru");
        Assert.Equal("Рендер", Loc.Get(Str.FileRender));
        Loc.SetLanguage("en");
    }

    [SkippableFact]
    public void F12_and_file_render_start_renders_in_the_running_editor()
    {
        using var dir = new TempDir();
        string png = dir.File("f12.png");
        string project = dir.File("stage.candela");
        using (var engine = StageTestSupport.CreateEngine(dir))
        {
            StageTestSupport.AddWall(engine, "Floor", 8, 8, WallKind.Floor);
            var head = StageTestSupport.AddFixture(engine, "Spot", 1, new Vector3(0f, 0f, 4f));
            var fixture = head.Get<FixtureComponent>()!;
            fixture.ManualAttributes["Dimmer"] = 1f;
            fixture.ManualAttributes["Shutter1"] = 1f;
            engine.View.FinalRender = new FinalRenderSettings
            {
                Width = 48,
                Height = 32,
                SamplesPerPixel = 2,
                Denoise = false,
                Format = ImageFileFormat.Exr,
                OutputPath = png,
            };
            engine.View.Camera = new SavedCamera { Eye = new Vector3(0f, -7f, 3f), Target = new Vector3(0f, 0f, 0.5f) };
            var saved = engine.SaveAs(project);
            Assert.True(saved.Success, saved.Summary);
        }

        string script = dir.Write("render.json", """
            { "actions": [
                { "type": "wait", "frames": 10 },
                { "type": "key", "chord": "F12" },
                { "type": "wait", "frames": 40 },
                { "type": "menu", "path": "File/Render" },
                { "type": "wait", "frames": 40 }
            ] }
            """);
        var run = EditorProcess.Run(dir, new[] { "--open", project, "--screenshot", dir.File("end.png"), "--script", script });
        Assert.True(run.ExitCode == ExitCodes.Ok, run.Output);
        Assert.DoesNotContain("ERR [", run.Output);
        // Two renders, one per command, each written as PNG and EXR (the log line of the finished render names both).
        int written = run.Output.Split('\n').Count(l => l.Contains("Written", StringComparison.Ordinal) && l.Contains("f12.png", StringComparison.Ordinal) &&
                                                       l.Contains("f12.exr", StringComparison.Ordinal));
        Assert.True(written == 2, $"{written} renders were written:\n{run.Output}");
        Assert.Equal((48, 32), (LoadPng(png).Width, LoadPng(png).Height));
        Assert.True(File.Exists(dir.File("f12.exr")));
    }
}

/// <summary>Runs the <c>candela</c> command line (copied next to the tests by the project reference) in its own process.</summary>
internal static class CandelaCli
{
    public sealed record Result(int ExitCode, string Output);

    /// <summary>Runs <c>candela</c> with <paramref name="args"/>; settings and caches go to <paramref name="dir"/>.</summary>
    public static Result Run(TempDir dir, params string[] args)
    {
        string dll = Path.Combine(AppContext.BaseDirectory, "candela.dll");
        Assert.True(File.Exists(dll), $"{dll} is missing");
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add(dll);
        foreach (string a in args)
            psi.ArgumentList.Add(a);
        // An empty configuration (default settings) and private caches: the run does not depend on the machine's settings.
        psi.Environment["CANDELA_CONFIG_DIR"] = dir.File("cli-config");
        psi.Environment["CANDELA_CACHE_DIR"] = dir.File("cli-cache");
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromSeconds(240)))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("candela did not exit within 240 s");
        }

        process.WaitForExit();
        string output = stdout.Result + stderr.Result;
        Skip.If(process.ExitCode == 3 && output.Contains("Vulkan", StringComparison.OrdinalIgnoreCase), "no Vulkan device: " + output);
        return new Result(process.ExitCode, output);
    }
}

/// <summary>A renderer for the path tracer tests of the editor: a Vulkan device with ray queries, or the test is skipped.</summary>
internal static class FinalRenderSupportForApp
{
    public static VulkanRenderer CreateOrSkip()
    {
        VulkanRenderer renderer;
        try
        {
            renderer = VulkanRenderer.Create(new VulkanContextOptions { CacheDirectory = Path.Combine(AppContext.BaseDirectory, "vk-cache") });
        }
        catch (VulkanUnavailableException e)
        {
            Skip.If(true, $"No Vulkan device available: {e.Message}");
            throw;
        }

        if (!renderer.Info.SupportsRayTracing)
        {
            string name = renderer.Info.DeviceName;
            renderer.Dispose();
            Skip.If(true, $"{name} has no ray queries (the path tracer needs them).");
        }

        return renderer;
    }
}
