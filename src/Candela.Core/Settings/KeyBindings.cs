using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Candela.Core.Settings;

/// <summary>Modifier keys of a <see cref="KeyChord"/>.</summary>
[Flags]
public enum KeyModifiers
{
    /// <summary>No modifier.</summary>
    None = 0,

    /// <summary>Shift.</summary>
    Shift = 1,

    /// <summary>Control (Ctrl).</summary>
    Ctrl = 2,

    /// <summary>Alt (Option on macOS).</summary>
    Alt = 4,

    /// <summary>Super: the Windows key, Command on macOS, Meta/Super on Linux.</summary>
    Super = 8,
}

/// <summary>
/// A key with modifiers. Keys are named, windowing-independent strings ("A".."Z", "0".."9", "F1".."F12", "Delete",
/// "Escape", "Enter", "Space", "Tab", "Backspace", "Up", "Down", "Left", "Right", "Home", "End", "PageUp", "PageDown",
/// "Numpad0".."Numpad9", "Minus", "Equal", "Comma", "Period", see <see cref="IsKnownKey"/>).
/// The text form is the modifiers and exactly one key joined by '+', e.g. "Ctrl+Shift+S" (<see cref="ToString"/> writes
/// it, <see cref="Parse"/> reads it back).
/// </summary>
/// <param name="Key">The key name (kept as written; the windowing layer maps names to physical keys).</param>
/// <param name="Modifiers">Modifier keys held with <paramref name="Key"/>.</param>
public readonly record struct KeyChord(string Key, KeyModifiers Modifiers = KeyModifiers.None)
{
    private static readonly Dictionary<string, KeyModifiers> ModifierNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Ctrl"] = KeyModifiers.Ctrl,
        ["Control"] = KeyModifiers.Ctrl,
        ["Shift"] = KeyModifiers.Shift,
        ["Alt"] = KeyModifiers.Alt,
        ["Option"] = KeyModifiers.Alt,
        ["Super"] = KeyModifiers.Super,
        ["Cmd"] = KeyModifiers.Super,
        ["Command"] = KeyModifiers.Super,
        ["Win"] = KeyModifiers.Super,
        ["Windows"] = KeyModifiers.Super,
        ["Meta"] = KeyModifiers.Super,
    };

    private static readonly HashSet<string> KnownKeys = BuildKnownKeys();

    /// <summary>"Ctrl+Shift+Alt+Super+Key" (modifiers in this fixed order, only those that are set).</summary>
    public override string ToString()
    {
        var sb = new StringBuilder();
        if ((Modifiers & KeyModifiers.Ctrl) != 0) sb.Append("Ctrl+");
        if ((Modifiers & KeyModifiers.Shift) != 0) sb.Append("Shift+");
        if ((Modifiers & KeyModifiers.Alt) != 0) sb.Append("Alt+");
        if ((Modifiers & KeyModifiers.Super) != 0) sb.Append("Super+");
        sb.Append(Key);
        return sb.ToString();
    }

    /// <summary>
    /// True for the key names Candela documents (letters, digits, F1–F24, navigation and editing keys, punctuation names
    /// such as "Comma", numpad keys), compared case-insensitively. A windowing layer may support more names; chords with
    /// other key names still parse.
    /// </summary>
    public static bool IsKnownKey(string? key) => key is not null && KnownKeys.Contains(key);

    /// <summary>
    /// Reads "Ctrl+Shift+S": '+'-separated parts (spaces around parts are ignored), modifiers in any order — Ctrl
    /// (Control), Shift, Alt (Option), Super (Cmd, Command, Win, Windows, Meta), case-insensitive — and exactly one key,
    /// kept as written.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    /// <exception cref="FormatException">
    /// The text is empty, has an empty part, repeats a modifier, has no key, has more than one key, or names an unknown
    /// modifier. The message says which, quoting the offending part.
    /// </exception>
    public static KeyChord Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!TryParse(text, out var chord, out var error)) throw new FormatException(error);
        return chord;
    }

    /// <summary>Like <see cref="Parse"/>, but returns false instead of throwing (also for null).</summary>
    public static bool TryParse([NotNullWhen(true)] string? text, out KeyChord chord) => TryParse(text, out chord, out _);

    /// <summary>
    /// Like <see cref="Parse"/>, but returns false instead of throwing; <paramref name="error"/> then explains the problem
    /// (for the key binding editor).
    /// </summary>
    public static bool TryParse([NotNullWhen(true)] string? text, out KeyChord chord, [NotNullWhen(false)] out string? error)
    {
        chord = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "The key chord is empty; write modifiers and one key joined by '+', e.g. 'Ctrl+S'.";
            return false;
        }

        var mods = KeyModifiers.None;
        string? key = null;
        string? secondKey = null;
        string? unknownModifier = null;
        foreach (var raw in text.Split('+'))
        {
            var part = raw.Trim();
            if (part.Length == 0)
            {
                error = $"'{text}' has an empty part; write modifiers and one key joined by single '+' signs, e.g. 'Ctrl+S'.";
                return false;
            }
            if (ModifierNames.TryGetValue(part, out var mod))
            {
                if ((mods & mod) != 0)
                {
                    error = $"'{text}' names the modifier {mod} twice.";
                    return false;
                }
                mods |= mod;
                continue;
            }
            if (key is null)
            {
                key = part;
                continue;
            }
            // A second non-modifier part: either a second key or a misspelt / unsupported modifier written before the
            // key. Unknown names in front of a known key read as modifiers, which gives the more useful message.
            if (!IsKnownKey(key)) unknownModifier ??= key;
            else if (!IsKnownKey(part)) unknownModifier ??= part;
            secondKey ??= part;
            key = part;
        }

        if (unknownModifier is not null)
        {
            error = $"'{text}' has the unknown modifier '{unknownModifier}'; modifiers are Ctrl, Shift, Alt and Super, and a chord has exactly one key.";
            return false;
        }
        if (secondKey is not null)
        {
            error = $"'{text}' has more than one key; a chord is modifiers (Ctrl, Shift, Alt, Super) plus exactly one key, e.g. 'Ctrl+S'.";
            return false;
        }
        if (key is null)
        {
            error = $"'{text}' has no key; a chord is modifiers (Ctrl, Shift, Alt, Super) plus exactly one key, e.g. 'Ctrl+S'.";
            return false;
        }
        chord = new KeyChord(key, mods);
        error = null;
        return true;
    }

    private static HashSet<string> BuildKnownKeys()
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (char c = 'A'; c <= 'Z'; c++) keys.Add(c.ToString());
        for (int i = 0; i <= 9; i++)
        {
            keys.Add(i.ToString(System.Globalization.CultureInfo.InvariantCulture));
            keys.Add("Numpad" + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        for (int i = 1; i <= 24; i++) keys.Add("F" + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var k in new[]
                 {
                     "Delete", "Escape", "Enter", "Space", "Tab", "Backspace", "Insert", "Up", "Down", "Left", "Right", "Home",
                     "End", "PageUp", "PageDown", "Minus", "Equal", "Comma", "Period", "Slash", "Semicolon", "Apostrophe",
                     "LeftBracket", "RightBracket", "Backslash", "GraveAccent", "NumpadDecimal", "NumpadDivide",
                     "NumpadMultiply", "NumpadSubtract", "NumpadAdd", "NumpadEnter", "NumpadEqual", "PrintScreen", "Pause",
                     "CapsLock", "ScrollLock", "NumLock", "Menu",
                 })
        {
            keys.Add(k);
        }
        return keys;
    }
}

/// <summary>
/// Action → key chords (text form, see <see cref="KeyChord"/>). Stored as presets of kind <see cref="PresetKind"/>.
/// A preset edited by hand may contain chords that do not parse; the lookups skip them (and
/// <see cref="FindInvalidChords"/> lists them for the settings window) instead of failing the whole preset.
/// </summary>
public sealed class KeyBindingSet
{
    /// <summary>Preset kind (folder name) of key binding presets.</summary>
    public const string PresetKind = "keybindings";

    /// <summary>Action id (<see cref="EditorActions"/>) → chords in text form.</summary>
    public Dictionary<string, List<string>> Bindings { get; set; } = new();

    /// <summary>The chords bound to <paramref name="action"/> that parse (invalid entries are skipped).</summary>
    public IEnumerable<KeyChord> ChordsFor(string action)
    {
        if (Bindings is null || !Bindings.TryGetValue(action, out var list) || list is null) yield break;
        foreach (var text in list)
            if (KeyChord.TryParse(text, out var chord)) yield return chord;
    }

    /// <summary>Actions bound to <paramref name="chord"/> (normally one; more means a conflict to show in settings).</summary>
    public IEnumerable<string> ActionsFor(KeyChord chord)
    {
        if (Bindings is null) yield break;
        foreach (var (action, list) in Bindings)
        {
            if (list is null) continue;
            foreach (var text in list)
            {
                if (!KeyChord.TryParse(text, out var c) || c != chord) continue;
                yield return action;
                break;
            }
        }
    }

    /// <summary>Every stored chord that does not parse, with the reason (for the settings window).</summary>
    public IEnumerable<(string Action, string? Text, string Error)> FindInvalidChords()
    {
        if (Bindings is null) yield break;
        foreach (var (action, list) in Bindings)
        {
            if (list is null) continue;
            foreach (var text in list)
                if (!KeyChord.TryParse(text, out _, out var error)) yield return (action, text, error);
        }
    }

    /// <summary>Replaces the chords of <paramref name="action"/>.</summary>
    public void Set(string action, params KeyChord[] chords) => Bindings[action] = chords.Select(c => c.ToString()).ToList();

    /// <summary>The built-in "Default" preset.</summary>
    public static KeyBindingSet CreateDefault()
    {
        var k = new KeyBindingSet();
        var c = KeyModifiers.Ctrl;
        var s = KeyModifiers.Shift;
        k.Set(EditorActions.Undo, new KeyChord("Z", c));
        k.Set(EditorActions.Redo, new KeyChord("Y", c), new KeyChord("Z", c | s));
        k.Set(EditorActions.Delete, new KeyChord("Delete"));
        k.Set(EditorActions.Duplicate, new KeyChord("D", c));
        k.Set(EditorActions.Copy, new KeyChord("C", c));
        k.Set(EditorActions.Paste, new KeyChord("V", c));
        k.Set(EditorActions.SelectAll, new KeyChord("A", c));
        k.Set(EditorActions.DeselectAll, new KeyChord("Escape"));
        k.Set(EditorActions.ToolSelect, new KeyChord("Q"));
        k.Set(EditorActions.ToolMove, new KeyChord("W"));
        k.Set(EditorActions.ToolRotate, new KeyChord("E"));
        k.Set(EditorActions.ToolCable, new KeyChord("C"));
        k.Set(EditorActions.ToolWall, new KeyChord("B"));
        k.Set(EditorActions.FrameSelection, new KeyChord("F"));
        k.Set(EditorActions.ViewTop, new KeyChord("Numpad7"));
        k.Set(EditorActions.ViewFront, new KeyChord("Numpad1"));
        k.Set(EditorActions.ViewRight, new KeyChord("Numpad3"));
        k.Set(EditorActions.ViewToggleOrtho, new KeyChord("Numpad5"));
        k.Set(EditorActions.ShadingWireframe, new KeyChord("1", KeyModifiers.Alt));
        k.Set(EditorActions.ShadingColorless, new KeyChord("2", KeyModifiers.Alt));
        k.Set(EditorActions.ShadingFull, new KeyChord("3", KeyModifiers.Alt));
        k.Set(EditorActions.OverlayLux, new KeyChord("L", KeyModifiers.Alt));
        k.Set(EditorActions.OverlayNoise, new KeyChord("N", KeyModifiers.Alt));
        k.Set(EditorActions.OverlayCables, new KeyChord("C", KeyModifiers.Alt));
        k.Set(EditorActions.FileNew, new KeyChord("N", c));
        k.Set(EditorActions.FileOpen, new KeyChord("O", c));
        k.Set(EditorActions.FileSave, new KeyChord("S", c));
        k.Set(EditorActions.FileSaveAs, new KeyChord("S", c | s));
        k.Set(EditorActions.FileImport, new KeyChord("I", c));
        k.Set(EditorActions.Render, new KeyChord("F12"));
        k.Set(EditorActions.Settings, new KeyChord("Comma", c));
        return k;
    }
}

/// <summary>Stable action ids used by menus, toolbars and key bindings.</summary>
public static class EditorActions
{
    public const string Undo = "edit.undo";
    public const string Redo = "edit.redo";
    public const string Delete = "edit.delete";
    public const string Duplicate = "edit.duplicate";
    public const string Copy = "edit.copy";
    public const string Paste = "edit.paste";
    public const string SelectAll = "edit.selectAll";
    public const string DeselectAll = "edit.deselectAll";
    public const string ToolSelect = "tool.select";
    public const string ToolMove = "tool.move";
    public const string ToolRotate = "tool.rotate";
    public const string ToolCable = "tool.cable";
    public const string ToolWall = "tool.wall";
    public const string FrameSelection = "view.frameSelection";
    public const string ViewTop = "view.top";
    public const string ViewFront = "view.front";
    public const string ViewRight = "view.right";
    public const string ViewToggleOrtho = "view.toggleOrtho";
    public const string ShadingWireframe = "view.shading.wireframe";
    public const string ShadingColorless = "view.shading.colorless";
    public const string ShadingFull = "view.shading.full";
    public const string OverlayLux = "view.overlay.lux";
    public const string OverlayNoise = "view.overlay.noise";
    public const string OverlayCables = "view.overlay.cables";
    public const string FileNew = "file.new";
    public const string FileOpen = "file.open";
    public const string FileSave = "file.save";
    public const string FileSaveAs = "file.saveAs";
    public const string FileImport = "file.import";
    public const string FileExport = "file.export";
    public const string Render = "file.render";
    public const string Settings = "app.settings";
}
