using Candela.Assets;
using Candela.Core.Geometry;
using Candela.Rendering.Vulkan;

namespace Candela.Engine.Tests;

/// <summary>
/// End-to-end rendering on the Vulkan renderer (lavapipe in the cloud): the CLI render of Фокина.mvr and fixture
/// thumbnails. Skipped when no Vulkan device exists.
/// </summary>
[Collection(VulkanCollection.Name)]
public sealed class RenderingTests
{
    private static VulkanRenderer CreateRendererOrSkip()
    {
        try
        {
            return VulkanRenderer.Create(new VulkanContextOptions
            {
                CacheDirectory = Path.Combine(AppContext.BaseDirectory, "vk-cache"),
                MinimumLogLevel = VulkanLogLevel.Error,
            });
        }
        catch (VulkanUnavailableException e)
        {
            Skip.If(true, $"No Vulkan device available: {e.Message}");
            throw;
        }
    }

    /// <summary>Folder where the tests leave their images for inspection (CANDELA_TEST_OUTPUT or the test output folder).</summary>
    private static string OutputDirectory
    {
        get
        {
            string dir = Environment.GetEnvironmentVariable("CANDELA_TEST_OUTPUT") is { Length: > 0 } d ? d : Path.Combine(AppContext.BaseDirectory, "render-output");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private static (double Mean, double BrightShare) Luminance(ImageData image)
    {
        double sum = 0;
        int bright = 0, n = image.Width * image.Height;
        for (int i = 0; i < n; i++)
        {
            double y = (0.2126 * image.Pixels[4 * i] + 0.7152 * image.Pixels[4 * i + 1] + 0.0722 * image.Pixels[4 * i + 2]) / 255.0;
            sum += y;
            if (y > 0.35) bright++;
        }
        return (sum / n, bright / (double)n);
    }

    [SkippableFact]
    public void Cli_render_of_fokina_with_default_auto_exposure_keeps_pools_visible_and_the_venue_dark()
    {
        using (CreateRendererOrSkip())
        {
            // only probes for a device; the CLI creates its own renderer
        }
        string dir = TestEnvironment.NewTempDirectory("cli-render-auto");
        string png = Path.Combine(dir, "fokina-auto.png");
        var output = new StringWriter();
        var error = new StringWriter();
        int code = Cli.Program.Run(
            ["render",
                TestEnvironment.Fokina,
                "--out",
                png,
                "--width",
                "480",
                "--height",
                "270",
                "--cache",
                Path.Combine(dir, "cache"),
                "--library",
                TestEnvironment.Root],
            output, error);
        Assert.True(code == Cli.Program.ExitOk, $"exit {code}\n{output}\n{error}");
        Assert.Contains("auto exposure", output.ToString(), StringComparison.Ordinal);
        File.Copy(png, Path.Combine(OutputDirectory, "fokina-cli-auto.png"), overwrite: true);

        Assert.True(ImageImporter.TryDecode(File.ReadAllBytes(png), out var image, out var problem), problem);
        var (mean, bright) = Luminance(image!);
        // stage metering places the lit band below white: the pools stay visible, the dark venue stays dark
        Assert.True(mean > 0.02, $"the image is (nearly) black: mean luminance {mean:0.000}");
        Assert.True(bright > 0.02, $"no lit pools: {bright:P1} bright pixels");
        Assert.True(bright < 0.5, $"the venue is lit up like the stage: {bright:P1} bright pixels");
    }

    [SkippableFact]
    public void Cli_render_of_fokina_produces_a_lit_stage()
    {
        using (CreateRendererOrSkip())
        {
            // only probes for a device; the CLI creates its own renderer
        }
        string dir = TestEnvironment.NewTempDirectory("cli-render");
        string png = Path.Combine(dir, "fokina.png");
        var output = new StringWriter();
        var error = new StringWriter();
        int code = Cli.Program.Run(
            ["render",
                TestEnvironment.Fokina,
                "--out",
                png,
                "--width",
                "480",
                "--height",
                "270",
                "--cache",
                Path.Combine(dir, "cache"),
                "--library",
                TestEnvironment.Root,
                "--frames",
                "4",
                "--ev",
                "floor"],
            output, error);
        Assert.True(code == Cli.Program.ExitOk, $"exit {code}\n{output}\n{error}");
        string text = output.ToString();
        Assert.Contains("open white on 91 fixtures", text, StringComparison.Ordinal);
        Assert.Contains("Wrote", text, StringComparison.Ordinal);
        Assert.Contains("Issues:", text, StringComparison.Ordinal);
        File.Copy(png, Path.Combine(OutputDirectory, "fokina-cli.png"), overwrite: true);

        Assert.True(ImageImporter.TryDecode(File.ReadAllBytes(png), out var image, out var problem), problem);
        Assert.Equal(480, image!.Width);
        Assert.Equal(270, image.Height);
        var (mean, bright) = Luminance(image);
        Assert.True(mean > 0.05, $"the image is (nearly) black: mean luminance {mean:0.000}");
        Assert.True(bright > 0.05, $"no lit floor area: {bright:P1} bright pixels");
        Assert.True(bright < 0.9, $"the image is blown out: {bright:P1} bright pixels");

        // the auto view frames the content: the lit stage spans a large part of the picture. At open white only the beams'
        // pools light the floor (about half the width); the whole floor used to glow from the Zonda 3 FX "Basic" liquid-effect
        // rings, which no channel of that mode addresses and which now stay dark (Candela.Dmx ColorEvaluationTests)
        int minX = image.Width, maxX = -1, minY = image.Height, maxY = -1;
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                int i = 4 * (y * image.Width + x);
                double l = (0.2126 * image.Pixels[i] + 0.7152 * image.Pixels[i + 1] + 0.0722 * image.Pixels[i + 2]) / 255.0;
                if (l < 0.12) continue;
                minX = Math.Min(minX, x);
                maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y);
                maxY = Math.Max(maxY, y);
            }
        }
        Assert.True(maxX - minX > 0.4 * image.Width, $"the stage spans only x {minX}…{maxX} of {image.Width}");
        Assert.True(maxY - minY > 0.6 * image.Height, $"the stage spans only y {minY}…{maxY} of {image.Height}");
    }

    [SkippableFact]
    public void Cli_render_of_fokina_with_the_demo_look_shows_coloured_beams_in_the_air()
    {
        using (CreateRendererOrSkip())
        {
            // only probes for a device; the CLI creates its own renderer
        }
        string dir = TestEnvironment.NewTempDirectory("cli-demo");
        string png = Path.Combine(dir, "demo.png");
        var output = new StringWriter();
        var error = new StringWriter();
        int code = Cli.Program.Run(
            ["render",
                TestEnvironment.Fokina,
                "--out",
                png,
                "--width",
                "320",
                "--height",
                "180",
                "--look",
                "demo",
                "--cache",
                Path.Combine(dir, "cache"),
                "--library",
                TestEnvironment.Root],
            output, error);
        Assert.True(code == Cli.Program.ExitOk, $"exit {code}\n{output}\n{error}");
        string text = output.ToString();
        Assert.Contains("demo on 91 fixtures", text, StringComparison.Ordinal);
        Assert.Contains("auto exposure", text, StringComparison.Ordinal);
        File.Copy(png, Path.Combine(OutputDirectory, "fokina-demo.png"), overwrite: true);

        Assert.True(ImageImporter.TryDecode(File.ReadAllBytes(png), out var image, out var problem), problem);
        var (mean, bright) = Luminance(image!);
        Assert.True(mean > 0.04, $"the image is (nearly) black: mean luminance {mean:0.000}");
        Assert.True(bright < 0.6, $"the image is blown out: {bright:P1} bright pixels");

        // saturated light of several hues (60° sectors), and beams in the haze above the stage (the top third of the
        // auto view is air)
        var sectors = new int[6];
        int pixels = image!.Width * image.Height, air = 0;
        for (int i = 0; i < pixels; i++)
        {
            double r = image.Pixels[4 * i] / 255.0, g = image.Pixels[4 * i + 1] / 255.0, b = image.Pixels[4 * i + 2] / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            double luminance = 0.2126 * r + 0.7152 * g + 0.0722 * b;
            if (i / image.Width < image.Height / 3 && luminance > 0.15) air++;
            if (max < 0.15 || (max - min) / max < 0.35) continue;
            double hue = max == r ? 60 * (((g - b) / (max - min) + 6) % 6) : max == g ? 60 * ((b - r) / (max - min) + 2) : 60 * ((r - g) / (max - min) + 4);
            sectors[(int)(hue / 60) % 6]++;
        }
        int hues = sectors.Count(n => n > pixels * 0.005);
        Assert.True(hues >= 3, $"only {hues} hues: {string.Join(", ", sectors)}");
        Assert.True(air > pixels / 3 * 0.1, $"no beams in the air: {air} lit pixels in the top third");
    }

    [SkippableFact]
    public void Cli_render_with_the_smoke_overlay_greys_the_scene_and_marks_the_running_hazer()
    {
        using (CreateRendererOrSkip())
        {
            // only probes for a device; the CLI creates its own renderer
        }
        string dir = TestEnvironment.NewTempDirectory("cli-smoke");
        string project = Path.Combine(dir, "hazer.candela");
        using (var engine = TestEnvironment.CreateEngine("cli-smoke-build"))
        {
            SceneBuilder.Floor(engine.Scene, 12f, Core.SceneGraph.Components.MaterialIds.WoodStage);
            var hazer = SceneBuilder.Fixture(engine.Scene, SceneBuilder.EntourHaze, "1 Channel Mode", new System.Numerics.Vector3(0f, 0f, 0f),
                name: "Hazer");
            hazer.Get<Core.SceneGraph.Components.FixtureComponent>()!.ManualAttributes["Haze1"] = 100f;
            Assert.True(engine.SaveAs(project).Success);
        }

        string png = Path.Combine(dir, "smoke.png");
        var output = new StringWriter();
        var error = new StringWriter();
        int code = Cli.Program.Run(
            ["render",
                project,
                "--out",
                png,
                "--width",
                "320",
                "--height",
                "180",
                "--overlay",
                "smoke",
                "--look",
                "show",
                "--time",
                "4",
                "--camera",
                "0,-6,3",
                "--target",
                "0,0,0.8",
                "--cache",
                Path.Combine(dir, "cache"),
                "--library",
                TestEnvironment.Root],
            output, error);
        Assert.True(code == Cli.Program.ExitOk, $"exit {code}\n{output}\n{error}");
        string text = output.ToString();
        Assert.Contains("overlay Smoke", text, StringComparison.Ordinal);
        Assert.Contains("Smoke:    overlay 0.005 … 2 /m", text, StringComparison.Ordinal);
        Assert.Contains("Hazer (haze) 100 %", text, StringComparison.Ordinal);
        File.Copy(png, Path.Combine(OutputDirectory, "cli-smoke-overlay.png"), overwrite: true);

        // the hazer's arrow (cyan) is drawn; everything else is grey or tinted by the gradient's colours
        Assert.True(ImageImporter.TryDecode(File.ReadAllBytes(png), out var image, out var problem), problem);
        int cyan = 0;
        for (int i = 0; i < image!.Width * image.Height; i++)
        {
            int r = image.Pixels[4 * i], g = image.Pixels[4 * i + 1], b = image.Pixels[4 * i + 2];
            if (b > 200 && g > 170 && r < 140) cyan++;
        }
        Assert.True(cyan > 20, $"no hazer marker: {cyan} cyan pixels");
    }

    [SkippableFact]
    public void Thumbnail_of_a_file_deleted_since_the_scan_is_a_dark_picture_not_an_exception()
    {
        using var renderer = CreateRendererOrSkip();
        string folder = TestEnvironment.NewTempDirectory("thumb-deleted");
        File.Copy(Path.Combine(TestEnvironment.Root, SceneBuilder.MacViper), Path.Combine(folder, SceneBuilder.MacViper));
        var library = Gdtf.FixtureLibrary.Scan(new[] { folder });
        var entry = Assert.Single(library.Entries);
        File.Delete(entry.FilePath);
        var store = new ThumbnailStore(TestEnvironment.NewTempDirectory("thumb-deleted-store"));
        using var thumbnails = new FixtureThumbnailRenderer(renderer, library, store);
        var image = thumbnails.Render(entry, 32);
        Assert.Equal(32, image.Width);
        Assert.Empty(Directory.GetFiles(store.Directory, "*.png", SearchOption.AllDirectories));
    }

    [SkippableFact]
    public void Cli_render_plays_a_dmx_recording_before_rendering()
    {
        using (CreateRendererOrSkip())
        {
            // only probes for a device
        }
        string dir = TestEnvironment.NewTempDirectory("cli-dmx");
        var hub = new Dmx.Input.DmxInputHub();
        var recorder = new DmxRecorder(hub);
        recorder.Start(0, "Full");
        var full = Enumerable.Repeat((byte)255, 512).ToArray();
        for (int u = 1; u <= 7; u++) hub.Submit(new Dmx.Input.DmxSourceId("artnet", "desk"), u, full);
        recorder.Capture(0);
        recorder.Capture(1);
        string take = Path.Combine(dir, "full" + DmxRecording.Extension);
        recorder.Stop()!.Save(take);

        string png = Path.Combine(dir, "dmx.png");
        var output = new StringWriter();
        var error = new StringWriter();
        int code = Cli.Program.Run(
            ["render",
                TestEnvironment.Fokina,
                "--out",
                png,
                "--width",
                "160",
                "--height",
                "90",
                "--frames",
                "1",
                "--mode",
                "colorless",
                "--look",
                "dmx-file",
                take,
                "--time",
                "0.5",
                "--cache",
                Path.Combine(dir, "cache"),
                "--library",
                TestEnvironment.Root],
            output, error);
        Assert.True(code == Cli.Program.ExitOk, $"exit {code}\n{output}\n{error}");
        Assert.Contains("DMX recording 'Full'", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("simulated 0.5 s", output.ToString(), StringComparison.Ordinal);
        Assert.True(File.Exists(png));
    }

    [SkippableFact]
    public void Thumbnail_of_a_real_fixture_shows_the_fixture_and_is_cached()
    {
        using var renderer = CreateRendererOrSkip();
        var store = new ThumbnailStore(TestEnvironment.NewTempDirectory("thumbs"));
        var entry = TestEnvironment.Entry(SceneBuilder.MacViper);
        using var thumbnails = new FixtureThumbnailRenderer(renderer, TestEnvironment.Library, store);

        var image = thumbnails.Render(entry, 96);
        ImageImporter.SavePng(image, Path.Combine(OutputDirectory, "thumbnail-mac-viper.png"));
        Assert.Equal(96, image.Width);
        Assert.Equal(96, image.Height);
        var (mean, _) = Luminance(image);
        double corner = (image.Pixels[0] + image.Pixels[1] + image.Pixels[2]) / (3 * 255.0);
        double centre = 0;
        int count = 0;
        for (int y = 32; y < 64; y++)
            for (int x = 32; x < 64; x++, count++)
                centre += (image.Pixels[4 * (y * 96 + x)] + image.Pixels[4 * (y * 96 + x) + 1] + image.Pixels[4 * (y * 96 + x) + 2]) / (3 * 255.0);
        centre /= count;
        Assert.True(mean > corner + 0.01, $"the fixture is not visible (mean {mean:0.000}, background {corner:0.000})");
        Assert.True(centre > corner + 0.03, $"centre {centre:0.000} vs background {corner:0.000}");
        Assert.Single(Directory.GetFiles(store.Directory, "*.png", SearchOption.AllDirectories));
        Assert.Equal(image.Pixels, thumbnails.Render(entry, 96).Pixels); // from the cache

        // orbit frames differ as the camera circles the fixture
        var a = thumbnails.RenderOrbitFrame(entry, 64, 64, 0f);
        var b = thumbnails.RenderOrbitFrame(entry, 64, 64, MathF.PI / 2);
        Assert.NotEqual(a.Pixels, b.Pixels);
    }
}

/// <summary>Vulkan tests run one at a time (lavapipe is memory hungry).</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class VulkanCollection
{
    public const string Name = "Vulkan";
}
