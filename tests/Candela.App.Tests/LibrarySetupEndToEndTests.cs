namespace Candela.App.Tests;

/// <summary>
/// The real editor under a virtual X server with <c>--library-setup</c>: the setup window opens over the workspace, a
/// folder chosen with the scripted equivalent of its buttons (a path with Cyrillic letters and spaces) is counted and
/// read into the library, and the window says so — the text it draws is logged with the <c>uiText</c> script action.
/// Skipped where xvfb-run or a Vulkan device is missing.
/// </summary>
[Collection(UiStateCollection.Name)]
public sealed class LibrarySetupEndToEndTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static string Json(string text) => System.Text.Json.JsonEncodedText.Encode(text).ToString();

    [SkippableFact]
    public void The_setup_window_reads_a_chosen_folder_into_the_library()
    {
        string library = Path.Combine(_dir.Path, "Мои приборы", "GDTF files");
        Directory.CreateDirectory(Path.Combine(library, "older"));
        // a name the repository's library does not have, so the engine really adds it
        File.Copy(Path.Combine(EngineTestSupport.Root, "Martin_Professional@VDO_Atomic_Bold@20260620.gdtf"),
            Path.Combine(library, "Candela_Test@Setup_Atomic@1.gdtf"));
        File.Copy(Path.Combine(EngineTestSupport.Root, "Ayrton@Zonda_3_FX@V1.81_Corrected_Factory_Tilt.gdtf"),
            Path.Combine(library, "older", "Ayrton@Zonda_3_FX@V1.81_Corrected_Factory_Tilt.gdtf"));
        string before = _dir.File("before.txt");
        string after = _dir.File("after.txt");
        string script = _dir.Write("setup.json", $$"""
            { "actions": [
                { "type": "wait", "frames": 3 },
                { "type": "uiText", "path": "{{Json(before)}}" },
                { "type": "setting", "name": "librarySetup", "value": "{{Json(library)}}" },
                { "type": "wait", "frames": 12 },
                { "type": "uiText", "path": "{{Json(after)}}" },
                { "type": "setting", "name": "librarySetup", "value": "close" },
                { "type": "wait", "frames": 2 }
            ] }
            """);

        var run = EditorProcess.Run(_dir, new[] { "--library-setup", "--screenshot", _dir.File("shot.png"), "--script", script, "--frames", "20" });

        Assert.True(run.ExitCode == 0, run.Output);
        string first = File.ReadAllText(before);
        Assert.Contains("Welcome to Candela", first, StringComparison.Ordinal);
        Assert.Contains("No folder chosen yet.", first, StringComparison.Ordinal);
        string chosen = File.ReadAllText(after);
        Assert.Contains(library, chosen, StringComparison.Ordinal);
        Assert.Contains("1 fixture files (.gdtf) found", chosen, StringComparison.Ordinal);
        Assert.Contains("1 more .gdtf files are in subfolders", chosen, StringComparison.Ordinal);
        Assert.True(run.Output.Contains("Added 1 fixture type file(s)", StringComparison.Ordinal), run.Output);
        // the run ended normally: no crash report
        Assert.Empty(Directory.GetFiles(_dir.File("config"), "crash-*.txt", SearchOption.AllDirectories));
    }
}
