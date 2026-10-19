using Candela.App.Input;
using Hexa.NET.ImGui;

namespace Candela.App.Windows;

/// <summary>Keyboard conventions shared by the editor's floating windows (Settings, Keyboard Shortcuts, About).</summary>
internal static class WindowKeys
{
    /// <summary>
    /// True when Escape (without modifiers) was pressed this frame while the current window has the focus and no text
    /// field is being edited — the window should close. The press is consumed, so it does not also run the Escape
    /// shortcut (Deselect) on the scene behind the window. Call between <c>Begin</c> and <c>End</c>.
    /// </summary>
    public static bool CloseRequested(KeyBindingDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        var io = ImGui.GetIO();
        if (io.WantTextInput || io.KeyCtrl || io.KeyShift || io.KeyAlt || dispatcher.IsConsumed(ImGuiKey.Escape) ||
            !ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows) || !ImGui.IsKeyPressed(ImGuiKey.Escape, false))
            return false;
        dispatcher.Consume(ImGuiKey.Escape);
        return true;
    }
}
