using Hexa.NET.ImGui;

namespace Candela.App.Ui;

/// <summary>
/// A float slider that commits its value only when it is let go (or when a value typed after Ctrl+click is confirmed),
/// for settings whose change is expensive or moves the slider itself — the UI scale and the font size restyle the whole
/// interface, and applied while dragging they re-scaled the slider under the mouse at every frame. While the slider is
/// held, the value being dragged is kept in <c>pending</c> and shown instead of the setting.
/// </summary>
internal static class DeferredSlider
{
    /// <summary>
    /// Draws the slider <paramref name="id"/> over [<paramref name="min"/>, <paramref name="max"/>] showing
    /// <paramref name="pending"/> while it is held, otherwise <paramref name="current"/>.
    /// </summary>
    /// <param name="id">ImGui label / id of the slider.</param>
    /// <param name="pending">The value being dragged; null while the slider is not held (kept by the caller between frames).</param>
    /// <param name="current">The setting's current value.</param>
    /// <param name="min">Left end of the slider.</param>
    /// <param name="max">Right end of the slider.</param>
    /// <param name="format">Number format of the slider (an ImGui printf format of the editor's own).</param>
    /// <param name="committed">The value to apply when the method returns true; <paramref name="current"/> otherwise.</param>
    /// <returns>True in the frame the slider was let go after a change.</returns>
    public static bool Float(string id, ref float? pending, float current, float min, float max, string format, out float committed)
    {
        float value = pending ?? current;
        ImGui.SliderFloat(id, ref value, min, max, format);
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            pending = null;
            committed = Math.Clamp(float.IsFinite(value) ? value : current, min, max);
            return true;
        }

        pending = ImGui.IsItemActive() ? value : null;
        committed = current;
        return false;
    }
}
