using Silk.NET.Vulkan;

namespace Candela.Rendering.Vulkan;

/// <summary>How a resource's memory is accessed; picks the memory type.</summary>
public enum MemoryUsage
{
    /// <summary>Device-local, never mapped (meshes, textures, render targets).</summary>
    GpuOnly,

    /// <summary>Host-visible + coherent, written sequentially by the CPU, read once by the GPU (staging).</summary>
    Upload,

    /// <summary>Host-visible, cached when possible, written by the GPU and read by the CPU (readbacks).</summary>
    Readback,

    /// <summary>Host-visible + coherent, device-local when possible (small per-frame constants).</summary>
    Dynamic,
}

/// <summary>Whether a resource is linear (buffers, linear images) or optimally tiled (images) — see bufferImageGranularity.</summary>
internal enum ResourceKind
{
    Linear,
    Optimal,
}

/// <summary>
/// A range of device memory owned by a <see cref="DeviceMemoryAllocator"/>: either a sub-allocation of a block or a
/// dedicated <c>VkDeviceMemory</c>. Host-visible memory is persistently mapped (<see cref="MappedPointer"/>).
/// </summary>
public readonly unsafe struct MemoryAllocation
{
    internal MemoryAllocation(DeviceMemory memory, ulong offset, ulong size, byte* mapped, uint memoryTypeIndex,
        MemoryBlock? block, bool coherent)
    {
        Memory = memory;
        Offset = offset;
        Size = size;
        MappedPointer = mapped;
        MemoryTypeIndex = memoryTypeIndex;
        Block = block;
        IsCoherent = coherent;
    }

    /// <summary>The device memory object.</summary>
    public DeviceMemory Memory { get; }

    /// <summary>Offset of the allocation inside <see cref="Memory"/>, bytes.</summary>
    public ulong Offset { get; }

    /// <summary>Size of the allocation, bytes.</summary>
    public ulong Size { get; }

    /// <summary>CPU pointer to the first byte of the allocation (null when not host-visible).</summary>
    public byte* MappedPointer { get; }

    /// <summary>Memory type index.</summary>
    public uint MemoryTypeIndex { get; }

    /// <summary>True for a dedicated <c>VkDeviceMemory</c>.</summary>
    public bool IsDedicated => Block is null && Memory.Handle != 0;

    /// <summary>True when the memory is host-coherent (no flush/invalidate needed).</summary>
    public bool IsCoherent { get; }

    /// <summary>True when the allocation refers to memory.</summary>
    public bool IsValid => Memory.Handle != 0;

    internal MemoryBlock? Block { get; }
}

/// <summary>Allocator statistics (for diagnostics and leak tests).</summary>
/// <param name="BlockCount">Number of shared blocks.</param>
/// <param name="DedicatedCount">Number of dedicated allocations.</param>
/// <param name="AllocationCount">Live sub-allocations in blocks.</param>
/// <param name="ReservedBytes">Bytes of device memory held (blocks + dedicated).</param>
/// <param name="UsedBytes">Bytes handed out to resources.</param>
public readonly record struct MemoryStatistics(int BlockCount, int DedicatedCount, int AllocationCount, ulong ReservedBytes, ulong UsedBytes);

/// <summary>A device-memory block carved into sub-allocations with a sorted, coalescing free list.</summary>
internal sealed unsafe class MemoryBlock
{
    private readonly List<(ulong Offset, ulong Size)> _free = new();

    public MemoryBlock(DeviceMemory memory, ulong size, uint typeIndex, ResourceKind kind, byte* mapped)
    {
        Memory = memory;
        Size = size;
        TypeIndex = typeIndex;
        Kind = kind;
        Mapped = mapped;
        _free.Add((0, size));
    }

    public DeviceMemory Memory { get; }
    public ulong Size { get; }
    public uint TypeIndex { get; }
    public ResourceKind Kind { get; }
    public byte* Mapped { get; }
    public int AllocationCount { get; private set; }
    public ulong UsedBytes { get; private set; }

    /// <summary>Free ranges (sorted by offset, never adjacent) — exposed for tests.</summary>
    public IReadOnlyList<(ulong Offset, ulong Size)> FreeRanges => _free;

    /// <summary>First fit: the first free range that holds <paramref name="size"/> bytes at <paramref name="alignment"/>.</summary>
    public bool TryAllocate(ulong size, ulong alignment, out ulong offset)
    {
        for (int i = 0; i < _free.Count; i++)
        {
            var (start, length) = _free[i];
            ulong aligned = VkUtil.AlignUp(start, alignment);
            ulong end = start + length;
            if (aligned + size > end)
                continue;

            // Split: [start, aligned) stays free as padding, [aligned + size, end) stays free as the tail.
            _free.RemoveAt(i);
            int insertAt = i;
            if (aligned > start)
                _free.Insert(insertAt++, (start, aligned - start));
            if (aligned + size < end)
                _free.Insert(insertAt, (aligned + size, end - aligned - size));
            offset = aligned;
            AllocationCount++;
            UsedBytes += size;
            return true;
        }

        offset = 0;
        return false;
    }

    public void Free(ulong offset, ulong size)
    {
        int i = 0;
        while (i < _free.Count && _free[i].Offset < offset)
            i++;
        if (i > 0 && _free[i - 1].Offset + _free[i - 1].Size > offset)
            throw new InvalidOperationException("Double free or corrupt allocation in a memory block.");
        if (i < _free.Count && offset + size > _free[i].Offset)
            throw new InvalidOperationException("Double free or corrupt allocation in a memory block.");

        ulong newOffset = offset, newSize = size;
        bool mergePrev = i > 0 && _free[i - 1].Offset + _free[i - 1].Size == offset;
        bool mergeNext = i < _free.Count && offset + size == _free[i].Offset;
        if (mergeNext)
        {
            newSize += _free[i].Size;
            _free.RemoveAt(i);
        }

        if (mergePrev)
        {
            newOffset = _free[i - 1].Offset;
            newSize += _free[i - 1].Size;
            _free[i - 1] = (newOffset, newSize);
        }
        else
        {
            _free.Insert(i, (newOffset, newSize));
        }

        AllocationCount--;
        UsedBytes -= size;
    }

    public bool IsEmpty => AllocationCount == 0;
}

/// <summary>
/// Device-memory sub-allocator. Memory is taken from the driver in large blocks per memory type (64 MiB device-local,
/// 16 MiB host-visible, smaller on small heaps) and handed out with a first-fit free list that coalesces on free;
/// alignment is honoured per request and <c>bufferImageGranularity</c> by keeping linear resources (buffers) and
/// optimal-tiled images in separate blocks whenever the granularity is larger than 1. Large requests, images the
/// driver prefers dedicated (<c>VkMemoryDedicatedRequirements</c>) and images of 16 MiB or more get a dedicated
/// allocation. Host-visible memory is mapped once for its whole lifetime. Main thread only.
/// </summary>
public sealed unsafe class DeviceMemoryAllocator : IDisposable
{
    /// <summary>Images at least this large get their own allocation.</summary>
    public const ulong LargeImageThreshold = 16ul << 20;

    private readonly Vk _vk;
    private readonly Device _device;
    private readonly PhysicalDeviceMemoryProperties _properties;
    private readonly ulong _granularity;
    private readonly ulong _nonCoherentAtom;
    private readonly uint _maxAllocations;
    private readonly bool _deviceAddress;
    private readonly List<MemoryBlock> _blocks = new();
    private readonly Dictionary<ulong, (ulong Size, uint Type)> _dedicated = new();
    private int _driverAllocationCount;
    private bool _disposed;

    internal DeviceMemoryAllocator(Vk vk, PhysicalDevice physicalDevice, Device device, in PhysicalDeviceLimits limits, bool deviceAddress)
    {
        _vk = vk;
        _device = device;
        _properties = vk.GetPhysicalDeviceMemoryProperties(physicalDevice);
        _granularity = Math.Max(limits.BufferImageGranularity, 1);
        _nonCoherentAtom = Math.Max(limits.NonCoherentAtomSize, 1);
        _maxAllocations = limits.MaxMemoryAllocationCount;
        _deviceAddress = deviceAddress;
    }

    /// <summary>Default block size for device-local memory.</summary>
    public ulong DeviceLocalBlockSize { get; init; } = 64ul << 20;

    /// <summary>Default block size for host-visible memory.</summary>
    public ulong HostVisibleBlockSize { get; init; } = 16ul << 20;

    /// <summary>Current statistics.</summary>
    public MemoryStatistics Statistics
    {
        get
        {
            ulong reserved = 0, used = 0;
            int allocations = 0;
            foreach (var b in _blocks)
            {
                reserved += b.Size;
                used += b.UsedBytes;
                allocations += b.AllocationCount;
            }

            foreach (var d in _dedicated.Values)
            {
                reserved += d.Size;
                used += d.Size;
            }

            return new MemoryStatistics(_blocks.Count, _dedicated.Count, allocations, reserved, used);
        }
    }

    internal IReadOnlyList<MemoryBlock> Blocks => _blocks;

    /// <summary>Allocates memory for <paramref name="buffer"/> and binds it.</summary>
    public MemoryAllocation AllocateAndBind(Buffer buffer, MemoryUsage usage)
    {
        var dedicatedReq = new MemoryDedicatedRequirements { SType = StructureType.MemoryDedicatedRequirements };
        var req2 = new MemoryRequirements2 { SType = StructureType.MemoryRequirements2, PNext = &dedicatedReq };
        var info = new BufferMemoryRequirementsInfo2 { SType = StructureType.BufferMemoryRequirementsInfo2, Buffer = buffer };
        _vk.GetBufferMemoryRequirements2(_device, &info, &req2);
        bool dedicated = dedicatedReq.RequiresDedicatedAllocation || dedicatedReq.PrefersDedicatedAllocation;
        var allocation = Allocate(req2.MemoryRequirements, usage, ResourceKind.Linear, dedicated, buffer, default);
        VkUtil.Check(_vk.BindBufferMemory(_device, buffer, allocation.Memory, allocation.Offset), "vkBindBufferMemory");
        return allocation;
    }

    /// <summary>Allocates memory for <paramref name="image"/> (optimal tiling) and binds it.</summary>
    /// <param name="image">The image.</param>
    /// <param name="usage">Memory usage (normally <see cref="MemoryUsage.GpuOnly"/>).</param>
    /// <param name="preferDedicated">Force a dedicated allocation (e.g. render targets that are resized often).</param>
    public MemoryAllocation AllocateAndBind(Image image, MemoryUsage usage, bool preferDedicated = false)
    {
        var dedicatedReq = new MemoryDedicatedRequirements { SType = StructureType.MemoryDedicatedRequirements };
        var req2 = new MemoryRequirements2 { SType = StructureType.MemoryRequirements2, PNext = &dedicatedReq };
        var info = new ImageMemoryRequirementsInfo2 { SType = StructureType.ImageMemoryRequirementsInfo2, Image = image };
        _vk.GetImageMemoryRequirements2(_device, &info, &req2);
        bool dedicated = preferDedicated || dedicatedReq.RequiresDedicatedAllocation || dedicatedReq.PrefersDedicatedAllocation ||
                         req2.MemoryRequirements.Size >= LargeImageThreshold;
        var allocation = Allocate(req2.MemoryRequirements, usage, ResourceKind.Optimal, dedicated, default, image);
        VkUtil.Check(_vk.BindImageMemory(_device, image, allocation.Memory, allocation.Offset), "vkBindImageMemory");
        return allocation;
    }

    /// <summary>Allocates memory for the given requirements without binding (sub-allocated unless large).</summary>
    internal MemoryAllocation Allocate(in MemoryRequirements requirements, MemoryUsage usage, ResourceKind kind,
        bool dedicated = false, Buffer dedicatedBuffer = default, Image dedicatedImage = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_granularity <= 1)
            kind = ResourceKind.Linear; // no granularity constraint: linear and optimal resources may share blocks

        uint excluded = 0;
        while (true)
        {
            int typeIndex = FindMemoryType(requirements.MemoryTypeBits & ~excluded, usage);
            if (typeIndex < 0)
                throw new VulkanException(Result.ErrorOutOfDeviceMemory, $"No memory type for {usage} (type bits 0x{requirements.MemoryTypeBits:X})");
            var flags = _properties.MemoryTypes[typeIndex].PropertyFlags;
            bool hostVisible = (flags & MemoryPropertyFlags.HostVisibleBit) != 0;
            bool coherent = (flags & MemoryPropertyFlags.HostCoherentBit) != 0;
            ulong blockSize = BlockSizeFor((uint)typeIndex, hostVisible);
            ulong alignment = Math.Max(requirements.Alignment, 1);
            if (hostVisible && !coherent)
                alignment = Math.Max(alignment, _nonCoherentAtom);

            var result = dedicated || requirements.Size > blockSize / 2
                ? TryAllocateDedicated(requirements.Size, (uint)typeIndex, hostVisible, coherent, dedicatedBuffer, dedicatedImage, out var allocation)
                : TryAllocateInBlock(requirements.Size, alignment, (uint)typeIndex, kind, blockSize, hostVisible, coherent, out allocation);
            if (result == Result.Success)
                return allocation;
            if (result is Result.ErrorOutOfDeviceMemory or Result.ErrorOutOfHostMemory)
            {
                excluded |= 1u << typeIndex; // try the next suitable memory type
                continue;
            }

            VkUtil.Check(result, "vkAllocateMemory");
        }
    }

    /// <summary>Returns <paramref name="allocation"/> to its block (or frees the dedicated memory).</summary>
    public void Free(in MemoryAllocation allocation)
    {
        if (!allocation.IsValid || _disposed)
            return;
        if (allocation.Block is { } block)
        {
            block.Free(allocation.Offset, allocation.Size);
            if (block.IsEmpty && CountEmptyBlocks(block.TypeIndex, block.Kind) > 1)
            {
                _blocks.Remove(block);
                FreeMemory(block.Memory, block.Mapped != null);
            }

            return;
        }

        if (_dedicated.Remove(allocation.Memory.Handle))
            FreeMemory(allocation.Memory, allocation.MappedPointer != null);
    }

    /// <summary>Makes CPU writes visible to the device for non-coherent memory (no-op when coherent).</summary>
    public void Flush(in MemoryAllocation allocation, ulong offset, ulong size)
    {
        if (allocation.IsCoherent || allocation.MappedPointer == null)
            return;
        var range = AtomRange(allocation, offset, size);
        VkUtil.Check(_vk.FlushMappedMemoryRanges(_device, 1, &range), "vkFlushMappedMemoryRanges");
    }

    /// <summary>Makes device writes visible to the CPU for non-coherent memory (no-op when coherent).</summary>
    public void Invalidate(in MemoryAllocation allocation, ulong offset, ulong size)
    {
        if (allocation.IsCoherent || allocation.MappedPointer == null)
            return;
        var range = AtomRange(allocation, offset, size);
        VkUtil.Check(_vk.InvalidateMappedMemoryRanges(_device, 1, &range), "vkInvalidateMappedMemoryRanges");
    }

    /// <summary>Frees every block (all resources must have been destroyed).</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        foreach (var b in _blocks)
            FreeMemory(b.Memory, b.Mapped != null);
        _blocks.Clear();
        foreach (var handle in _dedicated.Keys)
            FreeMemory(new DeviceMemory(handle), false);
        _dedicated.Clear();
        _disposed = true;
    }

    /// <summary>
    /// Chooses the memory type for a usage: required flags must be present; preferred flags win; flags that are
    /// better avoided (e.g. host-visible for GPU-only data) lose. Ties go to the lower index (driver order).
    /// </summary>
    internal int FindMemoryType(uint typeBits, MemoryUsage usage)
    {
        var (required, preferred, avoided) = usage switch
        {
            MemoryUsage.GpuOnly => (MemoryPropertyFlags.DeviceLocalBit, MemoryPropertyFlags.None, MemoryPropertyFlags.HostVisibleBit),
            MemoryUsage.Upload => (MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit, MemoryPropertyFlags.None,
                MemoryPropertyFlags.HostCachedBit),
            MemoryUsage.Readback => (MemoryPropertyFlags.HostVisibleBit, MemoryPropertyFlags.HostCachedBit | MemoryPropertyFlags.HostCoherentBit,
                MemoryPropertyFlags.None),
            _ => (MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit, MemoryPropertyFlags.DeviceLocalBit,
                MemoryPropertyFlags.None),
        };

        int best = -1, bestScore = int.MinValue;
        for (int pass = 0; pass < 2 && best < 0; pass++)
        {
            // Second pass (GPU-only data on a device without matching device-local types): drop the requirement.
            var req = pass == 0 ? required : required & ~MemoryPropertyFlags.DeviceLocalBit;
            for (int i = 0; i < _properties.MemoryTypeCount; i++)
            {
                if ((typeBits & (1u << i)) == 0)
                    continue;
                var flags = _properties.MemoryTypes[i].PropertyFlags;
                if ((flags & req) != req)
                    continue;
                if ((flags & (MemoryPropertyFlags.ProtectedBit | MemoryPropertyFlags.LazilyAllocatedBit)) != 0)
                    continue;
                int score = 2 * BitCount((uint)(flags & preferred)) - BitCount((uint)(flags & avoided));
                if (score > bestScore)
                {
                    best = i;
                    bestScore = score;
                }
            }
        }

        return best;
    }

    private static int BitCount(uint v) => System.Numerics.BitOperations.PopCount(v);

    private ulong BlockSizeFor(uint typeIndex, bool hostVisible)
    {
        ulong heapSize = _properties.MemoryHeaps[(int)_properties.MemoryTypes[(int)typeIndex].HeapIndex].Size;
        ulong preferred = hostVisible ? HostVisibleBlockSize : DeviceLocalBlockSize;
        // Small heaps (e.g. a 256 MiB BAR window) get smaller blocks: at most 1/8 of the heap.
        return Math.Max(Math.Min(preferred, heapSize / 8), 1ul << 20);
    }

    private Result TryAllocateInBlock(ulong size, ulong alignment, uint typeIndex, ResourceKind kind, ulong blockSize,
        bool hostVisible, bool coherent, out MemoryAllocation allocation)
    {
        foreach (var block in _blocks)
        {
            if (block.TypeIndex != typeIndex || block.Kind != kind)
                continue;
            if (block.TryAllocate(size, alignment, out ulong offset))
            {
                allocation = new MemoryAllocation(block.Memory, offset, size, block.Mapped == null ? null : block.Mapped + offset,
                    typeIndex, block, coherent);
                return Result.Success;
            }
        }

        var result = AllocateMemory(blockSize, typeIndex, default, default, out var memory);
        if (result != Result.Success)
        {
            allocation = default;
            return result;
        }

        byte* mapped = null;
        if (hostVisible)
            mapped = Map(memory);
        var newBlock = new MemoryBlock(memory, blockSize, typeIndex, kind, mapped);
        _blocks.Add(newBlock);
        if (!newBlock.TryAllocate(size, alignment, out ulong off))
            throw new InvalidOperationException("Fresh memory block cannot hold the request.");
        allocation = new MemoryAllocation(memory, off, size, mapped == null ? null : mapped + off, typeIndex, newBlock, coherent);
        return Result.Success;
    }

    private Result TryAllocateDedicated(ulong size, uint typeIndex, bool hostVisible, bool coherent, Buffer buffer, Image image,
        out MemoryAllocation allocation)
    {
        var result = AllocateMemory(size, typeIndex, buffer, image, out var memory);
        if (result != Result.Success)
        {
            allocation = default;
            return result;
        }

        byte* mapped = hostVisible ? Map(memory) : null;
        _dedicated.Add(memory.Handle, (size, typeIndex));
        allocation = new MemoryAllocation(memory, 0, size, mapped, typeIndex, null, coherent);
        return Result.Success;
    }

    private Result AllocateMemory(ulong size, uint typeIndex, Buffer dedicatedBuffer, Image dedicatedImage, out DeviceMemory memory)
    {
        memory = default;
        if (_maxAllocations != 0 && _driverAllocationCount >= _maxAllocations)
            return Result.ErrorTooManyObjects;

        var flagsInfo = new MemoryAllocateFlagsInfo { SType = StructureType.MemoryAllocateFlagsInfo, Flags = MemoryAllocateFlags.DeviceAddressBit };
        var dedicatedInfo = new MemoryDedicatedAllocateInfo
        {
            SType = StructureType.MemoryDedicatedAllocateInfo,
            Buffer = dedicatedBuffer,
            Image = dedicatedImage,
        };
        var info = new MemoryAllocateInfo { SType = StructureType.MemoryAllocateInfo, AllocationSize = size, MemoryTypeIndex = typeIndex };
        void* next = null;
        if (dedicatedBuffer.Handle != 0 || dedicatedImage.Handle != 0)
        {
            dedicatedInfo.PNext = next;
            next = &dedicatedInfo;
        }

        // Buffers may need a device address (ray tracing, stage 3); images never do.
        if (_deviceAddress && dedicatedImage.Handle == 0)
        {
            flagsInfo.PNext = next;
            next = &flagsInfo;
        }

        info.PNext = next;
        DeviceMemory m;
        var result = _vk.AllocateMemory(_device, &info, null, &m);
        if (result == Result.Success)
        {
            memory = m;
            _driverAllocationCount++;
        }

        return result;
    }

    private byte* Map(DeviceMemory memory)
    {
        void* p;
        VkUtil.Check(_vk.MapMemory(_device, memory, 0, Vk.WholeSize, 0, &p), "vkMapMemory");
        return (byte*)p;
    }

    private void FreeMemory(DeviceMemory memory, bool mapped)
    {
        if (mapped)
            _vk.UnmapMemory(_device, memory);
        _vk.FreeMemory(_device, memory, null);
        _driverAllocationCount--;
    }

    private int CountEmptyBlocks(uint typeIndex, ResourceKind kind)
    {
        int n = 0;
        foreach (var b in _blocks)
        {
            if (b.TypeIndex == typeIndex && b.Kind == kind && b.IsEmpty)
                n++;
        }

        return n;
    }

    private MappedMemoryRange AtomRange(in MemoryAllocation allocation, ulong offset, ulong size)
    {
        ulong memorySize = allocation.Block?.Size ?? allocation.Size;
        ulong start = (allocation.Offset + offset) / _nonCoherentAtom * _nonCoherentAtom;
        ulong end = Math.Min(VkUtil.AlignUp(allocation.Offset + offset + Math.Min(size, allocation.Size - offset), _nonCoherentAtom), memorySize);
        return new MappedMemoryRange
        {
            SType = StructureType.MappedMemoryRange,
            Memory = allocation.Memory,
            Offset = start,
            Size = end == memorySize ? Vk.WholeSize : end - start,
        };
    }
}
