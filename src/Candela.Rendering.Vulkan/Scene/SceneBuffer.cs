using System.Runtime.CompilerServices;
using Silk.NET.Vulkan;

namespace Candela.Rendering.Vulkan;

/// <summary>
/// A device-local GPU array with a CPU mirror (materials, instances, material slots, lights, line vertices). The CPU
/// side is edited in place and marked dirty; once per <see cref="IRenderer.Render"/> the dirty range is copied through
/// the staging ring with a write-after-read barrier against earlier frames — no device-wide waits. When the array
/// outgrows the GPU buffer a larger buffer replaces it (the old one is destroyed after the frames in flight) and
/// <see cref="Version"/> changes so descriptor sets are rewritten. Main thread only.
/// </summary>
/// <typeparam name="T">Unmanaged element type mirroring a GLSL struct.</typeparam>
internal sealed unsafe class SceneBuffer<T> : IDisposable
    where T : unmanaged
{
    private const int MinCapacity = 16;
    private readonly VulkanContext _context;
    private readonly BufferUsageFlags _usage;
    private readonly string _name;
    private T[] _data;
    private GpuBuffer _gpu;
    private int _count;
    private int _dirtyBegin = int.MaxValue;
    private int _dirtyEnd;
    private bool _uploadPrepared;

    public SceneBuffer(VulkanContext context, BufferUsageFlags usage, string name, int initialCapacity = MinCapacity)
    {
        _context = context;
        _usage = usage | BufferUsageFlags.TransferDstBit;
        _name = name;
        _data = new T[Math.Max(initialCapacity, MinCapacity)];
        _gpu = CreateGpu(_data.Length);
    }

    /// <summary>Number of elements in use (the uploaded range is [0, Count)).</summary>
    public int Count => _count;

    /// <summary>The GPU buffer (replaced when the array grows; see <see cref="Version"/>).</summary>
    public GpuBuffer Buffer => _gpu;

    /// <summary>Changes whenever <see cref="Buffer"/> is replaced.</summary>
    public uint Version { get; private set; }

    /// <summary>Bytes per element.</summary>
    public static int Stride => sizeof(T);

    /// <summary>Element <paramref name="index"/> for editing (call <see cref="MarkDirty"/> afterwards).</summary>
    public ref T this[int index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if ((uint)index >= (uint)_count)
                throw new ArgumentOutOfRangeException(nameof(index));
            return ref _data[index];
        }
    }

    /// <summary>The elements in use (read-only view for CPU-side queries).</summary>
    public ReadOnlySpan<T> Items => _data.AsSpan(0, _count);

    /// <summary>Sets the number of elements in use, growing the CPU array (new elements are zero).</summary>
    public void Resize(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count > _data.Length)
            Array.Resize(ref _data, Math.Max(count, _data.Length * 2));
        if (count < _count)
            Array.Clear(_data, count, _count - count);
        _count = count;
    }

    /// <summary>Replaces the whole content with <paramref name="values"/>.</summary>
    public void Set(ReadOnlySpan<T> values)
    {
        Resize(values.Length);
        values.CopyTo(_data);
        if (values.Length > 0)
            MarkDirty(0, values.Length);
    }

    /// <summary>Marks [first, first + count) for upload.</summary>
    public void MarkDirty(int first, int count = 1)
    {
        if (count <= 0)
            return;
        _dirtyBegin = Math.Min(_dirtyBegin, first);
        _dirtyEnd = Math.Max(_dirtyEnd, first + count);
    }

    /// <summary>
    /// First half of an upload: replaces the GPU buffer if it is too small and declares the transfer write (so all
    /// write-after-read barriers of a frame's uploads go into one batch). Returns true when there is data to copy.
    /// </summary>
    public bool PrepareUpload(BarrierBatch barriers)
    {
        if ((ulong)_count * (ulong)sizeof(T) > _gpu.Size)
        {
            _context.DeferDispose(_gpu);
            _gpu = CreateGpu(Math.Max(_data.Length, _count));
            Version++;
            if (_count > 0)
                MarkDirty(0, _count);
        }

        _dirtyEnd = Math.Min(_dirtyEnd, _count);
        if (_dirtyBegin >= _dirtyEnd)
        {
            _dirtyBegin = int.MaxValue;
            _dirtyEnd = 0;
            _uploadPrepared = false;
            return false;
        }

        barriers.Use(_gpu, BufferUse.TransferDestination);
        _uploadPrepared = true;
        return true;
    }

    /// <summary>Second half: copies the dirty range through the staging ring (after the barriers were flushed).</summary>
    public void RecordUpload(CommandBuffer cmd)
    {
        if (!_uploadPrepared)
            return;
        _uploadPrepared = false;
        int stride = sizeof(T);
        ulong offset = (ulong)_dirtyBegin * (ulong)stride;
        ulong size = (ulong)(_dirtyEnd - _dirtyBegin) * (ulong)stride;
        var region = _context.Upload.Allocate(size, 16);
        fixed (T* src = &_data[_dirtyBegin])
            System.Buffer.MemoryCopy(src, region.Pointer, (long)size, (long)size);
        var copy = new BufferCopy(region.Offset, offset, size);
        _context.Vk.CmdCopyBuffer(cmd, region.Buffer, _gpu.Handle, 1, &copy);
        _dirtyBegin = int.MaxValue;
        _dirtyEnd = 0;
    }

    public void Dispose() => _gpu.Dispose();

    private GpuBuffer CreateGpu(int capacity) =>
        GpuBuffer.Create(_context, (ulong)Math.Max(capacity, MinCapacity) * (ulong)sizeof(T), _usage, MemoryUsage.GpuOnly, _name);
}
