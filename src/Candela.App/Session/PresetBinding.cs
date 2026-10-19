using System.Text.Json;
using Candela.Core.Presets;
using Candela.Core.Serialization;

namespace Candela.App.Session;

/// <summary>
/// Connects a long-lived settings object that the editor uses directly (the preview settings every viewport renders
/// with, a lux gradient, …) to a Core <see cref="PresetLibrary{T}"/> (requirement: every observable configurable thing
/// has a preset dropdown — <c>Default</c>, saved names, <c>Custom</c> after any edit, and <c>Save as…</c>). Selecting a
/// preset copies its values into the live object; editing the live object and calling <see cref="MarkEdited"/>
/// switches the dropdown to <c>Custom</c>. Main thread only.
/// </summary>
/// <typeparam name="T">Settings type (plain data, JSON-serialisable).</typeparam>
internal sealed class PresetBinding<T> where T : class
{
    private readonly Func<T> _defaults;
    private readonly Action<T, T> _copy;
    private readonly Func<T?, T> _sanitize;
    private IReadOnlyList<string>? _names;

    /// <summary>Creates the binding.</summary>
    /// <param name="kind">Preset folder name ("preview", "lux-gradient", …).</param>
    /// <param name="live">The object the editor uses; it keeps its identity, values are copied into it.</param>
    /// <param name="defaults">Builds the read-only <c>Default</c> preset.</param>
    /// <param name="copy">Copies every value from the first argument into the second.</param>
    /// <param name="sanitize">Makes a value read from a file safe to use (finite, in range); null input gives the defaults.</param>
    /// <param name="store">Where named presets are kept.</param>
    public PresetBinding(string kind, T live, Func<T> defaults, Action<T, T> copy, Func<T?, T> sanitize, IPresetStore store)
    {
        ArgumentException.ThrowIfNullOrEmpty(kind);
        Live = live ?? throw new ArgumentNullException(nameof(live));
        _defaults = defaults ?? throw new ArgumentNullException(nameof(defaults));
        _copy = copy ?? throw new ArgumentNullException(nameof(copy));
        _sanitize = sanitize ?? throw new ArgumentNullException(nameof(sanitize));
        Library = new PresetLibrary<T>(kind, defaults, store ?? throw new ArgumentNullException(nameof(store)));
    }

    /// <summary>The object in use.</summary>
    public T Live { get; }

    /// <summary>The preset library behind the dropdown.</summary>
    public PresetLibrary<T> Library { get; }

    /// <summary>Name shown in the dropdown: <c>Default</c>, a saved name or <c>Custom</c>.</summary>
    public string CurrentName => Library.CurrentName;

    /// <summary>True when the live values were edited since a preset was selected.</summary>
    public bool IsCustom => Library.IsCustom;

    /// <summary>Increases with every change of the live values (selection, edit, project load); consumers compare it.</summary>
    public long Version { get; private set; }

    /// <summary>The last problem with a preset file (unreadable, not writable), or null.</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// Dropdown entries (Default, saved names sorted, Custom when edited). Read from the store when first needed and
    /// after <see cref="RefreshNames"/>, a save or a delete — not every frame.
    /// </summary>
    public IReadOnlyList<string> Names => _names ??= ReadNames();

    /// <summary>Re-reads the saved preset names (when the dropdown opens).</summary>
    public void RefreshNames() => _names = null;

    /// <summary>Selects a preset and copies its values into <see cref="Live"/>; false (with <see cref="LastError"/>) when it cannot be read.</summary>
    public bool Select(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name == PresetLibrary<T>.CustomName)
            return true;
        try
        {
            Library.Select(name);
        }
        catch (Exception e) when (e is KeyNotFoundException or JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            LastError = $"Preset '{name}' could not be read: {e.Message}";
            _names = null;
            return false;
        }

        // A hand-edited preset file may hold anything: what goes live is checked.
        _copy(_sanitize(Library.Current), Live);
        LastError = null;
        _names = null;
        Version++;
        return true;
    }

    /// <summary>Call after editing <see cref="Live"/>: the dropdown shows <c>Custom</c>.</summary>
    public void MarkEdited()
    {
        _copy(Live, Library.Current);
        if (!Library.IsCustom)
        {
            Library.MarkModified();
            _names = null;
        }

        Version++;
    }

    /// <summary>Saves the live values under <paramref name="name"/>; false (with <see cref="LastError"/>) when the name is not allowed or the file cannot be written.</summary>
    public bool SaveAs(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        _copy(Live, Library.Current);
        try
        {
            Library.SaveAs(name);
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            LastError = e.Message;
            return false;
        }

        LastError = null;
        _names = null;
        return true;
    }

    /// <summary>Deletes a saved preset (Default and Custom cannot be deleted); false (with <see cref="LastError"/>) on failure.</summary>
    public bool Delete(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        try
        {
            Library.Delete(name);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            LastError = e.Message;
            return false;
        }

        LastError = null;
        _names = null;
        return true;
    }

    /// <summary>
    /// Takes values from outside (a project that was opened): they become the live values, and the dropdown shows
    /// <c>Default</c> when they equal the defaults, otherwise <c>Custom</c>.
    /// </summary>
    public void Adopt(T? value)
    {
        var sane = _sanitize(value);
        _copy(sane, Live);
        if (SameAs(sane, _defaults()))
        {
            Library.Select(PresetLibrary<T>.DefaultName);
            _copy(Library.Current, Live);
        }
        else
        {
            Library.SetCustom(sane);
        }

        _names = null;
        Version++;
    }

    /// <summary>True when the live values equal the <c>Default</c> preset (compared as JSON).</summary>
    public bool IsAtDefaults => SameAs(Live, _defaults());

    private static bool SameAs(T a, T b) =>
        string.Equals(CandelaJson.Serialize(a, indented: false), CandelaJson.Serialize(b, indented: false), StringComparison.Ordinal);

    private IReadOnlyList<string> ReadNames()
    {
        try
        {
            return Library.Names;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            LastError = e.Message;
            var list = new List<string> { PresetLibrary<T>.DefaultName };
            if (Library.IsCustom)
                list.Add(PresetLibrary<T>.CustomName);
            return list;
        }
    }
}
