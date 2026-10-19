using Candela.Assets;
using Candela.Cli;
using Candela.Core.Geometry;
using Candela.Dmx.Input;
using Candela.Rendering.Vulkan;

namespace Candela.Engine.Tests;

/// <summary>
/// <c>candela render --final</c> (the path tracer) and image sequences from a DMX recording: option parsing and frame
/// planning without a GPU; small renders of Фокина.mvr on the Vulkan device (skipped without one).
/// </summary>
[Collection(VulkanCollection.Name)]
public sealed class FinalRenderCliTests
{
    private const string Scene = "show.mvr";

    /// <summary>A recording of universes 1 … 7 at full from 0 s, all zero from 1 s, 2 s long, with EBU timecode from 01:00:00:00.</summary>
    private static DmxRecording FullThenBlackout()
    {
        var frames = new List<DmxRecordedFrame>();
        for (int n = 0; n <= 50; n++)
        {
            double t = n * 0.04;
            var changes = new List<DmxUniverseChange>();
            if (n == 0 || n == 25)
            {
                var values = Enumerable.Repeat(n == 0 ? (byte)255 : (byte)0, 512).ToArray();
                for (int u = 1; u <= 7; u++)
                    changes.Add(new DmxUniverseChange(u, n == 0 ? DmxUniverseChangeKind.Full : DmxUniverseChangeKind.Delta, new[] { new DmxSlotRun(0, values) }));
            }
            frames.Add(new DmxRecordedFrame(t, new Timecode(1, 0, n / 25, n % 25, TimecodeType.Ebu25), changes));
        }
        return new DmxRecording("Full then blackout", frames);
    }

    private static (int Code, string Output, string Error) Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        int code = Program.Run(args, output, error);
        return (code, output.ToString(), error.ToString());
    }

    private static void SkipWithoutVulkan()
    {
        try
        {
            using var renderer = VulkanRenderer.Create(new VulkanContextOptions { MinimumLogLevel = VulkanLogLevel.Error });
        }
        catch (VulkanUnavailableException e)
        {
            Skip.If(true, $"No Vulkan device available: {e.Message}");
        }
    }

    private static string[] Common(string dir, params string[] more) =>
        [TestEnvironment.Fokina, "--cache", Path.Combine(dir, "cache"), "--library", TestEnvironment.Root, .. more];

    private static double MeanLuminance(ExrImage image)
    {
        float[] r = image.Channel("R")!.Values, g = image.Channel("G")!.Values, b = image.Channel("B")!.Values;
        double sum = 0;
        for (int i = 0; i < r.Length; i++)
            sum += 0.2627 * r[i] + 0.6780 * g[i] + 0.0593 * b[i]; // Rec.2020 luminance
        return sum / r.Length;
    }

    [Fact]
    public void Final_render_options_parse_and_belong_together()
    {
        var o = RenderOptions.Parse([Scene,
            "--out",
            "x.png",
            "--final",
            "--samples",
            "16",
            "--exr",
            "x.exr",
            "--denoise",
            "--bounces",
            "3",
            "--volume-bounces",
            "1",
            "--seed",
            "7",
            "--clamp",
            "500"]);
        Assert.True(o.Final);
        Assert.Equal(16, o.Samples);
        Assert.True(o.SamplesGiven);
        Assert.Equal("x.exr", o.Exr);
        Assert.True(o.Denoise);
        Assert.Equal(3, o.Bounces);
        Assert.Equal(1, o.VolumeBounces);
        Assert.Equal(7u, o.Seed);
        Assert.Equal(500f, o.Clamp);
        Assert.Null(o.Seconds);

        // defaults; --seconds alone renders until the time is up, with --samples whichever comes first
        var d = RenderOptions.Parse([Scene, "--out", "x.png", "--final"]);
        Assert.Equal(RenderOptions.DefaultSamples, d.Samples);
        Assert.Equal(6, d.Bounces);
        Assert.Equal(2, d.VolumeBounces);
        Assert.False(d.Denoise);
        Assert.True(d.AutoExposure);
        var timed = RenderOptions.Parse([Scene, "--out", "x.png", "--final", "--seconds", "30"]);
        Assert.Equal(30.0, timed.Seconds!.Value, 6);
        Assert.Equal(VulkanFinalRender.MaxSamples, timed.Samples);
        Assert.Equal(8, RenderOptions.Parse([Scene, "--out", "x.png", "--final", "--seconds", "30", "--samples", "8"]).Samples);

        // final-only options need --final; real-time-only options do not combine with it
        Assert.Throws<UsageException>(() => RenderOptions.Parse([Scene, "--out", "x.png", "--samples", "16"]));
        Assert.Throws<UsageException>(() => RenderOptions.Parse([Scene, "--out", "x.png", "--exr", "x.exr"]));
        Assert.Throws<UsageException>(() => RenderOptions.Parse([Scene, "--out", "x.png", "--denoise"]));
        Assert.Throws<UsageException>(() => RenderOptions.Parse([Scene, "--out", "x.png", "--final", "--mode", "colorless"]));
        Assert.Throws<UsageException>(() => RenderOptions.Parse([Scene, "--out", "x.png", "--final", "--overlay", "lux"]));
        Assert.Throws<UsageException>(() => RenderOptions.Parse([Scene, "--out", "x.png", "--final", "--grid"]));
        Assert.Throws<UsageException>(() => RenderOptions.Parse([Scene, "--out", "x.png", "--final", "--samples", "0"]));
        Assert.Throws<UsageException>(() => RenderOptions.Parse([Scene, "--out", "x.png", "--final", "--bounces", "65"]));
        Assert.Throws<UsageException>(() => RenderOptions.Parse([Scene, "--out", "x.png", "--final", "--clamp", "-1"]));
        string folder = TestEnvironment.NewTempDirectory("cli-exr-folder");
        Assert.Throws<UsageException>(() => RenderOptions.Parse([Scene, "--out", "x.png", "--final", "--exr", folder]));
        Assert.Equal(Program.ExitUsage, Run("render", Scene, "--out", "x.png", "--final", "--grid").Code);
    }

    [Fact]
    public void Times_parse_as_seconds_or_timecodes_and_ranges_are_checked()
    {
        var seconds = RenderOptions.Parse([Scene, "--out", "x.png", "--time", "2.5", "--end", "4", "--fps", "30"]);
        Assert.Equal(2.5, seconds.Time, 6);
        Assert.Equal(TimeSpec.FromSeconds(4), seconds.End);
        Assert.Equal(30.0, seconds.FrameRate, 6);

        var tc = RenderOptions.Parse([Scene, "--out", "x.png", "--look", "dmx-file", "take.cdmx", "--time", "01:00:00:10", "--end", "01:00:01;15"]);
        Assert.True(tc.Start.IsTimecode);
        Assert.Equal(new TimeSpec(null, 1, 0, 0, 10), tc.Start);
        Assert.True(tc.End!.Value.DropFrame);
        Assert.Equal("01:00:01;15", tc.End.ToString());

        // timecodes need a recording; malformed times; an end before the start
        Assert.Throws<UsageException>(() => RenderOptions.Parse([Scene, "--out", "x.png", "--time", "01:00:00:10"]));
        Assert.Throws<UsageException>(() => RenderOptions.Parse([Scene, "--out", "x.png", "--time", "1:2:3"]));
        Assert.Throws<UsageException>(() => RenderOptions.Parse([Scene, "--out", "x.png", "--time", "01:00:61:00", "--look", "dmx-file", "t.cdmx"]));
        Assert.Throws<UsageException>(() => RenderOptions.Parse([Scene, "--out", "x.png", "--time", "-1"]));
        Assert.Throws<UsageException>(() => RenderOptions.Parse([Scene, "--out", "x.png", "--time", "5", "--end", "2"]));
        Assert.Throws<UsageException>(() => RenderOptions.Parse([Scene, "--out", "x.png", "--fps", "0"]));
    }

    [Fact]
    public void Frames_are_planned_from_the_recording_timecode()
    {
        var recording = FullThenBlackout();
        var error = new StringWriter();
        var options = RenderOptions.Parse([Scene,
            "--out",
            "out/show.png",
            "--look",
            "dmx-file",
            "t.cdmx",
            "--time",
            "01:00:00:10",
            "--end",
            "01:00:01:15",
            "--fps",
            "2"]);
        var plan = RenderCommand.PlanFrames(options, recording, error)!;
        Assert.NotNull(plan);
        Assert.Equal(0.4, plan.Start, 6);
        Assert.Equal(3, plan.Count); // 0.4, 0.9, 1.4 s
        Assert.Equal(1.4, plan.TimeOf(2), 6);
        Assert.Equal(Path.Combine("out", "show_0001.png"), plan.PathOf("out/show.png", 0));
        Assert.Equal(Path.Combine("out", "show_0003.exr"), plan.PathOf("out/show.exr", 2));
        Assert.Equal("out/show.png", new FramePlan(1, 1, 25).PathOf("out/show.png", 0)); // one image keeps its name
        Assert.Equal("x_00001.png", new FramePlan(0, 12000, 25).PathOf("x.png", 0));

        // seconds pass through; one frame without --end; the end itself is rendered when it falls on a frame
        var single = RenderCommand.PlanFrames(RenderOptions.Parse([Scene, "--out", "x.png", "--time", "1.5"]), null, error)!;
        Assert.Equal((1.5, 1), (single.Start, single.Count));
        Assert.Equal(26, RenderCommand.PlanFrames(RenderOptions.Parse([Scene, "--out", "x.png", "--end", "1"]), null, error)!.Count);

        // a timecode before the recording starts or after it ends cannot be rendered
        var late = RenderOptions.Parse([Scene, "--out", "x.png", "--look", "dmx-file", "t.cdmx", "--time", "02:00:00:00"]);
        Assert.Null(RenderCommand.PlanFrames(late, recording, error));
        Assert.Contains("does not reach timecode 02:00:00:00", error.ToString(), StringComparison.Ordinal);
        var early = RenderOptions.Parse([Scene, "--out", "x.png", "--look", "dmx-file", "t.cdmx", "--time", "00:59:59:00"]);
        Assert.Equal(0.0, RenderCommand.PlanFrames(early, recording, error)!.Start, 6); // the first frame at or after it
        var backwards = RenderOptions.Parse([Scene, "--out", "x.png", "--look", "dmx-file", "t.cdmx", "--time", "01:00:01:00", "--end", "01:00:00:05"]);
        Assert.Throws<UsageException>(() => RenderCommand.PlanFrames(backwards, recording, error));
        Assert.Throws<UsageException>(() =>
            RenderCommand.PlanFrames(RenderOptions.Parse([Scene, "--out", "x.png", "--end", "36000", "--fps", "240"]), null, error));
    }

    [SkippableFact]
    public void Final_render_of_fokina_writes_the_png_and_a_layered_exr()
    {
        SkipWithoutVulkan();
        string dir = TestEnvironment.NewTempDirectory("cli-final");
        string png = Path.Combine(dir, "fokina-final.png"), exr = Path.Combine(dir, "fokina-final.exr");
        var (code, output, error) = Run(["render",
            .. Common(dir, "--out", png, "--width", "160", "--height", "90", "--look", "demo", "--final",
            "--samples", "2", "--denoise", "--exr", exr, "--bounces", "3", "--seed", "3", "--clamp", "100000")]);
        Assert.True(code == Program.ExitOk, $"exit {code}\n{output}\n{error}");
        Assert.Contains("path traced, 2 samples per pixel, 3 bounces, 2 volume bounces, clamp 100000 cd/m², denoised, seed 3", output, StringComparison.Ordinal);
        Assert.Contains("auto exposure (metered from the image", output, StringComparison.Ordinal);
        Assert.Contains("Frame:    1/1 at 0 s: 2 samples per pixel", output, StringComparison.Ordinal);

        Assert.True(ImageImporter.TryDecode(File.ReadAllBytes(png), out var image, out var problem), problem);
        Assert.Equal((160, 90), (image!.Width, image.Height));
        var exrImage = OpenExr.Read(exr);
        Assert.Equal((160, 90), (exrImage.Width, exrImage.Height));
        Assert.Equal(new[] { "A", "B", "G", "N.X", "N.Y", "N.Z", "R", "Z", "albedo.B", "albedo.G", "albedo.R", "raw.B", "raw.G", "raw.R" },
            exrImage.Channels.Select(c => c.Name));
        Assert.Equal(ExrPixelType.Float, exrImage.Channel("R")!.Type);
        Assert.Equal(ExrColorimetry.Rec2020.Red, exrImage.Colorimetry!.Red);
        Assert.True(exrImage.Colorimetry.WhiteLuminance is > 0f and < 1e6f, $"white luminance {exrImage.Colorimetry.WhiteLuminance}");
        foreach (var channel in exrImage.Channels)
            Assert.All(channel.Values, v => Assert.True(float.IsFinite(v), $"{channel.Name} has {v}"));
        double mean = MeanLuminance(exrImage);
        Assert.True(mean > 1e-3, $"the final render is black: mean {mean}");
        // the stage is in view: some pixels see surfaces (depth below the 10⁵ m of the empty background)
        Assert.Contains(exrImage.Channel("Z")!.Values, z => z is > 1f and < 100f);

        string docs = Path.Combine(AppContext.BaseDirectory, "render-output");
        Directory.CreateDirectory(docs);
        File.Copy(png, Path.Combine(docs, "fokina-cli-final.png"), overwrite: true);
    }

    [SkippableFact]
    public void A_timecode_range_of_a_dmx_recording_renders_frame_by_frame_in_real_time_and_with_the_path_tracer()
    {
        SkipWithoutVulkan();
        string dir = TestEnvironment.NewTempDirectory("cli-sequence");
        string take = Path.Combine(dir, "take" + DmxRecording.Extension);
        FullThenBlackout().Save(take);
        string[] range = ["--look", "dmx-file", take, "--time", "01:00:00:10", "--end", "01:00:01:15", "--fps", "2", "--width", "96", "--height", "54"];

        // the real-time view: one PNG per frame
        string realTime = Path.Combine(dir, "rt", "show.png");
        var rt = Run(["render", .. Common(dir, [.. range, "--out", realTime, "--frames", "2", "--ev", "8"])]);
        Assert.True(rt.Code == Program.ExitOk, $"exit {rt.Code}\n{rt.Output}\n{rt.Error}");
        Assert.Contains("Frames:   3 from 0.4 s to 1.4 s at 2 fps", rt.Output, StringComparison.Ordinal);
        Assert.Contains("simulated 0.4 s", rt.Output, StringComparison.Ordinal);
        for (int i = 1; i <= 3; i++)
            Assert.True(File.Exists(Path.Combine(dir, "rt", $"show_000{i}.png")), $"real-time frame {i}");
        Assert.False(File.Exists(realTime));

        // the path tracer: PNG and EXR per frame, the exposure metered on the first frame and kept
        string final = Path.Combine(dir, "pt", "show.png"), exr = Path.Combine(dir, "pt", "show.exr");
        var pt = Run(["render", .. Common(dir, [.. range, "--out", final, "--final", "--samples", "1", "--exr", exr])]);
        Assert.True(pt.Code == Program.ExitOk, $"exit {pt.Code}\n{pt.Output}\n{pt.Error}");
        Assert.Contains("Frame:    3/3 at 1.4 s", pt.Output, StringComparison.Ordinal);
        Assert.Contains("Wrote 3 of 3 frames", pt.Output, StringComparison.Ordinal);
        var frames = Enumerable.Range(1, 3).Select(i => OpenExr.Read(Path.Combine(dir, "pt", $"show_000{i}.exr"))).ToArray();
        Assert.All(Enumerable.Range(1, 3), i => Assert.True(File.Exists(Path.Combine(dir, "pt", $"show_000{i}.png"))));
        Assert.Equal(frames[0].Colorimetry!.WhiteLuminance, frames[2].Colorimetry!.WhiteLuminance);
        double lit = MeanLuminance(frames[0]), dark = MeanLuminance(frames[2]);
        Assert.True(lit > 1e-3, $"frame 1 is black: {lit}");
        // 0.4 s after the blackout the rig is dark: only the background of the venue is left
        Assert.True(dark < 0.25 * lit, $"frame 3 ({dark}) is not darker than frame 1 ({lit})");
    }

    [SkippableFact]
    public void A_final_render_cancelled_before_the_first_sample_writes_nothing()
    {
        SkipWithoutVulkan();
        string dir = TestEnvironment.NewTempDirectory("cli-cancel");
        string png = Path.Combine(dir, "never.png");
        var options = RenderOptions.Parse(Common(dir, "--out", png, "--width", "32", "--height", "18", "--final", "--exr", Path.Combine(dir, "never.exr")));
        var output = new StringWriter();
        var error = new StringWriter();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        int code = RenderCommand.Run(options, output, error, cancelled.Token);
        Assert.True(code == Program.ExitCancelled, $"exit {code}\n{output}\n{error}");
        Assert.Contains("nothing was written", output.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(png));
        Assert.False(File.Exists(Path.Combine(dir, "never.exr")));
    }
}
