using System.Numerics;
using System.Reflection;
using Candela.App.Input;
using Candela.App.Shell;
using Candela.App.Ui;
using Hexa.NET.ImGui;

namespace Candela.App.Windows;

/// <summary>Kind of a message box.</summary>
internal enum MessageKind
{
    /// <summary>Information.</summary>
    Info,

    /// <summary>Something is not available yet / a warning.</summary>
    Warning,

    /// <summary>An error.</summary>
    Error,
}

/// <summary>
/// Modal message boxes (one at a time, queued): "not available yet" notices, errors that need attention, confirmations.
/// </summary>
internal sealed class MessageBoxes
{
    private const string PopupId = "##message-box";
    private readonly Queue<Message> _queue = new();
    private Message? _current;

    /// <summary>The message being shown (tests, scripts).</summary>
    public string? CurrentText => _current?.Text;

    /// <summary>Number of messages waiting or shown.</summary>
    public int Count => _queue.Count + (_current is null ? 0 : 1);

    /// <summary>Queues a message with an OK button.</summary>
    public void Show(MessageKind kind, string title, string text) => _queue.Enqueue(new Message(kind, title, text, null, null));

    /// <summary>Queues a yes/no question; <paramref name="onAnswer"/> receives the answer.</summary>
    public void Ask(string title, string text, Action<bool> onAnswer) =>
        _queue.Enqueue(new Message(MessageKind.Info, title, text, onAnswer, null));

    /// <summary>Queues a prompt for a name; <paramref name="onName"/> receives the trimmed text when confirmed.</summary>
    public void Prompt(string title, string hint, Action<string> onName) =>
        _queue.Enqueue(new Message(MessageKind.Info, title, hint, null, onName));

    /// <summary>Closes the current message (scripts: "press OK").</summary>
    public void DismissCurrent()
    {
        _current = null;
    }

    /// <summary>Draws the current message (top level, once per frame).</summary>
    public unsafe void Draw(float scale)
    {
        if (_current is null)
        {
            if (_queue.Count == 0)
                return;
            _current = _queue.Dequeue();
            _current.Input = "";
            ImGui.OpenPopup(PopupId);
        }

        var m = _current;
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.WorkPos + viewport.WorkSize * 0.5f, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSizeConstraints(new Vector2(380, 0) * scale, new Vector2(620, 600) * scale);
        if (!ImGui.IsPopupOpen(PopupId))
            ImGui.OpenPopup(PopupId);
        if (!ImGui.BeginPopupModal($"{m.Title}{PopupId}", ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings))
            return;
        var (icon, color) = m.Kind switch
        {
            MessageKind.Error => (Icons.OctagonAlert, Theme.Error),
            MessageKind.Warning => (Icons.TriangleAlert, Theme.Warning),
            _ => (Icons.Info, Theme.Note),
        };
        float width = 520 * scale;
        bool done = false;
        bool answer = false;
        if (m.OnName is not null)
        {
            ImGui.SetNextItemWidth(width);
            if (ImGui.IsWindowAppearing())
                ImGui.SetKeyboardFocusHere();
            bool enter = ImGui.InputTextWithHint("##name", m.Text, ref m.Input, 120, ImGuiInputTextFlags.EnterReturnsTrue);
            ImGui.Spacing();
            ImGui.BeginDisabled(m.Input.Trim().Length == 0);
            if (ImGui.Button(Loc.Get(Str.MsgOk), new Vector2(110, 0) * scale) || (enter && m.Input.Trim().Length > 0))
            {
                done = true;
                answer = true;
            }

            ImGui.EndDisabled();
            ImGui.SameLine();
            if (ImGui.Button(Loc.Get(Str.MsgCancel), new Vector2(110, 0) * scale) || ImGui.IsKeyPressed(ImGuiKey.Escape))
                done = true;
        }
        else
        {
            ImGui.TextColored(color, icon);
            ImGui.SameLine();
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width);
            ImGui.TextUnformatted(m.Text);
            ImGui.PopTextWrapPos();
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            if (m.OnAnswer is not null)
            {
                if (ImGui.Button(Loc.Get(Str.MsgYes), new Vector2(110, 0) * scale))
                {
                    done = true;
                    answer = true;
                }

                ImGui.SameLine();
                if (ImGui.Button(Loc.Get(Str.MsgNo), new Vector2(110, 0) * scale) || ImGui.IsKeyPressed(ImGuiKey.Escape))
                    done = true;
            }
            else if (ImGui.Button(Loc.Get(Str.MsgOk), new Vector2(110, 0) * scale) || ImGui.IsKeyPressed(ImGuiKey.Enter) ||
                     ImGui.IsKeyPressed(ImGuiKey.Escape))
            {
                done = true;
            }
        }

        if (done)
        {
            ImGui.CloseCurrentPopup();
            _current = null;
            if (m.OnAnswer is { } a)
                a(answer);
            if (answer && m.OnName is { } n)
                n(m.Input.Trim());
        }

        ImGui.EndPopup();
    }

    private sealed class Message(MessageKind kind, string title, string text, Action<bool>? onAnswer, Action<string>? onName)
    {
        public MessageKind Kind { get; } = kind;
        public string Title { get; } = title;
        public string Text { get; } = text;
        public Action<bool>? OnAnswer { get; } = onAnswer;
        public Action<string>? OnName { get; } = onName;
        public string Input = "";
    }
}

/// <summary>Help → About Candela.</summary>
internal sealed class AboutWindow
{
    private readonly WorkspaceContext _ctx;

    /// <summary>Creates the window.</summary>
    public AboutWindow(WorkspaceContext context) => _ctx = context ?? throw new ArgumentNullException(nameof(context));

    /// <summary>Whether it is shown.</summary>
    public bool IsOpen { get; set; }

    /// <summary>Version of the editor assembly.</summary>
    public static string Version =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0";

    /// <summary>Draws the window when open.</summary>
    public unsafe void Draw(float scale)
    {
        if (!IsOpen)
            return;
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.WorkPos + viewport.WorkSize * 0.5f, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSize(new Vector2(520, 0) * scale, ImGuiCond.Appearing);
        bool open = true;
        if (ImGui.Begin($"{Loc.Get(Str.HelpAbout)}###About", &open, ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.PushFont(_ctx.Fonts.Heading, ImGui.GetStyle().FontSizeBase * 1.6f);
            ImGui.TextColored(Theme.Accent, $"{Icons.Lightbulb} Candela");
            ImGui.PopFont();
            ImGui.TextDisabled(Loc.Format(Str.MsgAboutVersion, Version));
            ImGui.Spacing();
            ImGui.PushTextWrapPos(500 * scale);
            ImGui.TextUnformatted(Loc.Get(Str.MsgAboutText));
            ImGui.PopTextWrapPos();
            ImGui.Separator();
            var info = _ctx.Gpu.Context.Info;
            ImGui.TextUnformatted($"GPU: {info.DeviceName} ({info.DriverName}, Vulkan {info.ApiVersion})");
            ImGui.TextUnformatted($"Dear ImGui {ImGui.GetVersionS()} · .NET {Environment.Version}");
            ImGui.Separator();
            ImGui.TextDisabled("Inter — SIL Open Font License 1.1 · Lucide icons — ISC License");
        }

        ImGui.End();
        if (!open)
            IsOpen = false;
    }
}

/// <summary>Help → Keyboard Shortcuts: every command with its chords (read-only; edit them in Settings → Keyboard).</summary>
internal sealed class ShortcutsWindow
{
    private readonly CommandRegistry _commands;
    private readonly KeyBindingsService _keys;
    private string _filter = "";

    /// <summary>Creates the window.</summary>
    public ShortcutsWindow(CommandRegistry commands, KeyBindingsService keys)
    {
        _commands = commands ?? throw new ArgumentNullException(nameof(commands));
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
    }

    /// <summary>Whether it is shown.</summary>
    public bool IsOpen { get; set; }

    /// <summary>Draws the window when open.</summary>
    public unsafe void Draw(float scale)
    {
        if (!IsOpen)
            return;
        ImGui.SetNextWindowSize(new Vector2(560, 520) * scale, ImGuiCond.FirstUseEver);
        bool open = true;
        if (ImGui.Begin($"{Icons.Keyboard}  {Loc.Get(Str.MsgShortcutsTitle)}###Shortcuts", &open, ImGuiWindowFlags.NoCollapse))
        {
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##filter", $"{Icons.Search}  {Loc.Get(Str.ConsoleFilter)}", ref _filter, 64);
            if (ImGui.BeginTable("##shortcuts", 2, ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp))
            {
                foreach (var command in _commands.All)
                {
                    if (_filter.Length > 0 && !command.Text.Contains(_filter, StringComparison.CurrentCultureIgnoreCase))
                        continue;
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(command.Icon.Length > 0 ? $"{command.Icon}  {command.Text}" : command.Text);
                    ImGui.TableNextColumn();
                    var chords = _keys.Current.Bindings.TryGetValue(command.Id, out var list) ? list : null;
                    ImGui.TextColored(Theme.Accent, chords is { Count: > 0 } ? string.Join("   ", chords) : "—");
                }

                ImGui.EndTable();
            }
        }

        ImGui.End();
        if (!open)
            IsOpen = false;
    }
}
