namespace Candela.Core.Editing;

/// <summary>
/// The set of selected entities, in selection order. Click selects one, Shift+click adds/removes.
/// The last selected entity is the primary one (gizmo pivot, properties panel).
/// </summary>
public sealed class Selection
{
    private readonly List<Guid> _ids = new();

    public IReadOnlyList<Guid> Ids => _ids;
    public Guid? Primary => _ids.Count > 0 ? _ids[^1] : null;
    public int Count => _ids.Count;
    public bool IsEmpty => _ids.Count == 0;

    public event Action? Changed;

    public bool Contains(Guid id) => _ids.Contains(id);

    public void Set(Guid id)
    {
        if (_ids.Count == 1 && _ids[0] == id) return;
        _ids.Clear();
        _ids.Add(id);
        Changed?.Invoke();
    }

    public void Set(IEnumerable<Guid> ids)
    {
        var list = ids.Distinct().ToList();
        if (list.SequenceEqual(_ids)) return;
        _ids.Clear();
        _ids.AddRange(list);
        Changed?.Invoke();
    }

    public void Add(Guid id)
    {
        _ids.Remove(id);
        _ids.Add(id);
        Changed?.Invoke();
    }

    /// <summary>Shift+click behaviour: add when absent, remove when present.</summary>
    public void Toggle(Guid id)
    {
        if (!_ids.Remove(id)) _ids.Add(id);
        Changed?.Invoke();
    }

    public void Remove(Guid id)
    {
        if (_ids.Remove(id)) Changed?.Invoke();
    }

    public void Clear()
    {
        if (_ids.Count == 0) return;
        _ids.Clear();
        Changed?.Invoke();
    }

    /// <summary>Drops ids of entities that no longer exist.</summary>
    public void Prune(Func<Guid, bool> exists)
    {
        if (_ids.RemoveAll(id => !exists(id)) > 0) Changed?.Invoke();
    }
}
