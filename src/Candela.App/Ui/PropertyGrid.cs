using Hexa.NET.ImGui;

namespace Candela.App.Ui;

/// <summary>
/// Two-column "label | editor" layout used by the properties, preview, render and settings panels: labels in a fixed
/// column on the left (muted), editors filling the rest, so long localised labels never get cut by a widget.
/// </summary>
internal static class PropertyGrid
{
    /// <summary>Starts a grid; returns false when it is clipped (then do not call <see cref="End"/>).</summary>
    /// <param name="id">Table id.</param>
    /// <param name="labelWidth">Label column width in pixels at 100 % (scaled by the UI scale).</param>
    public static bool Begin(string id, float labelWidth = 150f)
    {
        if (!ImGui.BeginTable(id, 2, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.NoSavedSettings))
            return false;
        var style = ImGui.GetStyle();
        ImGui.TableSetupColumn("label", ImGuiTableColumnFlags.WidthFixed, labelWidth * style.FontScaleMain * style.FontScaleDpi);
        ImGui.TableSetupColumn("value", ImGuiTableColumnFlags.WidthStretch);
        return true;
    }

    /// <summary>Starts a row: draws the label and prepares a full-width editor in the second column.</summary>
    public static void Row(string label)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextMuted);
        ImGui.TextUnformatted(label);
        ImGui.PopStyleColor();
        if (ImGui.IsItemHovered() && ImGui.CalcTextSize(label).X > ImGui.GetColumnWidth())
            ImGui.SetTooltip(label);
        ImGui.TableNextColumn();
        ImGui.SetNextItemWidth(-1);
    }

    /// <summary>A row with a localised label.</summary>
    public static void Row(Str label) => Row(Loc.Get(label));

    /// <summary>Ends the grid.</summary>
    public static void End() => ImGui.EndTable();
}
