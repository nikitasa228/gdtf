using Candela.App.Ui;
using Candela.Core.Settings;

namespace Candela.App.Input;

/// <summary>
/// Editor action ids beyond Core's <see cref="EditorActions"/> (which stay the canonical ids for everything they cover).
/// All of them can be bound to keys in the settings window.
/// </summary>
internal static class AppActions
{
    /// <summary>Edit → Cut.</summary>
    public const string Cut = "edit.cut";

    /// <summary>View → camera views: left.</summary>
    public const string ViewLeft = "view.left";

    /// <summary>View → camera views: back.</summary>
    public const string ViewBack = "view.back";

    /// <summary>View → camera views: bottom.</summary>
    public const string ViewBottom = "view.bottom";

    /// <summary>Overlay off.</summary>
    public const string OverlayNone = "view.overlay.none";

    /// <summary>Grid on/off in the active viewport.</summary>
    public const string ToggleGrid = "view.grid";

    /// <summary>Window → New Viewport.</summary>
    public const string NewViewport = "view.newViewport";

    /// <summary>Window → Reset Layout.</summary>
    public const string ResetLayout = "window.resetLayout";

    /// <summary>Floor tool.</summary>
    public const string ToolFloor = "tool.floor";

    /// <summary>Snapping on/off.</summary>
    public const string ToggleSnap = "tool.snap";

    /// <summary>Gizmo axes local/world.</summary>
    public const string ToggleSpace = "tool.space";

    /// <summary>Help → Keyboard Shortcuts.</summary>
    public const string Shortcuts = "help.shortcuts";

    /// <summary>File → Exit.</summary>
    public const string Exit = "app.exit";

    /// <summary>Default chords of the app actions (merged into Core's default set).</summary>
    public static void AddDefaults(KeyBindingSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        void Add(string action, params KeyChord[] chords)
        {
            if (!set.Bindings.ContainsKey(action))
                set.Set(action, chords);
        }

        var c = KeyModifiers.Ctrl;
        Add(Cut, new KeyChord("X", c));
        Add(ViewLeft, new KeyChord("Numpad3", c));
        Add(ViewBack, new KeyChord("Numpad1", c));
        Add(ViewBottom, new KeyChord("Numpad7", c));
        Add(OverlayNone, new KeyChord("0", KeyModifiers.Alt));
        Add(ToggleGrid, new KeyChord("G", KeyModifiers.Alt));
        Add(NewViewport, new KeyChord("N", c | KeyModifiers.Alt));
        Add(ResetLayout);
        Add(ToolFloor, new KeyChord("B", KeyModifiers.Shift));
        Add(ToggleSnap, new KeyChord("S", KeyModifiers.Alt));
        Add(ToggleSpace, new KeyChord("X", KeyModifiers.Alt));
        Add(Shortcuts, new KeyChord("F1"));
        Add(Exit, new KeyChord("Q", c));
        SessionActions.AddDefaults(set);
    }
}

/// <summary>A command of the editor: label, icon and what it does. Menus, shortcuts and scripts all run these.</summary>
/// <param name="Id">Stable action id (key bindings, scripts).</param>
/// <param name="Label">Label text key.</param>
/// <param name="Icon">Icon glyph (empty = none).</param>
/// <param name="Run">The action.</param>
/// <param name="IsEnabled">Whether it can run now (null = always).</param>
/// <param name="IsChecked">Check-mark state for toggles (null = not a toggle).</param>
internal sealed record EditorCommand(string Id, Str Label, string Icon, Action Run, Func<bool>? IsEnabled = null, Func<bool>? IsChecked = null)
{
    /// <summary>True when the command can run.</summary>
    public bool Enabled => IsEnabled?.Invoke() ?? true;

    /// <summary>Localised label.</summary>
    public string Text => Loc.Get(Label);
}

/// <summary>All editor commands by id.</summary>
internal sealed class CommandRegistry
{
    private readonly Dictionary<string, EditorCommand> _commands = new(StringComparer.Ordinal);
    private readonly List<EditorCommand> _ordered = new();

    /// <summary>Commands in registration order (the order of the shortcuts editor).</summary>
    public IReadOnlyList<EditorCommand> All => _ordered;

    /// <summary>Adds or replaces a command.</summary>
    public void Register(EditorCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (_commands.ContainsKey(command.Id))
            _ordered.RemoveAll(c => c.Id == command.Id);
        _commands[command.Id] = command;
        _ordered.Add(command);
    }

    /// <summary>The command with this id, or null.</summary>
    public EditorCommand? Find(string id) => _commands.GetValueOrDefault(id);

    /// <summary>Runs a command when it exists and is enabled; returns whether it ran.</summary>
    public bool TryRun(string id)
    {
        if (!_commands.TryGetValue(id, out var command) || !command.Enabled)
            return false;
        command.Run();
        return true;
    }
}
