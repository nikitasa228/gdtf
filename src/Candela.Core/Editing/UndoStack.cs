namespace Candela.Core.Editing;

/// <summary>An edit that can be undone. Every change the user makes to the scene goes through one.</summary>
public interface IUndoableCommand
{
    /// <summary>
    /// Shown in Edit → Undo "…". Read on every frame by menus and toolbars, so implementations compute it once (when the
    /// command is created) and return the stored string.
    /// </summary>
    string Description { get; }

    /// <summary>Applies the edit (first execution and redo).</summary>
    void Do();

    /// <summary>Reverts what <see cref="Do"/> applied.</summary>
    void Undo();

    /// <summary>
    /// Lets continuous edits (dragging a gizmo, a slider) collapse into one undo step: return true after absorbing
    /// <paramref name="next"/> into this command.
    /// </summary>
    bool TryMerge(IUndoableCommand next) => false;
}

/// <summary>Undo/redo history with grouping (several commands → one step) and merging of continuous edits.</summary>
public sealed class UndoStack
{
    private readonly List<IUndoableCommand> _done = new();
    private readonly List<IUndoableCommand> _undone = new();
    private CompositeCommand? _group;
    private int _groupDepth;
    private bool _mergeBarrier;

    /// <summary>Creates an empty history that keeps at most <paramref name="capacity"/> undo steps.</summary>
    public UndoStack(int capacity = 500) => Capacity = capacity;

    /// <summary>Maximum number of undo steps kept; the oldest step is dropped beyond it.</summary>
    public int Capacity { get; }

    /// <summary>True when there is a step to undo.</summary>
    public bool CanUndo => _done.Count > 0;

    /// <summary>True when there is an undone step to redo.</summary>
    public bool CanRedo => _undone.Count > 0;

    /// <summary>Description of the step <see cref="Undo"/> would revert, or null. Allocation-free.</summary>
    public string? UndoDescription => CanUndo ? _done[^1].Description : null;

    /// <summary>Description of the step <see cref="Redo"/> would apply again, or null. Allocation-free.</summary>
    public string? RedoDescription => CanRedo ? _undone[^1].Description : null;

    /// <summary>Raised after any execute/undo/redo.</summary>
    public event Action? Changed;

    /// <summary>Runs the command and records it.</summary>
    public void Execute(IUndoableCommand command)
    {
        command.Do();
        if (_group is not null)
        {
            _group.Commands.Add(command);
            return;
        }
        Record(command);
        Changed?.Invoke();
    }

    /// <summary>Starts a group; everything executed until the matching <see cref="EndGroup"/> is one undo step.</summary>
    public void BeginGroup(string description)
    {
        if (_groupDepth++ == 0) _group = new CompositeCommand(description);
    }

    /// <summary>Ends the group started by the matching <see cref="BeginGroup"/>; an empty group records nothing.</summary>
    public void EndGroup()
    {
        if (_groupDepth == 0) throw new InvalidOperationException("EndGroup without BeginGroup.");
        if (--_groupDepth > 0) return;
        var g = _group!;
        _group = null;
        if (g.Commands.Count > 0) Record(g);
        Changed?.Invoke();
    }

    /// <summary>Group helper: <c>using (undo.Group("Align")) { … }</c>.</summary>
    public IDisposable Group(string description)
    {
        BeginGroup(description);
        return new GroupScope(this);
    }

    /// <summary>Stops the next command from merging into the previous one (call when a drag ends).</summary>
    public void BreakMerge() => _mergeBarrier = true;

    /// <summary>Reverts the most recent step (no-op when there is none).</summary>
    public void Undo()
    {
        if (!CanUndo) return;
        var c = _done[^1];
        _done.RemoveAt(_done.Count - 1);
        c.Undo();
        _undone.Add(c);
        _mergeBarrier = true;
        Changed?.Invoke();
    }

    /// <summary>Applies the most recently undone step again (no-op when there is none).</summary>
    public void Redo()
    {
        if (!CanRedo) return;
        var c = _undone[^1];
        _undone.RemoveAt(_undone.Count - 1);
        c.Do();
        _done.Add(c);
        _mergeBarrier = true;
        Changed?.Invoke();
    }

    /// <summary>Forgets the whole history (e.g. after loading another project).</summary>
    public void Clear()
    {
        _done.Clear();
        _undone.Clear();
        Changed?.Invoke();
    }

    private void Record(IUndoableCommand command)
    {
        _undone.Clear();
        if (!_mergeBarrier && _done.Count > 0 && _done[^1].TryMerge(command)) return;
        _mergeBarrier = false;
        _done.Add(command);
        if (_done.Count > Capacity) _done.RemoveAt(0);
    }

    private sealed class GroupScope(UndoStack stack) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            stack.EndGroup();
        }
    }
}

/// <summary>Several commands undone/redone together (in reverse order on undo).</summary>
public sealed class CompositeCommand(string description) : IUndoableCommand
{
    /// <inheritdoc/>
    public string Description { get; } = description;

    /// <summary>The commands of the step, in execution order.</summary>
    public List<IUndoableCommand> Commands { get; } = new();

    /// <inheritdoc/>
    public void Do()
    {
        foreach (var c in Commands) c.Do();
    }

    /// <inheritdoc/>
    public void Undo()
    {
        for (int i = Commands.Count - 1; i >= 0; i--) Commands[i].Undo();
    }
}
