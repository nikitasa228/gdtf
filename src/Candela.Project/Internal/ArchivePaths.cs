using System.Text;

namespace Candela.Project.Internal;

/// <summary>Entry names of the <c>.candela</c> archive and the rules that make stored file names safe to extract.</summary>
internal static class ArchivePaths
{
    public const string Manifest = "manifest.json";
    public const string Scene = "scene.json";
    public const string Settings = "settings.json";
    public const string AssetsFolder = "assets/";
    public const string AssetIndex = "assets/index.json";
    public const string FixturesFolder = "fixtures/";
    public const string FixtureIndex = "fixtures/index.json";
    public const string Thumbnail = "thumbnail.png";

    /// <summary>Longest file name segment written (Windows allows 255 UTF-16 units; leave room for suffixes).</summary>
    private const int MaxSegmentLength = 120;

    private static readonly string[] s_reservedWindowsNames =
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Folder of one asset: <c>assets/{guid}/</c>.</summary>
    public static string AssetFolder(Guid id) => AssetsFolder + id.ToString("D") + "/";

    /// <summary>
    /// A file name segment that can be extracted on Windows, macOS and Linux: control characters and
    /// <c>/ \ : * ? " &lt; &gt; |</c> become <c>_</c>, trailing dots and spaces are removed, reserved device names get a
    /// leading <c>_</c>, and long names are shortened (keeping the extension).
    /// </summary>
    public static string SafeSegment(string? name, string fallback)
    {
        if (string.IsNullOrWhiteSpace(name)) return fallback;
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            bool bad = ch < 0x20 || ch == 0x7F || ch is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|';
            sb.Append(bad ? '_' : ch);
        }
        var s = sb.ToString().Trim().TrimEnd('.', ' ');
        if (s.Length == 0 || s == "." || s == "..") return fallback;
        var stem = s;
        int dot = s.IndexOf('.');
        if (dot > 0) stem = s[..dot];
        foreach (var reserved in s_reservedWindowsNames)
        {
            if (string.Equals(stem, reserved, StringComparison.OrdinalIgnoreCase))
            {
                s = "_" + s;
                break;
            }
        }
        if (s.Length > MaxSegmentLength)
        {
            var ext = Path.GetExtension(s);
            if (ext.Length > 16) ext = "";
            s = s[..(MaxSegmentLength - ext.Length)] + ext;
        }
        return s;
    }

    /// <summary>A relative path (forward slashes) with every segment made safe; empty segments are dropped.</summary>
    public static string SafeRelativePath(string relativePath, string fallback)
    {
        var parts = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var safe = new List<string>(parts.Length);
        foreach (var p in parts)
        {
            if (p is "." or "..") continue;
            safe.Add(SafeSegment(p, "_"));
        }
        return safe.Count == 0 ? fallback : string.Join('/', safe);
    }

    /// <summary>
    /// Makes <paramref name="entryName"/> unique among <paramref name="used"/> (case-insensitive, as file systems on Windows
    /// and macOS are) by inserting " (2)", " (3)", … before the extension of the last segment, and records it.
    /// </summary>
    public static string Unique(string entryName, HashSet<string> used)
    {
        if (used.Add(entryName)) return entryName;
        int slash = entryName.LastIndexOf('/');
        var folder = slash >= 0 ? entryName[..(slash + 1)] : "";
        var file = entryName[(slash + 1)..];
        var ext = Path.GetExtension(file);
        var stem = file[..^ext.Length];
        for (int n = 2; ; n++)
        {
            var candidate = $"{folder}{stem} ({n}){ext}";
            if (used.Add(candidate)) return candidate;
        }
    }

    /// <summary>True for file types whose content is already compressed, so deflating them again only costs time.</summary>
    public static bool IsPrecompressed(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".gdtf" or ".mvr" or ".zip" or ".gz" or ".7z" or ".mp4"
            or ".mov" or ".mp3" or ".ogg" or ".avif" or ".heic" or ".ktx2" or ".3mf" or ".jfif";
    }
}
