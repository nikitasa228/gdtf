using Hexa.NET.ImGui;

namespace Candela.App.Ui;

/// <summary>
/// Rules for the interface scale: the user setting (<c>AppSettings.UiScale</c>, 100 % and above, requirement) times the
/// monitor's content scale reported by GLFW (DPI awareness), and the base font size. The scale actually applied is
/// bounded twice — by a largest readable text height and by what the glyph atlas texture of the GPU can hold — so no
/// combination of settings can make Dear ImGui rasterise more glyphs than its atlas can pack (its FreeType loader then
/// aborts the process).
/// </summary>
internal static class UiScale
{
    /// <summary>Smallest UI scale (100 %).</summary>
    public const float Min = 1f;

    /// <summary>Largest UI scale (300 %, the end of the settings slider and of the presets).</summary>
    public const float Max = 3f;

    /// <summary>Smallest base font size, pixels at 100 %.</summary>
    public const float MinFontSize = 11f;

    /// <summary>Largest base font size, pixels at 100 %.</summary>
    public const float MaxFontSize = 28f;

    /// <summary>
    /// Largest body text height in pixels at 100 % monitor scale (base font size × UI scale): 64 px is 300 % of a 21 px
    /// font, about 16 lines on a 1080-pixel screen. Larger settings are applied at this size.
    /// </summary>
    public const float MaxTextPixels = 64f;

    /// <summary>
    /// Largest side of the font atlas texture the editor lets Dear ImGui grow to (pixels): Dear ImGui's own default,
    /// lowered to the GPU's <c>maxImageDimension2D</c> where that is smaller. An RGBA atlas of this size takes 256 MB.
    /// </summary>
    public const int MaxAtlasSize = 8192;

    /// <summary>
    /// Atlas side pixels per pixel of physical body text height: body text of <c>s / 160</c> pixels at most in an atlas
    /// of side <c>s</c> (51 px in 8192, 25 px in 4096). Measured with the editor's fonts: every glyph of ASCII,
    /// Latin-1, Latin Extended-A, Cyrillic and 400 icons, in both fonts at the body size and the five heading sizes the
    /// panels push (1.1 to 2 ×), packs into about 11 000 × h² pixels for body text of h pixels. Dear ImGui drops the
    /// glyphs of an older size only after two frames without them, so two such sets must fit, with a margin for packing
    /// waste: 2 × 11 000 × h² ≤ 0.85 × s². When a glyph does not fit, Dear ImGui's FreeType loader aborts the process
    /// ("Out of texture memory"), so this bound is what keeps every scale safe.
    /// </summary>
    public const float AtlasPixelsPerTextPixel = 160f;

    /// <summary>Steps offered by the settings window (100 % … 300 %).</summary>
    public static readonly float[] Presets = { 1f, 1.1f, 1.25f, 1.5f, 1.75f, 2f, 2.5f, 3f };

    /// <summary>The user scale clamped to [100 %, 300 %]; NaN/∞ → 100 %.</summary>
    public static float ClampScale(float scale) => float.IsFinite(scale) ? Math.Clamp(scale, Min, Max) : Min;

    /// <summary>A monitor content scale clamped to [0.5, 4]; NaN/∞/≤ 0 → 1.</summary>
    public static float ClampDpi(float dpi) => float.IsFinite(dpi) && dpi > 0f ? Math.Clamp(dpi, 0.5f, 4f) : 1f;

    /// <summary>The base font size clamped to [11, 28] px; invalid → 15 px.</summary>
    public static float ClampFontSize(float size) =>
        float.IsFinite(size) && size > 0f ? Math.Clamp(size, MinFontSize, MaxFontSize) : FontSet.DefaultSize;

    /// <summary>Total factor applied to sizes: user scale × monitor content scale (before the text limits).</summary>
    public static float Effective(float userScale, float contentScale) => ClampScale(userScale) * ClampDpi(contentScale);

    /// <summary>
    /// The atlas side (pixels) for a GPU whose largest 2D image side is <paramref name="maxImageDimension2D"/>:
    /// <see cref="MaxAtlasSize"/>, or the device limit when smaller (never below Vulkan's guaranteed 4096).
    /// </summary>
    public static int AtlasSize(uint maxImageDimension2D) =>
        (int)Math.Clamp(maxImageDimension2D == 0 ? (uint)MaxAtlasSize : maxImageDimension2D, 4096u, (uint)MaxAtlasSize);

    /// <summary>
    /// The user scale that is applied for <paramref name="userScale"/> on a monitor of content scale
    /// <paramref name="dpiScale"/> with base font <paramref name="fontSize"/> and a glyph atlas of side
    /// <paramref name="atlasSize"/>: the clamped setting, lowered when the body text would be taller than
    /// <see cref="MaxTextPixels"/> (at 100 % monitor scale) or than the atlas can hold
    /// (<paramref name="atlasSize"/> / <see cref="AtlasPixelsPerTextPixel"/> physical pixels). Never below a quarter, so
    /// even a pathological combination of monitor scale and atlas stays legible.
    /// </summary>
    public static float Applied(float userScale, float dpiScale, float fontSize, int atlasSize)
    {
        float user = ClampScale(userScale);
        float dpi = ClampDpi(dpiScale);
        float font = ClampFontSize(fontSize);
        int atlas = atlasSize > 0 ? atlasSize : MaxAtlasSize;
        float byText = MaxTextPixels / font;
        float byAtlas = atlas / AtlasPixelsPerTextPixel / (font * dpi);
        return Math.Max(0.25f, Math.Min(user, Math.Min(byText, byAtlas)));
    }

    /// <summary>
    /// Factor of the current frame's sizes — the applied user scale × the monitor scale of the window being drawn —
    /// read from the Dear ImGui style (what <see cref="Theme.Apply"/> set), so panels scale exactly like the fonts.
    /// </summary>
    public static float Frame()
    {
        var style = ImGui.GetStyle();
        return style.FontScaleMain * style.FontScaleDpi;
    }

    /// <summary>Percent label of a scale ("125 %").</summary>
    public static string Percent(float scale) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{MathF.Round(ClampScale(scale) * 100f):0} %");

    /// <summary>Percent label of an applied scale, which may lie below 100 % ("90 %").</summary>
    public static string AppliedPercent(float scale) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{MathF.Round(scale * 100f):0} %");
}
