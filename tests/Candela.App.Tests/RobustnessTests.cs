using System.Numerics;
using Candela.App.Diagnostics;
using Candela.App.Docking;
using Candela.App.Editing;
using Candela.App.Files;
using Candela.App.Input;
using Candela.App.Session;
using Candela.App.Settings;
using Candela.App.Shell;
using Candela.App.Ui;
using Candela.App.Viewport;
using Candela.Core.Editing;
using Candela.Core.Math;
using Candela.Core.Presets;
using Candela.Core.SceneGraph;
using Candela.Core.Settings;
using Candela.Rendering;
using Hexa.NET.ImGui;
using SceneModel = Candela.Core.SceneGraph.Scene;

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

    private sealed class TestPanel(string id) : Panel(id, "")
    {
        public override string Title => Id;

        public override void Draw()
        {
        }
    }

    /// <summary>Dear ImGui's settings text after building a preset (optionally rearranged) on a window-less context.</summary>
    private static string IniOf(LayoutPreset preset, Action<List<Panel>, DockWorkspace>? rearrange = null)
    {
        using var ui = new HeadlessImGui(Display);
        var ids = new List<string> { "Library", "Viewport1", "Properties", "Preview", "Render", "Issues", "Console" };
        if (preset == LayoutPreset.FourViewports)
            ids.AddRange(new[] { "Viewport2", "Viewport3", "Viewport4" });
        var panels = ids.Select(i => (Panel)new TestPanel(i)).ToList();
        var dock = new DockWorkspace();
        var plan = new LayoutPlan();
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
            IniOf(LayoutPreset.Default),
            IniOf(LayoutPreset.FourViewports),
            // A floating group of two panels (a root node without a dock space window) and an icon-column group.
            IniOf(LayoutPreset.Default, (panels, dock) =>
            {
                uint right = DockWorkspace.DockIdOf(panels[2]);
                dock.TabStyles.SetStyle(right, TabStyle.LeftIcons);
                DockWorkspace.Undock(panels[5]);
            }),
        };
        foreach (var ini in layouts)
        {
            Assert.Contains("[Docking][Data]", ini);
            Assert.True(DockIniValidator.Validate(ini, out string reason), reason + "\n" + ini);
        }

        Assert.True(DockIniValidator.Validate("", out _));
        Assert.True(DockIniValidator.Validate("[Window][A]\nPos=1,2\n", out _));
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
        string ini = IniOf(LayoutPreset.Default);
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
        Assert.False(DockIniValidator.Validate(bad, out string reason));
        Assert.False(string.IsNullOrWhiteSpace(reason));

        // Through the layout file: a damaged layout, which the workspace replaces with the default arrangement.
        var file = new LayoutFile { Name = "x", ImGui = bad };
        var e = Assert.Throws<LayoutFormatException>(() => LayoutFile.FromJson(file.ToJson()));
        Assert.Contains("damaged", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_restored_valid_layout_still_loads_into_dear_imgui()
    {
        string ini = IniOf(LayoutPreset.FourViewports);
        var file = LayoutFile.FromJson(new LayoutFile { Name = "four", ImGui = ini }.ToJson());
        using var ui = new HeadlessImGui(Display);
        ImGui.LoadIniSettingsFromMemory(file.ImGui);
        ui.Frame(() => ImGui.DockSpaceOverViewport(DockWorkspace.DockSpaceId, ImGui.GetMainViewport()));
        Assert.False(ImGuiP.DockBuilderGetNode(DockWorkspace.DockSpaceId).IsNull);
    }
}

/// <summary>Repeated failures neither flood the issues panel nor fill the disk with log text.</summary>
[Collection(UiStateCollection.Name)]
public sealed class FailureReportingTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly AppLog _log = TestLog.Create();
    private readonly SettingsStore _settings;
    private readonly EditorSession _session;

    public FailureReportingTests()
    {
        Loc.SetLanguage("en");
        _settings = new SettingsStore(_log, _dir.File("settings.json"));
        _settings.Load();
        _session = new EditorSession(_log, _settings);
    }

    public void Dispose()
    {
        _session.Dispose();
        _log.Dispose();
        _dir.Dispose();
    }

    [Fact]
    public void An_error_repeated_every_frame_is_one_issue()
    {
        for (int frame = 0; frame < 1000; frame++)
            _session.ReportProgramError("app.frame-exception", "Unhandled error in frame: boom");
        _session.ReportProgramError("app.frame-exception", "Unhandled error in frame: other");
        Assert.Equal(2, _session.Issues.Current.Count);

        // Distinct messages are capped.
        for (int i = 0; i < 1000; i++)
            _session.ReportProgramError("app.frame-exception", "message " + i);
        Assert.Equal(EditorSession.MaxProgramMessages, _session.Issues.Current.Count);
    }

    [Fact]
    public void Clearing_the_messages_empties_the_issues_panel_and_allows_new_reports()
    {
        _session.ReportProgramError("app.panel-exception", "Panel failed");
        _ = _session.Open(_dir.Write("scene.mvr", "x")); // a "not available" note
        Assert.Equal(2, _session.Issues.Current.Count);

        _session.ClearProgramMessages();
        Assert.Empty(_session.Issues.Current); // the list the panel shows, not only the internal one

        _session.ReportProgramError("app.panel-exception", "Panel failed");
        Assert.Single(_session.Issues.Current);
    }

    [Fact]
    public void The_same_exception_is_logged_in_full_once_then_counted()
    {
        var filter = new RepeatedErrorFilter();
        var kinds = new List<ErrorLogKind>();
        for (int i = 0; i < 1000; i++)
            kinds.Add(filter.Record(Throw("boom")));
        Assert.Equal(ErrorLogKind.Full, kinds[0]);
        Assert.Equal(1, kinds.Count(k => k == ErrorLogKind.Full));
        // Counted at 10, 100 and 1000 repeats.
        Assert.Equal(new[] { 9, 99, 999 }, kinds.Select((k, i) => (k, i)).Where(t => t.k == ErrorLogKind.Count).Select(t => t.i));
        Assert.Equal(1000, filter.Repeats);

        // A different failure is logged in full again.
        Assert.Equal(ErrorLogKind.Full, filter.Record(Throw("other")));
        Assert.Equal(ErrorLogKind.Full, filter.Record(new ArgumentException("boom")));
    }

    private static InvalidOperationException Throw(string message)
    {
        try
        {
            throw new InvalidOperationException(message);
        }
        catch (InvalidOperationException e)
        {
            return e;
        }
    }

    [Fact]
    public void The_log_file_keeps_the_previous_run_and_stops_at_its_size_limit()
    {
        string path = _dir.File("logs/app.log");
        using (var first = new AppLog { ConsoleLevel = LogLevel.Error + 1 })
        {
            first.OpenFile(path);
            first.Info("Test", "first run");
        }

        using (var second = new AppLog { ConsoleLevel = LogLevel.Error + 1, MaxFileBytes = 4096 })
        {
            second.OpenFile(path);
            for (int i = 0; i < 1000; i++)
                second.Error("Frame", "repeated failure " + i, new InvalidOperationException("detail"));
            Assert.Equal(1000, second.Count); // the in-memory log keeps everything
        }

        string current = File.ReadAllText(path);
        Assert.DoesNotContain("first run", current);
        Assert.Contains("repeated failure 0", current);
        Assert.DoesNotContain("repeated failure 999", current);
        Assert.Contains("further messages are kept in memory only", current);
        Assert.InRange(new FileInfo(path).Length, 1, 4096 + 512);
        Assert.Contains("first run", File.ReadAllText(AppLog.PreviousPathOf(path)));
        Assert.EndsWith("app.previous.log", AppLog.PreviousPathOf(path), StringComparison.Ordinal);
    }
}

/// <summary>The gizmo drag of a multi-selection around the primary pivot (the logic the viewport runs per drag frame).</summary>
public sealed class GizmoDragTests
{
    private static void Near(Vector3 expected, Vector3 actual, float tolerance = 1e-4f) =>
        Assert.True(Vector3.Distance(expected, actual) <= tolerance, $"expected {expected}, got {actual}");

    private readonly SceneModel _scene = new("t");
    private readonly Selection _selection = new();
    private readonly UndoStack _undo = new();

    private Entity Add(string name, Vector3 position, Entity? parent = null) =>
        _scene.Add(new Entity(name) { Local = new Transform(position) }, parent);

    /// <summary>Drags the gizmo of <paramref name="primary"/> in <paramref name="frames"/> steps to <paramref name="end"/>.</summary>
    private void Drag(GizmoDrag drag, Entity primary, Matrix4x4 end, int frames = 10)
    {
        var start = primary.WorldMatrix;
        drag.Begin(_scene, _selection, primary, _undo);
        Assert.True(Matrix4x4.Decompose(start, out _, out var r0, out var t0));
        Assert.True(Matrix4x4.Decompose(end, out var s1, out var r1, out var t1));
        for (int f = 1; f <= frames; f++)
        {
            float k = f / (float)frames;
            var m = Matrix4x4.CreateScale(s1) * Matrix4x4.CreateFromQuaternion(Quaternion.Slerp(r0, r1, k)) *
                    Matrix4x4.CreateTranslation(Vector3.Lerp(t0, t1, k));
            drag.Update(m, _undo, "Move");
        }

        drag.End(_undo);
    }

    [Fact]
    public void A_child_selected_with_its_parent_moves_once()
    {
        var parent = Add("parent", new Vector3(1, 0, 0));
        var child = Add("child", new Vector3(0, 2, 0), parent);
        _selection.Set(new[] { parent.Id, child.Id }); // the child is the primary (last selected)
        var drag = new GizmoDrag();
        var end = child.WorldMatrix * Matrix4x4.CreateTranslation(3, 0, 0);
        Drag(drag, child, end);

        // The gizmo moved the child by +3 in X; the parent carries it, so both moved exactly +3 (not +6 for the child).
        Near(new Vector3(4, 0, 0), parent.WorldPosition);
        Near(new Vector3(4, 2, 0), child.WorldPosition);
        Near(new Vector3(0, 2, 0), child.Local.Position); // still the same offset from its parent
    }

    [Fact]
    public void A_selected_child_of_a_locked_parent_moves_alone()
    {
        var parent = Add("parent", new Vector3(1, 0, 0));
        parent.Locked = true;
        var child = Add("child", new Vector3(0, 2, 0), parent);
        var other = Add("other", new Vector3(5, 5, 0));
        _selection.Set(new[] { parent.Id, other.Id, child.Id });
        var drag = new GizmoDrag();
        Drag(drag, child, child.WorldMatrix * Matrix4x4.CreateTranslation(0, 0, 2));
        Near(new Vector3(1, 0, 0), parent.WorldPosition);
        Near(new Vector3(1, 2, 2), child.WorldPosition);
        Near(new Vector3(5, 5, 2), other.WorldPosition);
    }

    [Fact]
    public void Entities_on_locked_layers_stay_put()
    {
        var layer = new Layer { Name = "rig", Locked = true };
        _scene.Layers.Add(layer);
        var a = Add("a", Vector3.Zero);
        var b = Add("b", new Vector3(2, 0, 0));
        b.LayerId = layer.Id;
        _selection.Set(new[] { b.Id, a.Id });
        Assert.False(SelectionController.IsEditable(_scene, b));
        Drag(new GizmoDrag(), a, Matrix4x4.CreateTranslation(0, 1, 0));
        Near(new Vector3(0, 1, 0), a.WorldPosition);
        Near(new Vector3(2, 0, 0), b.WorldPosition);
    }

    [Fact]
    public void Rotation_turns_the_selection_around_the_primary_pivot_and_is_one_undo_step()
    {
        var pivot = Add("pivot", new Vector3(1, 1, 0));
        var other = Add("other", new Vector3(3, 1, 0));
        _selection.Set(new[] { other.Id, pivot.Id });
        var end = Matrix4x4.CreateRotationZ(MathF.PI / 2) * Matrix4x4.CreateTranslation(1, 1, 0);
        Drag(new GizmoDrag(), pivot, end, frames: 30);

        Near(new Vector3(1, 1, 0), pivot.WorldPosition);
        Near(new Vector3(1, 3, 0), other.WorldPosition); // (2, 0) from the pivot turned by 90° about Z
        Near(Vector3.Transform(Vector3.UnitX, other.Local.Rotation), Vector3.UnitY);

        Assert.Equal("Move", _undo.UndoDescription);
        _undo.Undo();
        Near(new Vector3(3, 1, 0), other.WorldPosition);
        Assert.Equal(Quaternion.Identity, pivot.Local.Rotation);
        Assert.False(_undo.CanUndo); // thirty drag frames, one step
        _undo.Redo();
        Near(new Vector3(1, 3, 0), other.WorldPosition);
    }

    [Fact]
    public void Two_drags_are_two_steps_and_bad_matrices_are_ignored()
    {
        var a = Add("a", Vector3.Zero);
        _selection.Set(a.Id);
        var drag = new GizmoDrag();
        Drag(drag, a, Matrix4x4.CreateTranslation(1, 0, 0));
        Drag(drag, a, Matrix4x4.CreateTranslation(1, 1, 0));
        drag.Begin(_scene, _selection, a, _undo);
        var nan = Matrix4x4.CreateTranslation(float.NaN, 0, 0);
        drag.Update(nan, _undo, "Move");
        drag.End(_undo);
        Near(new Vector3(1, 1, 0), a.WorldPosition);
        _undo.Undo();
        Near(new Vector3(1, 0, 0), a.WorldPosition);
        _undo.Undo();
        Near(Vector3.Zero, a.WorldPosition);
        Assert.False(_undo.CanUndo);
        Assert.False(drag.IsActive);
        Assert.Empty(drag.Entities);
    }
}

/// <summary>Camera edge cases: far plane at the zoom limit, entering fly mode from an orthographic view.</summary>
public sealed class CameraRobustnessTests
{
    [Fact]
    public void Zoomed_out_to_the_limit_the_target_stays_inside_the_depth_range()
    {
        var camera = new ViewportCamera { Distance = ViewportCamera.MaxDistance };
        Assert.Equal(ViewportCamera.MaxDistance, camera.Distance);
        Assert.True(camera.Far > camera.Distance * 1.5f, $"far {camera.Far} m clips the target at {camera.Distance} m");
        Assert.True(camera.Near < camera.Distance);

        // The stage (around the target) still projects inside the view.
        Assert.True(camera.WorldToScreen(camera.Target, new Vector2(800, 600), out var pixel));
        Assert.InRange(pixel.X, 399f, 401f);
        var clip = Vector4.Transform(new Vector4(camera.Target, 1f), camera.ViewMatrix * camera.ProjectionMatrix(800f / 600f));
        Assert.InRange(clip.Z / clip.W, 0f, 1f);
    }

    [Fact]
    public void Flying_from_a_zoomed_orthographic_view_keeps_the_apparent_size()
    {
        var camera = new ViewportCamera();
        camera.SnapTo(ViewDirection.Top);
        camera.Zoom(-12f); // zoom the plan view far out: only the orthographic height changes
        float visible = camera.OrthoHeight;
        Assert.True(visible > 30f);

        camera.Look(0f, 0f); // right mouse button in the top view: fly mode
        Assert.Equal(ProjectionKind.Perspective, camera.Projection);
        float apparent = 2f * camera.Distance * MathF.Tan(camera.FovY * 0.5f);
        Assert.Equal(visible, apparent, 0.01f * visible);
        Assert.Null(camera.SnappedView);
    }
}

/// <summary>Picking coordinates at different framebuffer scales (high-DPI monitors) and at the image edges.</summary>
public sealed class PickPixelTests
{
    [Theory]
    [InlineData(1f)]
    [InlineData(1.25f)]
    [InlineData(2f)]
    public void Pixels_follow_the_framebuffer_scale_and_stay_inside_the_target(float scale)
    {
        var imageMin = new Vector2(300.5f, 40.25f);
        var avail = new Vector2(457.4f, 290.3f); // a fractional panel size
        int w = Math.Max(1, (int)MathF.Round(avail.X * scale)), h = Math.Max(1, (int)MathF.Round(avail.Y * scale));

        // Centre of the image → centre of the target.
        var (cx, cy) = ViewportPanel.PixelOf(imageMin + avail * 0.5f, imageMin, scale, w, h);
        Assert.InRange(cx, w / 2 - 1, w / 2 + 1);
        Assert.InRange(cy, h / 2 - 1, h / 2 + 1);

        // The last point inside the image (ImGui's hover rectangle is [min, max)) is the last target pixel, not outside it.
        var last = imageMin + avail - new Vector2(0.01f);
        var (lx, ly) = ViewportPanel.PixelOf(last, imageMin, scale, w, h);
        Assert.Equal(w - 1, lx);
        Assert.Equal(h - 1, ly);

        // The first point is pixel 0; points outside clamp; non-finite input is safe.
        Assert.Equal((0, 0), ViewportPanel.PixelOf(imageMin, imageMin, scale, w, h));
        Assert.Equal((0, 0), ViewportPanel.PixelOf(imageMin - new Vector2(5f), imageMin, scale, w, h));
        Assert.Equal((0, 0), ViewportPanel.PixelOf(new Vector2(float.NaN), imageMin, scale, w, h));
    }

    [Fact]
    public void A_high_dpi_click_maps_to_twice_the_ui_offset()
    {
        var (x, y) = ViewportPanel.PixelOf(new Vector2(110f, 70f), new Vector2(10f, 20f), 2f, 1000, 1000);
        Assert.Equal((200, 100), (x, y));
        Assert.Equal((50, 25), ViewportPanel.PixelOf(new Vector2(60f, 45f), new Vector2(10f, 20f), 0f, 1000, 1000)); // bad scale = 1
    }
}

/// <summary>Damaged settings files are set aside once; headless (read-only) runs leave the user's files untouched.</summary>
public sealed class SettingsRobustnessTests
{
    [Fact]
    public void A_damaged_settings_file_is_set_aside_only_once()
    {
        using var dir = new TempDir();
        string path = dir.Write("settings.json", "{ \"uiScale\": 2, oops");
        var first = new SettingsStore(TestLog.Create(), path);
        Assert.Equal(1f, first.Load().UiScale);
        Assert.False(File.Exists(path)); // moved, not copied
        Assert.Single(Directory.GetFiles(dir.Path, "settings.json.damaged-*"));

        // The next start (no change saved in between) finds no damaged file and makes no second copy.
        var second = new SettingsStore(TestLog.Create(), path);
        Assert.Equal(1f, second.Load().UiScale);
        Assert.Single(Directory.GetFiles(dir.Path, "settings.json.damaged-*"));
    }

    [Fact]
    public void A_read_only_run_does_not_touch_a_damaged_settings_file()
    {
        using var dir = new TempDir();
        string path = dir.Write("settings.json", "[1, 2");
        var log = TestLog.Create();
        var store = new SettingsStore(log, path) { ReadOnly = true };
        Assert.Equal("en", store.Load().Language);
        Assert.Equal("[1, 2", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(dir.Path, "settings.json.damaged-*"));
        Assert.Contains(TestLog.Entries(log), e => e.Message.Contains("could not be read", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    [InlineData("{ \"uiScale\": \"big\" }")]
    [InlineData("{ \"uiScale\": 1e400 }")]
    [InlineData("{ \"artNetUniverseOffset\": 99999999999 }")]
    [InlineData("{ \"recentFiles\": [null, 5] }")]
    [InlineData("{ \"libraryFolders\": {} }")]
    public void Any_unreadable_content_gives_defaults(string json)
    {
        using var dir = new TempDir();
        string path = dir.Write("settings.json", json);
        var s = new SettingsStore(TestLog.Create(), path).Load();
        Assert.Equal(1f, s.UiScale);
        Assert.Equal("en", s.Language);
    }

    [Fact]
    public void Odd_but_readable_values_are_repaired()
    {
        using var dir = new TempDir();
        string path = dir.Write("settings.json",
            "{ \"uiScale\": \"NaN\", \"fontSize\": -3, \"language\": null, \"navigationStyle\": null, \"theme\": null, " +
            "\"recentFiles\": null, \"libraryFolders\": [null, \"\", \"/x\", \"/x\"], \"zoomSpeed\": \"Infinity\", \"keyBindingPreset\": \"  \" }");
        var s = new SettingsStore(TestLog.Create(), path).Load();
        Assert.Equal(1f, s.UiScale);
        Assert.Equal(FontSet.DefaultSize, s.FontSize);
        Assert.Equal("en", s.Language);
        Assert.Equal("Candela", s.NavigationStyle);
        Assert.Equal("Dark", s.Theme);
        Assert.Empty(s.RecentFiles);
        Assert.Equal(new[] { "/x" }, s.LibraryFolders);
        Assert.Equal(1f, s.ZoomSpeed);
        Assert.Equal("Default", s.KeyBindingPreset);
    }
}

/// <summary>Hand-edited or damaged key binding files never stop the editor from starting.</summary>
public sealed class KeyBindingFileRobustnessTests
{
    private const string Damaged = """
        { "bindings": {
            "edit.undo": [null, "", "Ctrl+Z"],
            "edit.redo": null,
            "": ["F5"],
            "view.top": ["Ctrl+", "NoSuchKey", "  "]
        } }
        """;

    [Fact]
    public void A_damaged_custom_set_is_repaired_at_start()
    {
        using var dir = new TempDir();
        var settings = new AppSettings { KeyBindingPreset = "Custom" };
        string custom = dir.Write("keybindings.custom.json", Damaged);
        var service = new KeyBindingsService(settings, new FilePresetStore(dir.File("presets")), custom, TestLog.Create());
        Assert.True(service.Library.IsCustom);
        var bindings = service.Current.Bindings;
        Assert.Equal(new[] { "Ctrl+Z" }, bindings[EditorActions.Undo]);
        Assert.Empty(bindings[EditorActions.Redo]);
        Assert.False(bindings.ContainsKey(""));
        Assert.True(bindings.ContainsKey(AppActions.NewViewport)); // newer actions get their defaults

        // The dispatcher's lookup and the display code accept the repaired set.
        var map = new KeyBindingMap(service.Current);
        Assert.Equal(2, map.InvalidCount); // "Ctrl+" and "NoSuchKey"
        Assert.Equal(EditorActions.Undo, map.Resolve(new KeyChord("Z", KeyModifiers.Ctrl)));
        foreach (var chords in bindings.Values)
            foreach (var chord in chords)
                Assert.False(string.IsNullOrEmpty(KeyNames.DisplayChord(chord)));
    }

    [Fact]
    public void A_damaged_named_preset_is_repaired_and_a_missing_one_falls_back()
    {
        using var dir = new TempDir();
        var store = new FilePresetStore(dir.File("presets"));
        store.Save(KeyBindingSet.PresetKind, "Hand edited", Damaged);
        store.Save(KeyBindingSet.PresetKind, "No table", "{ \"bindings\": null }");
        var settings = new AppSettings { KeyBindingPreset = "Hand edited" };
        var service = new KeyBindingsService(settings, store, dir.File("custom.json"), TestLog.Create());
        Assert.Equal("Hand edited", service.Library.CurrentName);
        Assert.Empty(service.Current.Bindings[EditorActions.Redo]);
        _ = new KeyBindingMap(service.Current);

        service.Select("No table");
        Assert.NotNull(service.Current.Bindings);
        _ = new KeyBindingMap(service.Current);

        service.Select("Does not exist");
        Assert.Equal("Default", service.Library.CurrentName);

        // A map over a set without a table (a set built in code) is empty, not a crash.
        var map = new KeyBindingMap(new KeyBindingSet { Bindings = null! });
        Assert.Null(map.Resolve(new KeyChord("Z", KeyModifiers.Ctrl)));
    }
}

/// <summary>A failing dialog or menu stays contained: the frame completes and the rest of the UI keeps working.</summary>
[Collection(UiStateCollection.Name)]
public sealed class UiGuardTests
{
    [Fact]
    public void A_failing_modal_dialog_is_contained_and_closed()
    {
        using var ui = new HeadlessImGui(new Vector2(800, 600));
        var log = TestLog.Create();
        var reports = new List<string>();
        var guard = new UiGuard(log, (part, message) => reports.Add(part + ": " + message));
        bool open = true, otherDrawn = false;
        for (int frame = 0; frame < 5; frame++)
        {
            ui.Frame(() =>
            {
                bool ok = guard.Run("dialog", () =>
                {
                    if (!open)
                        return;
                    if (!ImGui.IsPopupOpen("Dialog"))
                        ImGui.OpenPopup("Dialog");
                    if (ImGui.BeginPopupModal("Dialog"))
                    {
                        ImGui.PushStyleVar(ImGuiStyleVar.Alpha, 0.5f);
                        ImGui.BeginChild("inner");
                        throw new IOException("disk gone");
                    }
                });
                if (!ok)
                {
                    open = false; // the workspace closes a failed dialog
                    UiGuard.CloseAllPopups();
                }

                Assert.True(guard.Run("other", () =>
                {
                    ImGui.Begin("Other");
                    ImGui.Text("works");
                    ImGui.End();
                    otherDrawn = true;
                }));
            });
        }

        Assert.True(otherDrawn);
        Assert.False(open);
        Assert.Single(reports);
        Assert.Contains("disk gone", reports[0], StringComparison.Ordinal);
        ui.Frame(() => Assert.False(ImGui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId)));
    }

    [Fact]
    public void A_part_failing_every_frame_is_logged_once_in_full()
    {
        using var ui = new HeadlessImGui(new Vector2(800, 600));
        var log = TestLog.Create();
        int reports = 0;
        var guard = new UiGuard(log, (_, _) => reports++);
        for (int frame = 0; frame < 25; frame++)
            ui.Frame(() => guard.Run("menu", () => throw new InvalidOperationException("broken menu")));
        Assert.Equal(25, reports); // the session de-duplicates the issues
        var errors = TestLog.Entries(log).Where(e => e.Level == LogLevel.Error).ToList();
        Assert.Equal(2, errors.Count); // the first failure, then "failed 10 times"
        Assert.Contains("10 times", errors[1].Message, StringComparison.Ordinal);
    }
}

/// <summary>The file browser on odd paths: unreadable folders, symbolic link loops, Unicode names, very large folders.</summary>
[Collection(UiStateCollection.Name)]
public sealed class FileBrowserOddPathTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static FileBrowserModel Model(FileBrowserMode mode = FileBrowserMode.Open) =>
        new(mode, new[] { new FileFilter("All", "*") });

    [Fact]
    public void An_unreadable_folder_is_reported_not_shown_as_empty()
    {
        // As root most folders are readable; /proc/1/map_files is not (needs CAP_SYS_ADMIN). Elsewhere a folder without
        // permissions is made (that works for normal users).
        string? locked = null;
        if (OperatingSystem.IsLinux() && Directory.Exists("/proc/1/map_files") && !CanList("/proc/1/map_files"))
            locked = "/proc/1/map_files";
        else if (!OperatingSystem.IsWindows())
        {
            locked = Path.Combine(_dir.Path, "locked");
            Directory.CreateDirectory(locked);
            File.WriteAllText(Path.Combine(locked, "secret.txt"), "x");
            File.SetUnixFileMode(locked, UnixFileMode.None);
            if (CanList(locked))
                locked = null; // running with privileges that ignore permissions
        }

        if (locked is null)
            return; // no unreadable folder can be made here

        var model = Model();
        Assert.True(model.Navigate(_dir.Path));
        int before = model.Entries.Count;
        Assert.False(model.Navigate(locked));
        Assert.NotEmpty(model.Error);
        Assert.Equal(_dir.Path, model.CurrentDirectory); // the previous listing stays
        Assert.Equal(before, model.Entries.Count);
        if (!OperatingSystem.IsWindows() && locked.StartsWith(_dir.Path, StringComparison.Ordinal))
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static bool CanList(string path)
    {
        try
        {
            _ = Directory.EnumerateFileSystemEntries(path).FirstOrDefault();
            return true;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    [Fact]
    public void Symbolic_link_loops_and_broken_links_are_listed_safely()
    {
        if (OperatingSystem.IsWindows())
            return; // symbolic links need privileges there
        string root = Path.Combine(_dir.Path, "links");
        Directory.CreateDirectory(Path.Combine(root, "real"));
        File.CreateSymbolicLink(Path.Combine(root, "self"), Path.Combine(root, "self")); // ELOOP
        Directory.CreateSymbolicLink(Path.Combine(root, "up"), root); // a cycle back to the folder itself
        File.CreateSymbolicLink(Path.Combine(root, "broken.mvr"), Path.Combine(root, "nowhere.mvr"));

        var model = Model();
        Assert.True(model.Navigate(root), model.Error);
        var names = model.Entries.Select(e => e.Name).ToList();
        Assert.Contains("real", names);
        Assert.Contains("up", names);
        Assert.Contains("broken.mvr", names);
        Assert.True(model.Entries.Single(e => e.Name == "up").IsDirectory);

        // Following the cycle is just another folder; the self-loop is refused with a message.
        Assert.True(model.Navigate(Path.Combine(root, "up", "up", "up")));
        Assert.False(model.Navigate(Path.Combine(root, "self")));
        Assert.NotEmpty(model.Error);

        // A broken link cannot be opened.
        Assert.True(model.Navigate(root));
        model.FileName = "broken.mvr";
        Assert.Null(model.Resolve());
    }

    [Fact]
    public void Unicode_names_are_listed_sorted_and_resolved()
    {
        string root = Path.Combine(_dir.Path, "Сцены 🎭");
        Directory.CreateDirectory(root);
        foreach (var name in new[] { "Фокина 2.mvr", "Фокина 10.mvr", "舞台.mvr", "éclairage.mvr", "notes.txt" })
            File.WriteAllText(Path.Combine(root, name), "x");
        var model = new FileBrowserModel(FileBrowserMode.Open, new[] { new FileFilter("MVR", "*.mvr") });
        Assert.True(model.Navigate(root), model.Error);
        var names = model.Entries.Select(e => e.Name).ToList();
        Assert.Equal(4, names.Count);
        Assert.True(names.IndexOf("Фокина 2.mvr") < names.IndexOf("Фокина 10.mvr")); // natural order in Cyrillic too
        model.FileName = "舞台.mvr";
        Assert.Equal(Path.Combine(root, "舞台.mvr"), model.Resolve());
    }

    [Fact]
    public void A_very_large_folder_draws_only_the_visible_rows()
    {
        string big = Path.Combine(_dir.Path, "big");
        Directory.CreateDirectory(big);
        const int count = 20000;
        for (int i = 0; i < count; i++)
            File.Create(Path.Combine(big, $"fixture {i}.gdtf")).Dispose();

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var model = Model();
        Assert.True(model.Navigate(big));
        Assert.Equal(count, model.Entries.Count);
        Assert.True(watch.Elapsed.TotalSeconds < 20, $"listing took {watch.Elapsed.TotalSeconds:0.0} s");

        Loc.SetLanguage("en");
        using var ui = new HeadlessImGui(new Vector2(1600, 900));
        var dialog = new FileBrowserDialog(_dir.File("recent-places.json"));
        dialog.Show(FileBrowserMode.Open, "Open", new[] { new FileFilter("All", "*") }, big, null, _ => { });
        for (int i = 0; i < 3; i++)
            ui.Frame(() => dialog.Draw(1f)); // first frames build the row texts once

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10; i++)
            ui.Frame(() => dialog.Draw(1f));
        long perFrame = (GC.GetAllocatedBytesForCurrentThread() - before) / 10;
        // Drawing all 20 000 rows built three strings per row per frame (megabytes); visible rows only, cached: almost nothing.
        Assert.True(perFrame < 16 * 1024, $"{perFrame} bytes allocated per frame");
        Assert.True(dialog.IsOpen);
    }
}

public sealed class FileBrowserPathTextTests
{
    [Fact]
    public void A_tilde_means_the_home_folder()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(home, FileBrowserModel.ExpandHome("~"));
        Assert.Equal(Path.Combine(home, "shows"), FileBrowserModel.ExpandHome("~/shows"));
        Assert.Equal("~shows", FileBrowserModel.ExpandHome("~shows")); // another user's home is not guessed
        Assert.Equal("/tmp/~", FileBrowserModel.ExpandHome("/tmp/~"));
        var model = new FileBrowserModel(FileBrowserMode.SelectFolder, Array.Empty<FileFilter>());
        if (Directory.Exists(home))
        {
            Assert.True(model.Navigate("~"), model.Error);
            Assert.Equal(Path.GetFullPath(home), model.CurrentDirectory);
        }
    }
}

/// <summary>The rotation fields keep the Euler angles the user entered instead of flipping to an equivalent triple.</summary>
public sealed class EulerDisplayTests
{
    private static void Near(Vector3 expected, Vector3 actual) =>
        Assert.True(Vector3.Distance(expected, actual) < 1e-3f, $"expected {expected}, got {actual}");

    [Fact]
    public void A_y_rotation_beyond_90_degrees_stays_as_entered()
    {
        var entity = new Entity("spot");
        var display = new EulerDisplay();
        Near(Vector3.Zero, display.Get(entity, entity.Local.Rotation));

        // Typed (10°, 80°, 20°), then a drag on Y up to 120° in steps, as the panel applies it frame by frame.
        var angles = new Vector3(10f, 80f, 20f);
        entity.Local = entity.Local with { Rotation = TransformMath.FromEulerDegrees(angles) };
        display.Set(entity, angles, entity.Local.Rotation);
        for (float y = 85f; y <= 120f; y += 5f)
        {
            var shown = display.Get(entity, entity.Local.Rotation);
            Near(angles, shown); // the field continues from what it showed, not from (−170°, 85°, −160°)
            angles = shown with { Y = y };
            var q = TransformMath.FromEulerDegrees(angles);
            entity.Local = entity.Local with { Rotation = q };
            display.Set(entity, angles, entity.Local.Rotation);
        }

        Near(new Vector3(10f, 120f, 20f), display.Get(entity, entity.Local.Rotation));
        // The plain decomposition of the same rotation is the other triple.
        Near(new Vector3(-170f, 60f, -160f), TransformMath.ToEulerDegrees(entity.Local.Rotation));
    }

    [Fact]
    public void A_rotation_changed_elsewhere_or_another_entity_is_decomposed_afresh()
    {
        var a = new Entity("a");
        var b = new Entity("b");
        var display = new EulerDisplay();
        var q = TransformMath.FromEulerDegrees(new Vector3(0f, 100f, 0f));
        display.Set(a, new Vector3(0f, 100f, 0f), q);
        Near(new Vector3(0f, 100f, 0f), display.Get(a, q));
        Near(new Vector3(0f, 100f, 0f), display.Get(a, Quaternion.Negate(q))); // the same rotation

        // The gizmo (or undo) turned it: the stored triple no longer applies.
        var turned = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.5f);
        Near(TransformMath.ToEulerDegrees(turned), display.Get(a, turned));
        // Another entity with the same rotation as the stored one is not given a's triple.
        display.Set(a, new Vector3(0f, 100f, 0f), q);
        Near(TransformMath.ToEulerDegrees(q), display.Get(b, q));
    }
}

/// <summary>Frame Selection on objects without geometry frames them, not the whole stage.</summary>
public sealed class FramingBoundsTests
{
    [Fact]
    public void Entities_without_geometry_frame_a_small_box_around_their_origin()
    {
        var scene = new SceneModel("t");
        var light = scene.Add(new Entity("light") { Local = new Transform(new Vector3(4, 2, 6)) });
        var box = scene.Add(new Entity("box"));
        var boxBounds = new Aabb(new Vector3(-1, -1, 0), new Vector3(1, 1, 2));
        bool Lookup(Guid id, out Aabb b)
        {
            b = id == box.Id ? boxBounds : Aabb.Empty;
            return id == box.Id;
        }

        var lightOnly = SelectionController.FramingBounds(scene, new[] { light.Id }, Lookup);
        Assert.False(lightOnly.IsEmpty);
        Assert.Equal(new Vector3(4, 2, 6), lightOnly.Center);
        Assert.Equal(new Vector3(1f), lightOnly.Size);

        var both = SelectionController.FramingBounds(scene, new[] { box.Id, light.Id }, Lookup);
        Assert.Equal(new Vector3(-1, -1, 0), both.Min);
        Assert.Equal(new Vector3(4.5f, 2.5f, 6.5f), both.Max);

        // Unknown ids frame nothing (the caller then frames the whole stage).
        Assert.True(SelectionController.FramingBounds(scene, new[] { Guid.NewGuid() }, Lookup).IsEmpty);
    }
}

/// <summary>Folder checks for the settings window never block the UI thread.</summary>
public sealed class FolderStatusCacheTests
{
    [Fact]
    public void A_folder_that_does_not_answer_does_not_block_and_is_checked_rarely()
    {
        using var gate = new ManualResetEventSlim(false);
        int calls = 0;
        var cache = new FolderStatusCache(path =>
        {
            Interlocked.Increment(ref calls);
            gate.Wait(TimeSpan.FromSeconds(10)); // an unreachable network share
            return path == "/share/fixtures";
        }, maxAgeSeconds: 2.0);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (int frame = 0; frame < 100; frame++)
            Assert.Null(cache.Exists("/share/fixtures", frame / 60.0)); // unknown while the check hangs
        Assert.True(watch.Elapsed.TotalSeconds < 1, "the UI thread waited for the folder check");

        gate.Set();
        Assert.True(SpinWait.SpinUntil(() => cache.Exists("/share/fixtures", 1.0) == true, TimeSpan.FromSeconds(10)));
        Assert.Equal(1, Volatile.Read(ref calls)); // one check for 100 frames within the 2 s window

        // Old answers are refreshed in the background; failures read as "missing".
        Assert.True(cache.Exists("/share/fixtures", 5.0));
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref calls) == 2, TimeSpan.FromSeconds(10)));
        var failing = new FolderStatusCache(_ => throw new IOException("network name no longer available"));
        Assert.True(SpinWait.SpinUntil(() => failing.Exists("//server/share", 0) == false, TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void Real_folders_are_reported()
    {
        using var dir = new TempDir();
        var cache = new FolderStatusCache();
        Assert.True(SpinWait.SpinUntil(() => cache.Exists(dir.Path, 0) == true, TimeSpan.FromSeconds(10)));
        Assert.True(SpinWait.SpinUntil(() => cache.Exists(Path.Combine(dir.Path, "missing"), 0) == false, TimeSpan.FromSeconds(10)));
        cache.Retain(new[] { dir.Path });
    }
}
