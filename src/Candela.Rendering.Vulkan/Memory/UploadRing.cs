using Silk.NET.Vulkan;

namespace Candela.Rendering.Vulkan;

/// <summary>A piece of staging memory: write through <see cref="Pointer"/>, then copy from <see cref="Buffer"/> at <see cref="Offset"/>.</summary>
internal readonly unsafe struct StagingRegion
{
    public StagingRegion(Buffer buffer, ulong offset, ulong size, byte* pointer)
    {
        Buffer = buffer;
        Offset = offset;
        Size = size;
        Pointer = pointer;
    }

    public Buffer Buffer { get; }
    public ulong Offset { get; }
    public ulong Size { get; }
    public byte* Pointer { get; }

    public Span<byte> Span => new(Pointer, checked((int)Size));
}

/// <summary>
/// Staging ring for CPU → GPU uploads. One persistently mapped, host-coherent buffer is used as a ring of virtual
/// offsets: <see cref="Allocate"/> advances the head; when a frame is submitted the head is recorded and when that
/// frame's fence has been waited for, the tail advances to it. Requests larger than half the ring, or that do not
/// fit while earlier frames are in flight, get a temporary buffer that is destroyed after the frames that use it;
/// the ring then doubles for the next frames. No device-wide waits. Main thread only.
/// </summary>
internal sealed unsafe class UploadRing : IDisposable
{
    private const ulong MaxCapacity = 1ul << 30;
    private readonly VulkanContext _context;
    private GpuBuffer _buffer;
    private ulong _capacity;
    private ulong _head;
    private ulong _tail;
    private uint _generation;
    private ulong _wantedCapacity;

    public UploadRing(VulkanContext context, ulong capacity)
    {
        _context = context;
        _capacity = capacity;
        _wantedCapacity = capacity;
        _buffer = CreateBuffer(capacity);
    }

    public ulong Capacity => _capacity;

    /// <summary>Bytes currently reserved by frames in flight or pending uploads.</summary>
    public ulong BytesInUse => _head - _tail;

    /// <summary>Position recorded when a frame is submitted; passed back to <see cref="Release"/> when it completes.</summary>
    public (uint Generation, ulong Head) Mark => (_generation, _head);

    /// <summary>Reserves <paramref name="size"/> bytes aligned to <paramref name="alignment"/> (a power of two ≤ 256 or a multiple of 4).</summary>
    public StagingRegion Allocate(ulong size, ulong alignment = 16)
    {
        if (size == 0)
            size = 4;
        alignment = Math.Max(alignment, 4);
        if (size <= _capacity / 2 && _capacity % alignment == 0)
        {
            ulong pos = VkUtil.AlignUp(_head, alignment);
            if (pos % _capacity + size > _capacity)
                pos = VkUtil.AlignUp(pos, _capacity); // do not straddle the end: wrap to the next lap
            if (pos + size - _tail <= _capacity)
            {
                _head = pos + size;
                ulong physical = pos % _capacity;
                return new StagingRegion(_buffer.Handle, physical, size, _buffer.MappedPointer + physical);
            }
        }

        // Too large or ring full: a temporary buffer for this request, and a larger ring next time.
        _wantedCapacity = Math.Min(Math.Max(_wantedCapacity * 2, VkUtil.AlignUp(size * 2, 1ul << 20)), MaxCapacity);
        var temp = GpuBuffer.Create(_context, size, BufferUsageFlags.TransferSrcBit, MemoryUsage.Upload, "staging (temporary)");
        _context.DeferDispose(temp);
        return new StagingRegion(temp.Handle, 0, size, temp.MappedPointer);
    }

    /// <summary>Frees the space used by a completed frame; replaces the ring by a larger one when it was too small.</summary>
    public void Release((uint Generation, ulong Head) mark)
    {
        if (mark.Generation == _generation && mark.Head > _tail)
            _tail = mark.Head;
        if (_wantedCapacity > _capacity)
            Grow();
    }

    private void Grow()
    {
        // Regions of the old ring that frames in flight or pending copies still read stay valid: the old buffer is
        // destroyed through the deletion queue, and marks of the old generation are ignored from now on.
        _context.DeferDispose(_buffer);
        _capacity = _wantedCapacity;
        _buffer = CreateBuffer(_capacity);
        _head = 0;
        _tail = 0;
        _generation++;
    }

    private GpuBuffer CreateBuffer(ulong capacity) =>
        GpuBuffer.Create(_context, capacity, BufferUsageFlags.TransferSrcBit, MemoryUsage.Upload, "staging ring");

    public void Dispose() => _buffer.Dispose();
}
