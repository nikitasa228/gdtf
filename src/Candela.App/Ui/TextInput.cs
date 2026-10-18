using Hexa.NET.ImGui;

namespace Candela.App.Ui;

/// <summary>
/// Single-line text fields confirmed with Enter. The fields are drawn without <c>ImGuiInputTextFlags.EnterReturnsTrue</c>:
/// with that flag the binding's <c>ref string</c> overloads hand the typed text back only when Enter is pressed, so a
/// name typed and then confirmed with the mouse (OK, Save) was lost. The field updates its string while typing, and
/// <see cref="Confirmed"/> tells when Enter ended the edit.
/// </summary>
internal static class TextInput
{
    /// <summary>True when the text field drawn last was just confirmed with Enter (it lost the keyboard to Enter).</summary>
    public static bool Confirmed() =>
        ImGui.IsItemDeactivated() && (ImGui.IsKeyPressed(ImGuiKey.Enter, false) || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter, false));
}
