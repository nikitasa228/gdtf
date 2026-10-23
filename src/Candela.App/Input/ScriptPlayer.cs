using System.Numerics;
using Candela.App.Diagnostics;
using Candela.Core.Settings;
using Hexa.NET.ImGui;

namespace Candela.App.Input;

/// <summary>What scripted actions can ask of the editor besides raw input.</summary>
internal interface IScriptTarget
{
    /// <summary>Selects a main-menu item by its label path (consumed while the menu bar is drawn).</summary>
    void RunMenu(IReadOnlyList<string> path);

    /// <summary>Runs an editor command; false when unknown or disabled.</summary>
    bool RunAction(string id);

    /// <summary>Opens or closes a panel.</summary>
    void SetPanelOpen(string panelId, bool open);

    /// <summary>Undocks a panel into a floating window (position/size in main-window pixels; 0 = keep).</summary>
    void UndockPanel(string panelId, Vector2 position, Vector2 size);

    /// <summary>Sets the tab style of the group containing a panel.</summary>
    void SetTabStyle(string panelId, string style);

    /// <summary>Configures a viewport (empty strings keep the current value).</summary>
    void ConfigureViewport(string viewportId, string view, string projection, string shading, string overlay, bool grid);

    /// <summary>Selects entities by name.</summary>
    void SelectByName(IReadOnlyList<string> names);

    /// <summary>Applies a layout preset or saved layout.</summary>
    void ApplyLayout(string name);

    /// <summary>Changes a setting by name.</summary>
    void SetSetting(string name, string value);

    /// <summary>Resizes the main window (screen pixels) or sets its state ("minimize", "restore", "maximize").</summary>
    void SetMainWindow(int width, int height, string state);

    /// <summary>Opens a scene or project (asks about unsaved changes like File → Open).</summary>
    void OpenFile(string path);

    /// <summary>Drops files onto the main window at <paramref name="position"/> (main-window pixels), like a file manager.</summary>
    void DropFiles(IReadOnlyList<string> paths, Vector2 position);

    /// <summary>Saves the scene as a project file (File → Save As with that file chosen).</summary>
    void SaveFile(string path);

    /// <summary>The main window (for marking the cursor as inside it).</summary>
    Hexa.NET.ImGui.Backends.GLFW.GLFWwindowPtr MainWindow { get; }

    /// <summary>
    /// The screen point (Dear ImGui coordinates) of a world point or of an entity's drawn centre in the active viewport,
    /// or null when it is not visible there.
    /// </summary>
    Vector2? ScreenPointOf(Vector3? world, string entityName);

    /// <summary>Scrolls the Library's fixture grid so that tile <paramref name="index"/> is in view at the next frame.</summary>
    void RevealLibraryTile(int index);

    /// <summary>The screen point (Dear ImGui coordinates) of the centre of Library tile <paramref name="index"/>, or null when not in view.</summary>
    Vector2? LibraryTilePoint(int index);
}

/// <summary>
/// Replays an <see cref="InputScript"/> frame by frame: every action is expanded into input events (mouse position,
/// buttons, wheel, modifier and key events, text) queued into Dear ImGui before the frame's <c>NewFrame</c>, so scripted
/// clicks go through exactly the same code as real ones; editor-level actions (menu by label, commands, panels,
/// layouts, viewport setup) are handed to the <see cref="IScriptTarget"/>. Coordinates are relative to the main window.
/// </summary>
internal sealed class ScriptPlayer
{
    private readonly List<Step>[] _frames;
    private readonly Dictionary<int, string> _screenshots = new();
    private readonly Dictionary<int, string> _textLogs = new();
    private readonly AppLog _log;
    private bool _started;

    /// <summary>Expands <paramref name="script"/> into a per-frame schedule.</summary>
    public ScriptPlayer(InputScript script, AppLog log)
    {
        ArgumentNullException.ThrowIfNull(script);
        _log = log ?? throw new ArgumentNullException(nameof(log));
        var schedule = new List<List<Step>>();
        int frame = 1; // frame 0 lets the UI lay out once before input arrives
        foreach (var a in script.Actions)
            frame = Expand(a, frame, schedule);
        while (schedule.Count <= frame)
            schedule.Add(new List<Step>());
        _frames = schedule.ToArray();
    }

    /// <summary>Frames the script needs (the last frame with an event + 1).</summary>
    public int FrameCount => _frames.Length;

    /// <summary>True when the script saves a screenshot at <paramref name="frame"/>.</summary>
    public bool WantsScreenshot(int frame) => _screenshots.ContainsKey(frame);

    /// <summary>The path of the screenshot taken at <paramref name="frame"/>, or null.</summary>
    public string? ScreenshotPath(int frame) => _screenshots.GetValueOrDefault(frame);

    /// <summary>
    /// The file the text drawn in <paramref name="frame"/> is appended to (<see cref="ScriptActionType.UiText"/>), or null.
    /// </summary>
    public string? TextLogPath(int frame) => _textLogs.GetValueOrDefault(frame);

    /// <summary>Queues this frame's events (call before ImGui's NewFrame).</summary>
    public void Apply(int frame, IScriptTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var io = ImGui.GetIO();
        if (!_started)
        {
            _started = true;
            // Scripted clicks are a few frames apart; on a slow software renderer that can exceed the default 0.3 s.
            io.MouseDoubleClickTime = 2f;
            // Tell the GLFW backend the cursor is inside the window, so it stops polling the real OS cursor.
            Hexa.NET.ImGui.Backends.GLFW.ImGuiImplGLFW.CursorEnterCallback(target.MainWindow, 1);
        }

        if (frame < 0 || frame >= _frames.Length)
            return;
        var origin = ImGui.GetMainViewport().Pos;
        foreach (var step in _frames[frame])
        {
            try
            {
                Run(step, io, origin, target);
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or KeyNotFoundException or FormatException)
            {
                _log.Warn("Script", $"Frame {frame}: {step.Kind} failed: {e.Message}");
            }
        }
    }

    private static void Run(Step step, ImGuiIOPtr io, Vector2 origin, IScriptTarget target)
    {
        switch (step.Kind)
        {
            case StepKind.MousePos:
                io.AddMousePosEvent(origin.X + step.Position.X, origin.Y + step.Position.Y);
                break;
            case StepKind.MouseScene:
                if (target.ScreenPointOf(step.Action!.World, step.Action.Entity) is not { } p)
                    throw new InvalidOperationException(step.Action.Entity.Length > 0
                        ? $"'{step.Action.Entity}' is not visible in the active viewport"
                        : $"the world point {step.Action.World} is not visible in the active viewport");
                io.AddMousePosEvent(p.X, p.Y);
                break;
            case StepKind.RevealTile:
                target.RevealLibraryTile(step.Action!.Tile ?? 0);
                break;
            case StepKind.MouseTile:
                {
                    int index = step.Action!.Tile ?? 0;
                    if (target.LibraryTilePoint(index) is not { } tile)
                        throw new InvalidOperationException($"Library tile {index} is not in view");
                    io.AddMousePosEvent(tile.X, tile.Y);
                    break;
                }

            case StepKind.MouseButton:
                io.AddMouseButtonEvent(step.Button, step.Down);
                break;
            case StepKind.Wheel:
                io.AddMouseWheelEvent(0f, step.Amount);
                break;
            case StepKind.Modifiers:
                io.AddKeyEvent(ImGuiKey.ModCtrl, (step.Modifiers & KeyModifiers.Ctrl) != 0);
                io.AddKeyEvent(ImGuiKey.ModShift, (step.Modifiers & KeyModifiers.Shift) != 0);
                io.AddKeyEvent(ImGuiKey.ModAlt, (step.Modifiers & KeyModifiers.Alt) != 0);
                io.AddKeyEvent(ImGuiKey.ModSuper, (step.Modifiers & KeyModifiers.Super) != 0);
                break;
            case StepKind.Key:
                io.AddKeyEvent(step.Key, step.Down);
                break;
            case StepKind.Text:
                io.AddInputCharactersUTF8(step.Text);
                break;
            case StepKind.Target:
                RunTarget(step.Action!, target);
                break;
        }
    }

    private static void RunTarget(ScriptAction a, IScriptTarget target)
    {
        switch (a.Type)
        {
            case ScriptActionType.Menu:
                target.RunMenu(a.Text.Split(new[] { '>', '/' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                break;
            case ScriptActionType.Action:
                if (!target.RunAction(a.Text))
                    throw new InvalidOperationException($"action '{a.Text}' is unknown or disabled");
                break;
            case ScriptActionType.Panel:
                target.SetPanelOpen(a.Panel, a.Flag);
                break;
            case ScriptActionType.Undock:
                target.UndockPanel(a.Panel, new Vector2(a.X, a.Y), new Vector2(a.Width, a.Height));
                break;
            case ScriptActionType.TabStyle:
                target.SetTabStyle(a.Panel, a.Style);
                break;
            case ScriptActionType.Viewport:
                target.ConfigureViewport(a.ViewportId.Length > 0 ? a.ViewportId : a.Text.Length > 0 ? a.Text : "Viewport1", a.View, a.Projection, a.Shading, a.Overlay, a.Flag);
                break;
            case ScriptActionType.Select:
                target.SelectByName(a.Names);
                break;
            case ScriptActionType.Layout:
                target.ApplyLayout(a.Text);
                break;
            case ScriptActionType.Setting:
                target.SetSetting(a.Text, a.Value);
                break;
            case ScriptActionType.Window:
                target.SetMainWindow((int)a.Width, (int)a.Height, a.Value);
                break;
            case ScriptActionType.Open:
                target.OpenFile(Path.GetFullPath(a.Text));
                break;
            case ScriptActionType.Save:
                target.SaveFile(Path.GetFullPath(a.Text));
                break;
            case ScriptActionType.Drop:
                target.DropFiles((a.Names.Count > 0 ? a.Names : new[] { a.Text }).Select(Path.GetFullPath).ToList(), new Vector2(a.X, a.Y));
                break;
        }
    }

    private int Expand(ScriptAction a, int frame, List<List<Step>> schedule)
    {
        var pos = new Vector2(a.X, a.Y);
        var mods = ParseModifiers(a.Modifiers);
        // A scene point (world position or entity) is turned into a screen point when the action runs; a Library tile is
        // scrolled into view first and its centre is taken two frames later, once the grid has been drawn there.
        var mouse = a.Tile is not null ? Step.MouseOnTile(a) : a.HasScenePoint ? Step.MouseAt(a) : Step.Mouse(pos);
        if (a.Tile is not null && a.Type is ScriptActionType.Move or ScriptActionType.Click or ScriptActionType.Press or ScriptActionType.Scroll)
        {
            At(schedule, frame).Add(Step.Reveal(a));
            frame += 2;
        }
        switch (a.Type)
        {
            case ScriptActionType.Wait:
                return frame + a.Frames;
            case ScriptActionType.Move:
                At(schedule, frame).Add(mouse);
                return frame + 1;
            case ScriptActionType.Click:
                At(schedule, frame).Add(mouse);
                At(schedule, frame).Add(Step.Mods(mods));
                At(schedule, frame + 1).Add(Step.ButtonEvent(a.Button, true));
                At(schedule, frame + 2).Add(Step.ButtonEvent(a.Button, false));
                frame += 3;
                if (a.Double)
                {
                    At(schedule, frame).Add(Step.ButtonEvent(a.Button, true));
                    At(schedule, frame + 1).Add(Step.ButtonEvent(a.Button, false));
                    frame += 2;
                }

                At(schedule, frame).Add(Step.Mods(KeyModifiers.None));
                return frame + 1;
            case ScriptActionType.Drag:
                {
                    var to = new Vector2(a.ToX, a.ToY);
                    At(schedule, frame).Add(Step.Mouse(pos));
                    At(schedule, frame).Add(Step.Mods(mods));
                    At(schedule, frame + 1).Add(Step.ButtonEvent(a.Button, true));
                    int steps = Math.Max(2, a.Frames);
                    for (int i = 1; i <= steps; i++)
                        At(schedule, frame + 1 + i).Add(Step.Mouse(Vector2.Lerp(pos, to, i / (float)steps)));
                    At(schedule, frame + steps + 2).Add(Step.ButtonEvent(a.Button, false));
                    At(schedule, frame + steps + 3).Add(Step.Mods(KeyModifiers.None));
                    return frame + steps + 4;
                }

            case ScriptActionType.Press:
                At(schedule, frame).Add(mouse);
                At(schedule, frame).Add(Step.Mods(mods));
                At(schedule, frame + 1).Add(Step.ButtonEvent(a.Button, true));
                return frame + 2;
            case ScriptActionType.Release:
                At(schedule, frame).Add(Step.ButtonEvent(a.Button, false));
                At(schedule, frame + 1).Add(Step.Mods(KeyModifiers.None));
                return frame + 2;
            case ScriptActionType.Scroll:
                At(schedule, frame).Add(mouse);
                At(schedule, frame + 1).Add(Step.WheelEvent(a.Delta));
                return frame + 2;
            case ScriptActionType.Key:
                {
                    var chord = KeyChord.Parse(a.Text);
                    var key = KeyNames.ToImGui(chord.Key);
                    if (key == ImGuiKey.None)
                        throw new FormatException($"unknown key '{chord.Key}'");
                    At(schedule, frame).Add(Step.Mods(chord.Modifiers));
                    At(schedule, frame + 1).Add(Step.KeyEvent(key, true));
                    At(schedule, frame + 2).Add(Step.KeyEvent(key, false));
                    At(schedule, frame + 3).Add(Step.Mods(KeyModifiers.None));
                    return frame + 4;
                }

            case ScriptActionType.Text:
                At(schedule, frame).Add(Step.TextEvent(a.Text));
                return frame + 1;
            case ScriptActionType.Screenshot:
                // The frame after the previous action has settled.
                _screenshots[frame] = a.Text;
                At(schedule, frame);
                return frame + 1;
            case ScriptActionType.UiText:
                // Used as given (relative to the working folder); a path that cannot be opened is reported then.
                _textLogs[frame] = a.Text;
                At(schedule, frame);
                return frame + 1;
            default:
                At(schedule, frame).Add(Step.TargetAction(a));
                return frame + 2;
        }
    }

    private static List<Step> At(List<List<Step>> schedule, int frame)
    {
        while (schedule.Count <= frame)
            schedule.Add(new List<Step>());
        return schedule[frame];
    }

    private static KeyModifiers ParseModifiers(string text)
    {
        var m = KeyModifiers.None;
        foreach (var part in text.Split(new[] { '+', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            m |= part.ToLowerInvariant() switch
            {
                "ctrl" or "control" => KeyModifiers.Ctrl,
                "shift" => KeyModifiers.Shift,
                "alt" => KeyModifiers.Alt,
                "super" or "cmd" or "win" => KeyModifiers.Super,
                _ => throw new FormatException($"unknown modifier '{part}'"),
            };
        }

        return m;
    }

    private enum StepKind
    {
        MousePos,
        MouseScene,
        RevealTile,
        MouseTile,
        MouseButton,
        Wheel,
        Modifiers,
        Key,
        Text,
        Target,
    }

    private readonly record struct Step(StepKind Kind)
    {
        public Vector2 Position { get; init; }
        public int Button { get; init; }
        public bool Down { get; init; }
        public float Amount { get; init; }
        public KeyModifiers Modifiers { get; init; }
        public ImGuiKey Key { get; init; }
        public string Text { get; init; }
        public ScriptAction? Action { get; init; }

        public static Step Mouse(Vector2 p) => new(StepKind.MousePos) { Position = p, Text = "" };
        public static Step MouseAt(ScriptAction a) => new(StepKind.MouseScene) { Action = a, Text = "" };
        public static Step MouseOnTile(ScriptAction a) => new(StepKind.MouseTile) { Action = a, Text = "" };
        public static Step Reveal(ScriptAction a) => new(StepKind.RevealTile) { Action = a, Text = "" };
        public static Step ButtonEvent(int b, bool down) => new(StepKind.MouseButton) { Button = b, Down = down, Text = "" };
        public static Step WheelEvent(float amount) => new(StepKind.Wheel) { Amount = amount, Text = "" };
        public static Step Mods(KeyModifiers m) => new(StepKind.Modifiers) { Modifiers = m, Text = "" };
        public static Step KeyEvent(ImGuiKey k, bool down) => new(StepKind.Key) { Key = k, Down = down, Text = "" };
        public static Step TextEvent(string text) => new(StepKind.Text) { Text = text };
        public static Step TargetAction(ScriptAction a) => new(StepKind.Target) { Action = a, Text = "" };
    }
}
