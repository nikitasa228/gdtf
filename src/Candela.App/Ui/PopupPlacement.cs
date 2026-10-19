using System.Numerics;

namespace Candela.App.Ui;

/// <summary>Where menus opened from a button go, so they stay inside the window the button is in.</summary>
internal static class PopupPlacement
{
    /// <summary>
    /// Position and pivot (for <c>SetNextWindowPos</c>) of a menu opened from a button with the screen rectangle
    /// (<paramref name="buttonMin"/>, <paramref name="buttonMax"/>) in a viewport at <paramref name="viewportPos"/> of
    /// <paramref name="viewportSize"/>: below the button with its left edge on the button's left edge, or — for a button in
    /// the right half — its right edge on the button's right edge, so the menu opens towards the inside of the window;
    /// a button in the lower half opens the menu upwards. A menu opened at the mouse next to the right edge would leave
    /// the main window (and, with multi-viewport, become a separate OS window).
    /// </summary>
    public static (Vector2 Position, Vector2 Pivot) ForButton(Vector2 buttonMin, Vector2 buttonMax, Vector2 viewportPos, Vector2 viewportSize)
    {
        var centre = viewportPos + viewportSize * 0.5f;
        bool right = (buttonMin.X + buttonMax.X) * 0.5f > centre.X;
        bool below = (buttonMin.Y + buttonMax.Y) * 0.5f > centre.Y;
        float x = right ? buttonMax.X : buttonMin.X;
        float y = below ? buttonMin.Y : buttonMax.Y;
        return (new Vector2(x, y), new Vector2(right ? 1f : 0f, below ? 1f : 0f));
    }

    /// <summary>
    /// Position and pivot of a popup opened at a point (the mouse) in a viewport at <paramref name="viewportPos"/> of
    /// <paramref name="viewportSize"/>: <paramref name="gap"/> pixels beside the point, towards the inside of the
    /// window — to the right and below in the upper left quarter, to the left and above in the lower right one — so a
    /// popup opened near an edge stays in the main window.
    /// </summary>
    public static (Vector2 Position, Vector2 Pivot) AtPoint(Vector2 point, float gap, Vector2 viewportPos, Vector2 viewportSize)
    {
        var centre = viewportPos + viewportSize * 0.5f;
        bool left = point.X > centre.X;
        bool above = point.Y > centre.Y;
        return (new Vector2(left ? point.X - gap : point.X + gap, above ? point.Y - gap : point.Y + gap), new Vector2(left ? 1f : 0f, above ? 1f : 0f));
    }

    /// <summary>
    /// The first size of a dialog window: <paramref name="desired"/> (already scaled), but at most 92 % of the main
    /// window's work area. A dialog larger than the main window would hang over its edge and, with multi-viewport
    /// enabled, turn into a separate OS window.
    /// </summary>
    public static Vector2 FitInWindow(Vector2 desired, Vector2 workSize) => Vector2.Min(desired, workSize * 0.92f);
}
