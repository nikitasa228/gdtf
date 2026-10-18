using System.Text.Json;
using System.Text.Json.Serialization;

namespace Candela.App.Docking;

/// <summary>A layout file could not be read.</summary>
internal sealed class LayoutFormatException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>How a viewport panel looked when the layout was saved.</summary>
internal sealed class ViewportLayout
{
    /// <summary>Panel id ("Viewport1").</summary>
    public string Id { get; set; } = "";

    /// <summary>Camera preset: "Perspective" (free camera) or a fixed view ("Top", "Front", "Right", "Left", "Back", "Bottom").</summary>
    public string View { get; set; } = "Perspective";

    /// <summary>"Perspective" or "Orthographic".</summary>
    public string Projection { get; set; } = "Perspective";

    /// <summary>"Wireframe", "Colorless" or "Full".</summary>
    public string Shading { get; set; } = "Full";

    /// <summary>"None", "Lux", "Noise" or "Cables".</summary>
    public string Overlay { get; set; } = "None";

    /// <summary>Ground grid on.</summary>
    public bool Grid { get; set; } = true;

    /// <summary>FPS / frame time readout on.</summary>
    public bool Stats { get; set; } = true;
}

/// <summary>
/// A saved workspace layout (JSON, in <c>ConfigPaths.LayoutsDir</c>): the dock tree and window placement (Dear ImGui's
/// ini text, which also carries per-node flags such as the hidden tab bar of icon-column groups), the tab style of each
/// dock node, which panels are open and the state of each viewport.
/// </summary>
internal sealed class LayoutFile
{
    /// <summary>Current format version.</summary>
    public const int CurrentFormat = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Format version of the file.</summary>
    public int Format { get; set; } = CurrentFormat;

    /// <summary>Display name of the layout.</summary>
    public string Name { get; set; } = "";

    /// <summary>Ids of the open panels.</summary>
    public List<string> Panels { get; set; } = new();

    /// <summary>State of every viewport panel (open or not).</summary>
    public List<ViewportLayout> Viewports { get; set; } = new();

    /// <summary>Tab styles per dock node (<see cref="TabStyleMap.Serialize"/>).</summary>
    public string TabStyles { get; set; } = "";

    /// <summary>Dear ImGui's settings (windows, dock nodes) as saved by <c>SaveIniSettingsToMemory</c>.</summary>
    public string ImGui { get; set; } = "";

    /// <summary>Serialises to JSON.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, Options);

    /// <summary>Parses JSON; throws <see cref="LayoutFormatException"/> for damaged or newer files.</summary>
    public static LayoutFile FromJson(string json)
    {
        LayoutFile? file;
        try
        {
            file = JsonSerializer.Deserialize<LayoutFile>(json, Options);
        }
        catch (JsonException e)
        {
            throw new LayoutFormatException($"The layout file is damaged: {e.Message}", e);
        }

        if (file is null)
            throw new LayoutFormatException("The layout file is empty.");
        if (file.Format > CurrentFormat)
            throw new LayoutFormatException($"The layout was saved by a newer version (format {file.Format}).");
        file.Panels ??= new List<string>();
        file.Viewports ??= new List<ViewportLayout>();
        file.Panels = file.Panels.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct().ToList();
        file.Viewports = file.Viewports.Where(v => v is not null && IsViewportId(v.Id)).GroupBy(v => v.Id).Select(g => g.First()).ToList();
        file.TabStyles ??= "";
        file.ImGui ??= "";
        file.Name ??= "";
        return file;
    }

    /// <summary>True for viewport panel ids ("Viewport1" … "Viewport16").</summary>
    public static bool IsViewportId(string? id) =>
        id is not null && id.StartsWith("Viewport", StringComparison.Ordinal) && int.TryParse(id.AsSpan(8), out int n) && n >= 1 && n <= 16;
}
