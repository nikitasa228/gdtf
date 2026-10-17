namespace Candela.Rendering.Vulkan;

/// <summary>
/// First-fit allocator of index ranges in a growable array (material slots of instances). Freed ranges are kept in
/// a sorted list and coalesced with their neighbours; allocations that fit nowhere extend the end.
/// </summary>
internal sealed class RangeAllocator
{
    private readonly List<(int Offset, int Count)> _free = new();

    /// <summary>One past the highest index ever allocated.</summary>
    public int End { get; private set; }

    /// <summary>Allocates <paramref name="count"/> consecutive indices and returns the first.</summary>
    public int Allocate(int count)
    {
        if (count <= 0)
            return 0;
        for (int i = 0; i < _free.Count; i++)
        {
            var (offset, size) = _free[i];
            if (size < count)
                continue;
            if (size == count)
                _free.RemoveAt(i);
            else
                _free[i] = (offset + count, size - count);
            return offset;
        }

        int start = End;
        End += count;
        return start;
    }

    /// <summary>Returns a range to the free list.</summary>
    public void Free(int offset, int count)
    {
        if (count <= 0)
            return;
        int i = 0;
        while (i < _free.Count && _free[i].Offset < offset)
            i++;
        bool mergePrev = i > 0 && _free[i - 1].Offset + _free[i - 1].Count == offset;
        bool mergeNext = i < _free.Count && offset + count == _free[i].Offset;
        if (mergePrev && mergeNext)
        {
            _free[i - 1] = (_free[i - 1].Offset, _free[i - 1].Count + count + _free[i].Count);
            _free.RemoveAt(i);
        }
        else if (mergePrev)
        {
            _free[i - 1] = (_free[i - 1].Offset, _free[i - 1].Count + count);
        }
        else if (mergeNext)
        {
            _free[i] = (offset, count + _free[i].Count);
        }
        else
        {
            _free.Insert(i, (offset, count));
        }
    }
}
