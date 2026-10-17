using Silk.NET.Vulkan;

namespace Candela.Rendering.Vulkan;

/// <summary>
/// A <c>VkBuffer</c> with its memory. Host-visible buffers are persistently mapped. <see cref="Dispose"/> destroys
/// immediately — use <see cref="VulkanContext.DeferDispose"/> for buffers that frames in flight may still read.
/// </summary>
public sealed unsafe class GpuBuffer : IDisposable
{
    private readonly VulkanContext _context;
    private MemoryAllocation _allocation;
    private Buffer _handle;

    private GpuBuffer(VulkanContext context, Buffer handle, ulong size, BufferUsageFlags usage, MemoryAllocation allocation, string name)
    {
        _context = context;
        _handle = handle;
        Size = size;
        Usage = usage;
        _allocation = allocation;
        Name = name;
        if ((usage & BufferUsageFlags.ShaderDeviceAddressBit) != 0)
        {
            var info = new BufferDeviceAddressInfo { SType = StructureType.BufferDeviceAddressInfo, Buffer = handle };
            DeviceAddress = context.Vk.GetBufferDeviceAddress(context.Device, &info);
        }
    }

    /// <summary>The buffer handle (default after disposal).</summary>
    public Buffer Handle => _handle;

    /// <summary>Size in bytes.</summary>
    public ulong Size { get; }

    /// <summary>Usage flags the buffer was created with.</summary>
    public BufferUsageFlags Usage { get; }

    /// <summary>Debug name.</summary>
    public string Name { get; }

    /// <summary>The memory allocation.</summary>
    public MemoryAllocation Allocation => _allocation;

    /// <summary>CPU pointer to the start of the buffer (null when not host-visible).</summary>
    public byte* MappedPointer => _allocation.MappedPointer;

    /// <summary>GPU address (0 unless created with <c>SHADER_DEVICE_ADDRESS</c>).</summary>
    public ulong DeviceAddress { get; }

    /// <summary>The mapped bytes (empty when not host-visible).</summary>
    public Span<byte> MappedSpan => _allocation.MappedPointer == null ? Span<byte>.Empty : new Span<byte>(_allocation.MappedPointer, checked((int)Size));

    /// <summary>Synchronisation state for the barrier tracker.</summary>
    internal ResourceState State;

    /// <summary>Creates a buffer and allocates/binds its memory.</summary>
    public static GpuBuffer Create(VulkanContext context, ulong size, BufferUsageFlags usage, MemoryUsage memoryUsage, string name)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (size == 0)
            throw new ArgumentOutOfRangeException(nameof(size), "Buffers must not be empty.");
        if (context.Capabilities.BufferDeviceAddress && (usage & (BufferUsageFlags.StorageBufferBit | BufferUsageFlags.VertexBufferBit |
                BufferUsageFlags.IndexBufferBit)) != 0 && memoryUsage == MemoryUsage.GpuOnly)
        {
            usage |= BufferUsageFlags.ShaderDeviceAddressBit; // stage 3 reads geometry and scene data by address
        }

        var info = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = size,
            Usage = usage,
            SharingMode = SharingMode.Exclusive,
        };
        Buffer buffer;
        VkUtil.Check(context.Vk.CreateBuffer(context.Device, &info, null, &buffer), "vkCreateBuffer");
        MemoryAllocation allocation;
        try
        {
            allocation = context.Allocator.AllocateAndBind(buffer, memoryUsage);
        }
        catch
        {
            context.Vk.DestroyBuffer(context.Device, buffer, null);
            throw;
        }

        context.SetDebugName(ObjectType.Buffer, buffer.Handle, name);
        return new GpuBuffer(context, buffer, size, usage, allocation, name);
    }

    /// <summary>Copies <paramref name="data"/> into the mapped memory at <paramref name="offset"/> (host-visible buffers only).</summary>
    public void Write<T>(ReadOnlySpan<T> data, ulong offset = 0)
        where T : unmanaged
    {
        if (_allocation.MappedPointer == null)
            throw new InvalidOperationException($"Buffer '{Name}' is not host-visible.");
        ulong bytes = (ulong)data.Length * (ulong)sizeof(T);
        if (offset + bytes > Size)
            throw new ArgumentOutOfRangeException(nameof(offset), "Write past the end of the buffer.");
        fixed (T* src = data)
            System.Buffer.MemoryCopy(src, _allocation.MappedPointer + offset, (long)(Size - offset), (long)bytes);
        _context.Allocator.Flush(_allocation, offset, bytes);
    }

    /// <summary>Destroys the buffer and frees its memory now.</summary>
    public void Dispose()
    {
        if (_handle.Handle == 0)
            return;
        _context.Vk.DestroyBuffer(_context.Device, _handle, null);
        _context.Allocator.Free(_allocation);
        _handle = default;
        _allocation = default;
    }
}
