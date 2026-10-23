using System.Globalization;
using System.Text.RegularExpressions;
using Xunit.Abstractions;

namespace Candela.App.Tests;

/// <summary>
/// The reported freeze and crash while moving the cursor over the fixture library, in the real editor with the real
/// Dear ImGui Vulkan backend and the validation layer: tiles are hovered one after another by script (each grows and
/// asks the thumbnail worker for its rotating preview), with the Library's texture cap lowered so that the short run
/// releases many pictures. Before the fix the backend never destroyed a released picture (Dear ImGui does not count
/// <c>UnusedFrames</c> for user textures) and they piled up until its descriptor pool ran out; now every released
/// picture is destroyed a few frames later, no destruction touches an image still in flight (the validation layer
/// would report it), and the run ends cleanly.
/// </summary>
[Collection(UiStateCollection.Name)]
public sealed class LibraryHoverEndToEndTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly ITestOutputHelper _output;

    public LibraryHoverEndToEndTests(ITestOutputHelper output) => _output = output;

    public void Dispose() => _dir.Dispose();

    [SkippableFact]
    public void Hovering_tiles_releases_pictures_through_the_backend_without_validation_errors()
    {
        const int cap = 32;
        var actions = new List<string>
        {
            """{ "type": "wait", "frames": 2 }""",
            $$"""{ "type": "setting", "name": "libraryTextureCap", "value": "{{cap}}" }""",
        };
        // Tiles far apart, so every hover scrolls a new screen of tiles into view and asks for its pictures.
        for (int i = 0; i < 14; i++)
        {
            actions.Add($$"""{ "type": "move", "tile": {{i * 23}} }""");
            actions.Add("""{ "type": "wait", "frames": 4 }""");
        }

        string script = _dir.Write("hover.json", "{ \"actions\": [\n" + string.Join(",\n", actions) + "\n] }");
        var run = EditorProcess.Run(_dir, new[] { "--width", "1024", "--height", "640", "--validation", "--screenshot", _dir.File("end.png"), "--script", script });
        Assert.True(run.ExitCode == ExitCodes.Ok, $"exit {run.ExitCode}: {run.Output}");
        Assert.Contains("validation on", run.Output);
        Assert.DoesNotContain("ERR [", run.Output);
        Assert.DoesNotContain("VUID-", run.Output);
        Assert.DoesNotContain("SYNC-", run.Output);

        // "Pictures: 113 textures made, 85 freed, 28 alive, 0 waiting for the GPU, 0 refused." when the panel goes away
        var stats = Regex.Match(run.Output, @"Pictures: (\d+) textures made, (\d+) freed, (\d+) alive, (\d+) waiting for the GPU, (\d+) refused");
        Assert.True(stats.Success, run.Output);
        _output.WriteLine(stats.Value);
        int Value(int group) => int.Parse(stats.Groups[group].Value, CultureInfo.InvariantCulture);
        int made = Value(1), freed = Value(2), alive = Value(3), waiting = Value(4), refused = Value(5);
        Assert.True(made > cap, $"only {made} pictures were made: the hovers did not ask for enough");
        Assert.Equal(made, freed + alive + waiting);
        Assert.True(alive <= LibraryPanelCap(cap), $"{alive} pictures alive above the cap of {cap}");
        // Released pictures are destroyed by the backend within a few frames (only the last frames' ones may still wait),
        // not kept until the descriptor pool runs out (before the fix: 0 freed, every released one waiting).
        int released = made - alive;
        Assert.True(released >= cap / 2, $"only {released} pictures were released: the run did not exercise the release");
        Assert.True(freed > 0 && waiting <= released / 2, $"{freed} of {released} released pictures freed, {waiting} still waiting for the GPU");
        Assert.Equal(0, refused);
    }

    /// <summary>Most pictures alive after an eviction pass: the cap plus what one frame can add (6) and keep drawn.</summary>
    private static int LibraryPanelCap(int cap) => cap + 6 + Candela.App.Panels.LibraryPanel.OrbitFrames;
}
