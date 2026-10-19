using System.Numerics;
using Candela.App.Diagnostics;
using Candela.App.Settings;
using Candela.App.Ui;
using Candela.Electrical;

namespace Candela.App.Tests;

/// <summary>
/// The real editor under a virtual X server with names, labels and paths full of printf conversions ("%s %n %%"): a
/// project in a folder with such a name, a fixture, a console and a cable named that way, a GDTF file named that way
/// dropped onto the window (added to the fixture library and selected there), the file browser listing such files, and
/// the same in Russian. The editor neither crashes nor prints garbage: the text it draws (logged with the
/// <c>uiText</c> script action) contains the names literally. Skipped where xvfb-run or a Vulkan device is missing.
/// </summary>
[Collection(UiStateCollection.Name)]
public sealed class SafeTextEndToEndTests : IDisposable
{
    private const string Nasty = SafeTextTests.Nasty;
    private readonly TempDir _dir = new();
    private readonly AppLog _log = TestLog.Create();

    public void Dispose()
    {
        _log.Dispose();
        _dir.Dispose();
    }

    /// <summary>A string as the inside of a JSON string literal.</summary>
    private static string Json(string text) => System.Text.Json.JsonEncodedText.Encode(text).ToString();

    [SkippableFact]
    public void Names_and_paths_with_printf_conversions_are_drawn_literally_and_a_dropped_gdtf_is_selected_in_the_library()
    {
        Loc.SetLanguage("en");
        string folder = Path.Combine(_dir.Path, "Stage " + Nasty);
        Directory.CreateDirectory(folder);
        string project = Path.Combine(folder, "Show " + Nasty + ".candela");
        var settings = new SettingsStore(_log, _dir.File("settings.json"));
        settings.Load();
        using (var session = EngineTestSupport.CreateSession(_log, settings, _dir))
        {
            var engine = session.Engine!;
            var head = StageTestSupport.AddFixture(engine, "Spot " + Nasty, 1, new Vector3(0f, 0f, 4f));
            var console = StageTestSupport.AddConsole(engine, "Desk " + Nasty, "DMX A", 1);
            var dmxIn = engine.Network.Ports.GetPorts(head).First(p => p.Role is PortRole.DataIn && p.Signal == SignalKind.Dmx512);
            StageTestSupport.AddCable(engine, CableCatalog.Ids.DmxXlr5, "DMX " + Nasty, (console, "DMX A"), (head, dmxIn.Name));
            StageTestSupport.AddWall(engine, "Floor " + Nasty, 8f, 6f, Core.SceneGraph.Components.WallKind.Floor);
            var saved = session.SaveAs(project);
            Assert.True(saved.Succeeded, saved.Message);
        }

        File.WriteAllText(Path.Combine(folder, "Other " + Nasty + ".mvr"), "");
        File.WriteAllText(Path.Combine(folder, "a##b.mvr"), "");
        string gdtf = Path.Combine(folder, "Fixture " + Nasty + ".gdtf");
        File.Copy(Directory.EnumerateFiles(EngineTestSupport.Root, "ACME@ACME_AECO_15@*.gdtf").First(), gdtf);

        string Shot(string name) => _dir.File(name + ".png");
        string Text(string name) => _dir.File(name + ".txt");
        string script = _dir.Write("nasty.json", $$"""
            { "actions": [
                { "type": "wait", "frames": 6 },
                { "type": "select", "names": [ "{{Json("Spot " + Nasty)}}" ] },
                { "type": "wait", "frames": 4 },
                { "type": "screenshot", "path": "{{Json(Shot("selected"))}}" },
                { "type": "uiText", "path": "{{Json(Text("selected"))}}" },
                { "type": "drop", "path": "{{Json(gdtf)}}", "x": 700, "y": 420 },
                { "type": "wait", "frames": 10 },
                { "type": "screenshot", "path": "{{Json(Shot("library"))}}" },
                { "type": "action", "id": "file.open" },
                { "type": "wait", "frames": 4 },
                { "type": "screenshot", "path": "{{Json(Shot("browser"))}}" },
                { "type": "uiText", "path": "{{Json(Text("browser"))}}" },
                { "type": "key", "chord": "Escape" },
                { "type": "wait", "frames": 2 },
                { "type": "setting", "name": "language", "value": "ru" },
                { "type": "wait", "frames": 4 },
                { "type": "screenshot", "path": "{{Json(Shot("russian"))}}" },
                { "type": "uiText", "path": "{{Json(Text("russian"))}}" }
            ] }
            """);
        var run = EditorProcess.Run(_dir, new[] { "--open", project, "--screenshot", Shot("end"), "--script", script });
        // CANDELA_KEEP_SCREENSHOTS=<folder> keeps the screenshots and text logs for a look.
        if (Environment.GetEnvironmentVariable("CANDELA_KEEP_SCREENSHOTS") is { Length: > 0 } keep)
        {
            Directory.CreateDirectory(keep);
            foreach (string file in Directory.EnumerateFiles(_dir.Path).Where(f => f.EndsWith(".png", StringComparison.Ordinal) || f.EndsWith(".txt", StringComparison.Ordinal)))
                File.Copy(file, Path.Combine(keep, "safetext-" + Path.GetFileName(file)), overwrite: true);
        }

        Assert.True(run.ExitCode == ExitCodes.Ok, run.Output);
        Assert.DoesNotContain("ERR [", run.Output);
        Assert.Contains($"Opened 'Show {Nasty}.candela'", run.Output);
        // The dropped GDTF reached the fixture library and the Library panel heard of it.
        Assert.Contains($"Added 'Fixture {Nasty}.gdtf' (", run.Output);
        Assert.Contains("fixture type(s) added to the library (Import)", run.Output);

        // What the editor drew: the names exactly, never a formatted rendering of them.
        string selected = File.ReadAllText(Text("selected"));
        Assert.Contains("DMX " + Nasty, selected, StringComparison.Ordinal);
        Assert.Contains("Spot " + Nasty, selected, StringComparison.Ordinal);
        string browser = File.ReadAllText(Text("browser"));
        Assert.Contains($"Show {Nasty}.candela", browser, StringComparison.Ordinal);
        Assert.Contains($"Other {Nasty}.mvr", browser, StringComparison.Ordinal);
        Assert.Contains("a# #b.mvr", browser, StringComparison.Ordinal);
        string russian = File.ReadAllText(Text("russian"));
        Assert.Contains("DMX " + Nasty, russian, StringComparison.Ordinal);
        foreach (string name in new[] { "selected", "library", "browser", "russian" })
            Assert.True(new FileInfo(Shot(name)).Length > 10_000, $"{name}.png is missing or empty");
    }
}
