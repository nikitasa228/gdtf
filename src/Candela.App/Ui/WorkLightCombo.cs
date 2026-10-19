using Candela.Engine;
using Hexa.NET.ImGui;

namespace Candela.App.Ui;

/// <summary>
/// The Off / Auto / On choice of a work light (<see cref="WorkLightMode"/>) as a drop-down with a hint per mode: the
/// Preview panel's work light and the Render panel's request. Labels come from <see cref="Loc"/> (no allocation).
/// </summary>
internal static class WorkLightCombo
{
    private static readonly WorkLightMode[] Modes = { WorkLightMode.Off, WorkLightMode.Auto, WorkLightMode.On };

    /// <summary>The short label of a mode ("Off", "Auto", "On").</summary>
    public static Str Label(WorkLightMode mode) => mode switch
    {
        WorkLightMode.Off => Str.WorkLightOff,
        WorkLightMode.On => Str.WorkLightOn,
        _ => Str.WorkLightAuto,
    };

    /// <summary>What a mode does, for its tooltip.</summary>
    public static Str Hint(WorkLightMode mode) => mode switch
    {
        WorkLightMode.Off => Str.WorkLightOffHint,
        WorkLightMode.On => Str.WorkLightOnHint,
        _ => Str.WorkLightAutoHint,
    };

    /// <summary>Draws the drop-down (fills the item width); true with <paramref name="selected"/> when another mode was chosen.</summary>
    public static bool Draw(string id, WorkLightMode current, out WorkLightMode selected)
    {
        selected = current;
        bool changed = false;
        if (ImGui.BeginCombo(id, Loc.Get(Label(current))))
        {
            foreach (var mode in Modes)
            {
                if (ImGui.Selectable(Loc.Get(Label(mode)), mode == current) && mode != current)
                {
                    selected = mode;
                    changed = true;
                }

                if (ImGui.IsItemHovered())
                    SafeText.Tooltip(Loc.Get(Hint(mode)));
            }

            ImGui.EndCombo();
        }

        return changed;
    }
}
