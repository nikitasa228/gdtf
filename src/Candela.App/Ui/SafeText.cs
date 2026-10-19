using System.Numerics;
using Hexa.NET.ImGui;

namespace Candela.App.Ui;

/// <summary>
/// Text drawing for strings that are not format strings. Dear ImGui's <c>Text</c>, <c>TextDisabled</c>,
/// <c>TextColored</c>, <c>TextWrapped</c> and <c>SetTooltip</c> take a printf format, so a '%' in a name, a path or a
/// translated text ("17% of 16 A") would be read as a conversion — garbage at best, a crash with "%s". These helpers
/// always draw the text as it is (<c>TextUnformatted</c>).
/// </summary>
internal static class SafeText
{
    /// <summary>The text in the disabled (dimmed) text colour.</summary>
    public static void Disabled(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
        ImGui.TextUnformatted(text);
        ImGui.PopStyleColor();
    }

    /// <summary>The text in <paramref name="color"/>.</summary>
    public static void Colored(Vector4 color, string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGui.TextUnformatted(text);
        ImGui.PopStyleColor();
    }

    /// <summary>The text wrapped at the right edge of the window.</summary>
    public static void Wrapped(string text)
    {
        ImGui.PushTextWrapPos(0f);
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
    }

    /// <summary>A tooltip with the text (call while the item is hovered).</summary>
    public static void Tooltip(string text)
    {
        if (ImGui.BeginTooltip())
        {
            ImGui.TextUnformatted(text);
            ImGui.EndTooltip();
        }
    }
}
