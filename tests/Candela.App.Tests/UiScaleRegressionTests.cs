using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text;
using Candela.App.Ui;
using Hexa.NET.ImGui;

namespace Candela.App.Tests;

/// <summary>
/// The reported freeze and crash while moving the interface-scale slider: <c>Theme.Apply</c> reset only the sizes it
/// knew and then called <c>ImGuiStyle.ScaleAllSizes</c>, which multiplies the current values, so every other scaled
/// field (<c>WindowBorderHoverPadding</c>, <c>TreeLinesRounding</c>) compounded with each value the dragged slider
/// passed. After some fifty values the hover padding overflowed to a negative integer — windows' resize borders
/// covered the screen first (the freeze), then Dear ImGui's new-frame sanity check aborted the process (the crash).
/// These tests sweep the whole slider range: the style must be the same as one application of the final value, the
/// glyphs of the largest allowed text must fit the atlas without repacking every frame, the slider must commit only
/// when let go, and the real editor must survive the sweep.
/// </summary>
[Collection(UiStateCollection.Name)]
public sealed unsafe class UiScaleRegressionTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    /// <summary>Every field of the current style as text (field by field: the struct's padding bytes are undefined).</summary>
    private static string StyleFields()
    {
        object boxed = *ImGui.GetStyle().Handle;
        var text = new StringBuilder();
        foreach (var field in typeof(ImGuiStyle).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                                                           System.Reflection.BindingFlags.Instance))
            text.Append(field.Name).Append('=').Append(Convert.ToString(field.GetValue(boxed), CultureInfo.InvariantCulture)).Append('\n');
        return text.ToString();
    }

    private static void DrawSomeUi()
    {
        ImGui.SetNextWindowPos(new Vector2(10, 10));
        ImGui.SetNextWindowSize(new Vector2(500, 400));
        if (ImGui.Begin("Scale test"))
        {
            ImGui.TextUnformatted("Interface scale / Масштаб интерфейса");
            float v = 0.5f;
            ImGui.SliderFloat("##v", ref v, 0f, 1f, "%.2f");
            ImGui.Button("Button");
            if (ImGui.TreeNode("Tree"))
                ImGui.TreePop();
        }

        ImGui.End();
    }

    [Fact]
    public void Applying_every_slider_value_in_turn_gives_the_style_of_the_last_value_alone()
    {
        using var ui = new HeadlessImGui(new Vector2(1876, 1070));
        Theme.Apply(1f, 1f, FontSet.DefaultSize);
        string reference100 = StyleFields();
        Assert.Contains("WindowBorderHoverPadding=", reference100);
        float border100 = ImGui.GetStyle().WindowBorderHoverPadding;
        Assert.True(border100 > 0f);

        foreach (float font in new[] { FontSet.DefaultSize, UiScale.MaxFontSize, UiScale.MinFontSize })
        {
            // Up and down the slider in 1 % steps, a frame each, as a drag would apply them.
            for (int pass = 0; pass < 2; pass++)
            {
                for (int i = 0; i <= 200; i++)
                {
                    int percent = pass == 0 ? 100 + i : 300 - i;
                    float applied = Theme.Apply(percent / 100f, 1f, font);
                    string once = StyleFields();
                    // Idempotent: applying the same value again changes nothing (the old code compounded here).
                    Theme.Apply(percent / 100f, 1f, font);
                    Assert.True(once == StyleFields(), $"the style drifted at {percent} % ({font} px)");
                    var style = ImGui.GetStyle();
                    float hover = style.WindowBorderHoverPadding;
                    Assert.True(hover > 0f && hover <= border100 * applied + 1f, $"WindowBorderHoverPadding {hover} at {percent} %");
                    Assert.True(float.IsFinite(style.FontScaleMain) && style.FontScaleMain <= percent / 100f + 1e-4f);
                    Assert.InRange(style.WindowPadding.X, 1f, 8f * UiScale.Max + 1f);
                    ui.Frame(DrawSomeUi);
                }
            }
        }

        Theme.Apply(1f, 1f, FontSet.DefaultSize);
        Assert.Equal(reference100, StyleFields());
    }

    [Fact]
    public void The_applied_scale_keeps_text_readable_and_within_the_glyph_atlas()
    {
        foreach (int atlas in new[] { 4096, 8192 })
        {
            foreach (float dpi in new[] { 0.5f, 1f, 1.25f, 1.5f, 2f, 3f, 4f })
            {
                for (float font = UiScale.MinFontSize; font <= UiScale.MaxFontSize; font += 1f)
                {
                    for (int percent = 100; percent <= 300; percent += 5)
                    {
                        float user = percent / 100f;
                        float applied = UiScale.Applied(user, dpi, font, atlas);
                        Assert.InRange(applied, 0.25f, user + 1e-5f);
                        Assert.True(font * applied <= UiScale.MaxTextPixels + 1e-3f, $"{font} px × {applied} is above the text limit");
                        Assert.True(font * applied * dpi <= atlas / UiScale.AtlasPixelsPerTextPixel + 1e-3f || applied == 0.25f,
                            $"{font} px × {applied} × {dpi} does not fit an atlas of {atlas}");
                    }
                }
            }
        }

        // Ordinary settings are applied as they are.
        Assert.Equal(3f, UiScale.Applied(3f, 1f, 15f, 8192), 4);
        Assert.Equal(2f, UiScale.Applied(2f, 1.5f, 15f, 8192), 4);
        Assert.Equal(1.25f, UiScale.Applied(1.25f, 1f, 15f, 4096), 4);
        // Large settings are lowered: a 28 px font at 300 % is shown at 51 px (8192 atlas), 25.6 px with a 4096 one.
        Assert.Equal(8192 / 160f / 28f, UiScale.Applied(3f, 1f, 28f, 8192), 4);
        Assert.Equal(4096 / 160f / 28f, UiScale.Applied(3f, 1f, 28f, 4096), 4);
        // The atlas side follows the device, within Dear ImGui's default and Vulkan's guaranteed minimum.
        Assert.Equal(8192, UiScale.AtlasSize(16384));
        Assert.Equal(4096, UiScale.AtlasSize(4096));
        Assert.Equal(4096, UiScale.AtlasSize(1024));
        Assert.Equal(8192, UiScale.AtlasSize(0));
        // The settings file, the command line and the slider share the range.
        Assert.Equal(UiScale.Max, UiScale.ClampScale(4f));
        Assert.Equal(UiScale.Max, UiScale.Presets[^1]);
    }

    [Theory]
    [InlineData(4096, 2f)]
    [InlineData(8192, 4f)]
    [InlineData(8192, 1f)]
    public void The_largest_settings_fit_the_atlas_without_repacking_every_frame(int atlasSize, float dpi)
    {
        using var ui = new HeadlessImGui(new Vector2(1876, 1070));
        var io = ImGui.GetIO();
        io.Fonts.TexMaxWidth = atlasSize;
        io.Fonts.TexMaxHeight = atlasSize;
        var fonts = FontSet.Load(TestLog.Create());
        Assert.True(fonts.IsBundled);

        // The worst case UiScale's bound is made for: every glyph of ASCII, Latin-1, Latin Extended-A, Cyrillic and 400
        // icons in one frame, in both fonts at the body size and the heading sizes the panels push (1.1 … 2 ×). Dear
        // ImGui rasterises every character of a drawn line, also the part clipped by the window.
        var text = new StringBuilder();
        for (char c = ' '; c <= '~'; c++)
            text.Append(c);
        for (char c = ' '; c <= 'ſ'; c++)
            text.Append(c);
        for (char c = 'Ѐ'; c <= 'ӿ'; c++)
            text.Append(c);
        for (char c = Icons.RangeFirst; c < Icons.RangeFirst + 400; c++)
            text.Append(c);
        string all = text.ToString();
        float[] multipliers = { 1f, 1.1f, 1.35f, 1.45f, 1.6f, 2f };

        void Draw()
        {
            ImGui.SetNextWindowPos(Vector2.Zero);
            ImGui.SetNextWindowSize(io.DisplaySize);
            ImGui.Begin("Glyphs");
            float size = ImGui.GetStyle().FontSizeBase;
            foreach (float m in multipliers)
            {
                ImGui.PushFont(fonts.Regular, size * m);
                ImGui.TextUnformatted(all);
                ImGui.PopFont();
                ImGui.PushFont(fonts.Heading, size * m);
                ImGui.TextUnformatted(all);
                ImGui.PopFont();
            }

            ImGui.End();
        }

        // The worst case first, then the slider swept down and up again: the bakes of earlier sizes must be discarded,
        // not pile up until the atlas is full.
        var times = new List<double>();
        foreach (int percent in new[] { 300, 250, 200, 150, 100, 150, 200, 250, 300 })
        {
            Theme.Apply(percent / 100f, dpi, UiScale.MaxFontSize);
            for (int frame = 0; frame < 4; frame++)
            {
                var watch = Stopwatch.StartNew();
                ui.Frame(Draw);
                times.Add(watch.Elapsed.TotalMilliseconds);
                var tex = io.Fonts.TexData;
                Assert.True(tex.Width <= atlasSize && tex.Height <= atlasSize, $"atlas {tex.Width} × {tex.Height} above {atlasSize}");
            }
        }

        // Steady frames (the third and fourth at each scale) only look glyphs up: an atlas that overflowed would repack
        // for every missing glyph in every frame.
        var steady = times.Where((_, i) => i % 4 >= 2).ToList();
        Assert.True(steady.Max() < 250, $"steady frames up to {steady.Max():0} ms: the atlas repacks every frame");
    }

    [Fact]
    public void The_scale_slider_commits_only_when_it_is_let_go()
    {
        using var ui = new HeadlessImGui(new Vector2(800, 600));
        float? pending = null;
        float current = 100f;
        int commits = 0;
        var min = Vector2.Zero;
        var max = Vector2.Zero;

        void Draw()
        {
            ImGui.SetNextWindowPos(Vector2.Zero);
            ImGui.SetNextWindowSize(new Vector2(500, 200));
            ImGui.Begin("Settings", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove);
            ImGui.SetNextItemWidth(400);
            if (DeferredSlider.Float("##scale", ref pending, current, 100f, 300f, "%.0f %%", out float committed))
            {
                commits++;
                current = committed;
            }

            min = ImGui.GetItemRectMin();
            max = ImGui.GetItemRectMax();
            ImGui.End();
        }

        ui.Frame(Draw);
        var io = ImGui.GetIO();
        float y = (min.Y + max.Y) * 0.5f;
        io.AddMousePosEvent(min.X + 2, y);
        ui.Frame(Draw);
        io.AddMouseButtonEvent(0, true);
        ui.Frame(Draw);
        for (int step = 1; step <= 20; step++)
        {
            io.AddMousePosEvent(min.X + (max.X - min.X) * step / 20f + 5f, y);
            ui.Frame(Draw);
            Assert.Equal(0, commits);
            Assert.Equal(100f, current);
            Assert.NotNull(pending);
        }

        Assert.True(pending > 290f, $"the dragged value is {pending}");
        io.AddMouseButtonEvent(0, false);
        ui.Frame(Draw);
        Assert.Equal(1, commits);
        Assert.Null(pending);
        Assert.InRange(current, 290f, 300f);

        // A click without a move commits nothing new.
        ui.Frame(Draw);
        Assert.Equal(1, commits);
    }

    [SkippableFact]
    public void The_editor_survives_the_whole_scale_range_in_both_directions()
    {
        // The real editor at the reporter's window size: the settings window open, the scale set frame after frame over
        // the whole slider range up and down (as the old slider applied it while dragging), with the smallest and the
        // largest font. Before the fix this aborted in Dear ImGui after a few dozen values (exit code 134).
        var actions = new List<string>
        {
            """{ "type": "wait", "frames": 2 }""",
            """{ "type": "menu", "path": "File/Settings" }""",
            """{ "type": "wait", "frames": 2 }""",
        };
        foreach (string font in new[] { "28", "11" })
        {
            actions.Add($$"""{ "type": "setting", "name": "fontSize", "value": "{{font}}" }""");
            for (int percent = 100; percent <= 300; percent += 8)
                actions.Add($$"""{ "type": "setting", "name": "uiScale", "value": "{{(percent / 100.0).ToString(CultureInfo.InvariantCulture)}}" }""");
            for (int percent = 300; percent >= 100; percent -= 8)
                actions.Add($$"""{ "type": "setting", "name": "uiScale", "value": "{{(percent / 100.0).ToString(CultureInfo.InvariantCulture)}}" }""");
        }

        string large = _dir.File("scale-300.png").Replace('\\', '/');
        actions.Add("""{ "type": "setting", "name": "uiScale", "value": "3" }""");
        actions.Add("""{ "type": "wait", "frames": 2 }""");
        actions.Add($$"""{ "type": "screenshot", "path": "{{large}}" }""");
        actions.Add("""{ "type": "setting", "name": "uiScale", "value": "1" }""");
        actions.Add("""{ "type": "wait", "frames": 2 }""");
        string script = _dir.Write("scale.json", "{ \"actions\": [\n" + string.Join(",\n", actions) + "\n] }");
        string end = _dir.File("end.png");
        var run = EditorProcess.Run(_dir, new[] { "--preview-stage", "--width", "1280", "--height", "720", "--screenshot", end, "--script", script });
        Assert.True(run.ExitCode == ExitCodes.Ok, $"exit {run.ExitCode}: {run.Output}");
        Assert.DoesNotContain("Assertion", run.Output);
        Assert.DoesNotContain("ERR [", run.Output);
        var big = EditorProcess.LoadPng(large);
        var normal = EditorProcess.LoadPng(end);
        // The large UI really is larger (the menu bar is taller), and back at 100 % the frame is a normal one.
        Assert.True(EditorProcess.DifferentPixels(big, normal, 0, 0, 1280, 60) > 0.05, "300 % looks like 100 %");
    }
}
