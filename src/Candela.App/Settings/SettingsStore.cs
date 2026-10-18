using System.Globalization;
using System.Text.Json;
using Candela.App.Diagnostics;
using Candela.App.Ui;
using Candela.Core.Serialization;
using Candela.Core.Settings;

namespace Candela.App.Settings;

/// <summary>
/// Crash-safe persistence of the user's <see cref="AppSettings"/>. Loading never fails: a damaged file is kept aside as
/// <c>settings.json.damaged-&lt;time&gt;</c> (so the user's values are not silently overwritten) and defaults are used;
/// out-of-range values are clamped. Saving writes a temporary file and renames it over the old one, so a crash during
/// a save leaves either the old or the new settings; failures are logged, never thrown. Saves are debounced while the
/// user drags a slider.
/// </summary>
internal sealed class SettingsStore
{
    private readonly string _path;
    private readonly AppLog _log;
    private double _saveDueAt = double.PositiveInfinity;

    /// <summary>Creates a store for <paramref name="path"/> (default: <see cref="ConfigPaths.SettingsFile"/>).</summary>
    public SettingsStore(AppLog log, string? path = null)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _path = path ?? ConfigPaths.SettingsFile;
        Settings = new AppSettings();
    }

    /// <summary>The settings in use (edited in place by the settings window).</summary>
    public AppSettings Settings { get; private set; }

    /// <summary>The settings file.</summary>
    public string FilePath => _path;

    /// <summary>True when a change is waiting to be written.</summary>
    public bool HasPendingSave => !double.IsPositiveInfinity(_saveDueAt);

    /// <summary>Loads the settings file (or defaults) and sanitises the values.</summary>
    public AppSettings Load()
    {
        AppSettings? loaded = null;
        if (File.Exists(_path))
        {
            try
            {
                loaded = CandelaJson.Deserialize<AppSettings>(File.ReadAllText(_path));
                if (loaded is null)
                    throw new JsonException("The file is empty.");
            }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
            {
                string backup = _path + ".damaged-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                try
                {
                    File.Copy(_path, backup, overwrite: true);
                    _log.Warn("Settings", $"The settings file could not be read ({e.Message}); it was kept as '{backup}' and defaults are used.");
                }
                catch (Exception copyError) when (copyError is IOException or UnauthorizedAccessException)
                {
                    _log.Warn("Settings", $"The settings file could not be read ({e.Message}); defaults are used.");
                }

                loaded = null;
            }
        }

        Settings = Sanitize(loaded ?? new AppSettings());
        return Settings;
    }

    /// <summary>Requests a save <paramref name="delaySeconds"/> after <paramref name="now"/> (later requests postpone it).</summary>
    public void RequestSave(double now, double delaySeconds = 0.75) => _saveDueAt = now + delaySeconds;

    /// <summary>Writes the settings when a requested save is due.</summary>
    public void Update(double now)
    {
        if (now >= _saveDueAt)
            SaveNow();
    }

    /// <summary>Writes the settings immediately (atomic replace). Returns false and logs on failure.</summary>
    public bool SaveNow()
    {
        _saveDueAt = double.PositiveInfinity;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            string tmp = _path + "." + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + ".tmp";
            File.WriteAllText(tmp, CandelaJson.Serialize(Settings));
            File.Move(tmp, _path, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _log.Warn("Settings", $"The settings could not be saved to '{_path}': {e.Message}");
            return false;
        }
    }

    /// <summary>Clamps every value to its valid range and repairs missing collections.</summary>
    public static AppSettings Sanitize(AppSettings s)
    {
        ArgumentNullException.ThrowIfNull(s);
        s.UiScale = UiScale.ClampScale(s.UiScale);
        s.FontSize = UiScale.ClampFontSize(s.FontSize);
        s.Theme = string.IsNullOrWhiteSpace(s.Theme) ? "Dark" : s.Theme;
        s.Language = s.Language?.Trim().ToLowerInvariant() is "ru" ? "ru" : "en";
        s.LibraryFolders = (s.LibraryFolders ?? new List<string>()).Where(f => !string.IsNullOrWhiteSpace(f)).Distinct().ToList();
        s.KeyBindingPreset = string.IsNullOrWhiteSpace(s.KeyBindingPreset) ? "Default" : s.KeyBindingPreset;
        s.NetworkInterface ??= "";
        // docs/protocols.md: internal universe = Art-Net port-address + offset; 1 (default) or 0 are the meaningful values.
        s.ArtNetUniverseOffset = Math.Clamp(s.ArtNetUniverseOffset, 0, 1);
        s.AutosaveMinutes = Math.Clamp(s.AutosaveMinutes, 0, 240);
        s.MaxRecentFiles = Math.Clamp(s.MaxRecentFiles, 0, 50);
        s.RecentFiles = (s.RecentFiles ?? new List<string>()).Where(f => !string.IsNullOrWhiteSpace(f)).Distinct().Take(s.MaxRecentFiles).ToList();
        s.NavigationStyle = Viewport.NavigationStyles.Normalize(s.NavigationStyle);
        s.ZoomSpeed = float.IsFinite(s.ZoomSpeed) ? Math.Clamp(s.ZoomSpeed, 0.1f, 5f) : 1f;
        s.PreferredGpu ??= "";
        return s;
    }
}
