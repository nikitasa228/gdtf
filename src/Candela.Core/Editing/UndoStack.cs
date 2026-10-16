namespace Candela.Core.Editing;

/// <summary>An edit that can be undone. Every change the user makes to the scene goes through one.</summary>
public interface IUndoableCommand
{
    /// <summary>Shown in Edit → Undo "…".</summary>
    string Description { get; }

    void Do();
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

    public UndoStack(int capacity = 500) => Capacity = capacity;

    public int Capacity { get; }
    public bool CanUndo => _done.Count > 0;
    public bool CanRedo => _undone.Count > 0;
    public string? UndoDescription => CanUndo ? _done[^1].Description : null;
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
    public string Description { get; } = description;
    public List<IUndoableCommand> Commands { get; } = new();

    public void Do()
    {
        foreach (var c in Commands) c.Do();
    }

    public void Undo()
    {
        for (int i = Commands.Count - 1; i >= 0; i--) Commands[i].Undo();
    }
}
