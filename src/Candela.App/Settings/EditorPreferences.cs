using System.Text.Json;
using System.Text.Json.Serialization;
using Candela.App.Diagnostics;

namespace Candela.App.Settings;

/// <summary>What the editor does with the DMX addresses of fixtures that Duplicate or Paste copied.</summary>
internal enum DuplicatePatchMode
{
    /// <summary>Asks after each copy whose fixtures overlap their originals (the default).</summary>
    Ask,

    /// <summary>Patches the copies to the next free addresses at once.</summary>
    NextFree,

    /// <summary>Keeps the addresses (the copies overlap; the Issues panel lists the overlaps).</summary>
    Keep,
}

/// <summary>
/// Editing preferences of the editor that the shared <c>AppSettings</c> of Candela.Core do not hold (stored in
/// <c>editor-preferences.json</c> in the config folder): what happens to the DMX addresses of duplicated and pasted
/// fixtures, and whether the fixture library setup window opens at start-up. Read defensively (a damaged file gives the
/// defaults) and written atomically; read-only runs never write.
/// </summary>
internal sealed class EditorPreferences
{
    /// <summary>File name in the config folder.</summary>
    public const string FileName = "editor-preferences.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>What Duplicate and Paste do with the copied fixtures' addresses.</summary>
    public DuplicatePatchMode DuplicatePatch { get; set; } = DuplicatePatchMode.Ask;

    /// <summary>
    /// The fixture library setup window opens at start-up while no library folder of the settings holds a GDTF file
    /// (the window's check box; Help → Fixture Library Setup opens it at any time).
    /// </summary>
    public bool ShowLibrarySetup { get; set; } = true;

    /// <summary>Reads the preferences of <paramref name="configDir"/> (defaults when missing or unreadable).</summary>
    public static EditorPreferences Load(string configDir, AppLog? log = null)
    {
        string path = Path.Combine(configDir, FileName);
        try
        {
            if (!File.Exists(path))
                return new EditorPreferences();
            var loaded = JsonSerializer.Deserialize<EditorPreferences>(File.ReadAllText(path), Json) ?? new EditorPreferences();
            if (!Enum.IsDefined(loaded.DuplicatePatch))
                loaded.DuplicatePatch = DuplicatePatchMode.Ask;
            return loaded;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            log?.Warn("Settings", $"'{path}' could not be read ({e.Message}); the default editing preferences are used.");
            return new EditorPreferences();
        }
    }

    /// <summary>Writes the preferences to <paramref name="configDir"/> (atomic replace); false (logged) when it fails.</summary>
    public bool Save(string configDir, AppLog? log = null)
    {
        string path = Path.Combine(configDir, FileName);
        try
        {
            Directory.CreateDirectory(configDir);
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            log?.Warn("Settings", $"'{path}' could not be written: {e.Message}");
            return false;
        }
    }
}
