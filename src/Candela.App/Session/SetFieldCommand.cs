using Candela.Core.Editing;
using Candela.Core.SceneGraph;

namespace Candela.App.Session;

/// <summary>
/// Sets one field of a component on several entities at once (multi-selection editing in the properties panel) as one
/// undo step. Consecutive commands for the same field and the same components merge, so a slider drag is one step until
/// <see cref="UndoStack.BreakMerge"/>. Every write raises the component's change notification.
/// </summary>
/// <typeparam name="TComponent">Component type.</typeparam>
/// <typeparam name="TValue">Field type.</typeparam>
internal sealed class SetFieldCommand<TComponent, TValue> : IUndoableCommand where TComponent : Component
{
    private readonly TComponent[] _targets;
    private readonly TValue[] _before;
    private readonly string _field;
    private readonly Action<TComponent, TValue> _set;
    private TValue _after;

    /// <summary>Creates the command (the values before are read now).</summary>
    /// <param name="targets">Components to change (at least one).</param>
    /// <param name="field">Name of the field (merge key and part of the description).</param>
    /// <param name="get">Reads the field.</param>
    /// <param name="set">Writes the field.</param>
    /// <param name="value">The new value.</param>
    /// <param name="description">Undo text.</param>
    public SetFieldCommand(IReadOnlyList<TComponent> targets, string field, Func<TComponent, TValue> get, Action<TComponent, TValue> set, TValue value,
        string description)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentException.ThrowIfNullOrEmpty(field);
        ArgumentNullException.ThrowIfNull(get);
        if (targets.Count == 0)
            throw new ArgumentException("At least one component is needed.", nameof(targets));
        _targets = targets.ToArray();
        _field = field;
        _set = set ?? throw new ArgumentNullException(nameof(set));
        _after = value;
        _before = new TValue[_targets.Length];
        for (int i = 0; i < _targets.Length; i++)
            _before[i] = get(_targets[i]);
        Description = description ?? field;
    }

    /// <inheritdoc/>
    public string Description { get; }

    /// <inheritdoc/>
    public void Do()
    {
        foreach (var target in _targets)
        {
            _set(target, _after);
            target.NotifyChanged();
        }
    }

    /// <inheritdoc/>
    public void Undo()
    {
        for (int i = _targets.Length - 1; i >= 0; i--)
        {
            _set(_targets[i], _before[i]);
            _targets[i].NotifyChanged();
        }
    }

    /// <inheritdoc/>
    public bool TryMerge(IUndoableCommand next)
    {
        if (next is not SetFieldCommand<TComponent, TValue> m || !string.Equals(m._field, _field, StringComparison.Ordinal) ||
            m._targets.Length != _targets.Length)
            return false;
        for (int i = 0; i < _targets.Length; i++)
        {
            if (!ReferenceEquals(m._targets[i], _targets[i]))
                return false;
        }

        _after = m._after;
        return true;
    }
}
