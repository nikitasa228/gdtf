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

/// <summary>Repeated failures neither flood the issues panel nor fill the disk with log text.</summary>
[Collection(UiStateCollection.Name)]
public sealed class FailureReportingTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly Candela.App.Diagnostics.AppLog _log = TestLog.Create();
    private readonly Candela.App.Settings.SettingsStore _settings;
    private readonly Candela.App.Session.EditorSession _session;

    public FailureReportingTests()
    {
        Candela.App.Ui.Loc.SetLanguage("en");
        _settings = new Candela.App.Settings.SettingsStore(_log, _dir.File("settings.json"));
        _settings.Load();
        _session = new Candela.App.Session.EditorSession(_log, _settings);
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
        Assert.Equal(Candela.App.Session.EditorSession.MaxProgramMessages, _session.Issues.Current.Count);
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
        var filter = new Candela.App.Diagnostics.RepeatedErrorFilter();
        var kinds = new List<Candela.App.Diagnostics.ErrorLogKind>();
        for (int i = 0; i < 1000; i++)
            kinds.Add(filter.Record(Throw("boom")));
        Assert.Equal(Candela.App.Diagnostics.ErrorLogKind.Full, kinds[0]);
        Assert.Equal(1, kinds.Count(k => k == Candela.App.Diagnostics.ErrorLogKind.Full));
        // Counted at 10, 100 and 1000 repeats.
        Assert.Equal(new[] { 9, 99, 999 }, kinds.Select((k, i) => (k, i)).Where(t => t.k == Candela.App.Diagnostics.ErrorLogKind.Count).Select(t => t.i));
        Assert.Equal(1000, filter.Repeats);

        // A different failure is logged in full again.
        Assert.Equal(Candela.App.Diagnostics.ErrorLogKind.Full, filter.Record(Throw("other")));
        Assert.Equal(Candela.App.Diagnostics.ErrorLogKind.Full, filter.Record(new ArgumentException("boom")));
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
        using (var first = new Candela.App.Diagnostics.AppLog { ConsoleLevel = Candela.App.Diagnostics.LogLevel.Error + 1 })
        {
            first.OpenFile(path);
            first.Info("Test", "first run");
        }

        using (var second = new Candela.App.Diagnostics.AppLog { ConsoleLevel = Candela.App.Diagnostics.LogLevel.Error + 1, MaxFileBytes = 4096 })
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
        Assert.Contains("first run", File.ReadAllText(Candela.App.Diagnostics.AppLog.PreviousPathOf(path)));
        Assert.EndsWith("app.previous.log", Candela.App.Diagnostics.AppLog.PreviousPathOf(path), StringComparison.Ordinal);
    }
}

/// <summary>The gizmo drag of a multi-selection around the primary pivot (the logic the viewport runs per drag frame).</summary>
public sealed class GizmoDragTests
{
    private static void Near(Vector3 expected, Vector3 actual, float tolerance = 1e-4f) =>
        Assert.True(Vector3.Distance(expected, actual) <= tolerance, $"expected {expected}, got {actual}");

    private readonly Candela.Core.SceneGraph.Scene _scene = new("t");
    private readonly Candela.Core.Editing.Selection _selection = new();
    private readonly Candela.Core.Editing.UndoStack _undo = new();

    private Candela.Core.SceneGraph.Entity Add(string name, Vector3 position, Candela.Core.SceneGraph.Entity? parent = null) =>
        _scene.Add(new Candela.Core.SceneGraph.Entity(name) { Local = new Candela.Core.Math.Transform(position) }, parent);

    /// <summary>Drags the gizmo of <paramref name="primary"/> in <paramref name="frames"/> steps to <paramref name="end"/>.</summary>
    private void Drag(Candela.App.Editing.GizmoDrag drag, Candela.Core.SceneGraph.Entity primary, Matrix4x4 end, int frames = 10)
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
        var drag = new Candela.App.Editing.GizmoDrag();
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
        var drag = new Candela.App.Editing.GizmoDrag();
        Drag(drag, child, child.WorldMatrix * Matrix4x4.CreateTranslation(0, 0, 2));
        Near(new Vector3(1, 0, 0), parent.WorldPosition);
        Near(new Vector3(1, 2, 2), child.WorldPosition);
        Near(new Vector3(5, 5, 2), other.WorldPosition);
    }

    [Fact]
    public void Entities_on_locked_layers_stay_put()
    {
        var layer = new Candela.Core.SceneGraph.Layer { Name = "rig", Locked = true };
        _scene.Layers.Add(layer);
        var a = Add("a", Vector3.Zero);
        var b = Add("b", new Vector3(2, 0, 0));
        b.LayerId = layer.Id;
        _selection.Set(new[] { b.Id, a.Id });
        Assert.False(Candela.App.Editing.SelectionController.IsEditable(_scene, b));
        Drag(new Candela.App.Editing.GizmoDrag(), a, Matrix4x4.CreateTranslation(0, 1, 0));
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
        Drag(new Candela.App.Editing.GizmoDrag(), pivot, end, frames: 30);

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
        var drag = new Candela.App.Editing.GizmoDrag();
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
        var camera = new Candela.App.Viewport.ViewportCamera { Distance = Candela.App.Viewport.ViewportCamera.MaxDistance };
        Assert.Equal(Candela.App.Viewport.ViewportCamera.MaxDistance, camera.Distance);
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
        var camera = new Candela.App.Viewport.ViewportCamera();
        camera.SnapTo(Candela.App.Viewport.ViewDirection.Top);
        camera.Zoom(-12f); // zoom the plan view far out: only the orthographic height changes
        float visible = camera.OrthoHeight;
        Assert.True(visible > 30f);

        camera.Look(0f, 0f); // right mouse button in the top view: fly mode
        Assert.Equal(Candela.Rendering.ProjectionKind.Perspective, camera.Projection);
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
        var (cx, cy) = Candela.App.Viewport.ViewportPanel.PixelOf(imageMin + avail * 0.5f, imageMin, scale, w, h);
        Assert.InRange(cx, w / 2 - 1, w / 2 + 1);
        Assert.InRange(cy, h / 2 - 1, h / 2 + 1);

        // The last point inside the image (ImGui's hover rectangle is [min, max)) is the last target pixel, not outside it.
        var last = imageMin + avail - new Vector2(0.01f);
        var (lx, ly) = Candela.App.Viewport.ViewportPanel.PixelOf(last, imageMin, scale, w, h);
        Assert.Equal(w - 1, lx);
        Assert.Equal(h - 1, ly);

        // The first point is pixel 0; points outside clamp; non-finite input is safe.
        Assert.Equal((0, 0), Candela.App.Viewport.ViewportPanel.PixelOf(imageMin, imageMin, scale, w, h));
        Assert.Equal((0, 0), Candela.App.Viewport.ViewportPanel.PixelOf(imageMin - new Vector2(5f), imageMin, scale, w, h));
        Assert.Equal((0, 0), Candela.App.Viewport.ViewportPanel.PixelOf(new Vector2(float.NaN), imageMin, scale, w, h));
    }

    [Fact]
    public void A_high_dpi_click_maps_to_twice_the_ui_offset()
    {
        var (x, y) = Candela.App.Viewport.ViewportPanel.PixelOf(new Vector2(110f, 70f), new Vector2(10f, 20f), 2f, 1000, 1000);
        Assert.Equal((200, 100), (x, y));
        Assert.Equal((50, 25), Candela.App.Viewport.ViewportPanel.PixelOf(new Vector2(60f, 45f), new Vector2(10f, 20f), 0f, 1000, 1000)); // bad scale = 1
    }
}
