using System.Numerics;
using Hexa.NET.ImGui;

namespace Candela.App.Ui;

/// <summary>
/// The editor's dark theme (neutral greys like modern lighting/visualisation software, one warm accent that matches
/// the vivid orange selection outline) and the application of the UI scale. Colours are sRGB with alpha.
/// </summary>
internal static class Theme
{
    /// <summary>Accent colour (active tab overline, check marks, sliders, selection highlights).</summary>
    public static readonly Vector4 Accent = Rgb(0xFF8A1F);

    /// <summary>Main text colour.</summary>
    public static readonly Vector4 Text = Rgb(0xDCDDE0);

    /// <summary>Secondary text (hints, units, captions).</summary>
    public static readonly Vector4 TextMuted = Rgb(0x8E9198);

    /// <summary>Error severity colour.</summary>
    public static readonly Vector4 Error = Rgb(0xF0524F);

    /// <summary>Warning severity colour.</summary>
    public static readonly Vector4 Warning = Rgb(0xF2B33D);

    /// <summary>Note severity colour.</summary>
    public static readonly Vector4 Note = Rgb(0x5AA9F5);

    /// <summary>Success colour.</summary>
    public static readonly Vector4 Success = Rgb(0x4CC38A);

    /// <summary>Background of the viewport toolbars and overlays (translucent).</summary>
    public static readonly Vector4 OverlayBackground = new(0.08f, 0.085f, 0.095f, 0.78f);

    /// <summary>X axis colour (red), as in the renderer's grid.</summary>
    public static readonly Vector4 AxisX = Rgb(0xE5484D);

    /// <summary>Y axis colour (green).</summary>
    public static readonly Vector4 AxisY = Rgb(0x5BB85D);

    /// <summary>Z axis colour (blue).</summary>
    public static readonly Vector4 AxisZ = Rgb(0x3E8BF7);

    /// <summary>Background behind the docked panels (dock space, empty nodes).</summary>
    public static readonly Vector4 WorkspaceBackground = Rgb(0x121315);

    /// <summary>
    /// Applies colours and sizes for the user's UI scale <paramref name="uiScale"/> (≥ 1, the setting) on a monitor with
    /// content scale <paramref name="dpiScale"/>, with base font size <paramref name="fontSize"/> (pixels at 100 %).
    /// Called at start-up and whenever one of them changes; fonts re-rasterise on demand.
    /// </summary>
    public static void Apply(float uiScale, float dpiScale, float fontSize)
    {
        uiScale = UiScale.ClampScale(uiScale);
        dpiScale = UiScale.ClampDpi(dpiScale);
        var style = ImGui.GetStyle();
        ResetSizes(style);
        style.ScaleAllSizes(uiScale * dpiScale);
        style.FontSizeBase = UiScale.ClampFontSize(fontSize);
        style.FontScaleMain = uiScale;
        style.FontScaleDpi = dpiScale;
        ApplyColors(style);
    }

    /// <summary>ImGui colour (packed ABGR) of an sRGB colour with alpha.</summary>
    public static uint U32(Vector4 color) => ImGui.ColorConvertFloat4ToU32(color);

    /// <summary>An opaque colour from 0xRRGGBB.</summary>
    public static Vector4 Rgb(int rgb, float alpha = 1f) =>
        new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, alpha);

    /// <summary>The colour with another alpha.</summary>
    public static Vector4 WithAlpha(Vector4 color, float alpha) => new(color.X, color.Y, color.Z, alpha);

    private static void ResetSizes(ImGuiStylePtr style)
    {
        // Sizes at 100 %, scaled afterwards (ScaleAllSizes multiplies the current values, so start from scratch).
        style.WindowPadding = new Vector2(8, 8);
        style.WindowRounding = 4;
        style.WindowBorderSize = 1;
        style.WindowMinSize = new Vector2(64, 48);
        style.WindowTitleAlign = new Vector2(0f, 0.5f);
        style.WindowMenuButtonPosition = ImGuiDir.None;
        style.ChildRounding = 3;
        style.ChildBorderSize = 1;
        style.PopupRounding = 4;
        style.PopupBorderSize = 1;
        style.FramePadding = new Vector2(8, 4);
        style.FrameRounding = 3;
        style.FrameBorderSize = 0;
        style.ItemSpacing = new Vector2(8, 6);
        style.ItemInnerSpacing = new Vector2(6, 4);
        style.CellPadding = new Vector2(6, 3);
        style.TouchExtraPadding = Vector2.Zero;
        style.IndentSpacing = 16;
        style.ColumnsMinSpacing = 6;
        style.ScrollbarSize = 12;
        style.ScrollbarRounding = 6;
        style.GrabMinSize = 10;
        style.GrabRounding = 3;
        style.LogSliderDeadzone = 4;
        style.ImageBorderSize = 0;
        style.TabRounding = 4;
        style.TabBorderSize = 0;
        style.TabMinWidthBase = 1;
        style.TabMinWidthShrink = 80;
        style.TabCloseButtonMinWidthSelected = 0;
        style.TabCloseButtonMinWidthUnselected = 0;
        style.TabBarBorderSize = 1;
        style.TabBarOverlineSize = 2;
        style.SeparatorTextBorderSize = 1;
        style.SeparatorTextAlign = new Vector2(0f, 0.5f);
        style.SeparatorTextPadding = new Vector2(16, 3);
        style.DisplayWindowPadding = new Vector2(19, 19);
        style.DisplaySafeAreaPadding = new Vector2(3, 3);
        style.DockingSeparatorSize = 2;
        style.MouseCursorScale = 1;
        style.ButtonTextAlign = new Vector2(0.5f, 0.5f);
        style.SelectableTextAlign = new Vector2(0f, 0.5f);
        style.CurveTessellationTol = 1.25f;
        style.CircleTessellationMaxError = 0.3f;
        style.HoverDelayShort = 0.25f;
        style.HoverDelayNormal = 0.5f;
    }

    private static void ApplyColors(ImGuiStylePtr style)
    {
        var c = style.Colors;
        var windowBg = Rgb(0x1E1F22);
        var panelBg = Rgb(0x232428);
        var frame = Rgb(0x2C2E33);
        var frameHover = Rgb(0x363940);
        var frameActive = Rgb(0x40444C);
        var border = Rgb(0x34363C);
        var header = Rgb(0x33363D);
        var headerHover = Rgb(0x3C4048);
        var headerActive = WithAlpha(Accent, 0.40f);

        c[(int)ImGuiCol.Text] = Text;
        c[(int)ImGuiCol.TextDisabled] = Rgb(0x6C6F76);
        c[(int)ImGuiCol.WindowBg] = windowBg;
        c[(int)ImGuiCol.ChildBg] = Rgb(0x000000, 0f);
        c[(int)ImGuiCol.PopupBg] = Rgb(0x25272B, 0.98f);
        c[(int)ImGuiCol.Border] = border;
        c[(int)ImGuiCol.BorderShadow] = Rgb(0x000000, 0f);
        c[(int)ImGuiCol.FrameBg] = frame;
        c[(int)ImGuiCol.FrameBgHovered] = frameHover;
        c[(int)ImGuiCol.FrameBgActive] = frameActive;
        c[(int)ImGuiCol.TitleBg] = Rgb(0x18191C);
        c[(int)ImGuiCol.TitleBgActive] = Rgb(0x1C1D21);
        c[(int)ImGuiCol.TitleBgCollapsed] = Rgb(0x18191C, 0.8f);
        c[(int)ImGuiCol.MenuBarBg] = Rgb(0x1A1B1E);
        c[(int)ImGuiCol.ScrollbarBg] = Rgb(0x000000, 0f);
        c[(int)ImGuiCol.ScrollbarGrab] = Rgb(0x3A3D44);
        c[(int)ImGuiCol.ScrollbarGrabHovered] = Rgb(0x4A4E57);
        c[(int)ImGuiCol.ScrollbarGrabActive] = Rgb(0x5A5F69);
        c[(int)ImGuiCol.CheckMark] = Accent;
        c[(int)ImGuiCol.SliderGrab] = WithAlpha(Accent, 0.85f);
        c[(int)ImGuiCol.SliderGrabActive] = Accent;
        c[(int)ImGuiCol.Button] = Rgb(0x2F3237);
        c[(int)ImGuiCol.ButtonHovered] = Rgb(0x3B3F46);
        c[(int)ImGuiCol.ButtonActive] = Rgb(0x474C55);
        c[(int)ImGuiCol.Header] = header;
        c[(int)ImGuiCol.HeaderHovered] = headerHover;
        c[(int)ImGuiCol.HeaderActive] = headerActive;
        c[(int)ImGuiCol.Separator] = border;
        c[(int)ImGuiCol.SeparatorHovered] = WithAlpha(Accent, 0.6f);
        c[(int)ImGuiCol.SeparatorActive] = Accent;
        c[(int)ImGuiCol.ResizeGrip] = Rgb(0x000000, 0f);
        c[(int)ImGuiCol.ResizeGripHovered] = WithAlpha(Accent, 0.5f);
        c[(int)ImGuiCol.ResizeGripActive] = Accent;
        c[(int)ImGuiCol.InputTextCursor] = Text;
        c[(int)ImGuiCol.TabHovered] = Rgb(0x34373D);
        c[(int)ImGuiCol.Tab] = Rgb(0x1E1F22);
        c[(int)ImGuiCol.TabSelected] = panelBg;
        c[(int)ImGuiCol.TabSelectedOverline] = Accent;
        c[(int)ImGuiCol.TabDimmed] = Rgb(0x1B1C1F);
        c[(int)ImGuiCol.TabDimmedSelected] = Rgb(0x222327);
        c[(int)ImGuiCol.TabDimmedSelectedOverline] = WithAlpha(Accent, 0.35f);
        c[(int)ImGuiCol.DockingPreview] = WithAlpha(Accent, 0.45f);
        c[(int)ImGuiCol.DockingEmptyBg] = WorkspaceBackground;
        c[(int)ImGuiCol.PlotLines] = Rgb(0x9BA0A8);
        c[(int)ImGuiCol.PlotLinesHovered] = Accent;
        c[(int)ImGuiCol.PlotHistogram] = WithAlpha(Accent, 0.8f);
        c[(int)ImGuiCol.PlotHistogramHovered] = Accent;
        c[(int)ImGuiCol.TableHeaderBg] = Rgb(0x26282C);
        c[(int)ImGuiCol.TableBorderStrong] = border;
        c[(int)ImGuiCol.TableBorderLight] = Rgb(0x2A2C31);
        c[(int)ImGuiCol.TableRowBg] = Rgb(0x000000, 0f);
        c[(int)ImGuiCol.TableRowBgAlt] = Rgb(0xFFFFFF, 0.025f);
        c[(int)ImGuiCol.TextLink] = Note;
        c[(int)ImGuiCol.TextSelectedBg] = WithAlpha(Accent, 0.35f);
        c[(int)ImGuiCol.TreeLines] = border;
        c[(int)ImGuiCol.DragDropTarget] = Accent;
        c[(int)ImGuiCol.NavCursor] = Accent;
        c[(int)ImGuiCol.NavWindowingHighlight] = Rgb(0xFFFFFF, 0.7f);
        c[(int)ImGuiCol.NavWindowingDimBg] = Rgb(0x000000, 0.35f);
        c[(int)ImGuiCol.ModalWindowDimBg] = Rgb(0x000000, 0.5f);
    }
}
