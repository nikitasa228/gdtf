namespace Candela.Core.Editing;

/// <summary>
/// The set of selected entities (by <see cref="SceneGraph.Entity.Id"/>), in selection order. Click selects one,
/// Shift+click adds/removes. The last selected entity is the primary one (gizmo pivot, properties panel).
/// <see cref="Contains"/> is a hash lookup (safe to call per entity per frame) and never allocates.
/// </summary>
public sealed class Selection
{
    private readonly List<Guid> _ids = new();
    private readonly HashSet<Guid> _set = new();

    /// <summary>Selected ids in selection order (the last one is <see cref="Primary"/>).</summary>
    public IReadOnlyList<Guid> Ids => _ids;

    /// <summary>The most recently selected id, or null when nothing is selected.</summary>
    public Guid? Primary => _ids.Count > 0 ? _ids[^1] : null;

    /// <summary>Number of selected entities.</summary>
    public int Count => _ids.Count;

    /// <summary>True when nothing is selected.</summary>
    public bool IsEmpty => _ids.Count == 0;

    /// <summary>Raised after every change of the selection (not when an operation changes nothing).</summary>
    public event Action? Changed;

    /// <summary>True when <paramref name="id"/> is selected. O(1), allocation-free.</summary>
    public bool Contains(Guid id) => _set.Contains(id);

    /// <summary>Selects exactly <paramref name="id"/> (a plain click).</summary>
    public void Set(Guid id)
    {
        if (_ids.Count == 1 && _ids[0] == id) return;
        _ids.Clear();
        _set.Clear();
        _ids.Add(id);
        _set.Add(id);
        Changed?.Invoke();
    }

    /// <summary>Selects exactly <paramref name="ids"/> in the given order (repeated ids count once, at their first position).</summary>
    public void Set(IEnumerable<Guid> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var list = ids.Distinct().ToList();
        if (list.SequenceEqual(_ids)) return;
        _ids.Clear();
        _set.Clear();
        _ids.AddRange(list);
        _set.UnionWith(list);
        Changed?.Invoke();
    }

    /// <summary>
    /// Adds <paramref name="id"/> as the primary selection (moved to the end when already selected; no change when it
    /// already is the primary one).
    /// </summary>
    public void Add(Guid id)
    {
        if (_ids.Count > 0 && _ids[^1] == id) return;
        if (!_set.Add(id)) _ids.Remove(id);
        _ids.Add(id);
        Changed?.Invoke();
    }

    /// <summary>Shift+click behaviour: add when absent (as the primary selection), remove when present.</summary>
    public void Toggle(Guid id)
    {
        if (_set.Remove(id)) _ids.Remove(id);
        else
        {
            _set.Add(id);
            _ids.Add(id);
        }
        Changed?.Invoke();
    }

    /// <summary>Deselects <paramref name="id"/> (no change when it is not selected).</summary>
    public void Remove(Guid id)
    {
        if (!_set.Remove(id)) return;
        _ids.Remove(id);
        Changed?.Invoke();
    }

    /// <summary>Deselects everything.</summary>
    public void Clear()
    {
        if (_ids.Count == 0) return;
        _ids.Clear();
        _set.Clear();
        Changed?.Invoke();
    }

    /// <summary>Drops ids of entities that no longer exist (<paramref name="exists"/> returns false for them).</summary>
    public void Prune(Func<Guid, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(exists);
        if (_ids.RemoveAll(id => !exists(id)) == 0) return;
        _set.Clear();
        _set.UnionWith(_ids);
        Changed?.Invoke();
    }
}
