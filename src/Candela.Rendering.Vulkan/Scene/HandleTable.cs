namespace Candela.Rendering.Vulkan;

/// <summary>
/// Dense slot storage behind the opaque <c>*Handle</c> values of <see cref="IRenderWorld"/>. A handle value packs the
/// slot index + 1 (low 24 bits) and a 7-bit generation, so a stale handle (destroyed object, slot reused) is rejected
/// instead of silently addressing another object. Slot indices are stable for the lifetime of the object and double
/// as GPU array indices. Main thread only.
/// </summary>
/// <typeparam name="T">Record type stored per slot.</typeparam>
internal sealed class HandleTable<T>
    where T : class
{
    private const int IndexBits = 24;
    private const int IndexMask = (1 << IndexBits) - 1;
    private const int GenerationMask = 0x7F;
    private readonly int _firstIndex;
    private readonly Stack<int> _free = new();
    private T?[] _items;
    private byte[] _generations;
    private int _highWater;

    /// <param name="firstIndex">Slots below this index are reserved (e.g. slot 0 = default material).</param>
    /// <param name="initialCapacity">Initial number of slots.</param>
    public HandleTable(int firstIndex = 0, int initialCapacity = 64)
    {
        _firstIndex = firstIndex;
        _items = new T?[Math.Max(initialCapacity, firstIndex + 1)];
        _generations = new byte[_items.Length];
        _highWater = firstIndex;
    }

    /// <summary>One past the highest slot index ever used (size GPU arrays by this).</summary>
    public int HighWater => _highWater;

    /// <summary>Number of live objects.</summary>
    public int Count { get; private set; }

    /// <summary>Stores <paramref name="item"/> in a free slot and returns its handle value.</summary>
    public int Add(T item, out int index)
    {
        if (_free.Count > 0)
        {
            index = _free.Pop();
        }
        else
        {
            index = _highWater++;
            if (index > IndexMask - 1)
                throw new InvalidOperationException("Too many render objects.");
            if (index >= _items.Length)
            {
                int size = Math.Max(_items.Length * 2, index + 1);
                Array.Resize(ref _items, size);
                Array.Resize(ref _generations, size);
            }
        }

        _items[index] = item;
        Count++;
        return ((_generations[index] & GenerationMask) << IndexBits) | (index + 1);
    }

    /// <summary>The record of a live handle, or null when the handle is invalid or stale.</summary>
    public T? TryGet(int handle, out int index)
    {
        index = (handle & IndexMask) - 1;
        if (handle <= 0 || index < _firstIndex || index >= _highWater)
            return null;
        var item = _items[index];
        if (item is null || ((handle >> IndexBits) & GenerationMask) != (_generations[index] & GenerationMask))
            return null;
        return item;
    }

    /// <summary>The record of a live handle; throws <see cref="ArgumentException"/> for invalid or stale handles.</summary>
    public T Get(int handle, out int index, string kind) =>
        TryGet(handle, out index) ?? throw new ArgumentException($"Invalid or destroyed {kind} handle ({handle}).");

    /// <summary>The record in a slot (null when free).</summary>
    public T? AtIndex(int index) => (uint)index < (uint)_highWater ? _items[index] : null;

    /// <summary>
    /// Invalidates a live handle and returns its record (null when the handle was invalid/stale). With
    /// <paramref name="freeSlot"/> false the slot index stays reserved until <see cref="FreeSlot"/> (GPU data at that
    /// index is still referenced, e.g. a destroyed material still used by instances).
    /// </summary>
    public T? Remove(int handle, out int index, bool freeSlot = true)
    {
        var item = TryGet(handle, out index);
        if (item is null)
            return null;
        _items[index] = null;
        _generations[index]++;
        Count--;
        if (freeSlot)
            _free.Push(index);
        return item;
    }

    /// <summary>Returns a slot reserved by <see cref="Remove"/> with <c>freeSlot: false</c> to the free list.</summary>
    public void FreeSlot(int index) => _free.Push(index);
}
