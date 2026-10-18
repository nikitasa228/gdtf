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
        foreach (var (action, chords) in set.Bindings)
        {
            if (chords is null)
                continue;
            foreach (var text in chords)
            {
                KeyChord chord;
                try
                {
                    chord = KeyChord.Parse(text);
                }
                catch (FormatException)
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
/// keys). Plain letter keys are ignored while a text field has the keyboard, and everything is ignored while the
/// shortcut editor records a new chord or a viewport flies with W/A/S/D.
/// </summary>
internal sealed class KeyBindingDispatcher
{
    private readonly CommandRegistry _commands;
    private KeyBindingMap _map;
    private ImGuiKey[] _keys;

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

    /// <summary>Replaces the bindings (preset changed or a chord edited).</summary>
    public void Rebuild(KeyBindingSet bindings)
    {
        _map = new KeyBindingMap(bindings);
        _keys = KeyNames.BindableKeys.ToArray();
    }

    /// <summary>Checks this frame's key presses and runs the bound commands. Call once per frame inside the ImGui frame.</summary>
    public void Dispatch()
    {
        if (Suspended)
            return;
        var io = ImGui.GetIO();
        bool typing = io.WantTextInput;
        var mods = KeyNames.ModifiersOf(io.KeyCtrl, io.KeyShift, io.KeyAlt, io.KeySuper);
        foreach (var key in _keys)
        {
            if (!ImGui.IsKeyPressed(key, false))
                continue;
            // While typing, only chords with Ctrl/Alt/Super are shortcuts (Delete, Escape, letters belong to the text field).
            if (typing && (mods & (KeyModifiers.Ctrl | KeyModifiers.Alt | KeyModifiers.Super)) == 0)
                continue;
            string? name = KeyNames.FromImGui(key);
            if (name is null)
                continue;
            var action = _map.Resolve(new KeyChord(name, mods));
            if (action is not null && _commands.TryRun(action))
                LastAction = action;
        }
    }
}
