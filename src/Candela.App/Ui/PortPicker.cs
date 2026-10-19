using System.Numerics;
using Candela.App.Editing;
using Candela.Electrical;
using Hexa.NET.ImGui;

namespace Candela.App.Ui;

/// <summary>
/// The port picker of the cable tool (requirement: "если у устройства есть несколько возможных входов/выходов под этот
/// тип кабеля, то при нажатии около курсора появится меню выбора"): one row per port of the clicked device with the
/// drawing of its connector, its name, direction and connector, and what already plugs into it; ports the cable fits
/// can be chosen (with the mouse or the keys 1–9), the others say why not (in use, output to output, wrong signal,
/// keyed connector).
/// </summary>
internal static class PortPicker
{
    /// <summary>Draws the picker's rows inside an open popup; returns the chosen port name, or null.</summary>
    public static string? Draw(PortPick pick, CableType type, float scale)
    {
        ArgumentNullException.ThrowIfNull(pick);
        ArgumentNullException.ThrowIfNull(type);
        string? chosen = null;
        ImGui.TextColored(Theme.Accent, Icons.Cable + "  " + CableLoc.Format(CableStr.PickerTitle, CableLoc.CableName(type), pick.DeviceName));
        ImGui.Separator();
        float icon = MathF.Round(ImGui.GetTextLineHeightWithSpacing() * 2f);
        float width = MathF.Max(340f * scale, ImGui.CalcTextSize(pick.DeviceName).X + icon * 2f);
        int number = 0;
        bool any = false;
        for (int i = 0; i < pick.Candidates.Count; i++)
        {
            var c = pick.Candidates[i];
            ImGui.PushID(i);
            var start = ImGui.GetCursorScreenPos();
            string key = "";
            if (c.Usable)
            {
                number++;
                any = true;
                if (number <= 9)
                    key = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            ImGui.BeginDisabled(!c.Usable);
            bool clicked = ImGui.Selectable("##port", false, ImGuiSelectableFlags.None, new Vector2(width, icon));
            ImGui.EndDisabled();
            if (c.Usable && (clicked || number <= 9 && ImGui.IsKeyPressed(ImGuiKey.Key1 + (number - 1), false)))
                chosen = c.Port.Name;
            var dl = ImGui.GetWindowDrawList();
            float alpha = c.Usable ? 1f : 0.45f;
            ConnectorIcons.Draw(dl, start, icon, c.Port.Connector.IllustrationKey);
            if (!c.Usable)
                dl.AddRectFilled(start, start + new Vector2(icon), Theme.U32(Theme.WithAlpha(Theme.Rgb(0x1A1B1E), 0.55f)), 4f);
            float x = start.X + icon + 8f * scale;
            float line = ImGui.GetTextLineHeight();
            string title = key.Length > 0 ? $"{key}   {c.Port.Name}" : c.Port.Name;
            dl.AddText(new Vector2(x, start.Y + 1f * scale), Theme.U32(Theme.WithAlpha(Theme.Text, alpha)), title);
            dl.AddText(new Vector2(x, start.Y + line + 2f * scale), Theme.U32(Theme.WithAlpha(Theme.TextMuted, alpha)), Detail(c));
            if (Status(c) is { } status)
            {
                var color = c.Occupant is not null ? Theme.TextMuted : c.Usable ? Theme.Warning : Theme.Error;
                var size = ImGui.CalcTextSize(status.Text);
                float right = start.X + width - size.X - 4f * scale;
                dl.AddText(new Vector2(MathF.Max(x + 120f * scale, right), start.Y + 1f * scale), Theme.U32(color), status.Text);
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) && status.Tooltip is { } tip)
                    ImGui.SetTooltip(tip);
            }

            ImGui.PopID();
        }

        if (!any)
        {
            ImGui.Spacing();
            ImGui.TextColored(Theme.Warning, CableLoc.Get(CableStr.PickerNoFreePort));
        }

        ImGui.Separator();
        ImGui.TextDisabled(CableLoc.Get(CableStr.PickerHint));
        return chosen;
    }

    /// <summary>"XLR 5-pin · DMX512 input · U1".</summary>
    internal static string Detail(PortCandidate c)
    {
        var port = c.Port;
        string direction = CableLoc.Get(port.Direction switch
        {
            PortDirection.Input => CableStr.PickerInput,
            PortDirection.Output => CableStr.PickerOutput,
            _ => CableStr.PickerBidirectional,
        });
        string extra = port.Universe is { } u
            ? port.UniverseCount > 1 ? $" · U{u}–{u + port.UniverseCount - 1}" : $" · U{u}"
            : port.BreakerA is { } breaker ? $" · {breaker:0} {StageUnits.Current.Ampere}" : "";
        return $"{CableLoc.ConnectorName(port.Connector)} · {direction}{extra}";
    }

    /// <summary>What keeps a port from being chosen (or the adapter it needs), with the full reason for the tooltip.</summary>
    internal static (string Text, string? Tooltip)? Status(PortCandidate c)
    {
        if (c.Occupant is { } cable)
            return (CableLoc.Format(CableStr.PickerConnected, cable.DisplayLabel), null);
        if (c.WrongDirection)
        {
            string reason = CableTool.DirectionReason(c.Port);
            return (CableLoc.Format(CableStr.PickerIncompatible, reason), reason);
        }

        return c.Check.Match switch
        {
            ConnectorMatch.Incompatible => (CableLoc.Format(CableStr.PickerIncompatible, Short(c.Check.Reason)), c.Check.Reason),
            ConnectorMatch.NeedsAdapter => (CableLoc.Format(CableStr.PickerAdapter, Short(c.Check.Reason)), c.Check.Reason),
            _ => null,
        };
    }

    private static string Short(string reason) => reason.Length <= 38 ? reason : reason[..36] + "…";
}
