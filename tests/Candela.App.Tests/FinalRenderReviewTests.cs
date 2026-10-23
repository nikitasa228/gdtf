using System.Diagnostics;
using System.Numerics;
using Candela.App.Session;
using Candela.App.Ui;
using Candela.Core.Geometry;
using Candela.Core.Math;
using Candela.Core.SceneGraph.Components;
using Candela.Dmx.Input;
using Candela.Engine;
using Candela.Rendering;
using Candela.Rendering.Vulkan;
using StbImageSharp;

namespace Candela.App.Tests;

/// <summary>
/// Review of the editor's path-traced render (WP-05): a scene replaced inside the editor's one engine (File → New, Open,
/// recovery) cancels the running render; a path-traced timecode sequence with auto exposure and exposure compensation
/// keeps the first frame's exposure (the compensation is not applied twice); the first frame of a sequence is the
/// range's start even when the editor ticked the stage once more after the render started; path tracer dispatches are
/// sized to the editor's frame budget, so a large image does not hold every editor frame for a quarter of a second.
/// </summary>
[Collection(UiStateCollection.Name)]
public sealed class FinalRenderReviewTests
{
    // ------------------------------------------------------------------------------------------------ helpers

    /// <summary>A recording of <paramref name="seconds"/> s that carries timecode only (the fixtures keep their manual look).</summary>
    private static DmxRecording TimecodeOnly(double seconds)
    {
        var frames = new List<DmxRecordedFrame>();
        for (int i = 0; i <= (int)Math.Round(seconds * 25); i++)
            frames.Add(new DmxRecordedFrame(i / 25.0, new Timecode(0, 0, i / 25, i % 25, TimecodeType.Ebu25), Array.Empty<DmxUniverseChange>()));
        return new DmxRecording("tc", frames);
    }

    private static RenderRequest Request(string path, RenderMode mode, RenderRange? range = null, int width = 32, int height = 32, int samples = 2) =>
        new(new RenderCamera { Ev100 = 6f }, new ViewOptions { ShowGrid = false }, new PreviewSettings(), width, height, 1, 1, path, range)
        {
            Mode = mode,
            Final = new FinalRenderSettings { Width = width, Height = height, SamplesPerPixel = samples, Denoise = false },
            FinalOptions = new FinalRenderOptions { AutoExposure = false },
        };

    private static void RunToEnd(RenderJobRunner job, StageEngine? engine, IRenderer renderer, TimeSpan? timeout = null)
    {
        var watch = Stopwatch.StartNew();
        var limit = timeout ?? TimeSpan.FromSeconds(120);
        for (int i = 0; job.State == RenderJobState.Running; i++)
        {
            Assert.True(watch.Elapsed < limit, $"the render did not end within {limit.TotalSeconds:0} s");
            job.Step(engine, renderer, i / 60.0);
        }
    }

    private static double MeanLuma(string path)
    {
        Assert.True(File.Exists(path), $"{path} was not written");
        using var stream = File.OpenRead(path);
        var image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
        double sum = 0;
        for (int i = 0; i < image.Data.Length; i += 4)
            sum += 0.2126 * image.Data[i] + 0.7152 * image.Data[i + 1] + 0.0722 * image.Data[i + 2];
        return sum / (image.Width * image.Height);
    }

    // ------------------------------------------------------------------------------------------------ stage replaced

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_scene_replaced_in_the_same_engine_cancels_the_running_render(bool pathTraced)
    {
        var mode = pathTraced ? RenderMode.PathTraced : RenderMode.Fast;
        Loc.SetLanguage("en");
        using var dir = new TempDir();
        using var engine = StageTestSupport.CreateEngine(dir);
        StageTestSupport.AddWall(engine, "Floor", 6, 6, WallKind.Floor);
        var renderer = new FakePathTracer();
        engine.AttachRenderWorld(renderer.World, loadFixtureModelsInBackground: false);
        engine.Tick(0);
        using var job = new RenderJobRunner();

        // File → New / Open / recovery keep the editor's one StageEngine and replace its scene (and reset its clock).
        string still = dir.File("still.png");
        Assert.True(job.Start(Request(still, mode), engine), job.Message);
        engine.New("Another stage");
        job.Step(engine, renderer, 0);
        Assert.Equal(RenderJobState.Cancelled, job.State);
        Assert.Empty(renderer.Started);
        Assert.Empty(renderer.RenderedFrames);
        Assert.False(File.Exists(still), "nothing of the old stage is written");

        // A sequence: the recording it played into the stage stops as well.
        string sequence = dir.File("seq.png");
        Assert.True(job.Start(Request(sequence, mode, new RenderRange(TimecodeOnly(2), 0.5, 0.6, 25)), engine), job.Message);
        Assert.NotNull(engine.Player);
        Assert.True(engine.Open(SaveEmptyProject(dir)).Success);
        job.Step(engine, renderer, 1);
        Assert.Equal(RenderJobState.Cancelled, job.State);
        Assert.Null(engine.Player);
        Assert.Empty(renderer.Started);
        Assert.Empty(renderer.RenderedFrames);
        Assert.False(job.HoldsEngineClock);
    }

    [Fact]
    public void Stop_and_save_after_the_scene_was_replaced_writes_nothing()
    {
        Loc.SetLanguage("en");
        using var dir = new TempDir();
        using var engine = StageTestSupport.CreateEngine(dir);
        var renderer = new FakePathTracer();
        engine.AttachRenderWorld(renderer.World, loadFixtureModelsInBackground: false);
        engine.Tick(0);
        using var job = new RenderJobRunner();
        string path = dir.File("stopped.png");
        Assert.True(job.Start(Request(path, RenderMode.PathTraced, samples: 100), engine), job.Message);
        job.Step(engine, renderer, 0);
        Assert.True(job.SamplesDone > 0);
        engine.New("Another stage");
        Assert.False(job.StopAndSave(), "the samples belong to the stage that was closed");
        Assert.Equal(RenderJobState.Cancelled, job.State);
        Assert.False(File.Exists(path));
    }

    private static string SaveEmptyProject(TempDir dir)
    {
        string project = dir.File("other.candela");
        using var otherDir = new TempDir();
        using var other = StageTestSupport.CreateEngine(otherDir);
        Assert.True(other.SaveAs(project).Success);
        return project;
    }

    // ------------------------------------------------------------------------------------------------ sequence timing

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_first_frame_of_a_sequence_is_the_range_start_even_after_one_more_editor_tick(bool pathTraced)
    {
        var mode = pathTraced ? RenderMode.PathTraced : RenderMode.Fast;
        Loc.SetLanguage("en");
        using var dir = new TempDir();
        using var engine = StageTestSupport.CreateEngine(dir);
        StageTestSupport.AddFixture(engine, "Spot", 1, new Vector3(0, 0, 6));
        var renderer = new FakePathTracer { Engine = engine };
        engine.AttachRenderWorld(renderer.World, loadFixtureModelsInBackground: false);
        engine.Tick(0);
        using var job = new RenderJobRunner();
        var range = new RenderRange(TimecodeOnly(4), 2.0, 2.08, 25);
        Assert.Equal(3, range.FrameCount);
        Assert.True(job.Start(Request(dir.File("seq.png"), mode, range), engine), job.Message);
        Assert.Equal(2.0, engine.Player!.Position, 9);

        // The editor's frame after the one that started the render ticks the stage once more (it set its clock hold in
        // the previous frame, before the render started); a slow start-up frame makes that tick long.
        engine.Tick(0.5);
        Assert.Equal(2.5, engine.Player!.Position, 9);
        RunToEnd(job, engine, renderer);
        Assert.True(job.State == RenderJobState.Completed, job.Message);
        var positions = mode == RenderMode.PathTraced ? renderer.Started.Select(s => s.Position).ToList() : renderer.RenderedFrames;
        Assert.Equal(new[] { 2.0, 2.04, 2.08 }, positions.Select(p => Math.Round(p, 6)));
    }

    // ------------------------------------------------------------------------------------------------ sequence exposure

    [Fact]
    public void A_path_traced_sequence_with_compensation_keeps_the_first_frames_exposure()
    {
        Loc.SetLanguage("en");
        using var dir = new TempDir();
        using var engine = StageTestSupport.CreateEngine(dir);
        var renderer = new FakePathTracer { MeteredEv100 = 10f };
        engine.AttachRenderWorld(renderer.World, loadFixtureModelsInBackground: false);
        engine.Tick(0);
        using var job = new RenderJobRunner();
        var request = Request(dir.File("seq.png"), RenderMode.PathTraced, new RenderRange(TimecodeOnly(2), 0.4, 0.48, 25)) with
        {
            FinalOptions = new FinalRenderOptions { AutoExposure = true, ExposureCompensation = 2f },
            WriteExr = true,
        };
        Assert.True(job.Start(request, engine), job.Message);
        RunToEnd(job, engine, renderer);
        Assert.True(job.State == RenderJobState.Completed, job.Message);
        Assert.Equal(3, renderer.Started.Count);
        Assert.True(renderer.Started[0].AutoExposure);
        Assert.All(renderer.Started.Skip(1), s => Assert.False(s.AutoExposure));
        // Every frame is exposed like the first: metered EV100 10, compensated by +2 stops once.
        float first = renderer.Started[0].Render.Exposure;
        Assert.Equal(Units.ExposureFromEv100(8f), first, 6);
        Assert.All(renderer.Started, s => Assert.Equal(first, s.Render.Exposure, 6));
        for (int i = 1; i <= 3; i++)
        {
            var exr = OpenExr.Read(dir.File($"seq_000{i}.exr"));
            Assert.Equal(1f / first, exr.Colorimetry!.WhiteLuminance!.Value, 2);
        }
    }

    [SkippableFact]
    public void A_path_traced_sequence_writes_png_and_exr_frames_exposed_alike()
    {
        Loc.SetLanguage("en");
        using var dir = new TempDir();
        using var renderer = FinalRenderSupportForApp.CreateOrSkip();
        using var engine = LitRoom(dir, renderer);
        using var job = new RenderJobRunner();
        string path = dir.File(Path.Combine("seq", "shot.png"));
        var camera = RenderCamera.LookAt(new Vector3(0f, -6f, 2.2f), new Vector3(0f, 1.5f, 1.4f), 55f);
        camera.Ev100 = 8f;
        var request = new RenderRequest(camera, new ViewOptions { ShowGrid = false }, new PreviewSettings(), 48, 32, 1, 1, path,
            new RenderRange(TimecodeOnly(2), 0.4, 0.48, 25))
        {
            Mode = RenderMode.PathTraced,
            Final = new FinalRenderSettings { Width = 48, Height = 32, SamplesPerPixel = 4, MaxBounces = 2, MaxVolumeBounces = 1, Denoise = false },
            FinalOptions = new FinalRenderOptions { AutoExposure = true, ExposureCompensation = 2f },
            WriteExr = true,
        };
        Assert.True(job.Start(request, engine), job.Message);
        Assert.True(job.HoldsEngineClock);
        RunToEnd(job, engine, renderer, TimeSpan.FromSeconds(180));
        Assert.True(job.State == RenderJobState.Completed, job.Message);
        Assert.Null(engine.Player);
        Assert.False(job.HoldsEngineClock);
        Assert.Equal(dir.File(Path.Combine("seq", "shot_0003.png")), job.LastOutput);
        Assert.Equal(dir.File(Path.Combine("seq", "shot_0003.exr")), job.LastExr);

        // The stage stands still and the recording carries no DMX: three frames of the same picture, one exposure.
        var white = new List<float>();
        var means = new List<double>();
        for (int i = 1; i <= 3; i++)
        {
            var exr = OpenExr.Read(dir.File(Path.Combine("seq", $"shot_000{i}.exr")));
            Assert.Equal((48, 32), (exr.Width, exr.Height));
            white.Add(exr.Colorimetry!.WhiteLuminance!.Value);
            means.Add(MeanLuma(dir.File(Path.Combine("seq", $"shot_000{i}.png"))));
        }

        Assert.True(means[0] > 2, $"the room is lit (mean {means[0]:0.0})");
        Assert.All(white, w => Assert.True(Math.Abs(w - white[0]) <= 1e-4 * white[0], $"whiteLuminance {string.Join(", ", white)}"));
        Assert.All(means, m => Assert.True(Math.Abs(m - means[0]) <= 0.03 * means[0] + 0.5, $"PNG means {string.Join(", ", means.Select(x => x.ToString("0.0")))}"));
    }

    // ------------------------------------------------------------------------------------------------ responsiveness

    [SkippableFact]
    public void A_large_path_traced_image_advances_within_the_frame_budget()
    {
        Loc.SetLanguage("en");
        using var dir = new TempDir();
        using var renderer = FinalRenderSupportForApp.CreateOrSkip();
        using var engine = LitRoom(dir, renderer);
        using var job = new RenderJobRunner();
        var camera = RenderCamera.LookAt(new Vector3(0f, -6f, 2.2f), new Vector3(0f, 1.5f, 1.4f), 55f);
        camera.Ev100 = 8f;
        const int Width = 960, Height = 540;
        var request = new RenderRequest(camera, new ViewOptions { ShowGrid = false }, new PreviewSettings(), Width, Height, 1, 1, dir.File("big.png"), null)
        {
            Mode = RenderMode.PathTraced,
            Final = new FinalRenderSettings { Width = Width, Height = Height, SamplesPerPixel = 4096, MaxBounces = 6, MaxVolumeBounces = 2, Denoise = false },
            FinalOptions = new FinalRenderOptions { AutoExposure = false },
        };
        Assert.True(job.Start(request, engine), job.Message);
        var steps = new List<double>();
        var watch = Stopwatch.StartNew();
        long sampling = 0;
        while (job.State == RenderJobState.Running)
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(120), "the render made no progress");
            long start = Stopwatch.GetTimestamp();
            job.Step(engine, renderer, 0);
            double ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            if (job.SamplesDone == 0 && sampling == 0)
                continue; // starting the path tracer is not a sampling frame
            if (sampling == 0)
            {
                sampling = Stopwatch.GetTimestamp();
                continue; // the frame that also measured the first dispatches
            }

            steps.Add(ms);
            if (Stopwatch.GetElapsedTime(sampling) > TimeSpan.FromSeconds(3) && steps.Count >= 10)
                break;
        }

        job.Cancel();
        Assert.True(steps.Count >= 10, $"{steps.Count} sampling frames");
        double median = steps.Order().ElementAt(steps.Count / 2);
        // The frame budget (60 ms) plus about one dispatch (20 ms), not a quarter of a second per dispatch.
        double limit = (RenderJobRunner.Budget + 4 * RenderJobRunner.DispatchTarget).TotalMilliseconds;
        Assert.True(median < limit, $"the median sampling frame took {median:0} ms (limit {limit:0} ms): {string.Join(" ", steps.Select(s => s.ToString("0")))}");
    }

    /// <summary>A 10 × 10 m floor, a back wall and one moving head at full open white in haze, bound to <paramref name="renderer"/>.</summary>
    private static StageEngine LitRoom(TempDir dir, VulkanRenderer renderer)
    {
        var engine = StageTestSupport.CreateEngine(dir);
        StageTestSupport.AddWall(engine, "Floor", 10, 10, WallKind.Floor);
        var wall = StageTestSupport.AddWall(engine, "Back wall", 10, 5);
        wall.Local = new Transform(new Vector3(0f, 4f, 0f));
        var head = StageTestSupport.AddFixture(engine, "Spot", 1, new Vector3(0f, 0f, 4.5f));
        var fixture = head.Get<FixtureComponent>()!;
        fixture.ManualAttributes["Dimmer"] = 1f;
        fixture.ManualAttributes["Shutter1"] = 1f;
        fixture.NotifyChanged();
        var sync = engine.AttachRenderWorld(renderer.World, loadFixtureModelsInBackground: false);
        sync.HazeOverride = 0.05f;
        for (int i = 0; i < 3; i++)
            engine.Tick(1.0 / 30);
        return engine;
    }

    // ------------------------------------------------------------------------------------------------ fakes

    /// <summary>
    /// A renderer with a stand-in path tracer (one sample per <see cref="IFinalRender.Advance(TimeSpan)"/>, the exposure
    /// rule of <see cref="VulkanFinalRender"/>) and a real-time renderer that only counts frames; both note the recording
    /// position of the engine's player when they start a frame.
    /// </summary>
    private sealed class FakePathTracer : IRenderer
    {
        private readonly MemoryRenderWorld _world = new();

        /// <summary>The engine whose player position is noted (optional).</summary>
        public StageEngine? Engine { get; init; }

        /// <summary>EV100 the stand-in auto exposure meters (before compensation).</summary>
        public float MeteredEv100 { get; init; } = 9f;

        /// <summary>Every path-traced frame started: the render, the player position and whether it meters itself.</summary>
        public List<(FakeFinalRender Render, double Position, bool AutoExposure)> Started { get; } = new();

        /// <summary>Player positions of the real-time frames rendered.</summary>
        public List<double> RenderedFrames { get; } = new();

        public RendererInfo Info { get; } = new("fake", "none", "1.3", true, true, true, 0);

        public IRenderWorld World => _world;

        public IRenderTarget CreateTarget(int width, int height) => new Target(width, height);

        public void Render(IRenderTarget target, RenderCamera camera, ViewOptions view, PreviewSettings settings, float deltaTime) =>
            RenderedFrames.Add(Engine?.Player?.Position ?? double.NaN);

        public ImageData ReadPixels(IRenderTarget target) =>
            new() { Width = target.Width, Height = target.Height, Pixels = new byte[target.Width * target.Height * 4] };

        public PickResult? Pick(IRenderTarget target, int x, int y) => null;

        public IFinalRender StartFinalRender(RenderCamera camera, FinalRenderSettings settings) =>
            StartFinalRender(camera, settings, new FinalRenderOptions());

        public IFinalRender StartFinalRender(RenderCamera camera, FinalRenderSettings settings, FinalRenderOptions options)
        {
            var render = new FakeFinalRender(settings, options.AutoExposure
                ? Units.ExposureFromEv100(MeteredEv100 - options.ExposureCompensation)
                : Units.ExposureFromEv100(camera.Ev100 - options.ExposureCompensation));
            Started.Add((render, Engine?.Player?.Position ?? double.NaN, options.AutoExposure));
            return render;
        }

        public void Dispose()
        {
        }

        private sealed class Target(int width, int height) : IRenderTarget
        {
            public int Width { get; private set; } = width;

            public int Height { get; private set; } = height;

            public void Resize(int width, int height)
            {
                Width = width;
                Height = height;
            }

            public void Dispose()
            {
            }
        }
    }

    /// <summary>A stand-in path-traced frame: one sample per advance, grey pictures, a fixed exposure.</summary>
    private sealed class FakeFinalRender(FinalRenderSettings settings, float exposure) : IFinalRender
    {
        private readonly int _width = settings.Width;
        private readonly int _height = settings.Height;

        public int SamplesDone { get; private set; }

        public int SamplesTotal { get; } = settings.SamplesPerPixel;

        public bool IsComplete { get; private set; }

        public float Exposure { get; } = exposure;

        public void Advance(TimeSpan budget)
        {
            if (IsComplete)
                return;
            SamplesDone++;
            IsComplete = SamplesDone >= SamplesTotal;
        }

        public void Cancel() => IsComplete = true;

        public ImageData Preview()
        {
            var pixels = new byte[_width * _height * 4];
            Array.Fill(pixels, (byte)128);
            return new ImageData { Width = _width, Height = _height, Pixels = pixels };
        }

        public ImageData ResultHdr()
        {
            var pixels = new byte[_width * _height * 16];
            var floats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(pixels.AsSpan());
            floats.Fill(0.5f);
            return new ImageData { Width = _width, Height = _height, Format = PixelFormat.RgbaFloat, Pixels = pixels };
        }

        public void Dispose()
        {
        }
    }
}
