using System.Collections;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Candela.Core.SceneGraph;

/// <summary>
/// A named group of entities (MVR layers, editor layers). Every entity names its layer by <see cref="Entity.LayerId"/>.
/// A hidden layer hides its entities (they are still simulated and analysed); a locked layer keeps them from being
/// selected or moved in the viewport. While the layer belongs to a scene, every property change raises
/// <see cref="SceneChangeKind.LayersChanged"/> on it.
/// </summary>
public sealed class Layer
{
    private string _name = "Layer";
    private bool _visible = true;
    private bool _locked;

    /// <summary>Stable identity (MVR layer uuids are kept on import). Entities refer to it by <see cref="Entity.LayerId"/>.</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Display name (null is stored as an empty name).</summary>
    public string Name
    {
        get => _name;
        set
        {
            value ??= "";
            if (_name == value) return;
            _name = value;
            Notify();
        }
    }

    /// <summary>False hides every entity of the layer in the viewport and in renders.</summary>
    public bool Visible
    {
        get => _visible;
        set
        {
            if (_visible == value) return;
            _visible = value;
            Notify();
        }
    }

    /// <summary>True keeps the entities of the layer from being selected or moved in the viewport.</summary>
    public bool Locked
    {
        get => _locked;
        set
        {
            if (_locked == value) return;
            _locked = value;
            Notify();
        }
    }

    /// <summary>
    /// JSON properties this version does not know (written by a newer version of Candela), kept so that saving again
    /// does not drop them. Opaque: never interpreted and changing them raises no scene change. Null when there are none.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownProperties { get; set; }

    /// <summary>The scene whose layer list holds this layer (the last one it was added to), or null.</summary>
    internal Scene? Owner { get; set; }

    /// <inheritdoc/>
    public override string ToString() => $"{Name} ({Id})";

    private void Notify() => Owner?.Raise(new SceneChange(SceneChangeKind.LayersChanged, null) { Layer = this });
}

/// <summary>
/// The layers of a scene, in display order. It behaves like a list (<see cref="Add"/>, <see cref="Insert"/>,
/// <see cref="Remove"/>, <see cref="Clear"/>, indexer, LINQ) and keeps an id index in sync, so <see cref="Find"/> (and
/// <see cref="Scene.FindLayer"/>) is a dictionary lookup that never allocates — it is called per entity per frame.
/// Every change raises <see cref="SceneChangeKind.LayersChanged"/> on the scene. <c>foreach</c> uses a struct enumerator
/// and does not allocate either.
/// </summary>
/// <remarks>
/// Layer ids should be unique. When a malformed file repeats one, <see cref="Find"/> returns the first layer with that
/// id in list order, like a linear search would.
/// </remarks>
public sealed class LayerCollection : IList<Layer>, IReadOnlyList<Layer>
{
    private readonly Scene _scene;
    private readonly List<Layer> _items = new();
    private readonly Dictionary<Guid, Layer> _byId = new();

    internal LayerCollection(Scene scene) => _scene = scene;

    /// <summary>Number of layers.</summary>
    public int Count => _items.Count;

    bool ICollection<Layer>.IsReadOnly => false;

    /// <summary>The layer at <paramref name="index"/>; setting replaces it (raises one change).</summary>
    public Layer this[int index]
    {
        get => _items[index];
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            var old = _items[index];
            if (ReferenceEquals(old, value)) return;
            _items[index] = value;
            Detached(old);
            Attached(value);
            Raise(value);
        }
    }

    /// <summary>The layer with <paramref name="id"/>, or null. O(1), allocation-free.</summary>
    public Layer? Find(Guid id) => _byId.GetValueOrDefault(id);

    /// <summary>True when a layer with <paramref name="id"/> exists. O(1), allocation-free.</summary>
    public bool Contains(Guid id) => _byId.ContainsKey(id);

    /// <summary>Appends a layer.</summary>
    public void Add(Layer layer) => Insert(_items.Count, layer);

    /// <summary>Appends several layers, raising a single change.</summary>
    public void AddRange(IEnumerable<Layer> layers)
    {
        ArgumentNullException.ThrowIfNull(layers);
        Layer? only = null;
        int added = 0;
        foreach (var layer in layers)
        {
            ArgumentNullException.ThrowIfNull(layer, nameof(layers));
            _items.Add(layer);
            Attached(layer);
            only = layer;
            added++;
        }
        if (added > 0) Raise(added == 1 ? only : null);
    }

    /// <summary>Inserts a layer at <paramref name="index"/> (0 makes it the default layer).</summary>
    public void Insert(int index, Layer layer)
    {
        ArgumentNullException.ThrowIfNull(layer);
        _items.Insert(index, layer);
        Attached(layer);
        Raise(layer);
    }

    /// <summary>Removes a layer (by reference). Entities on it keep its id; treat unknown layer ids as visible and unlocked.</summary>
    public bool Remove(Layer layer)
    {
        if (layer is null) return false;
        int i = _items.IndexOf(layer);
        if (i < 0) return false;
        RemoveAt(i);
        return true;
    }

    /// <summary>Removes the layer at <paramref name="index"/>.</summary>
    public void RemoveAt(int index)
    {
        var layer = _items[index];
        _items.RemoveAt(index);
        Detached(layer);
        Raise(layer);
    }

    /// <summary>Moves a layer to another position (layer panel drag and drop).</summary>
    public void Move(int fromIndex, int toIndex)
    {
        if ((uint)fromIndex >= (uint)_items.Count) throw new ArgumentOutOfRangeException(nameof(fromIndex));
        if ((uint)toIndex >= (uint)_items.Count) throw new ArgumentOutOfRangeException(nameof(toIndex));
        if (fromIndex == toIndex) return;
        var layer = _items[fromIndex];
        _items.RemoveAt(fromIndex);
        _items.Insert(toIndex, layer);
        Reindex(layer.Id); // with a repeated id, the first layer in list order may have changed
        Raise(layer);
    }

    /// <summary>Removes every layer (raises one change). <see cref="Scene.DefaultLayer"/> recreates a default layer on demand.</summary>
    public void Clear()
    {
        if (_items.Count == 0) return;
        var removed = _items.ToArray();
        _items.Clear();
        _byId.Clear();
        foreach (var layer in removed)
            if (ReferenceEquals(layer.Owner, _scene)) layer.Owner = null;
        Raise(null);
    }

    /// <summary>True when this exact layer object is in the list.</summary>
    public bool Contains(Layer layer) => layer is not null && _items.Contains(layer);

    /// <summary>Position of this exact layer object, or -1.</summary>
    public int IndexOf(Layer layer) => layer is null ? -1 : _items.IndexOf(layer);

    /// <inheritdoc/>
    public void CopyTo(Layer[] array, int arrayIndex) => _items.CopyTo(array, arrayIndex);

    /// <summary>Allocation-free enumerator (the list must not change while enumerating).</summary>
    public List<Layer>.Enumerator GetEnumerator() => _items.GetEnumerator();

    IEnumerator<Layer> IEnumerable<Layer>.GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();

    /// <summary>Adds a layer without raising a change (scene construction).</summary>
    internal void AddSilently(Layer layer)
    {
        _items.Add(layer);
        Attached(layer);
    }

    private void Attached(Layer layer)
    {
        layer.Owner = _scene;
        if (!_byId.TryAdd(layer.Id, layer)) Reindex(layer.Id);
    }

    private void Detached(Layer layer)
    {
        if (_byId.TryGetValue(layer.Id, out var indexed) && ReferenceEquals(indexed, layer)) Reindex(layer.Id);
        if (ReferenceEquals(layer.Owner, _scene) && !_items.Contains(layer)) layer.Owner = null;
    }

    /// <summary>Points the index of <paramref name="id"/> at the first layer with that id in list order (rare: repeated ids).</summary>
    private void Reindex(Guid id)
    {
        foreach (var l in _items)
        {
            if (l.Id != id) continue;
            _byId[id] = l;
            return;
        }
        _byId.Remove(id);
    }

    private void Raise(Layer? layer) => _scene.Raise(new SceneChange(SceneChangeKind.LayersChanged, null) { Layer = layer });
}
