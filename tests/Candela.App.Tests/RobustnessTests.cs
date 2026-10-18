using System.Numerics;
using Candela.App.Shell;
using Hexa.NET.ImGui;

namespace Candela.App.Tests;

/// <summary>Failures inside a UI frame must never leave Dear ImGui in a state that aborts the process.</summary>
[Collection(UiStateCollection.Name)]
public sealed class UiFrameTests
{
    [Fact]
    public void An_exception_in_the_ui_ends_the_frame_and_the_next_frame_runs()
    {
        using var ui = new HeadlessImGui(new Vector2(800, 600));
        // The default (assert on recovery) is kept on purpose: recovery must not assert either.
        Assert.True(ImGui.GetIO().ConfigErrorRecoveryEnableAssert);
        int frameBefore = ImGui.GetFrameCount();
        ImGui.GetIO().DeltaTime = 1f / 60f;
        ImGui.NewFrame();
        var e = Assert.Throws<InvalidOperationException>(() => UiFrame.BuildAndRender(() =>
        {
            // A failure deep inside nested UI: a window, a child, a group, pushed style, colour and ID, a menu bar.
            ImGui.Begin("outer", ImGuiWindowFlags.MenuBar);
            ImGui.BeginMenuBar();
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(1, 1));
            ImGui.PushStyleColor(ImGuiCol.Text, 0xFF00FF00u);
            ImGui.PushID("id");
            ImGui.BeginChild("child", new Vector2(100, 100));
            ImGui.BeginGroup();
            throw new InvalidOperationException("boom");
        }));
        Assert.Equal("boom", e.Message);
        Assert.True(ImGui.GetIO().ConfigErrorRecoveryEnableAssert, "the setting is restored after recovery");

        // Without the guard the next NewFrame would abort the test host ("Forgot to call Render()").
        bool drawn = false;
        ui.Frame(() =>
        {
            ImGui.Begin("next");
            ImGui.Text("still alive");
            ImGui.End();
            drawn = true;
        });
        Assert.True(drawn);
        Assert.Equal(frameBefore + 2, ImGui.GetFrameCount());
    }

    [Fact]
    public void A_normal_frame_is_rendered_once()
    {
        using var ui = new HeadlessImGui(new Vector2(800, 600));
        ImGui.GetIO().DeltaTime = 1f / 60f;
        ImGui.NewFrame();
        int calls = 0;
        UiFrame.BuildAndRender(() => calls++);
        Assert.Equal(1, calls);
        Assert.False(ImGui.GetDrawData().IsNull);
        ui.Frame(() => { });
    }
}

/// <summary>
/// Damaged dock trees in layout files are rejected before Dear ImGui sees them (it aborts the process on them); what
/// Dear ImGui itself writes is always accepted.
/// </summary>
[Collection(UiStateCollection.Name)]
public sealed unsafe class DockIniValidatorTests
{
    private static readonly Vector2 Display = new(1600, 900);

    private sealed class TestPanel(string id) : Candela.App.Docking.Panel(id, "")
    {
        public override string Title => Id;

        public override void Draw()
        {
        }
    }

    /// <summary>Dear ImGui's settings text after building a preset (optionally rearranged) on a window-less context.</summary>
    private static string IniOf(Candela.App.Docking.LayoutPreset preset, Action<List<Candela.App.Docking.Panel>, Candela.App.Docking.DockWorkspace>? rearrange = null)
    {
        using var ui = new HeadlessImGui(Display);
        var ids = new List<string> { "Library", "Viewport1", "Properties", "Preview", "Render", "Issues", "Console" };
        if (preset == Candela.App.Docking.LayoutPreset.FourViewports)
            ids.AddRange(new[] { "Viewport2", "Viewport3", "Viewport4" });
        var panels = ids.Select(i => (Candela.App.Docking.Panel)new TestPanel(i)).ToList();
        var dock = new Candela.App.Docking.DockWorkspace();
        var plan = new Candela.App.Docking.LayoutPlan();
        plan.Left.Add(panels[0].DockName);
        plan.Center.AddRange(panels.Where(p => p.Id.StartsWith("Viewport", StringComparison.Ordinal)).Select(p => p.DockName));
        plan.Right.AddRange(panels.Skip(2).Take(3).Select(p => p.DockName));
        plan.Bottom.AddRange(panels.Skip(5).Take(2).Select(p => p.DockName));
        dock.RequestPreset(preset, plan);
        void Frames(int n)
        {
            for (int i = 0; i < n; i++)
            {
                ui.Frame(() =>
                {
                    dock.ApplyTabStyles();
                    dock.DrawDockSpace();
                    foreach (var p in panels.Where(p => p.IsOpen))
                        dock.DrawPanel(p, panels, 1f, _ => { });
                });
            }
        }

        Frames(3);
        if (rearrange is not null)
        {
            rearrange(panels, dock);
            Frames(3);
        }

        nuint size = 0;
        byte* ini = ImGui.SaveIniSettingsToMemory(&size);
        return System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)ini, (int)size) ?? "";
    }

    private static string Replace(string ini, string find, string replacement)
    {
        Assert.Contains(find, ini);
        return ini.Replace(find, replacement, StringComparison.Ordinal);
    }

    private static List<string> DockLines(string ini) =>
        ini.Split('\n').Where(l => l.TrimStart().StartsWith("Dock", StringComparison.Ordinal)).ToList();

    [Fact]
    public void What_dear_imgui_writes_is_accepted()
    {
        var layouts = new[]
        {
            IniOf(Candela.App.Docking.LayoutPreset.Default),
            IniOf(Candela.App.Docking.LayoutPreset.FourViewports),
            // A floating group of two panels (a root node without a dock space window) and an icon-column group.
            IniOf(Candela.App.Docking.LayoutPreset.Default, (panels, dock) =>
            {
                uint right = Candela.App.Docking.DockWorkspace.DockIdOf(panels[2]);
                dock.TabStyles.SetStyle(right, Candela.App.Docking.TabStyle.LeftIcons);
                Candela.App.Docking.DockWorkspace.Undock(panels[5]);
            }),
        };
        foreach (var ini in layouts)
        {
            Assert.Contains("[Docking][Data]", ini);
            Assert.True(Candela.App.Docking.DockIniValidator.Validate(ini, out string reason), reason + "\n" + ini);
        }

        Assert.True(Candela.App.Docking.DockIniValidator.Validate("", out _));
        Assert.True(Candela.App.Docking.DockIniValidator.Validate("[Window][A]\nPos=1,2\n", out _));
    }

    public static TheoryData<string> Damage => new()
    {
        "duplicate", "self-parent", "parent-after-child", "zero-id", "zero-parent", "size-overflow", "negative-size", "one-child",
        "no-split", "child-of-leaf", "three-children", "two-central", "central-split", "garbage", "nul", "deep",
    };

    /// <summary>Each of these crashed or hung Dear ImGui (checked by hand in a separate process) before the check existed.</summary>
    [Theory]
    [MemberData(nameof(Damage))]
    public void Damaged_dock_trees_are_rejected(string damage)
    {
        string ini = IniOf(Candela.App.Docking.LayoutPreset.Default);
        var lines = DockLines(ini);
        string firstChild = lines.First(l => l.Contains("Parent=", StringComparison.Ordinal));
        string leaf = lines.First(l => l.Contains("ID=0x00000005", StringComparison.Ordinal));
        string bad = damage switch
        {
            "duplicate" => Replace(ini, firstChild, firstChild + "\n" + firstChild),
            "self-parent" => Replace(ini, "ID=0x00000005 Parent=0x00000003", "ID=0x00000005 Parent=0x00000005"),
            "parent-after-child" => Replace(Replace(ini, leaf + "\n", ""), "[Docking][Data]\n", "[Docking][Data]\n" + leaf + "\n"),
            "zero-id" => Replace(ini, "ID=0x00000001", "ID=0x00000000"),
            "zero-parent" => Replace(ini, "Parent=0x00000003", "Parent=0x00000000"),
            "size-overflow" => Replace(ini, "SizeRef=287,", "SizeRef=99999,"),
            "negative-size" => Replace(ini, "SizeRef=287,", "SizeRef=-5,"),
            "one-child" => Replace(ini, lines.First(l => l.Contains("ID=0x00000006", StringComparison.Ordinal)) + "\n", ""),
            "no-split" => Replace(ini, "ID=0x00000003 Parent=0x00000002 SizeRef=916,900 Split=Y", "ID=0x00000003 Parent=0x00000002 SizeRef=916,900"),
            "child-of-leaf" => ini + "\n[Docking][Data]\n  DockNode ID=0x00000077 Parent=0x00000005 SizeRef=10,10\n",
            "three-children" => ini + "\n[Docking][Data]\n  DockNode ID=0x00000077 Parent=0x00000003 SizeRef=10,10\n",
            "two-central" => Replace(ini, "ID=0x00000006 Parent=0x00000003 SizeRef=916,234", "ID=0x00000006 Parent=0x00000003 SizeRef=916,234 CentralNode=1"),
            "central-split" => Replace(Replace(ini, " CentralNode=1", ""), "SizeRef=916,900 Split=Y", "SizeRef=916,900 Split=Y CentralNode=1"),
            "garbage" => Replace(ini, "[Docking][Data]\n", "[Docking][Data]\nDockNode ID=0xZZ Parent=?? SizeRef=-5,-5\n"),
            "nul" => ini.Replace("[Docking][Data]", "[Docking][Data]\0", StringComparison.Ordinal),
            "deep" => ini + "\n[Docking][Data]\n" + string.Join('\n', Enumerable.Range(0, 70).Select(i => i == 0
                ? "DockNode ID=0x00010000 Pos=0,0 Size=10,10 Split=X"
                : $"DockNode ID=0x{0x10000 + i:X8} Parent=0x{0x10000 + i - 1:X8} SizeRef=1,1 Split=X")),
            _ => throw new ArgumentOutOfRangeException(nameof(damage)),
        };
        Assert.NotEqual(ini, bad);
        Assert.False(Candela.App.Docking.DockIniValidator.Validate(bad, out string reason));
        Assert.False(string.IsNullOrWhiteSpace(reason));

        // Through the layout file: a damaged layout, which the workspace replaces with the default arrangement.
        var file = new Candela.App.Docking.LayoutFile { Name = "x", ImGui = bad };
        var e = Assert.Throws<Candela.App.Docking.LayoutFormatException>(() => Candela.App.Docking.LayoutFile.FromJson(file.ToJson()));
        Assert.Contains("damaged", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_restored_valid_layout_still_loads_into_dear_imgui()
    {
        string ini = IniOf(Candela.App.Docking.LayoutPreset.FourViewports);
        var file = Candela.App.Docking.LayoutFile.FromJson(new Candela.App.Docking.LayoutFile { Name = "four", ImGui = ini }.ToJson());
        using var ui = new HeadlessImGui(Display);
        ImGui.LoadIniSettingsFromMemory(file.ImGui);
        ui.Frame(() => ImGui.DockSpaceOverViewport(Candela.App.Docking.DockWorkspace.DockSpaceId, ImGui.GetMainViewport()));
        Assert.False(ImGuiP.DockBuilderGetNode(Candela.App.Docking.DockWorkspace.DockSpaceId).IsNull);
    }
}
