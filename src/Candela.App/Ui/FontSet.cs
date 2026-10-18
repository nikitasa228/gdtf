using System.Reflection;
using Candela.App.Diagnostics;
using Hexa.NET.ImGui;

namespace Candela.App.Ui;

/// <summary>
/// The UI fonts: Inter Regular (body) and Inter SemiBold (headings) — both cover Latin, Cyrillic and Greek — each with
/// the Lucide icon font merged in, so every label can mix text and <see cref="Icons"/>. Dear ImGui 1.92 rasterises
/// glyphs on demand at the size in use, so changing the UI scale only changes <c>style.FontSizeBase</c> /
/// <c>FontScaleMain</c> and the atlas re-renders by itself (no manual rebuild).
/// </summary>
internal sealed unsafe class FontSet
{
    /// <summary>Base font size in pixels at 100 % when the settings hold no valid value.</summary>
    public const float DefaultSize = 15f;

    private FontSet(ImFontPtr regular, ImFontPtr heading, bool bundled)
    {
        Regular = regular;
        Heading = heading;
        IsBundled = bundled;
    }

    /// <summary>Body text (the default font).</summary>
    public ImFontPtr Regular { get; }

    /// <summary>Semi-bold headings and emphasis.</summary>
    public ImFontPtr Heading { get; }

    /// <summary>False when the bundled fonts were missing and ImGui's built-in font is used instead (no Cyrillic, no icons).</summary>
    public bool IsBundled { get; }

    /// <summary>Loads the embedded fonts into the current context's atlas.</summary>
    public static FontSet Load(AppLog log)
    {
        var io = ImGui.GetIO();
        var regularData = ReadResource("Fonts/Inter-Regular.otf");
        var headingData = ReadResource("Fonts/Inter-SemiBold.otf");
        var iconData = ReadResource("Fonts/lucide.ttf");
        if (regularData is null || headingData is null || iconData is null)
        {
            log.Warn("UI", "Bundled fonts are missing; falling back to ImGui's built-in font (no Cyrillic, no icons).");
            var fallback = io.Fonts.AddFontDefault();
            return new FontSet(fallback, fallback, false);
        }

        var regular = AddWithIcons(io.Fonts, regularData, iconData, "Inter Regular");
        var heading = AddWithIcons(io.Fonts, headingData, iconData, "Inter SemiBold");
        io.FontDefault = regular;
        return new FontSet(regular, heading, true);
    }

    /// <summary>Reads an embedded resource of this assembly (null when absent).</summary>
    internal static byte[]? ReadResource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
        if (stream is null)
            return null;
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static ImFontPtr AddWithIcons(ImFontAtlasPtr atlas, byte[] textFont, byte[] iconFont, string name)
    {
        var text = ImGui.ImFontConfig();
        // Inter has its own glyphs (alternates) in the private use area where the icon font lives: exclude that range from
        // the text font so the merged icons win. ImGui copies the list.
        uint* exclude = stackalloc uint[] { Icons.RangeFirst, 0xF8FF, 0 };
        try
        {
            text.FontDataOwnedByAtlas = true;
            text.OversampleH = 2;
            text.OversampleV = 1;
            text.GlyphExcludeRanges = exclude;
            SetName(text, name);
            var font = AddFromMemory(atlas, textFont, text);

            var icons = ImGui.ImFontConfig();
            try
            {
                icons.MergeMode = true;
                icons.FontDataOwnedByAtlas = true;
                icons.PixelSnapH = true;
                // Icons are drawn on the em box; nudge them onto the text baseline and keep them square.
                icons.GlyphOffset = new System.Numerics.Vector2(0f, 2f);
                icons.GlyphMinAdvanceX = DefaultSize;
                SetName(icons, "Lucide");
                AddFromMemory(atlas, iconFont, icons);
            }
            finally
            {
                icons.Destroy();
            }

            return font;
        }
        finally
        {
            text.Destroy();
        }
    }

    private static ImFontPtr AddFromMemory(ImFontAtlasPtr atlas, byte[] data, ImFontConfigPtr config)
    {
        // The atlas takes ownership and frees the copy with ImGui's allocator.
        void* copy = ImGui.MemAlloc((nuint)data.Length);
        new ReadOnlySpan<byte>(data).CopyTo(new Span<byte>(copy, data.Length));
        return atlas.AddFontFromMemoryTTF(copy, data.Length, DefaultSize, config, null);
    }

    private static void SetName(ImFontConfigPtr config, string name)
    {
        var dst = config.Name;
        int n = Math.Min(name.Length, dst.Length - 1);
        for (int i = 0; i < n; i++)
            dst[i] = (byte)(name[i] < 128 ? name[i] : '?');
        dst[n] = 0;
    }
}
