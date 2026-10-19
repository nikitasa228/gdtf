using Hexa.NET.ImGui;

namespace Candela.App.Shell;

/// <summary>
/// Builds the UI of one Dear ImGui frame so that an exception can never leave the frame open. Dear ImGui aborts the
/// process (a native assertion, not an exception) when <c>NewFrame</c> is called while the previous frame was not ended
/// with <c>Render</c>, so a failure anywhere in the menus, dialogs or the status bar would otherwise crash the editor one
/// frame later: here the window, ID and style stacks are unwound with Dear ImGui's error recovery, the frame is ended
/// and the exception is passed on to the frame loop (which logs and reports it, then runs the next frame).
/// </summary>
internal static unsafe class UiFrame
{
    /// <summary>
    /// Runs <paramref name="draw"/> inside the current frame (after <c>NewFrame</c>) and ends the frame with
    /// <c>ImGui.Render</c>, also when <paramref name="draw"/> throws (the exception is rethrown after the frame ended).
    /// </summary>
    public static void BuildAndRender(Action draw)
    {
        ArgumentNullException.ThrowIfNull(draw);
        ImGuiErrorRecoveryState state;
        ImGuiP.ErrorRecoveryStoreState(&state);
        try
        {
            draw();
        }
        catch
        {
            Recover(&state);
            ImGui.Render();
            throw;
        }

        ImGui.Render();
    }

    /// <summary>
    /// Unwinds what was begun or pushed after <paramref name="state"/> was stored (windows, child windows, tables, tab
    /// bars, groups, ID / style / colour / font stacks) without Dear ImGui's assertion, which would abort the process.
    /// </summary>
    public static void Recover(ImGuiErrorRecoveryState* state)
    {
        var io = ImGui.GetIO();
        bool assert = io.ConfigErrorRecoveryEnableAssert;
        io.ConfigErrorRecoveryEnableAssert = false;
        try
        {
            ImGuiP.ErrorRecoveryTryToRecoverState(state);
        }
        finally
        {
            io.ConfigErrorRecoveryEnableAssert = assert;
        }
    }
}
