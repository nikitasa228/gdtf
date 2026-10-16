namespace Candela.Core.Settings;

[Flags]
public enum KeyModifiers
{
    None = 0,
    Shift = 1,
    Ctrl = 2,
    Alt = 4,
    Super = 8,
}

/// <summary>
/// A key with modifiers. Keys are named, windowing-independent strings ("A".."Z", "0".."9", "F1".."F12", "Delete",
/// "Escape", "Enter", "Space", "Tab", "Backspace", "Up", "Down", "Left", "Right", "Home", "End", "PageUp", "PageDown",
/// "Numpad0".."Numpad9", "Minus", "Equal", "Comma", "Period").
/// </summary>
public readonly record struct KeyChord(string Key, KeyModifiers Modifiers = KeyModifiers.None)
{
    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(KeyModifiers.Ctrl)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(KeyModifiers.Super)) parts.Add("Super");
        parts.Add(Key);
        return string.Join("+", parts);
    }

    public static KeyChord Parse(string text)
    {
        var mods = KeyModifiers.None;
        string key = "";
        foreach (var p in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (p.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= KeyModifiers.Ctrl; break;
                case "shift": mods |= KeyModifiers.Shift; break;
                case "alt": mods |= KeyModifiers.Alt; break;
                case "super" or "cmd" or "win": mods |= KeyModifiers.Super; break;
                default: key = p; break;
            }
        }
        if (key.Length == 0) throw new FormatException($"No key in '{text}'.");
        return new KeyChord(key, mods);
    }
}

/// <summary>Action → key chords. Stored as presets of kind <see cref="PresetKind"/>.</summary>
public sealed class KeyBindingSet
{
    public const string PresetKind = "keybindings";

    public Dictionary<string, List<string>> Bindings { get; set; } = new();

    public IEnumerable<KeyChord> ChordsFor(string action) =>
        Bindings.TryGetValue(action, out var list) ? list.Select(KeyChord.Parse) : Enumerable.Empty<KeyChord>();

    /// <summary>Actions bound to <paramref name="chord"/> (normally one; more means a conflict to show in settings).</summary>
    public IEnumerable<string> ActionsFor(KeyChord chord) =>
        Bindings.Where(kv => kv.Value.Any(s => KeyChord.Parse(s) == chord)).Select(kv => kv.Key);

    public void Set(string action, params KeyChord[] chords) => Bindings[action] = chords.Select(c => c.ToString()).ToList();

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
