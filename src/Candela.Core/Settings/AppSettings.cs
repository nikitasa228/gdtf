using System.Text.Json;
using System.Text.Json.Serialization;
using Candela.Core.Serialization;

namespace Candela.Core.Settings;

/// <summary>
/// Per-user program settings (not part of a project). Stored as JSON in the user config folder
/// (<see cref="ConfigPaths.SettingsFile"/>).
/// </summary>
public sealed class AppSettings
{
    /// <summary>Interface scale; 1.0 = 100 %. The settings window allows 1.0 and above.</summary>
    public float UiScale { get; set; } = 1f;

    /// <summary>Base font size in pixels at 100 %.</summary>
    public float FontSize { get; set; } = 15f;

    public string Theme { get; set; } = "Dark";

    /// <summary>UI language code ("en", "ru").</summary>
    public string Language { get; set; } = "en";

    /// <summary>Folders scanned for .gdtf files (the repository root holds the bundled library).</summary>
    public List<string> LibraryFolders { get; set; } = new();

    /// <summary>Researched fixture data (see _specs/SCHEMA.md); empty = look next to the library.</summary>
    public string? SpecsIndexPath { get; set; }

    /// <summary>Name of the key binding preset in use.</summary>
    public string KeyBindingPreset { get; set; } = "Default";

    /// <summary>Network interface for protocol input (IPv4 address or adapter name; empty = all).</summary>
    public string NetworkInterface { get; set; } = "";

    /// <summary>Internal universe = Art-Net port-address + this offset (1 matches grandMA / most consoles).</summary>
    public int ArtNetUniverseOffset { get; set; } = 1;

    /// <summary>Autosave interval in minutes (0 = off).</summary>
    public int AutosaveMinutes { get; set; } = 5;

    public int MaxRecentFiles { get; set; } = 12;
    public List<string> RecentFiles { get; set; } = new();

    /// <summary>Viewport navigation style ("Candela", "Blender", "Maya").</summary>
    public string NavigationStyle { get; set; } = "Candela";

    /// <summary>Mouse wheel zoom speed multiplier.</summary>
    public float ZoomSpeed { get; set; } = 1f;

    /// <summary>Enable the Vulkan validation layer (debugging; slower).</summary>
    public bool GpuValidation { get; set; }

    /// <summary>Preferred GPU name substring (empty = the most capable discrete GPU).</summary>
    public string PreferredGpu { get; set; } = "";

    /// <summary>
    /// JSON properties this version does not know (written by a newer version of Candela), kept so that saving again
    /// does not drop them. Opaque: never interpreted. Null when there are none.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownProperties { get; set; }

    public static AppSettings Load(string? path = null)
    {
        path ??= ConfigPaths.SettingsFile;
        try
        {
            if (File.Exists(path)) return CandelaJson.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new AppSettings();
        }
        catch (Exception)
        {
            // A damaged settings file must never stop the program from starting; fall back to defaults.
        }
        return new AppSettings();
    }

    public void Save(string? path = null)
    {
        path ??= ConfigPaths.SettingsFile;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, CandelaJson.Serialize(this));
        File.Move(tmp, path, overwrite: true);
    }

    public void AddRecentFile(string path)
    {
        RecentFiles.Remove(path);
        RecentFiles.Insert(0, path);
        if (RecentFiles.Count > MaxRecentFiles) RecentFiles.RemoveRange(MaxRecentFiles, RecentFiles.Count - MaxRecentFiles);
    }
}

/// <summary>Per-user folders (APPDATA on Windows, XDG config/cache on Linux, Library on macOS).</summary>
public static class ConfigPaths
{
    public static string ConfigDir =>
        Environment.GetEnvironmentVariable("CANDELA_CONFIG_DIR") is { Length: > 0 } d
            ? d
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Candela");

    public static string CacheDir =>
        Environment.GetEnvironmentVariable("CANDELA_CACHE_DIR") is { Length: > 0 } d
            ? d
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Candela", "cache");

    public static string SettingsFile => Path.Combine(ConfigDir, "settings.json");
    public static string PresetsDir => Path.Combine(ConfigDir, "presets");
    public static string LayoutsDir => Path.Combine(ConfigDir, "layouts");
}
