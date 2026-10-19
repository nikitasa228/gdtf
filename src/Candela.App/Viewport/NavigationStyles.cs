namespace Candela.App.Viewport;

/// <summary>What a mouse drag in the viewport does.</summary>
internal enum NavigationOperation
{
    /// <summary>Nothing (the button belongs to selection / tools).</summary>
    None,

    /// <summary>Rotate the camera around its target.</summary>
    Orbit,

    /// <summary>Move the camera and target parallel to the view plane.</summary>
    Pan,

    /// <summary>Dolly towards / away from the target (vertical drag).</summary>
    Zoom,

    /// <summary>Look around from the camera position; W/A/S/D/Q/E move while the button is held.</summary>
    Fly,
}

/// <summary>Mouse buttons used for navigation.</summary>
internal enum NavigationButton
{
    /// <summary>Left button.</summary>
    Left,

    /// <summary>Right button.</summary>
    Right,

    /// <summary>Middle button (wheel click).</summary>
    Middle,
}

/// <summary>Modifier keys held during a drag.</summary>
[Flags]
internal enum NavigationModifiers
{
    /// <summary>No modifier.</summary>
    None = 0,

    /// <summary>Shift.</summary>
    Shift = 1,

    /// <summary>Ctrl (Cmd on macOS).</summary>
    Ctrl = 2,

    /// <summary>Alt (Option on macOS).</summary>
    Alt = 4,
}

/// <summary>
/// Viewport navigation styles (<c>AppSettings.NavigationStyle</c>): which mouse button + modifiers orbit, pan, zoom and
/// fly. The left button without modifiers always selects (click, Shift+click, box) in every style.
/// <list type="table">
/// <item><term>Candela</term><description>MMB orbit · Shift+MMB pan · Ctrl+MMB zoom · RMB fly (+WASD) · Alt+LMB orbit · Alt+Shift+LMB pan</description></item>
/// <item><term>Blender</term><description>MMB orbit · Shift+MMB pan · Ctrl+MMB zoom · Shift+RMB fly</description></item>
/// <item><term>Maya</term><description>Alt+LMB orbit · Alt+MMB pan · Alt+RMB zoom · RMB fly</description></item>
/// </list>
/// </summary>
internal static class NavigationStyles
{
    /// <summary>The default style.</summary>
    public const string Candela = "Candela";

    /// <summary>Blender-like.</summary>
    public const string Blender = "Blender";

    /// <summary>Maya / 3ds Max-like.</summary>
    public const string Maya = "Maya";

    /// <summary>All styles, in the order of the settings dropdown.</summary>
    public static readonly string[] All = { Candela, Blender, Maya };

    /// <summary>A known style name (case-insensitive) or <see cref="Candela"/>.</summary>
    public static string Normalize(string? style)
    {
        foreach (var s in All)
            if (string.Equals(s, style?.Trim(), StringComparison.OrdinalIgnoreCase))
                return s;
        return Candela;
    }

    /// <summary>The navigation started by pressing <paramref name="button"/> with <paramref name="modifiers"/>.</summary>
    public static NavigationOperation Resolve(string style, NavigationButton button, NavigationModifiers modifiers)
    {
        bool alt = (modifiers & NavigationModifiers.Alt) != 0;
        bool shift = (modifiers & NavigationModifiers.Shift) != 0;
        bool ctrl = (modifiers & NavigationModifiers.Ctrl) != 0;
        switch (Normalize(style))
        {
            case Blender:
                return button switch
                {
                    NavigationButton.Middle when ctrl => NavigationOperation.Zoom,
                    NavigationButton.Middle when shift => NavigationOperation.Pan,
                    NavigationButton.Middle => NavigationOperation.Orbit,
                    NavigationButton.Right when shift => NavigationOperation.Fly,
                    _ => NavigationOperation.None,
                };
            case Maya:
                return button switch
                {
                    NavigationButton.Left when alt => NavigationOperation.Orbit,
                    NavigationButton.Middle when alt => NavigationOperation.Pan,
                    NavigationButton.Middle => NavigationOperation.Pan,
                    NavigationButton.Right when alt => NavigationOperation.Zoom,
                    NavigationButton.Right => NavigationOperation.Fly,
                    _ => NavigationOperation.None,
                };
            default:
                return button switch
                {
                    NavigationButton.Left when alt && shift => NavigationOperation.Pan,
                    NavigationButton.Left when alt => NavigationOperation.Orbit,
                    NavigationButton.Middle when ctrl => NavigationOperation.Zoom,
                    NavigationButton.Middle when shift => NavigationOperation.Pan,
                    NavigationButton.Middle => NavigationOperation.Orbit,
                    NavigationButton.Right => NavigationOperation.Fly,
                    _ => NavigationOperation.None,
                };
        }
    }
}
