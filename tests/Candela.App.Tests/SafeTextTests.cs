using System.Numerics;
using System.Text.RegularExpressions;
using Candela.App.Files;
using Candela.App.Panels;
using Candela.App.Session;
using Candela.App.Ui;
using Candela.Core.Settings;
using Candela.Dmx.Input;
using Candela.Electrical;
using Candela.Engine;
using Hexa.NET.ImGui;

namespace Candela.App.Tests;

/// <summary>
/// Text from users and files is drawn literally: no printf-style ImGui call is left in the editor's sources, names with
/// "##" keep their whole text in labels, and panels fed with names, labels and paths containing printf conversions
/// ("%s %n %%") render exactly those characters (captured with ImGui's text log) instead of garbage or a crash.
/// </summary>
[Collection(UiStateCollection.Name)]
public sealed partial class SafeTextTests : IDisposable
{
    /// <summary>printf conversions that would read (%s) or write (%n) through missing arguments.</summary>
    internal const string Nasty = "%s %n %%";

    public SafeTextTests() => Loc.SetLanguage("en");

    public void Dispose() => Loc.SetLanguage("en");

    [GeneratedRegex(@"\bImGuiP?\s*\.\s*(Text|TextColored|TextDisabled|TextWrapped|LabelText|BulletText|SetTooltip|SetItemTooltip|LogText|TextV|TextColoredV|TextDisabledV|TextWrappedV|LabelTextV|BulletTextV|SetTooltipV|SetItemTooltipV)\s*\(")]
    private static partial Regex FormatCall();

    [GeneratedRegex(@"\bImGuiP?\s*\.\s*TreeNode(Ex)?\s*\([^;]*,[^;]*,\s*[^;]*\)")]
    private static partial Regex FormattedTreeNode();

    [Fact]
    public void No_source_file_calls_an_imgui_function_that_reads_its_text_as_a_printf_format()
    {
        string sources = Path.Combine(EngineTestSupport.Root, "src", "Candela.App");
        var offenders = new List<string>();
        int files = 0;
        foreach (string file in Directory.EnumerateFiles(sources, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                continue;
            files++;
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].TrimStart();
                if (line.StartsWith("//", StringComparison.Ordinal))
                    continue;
                if (FormatCall().IsMatch(line) || FormattedTreeNode().IsMatch(line))
                    offenders.Add($"{Path.GetRelativePath(sources, file)}:{i + 1}: {line}");
            }
        }

        Assert.True(files > 50, $"only {files} source files found under {sources}");
        Assert.True(offenders.Count == 0, "printf-style ImGui text calls (use SafeText or TextUnformatted):\n" + string.Join("\n", offenders));
    }

    [Theory]
    [InlineData("Spot 1", "Spot 1")]
    [InlineData("a##b", "a# #b")]
    [InlineData("###hidden id", "# # #hidden id")]
    [InlineData("ends with ##", "ends with # #")]
    [InlineData("%s %n %% #1 # 2", "%s %n %% #1 # 2")]
    [InlineData("", "")]
    public void Labels_keep_the_whole_name_visible(string name, string expected)
    {
        string label = SafeText.Label(name);
        Assert.Equal(expected, label);
        Assert.DoesNotContain("##", label, StringComparison.Ordinal);
        Assert.Equal("", SafeText.Label(null));
    }

    [Fact]
    public void A_label_without_hashes_is_the_same_string()
    {
        string name = "Fixture " + Nasty;
        Assert.Same(name, SafeText.Label(name));
    }

    [Fact]
    public void Panels_render_names_labels_and_paths_with_printf_conversions_literally()
    {
        using var dir = new TempDir();
        using var engine = StageTestSupport.CreateEngine(dir);
        var head = StageTestSupport.AddFixture(engine, "Spot " + Nasty, 1, new Vector3(1, 0, 5));
        var console = StageTestSupport.AddConsole(engine, "Desk " + Nasty, "DMX A", 1);
        var ports = engine.Network.Ports.GetPorts(head);
        var dmxIn = ports.First(p => p.Role is PortRole.DataIn && p.Signal == SignalKind.Dmx512);
        var cable = StageTestSupport.AddCable(engine, CableCatalog.Ids.DmxXlr5, "DMX " + Nasty, (console, "DMX A"), (head, dmxIn.Name));
        var wall = StageTestSupport.AddWall(engine, "Wall " + Nasty, 6, 3);
        engine.Tick(0.02);
        using var tools = StageTestSupport.CreateTools();
        tools.External.CanSave = false;
        var app = new AppSettings();
        var stage = StagePanelContext.ForEngine(engine, tools, app);
        using var ui = new HeadlessImGui(new Vector2(1400, 1000));

        // The properties of the fixture (its cable link names the cable and the console) and the cable list.
        var section = new StagePropertiesSection(stage);
        string properties = Capture(ui, () => Window("props", new Vector2(520, 1400), () => section.Draw(head, 92f)));
        Assert.Contains("DMX " + Nasty, properties, StringComparison.Ordinal);
        var links = new CableLinksPanel(stage);
        engine.Selection.Set(cable.Id);
        string cables = Capture(ui, () => Window("links", new Vector2(1200, 600), links.Draw));
        // (The device cards are painted with the draw list, which takes no format either.)
        Assert.Contains("DMX " + Nasty, cables, StringComparison.Ordinal);
        Capture(ui, () => Window("wall", new Vector2(520, 900), () => section.Draw(wall, 92f)));

        // The recorder: a recording with such a name, loaded from a folder with such a name.
        string folder = Path.Combine(dir.Path, "Recordings " + Nasty);
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, "Take " + Nasty + DmxRecording.Extension);
        new DmxRecording("Take " + Nasty, new[] { new DmxRecordedFrame(0, null, Array.Empty<DmxUniverseChange>()) }).Save(file);
        tools.External.Bind(engine, app);
        Assert.True(tools.External.LoadRecording(file), tools.External.Message);
        var external = new ExternalControlPanel(stage) { ShowTab = 3 };
        string recorder = Capture(ui, () => Window("external", new Vector2(1200, 700), external.Draw));
        Assert.Contains("Take " + Nasty, recorder, StringComparison.Ordinal);
        Assert.Contains(file, recorder, StringComparison.Ordinal);

        // The file browser in a folder whose files and subfolders have such names (and one with "##").
        File.WriteAllText(Path.Combine(folder, "Stage " + Nasty + ".mvr"), "");
        File.WriteAllText(Path.Combine(folder, "a##b.mvr"), "");
        Directory.CreateDirectory(Path.Combine(folder, "Folder " + Nasty));
        var browser = new FileBrowserDialog(dir.File("recent-places.json"));
        browser.Show(FileBrowserMode.Open, "Open " + Nasty, new[] { new FileFilter("MVR", "*.mvr") }, folder, null, _ => { });
        Capture(ui, () => browser.Draw(1f));
        string listing = Capture(ui, () => browser.Draw(1f));
        Assert.Contains("Open " + Nasty, listing, StringComparison.Ordinal);
        Assert.Contains("Stage " + Nasty + ".mvr", listing, StringComparison.Ordinal);
        Assert.Contains("Folder " + Nasty, listing, StringComparison.Ordinal);
        Assert.Contains("a# #b.mvr", listing, StringComparison.Ordinal);
        browser.Cancel();

        // The helpers themselves, in Russian too.
        Loc.SetLanguage("ru");
        string helpers = Capture(ui, () => Window("helpers", new Vector2(600, 400), () =>
        {
            SafeText.Disabled("disabled " + Nasty);
            SafeText.Colored(Theme.Error, "colored " + Nasty);
            SafeText.Wrapped("wrapped " + Nasty);
            SafeText.Bullet("bullet " + Nasty);
            ImGui.Selectable(SafeText.Label("label ##" + Nasty));
        }));
        foreach (string expected in new[] { "disabled ", "colored ", "wrapped ", "bullet " })
            Assert.Contains(expected + Nasty, helpers, StringComparison.Ordinal);
        Assert.Contains("label # #" + Nasty, helpers, StringComparison.Ordinal);
    }

    private static void Window(string name, Vector2 size, Action draw)
    {
        ImGui.SetNextWindowPos(Vector2.Zero);
        ImGui.SetNextWindowSize(size);
        ImGui.Begin(name);
        draw();
        ImGui.End();
    }

    /// <summary>
    /// Runs a frame with ImGui's text log on (every text ImGui renders is copied into it) and returns what was rendered,
    /// read back through the context's clipboard. A frame is drawn first so windows have their size and contents.
    /// </summary>
    internal static string Capture(HeadlessImGui ui, Action draw)
    {
        ui.Frame(draw);
        ui.Frame(() =>
        {
            ImGui.LogToClipboard();
            draw();
            ImGui.LogFinish();
        });
        return ImGui.GetClipboardTextS() ?? "";
    }
}
