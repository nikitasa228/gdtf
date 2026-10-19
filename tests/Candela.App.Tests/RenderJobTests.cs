using System.Numerics;
using Candela.App.Panels;
using Candela.App.Session;
using Candela.App.Ui;
using Candela.Core.SceneGraph.Components;
using Candela.Dmx.Input;
using Candela.Engine;
using Candela.Rendering;
using Candela.Rendering.Vulkan;
using Hexa.NET.ImGui;
using StbImageSharp;

namespace Candela.App.Tests;

/// <summary>
/// The Render panel's job on a real (headless) Vulkan renderer — one supersampled, accumulated frame, and an image
/// sequence over a timecode range of a DMX recording played through the engine — plus the request planning, output
/// paths, range parsing and the Preview / Render panels drawn with an engine.
/// </summary>
[Collection(UiStateCollection.Name)]
public sealed class RenderJobTests
{
    private static VulkanRenderer CreateOrSkip()
    {
        try
        {
            return VulkanRenderer.Create(new VulkanContextOptions { CacheDirectory = Path.Combine(AppContext.BaseDirectory, "vk-cache") });
        }
        catch (VulkanUnavailableException e)
        {
            Skip.If(true, $"No Vulkan device available: {e.Message}");
            throw;
        }
    }

    /// <summary>A floor lit by one moving head pointing down from 6 m (manual values: full, open white).</summary>
    private static (StageEngine Engine, RenderCamera Camera) LitStage(TempDir dir, VulkanRenderer renderer)
    {
        var engine = StageTestSupport.CreateEngine(dir);
        StageTestSupport.AddWall(engine, "Floor", 12, 10, WallKind.Floor);
        var head = StageTestSupport.AddFixture(engine, "Spot", 1, new Vector3(0, 0, 6));
        var fixture = head.Get<FixtureComponent>()!;
        fixture.ManualAttributes["Dimmer"] = 1f;
        fixture.ManualAttributes["Shutter1"] = 1f;
        fixture.NotifyChanged();
        engine.AttachRenderWorld(renderer.World, loadFixtureModelsInBackground: false);
        for (int i = 0; i < 3; i++)
            engine.Tick(1.0 / 30);
        var camera = RenderCamera.LookAt(new Vector3(0, -9, 5), new Vector3(0, 0, 0));
        camera.Ev100 = 4f;
        return (engine, camera);
    }

    private static RenderRequest Request(RenderCamera camera, string path, int width, int height, int spp, RenderRange? range = null)
    {
        var (ss, passes) = RenderJobRunner.Plan(width, height, spp);
        var settings = RenderPanel.HighQuality(new PreviewSettings(), new FinalRenderSettings());
        settings.VolumetricSlices = 32;
        settings.ShadowMapResolution = 512;
        return new RenderRequest(camera, new ViewOptions { ShowGrid = false }, settings, width, height, ss, passes, path, range);
    }

    private static void RunToEnd(RenderJobRunner job, StageEngine? engine, IRenderer renderer, int maxSteps = 400)
    {
        for (int i = 0; i < maxSteps && job.State == RenderJobState.Running; i++)
            job.Step(engine, renderer, i);
    }

    private static ImageResult LoadPng(string path)
    {
        Assert.True(File.Exists(path), $"{path} was not written");
        using var stream = File.OpenRead(path);
        return ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
    }

    private static double MeanLuma(ImageResult image)
    {
        double sum = 0;
        for (int i = 0; i < image.Data.Length; i += 4)
            sum += 0.2126 * image.Data[i] + 0.7152 * image.Data[i + 1] + 0.0722 * image.Data[i + 2];
        return sum / (image.Width * image.Height);
    }

    [SkippableFact]
    public void One_frame_is_rendered_supersampled_and_accumulated_and_written_as_png()
    {
        Loc.SetLanguage("en");
        using var dir = new TempDir();
        using var renderer = CreateOrSkip();
        var (engine, camera) = LitStage(dir, renderer);
        using var _ = engine;
        using var job = new RenderJobRunner();
        string path = dir.File(Path.Combine("out", "frame.png"));
        var request = Request(camera, path, 96, 54, 8);
        Assert.Equal(2, request.Supersample);
        Assert.Equal(2, request.Passes);
        Assert.True(job.Start(request, engine), job.Message);
        Assert.False(job.HoldsEngineClock, "one frame does not need the engine clock");
        RunToEnd(job, engine, renderer);
        Assert.Equal(RenderJobState.Completed, job.State);
        Assert.Equal(path, job.LastOutput);
        Assert.False(job.MessageIsError);
        Assert.Contains("frame.png", job.Message, StringComparison.Ordinal);
        var image = LoadPng(path);
        Assert.Equal((96, 54), (image.Width, image.Height));
        Assert.True(MeanLuma(image) > 3, $"the lit floor shows (mean {MeanLuma(image):0.0})");
        Assert.Equal(1.0, job.Progress, 3);
    }

    [SkippableFact]
    public void A_timecode_range_renders_an_image_sequence_played_from_the_recording()
    {
        Loc.SetLanguage("en");
        using var dir = new TempDir();
        using var renderer = CreateOrSkip();
        var (engine, camera) = LitStage(dir, renderer);
        using var _ = engine;

        // Record two seconds of DMX: the dimmer ramps up while timecode runs.
        var source = new DmxSourceId("test", "console");
        var slots = new byte[512];
        engine.Recorder.Start(engine.Time, "ramp");
        for (int i = 0; i < 50; i++)
        {
            slots[0] = (byte)(i * 5);
            engine.Input.Submit(source, 1, slots);
            engine.Input.SubmitTimecode(new Timecode(0, 0, 10 + i / 25, i % 25, TimecodeType.Ebu25));
            engine.Tick(1.0 / 25);
        }

        var recording = engine.Recorder.Stop()!;
        engine.Input.Terminate(source, 1);
        Assert.True(RenderPanel.TryRangeTime("00:00:10:20", TimecodeType.Ebu25, recording, out double start));
        Assert.True(RenderPanel.TryRangeTime("00:00:10:22", TimecodeType.Ebu25, recording, out double end));
        Assert.True(end > start);
        var range = new RenderRange(recording, start, end, 25);
        Assert.Equal(3, range.FrameCount);

        using var job = new RenderJobRunner();
        string path = dir.File("seq.png");
        Assert.True(job.Start(Request(camera, path, 64, 36, 1, range), engine), job.Message);
        Assert.True(job.HoldsEngineClock);
        Assert.NotNull(engine.Player);
        RunToEnd(job, engine, renderer);
        Assert.Equal(RenderJobState.Completed, job.State);
        Assert.False(job.HoldsEngineClock);
        Assert.Null(engine.Player);
        for (int i = 1; i <= 3; i++)
            Assert.Equal((64, 36), (LoadPng(dir.File($"seq_000{i}.png")).Width, LoadPng(dir.File($"seq_000{i}.png")).Height));
        Assert.Contains("3", job.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Requests_that_cannot_run_fail_with_a_reason_and_cancel_stops()
    {
        Loc.SetLanguage("en");
        using var job = new RenderJobRunner();
        var camera = new RenderCamera();
        Assert.False(job.Start(new RenderRequest(camera, new ViewOptions(), new PreviewSettings(), 8, 8, 1, 1, "x.png", null), null));
        Assert.Equal(RenderJobState.Failed, job.State);
        Assert.True(job.MessageIsError);
        Assert.False(job.Start(new RenderRequest(camera, new ViewOptions(), new PreviewSettings(), 5000, 5000, 2, 1, "x.png", null), null));
        Assert.False(job.Start(new RenderRequest(camera, new ViewOptions(), new PreviewSettings(), 64, 64, 1, 1, " ", null), null));
        var recording = new DmxRecording("r", Array.Empty<DmxRecordedFrame>());
        Assert.False(job.Start(new RenderRequest(camera, new ViewOptions(), new PreviewSettings(), 64, 64, 1, 1, "x.png",
            new RenderRange(recording, 0, 1, 25)), null), "a range needs the engine");

        using var dir = new TempDir();
        Assert.True(job.Start(new RenderRequest(camera, new ViewOptions(), new PreviewSettings(), 64, 64, 1, 4, dir.File("a.png"), null), null));
        Assert.Equal(RenderJobState.Running, job.State);
        job.Cancel();
        Assert.Equal(RenderJobState.Cancelled, job.State);
        Assert.False(job.MessageIsError);
    }

    [Theory]
    [InlineData(1920, 1080, 256, 2, 64)]
    [InlineData(1920, 1080, 2, 1, 2)]
    [InlineData(1920, 1080, 1, 1, 1)]
    [InlineData(5000, 3000, 256, 1, 256)]
    [InlineData(640, 360, 100000, 2, 256)]
    public void Samples_become_supersampling_and_accumulated_frames(int w, int h, int spp, int ss, int passes) =>
        Assert.Equal((ss, passes), RenderJobRunner.Plan(w, h, spp));

    [Fact]
    public void Sequence_files_are_numbered_and_relative_outputs_go_to_the_pictures_folder()
    {
        string sep = Path.DirectorySeparatorChar.ToString();
        Assert.Equal("a" + sep + "shot_0001.png", RenderJobRunner.FramePath("a" + sep + "shot.png", 0, 10));
        Assert.Equal("a" + sep + "shot_00042.png", RenderJobRunner.FramePath("a" + sep + "shot.png", 41, 12000));
        Assert.Equal("shot.png", RenderJobRunner.FramePath("shot.png", 0, 1));
        string resolved = RenderPanel.ResolveOutput("render");
        Assert.True(Path.IsPathRooted(resolved));
        Assert.EndsWith(".png", resolved, StringComparison.Ordinal);
        Assert.StartsWith(RenderPanel.DefaultFolder(), resolved, StringComparison.Ordinal);
        string absolute = Path.Combine(Path.GetTempPath(), "x.png");
        Assert.Equal(absolute, RenderPanel.ResolveOutput(absolute));
        Assert.EndsWith("render.png", RenderPanel.ResolveOutput(null), StringComparison.Ordinal);
    }

    [Fact]
    public void Range_bounds_accept_seconds_and_timecodes_of_the_recording()
    {
        var frames = new List<DmxRecordedFrame>();
        for (int i = 0; i < 50; i++)
            frames.Add(new DmxRecordedFrame(i / 25.0, new Timecode(1, 0, i / 25, i % 25, TimecodeType.Ebu25), Array.Empty<DmxUniverseChange>()));
        var recording = new DmxRecording("tc", frames);
        Assert.True(RenderPanel.TryRangeTime("1.5", TimecodeType.Ebu25, recording, out double s));
        Assert.Equal(1.5, s, 6);
        Assert.True(RenderPanel.TryRangeTime("01:00:01:00", TimecodeType.Ebu25, recording, out s));
        Assert.Equal(1.0, s, 2);
        Assert.False(RenderPanel.TryRangeTime("02:00:00:00", TimecodeType.Ebu25, recording, out _));
        Assert.False(RenderPanel.TryRangeTime("garbage", TimecodeType.Ebu25, recording, out _));
        Assert.True(RenderPanel.TryRangeTime("99", TimecodeType.Ebu25, recording, out s));
        Assert.Equal(recording.Duration, s, 6);
    }

    [Fact]
    public void Preview_and_render_panels_edit_their_presets_and_draw_in_both_languages()
    {
        using var dir = new TempDir();
        using var engine = StageTestSupport.CreateEngine(dir);
        using var tools = StageTestSupport.CreateTools();
        var stage = StagePanelContext.ForEngine(engine, tools);
        var preview = new PreviewPanel(stage, () => null);
        var reports = new List<SessionResult>();
        var render = new RenderPanel(stage, reports.Add);
        using var ui = new HeadlessImGui(new Vector2(800, 1200));
        foreach (string language in new[] { "en", "ru" })
        {
            Loc.SetLanguage(language);
            render.RangeMode = language == "ru";
            for (int i = 0; i < 3; i++)
            {
                ui.Frame(() =>
                {
                    ImGui.SetNextWindowSize(new Vector2(420, 1100));
                    ImGui.Begin("preview-host");
                    preview.Draw();
                    ImGui.End();
                    ImGui.SetNextWindowSize(new Vector2(420, 1100));
                    ImGui.Begin("render-host");
                    render.Draw();
                    ImGui.End();
                });
            }
        }

        Loc.SetLanguage("en");
        // A range without a recording is refused with the reason in the panel.
        render.RangeMode = true;
        Assert.False(render.StartRender(tools.FinalRender.Live));
        Assert.Empty(reports);
        // The request uses the high-quality version of the preview look and the planned supersampling.
        var request = render.BuildRequest(tools.FinalRender.Live, null);
        Assert.Equal(tools.FinalRender.Live.Width, request.Width);
        Assert.Equal(256, request.Settings.VolumetricSlices);
        Assert.Equal(1f, request.Settings.ResolutionScale);
        Assert.Equal(ViewShading.Full, request.View.Shading);
        Assert.Equal(OverlayMode.None, request.View.Overlay);
        Assert.True(Path.IsPathRooted(request.OutputPath));

        // Haze is an undoable scene setting; consecutive slider steps merge.
        var scene = engine.Scene;
        engine.Undo.Execute(PreviewPanel.SceneSetting(scene, "haze", s => s.BaseHazeScattering, (s, v) => s.BaseHazeScattering = v, 0.05f));
        engine.Undo.Execute(PreviewPanel.SceneSetting(scene, "haze", s => s.BaseHazeScattering, (s, v) => s.BaseHazeScattering = v, 0.08f));
        Assert.Equal(0.08f, scene.Settings.BaseHazeScattering);
        engine.Undo.Undo();
        Assert.Equal(0.02f, scene.Settings.BaseHazeScattering);
        Assert.False(engine.Undo.CanUndo);

        // Viewport-wide settings are applied once per change.
        int version = tools.ViewportApplyVersion;
        tools.ApplyEv100ToViewports(9.5f);
        tools.ApplyGridToViewports(false);
        Assert.Equal(version + 2, tools.ViewportApplyVersion);
        Assert.Equal(9.5f, tools.ViewportEv100);
        Assert.False(tools.ViewportGrid);
        tools.ApplyEv100ToViewports(float.NaN);
        Assert.Equal(9.5f, tools.ViewportEv100);
    }
}
