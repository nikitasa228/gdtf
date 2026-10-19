using System.Numerics;
using Candela.Electrical;
using Hexa.NET.ImGui;

namespace Candela.App.Ui;

/// <summary>
/// The list of cable types of the cable tool (Tools → Cable, the type chip in the viewport): every type of the
/// catalogue grouped as data, power and multicore, each with its legend colour, the drawing of its connector and its
/// rating, in the UI language. Returns the chosen id.
/// </summary>
internal static class CableTypeMenu
{
    private static readonly SignalKind[] Groups = [SignalKind.Dmx512, SignalKind.Power, SignalKind.Multicore];

    /// <summary>
    /// Draws the list inside an open popup or menu; returns the id of the clicked type, or null. <paramref name="current"/>
    /// is shown as selected.
    /// </summary>
    public static string? Draw(CableCatalog catalog, string? current, float scale)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        string? chosen = null;
        float icon = ImGui.GetTextLineHeight() * 1.25f;
        bool first = true;
        foreach (var group in Groups)
        {
            bool header = false;
            foreach (var type in catalog.All)
            {
                if (GroupOf(type) != group)
                    continue;
                if (!header)
                {
                    if (!first)
                        ImGui.Spacing();
                    ImGui.TextDisabled(GroupLabel(group));
                    header = true;
                    first = false;
                }

                bool selected = string.Equals(type.Id, current, StringComparison.OrdinalIgnoreCase);
                ImGui.PushID(type.Id);
                var start = ImGui.GetCursorScreenPos();
                float height = MathF.Max(icon, ImGui.GetTextLineHeight()) + 2f * scale;
                if (ImGui.Selectable("##type", selected, ImGuiSelectableFlags.None, new Vector2(MathF.Max(300f * scale, ImGui.GetContentRegionAvail().X), height)))
                    chosen = type.Id;
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(Tooltip(type));
                var dl = ImGui.GetWindowDrawList();
                var (r, g, b) = type.ColorSrgb;
                float swatchH = ImGui.GetTextLineHeight() * 0.42f;
                var swatchMin = start + new Vector2(2f * scale, (height - swatchH) * 0.5f);
                dl.AddRectFilled(swatchMin, swatchMin + new Vector2(icon * 0.9f, swatchH), Theme.U32(new Vector4(r, g, b, 1f)), 2f);
                ConnectorIcons.Draw(dl, start + new Vector2(icon + 6f * scale, (height - icon) * 0.5f), icon, type.ConnectorA.IllustrationKey);
                dl.AddText(start + new Vector2(2f * icon + 14f * scale, (height - ImGui.GetTextLineHeight()) * 0.5f), Theme.U32(Theme.Text), CableLoc.CableName(type));
                ImGui.PopID();
            }
        }

        return chosen;
    }

    /// <summary>The group a type is listed under: data cables, single power circuits, multicores.</summary>
    internal static SignalKind GroupOf(CableType type) => type.Signal switch
    {
        SignalKind.Dmx512 or SignalKind.Ethernet => SignalKind.Dmx512,
        SignalKind.Multicore => SignalKind.Multicore,
        _ => SignalKind.Power,
    };

    private static string GroupLabel(SignalKind group) => group switch
    {
        SignalKind.Dmx512 => CableLoc.Get(CableStr.CableGroupData),
        SignalKind.Multicore => CableLoc.Get(CableStr.CableGroupMulticore),
        _ => CableLoc.Get(CableStr.CableGroupPower),
    };

    private static string Tooltip(CableType type)
    {
        string ends = type.ConnectorA.Id == type.ConnectorB.Id ? type.ConnectorA.DisplayName : $"{type.ConnectorA.DisplayName} → {type.ConnectorB.DisplayName}";
        var units = StageUnits.Current;
        string rating = type.RatedCurrentA is { } a ? $" · {a:0} {units.Ampere}" : "";
        string run = type.MaxRunM is { } m ? $" · ≤ {m:0} {units.Metre}" : "";
        return string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{CableLoc.CableName(type)}\n{ends}{rating}{run} · {type.WeightKgPerM:0.###} {units.Kilogram}/{units.Metre}");
    }
}
