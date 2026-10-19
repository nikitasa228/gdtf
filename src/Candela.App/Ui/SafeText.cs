using System.Numerics;
using Hexa.NET.ImGui;

namespace Candela.App.Ui;

/// <summary>
/// Text drawing for strings that are not format strings — every text the editor shows goes through here or through
/// <c>ImGui.TextUnformatted</c>. Dear ImGui's <c>Text</c>, <c>TextDisabled</c>, <c>TextColored</c>, <c>TextWrapped</c>,
/// <c>BulletText</c>, <c>LabelText</c>, <c>SetTooltip</c> and <c>SetItemTooltip</c> take a printf format and the binding
/// passes no arguments, so a '%' in a file name, a fixture name, a path, a value or a translated text ("17% of 16 A")
/// would be read as a conversion: garbage at best, a crash with "%s" or "%n". These helpers always draw the text as it
/// is (<c>TextUnformatted</c>); a test scans the sources so the format variants stay unused.
/// </summary>
/// <remarks>
/// Widget labels are not formats, but "##" in a label starts its hidden ID part, so a name containing "##" would be cut
/// off there. <see cref="Label"/> keeps such names whole for labels built from user or file data.
/// </remarks>
internal static class SafeText
{
    /// <summary>Hair space (U+200A) put between the two '#' of "##" in labels, so ImGui does not read an ID suffix.</summary>
    private const char HairSpace = ' ';

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

    /// <summary>The text wrapped at the right edge of the window (like <c>TextWrapped</c>).</summary>
    public static void Wrapped(string text)
    {
        ImGui.PushTextWrapPos(0f);
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
    }

    /// <summary>A bullet and the text (like <c>BulletText</c>).</summary>
    public static void Bullet(string text)
    {
        ImGui.Bullet();
        ImGui.TextUnformatted(text);
    }

    /// <summary>
    /// A tooltip with the text (call while the item is hovered). Like <c>SetTooltip</c> it replaces a tooltip another
    /// item of the same frame already set.
    /// </summary>
    public static void Tooltip(string text)
    {
        if (ImGuiP.BeginTooltipEx(ImGuiTooltipFlags.OverridePrevious, ImGuiWindowFlags.None))
        {
            ImGui.TextUnformatted(text);
            ImGui.EndTooltip();
        }
    }

    /// <summary>
    /// <paramref name="text"/> made safe to use as (the visible part of) a widget label: every "##" gets a hair space
    /// between its two '#', so ImGui shows the whole name instead of treating the rest as a hidden ID. Returns the same
    /// string (no allocation) when there is no "##"; append your own "##id" after it.
    /// </summary>
    public static string Label(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        if (!text.Contains("##", StringComparison.Ordinal))
            return text;
        // Between every two adjacent '#' ("###" included), so no "##" is left anywhere.
        var builder = new System.Text.StringBuilder(text.Length + 8);
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '#' && i > 0 && text[i - 1] == '#')
                builder.Append(HairSpace);
            builder.Append(text[i]);
        }

        return builder.ToString();
    }
}
