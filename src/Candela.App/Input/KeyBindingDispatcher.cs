using Candela.Core.Settings;
using Hexa.NET.ImGui;

namespace Candela.App.Input;

/// <summary>
/// Chord → action lookup built from a <see cref="KeyBindingSet"/> (pure logic, unit-tested): which action a pressed chord
/// runs and which chords are bound to more than one action (shown as conflicts in the settings window). When a chord is
/// bound to several actions the first one in the binding set's order wins.
/// </summary>
internal sealed class KeyBindingMap
{
    private readonly Dictionary<KeyChord, List<string>> _byChord = new();

    /// <summary>Builds the lookup; malformed chord strings are skipped and counted in <see cref="InvalidCount"/>.</summary>
    public KeyBindingMap(KeyBindingSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        if (set.Bindings is null)
            return;
        foreach (var (action, chords) in set.Bindings)
        {
            if (chords is null)
                continue;
            foreach (var text in chords)
            {
                KeyChord chord;
                try
                {
                    chord = KeyChord.Parse(text ?? "");
                }
                catch (Exception e) when (e is FormatException or ArgumentException)
                {
                    InvalidCount++;
                    continue;
                }

                var key = KeyNames.ToImGui(chord.Key);
                if (key == ImGuiKey.None)
                {
                    InvalidCount++;
                    continue;
                }

                // Canonical key name ("a" and "A" are the same key).
                chord = new KeyChord(KeyNames.FromImGui(key)!, chord.Modifiers);
                if (!_byChord.TryGetValue(chord, out var list))
                    _byChord[chord] = list = new List<string>(1);
                if (!list.Contains(action))
                    list.Add(action);
            }
        }
    }

    /// <summary>Chord strings that could not be parsed or name unknown keys.</summary>
    public int InvalidCount { get; }

    /// <summary>The action of a chord, or null.</summary>
    public string? Resolve(KeyChord chord) => _byChord.TryGetValue(Canonical(chord), out var list) && list.Count > 0 ? list[0] : null;

    /// <summary>All actions bound to a chord.</summary>
    public IReadOnlyList<string> ActionsFor(KeyChord chord) =>
        _byChord.TryGetValue(Canonical(chord), out var list) ? list : Array.Empty<string>();

    private static KeyChord Canonical(KeyChord chord) =>
        KeyNames.FromImGui(KeyNames.ToImGui(chord.Key)) is { } name && name != chord.Key ? new KeyChord(name, chord.Modifiers) : chord;

    /// <summary>Chords bound to more than one action.</summary>
    public IEnumerable<(KeyChord Chord, IReadOnlyList<string> Actions)> Conflicts() =>
        _byChord.Where(kv => kv.Value.Count > 1).Select(kv => (kv.Key, (IReadOnlyList<string>)kv.Value));

    /// <summary>The other actions sharing a chord with <paramref name="action"/>.</summary>
    public IEnumerable<string> ConflictsOf(string action, KeyChord chord) => ActionsFor(chord).Where(a => a != action);
}

/// <summary>
/// Runs editor commands for key presses (polled from Dear ImGui each frame, so scripted input works the same as real
/// keys). Plain letter keys and the text-editing chords (Ctrl+A / C / V / X / Z …) are ignored while a text field has
/// the keyboard, and everything is ignored while the
/// shortcut editor records a new chord, a viewport flies with W/A/S/D, or a popup (menu, combo box, context menu,
/// modal dialog) is open: the popup owns the keyboard, so Escape only closes it and Delete or Ctrl+Z cannot change the
/// scene behind a dialog. The frame in which a popup closes still belongs to it: Dear ImGui closes a menu on Escape, and
/// the dialogs close themselves on Escape, before the dispatcher runs — that Escape must not also clear the selection.
/// </summary>
internal sealed class KeyBindingDispatcher
{
    private readonly CommandRegistry _commands;
    private KeyBindingMap _map;
    private ImGuiKey[] _keys;
    private int _popupOpenFrame = int.MinValue;
    private readonly HashSet<ImGuiKey> _consumed = new();

    /// <summary>Creates a dispatcher for <paramref name="bindings"/>.</summary>
    public KeyBindingDispatcher(CommandRegistry commands, KeyBindingSet bindings)
    {
        _commands = commands ?? throw new ArgumentNullException(nameof(commands));
        _map = new KeyBindingMap(bindings);
        _keys = KeyNames.BindableKeys.ToArray();
    }

    /// <summary>The current lookup.</summary>
    public KeyBindingMap Map => _map;

    /// <summary>When true no shortcut runs (chord capture in the settings window, fly navigation).</summary>
    public bool Suspended { get; set; }

    /// <summary>The last action run (diagnostics and tests).</summary>
    public string? LastAction { get; private set; }

    /// <summary>
    /// Marks a key press of this frame as used by a window (Escape that closed the settings window or cancelled a chord
    /// recording), so it does not also run a shortcut. Cleared after <see cref="Dispatch"/>.
    /// </summary>
    public void Consume(ImGuiKey key) => _consumed.Add(key);

    /// <summary>True when a window already used this frame's press of <paramref name="key"/>.</summary>
    public bool IsConsumed(ImGuiKey key) => _consumed.Contains(key);

    /// <summary>Replaces the bindings (preset changed or a chord edited).</summary>
    public void Rebuild(KeyBindingSet bindings)
    {
        _map = new KeyBindingMap(bindings);
        _keys = KeyNames.BindableKeys.ToArray();
    }

    /// <summary>
    /// True for the chords a focused text field handles itself (Ctrl or Super, with or without Shift, and A, C, V, X, Z,
    /// Y, the arrows, Home, End, Backspace, Delete or Insert): they edit the text, so they must not also run the editor
    /// commands bound to them (Select All, Copy, Paste, Undo …).
    /// </summary>
    internal static bool IsTextEditingChord(ImGuiKey key, KeyModifiers mods)
    {
        if ((mods & KeyModifiers.Alt) != 0 || (mods & (KeyModifiers.Ctrl | KeyModifiers.Super)) == 0)
            return false;
        return key is ImGuiKey.A or ImGuiKey.C or ImGuiKey.V or ImGuiKey.X or ImGuiKey.Z or ImGuiKey.Y or ImGuiKey.LeftArrow
            or ImGuiKey.RightArrow or ImGuiKey.UpArrow or ImGuiKey.DownArrow or ImGuiKey.Home or ImGuiKey.End or ImGuiKey.Backspace
            or ImGuiKey.Delete or ImGuiKey.Insert;
    }

    /// <summary>Checks this frame's key presses and runs the bound commands. Call once per frame inside the ImGui frame.</summary>
    public void Dispatch()
    {
        int frame = ImGui.GetFrameCount();
        bool popupOpen = ImGui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel);
        bool popupClosedThisFrame = !popupOpen && _popupOpenFrame == frame - 1;
        if (popupOpen)
            _popupOpenFrame = frame;
        if (Suspended || popupOpen || popupClosedThisFrame)
        {
            _consumed.Clear();
            return;
        }

        var io = ImGui.GetIO();
        bool typing = io.WantTextInput;
        var mods = KeyNames.ModifiersOf(io.KeyCtrl, io.KeyShift, io.KeyAlt, io.KeySuper);
        foreach (var key in _keys)
        {
            if (!ImGui.IsKeyPressed(key, false) || _consumed.Contains(key))
                continue;
            // While typing, only chords with Ctrl/Alt/Super are shortcuts (Delete, Escape, letters belong to the text field),
            // and not the ones the field itself uses: Ctrl+A / C / V / X / Z select, copy, paste, cut and undo text there,
            // never all objects or the scene's clipboard.
            if (typing && ((mods & (KeyModifiers.Ctrl | KeyModifiers.Alt | KeyModifiers.Super)) == 0 || IsTextEditingChord(key, mods)))
                continue;
            string? name = KeyNames.FromImGui(key);
            if (name is null)
                continue;
            var action = _map.Resolve(new KeyChord(name, mods));
            if (action is not null && _commands.TryRun(action))
                LastAction = action;
        }

        _consumed.Clear();
    }
}
