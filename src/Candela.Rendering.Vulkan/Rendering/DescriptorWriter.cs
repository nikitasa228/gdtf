using Silk.NET.Vulkan;

namespace Candela.Rendering.Vulkan;

/// <summary>
/// Batches <c>vkUpdateDescriptorSets</c> writes (images, buffers, acceleration structures) in preallocated arrays,
/// fixing up the info pointers at flush time. Main thread only.
/// </summary>
internal sealed unsafe class DescriptorWriter
{
    private const int Capacity = 96;
    private readonly WriteDescriptorSet[] _writes = new WriteDescriptorSet[Capacity];
    private readonly DescriptorImageInfo[] _images = new DescriptorImageInfo[Capacity];
    private readonly DescriptorBufferInfo[] _buffers = new DescriptorBufferInfo[Capacity];
    private readonly AccelerationStructureKHR[] _structures = new AccelerationStructureKHR[4];
    private readonly WriteDescriptorSetAccelerationStructureKHR[] _structureWrites = new WriteDescriptorSetAccelerationStructureKHR[4];
    private readonly int[] _kind = new int[Capacity];
    private readonly int[] _infoIndex = new int[Capacity];
    private int _count;
    private int _imageCount;
    private int _bufferCount;
    private int _structureCount;

    public void Image(DescriptorSet set, uint binding, DescriptorType type, Sampler sampler, ImageView view, ImageLayout layout)
    {
        EnsureRoom();
        _images[_imageCount] = new DescriptorImageInfo(sampler, view, layout);
        Add(set, binding, type, 0, _imageCount++);
    }

    public void Buffer(DescriptorSet set, uint binding, DescriptorType type, Buffer buffer, ulong offset = 0, ulong range = Vk.WholeSize)
    {
        EnsureRoom();
        _buffers[_bufferCount] = new DescriptorBufferInfo(buffer, offset, range);
        Add(set, binding, type, 1, _bufferCount++);
    }

    public void AccelerationStructure(DescriptorSet set, uint binding, AccelerationStructureKHR structure)
    {
        if (_structureCount == _structures.Length)
            throw new InvalidOperationException("Too many acceleration structure writes in one batch.");
        EnsureRoom();
        _structures[_structureCount] = structure;
        Add(set, binding, DescriptorType.AccelerationStructureKhr, 2, _structureCount++);
    }

    /// <summary>Records every pending write.</summary>
    public void Flush(VulkanContext context)
    {
        if (_count == 0)
            return;
        fixed (WriteDescriptorSet* writes = _writes)
        fixed (DescriptorImageInfo* images = _images)
        fixed (DescriptorBufferInfo* buffers = _buffers)
        fixed (AccelerationStructureKHR* structures = _structures)
        fixed (WriteDescriptorSetAccelerationStructureKHR* structureWrites = _structureWrites)
        {
            for (int i = 0; i < _count; i++)
            {
                switch (_kind[i])
                {
                    case 0:
                        writes[i].PImageInfo = &images[_infoIndex[i]];
                        break;
                    case 1:
                        writes[i].PBufferInfo = &buffers[_infoIndex[i]];
                        break;
                    default:
                        int s = _infoIndex[i];
                        structureWrites[s] = new WriteDescriptorSetAccelerationStructureKHR
                        {
                            SType = StructureType.WriteDescriptorSetAccelerationStructureKhr,
                            AccelerationStructureCount = 1,
                            PAccelerationStructures = &structures[s],
                        };
                        writes[i].PNext = &structureWrites[s];
                        break;
                }
            }

            context.Vk.UpdateDescriptorSets(context.Device, (uint)_count, writes, 0, null);
        }

        _count = _imageCount = _bufferCount = _structureCount = 0;
    }

    private void EnsureRoom()
    {
        if (_count == Capacity)
            throw new InvalidOperationException("Descriptor write batch is full; flush it first.");
    }

    private void Add(DescriptorSet set, uint binding, DescriptorType type, int kind, int infoIndex)
    {
        _writes[_count] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = set,
            DstBinding = binding,
            DescriptorCount = 1,
            DescriptorType = type,
        };
        _kind[_count] = kind;
        _infoIndex[_count] = infoIndex;
        _count++;
    }
}

/// <summary>
/// The handles a set of descriptor sets was written with, so a per-frame check can tell (without allocating) whether
/// any referenced resource was replaced since.
/// </summary>
internal sealed class DescriptorSignature
{
    private ulong[] _current = new ulong[96];
    private ulong[] _written = new ulong[96];
    private int _currentCount;
    private int _writtenCount = -1;

    public void Begin() => _currentCount = 0;

    public void Add(ulong handle)
    {
        if (_currentCount == _current.Length)
            Array.Resize(ref _current, _current.Length * 2);
        _current[_currentCount++] = handle;
    }

    /// <summary>True when the handles added since <see cref="Begin"/> differ from those last committed.</summary>
    public bool Changed()
    {
        if (_currentCount != _writtenCount)
            return true;
        for (int i = 0; i < _currentCount; i++)
        {
            if (_current[i] != _written[i])
                return true;
        }

        return false;
    }

    /// <summary>Remembers the current handles as written.</summary>
    public void Commit()
    {
        if (_written.Length < _current.Length)
            Array.Resize(ref _written, _current.Length);
        Array.Copy(_current, _written, _currentCount);
        _writtenCount = _currentCount;
    }

    /// <summary>Forces the next <see cref="Changed"/> to return true.</summary>
    public void Invalidate() => _writtenCount = -1;
}
