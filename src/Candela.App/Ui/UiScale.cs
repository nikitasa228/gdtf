namespace Candela.App.Ui;

/// <summary>
/// Rules for the interface scale: the user setting (<c>AppSettings.UiScale</c>, 100 % and above, requirement) times the
/// monitor's content scale reported by GLFW (DPI awareness), and the base font size.
/// </summary>
internal static class UiScale
{
    /// <summary>Smallest UI scale (100 %).</summary>
    public const float Min = 1f;

    /// <summary>Largest UI scale (400 %).</summary>
    public const float Max = 4f;

    /// <summary>Smallest base font size, pixels at 100 %.</summary>
    public const float MinFontSize = 11f;

    /// <summary>Largest base font size, pixels at 100 %.</summary>
    public const float MaxFontSize = 28f;

    /// <summary>Steps offered by the settings window (100 % … 300 %).</summary>
    public static readonly float[] Presets = { 1f, 1.1f, 1.25f, 1.5f, 1.75f, 2f, 2.5f, 3f };

    /// <summary>The user scale clamped to [100 %, 400 %]; NaN/∞ → 100 %.</summary>
    public static float ClampScale(float scale) => float.IsFinite(scale) ? Math.Clamp(scale, Min, Max) : Min;

    /// <summary>A monitor content scale clamped to [0.5, 4]; NaN/∞/≤ 0 → 1.</summary>
    public static float ClampDpi(float dpi) => float.IsFinite(dpi) && dpi > 0f ? Math.Clamp(dpi, 0.5f, 4f) : 1f;

    /// <summary>The base font size clamped to [11, 28] px; invalid → 15 px.</summary>
    public static float ClampFontSize(float size) =>
        float.IsFinite(size) && size > 0f ? Math.Clamp(size, MinFontSize, MaxFontSize) : FontSet.DefaultSize;

    /// <summary>Total factor applied to sizes: user scale × monitor content scale.</summary>
    public static float Effective(float userScale, float contentScale) => ClampScale(userScale) * ClampDpi(contentScale);

    /// <summary>Percent label of a scale ("125 %").</summary>
    public static string Percent(float scale) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{MathF.Round(ClampScale(scale) * 100f):0} %");
}
