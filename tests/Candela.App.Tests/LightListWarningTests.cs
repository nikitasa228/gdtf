using System.Diagnostics;
using Candela.App.Ui;
using Candela.App.Viewport;
using Candela.Rendering;
using Candela.Rendering.Vulkan;
using StbImageSharp;

namespace Candela.App.Tests;

/// <summary>
/// The light-list overflow warning (lights beyond the renderer's clustered-light limit are not drawn): the viewport
/// shows it in its corner, in the UI language, and the log gets the renderer's warning — checked end to end under a
/// virtual X server with the limit lowered through <c>CANDELA_MAX_CLUSTERED_LIGHTS</c> on the preview stage (3 lights),
/// against a run without the limit.
/// </summary>
[Collection(UiStateCollection.Name)]
public sealed class LightListWarningTests : IDisposable
{
    /// <summary>Theme.Warning (#F2B33D), the colour of the warning line.</summary>
    private static readonly (int R, int G, int B) WarningColor = (0xF2, 0xB3, 0x3D);

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void TheWarningNamesTheMissingLightsInEitherLanguage()
    {
        string language = Loc.Language;
        try
        {
            var lists = new LightListDiagnostics(Lights: 1295, ListedLights: 512, MaxLightsInCluster: 512, OverflowedClusters: 40);
            Loc.SetLanguage("en");
            Assert.Equal("", ViewportPanel.LightListWarningText(new LightListDiagnostics(1295, 1295, 1295, 0)));
            Assert.Equal("", ViewportPanel.LightListWarningText(default));
            string english = ViewportPanel.LightListWarningText(lists);
            Assert.Contains("783 of 1295 lights are not drawn", english, StringComparison.Ordinal);
            Assert.Contains("512", english, StringComparison.Ordinal);
            Loc.SetLanguage("ru");
            string russian = ViewportPanel.LightListWarningText(lists);
            Assert.Contains("Не отрисовано 783 из 1295 источников", russian, StringComparison.Ordinal);
            Assert.Contains("512", russian, StringComparison.Ordinal);
        }
        finally
        {
            Loc.SetLanguage(language);
        }
    }

    /// <summary>
    /// The warning line is built once per set of numbers in it: the busiest cluster and the overflowed clusters change
    /// with the camera every frame, which must neither rebuild the text (no allocation in the frame loop) nor change it.
    /// </summary>
    [Fact]
    public void TheWarningLineIsRebuiltOnlyWhenItsNumbersOrTheLanguageChange()
    {
        string language = Loc.Language;
        try
        {
            Loc.SetLanguage("en");
            var label = new ViewportPanel.LightListWarningLabel();
            var lists = new LightListDiagnostics(Lights: 1295, ListedLights: 512, MaxLightsInCluster: 512, OverflowedClusters: 40);
            string first = label.For(lists);
            Assert.Contains("783 of 1295 lights are not drawn", first, StringComparison.Ordinal);
            Assert.Same(first, label.For(lists with { MaxLightsInCluster = 300, OverflowedClusters = 7 }));
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 1; i <= 100; i++)
                _ = label.For(lists with { MaxLightsInCluster = i, OverflowedClusters = i });
            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);

            Assert.Equal("", label.For(lists with { OverflowedClusters = 0 }));
            string more = label.For(lists with { Lights = 1300 });
            Assert.Contains("788 of 1300 lights", more, StringComparison.Ordinal);
            Loc.SetLanguage("ru");
            Assert.Contains("Не отрисовано 788 из 1300", label.For(lists with { Lights = 1300 }), StringComparison.Ordinal);
        }
        finally
        {
            Loc.SetLanguage(language);
        }
    }

    [SkippableFact]
    public void The_viewport_and_the_log_warn_when_lights_are_beyond_the_clustered_limit()
    {
        var limited = Run("limited", limit: "1");
        Assert.True(limited.ExitCode == ExitCodes.Ok, limited.Output);
        Assert.Contains("WRN [Vulkan] Light lists overflowed: 2 of 3 lights are beyond the limit of 1 clustered lights", limited.Output);

        var normal = Run("normal", limit: null);
        Assert.True(normal.ExitCode == ExitCodes.Ok, normal.Output);
        Assert.DoesNotContain("Light lists overflowed", normal.Output);

        // The warning line sits at the bottom of the viewport image (x 290 … 1205, y 45 … 638 in the 1600 × 900 window).
        int warned = WarningPixels(limited.Screenshot, 290, 560, 1205, 640);
        int clean = WarningPixels(normal.Screenshot, 290, 560, 1205, 640);
        Assert.True(warned > 150, $"{warned} warning-coloured pixels in the corner of the viewport with the limit");
        Assert.True(clean < 20, $"{clean} warning-coloured pixels in the corner of the viewport without the limit");
    }

    private sealed record Result(int ExitCode, string Output, ImageResult Screenshot);

    /// <summary>The editor on the preview stage for a few frames, with <c>CANDELA_MAX_CLUSTERED_LIGHTS</c> = <paramref name="limit"/>.</summary>
    private Result Run(string name, string? limit)
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "the headless run uses xvfb-run (Linux)");
        string? xvfb = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(f => Path.Combine(f, "xvfb-run")).FirstOrDefault(File.Exists);
        Skip.If(xvfb is null, "xvfb-run is not installed");
        string screenshot = _dir.File(name + ".png");
        var psi = new ProcessStartInfo(xvfb!) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[]
                 {
                     "-a", "-f", _dir.File(name + ".Xauthority"), "-s", "-screen 0 1920x1080x24", "dotnet", Path.Combine(AppContext.BaseDirectory, "candela-app.dll"),
                     "--config-dir", _dir.File(name + "-config"), "--preview-stage", "--frames", "10", "--screenshot", screenshot,
                 })
        {
            psi.ArgumentList.Add(a);
        }

        if (limit is null)
            psi.Environment.Remove(VulkanRendererOptions.MaxClusteredLightsEnvironmentVariable);
        else
            psi.Environment[VulkanRendererOptions.MaxClusteredLightsEnvironmentVariable] = limit;
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromSeconds(180)))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("the editor did not exit within 180 s");
        }

        process.WaitForExit();
        string output = stdout.Result + stderr.Result;
        Skip.If(process.ExitCode == ExitCodes.NoVulkan, "no Vulkan device: " + output);
        Assert.True(File.Exists(screenshot), $"no screenshot\n{output}");
        using var stream = File.OpenRead(screenshot);
        return new Result(process.ExitCode, output, ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha));
    }

    /// <summary>Pixels close to the warning colour (its text, antialiased edges excluded) in a region.</summary>
    private static int WarningPixels(ImageResult image, int x0, int y0, int x1, int y1)
    {
        int n = 0;
        for (int y = y0; y < Math.Min(y1, image.Height); y++)
        {
            for (int x = x0; x < Math.Min(x1, image.Width); x++)
            {
                int i = (y * image.Width + x) * 4;
                if (Math.Abs(image.Data[i] - WarningColor.R) + Math.Abs(image.Data[i + 1] - WarningColor.G) + Math.Abs(image.Data[i + 2] - WarningColor.B) < 40)
                    n++;
            }
        }

        return n;
    }
}
