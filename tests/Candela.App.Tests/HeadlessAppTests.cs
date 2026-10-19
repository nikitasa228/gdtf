using System.Diagnostics;
using StbImageSharp;

namespace Candela.App.Tests;

/// <summary>
/// End-to-end runs of the editor under a virtual X server (<c>xvfb-run</c>): the real window, Vulkan swapchain, Dear
/// ImGui backends and frame loop, captured with <c>--screenshot</c>. Skipped where xvfb-run or a Vulkan device is
/// missing (Windows, macOS, CI without Xvfb).
/// </summary>
[Collection(UiStateCollection.Name)]
public sealed class HeadlessAppTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static string? FindOnPath(string name)
    {
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(folder, name);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    /// <summary>The editor assembly next to the tests (copied with its runtime config by the project reference).</summary>
    private static string AppDll()
    {
        string dll = Path.Combine(AppContext.BaseDirectory, "candela-app.dll");
        Assert.True(File.Exists(dll), $"{dll} is missing");
        Assert.True(File.Exists(Path.ChangeExtension(dll, ".runtimeconfig.json")), "candela-app.runtimeconfig.json is missing");
        return dll;
    }

    private sealed record Run(int ExitCode, string Output);

    private Run RunApp(IEnumerable<string> appArgs, IDictionary<string, string>? environment = null)
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "the headless run uses xvfb-run (Linux)");
        string? xvfb = FindOnPath("xvfb-run");
        Skip.If(xvfb is null, "xvfb-run is not installed");
        var psi = new ProcessStartInfo(xvfb!)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in new[] { "-a", "-s", "-screen 0 1920x1080x24", "dotnet", AppDll(), "--config-dir", _dir.File("config") })
            psi.ArgumentList.Add(a);
        foreach (var a in appArgs)
            psi.ArgumentList.Add(a);
        if (environment is not null)
        {
            foreach (var (k, v) in environment)
                psi.Environment[k] = v;
        }

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromSeconds(150)))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("the editor did not exit within 150 s");
        }

        process.WaitForExit();
        string output = stdout.Result + stderr.Result;
        if (process.ExitCode == ExitCodes.NoVulkan && environment is null)
            Skip.If(true, "no Vulkan device: " + output);
        return new Run(process.ExitCode, output);
    }

    private static ImageResult LoadPng(string path)
    {
        Assert.True(File.Exists(path), $"{path} was not written");
        using var stream = File.OpenRead(path);
        return ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
    }

    /// <summary>Number of distinct colours (quantised to 5 bits per channel) and the luminance standard deviation of a region.</summary>
    private static (int Colors, double StdDev) Stats(ImageResult image, int x0, int y0, int x1, int y1)
    {
        var colors = new HashSet<int>();
        double sum = 0, sumSq = 0;
        int n = 0;
        for (int y = y0; y < y1; y++)
        {
            for (int x = x0; x < x1; x++)
            {
                int i = (y * image.Width + x) * 4;
                byte r = image.Data[i], g = image.Data[i + 1], b = image.Data[i + 2];
                colors.Add((r >> 3) << 10 | (g >> 3) << 5 | (b >> 3));
                double l = 0.2126 * r + 0.7152 * g + 0.0722 * b;
                sum += l;
                sumSq += l * l;
                n++;
            }
        }

        double mean = sum / n;
        return (colors.Count, Math.Sqrt(Math.Max(0, sumSq / n - mean * mean)));
    }

    private static double DifferentPixels(ImageResult a, ImageResult b, int x0, int y0, int x1, int y1)
    {
        int different = 0, n = 0;
        for (int y = y0; y < y1; y++)
        {
            for (int x = x0; x < x1; x++)
            {
                int i = (y * a.Width + x) * 4;
                if (Math.Abs(a.Data[i] - b.Data[i]) + Math.Abs(a.Data[i + 1] - b.Data[i + 1]) + Math.Abs(a.Data[i + 2] - b.Data[i + 2]) > 24)
                    different++;
                n++;
            }
        }

        return different / (double)n;
    }

    [SkippableFact]
    public void Screenshot_of_the_default_layout_is_a_real_frame()
    {
        string png = _dir.File("default.png");
        // The renderer's test scene (--preview-stage) gives the viewport known, lit content.
        var run = RunApp(new[] { "--preview-stage", "--screenshot", png, "--frames", "6" });
        Assert.True(run.ExitCode == ExitCodes.Ok, run.Output);
        var image = LoadPng(png);
        Assert.Equal((1600, 900), (image.Width, image.Height));
        var whole = Stats(image, 0, 0, image.Width, image.Height);
        Assert.True(whole.Colors > 300, $"only {whole.Colors} colours: blank or broken frame");
        Assert.True(whole.StdDev > 8, $"luminance deviation {whole.StdDev:0.0}: flat frame");
        // The viewport (centre) shows the lit scene; the side panels show text on a dark background.
        var viewport = Stats(image, 400, 120, 1150, 560);
        Assert.True(viewport.Colors > 200, $"viewport has {viewport.Colors} colours");
        var library = Stats(image, 0, 40, 280, 300);
        Assert.True(library.StdDev > 5, "the library panel has text");
        Assert.DoesNotContain("ERR [", run.Output);
    }

    [SkippableFact]
    public void Scripted_click_selects_an_object_and_the_properties_panel_shows_it()
    {
        string before = _dir.File("before.png");
        string after = _dir.File("after.png");
        string end = _dir.File("end.png");
        string script = _dir.Write("select.json", $$"""
            { "actions": [
                { "type": "wait", "frames": 3 },
                { "type": "screenshot", "path": "{{before.Replace("\\", "/")}}" },
                { "type": "click", "x": 763, "y": 365 },
                { "type": "wait", "frames": 3 },
                { "type": "screenshot", "path": "{{after.Replace("\\", "/")}}" },
                { "type": "key", "chord": "Escape" },
                { "type": "wait", "frames": 2 }
            ] }
            """);
        var run = RunApp(new[] { "--preview-stage", "--screenshot", end, "--script", script });
        Assert.True(run.ExitCode == ExitCodes.Ok, run.Output);
        var a = LoadPng(before);
        var b = LoadPng(after);
        var c = LoadPng(end);
        // Properties (right) changes from the empty state to the selected object's fields, and back after Escape.
        double changed = DifferentPixels(a, b, 1215, 50, 1595, 450);
        Assert.True(changed > 0.01, $"the properties panel did not change ({changed:P2})");
        double restored = DifferentPixels(a, c, 1215, 50, 1595, 450);
        Assert.True(restored < 0.005, $"Escape did not clear the selection ({restored:P2} differ)");
        // The selection outline / gizmo appear around the sphere.
        Assert.True(DifferentPixels(a, b, 690, 290, 840, 440) > 0.01);
    }

    [SkippableFact]
    public void Shortcuts_work_again_after_fly_mode_was_interrupted_by_a_tab_switch()
    {
        // Right button held (fly mode suspends the shortcuts), then the viewport's tab is covered by a new viewport, and
        // the button is released while the first viewport is hidden: Ctrl+A must still select everything afterwards.
        string before = _dir.File("fly-before.png");
        string end = _dir.File("fly-end.png");
        string script = _dir.Write("fly.json", $$"""
            { "actions": [
                { "type": "wait", "frames": 3 },
                { "type": "press", "x": 700, "y": 400, "button": "right" },
                { "type": "move", "x": 710, "y": 405 },
                { "type": "wait", "frames": 2 },
                { "type": "action", "id": "view.newViewport" },
                { "type": "wait", "frames": 3 },
                { "type": "release", "button": "right" },
                { "type": "wait", "frames": 2 },
                { "type": "move", "x": 700, "y": 400 },
                { "type": "screenshot", "path": "{{before.Replace("\\", "/")}}" },
                { "type": "key", "chord": "Ctrl+A" },
                { "type": "wait", "frames": 3 }
            ] }
            """);
        var run = RunApp(new[] { "--preview-stage", "--screenshot", end, "--script", script });
        Assert.True(run.ExitCode == ExitCodes.Ok, run.Output);
        // The status bar's left part gains the orange "8 selected".
        double changed = DifferentPixels(LoadPng(before), LoadPng(end), 80, 876, 400, 898);
        Assert.True(changed > 0.01, $"Ctrl+A did nothing after the interrupted fly ({changed:P2} of the status bar changed)");
    }

    [SkippableFact]
    public void A_session_with_resizes_tear_offs_and_new_viewports_has_no_validation_errors()
    {
        string script = _dir.Write("stress.json", """
            { "actions": [
                { "type": "wait", "frames": 2 },
                { "type": "layout", "name": "viewports-4" },
                { "type": "select", "names": ["Gold sphere", "Painted box"] },
                { "type": "wait", "frames": 2 },
                { "type": "window", "width": 1000, "height": 640 },
                { "type": "wait", "frames": 2 },
                { "type": "undock", "panel": "Properties", "x": 1050, "y": 40, "width": 400, "height": 420 },
                { "type": "undock", "panel": "Viewport4", "x": 1050, "y": 500, "width": 480, "height": 320 },
                { "type": "wait", "frames": 3 },
                { "type": "action", "id": "view.newViewport" },
                { "type": "setting", "name": "uiScale", "value": "1.5" },
                { "type": "wait", "frames": 2 },
                { "type": "window", "width": 1400, "height": 820 },
                { "type": "panel", "panel": "Viewport5", "open": false },
                { "type": "setting", "name": "uiScale", "value": "1" },
                { "type": "wait", "frames": 2 },
                { "type": "layout", "name": "default" },
                { "type": "wait", "frames": 3 }
            ] }
            """);
        var run = RunApp(new[] { "--preview-stage", "--validation", "--screenshot", _dir.File("stress.png"), "--script", script });
        Assert.True(run.ExitCode == ExitCodes.Ok, run.Output);
        Assert.Contains("validation on", run.Output);
        Assert.DoesNotContain("ERR [", run.Output);
        Assert.DoesNotContain("VUID-", run.Output);
        Assert.DoesNotContain("SYNC-", run.Output);
    }

    [SkippableFact]
    public void Without_a_vulkan_driver_the_editor_explains_and_exits_with_code_3()
    {
        var run = RunApp(new[] { "--screenshot", _dir.File("none.png") },
            new Dictionary<string, string> { ["VK_ICD_FILENAMES"] = "/nonexistent/icd.json", ["VK_DRIVER_FILES"] = "/nonexistent/icd.json" });
        Assert.Equal(ExitCodes.NoVulkan, run.ExitCode);
        Assert.Contains("Vulkan", run.Output);
        Assert.False(File.Exists(_dir.File("none.png")));
    }

    [SkippableFact]
    public void Invalid_command_lines_exit_with_the_usage_code()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsWindows() || OperatingSystem.IsMacOS());
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        psi.ArgumentList.Add(AppDll());
        psi.ArgumentList.Add("--frames");
        psi.ArgumentList.Add("lots");
        using var process = Process.Start(psi)!;
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(TimeSpan.FromSeconds(60)));
        Assert.Equal(ExitCodes.Usage, process.ExitCode);
        Assert.Contains("--frames", output);
    }
}
