using System.Numerics;
using System.Runtime.InteropServices;
using Candela.App.Docking;
using Hexa.NET.ImGui;

namespace Candela.App.Tests;

public sealed class TabStyleMapTests
{
    [Fact]
    public void Serialize_and_parse_round_trip()
    {
        var map = new TabStyleMap();
        map.SetStyle(0x1234ABCD, TabStyle.LeftIcons);
        map.SetSelected(0x1234ABCD, "Properties");
        map.SetStyle(7, TabStyle.LeftIcons);
        map.SetSelected(99, "Console"); // top tabs with a remembered selection
        string text = map.Serialize();
        var parsed = TabStyleMap.Parse(text, out int skipped);
        Assert.Equal(0, skipped);
        Assert.Equal(3, parsed.Count);
        Assert.Equal(new TabStyleState(TabStyle.LeftIcons, "Properties"), parsed.Get(0x1234ABCD));
        Assert.Equal(new TabStyleState(TabStyle.LeftIcons), parsed.Get(7));
        Assert.Equal(new TabStyleState(TabStyle.TopTabs, "Console"), parsed.Get(99));
        Assert.Equal(text, parsed.Serialize());
    }

    [Fact]
    public void Unknown_nodes_use_top_tabs()
    {
        var map = new TabStyleMap();
        Assert.Equal(TabStyle.TopTabs, map.StyleOf(42));
        Assert.Equal(new TabStyleState(TabStyle.TopTabs), map.Get(42));
    }

    [Fact]
    public void Damaged_lines_are_skipped()
    {
        string text = "# comment\n0x00000010 LeftIcons Library\nnot-a-line\n0xZZ TopTabs\n0x00000011 Sideways\n0 LeftIcons\n\n17 lefticons";
        var map = TabStyleMap.Parse(text, out int skipped);
        Assert.Equal(4, skipped);
        Assert.Equal(TabStyle.LeftIcons, map.StyleOf(0x10));
        Assert.Equal("Library", map.Get(0x10).SelectedPanel);
        Assert.Equal(TabStyle.LeftIcons, map.StyleOf(17)); // decimal ids and any letter case
        Assert.Equal(0, TabStyleMap.Parse(null, out skipped).Count);
        Assert.Equal(0, skipped);
    }

    [Fact]
    public void Prune_and_transfer()
    {
        var map = new TabStyleMap();
        map.SetStyle(1, TabStyle.LeftIcons);
        map.SetStyle(2, TabStyle.LeftIcons);
        map.SetSelected(2, "Render");
        int changes = 0;
        map.Changed += () => changes++;
        map.Prune(id => id == 2);
        Assert.Equal(1, map.Count);
        map.Transfer(2, 5);
        Assert.Equal(new TabStyleState(TabStyle.LeftIcons, "Render"), map.Get(5));
        Assert.Equal(TabStyle.LeftIcons, map.StyleOf(2)); // copied, the source stays until pruned
        Assert.Equal(2, changes);
        map.Transfer(3, 6); // unknown source: nothing happens
        Assert.Equal(TabStyle.TopTabs, map.StyleOf(6));
        Assert.Equal(2, changes);
    }
}

public sealed class LayoutFileTests
{
    private static LayoutFile Sample() => new()
    {
        Name = "Studio",
        Panels = new List<string> { "Viewport1", "Library", "Properties" },
        Viewports = new List<ViewportLayout>
        {
            new() { Id = "Viewport1", View = "Top", Projection = "Orthographic", Shading = "Colorless", Overlay = "Lux", Grid = false, Stats = false },
        },
        TabStyles = "0x00000010 LeftIcons Library",
        ImGui = "[Window][###Library]\nPos=0,0\n",
    };

    [Fact]
    public void Json_round_trip()
    {
        var file = Sample();
        var back = LayoutFile.FromJson(file.ToJson());
        Assert.Equal(file.Name, back.Name);
        Assert.Equal(file.Panels, back.Panels);
        Assert.Equal(file.TabStyles, back.TabStyles);
        Assert.Equal(file.ImGui, back.ImGui);
        var v = Assert.Single(back.Viewports);
        Assert.Equal(("Top", "Orthographic", "Colorless", "Lux", false, false), (v.View, v.Projection, v.Shading, v.Overlay, v.Grid, v.Stats));
    }

    [Fact]
    public void Damaged_and_newer_files_are_rejected_with_a_clear_error()
    {
        Assert.Throws<LayoutFormatException>(() => LayoutFile.FromJson("{ \"format\": "));
        Assert.Throws<LayoutFormatException>(() => LayoutFile.FromJson("null"));
        var e = Assert.Throws<LayoutFormatException>(() => LayoutFile.FromJson("{ \"format\": 99 }"));
        Assert.Contains("newer", e.Message);
    }

    [Fact]
    public void Invalid_entries_are_dropped()
    {
        string json = """
            { "format": 1, "name": "x",
              "panels": ["Library", "", "Library", "Issues"],
              "viewports": [ { "id": "Viewport2" }, { "id": "Viewport99" }, { "id": "Console" }, { "id": "Viewport2", "view": "Top" } ],
              "tabStyles": null, "imgui": null }
            """;
        var file = LayoutFile.FromJson(json);
        Assert.Equal(new[] { "Library", "Issues" }, file.Panels);
        var v = Assert.Single(file.Viewports);
        Assert.Equal("Viewport2", v.Id);
        Assert.Equal("Perspective", v.View); // the first entry wins
        Assert.Equal("", file.TabStyles);
        Assert.Equal("", file.ImGui);
    }

    [Theory]
    [InlineData("Viewport1", true)]
    [InlineData("Viewport16", true)]
    [InlineData("Viewport17", false)]
    [InlineData("Viewport0", false)]
    [InlineData("viewport1", false)]
    [InlineData("Library", false)]
    [InlineData(null, false)]
    public void Viewport_ids(string? id, bool expected) => Assert.Equal(expected, LayoutFile.IsViewportId(id));
}

public sealed class LayoutStoreTests
{
    [Fact]
    public void Save_list_load_delete()
    {
        using var dir = new TempDir();
        var store = new LayoutStore(dir.File("layouts"));
        Assert.Empty(store.List());
        store.Save("Lighting desk", new LayoutFile { Panels = { "Library" } });
        store.Save("a/b: c?", new LayoutFile());
        store.Save(LayoutStore.WorkspaceName, new LayoutFile { Name = "workspace" });
        Assert.Equal(new[] { "a/b: c?", "Lighting desk" }, store.List());
        Assert.True(store.Exists("Lighting desk"));
        var loaded = store.Load("Lighting desk")!;
        Assert.Equal("Lighting desk", loaded.Name);
        Assert.Equal(new[] { "Library" }, loaded.Panels);
        Assert.Equal("workspace", store.Load(LayoutStore.WorkspaceName)!.Name);
        Assert.Null(store.Load("missing"));
        store.Delete("a/b: c?");
        store.Delete("missing");
        Assert.Equal(new[] { "Lighting desk" }, store.List());
        Assert.Empty(Directory.GetFiles(store.Folder, "*.tmp"));
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("Сцена 1")]
    [InlineData("..")]
    [InlineData("a%b")]
    [InlineData("con:*?<>|\"\\/")]
    public void Names_escape_to_safe_file_names_and_back(string name)
    {
        string escaped = LayoutStore.Escape(name);
        Assert.Equal(-1, escaped.IndexOfAny(Path.GetInvalidFileNameChars()));
        Assert.DoesNotContain('/', escaped);
        Assert.NotEqual("..", escaped);
        Assert.Equal(name, LayoutStore.Unescape(escaped));
    }

    [Fact]
    public void Damaged_files_raise_a_layout_error()
    {
        using var dir = new TempDir();
        var store = new LayoutStore(dir.Path);
        File.WriteAllText(store.PathOf("broken"), "{{{");
        Assert.Throws<LayoutFormatException>(() => store.Load("broken"));
        Assert.Throws<ArgumentException>(() => store.Save("  ", new LayoutFile()));
    }
}

/// <summary>Docking on a real (window-less) Dear ImGui context: default layout, tab styles, ini round trip, undocking.</summary>
[Collection(UiStateCollection.Name)]
public sealed unsafe class DockingTests
{
    private const ImGuiDockNodeFlags NoTabBar = (ImGuiDockNodeFlags)(1 << 12);
    private static readonly Vector2 Display = new(1600, 900);

    private sealed class TestPanel(string id) : Panel(id, "")
    {
        public int Draws { get; private set; }

        public override string Title => Id;

        public override void Draw() => Draws++;
    }

    private sealed class Rig
    {
        public Rig()
        {
            foreach (var id in new[] { "Library", "Viewport1", "Properties", "Preview", "Render", "Issues", "Console" })
                Panels.Add(new TestPanel(id));
        }

        public DockWorkspace Dock { get; } = new();

        public List<Panel> Panels { get; } = new();

        public Panel this[string id] => Panels.First(p => p.Id == id);

        public LayoutPlan Plan()
        {
            var plan = new LayoutPlan();
            plan.Left.Add(this["Library"].DockName);
            plan.Center.Add(this["Viewport1"].DockName);
            plan.Right.AddRange(new[] { "Properties", "Preview", "Render" }.Select(n => this[n].DockName));
            plan.Bottom.AddRange(new[] { "Issues", "Console" }.Select(n => this[n].DockName));
            return plan;
        }

        public void Frames(HeadlessImGui ui, int count)
        {
            for (int i = 0; i < count; i++)
            {
                ui.Frame(() =>
                {
                    Dock.ApplyTabStyles();
                    Dock.DrawDockSpace();
                    foreach (var p in Panels.Where(p => p.IsOpen))
                        Dock.DrawPanel(p, Panels, 1f, panel => panel.Draw());
                });
            }
        }
    }

    private static ImGuiWindowPtr Window(Panel panel) => ImGuiP.FindWindowByID(DockWorkspace.WindowIdOf(panel));

    private static ImGuiDockNodePtr NodeOf(Panel panel) => Window(panel).DockNode;

    private static string SaveIni()
    {
        nuint size = 0;
        byte* ini = ImGui.SaveIniSettingsToMemory(&size);
        return ini == null ? "" : Marshal.PtrToStringUTF8((nint)ini, (int)size) ?? "";
    }

    [Fact]
    public void Default_layout_places_the_panels_in_four_groups()
    {
        using var ui = new HeadlessImGui(Display);
        var rig = new Rig();
        rig.Dock.RequestPreset(LayoutPreset.Default, rig.Plan());
        rig.Frames(ui, 3);

        var library = NodeOf(rig["Library"]);
        var viewport = NodeOf(rig["Viewport1"]);
        var right = NodeOf(rig["Properties"]);
        var bottom = NodeOf(rig["Issues"]);
        Assert.False(library.IsNull || viewport.IsNull || right.IsNull || bottom.IsNull);
        Assert.Equal(4, new[] { library.ID, viewport.ID, right.ID, bottom.ID }.Distinct().Count());
        Assert.Equal(right.ID, NodeOf(rig["Render"]).ID);
        Assert.Equal(bottom.ID, NodeOf(rig["Console"]).ID);

        // Library left, Properties right, Issues below the viewport.
        Assert.True(library.Pos.X < viewport.Pos.X && viewport.Pos.X < right.Pos.X);
        Assert.True(bottom.Pos.Y > viewport.Pos.Y);
        Assert.InRange(library.Size.X / Display.X, 0.15f, 0.21f);
        Assert.True(viewport.Size.X > library.Size.X && viewport.Size.Y > bottom.Size.Y);
        Assert.True(((TestPanel)rig["Viewport1"]).Draws > 0);
    }

    [Fact]
    public void Four_viewport_preset_tiles_the_centre()
    {
        using var ui = new HeadlessImGui(Display);
        var rig = new Rig();
        for (int i = 2; i <= 4; i++)
            rig.Panels.Add(new TestPanel("Viewport" + i));
        var plan = rig.Plan();
        for (int i = 2; i <= 4; i++)
            plan.Center.Add(rig["Viewport" + i].DockName);
        rig.Dock.RequestPreset(LayoutPreset.FourViewports, plan);
        rig.Frames(ui, 3);
        var nodes = Enumerable.Range(1, 4).Select(i => NodeOf(rig["Viewport" + i])).ToList();
        Assert.Equal(4, nodes.Select(n => n.ID).Distinct().Count());
        Assert.Equal(nodes[0].Pos.Y, nodes[1].Pos.Y, 1);
        Assert.Equal(nodes[0].Pos.X, nodes[2].Pos.X, 1);
        Assert.True(nodes[3].Pos.X > nodes[2].Pos.X && nodes[3].Pos.Y > nodes[1].Pos.Y);
    }

    [Fact]
    public void Icon_column_style_hides_the_tab_bar_and_shows_the_selected_panel()
    {
        using var ui = new HeadlessImGui(Display);
        var rig = new Rig();
        rig.Dock.RequestPreset(LayoutPreset.Default, rig.Plan());
        rig.Frames(ui, 3);
        uint right = NodeOf(rig["Properties"]).ID;

        rig.Dock.TabStyles.SetStyle(right, TabStyle.LeftIcons);
        rig.Dock.TabStyles.SetSelected(right, "Render");
        rig.Frames(ui, 3);
        var node = ImGuiP.DockBuilderGetNode(right);
        Assert.True((node.LocalFlags & NoTabBar) != 0);
        Assert.Equal(Window(rig["Render"]).ID, node.VisibleWindow.ID);
        // Other groups keep their tabs.
        Assert.True((NodeOf(rig["Issues"]).LocalFlags & NoTabBar) == 0);

        rig.Dock.TabStyles.SetStyle(right, TabStyle.TopTabs);
        rig.Frames(ui, 3);
        node = ImGuiP.DockBuilderGetNode(right);
        Assert.True((node.LocalFlags & NoTabBar) == 0);
        Assert.Equal(Window(rig["Render"]).ID, node.VisibleWindow.ID); // the selection carries over to the tabs
    }

    [Fact]
    public void Layout_and_tab_styles_survive_a_restart()
    {
        using var dir = new TempDir();
        var store = new LayoutStore(dir.Path);
        uint right, bottom;
        Vector2 librarySize, bottomSize;
        Vector2 floatingPos;
        using (var ui = new HeadlessImGui(Display))
        {
            var rig = new Rig();
            rig.Dock.RequestPreset(LayoutPreset.Default, rig.Plan());
            rig.Frames(ui, 3);
            right = NodeOf(rig["Properties"]).ID;
            bottom = NodeOf(rig["Issues"]).ID;
            rig.Dock.TabStyles.SetStyle(right, TabStyle.LeftIcons);
            rig.Dock.TabStyles.SetSelected(right, "Preview");
            rig.Dock.TabStyles.SetSelected(bottom, "Console");
            DockWorkspace.Undock(rig["Render"]);
            rig.Frames(ui, 2);
            ImGuiP.SetWindowPos(Window(rig["Render"]), new Vector2(700, 250), ImGuiCond.Always);
            rig.Frames(ui, 2);
            Assert.True(Window(rig["Render"]).DockNode.IsNull);
            librarySize = NodeOf(rig["Library"]).Size;
            bottomSize = NodeOf(rig["Issues"]).Size;
            floatingPos = Window(rig["Render"]).Pos;
            store.Save("Mine", new LayoutFile
            {
                Panels = rig.Panels.Select(p => p.Id).ToList(),
                TabStyles = rig.Dock.SerializeTabStyles(),
                ImGui = SaveIni(),
            });
        }

        var loaded = store.Load("Mine")!;
        using (var ui = new HeadlessImGui(Display))
        {
            var rig = new Rig();
            ImGui.LoadIniSettingsFromMemory(loaded.ImGui);
            rig.Dock.ReplaceTabStyles(TabStyleMap.Parse(loaded.TabStyles, out int skipped));
            Assert.Equal(0, skipped);
            rig.Frames(ui, 3);

            Assert.Equal(right, NodeOf(rig["Properties"]).ID);
            Assert.Equal(right, NodeOf(rig["Preview"]).ID);
            var node = ImGuiP.DockBuilderGetNode(right);
            Assert.True((node.LocalFlags & NoTabBar) != 0);
            Assert.Equal(Window(rig["Preview"]).ID, node.VisibleWindow.ID);
            Assert.Equal(Window(rig["Console"]).ID, ImGuiP.DockBuilderGetNode(bottom).VisibleWindow.ID);
            Assert.True(Window(rig["Render"]).DockNode.IsNull, "the undocked panel stays floating");
            Assert.Equal(floatingPos, Window(rig["Render"]).Pos);
            Assert.Equal(librarySize, NodeOf(rig["Library"]).Size);
            Assert.Equal(bottomSize, NodeOf(rig["Issues"]).Size);
        }
    }

    [Fact]
    public void A_saved_layout_can_be_loaded_while_the_editor_runs()
    {
        using var ui = new HeadlessImGui(Display);
        var rig = new Rig();
        rig.Dock.RequestPreset(LayoutPreset.Default, rig.Plan());
        rig.Frames(ui, 3);
        string defaultIni = SaveIni();
        uint right = NodeOf(rig["Properties"]).ID;
        var librarySize = NodeOf(rig["Library"]).Size;

        // Rearrange: Render floats, Console moves into the right group.
        DockWorkspace.Undock(rig["Render"]);
        rig.Frames(ui, 2);
        ImGuiP.DockBuilderDockWindow(rig["Console"].DockName, right);
        rig.Frames(ui, 2);
        Assert.True(Window(rig["Render"]).DockNode.IsNull);
        Assert.Equal(right, NodeOf(rig["Console"]).ID);

        // Load the saved arrangement between frames, as the workspace does.
        ImGui.LoadIniSettingsFromMemory(defaultIni);
        rig.Frames(ui, 3);
        Assert.Equal(right, NodeOf(rig["Render"]).ID);
        Assert.Equal(NodeOf(rig["Issues"]).ID, NodeOf(rig["Console"]).ID);
        Assert.Equal(librarySize, NodeOf(rig["Library"]).Size);
    }

    [Fact]
    public void Serialising_prunes_styles_of_missing_nodes()
    {
        using var ui = new HeadlessImGui(Display);
        var rig = new Rig();
        rig.Dock.RequestPreset(LayoutPreset.Default, rig.Plan());
        rig.Frames(ui, 3);
        rig.Dock.TabStyles.SetStyle(0xDEADBEEF, TabStyle.LeftIcons); // a node that does not exist
        rig.Dock.TabStyles.SetStyle(NodeOf(rig["Library"]).ID, TabStyle.LeftIcons);
        string text = rig.Dock.SerializeTabStyles();
        Assert.DoesNotContain("DEADBEEF", text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, rig.Dock.TabStyles.Count);
    }
}
