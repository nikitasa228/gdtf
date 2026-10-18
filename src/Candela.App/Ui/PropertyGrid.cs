using Hexa.NET.ImGui;

namespace Candela.App.Ui;

/// <summary>
/// Two-column "label | editor" layout used by the properties, preview, render and settings panels: labels in a column
/// on the left (muted), editors filling the rest. The label column fits the widest label of the grid (measured while
/// the rows are drawn and applied from the next frame on), so long or translated labels are not cut; it never gets
/// narrower than the grid's minimum and never wider than half of the available width (longer labels are then clipped
/// and show their full text as a tooltip).
/// </summary>
internal static class PropertyGrid
{
    /// <summary>Share of the available width the label column may take at most.</summary>
    public const float MaxLabelShare = 0.5f;

    // Widest label of each grid in the previous frame, keyed by the table id (ids are stable across frames).
    private static readonly Dictionary<uint, float> s_widestLabel = new();
    private static uint s_currentTable;
    private static float s_currentWidest;

    /// <summary>Starts a grid; returns false when it is clipped (then do not call <see cref="End"/>).</summary>
    /// <param name="id">Table id.</param>
    /// <param name="minLabelWidth">Smallest label column width in pixels at 100 % (scaled by the UI scale).</param>
    public static bool Begin(string id, float minLabelWidth = 150f)
    {
        var style = ImGui.GetStyle();
        float scale = style.FontScaleMain * style.FontScaleDpi;
        uint key = ImGui.GetID(id);
        float widest = s_widestLabel.TryGetValue(key, out float w) ? w : 0f;
        float width = LabelColumnWidth(widest, minLabelWidth * scale, ImGui.GetContentRegionAvail().X, style.CellPadding.X);
        if (!ImGui.BeginTable(id, 2, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.NoSavedSettings))
            return false;
        s_currentTable = key;
        s_currentWidest = 0f;
        ImGui.TableSetupColumn("label", ImGuiTableColumnFlags.WidthFixed, width);
        ImGui.TableSetupColumn("value", ImGuiTableColumnFlags.WidthStretch);
        return true;
    }

    /// <summary>
    /// Width of the label column in pixels: the widest label plus the cell padding and a small gap, at least
    /// <paramref name="minimum"/> and at most <see cref="MaxLabelShare"/> of <paramref name="available"/> (but never below
    /// the minimum).
    /// </summary>
    internal static float LabelColumnWidth(float widestLabel, float minimum, float available, float cellPaddingX)
    {
        float wanted = widestLabel > 0f ? widestLabel + 2f * cellPaddingX + 6f : 0f;
        float cap = MathF.Max(minimum, available * MaxLabelShare);
        return MathF.Min(MathF.Max(minimum, wanted), cap);
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
        float labelWidth = label.Length == 0 ? 0f : ImGui.CalcTextSize(label).X;
        s_currentWidest = MathF.Max(s_currentWidest, labelWidth);
        if (ImGui.IsItemHovered() && labelWidth > ImGui.GetColumnWidth())
            ImGui.SetTooltip(label);
        ImGui.TableNextColumn();
        ImGui.SetNextItemWidth(-1);
    }

    /// <summary>A row with a localised label.</summary>
    public static void Row(Str label) => Row(Loc.Get(label));

    /// <summary>Ends the grid.</summary>
    public static void End()
    {
        s_widestLabel[s_currentTable] = s_currentWidest;
        ImGui.EndTable();
    }
}
