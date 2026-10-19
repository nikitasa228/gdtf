using Candela.App.Diagnostics;
using Hexa.NET.ImGui;

namespace Candela.App.Shell;

/// <summary>
/// Runs one top-level part of the UI — the main menu, the status bar, the dock space, a dialog, the shortcut
/// dispatch — so that an exception stays inside that part: Dear ImGui's stacks are unwound to where the part began, the
/// failure is logged (in full the first time, then counted) and reported, and the rest of the frame runs normally. A
/// part that fails every frame therefore cannot take the editor down (the frame loop stops after many consecutive
/// failed frames); the caller closes a failed dialog so it does not fail again.
/// </summary>
internal sealed unsafe class UiGuard
{
    private readonly AppLog _log;
    private readonly Action<string, string> _report;
    private readonly Dictionary<string, RepeatedErrorFilter> _filters = new();

    /// <summary>Creates a guard that logs to <paramref name="log"/> and reports (code, message) through <paramref name="report"/>.</summary>
    public UiGuard(AppLog log, Action<string, string> report)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _report = report ?? throw new ArgumentNullException(nameof(report));
    }

    /// <summary>
    /// Runs <paramref name="draw"/> (inside an ImGui frame); returns false when it threw (the exception was handled).
    /// <paramref name="part"/> names the part in the log and the issues panel.
    /// </summary>
    public bool Run(string part, Action draw)
    {
        ArgumentNullException.ThrowIfNull(draw);
        ImGuiErrorRecoveryState state;
        ImGuiP.ErrorRecoveryStoreState(&state);
        try
        {
            draw();
            return true;
        }
        catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            UiFrame.Recover(&state);
            if (!_filters.TryGetValue(part, out var filter))
                _filters[part] = filter = new RepeatedErrorFilter();
            switch (filter.Record(e))
            {
                case ErrorLogKind.Full:
                    _log.Error("UI", $"'{part}' failed", e);
                    break;
                case ErrorLogKind.Count:
                    _log.Error("UI", $"'{part}' failed {filter.Repeats} times: {e.GetType().Name}: {e.Message}");
                    break;
            }

            _report(part, e.Message);
            return false;
        }
    }

    /// <summary>Closes every open popup (after a failed modal dialog, so no stale modal blocks the workspace).</summary>
    public static void CloseAllPopups()
    {
        if (ImGui.GetCurrentContext().OpenPopupStack.Size > 0)
            ImGuiP.ClosePopupToLevel(0, false);
    }
}
