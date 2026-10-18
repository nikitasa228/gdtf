using Candela.App.Diagnostics;
using Candela.Core.Presets;
using Candela.Core.Serialization;
using Candela.Core.Settings;

namespace Candela.App.Input;

/// <summary>
/// The key binding presets (requirement: shortcuts editor with named presets): Core's <see cref="PresetLibrary{T}"/> of
/// <see cref="KeyBindingSet"/> — read-only <c>Default</c>, saved named presets in <c>ConfigPaths.PresetsDir/keybindings</c>
/// and <c>Custom</c> as soon as a chord is edited. The custom set survives restarts (<c>keybindings.custom.json</c> in the
/// config folder) and the preset in use is remembered in <c>AppSettings.KeyBindingPreset</c>.
/// </summary>
internal sealed class KeyBindingsService
{
    private readonly AppSettings _settings;
    private readonly string _customFile;
    private readonly AppLog _log;

    /// <summary>Creates the service over a preset store.</summary>
    /// <param name="settings">User settings (preset name).</param>
    /// <param name="store">Preset storage.</param>
    /// <param name="customFile">File keeping the unsaved custom set.</param>
    /// <param name="log">Log for unreadable files.</param>
    public KeyBindingsService(AppSettings settings, IPresetStore store, string customFile, AppLog log)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _customFile = customFile ?? throw new ArgumentNullException(nameof(customFile));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        Library = new PresetLibrary<KeyBindingSet>(KeyBindingSet.PresetKind, CreateDefault, store ?? throw new ArgumentNullException(nameof(store)));
        Restore();
    }

    /// <summary>The preset library (dropdown names, current set).</summary>
    public PresetLibrary<KeyBindingSet> Library { get; }

    /// <summary>The bindings in use.</summary>
    public KeyBindingSet Current => Library.Current;

    /// <summary>Raised after the bindings changed (dispatcher rebuild, settings save).</summary>
    public event Action? Changed;

    /// <summary>Core's default bindings plus the editor's own actions.</summary>
    public static KeyBindingSet CreateDefault()
    {
        var set = KeyBindingSet.CreateDefault();
        AppActions.AddDefaults(set);
        return set;
    }

    /// <summary>Selects a preset by name.</summary>
    public void Select(string name)
    {
        try
        {
            Library.Select(name);
            Sanitize(Library.Current);
        }
        catch (Exception e) when (IsUnreadable(e))
        {
            _log.Warn("Keys", $"The key binding preset '{name}' could not be loaded: {e.Message}");
            Library.ResetToDefault();
        }

        Persist();
    }

    /// <summary>Replaces chord <paramref name="index"/> of an action (index = count appends).</summary>
    public void SetChord(string action, int index, KeyChord chord)
    {
        var list = Chords(action);
        if (index >= 0 && index < list.Count)
            list[index] = chord.ToString();
        else
            list.Add(chord.ToString());
        Modified();
    }

    /// <summary>Removes chord <paramref name="index"/> of an action.</summary>
    public void RemoveChord(string action, int index)
    {
        var list = Chords(action);
        if (index < 0 || index >= list.Count)
            return;
        list.RemoveAt(index);
        Modified();
    }

    /// <summary>Saves the current set as a named preset and selects it.</summary>
    /// <exception cref="ArgumentException">The name is empty or reserved.</exception>
    public void SaveAs(string name)
    {
        Library.SaveAs(name);
        Persist();
    }

    /// <summary>Deletes a saved preset (the current set becomes Custom when it was that preset).</summary>
    public void Delete(string name)
    {
        try
        {
            Library.Delete(name);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _log.Warn("Keys", $"The key binding preset '{name}' could not be deleted: {e.Message}");
        }

        Persist();
    }

    /// <summary>
    /// Repairs a binding set read from a file: a missing table, unnamed actions, null chord lists and empty or null chord
    /// strings (hand-edited or damaged files) are removed, so nothing downstream meets a null. Returns the same set.
    /// </summary>
    public static KeyBindingSet Sanitize(KeyBindingSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        set.Bindings ??= new Dictionary<string, List<string>>();
        List<string>? unnamed = null, empty = null;
        foreach (var (action, chords) in set.Bindings)
        {
            if (string.IsNullOrWhiteSpace(action))
                (unnamed ??= new List<string>()).Add(action);
            else if (chords is null)
                (empty ??= new List<string>()).Add(action);
            else
                chords.RemoveAll(string.IsNullOrWhiteSpace);
        }

        // Changed after the enumeration (replacing a value would invalidate it).
        if (unnamed is not null)
            foreach (var action in unnamed)
                set.Bindings.Remove(action);
        if (empty is not null)
            foreach (var action in empty)
                set.Bindings[action] = new List<string>();
        return set;
    }

    private static bool IsUnreadable(Exception e) =>
        e is KeyNotFoundException or System.Text.Json.JsonException or IOException or UnauthorizedAccessException or NotSupportedException;

    /// <summary>Back to the default bindings.</summary>
    public void ResetToDefault()
    {
        Library.ResetToDefault();
        Persist();
    }

    /// <summary>The chord strings of an action (created empty when absent).</summary>
    private List<string> Chords(string action)
    {
        if (!Current.Bindings.TryGetValue(action, out var list) || list is null)
            Current.Bindings[action] = list = new List<string>();
        return list;
    }

    private void Modified()
    {
        Library.MarkModified();
        Persist();
    }

    private void Persist()
    {
        _settings.KeyBindingPreset = Library.CurrentName;
        if (Library.IsCustom)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_customFile))!);
                string tmp = _customFile + "." + Environment.ProcessId + ".tmp";
                File.WriteAllText(tmp, CandelaJson.Serialize(Current));
                File.Move(tmp, _customFile, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _log.Warn("Keys", $"The custom key bindings could not be saved: {e.Message}");
            }
        }

        Changed?.Invoke();
    }

    private void Restore()
    {
        string name = _settings.KeyBindingPreset;
        if (name == PresetLibrary<KeyBindingSet>.CustomName)
        {
            try
            {
                if (File.Exists(_customFile) && CandelaJson.Deserialize<KeyBindingSet>(File.ReadAllText(_customFile)) is { } custom)
                {
                    Library.SetCustom(Sanitize(custom));
                    // A custom set saved by an older version lacks newer actions: add their defaults.
                    AppActions.AddDefaults(Library.Current);
                    return;
                }
            }
            catch (Exception e) when (e is System.Text.Json.JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                _log.Warn("Keys", $"The custom key bindings could not be read ({e.Message}); the defaults are used.");
            }

            Library.ResetToDefault();
            return;
        }

        if (name != PresetLibrary<KeyBindingSet>.DefaultName)
        {
            try
            {
                Library.Select(name);
                Sanitize(Library.Current);
            }
            catch (Exception e) when (IsUnreadable(e))
            {
                _log.Warn("Keys", $"The key binding preset '{name}' is missing or damaged; the defaults are used.");
                Library.ResetToDefault();
            }
        }

        // Presets saved by an older version may lack newer actions: add their defaults.
        AppActions.AddDefaults(Library.Current);
    }
}
