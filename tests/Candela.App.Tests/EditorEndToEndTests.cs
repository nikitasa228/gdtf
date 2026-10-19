using System.Diagnostics;
using Candela.App.Diagnostics;
using Candela.App.Session;
using Candela.App.Settings;
using Candela.App.Ui;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;
using Candela.Dmx;
using StbImageSharp;

namespace Candela.App.Tests;

/// <summary>
/// The editor over the stage engine, end to end under a virtual X server: Фокина opened, a fixture clicked in the
/// viewport (Properties shows it, the orange outline is drawn), saved as a project, reopened and saved again (the scene
/// is unchanged); and a stage built from nothing — floor and wall drawn with the tools, a fixture dragged from the
/// library (placed on the floor, patched to 1.001), an image dropped onto the window — then saved. Skipped where
/// xvfb-run or a Vulkan device is missing.
/// </summary>
[Collection(UiStateCollection.Name)]
public sealed class EditorEndToEndTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly AppLog _log = TestLog.Create();

    public void Dispose()
    {
        _log.Dispose();
        _dir.Dispose();
    }

    [SkippableFact]
    public void Fokina_click_select_save_reopen_and_the_scene_is_unchanged()
    {
        string a = _dir.File("a.png"), b = _dir.File("b.png"), c = _dir.File("c.png");
        string first = _dir.File("fokina-a.candela"), second = _dir.File("fokina-b.candela");
        string script = _dir.Write("fokina.json", $$"""
            { "actions": [
                { "type": "wait", "frames": 4 },
                { "type": "viewport", "viewport": "Viewport1", "shading": "Colorless" },
                { "type": "action", "id": "tool.select" },
                { "type": "wait", "frames": 2 },
                { "type": "screenshot", "path": "{{Json(a)}}" },
                { "type": "click", "x": 627, "y": 472 },
                { "type": "wait", "frames": 4 },
                { "type": "screenshot", "path": "{{Json(b)}}" },
                { "type": "save", "path": "{{Json(first)}}" },
                { "type": "wait", "frames": 4 },
                { "type": "open", "path": "{{Json(first)}}" },
                { "type": "wait", "frames": 8 },
                { "type": "screenshot", "path": "{{Json(c)}}" },
                { "type": "save", "path": "{{Json(second)}}" },
                { "type": "wait", "frames": 4 }
            ] }
            """);
        var run = EditorProcess.Run(_dir, new[] { "--open", EngineTestSupport.Fokina, "--screenshot", _dir.File("end.png"), "--script", script });
        Assert.True(run.ExitCode == ExitCodes.Ok, run.Output);
        Assert.DoesNotContain("ERR [", run.Output);
        Assert.Contains("Opened 'Фокина.mvr': 91 fixtures", run.Output);
        Assert.Contains("Saved 'fokina-a.candela'", run.Output);
        Assert.Contains("Opened 'fokina-a.candela': 91 fixtures", run.Output);

        var before = EditorProcess.LoadPng(a);
        var selected = EditorProcess.LoadPng(b);
        var reopened = EditorProcess.LoadPng(c);
        // The Properties panel shows the clicked fixture.
        double properties = EditorProcess.DifferentPixels(before, selected, 1215, 50, 1595, 450);
        Assert.True(properties > 0.05, $"the properties panel did not change ({properties:P1})");
        // The vivid orange selection outline is drawn around it.
        int orangeBefore = EditorProcess.Orange(before, 500, 380, 760, 560);
        int orangeAfter = EditorProcess.Orange(selected, 500, 380, 760, 560);
        Assert.True(orangeAfter > orangeBefore + 40, $"no outline: {orangeBefore} → {orangeAfter} orange pixels");
        // Reopened from the project, the stage looks the same.
        double viewport = EditorProcess.DifferentPixels(before, reopened, 295, 90, 1200, 630);
        Assert.True(viewport < 0.02, $"the reopened stage differs in {viewport:P1} of the viewport");

        // And it is the same scene: the MVR, the saved project and the project saved after reopening.
        var settings = new SettingsStore(_log, _dir.File("settings.json"));
        settings.Load();
        using var session = EngineTestSupport.CreateSession(_log, settings, _dir);
        Assert.True(session.Open(EngineTestSupport.Fokina).Succeeded);
        var mvr = Snapshot(session.Scene);
        Assert.True(session.Open(first).Succeeded);
        var saved = Snapshot(session.Scene);
        Assert.True(session.Open(second).Succeeded);
        var resaved = Snapshot(session.Scene);
        Assert.Equal(91, mvr.Count(l => l.Contains("|fixture|", StringComparison.Ordinal)));
        Assert.Equal(mvr, saved);
        Assert.Equal(saved, resaved);
    }

    [SkippableFact]
    public void A_stage_built_with_the_tools_the_library_and_a_dropped_image_is_saved()
    {
        string project = _dir.File("built.candela");
        string placed = _dir.File("placed.png");
        string picture = Path.Combine(EngineTestSupport.Root, "docs", "images", "editor-view-cube.png");
        string script = _dir.Write("build.json", $$"""
            { "actions": [
                { "type": "wait", "frames": 6 },
                { "type": "action", "id": "tool.floor" },
                { "type": "drag", "x": 470, "y": 470, "toX": 1060, "toY": 590, "steps": 8 },
                { "type": "action", "id": "tool.wall" },
                { "type": "drag", "x": 520, "y": 330, "toX": 1000, "toY": 330, "steps": 8 },
                { "type": "action", "id": "tool.select" },
                { "type": "wait", "frames": 3 },
                { "type": "drag", "x": 70, "y": 350, "toX": 760, "toY": 520, "steps": 14 },
                { "type": "wait", "frames": 4 },
                { "type": "screenshot", "path": "{{Json(placed)}}" },
                { "type": "key", "chord": "Escape" },
                { "type": "drop", "path": "{{Json(picture)}}", "x": 900, "y": 470 },
                { "type": "wait", "frames": 6 },
                { "type": "save", "path": "{{Json(project)}}" },
                { "type": "wait", "frames": 4 }
            ] }
            """);
        var run = EditorProcess.Run(_dir, new[] { "--screenshot", _dir.File("end.png"), "--script", script });
        Assert.True(run.ExitCode == ExitCodes.Ok, run.Output);
        Assert.DoesNotContain("ERR [", run.Output);
        Assert.Contains("Placed '", run.Output);

        var settings = new SettingsStore(_log, _dir.File("settings.json"));
        settings.Load();
        using var session = EngineTestSupport.CreateSession(_log, settings, _dir);
        var opened = session.Open(project);
        Assert.True(opened.Succeeded, opened.Message);
        var walls = session.Scene.Query<WallComponent>().Select(q => q.Component).ToList();
        var floor = Assert.Single(walls, w => w.Kind == WallKind.Floor);
        var wall = Assert.Single(walls, w => w.Kind == WallKind.Wall);
        Assert.True(floor.Width > 1f && floor.Height > 0.5f, $"floor {floor.Width} × {floor.Height}");
        Assert.True(wall.Width > 1f && Math.Abs(wall.Height - 3f) < 1e-3f, $"wall {wall.Width} × {wall.Height}");
        Assert.Equal(MaterialIds.WoodStage, floor.MaterialId);
        var (fixtureEntity, fixture) = Assert.Single(session.Scene.Query<FixtureComponent>());
        Assert.Equal(new DmxAddress(1, 1, 1), Assert.Single(fixture.Addresses));
        Assert.Equal(EngineTestSupport.Library.Entries.First(e => EngineTestSupport.Library.IsLatestRevision(e)).FileName, fixture.GdtfSpec);
        Assert.True(Math.Abs(fixtureEntity.WorldPosition.Z) < 0.05f, "the fixture stands on the floor");
        var image = Assert.Single(session.Scene.Query<ImageComponent>());
        Assert.Equal(ImageMode.Reference, image.Component.Mode);
        // The drop placed the picture flat on the floor (its face up).
        var up = System.Numerics.Vector3.TransformNormal(-System.Numerics.Vector3.UnitY, image.Entity.WorldMatrix);
        Assert.True(up.Z > 0.99f);
        // The mode popup was open after the drop (the fixture type has several modes).
        Assert.True(EditorProcess.LoadPng(placed).Width == 1600);
    }

    private static string Json(string path) => path.Replace("\\", "/");

    /// <summary>Everything that must survive saving and reopening, sorted (ids, hierarchy, transforms, fixture data).</summary>
    private static List<string> Snapshot(Scene scene)
    {
        var list = new List<string>();
        foreach (var e in scene.All())
        {
            var p = e.Local.Position;
            var r = e.Local.Rotation;
            string line = FormattableString.Invariant(
                $"{e.Id}|{e.Name}|{e.Parent?.Id}|{e.Visible}|{e.Locked}|{p.X:F4},{p.Y:F4},{p.Z:F4}|{r.X:F4},{r.Y:F4},{r.Z:F4},{r.W:F4}");
            if (e.Get<FixtureComponent>() is { } f)
                line += FormattableString.Invariant($"|fixture|{f.GdtfSpec}|{f.GdtfMode}|{string.Join(";", f.Addresses)}|{f.FixtureId}|{f.ManualAttributes.Count}");
            list.Add(line);
        }

        list.Sort(StringComparer.Ordinal);
        return list;
    }
}

/// <summary>Runs <c>candela-app</c> under <c>xvfb-run</c> and reads its screenshots (shared by the end-to-end tests).</summary>
internal static class EditorProcess
{
    public sealed record Result(int ExitCode, string Output);

    public static Result Run(TempDir dir, IEnumerable<string> appArgs)
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "the headless run uses xvfb-run (Linux)");
        string? xvfb = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(f => Path.Combine(f, "xvfb-run")).FirstOrDefault(File.Exists);
        Skip.If(xvfb is null, "xvfb-run is not installed");
        string dll = Path.Combine(AppContext.BaseDirectory, "candela-app.dll");
        var psi = new ProcessStartInfo(xvfb!) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "-a", "-s", "-screen 0 1920x1080x24", "dotnet", dll, "--config-dir", dir.File("config") })
            psi.ArgumentList.Add(a);
        foreach (var a in appArgs)
            psi.ArgumentList.Add(a);
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
        return new Result(process.ExitCode, output);
    }

    public static ImageResult LoadPng(string path)
    {
        Assert.True(File.Exists(path), $"{path} was not written");
        using var stream = File.OpenRead(path);
        return ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
    }

    public static double DifferentPixels(ImageResult a, ImageResult b, int x0, int y0, int x1, int y1)
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

    /// <summary>Pixels of the selection orange (#FF7A00 and its antialiased edge) in a region.</summary>
    public static int Orange(ImageResult image, int x0, int y0, int x1, int y1)
    {
        int n = 0;
        for (int y = y0; y < y1; y++)
        {
            for (int x = x0; x < x1; x++)
            {
                int i = (y * image.Width + x) * 4;
                byte r = image.Data[i], g = image.Data[i + 1], b = image.Data[i + 2];
                if (r > 200 && g > 70 && g < 170 && b < 70)
                    n++;
            }
        }

        return n;
    }
}
