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
