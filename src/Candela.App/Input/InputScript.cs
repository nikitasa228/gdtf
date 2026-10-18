using System.Globalization;
using System.Text.Json;

namespace Candela.App.Input;

/// <summary>Kinds of scripted actions.</summary>
internal enum ScriptActionType
{
    /// <summary>Do nothing for <c>frames</c> frames.</summary>
    Wait,

    /// <summary>Move the mouse to (x, y).</summary>
    Move,

    /// <summary>Click (optionally double-click) at (x, y) with a button and modifiers.</summary>
    Click,

    /// <summary>Press at (x, y), move to (toX, toY) in <c>steps</c> frames, release.</summary>
    Drag,

    /// <summary>Mouse wheel at (x, y) by <c>delta</c> notches (positive = away from the user / zoom in).</summary>
    Scroll,

    /// <summary>Press and release a key chord ("Ctrl+Z", "F", "Delete").</summary>
    Key,

    /// <summary>Type text into the focused field.</summary>
    Text,

    /// <summary>Select a main-menu item by its labels ("File > Settings…"; localised text, English text or key names).</summary>
    Menu,

    /// <summary>Run an editor action by id ("app.settings", "edit.undo").</summary>
    Action,

    /// <summary>Save the frame as PNG (in addition to the final <c>--screenshot</c>).</summary>
    Screenshot,

    /// <summary>Open or close a panel by id.</summary>
    Panel,

    /// <summary>Undock a panel into a floating window at (x, y) with an optional size.</summary>
    Undock,

    /// <summary>Set the tab style ("TopTabs" or "LeftIcons") of the group containing a panel.</summary>
    TabStyle,

    /// <summary>Configure a viewport (view, projection, shading, overlay, grid).</summary>
    Viewport,

    /// <summary>Select entities by name.</summary>
    Select,

    /// <summary>Apply a layout (built-in preset or saved name).</summary>
    Layout,

    /// <summary>Change a setting ("uiScale", "language", "fontSize").</summary>
    Setting,
}

/// <summary>One scripted action. Coordinates are pixels of the main window's client area (top-left origin).</summary>
internal sealed record ScriptAction(ScriptActionType Type)
{
    /// <summary>X, pixels.</summary>
    public float X { get; init; }

    /// <summary>Y, pixels.</summary>
    public float Y { get; init; }

    /// <summary>Drag end X, pixels.</summary>
    public float ToX { get; init; }

    /// <summary>Drag end Y, pixels.</summary>
    public float ToY { get; init; }

    /// <summary>Frames (wait) or interpolation steps (drag).</summary>
    public int Frames { get; init; } = 1;

    /// <summary>Mouse button: 0 left, 1 right, 2 middle.</summary>
    public int Button { get; init; }

    /// <summary>Modifiers held: any of "ctrl", "shift", "alt", "super" separated by '+', ',' or spaces.</summary>
    public string Modifiers { get; init; } = "";

    /// <summary>Double click.</summary>
    public bool Double { get; init; }

    /// <summary>Wheel notches.</summary>
    public float Delta { get; init; }

    /// <summary>Text, chord, menu path, action id, file path, panel id, layout name or setting name.</summary>
    public string Text { get; init; } = "";

    /// <summary>Setting value.</summary>
    public string Value { get; init; } = "";

    /// <summary>Panel id (undock, tabStyle).</summary>
    public string Panel { get; init; } = "";

    /// <summary>Tab style ("TopTabs" or "LeftIcons").</summary>
    public string Style { get; init; } = "";

    /// <summary>Viewport panel id (viewport action; default "Viewport1").</summary>
    public string ViewportId { get; init; } = "";

    /// <summary>Open flag for panels / grid flag for viewports.</summary>
    public bool Flag { get; init; } = true;

    /// <summary>Viewport camera view ("Top", "Front", "Perspective"…).</summary>
    public string View { get; init; } = "";

    /// <summary>Viewport shading.</summary>
    public string Shading { get; init; } = "";

    /// <summary>Viewport overlay.</summary>
    public string Overlay { get; init; } = "";

    /// <summary>Viewport projection.</summary>
    public string Projection { get; init; } = "";

    /// <summary>Width (undocked panel), pixels.</summary>
    public float Width { get; init; }

    /// <summary>Height (undocked panel), pixels.</summary>
    public float Height { get; init; }

    /// <summary>Entity names (select).</summary>
    public IReadOnlyList<string> Names { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Scripted input for the headless verification path (<c>--script actions.json</c>). The file holds
/// <c>{ "actions": [ { "type": "click", "x": 640, "y": 360, "modifiers": "shift" }, … ] }</c>; see docs/editor.md for every
/// action type. Unknown types and malformed values are reported with the action's index.
/// </summary>
internal sealed class InputScript
{
    /// <summary>The actions in order.</summary>
    public IReadOnlyList<ScriptAction> Actions { get; }

    /// <summary>Creates a script from actions.</summary>
    public InputScript(IReadOnlyList<ScriptAction> actions)
    {
        Actions = actions ?? throw new ArgumentNullException(nameof(actions));
    }

    /// <summary>Reads a script file.</summary>
    /// <exception cref="FormatException">The JSON is not a valid script.</exception>
    public static InputScript Load(string path) => Parse(File.ReadAllText(path));

    /// <summary>Parses script JSON.</summary>
    /// <exception cref="FormatException">The JSON is not a valid script.</exception>
    public static InputScript Parse(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException e)
        {
            throw new FormatException($"The script is not valid JSON: {e.Message}", e);
        }

        using (doc)
        {
            var root = doc.RootElement;
            JsonElement array = root.ValueKind == JsonValueKind.Array ? root
                : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("actions", out var a) ? a
                : throw new FormatException("A script is an object with an \"actions\" array (or the array itself).");
            if (array.ValueKind != JsonValueKind.Array)
                throw new FormatException("\"actions\" must be an array.");
            var actions = new List<ScriptAction>();
            int index = 0;
            foreach (var item in array.EnumerateArray())
            {
                try
                {
                    actions.Add(ParseAction(item));
                }
                catch (Exception e) when (e is FormatException or InvalidOperationException or KeyNotFoundException)
                {
                    throw new FormatException($"Action {index}: {e.Message}", e);
                }

                index++;
            }

            return new InputScript(actions);
        }
    }

    private static ScriptAction ParseAction(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object)
            throw new FormatException("each action is an object");
        string typeText = Str(e, "type") ?? throw new FormatException("missing \"type\"");
        if (!Enum.TryParse(typeText, ignoreCase: true, out ScriptActionType type) || !Enum.IsDefined(type))
            throw new FormatException($"unknown type '{typeText}'");
        var action = new ScriptAction(type)
        {
            X = Num(e, "x"),
            Y = Num(e, "y"),
            ToX = Num(e, "toX"),
            ToY = Num(e, "toY"),
            Frames = (int)Math.Clamp(Num(e, "frames", Num(e, "steps", 1)), 1, 100_000),
            Button = ParseButton(Str(e, "button")),
            Modifiers = Str(e, "modifiers") ?? "",
            Double = Bool(e, "double", false),
            Delta = Num(e, "delta"),
            Text = Str(e, "text") ?? Str(e, "chord") ?? Str(e, "path") ?? Str(e, "id") ?? Str(e, "name") ?? "",
            Value = Str(e, "value") ?? "",
            Panel = Str(e, "panel") ?? "",
            Style = Str(e, "style") ?? "",
            ViewportId = Str(e, "viewport") ?? "",
            Flag = Bool(e, "open", Bool(e, "grid", true)),
            View = Str(e, "view") ?? "",
            Shading = Str(e, "shading") ?? "",
            Overlay = Str(e, "overlay") ?? "",
            Projection = Str(e, "projection") ?? "",
            Width = Num(e, "width"),
            Height = Num(e, "height"),
            Names = e.TryGetProperty("names", out var names) && names.ValueKind == JsonValueKind.Array
                ? names.EnumerateArray().Select(n => n.GetString() ?? "").Where(n => n.Length > 0).ToList()
                : Array.Empty<string>(),
        };
        // "panel" names the panel of panel/undock/tabStyle actions; a panel action may also give it as "name"/"id".
        if (action.Type == ScriptActionType.Panel && action.Panel.Length == 0)
            action = action with { Panel = action.Text };
        Validate(action);
        return action;
    }

    private static void Validate(ScriptAction a)
    {
        bool needsText = a.Type is ScriptActionType.Key or ScriptActionType.Menu or ScriptActionType.Action or ScriptActionType.Screenshot
            or ScriptActionType.Layout or ScriptActionType.Setting;
        if (needsText && a.Text.Length == 0)
            throw new FormatException($"'{a.Type}' needs a value (text/chord/path/id/name)");
        if (a.Type is ScriptActionType.Panel or ScriptActionType.Undock or ScriptActionType.TabStyle && a.Panel.Length == 0)
            throw new FormatException($"'{a.Type}' needs \"panel\"");
        if (a.Type == ScriptActionType.TabStyle && a.Style.Length == 0)
            throw new FormatException("'tabStyle' needs \"style\"");
        if (a.Type == ScriptActionType.Key)
            _ = Candela.Core.Settings.KeyChord.Parse(a.Text);
        if (a.Type == ScriptActionType.Select && a.Names.Count == 0)
            throw new FormatException("'select' needs \"names\"");
    }

    private static int ParseButton(string? s) => s?.ToLowerInvariant() switch
    {
        null or "" or "left" or "0" => 0,
        "right" or "1" => 1,
        "middle" or "2" => 2,
        _ => throw new FormatException($"unknown button '{s}'"),
    };

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        } : null;

    private static float Num(JsonElement e, string name, float fallback = 0f)
    {
        if (!e.TryGetProperty(name, out var v))
            return fallback;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double d) && double.IsFinite(d))
            return (float)d;
        if (v.ValueKind == JsonValueKind.String && float.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out float f) && float.IsFinite(f))
            return f;
        throw new FormatException($"\"{name}\" must be a number");
    }

    private static bool Bool(JsonElement e, string name, bool fallback) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new FormatException($"\"{name}\" must be true or false"),
        } : fallback;
}
