using Candela.Core.Editing;
using Candela.Core.SceneGraph.Components;

namespace Candela.App.Session;

/// <summary>
/// Sets (or clears) one manual attribute value — GDTF attribute name → value in the attribute's physical unit, see
/// <see cref="FixtureComponent.ManualAttributes"/> — on one or several fixtures in one undo step. Consecutive commands
/// for the same attribute and the same fixtures merge (a slider drag is one step until
/// <see cref="UndoStack.BreakMerge"/>). Cheaper than a JSON snapshot of the whole component on every drag frame.
/// </summary>
internal sealed class ManualAttributeCommand : IUndoableCommand
{
    private readonly FixtureComponent[] _targets;
    private readonly float?[] _before;
    private readonly string _attribute;
    private float? _after;

    /// <summary>Creates the command; <paramref name="value"/> null removes the manual value (the GDTF default applies).</summary>
    /// <param name="targets">Fixtures to change (at least one).</param>
    /// <param name="attribute">GDTF attribute name, optionally <c>"Attribute@Instance"</c>.</param>
    /// <param name="value">New value in the attribute's physical unit (non-finite values are refused), or null.</param>
    /// <param name="description">Undo text.</param>
    public ManualAttributeCommand(IReadOnlyList<FixtureComponent> targets, string attribute, float? value, string description)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentException.ThrowIfNullOrEmpty(attribute);
        if (targets.Count == 0)
            throw new ArgumentException("At least one fixture is needed.", nameof(targets));
        if (value is { } v && !float.IsFinite(v))
            throw new ArgumentOutOfRangeException(nameof(value), "Manual values must be finite.");
        _targets = targets.ToArray();
        _attribute = attribute;
        _after = value;
        _before = new float?[_targets.Length];
        for (int i = 0; i < _targets.Length; i++)
            _before[i] = _targets[i].ManualAttributes is { } values && values.TryGetValue(attribute, out float old) ? old : null;
        Description = description ?? attribute;
    }

    /// <inheritdoc/>
    public string Description { get; }

    /// <summary>The attribute this command changes.</summary>
    public string Attribute => _attribute;

    /// <summary>The value applied by <see cref="Do"/> (null = cleared).</summary>
    public float? Value => _after;

    /// <inheritdoc/>
    public void Do()
    {
        for (int i = 0; i < _targets.Length; i++)
            Apply(_targets[i], _after);
    }

    /// <inheritdoc/>
    public void Undo()
    {
        for (int i = _targets.Length - 1; i >= 0; i--)
            Apply(_targets[i], _before[i]);
    }

    /// <inheritdoc/>
    public bool TryMerge(IUndoableCommand next)
    {
        if (next is not ManualAttributeCommand m || !string.Equals(m._attribute, _attribute, StringComparison.Ordinal) ||
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

    private void Apply(FixtureComponent fixture, float? value)
    {
        // A component read from a damaged file may have no dictionary at all.
        fixture.ManualAttributes ??= new Dictionary<string, float>();
        if (value is { } v)
            fixture.ManualAttributes[_attribute] = v;
        else
            fixture.ManualAttributes.Remove(_attribute);
        fixture.NotifyChanged();
    }
}
