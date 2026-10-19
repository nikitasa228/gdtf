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

    [GeneratedRegex(@"\bImGuiP?\s*\.\s*(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\(")]
    private static partial Regex ImGuiCall();

    [GeneratedRegex(@"\.\s*appendfv?\s*\(")]
    private static partial Regex TextBufferFormat();

    /// <summary>
    /// The Dear ImGui functions of the binding whose text is a printf format (a parameter named <c>fmt</c>), read from the
    /// binding itself so a function added by an update is covered too, with the number of arguments from which a call
    /// reaches a format overload (TreeNode and TreeNodeEx also have overloads with a plain label).
    /// </summary>
    internal static Dictionary<string, int> FormatFunctions()
    {
        var formats = new Dictionary<string, int>(StringComparer.Ordinal);
        var plain = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var type in new[] { typeof(ImGui), typeof(ImGuiP) })
        {
            foreach (var method in type.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
            {
                var parameters = method.GetParameters();
                int fmt = Array.FindIndex(parameters, p => p.Name == "fmt");
                if (fmt >= 0)
                    formats[method.Name] = Math.Min(formats.GetValueOrDefault(method.Name, int.MaxValue), fmt + 1);
                else
                    plain[method.Name] = Math.Max(plain.GetValueOrDefault(method.Name), parameters.Length);
            }
        }

        foreach (var (name, arguments) in formats.ToList())
        {
            // A plain overload that takes as many arguments as a format overload could not be told apart by counting.
            if (plain.TryGetValue(name, out int most))
                Assert.True(most < arguments, $"ImGui.{name} has plain and format overloads of the same length; extend the check");
            else
                formats[name] = 0;
        }

        return formats;
    }

    /// <summary>Top-level arguments of the call whose '(' is at <paramref name="open"/> (string and char literals skipped).</summary>
    internal static int CountArguments(string source, int open)
    {
        int depth = 0, commas = 0;
        bool any = false;
        for (int i = open; i < source.Length; i++)
        {
            char c = source[i];
            if (c is '"' or '\'' || (c is '$' or '@' && i + 1 < source.Length && source[i + 1] is '"' or '$' or '@'))
            {
                i = SkipLiteral(source, i) - 1;
                any = true;
                continue;
            }

            if (c is '(' or '[' or '{')
            {
                depth++;
                continue;
            }

            if (c is ')' or ']' or '}')
            {
                if (--depth == 0)
                    return any ? commas + 1 : 0;
                continue;
            }

            if (c == ',' && depth == 1)
                commas++;
            else if (!char.IsWhiteSpace(c) && depth >= 1)
                any = true;
        }

        return commas + 1;
    }

    /// <summary>The index after the string or char literal starting at <paramref name="i"/> (regular, verbatim, interpolated).</summary>
    private static int SkipLiteral(string source, int i)
    {
        bool verbatim = false, interpolated = false;
        while (i < source.Length && source[i] is '$' or '@')
        {
            verbatim |= source[i] == '@';
            interpolated |= source[i] == '$';
            i++;
        }

        char quote = source[i];
        i++;
        while (i < source.Length)
        {
            char c = source[i];
            if (!verbatim && c == '\\')
            {
                i += 2;
                continue;
            }

            if (c == quote)
            {
                if (verbatim && i + 1 < source.Length && source[i + 1] == quote)
                {
                    i += 2;
                    continue;
                }

                return i + 1;
            }

            if (interpolated && c == '{')
            {
                if (i + 1 < source.Length && source[i + 1] == '{')
                {
                    i += 2;
                    continue;
                }

                // An interpolation hole: an expression that may hold literals of its own, up to its closing brace.
                int depth = 1;
                i++;
                while (i < source.Length && depth > 0)
                {
                    char h = source[i];
                    if (h is '"' or '\'' || (h is '$' or '@' && i + 1 < source.Length && source[i + 1] is '"' or '$' or '@'))
                    {
                        i = SkipLiteral(source, i);
                        continue;
                    }

                    if (h == '{')
                        depth++;
                    else if (h == '}')
                        depth--;
                    i++;
                }

                continue;
            }

            i++;
        }

        return i;
    }

    [Fact]
    public void No_source_file_calls_an_imgui_function_that_reads_its_text_as_a_printf_format()
    {
        var formats = FormatFunctions();
        foreach (string name in new[] { "Text", "TextColored", "TextDisabled", "TextWrapped", "LabelText", "BulletText", "SetTooltip", "SetItemTooltip", "TextAligned", "LogText" })
            Assert.True(formats.ContainsKey(name), $"ImGui.{name} was not recognised as a format function");
        Assert.Equal(2, formats["TreeNode"]);
        Assert.Equal(3, formats["TreeNodeEx"]);

        string sources = Path.Combine(EngineTestSupport.Root, "src", "Candela.App");
        var offenders = new List<string>();
        int files = 0;
        foreach (string file in Directory.EnumerateFiles(sources, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                continue;
            files++;
            string text = File.ReadAllText(file);
            string[] lines = text.Split('\n');
            var lineStarts = new List<int> { 0 };
            for (int i = 0; i < text.Length; i++)
                if (text[i] == '\n')
                    lineStarts.Add(i + 1);
            foreach (Match match in ImGuiCall().Matches(text))
            {
                if (!formats.TryGetValue(match.Groups["name"].Value, out int fromArguments))
                    continue;
                int line = lineStarts.BinarySearch(match.Index);
                line = line >= 0 ? line : ~line - 1;
                string code = lines[line].Trim();
                if (code.StartsWith("//", StringComparison.Ordinal) || code.StartsWith("///", StringComparison.Ordinal))
                    continue;
                if (fromArguments == 0 || CountArguments(text, match.Index + match.Length - 1) >= fromArguments)
                    offenders.Add($"{Path.GetRelativePath(sources, file)}:{line + 1}: {code}");
            }

            foreach (Match match in TextBufferFormat().Matches(text))
            {
                int line = lineStarts.BinarySearch(match.Index);
                line = line >= 0 ? line : ~line - 1;
                offenders.Add($"{Path.GetRelativePath(sources, file)}:{line + 1}: {lines[line].Trim()}");
            }
        }

        Assert.True(files > 50, $"only {files} source files found under {sources}");
        Assert.True(offenders.Count == 0, "printf-style ImGui text calls (use SafeText or TextUnformatted):\n" + string.Join("\n", offenders));
    }

    [Theory]
    [InlineData("ImGui.TreeNode(\"label\")", 1)]
    [InlineData("ImGui.TreeNode(\"id\", name)", 2)]
    [InlineData("ImGui.TreeNodeEx(Loc.Get(a, b), ImGuiTreeNodeFlags.None)", 2)]
    [InlineData("ImGui.TreeNodeEx($\"{Icons.Box} {Format(a, \"x,y\")}##id\", flags, name)", 3)]
    [InlineData("ImGui.Text(@\"a \"\", b\")", 1)]
    [InlineData("ImGui.TextAligned(0.5f, -1f, label)", 3)]
    [InlineData("ImGui.NewLine()", 0)]
    public void Call_arguments_are_counted_past_strings_and_nested_calls(string call, int expected) =>
        Assert.Equal(expected, CountArguments(call, call.IndexOf('(')));

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
