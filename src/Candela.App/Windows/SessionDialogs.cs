using System.Globalization;
using System.Numerics;
using Candela.App.Ui;
using Candela.Project;
using Hexa.NET.ImGui;

namespace Candela.App.Windows;

/// <summary>Answer to "save the changes first?".</summary>
internal enum UnsavedChoice
{
    /// <summary>Save, then continue.</summary>
    Save,

    /// <summary>Continue without saving (the changes are lost).</summary>
    Discard,

    /// <summary>Do not continue.</summary>
    Cancel,
}

/// <summary>Answer to "open the dropped scene or import it?".</summary>
internal enum DropChoice
{
    /// <summary>Open it in place of the current scene.</summary>
    Open,

    /// <summary>Merge it into the current scene.</summary>
    Import,

    /// <summary>Do nothing.</summary>
    Cancel,
}

/// <summary>Which session dialog is showing.</summary>
internal enum SessionDialogKind
{
    /// <summary>None.</summary>
    None,

    /// <summary>A long operation is running ("Opening Фокина.mvr…").</summary>
    Busy,

    /// <summary>Save the changes before the scene is closed?</summary>
    Unsaved,

    /// <summary>Open or import a dropped scene?</summary>
    OpenOrImport,

    /// <summary>Autosaved sessions of runs that did not end cleanly.</summary>
    Recovery,
}

/// <summary>
/// The modal dialogs of the document session: a busy overlay shown while a file operation runs (the operation itself
/// runs between two frames, after the overlay has been presented once), the unsaved-changes question (Save / Don't Save
/// / Cancel) before New, Open and Exit, the open-or-import question for a scene dropped onto a non-empty stage, and the
/// start-up recovery prompt listing autosaved sessions of runs that ended abnormally. One dialog at a time; answers are
/// delivered through callbacks after the dialog closed.
/// </summary>
internal sealed class SessionDialogs
{
    private const string BusyId = "###session-busy";
    private const string DialogId = "###session-dialog";
    private readonly List<RecoverableSession> _sessions = new();
    private SessionDialogKind _kind;
    private string _busyText = "";
    private string _subject = "";
    private string _windowTitle = "";
    private Action<UnsavedChoice>? _onUnsaved;
    private Action<DropChoice>? _onDrop;
    private Action<RecoverableSession>? _onRecover;
    private Action<RecoverableSession>? _onDelete;
    private int _selected;
    private int _confirmDelete = -1;
    private bool _closeBusy;
    private float _spin;

    /// <summary>The dialog showing now.</summary>
    public SessionDialogKind Kind => _kind;

    /// <summary>True while any session dialog is showing (other modal dialogs wait).</summary>
    public bool IsOpen => _kind != SessionDialogKind.None || _closeBusy;

    /// <summary>The busy text, or empty.</summary>
    public string BusyText => _kind == SessionDialogKind.Busy ? _busyText : "";

    /// <summary>The sessions offered by the recovery prompt (tests).</summary>
    public IReadOnlyList<RecoverableSession> RecoverableSessions => _sessions;

    /// <summary>Shows the busy overlay with <paramref name="text"/> (replaces a question that is showing).</summary>
    public void ShowBusy(string text)
    {
        _busyText = text ?? "";
        _kind = SessionDialogKind.Busy;
    }

    /// <summary>Hides the busy overlay.</summary>
    public void HideBusy()
    {
        if (_kind != SessionDialogKind.Busy)
            return;
        _kind = SessionDialogKind.None;
        _closeBusy = true;
    }

    /// <summary>Asks whether to save the changes of <paramref name="sceneName"/>.</summary>
    public void AskUnsaved(string sceneName, Action<UnsavedChoice> onAnswer)
    {
        ArgumentNullException.ThrowIfNull(onAnswer);
        Reset();
        _kind = SessionDialogKind.Unsaved;
        _subject = Loc.Format(Str.UnsavedQuestion, sceneName);
        _windowTitle = Loc.Get(Str.UnsavedTitle) + DialogId;
        _onUnsaved = onAnswer;
    }

    /// <summary>Asks whether a dropped scene file is opened or imported.</summary>
    public void AskOpenOrImport(string fileName, Action<DropChoice> onAnswer)
    {
        ArgumentNullException.ThrowIfNull(onAnswer);
        Reset();
        _kind = SessionDialogKind.OpenOrImport;
        _subject = Loc.Format(Str.DropQuestion, fileName);
        _windowTitle = Loc.Get(Str.DropTitle) + DialogId;
        _onDrop = onAnswer;
    }

    /// <summary>
    /// Offers <paramref name="sessions"/> for recovery. <paramref name="recover"/> and <paramref name="delete"/> run when the
    /// user chooses; the session leaves the list (the dialog closes with the last one or with "Decide Later").
    /// </summary>
    public void OfferRecovery(IReadOnlyList<RecoverableSession> sessions, Action<RecoverableSession> recover, Action<RecoverableSession> delete)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        if (sessions.Count == 0)
            return;
        Reset();
        _sessions.AddRange(sessions);
        _kind = SessionDialogKind.Recovery;
        _windowTitle = Loc.Get(Str.RecoveryTitle) + DialogId;
        _onRecover = recover ?? throw new ArgumentNullException(nameof(recover));
        _onDelete = delete ?? throw new ArgumentNullException(nameof(delete));
    }

    /// <summary>Answers the unsaved-changes question (scripts and tests; the same as clicking the button).</summary>
    public void Answer(UnsavedChoice choice)
    {
        if (_kind != SessionDialogKind.Unsaved)
            return;
        var callback = _onUnsaved;
        Close();
        callback?.Invoke(choice);
    }

    /// <summary>Answers the open-or-import question (scripts and tests).</summary>
    public void Answer(DropChoice choice)
    {
        if (_kind != SessionDialogKind.OpenOrImport)
            return;
        var callback = _onDrop;
        Close();
        callback?.Invoke(choice);
    }

    /// <summary>Recovers the session at <paramref name="index"/> of the recovery list (scripts and tests).</summary>
    public void Recover(int index)
    {
        if (_kind != SessionDialogKind.Recovery || (uint)index >= (uint)_sessions.Count)
            return;
        var session = _sessions[index];
        var callback = _onRecover;
        // Recovering replaces the scene: the other sessions stay on disk and are offered again at the next start.
        Close();
        callback?.Invoke(session);
    }

    /// <summary>Deletes the autosave of the session at <paramref name="index"/> (scripts and tests).</summary>
    public void Delete(int index)
    {
        if (_kind != SessionDialogKind.Recovery || (uint)index >= (uint)_sessions.Count)
            return;
        var session = _sessions[index];
        _sessions.RemoveAt(index);
        _selected = Math.Clamp(_selected, 0, Math.Max(0, _sessions.Count - 1));
        _confirmDelete = -1;
        var callback = _onDelete;
        if (_sessions.Count == 0)
            Close();
        callback?.Invoke(session);
    }

    /// <summary>Closes the recovery prompt; the sessions stay recoverable.</summary>
    public void DecideLater()
    {
        if (_kind == SessionDialogKind.Recovery)
            Close();
    }

    /// <summary>Draws the current dialog (top level, once per frame, inside the ImGui frame).</summary>
    public void Draw(float scale, float deltaTime)
    {
        if (_closeBusy)
        {
            _closeBusy = false;
            if (ImGui.IsPopupOpen(BusyId) && ImGui.BeginPopupModal(BusyId, ImGuiWindowFlags.NoTitleBar))
            {
                ImGui.CloseCurrentPopup();
                ImGui.EndPopup();
            }
        }

        switch (_kind)
        {
            case SessionDialogKind.Busy:
                DrawBusy(scale, deltaTime);
                break;
            case SessionDialogKind.Unsaved:
            case SessionDialogKind.OpenOrImport:
            case SessionDialogKind.Recovery:
                DrawQuestion(scale);
                break;
        }
    }

    private void DrawBusy(float scale, float deltaTime)
    {
        if (!ImGui.IsPopupOpen(BusyId))
            ImGui.OpenPopup(BusyId);
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.WorkPos + viewport.WorkSize * 0.5f, ImGuiCond.Always, new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSizeConstraints(new Vector2(320, 0) * scale, new Vector2(640, 400) * scale);
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoMove |
                                       ImGuiWindowFlags.NoSavedSettings;
        if (!ImGui.BeginPopupModal(BusyId, flags))
            return;
        _spin += float.IsFinite(deltaTime) ? Math.Clamp(deltaTime, 0f, 0.1f) * 6f : 0f;
        float radius = 10f * scale;
        var p = ImGui.GetCursorScreenPos() + new Vector2(radius + 2f * scale, radius + 4f * scale);
        var draw = ImGui.GetWindowDrawList();
        draw.PathArcTo(p, radius, _spin, _spin + MathF.PI * 1.5f, 24);
        draw.PathStroke(Theme.U32(Theme.Accent), ImDrawFlags.None, 2.5f * scale);
        ImGui.Dummy(new Vector2(2f * radius + 4f * scale, 2f * radius + 8f * scale));
        ImGui.SameLine(0, 12f * scale);
        ImGui.BeginGroup();
        ImGui.TextUnformatted(_busyText);
        ImGui.TextColored(Theme.TextMuted, Loc.Get(Str.BusyPleaseWait));
        ImGui.EndGroup();
        ImGui.EndPopup();
    }

    private void DrawQuestion(float scale)
    {
        if (!ImGui.IsPopupOpen(DialogId))
            ImGui.OpenPopup(DialogId);
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.WorkPos + viewport.WorkSize * 0.5f, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSizeConstraints(new Vector2(420, 0) * scale, new Vector2(820, 640) * scale);
        if (!ImGui.BeginPopupModal(_windowTitle, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings))
            return;
        var kind = _kind;
        Action? after = kind switch
        {
            SessionDialogKind.Unsaved => DrawUnsaved(scale),
            SessionDialogKind.OpenOrImport => DrawOpenOrImport(scale),
            _ => DrawRecovery(scale),
        };
        if (_kind == SessionDialogKind.None)
            ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
        after?.Invoke();
    }

    private Action? DrawUnsaved(float scale)
    {
        Message(Icons.TriangleAlert, Theme.Warning, _subject, scale);
        var size = new Vector2(130, 0) * scale;
        UnsavedChoice? choice = null;
        if (Accent(Labels.With(Icons.Save, Str.UnsavedSave), size) || ImGui.IsKeyPressed(ImGuiKey.Enter, false))
            choice = UnsavedChoice.Save;
        ImGui.SameLine();
        if (ImGui.Button(Loc.Get(Str.UnsavedDiscard), size))
            choice = UnsavedChoice.Discard;
        ImGui.SameLine();
        if (ImGui.Button(Loc.Get(Str.MsgCancel), size) || ImGui.IsKeyPressed(ImGuiKey.Escape, false))
            choice = UnsavedChoice.Cancel;
        if (choice is not { } c)
            return null;
        var callback = _onUnsaved;
        Close();
        return () => callback?.Invoke(c);
    }

    private Action? DrawOpenOrImport(float scale)
    {
        Message(Icons.Theater, Theme.Note, _subject, scale);
        var size = new Vector2(130, 0) * scale;
        DropChoice? choice = null;
        if (Accent(Labels.With(Icons.FolderOpen, Str.DropOpen), size) || ImGui.IsKeyPressed(ImGuiKey.Enter, false))
            choice = DropChoice.Open;
        ImGui.SameLine();
        if (ImGui.Button(Labels.With(Icons.FileInput, Str.DropImport), size))
            choice = DropChoice.Import;
        ImGui.SameLine();
        if (ImGui.Button(Loc.Get(Str.MsgCancel), size) || ImGui.IsKeyPressed(ImGuiKey.Escape, false))
            choice = DropChoice.Cancel;
        if (choice is not { } c)
            return null;
        var callback = _onDrop;
        Close();
        return () => callback?.Invoke(c);
    }

    private Action? DrawRecovery(float scale)
    {
        Message(Icons.History, Theme.Warning, Loc.Get(Str.RecoveryText), scale);
        const ImGuiTableFlags tableFlags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp |
                                           ImGuiTableFlags.ScrollY;
        float rowHeight = ImGui.GetFrameHeightWithSpacing();
        float tableHeight = rowHeight * (Math.Min(_sessions.Count, 6) + 1.4f);
        if (ImGui.BeginTable("##recovery", 3, tableFlags, new Vector2(680 * scale, tableHeight)))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn(Loc.Get(Str.RecoveryScene), ImGuiTableColumnFlags.WidthStretch, 1.1f);
            ImGui.TableSetupColumn(Loc.Get(Str.RecoveryTime), ImGuiTableColumnFlags.WidthStretch, 0.8f);
            ImGui.TableSetupColumn(Loc.Get(Str.RecoveryFile), ImGuiTableColumnFlags.WidthStretch, 1.6f);
            ImGui.TableHeadersRow();
            for (int i = 0; i < _sessions.Count; i++)
            {
                var s = _sessions[i];
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                string name = string.IsNullOrWhiteSpace(s.SceneName) ? Loc.Get(Str.Untitled) : s.SceneName!;
                if (ImGui.Selectable($"{Icons.File}  {name}##recover{i}", _selected == i, ImGuiSelectableFlags.SpanAllColumns))
                {
                    _selected = i;
                    _confirmDelete = -1;
                }

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(s.LastSavedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture));
                ImGui.TableNextColumn();
                if (s.OriginalPath is { Length: > 0 } path)
                {
                    ImGui.TextUnformatted(Path.GetFileName(path));
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(path);
                }
                else
                {
                    ImGui.TextColored(Theme.TextMuted, Loc.Get(Str.RecoveryNeverSaved));
                }
            }

            ImGui.EndTable();
        }

        ImGui.Spacing();
        var size = new Vector2(150, 0) * scale;
        int index = Math.Clamp(_selected, 0, Math.Max(0, _sessions.Count - 1));
        Action? after = null;
        if (Accent(Labels.With(Icons.RotateCcw, Str.RecoveryRecover), size))
        {
            var session = _sessions[index];
            var callback = _onRecover;
            Close();
            after = () => callback?.Invoke(session);
        }

        ImGui.SameLine();
        bool confirming = _confirmDelete == index;
        if (confirming)
            ImGui.PushStyleColor(ImGuiCol.Button, Theme.WithAlpha(Theme.Error, 0.6f));
        if (ImGui.Button(confirming ? $"{Icons.Trash}  {Loc.Get(Str.RecoveryDelete)}?" : Labels.With(Icons.Trash, Str.RecoveryDelete), size) && after is null)
        {
            if (confirming)
            {
                var session = _sessions[index];
                _sessions.RemoveAt(index);
                _selected = Math.Clamp(_selected, 0, Math.Max(0, _sessions.Count - 1));
                _confirmDelete = -1;
                var callback = _onDelete;
                if (_sessions.Count == 0)
                    Close();
                after = () => callback?.Invoke(session);
            }
            else
            {
                _confirmDelete = index;
            }
        }

        if (confirming)
            ImGui.PopStyleColor();
        ImGui.SameLine();
        if ((ImGui.Button(Loc.Get(Str.RecoveryLater), size) || ImGui.IsKeyPressed(ImGuiKey.Escape, false)) && after is null)
            Close();
        return after;
    }

    private static void Message(string icon, Vector4 color, string text, float scale)
    {
        ImGui.TextColored(color, icon);
        ImGui.SameLine();
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + 560 * scale);
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
    }

    private static bool Accent(string label, Vector2 size)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, Theme.WithAlpha(Theme.Accent, 0.55f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Theme.WithAlpha(Theme.Accent, 0.75f));
        bool clicked = ImGui.Button(label, size);
        ImGui.PopStyleColor(2);
        return clicked;
    }

    private void Close()
    {
        _kind = SessionDialogKind.None;
        Reset();
    }

    private void Reset()
    {
        _sessions.Clear();
        _onUnsaved = null;
        _onDrop = null;
        _onRecover = null;
        _onDelete = null;
        _selected = 0;
        _confirmDelete = -1;
    }
}
