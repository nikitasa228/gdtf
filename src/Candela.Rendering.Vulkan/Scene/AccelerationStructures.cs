using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;

namespace Candela.Rendering.Vulkan;

/// <summary>Layout of <c>VkAccelerationStructureInstanceKHR</c> (64 bytes; the bit fields pack low bits first).</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct TlasInstance
{
    /// <summary>3×4 row-major object-to-world transform (column-vector convention).</summary>
    public fixed float Transform[12];

    /// <summary>Custom index (low 24 bits, <c>gl_InstanceCustomIndexEXT</c>) and visibility mask (high 8 bits).</summary>
    public uint CustomIndexAndMask;

    /// <summary>Shader binding table offset (low 24 bits) and <see cref="GeometryInstanceFlagsKHR"/> (high 8 bits).</summary>
    public uint SbtOffsetAndFlags;

    /// <summary>Device address of the bottom-level structure.</summary>
    public ulong AccelerationStructureReference;

    /// <summary>
    /// An instance of <paramref name="blasAddress"/> placed by the System.Numerics world matrix <paramref name="world"/>
    /// (row-vector convention: p' = p·W, so the instance transform is Wᵀ), visible to rays whose cull mask shares a bit
    /// with <paramref name="mask"/>. Triangles are traced from both sides.
    /// </summary>
    public static TlasInstance Create(in Matrix4x4 world, uint customIndex, byte mask, ulong blasAddress)
    {
        var i = new TlasInstance
        {
            CustomIndexAndMask = (customIndex & 0xFFFFFFu) | ((uint)mask << 24),
            SbtOffsetAndFlags = (uint)GeometryInstanceFlagsKHR.TriangleFacingCullDisableBitKhr << 24,
            AccelerationStructureReference = blasAddress,
        };
        i.Transform[0] = world.M11;
        i.Transform[1] = world.M21;
        i.Transform[2] = world.M31;
        i.Transform[3] = world.M41;
        i.Transform[4] = world.M12;
        i.Transform[5] = world.M22;
        i.Transform[6] = world.M32;
        i.Transform[7] = world.M42;
        i.Transform[8] = world.M13;
        i.Transform[9] = world.M23;
        i.Transform[10] = world.M33;
        i.Transform[11] = world.M43;
        return i;
    }
}

/// <summary>The acceleration-structure entry points of a device with their scratch alignment.</summary>
internal sealed unsafe class AccelerationStructureDevice : IDisposable
{
    public AccelerationStructureDevice(VulkanContext context)
    {
        Context = context;
        if (!context.Vk.TryGetDeviceExtension(context.Instance, context.Device, out KhrAccelerationStructure api))
            throw new VulkanException(Result.ErrorExtensionNotPresent, "VK_KHR_acceleration_structure functions");
        Api = api;
        var asProperties = new PhysicalDeviceAccelerationStructurePropertiesKHR { SType = StructureType.PhysicalDeviceAccelerationStructurePropertiesKhr };
        var properties = new PhysicalDeviceProperties2 { SType = StructureType.PhysicalDeviceProperties2, PNext = &asProperties };
        context.Vk.GetPhysicalDeviceProperties2(context.PhysicalDevice, &properties);
        ScratchAlignment = Math.Max(asProperties.MinAccelerationStructureScratchOffsetAlignment, 1u);
    }

    public VulkanContext Context { get; }

    public KhrAccelerationStructure Api { get; }

    /// <summary>Alignment of build scratch addresses.</summary>
    public ulong ScratchAlignment { get; }

    /// <summary>Creates an acceleration structure object over <paramref name="buffer"/>.</summary>
    public AccelerationStructureKHR Create(GpuBuffer buffer, AccelerationStructureTypeKHR type, ulong size)
    {
        var info = new AccelerationStructureCreateInfoKHR
        {
            SType = StructureType.AccelerationStructureCreateInfoKhr,
            Buffer = buffer.Handle,
            Offset = 0,
            Size = size,
            Type = type,
        };
        AccelerationStructureKHR structure;
        VkUtil.Check(Api.CreateAccelerationStructure(Context.Device, &info, null, &structure), "vkCreateAccelerationStructureKHR");
        return structure;
    }

    /// <summary>Device address of a structure (what instances reference).</summary>
    public ulong AddressOf(AccelerationStructureKHR structure)
    {
        var addressInfo = new AccelerationStructureDeviceAddressInfoKHR
        {
            SType = StructureType.AccelerationStructureDeviceAddressInfoKhr,
            AccelerationStructure = structure,
        };
        return Api.GetAccelerationStructureDeviceAddress(Context.Device, &addressInfo);
    }

    public void Dispose() => Api.Dispose();
}

/// <summary>
/// A bottom-level acceleration structure: triangles read in place from a buffer (meshes, light discs) or axis-aligned
/// boxes (procedural primitives such as the light volumes of the path tracer). Built once; destroyed with
/// <see cref="Dispose"/> (the caller defers it while frames may still trace it).
/// </summary>
internal sealed unsafe class BottomLevelStructure : IDisposable
{
    private readonly AccelerationStructureDevice _device;

    private BottomLevelStructure(AccelerationStructureDevice device, GpuBuffer storage, AccelerationStructureKHR structure)
    {
        _device = device;
        Storage = storage;
        Structure = structure;
        Address = device.AddressOf(structure);
    }

    public GpuBuffer Storage { get; }

    public AccelerationStructureKHR Structure { get; }

    /// <summary>Device address referenced by instances.</summary>
    public ulong Address { get; }

    /// <summary>One geometry of triangles: vertex positions (float3 at <c>Stride</c>) and 32-bit indices in one buffer.</summary>
    /// <param name="IndexOffset">Byte offset of the index range used by this geometry in the buffer.</param>
    /// <param name="IndexCount">Indices of the geometry (a multiple of 3).</param>
    /// <param name="Opaque">Hits are committed without shader involvement (otherwise they are ray-query candidates).</param>
    internal readonly record struct TriangleGeometry(ulong IndexOffset, int IndexCount, bool Opaque);

    /// <summary>
    /// Records the build of a triangle structure over <paramref name="buffer"/>: vertices start at its first byte with
    /// <paramref name="stride"/> bytes per vertex (position first), indices at the offsets of <paramref name="geometries"/>.
    /// </summary>
    public static BottomLevelStructure BuildTriangles(AccelerationStructureDevice device, CommandBuffer cmd, BarrierBatch barriers, GpuBuffer buffer,
        uint vertexCount, ulong stride, ReadOnlySpan<TriangleGeometry> geometries, string name)
    {
        int count = geometries.Length;
        var geometryInfos = new AccelerationStructureGeometryKHR[count];
        var ranges = new AccelerationStructureBuildRangeInfoKHR[count];
        var primitiveCounts = new uint[count];
        ulong vertexAddress = buffer.DeviceAddress;
        for (int i = 0; i < count; i++)
        {
            var g = geometries[i];
            geometryInfos[i] = new AccelerationStructureGeometryKHR
            {
                SType = StructureType.AccelerationStructureGeometryKhr,
                GeometryType = GeometryTypeKHR.TrianglesKhr,
                Flags = g.Opaque ? GeometryFlagsKHR.OpaqueBitKhr : GeometryFlagsKHR.None,
            };
            geometryInfos[i].Geometry.Triangles = new AccelerationStructureGeometryTrianglesDataKHR
            {
                SType = StructureType.AccelerationStructureGeometryTrianglesDataKhr,
                VertexFormat = Format.R32G32B32Sfloat,
                VertexData = new DeviceOrHostAddressConstKHR { DeviceAddress = vertexAddress },
                VertexStride = stride,
                MaxVertex = Math.Max(vertexCount, 1u) - 1,
                IndexType = IndexType.Uint32,
                IndexData = new DeviceOrHostAddressConstKHR { DeviceAddress = vertexAddress },
            };
            uint primitives = (uint)Math.Max(g.IndexCount, 0) / 3;
            primitiveCounts[i] = primitives;
            ranges[i] = new AccelerationStructureBuildRangeInfoKHR { PrimitiveCount = primitives, PrimitiveOffset = (uint)g.IndexOffset };
        }

        fixed (AccelerationStructureGeometryKHR* pGeometries = geometryInfos)
        fixed (AccelerationStructureBuildRangeInfoKHR* pRanges = ranges)
        fixed (uint* pCounts = primitiveCounts)
        {
            barriers.Use(buffer, BufferUse.AccelerationStructureBuildInput);
            return Build(device, cmd, barriers, pGeometries, (uint)count, pCounts, pRanges, name);
        }
    }

    /// <summary>
    /// Records the build of a structure of axis-aligned boxes (<c>VkAabbPositionsKHR</c>, 24 bytes each, starting at the
    /// first byte of <paramref name="boxes"/>); a box whose minimum x is NaN is inactive. Boxes are never opaque: ray
    /// queries report them as candidates (procedural primitives).
    /// </summary>
    public static BottomLevelStructure BuildAabbs(AccelerationStructureDevice device, CommandBuffer cmd, BarrierBatch barriers, GpuBuffer boxes,
        uint count, string name)
    {
        var geometry = new AccelerationStructureGeometryKHR
        {
            SType = StructureType.AccelerationStructureGeometryKhr,
            GeometryType = GeometryTypeKHR.AabbsKhr,
            Flags = GeometryFlagsKHR.None,
        };
        geometry.Geometry.Aabbs = new AccelerationStructureGeometryAabbsDataKHR
        {
            SType = StructureType.AccelerationStructureGeometryAabbsDataKhr,
            Data = new DeviceOrHostAddressConstKHR { DeviceAddress = boxes.DeviceAddress },
            Stride = 24,
        };
        uint primitives = count;
        var range = new AccelerationStructureBuildRangeInfoKHR { PrimitiveCount = primitives };
        barriers.Use(boxes, BufferUse.AccelerationStructureBuildInput);
        return Build(device, cmd, barriers, &geometry, 1, &primitives, &range, name);
    }

    public void Dispose()
    {
        _device.Api.DestroyAccelerationStructure(_device.Context.Device, Structure, null);
        Storage.Dispose();
    }

    private static BottomLevelStructure Build(AccelerationStructureDevice device, CommandBuffer cmd, BarrierBatch barriers,
        AccelerationStructureGeometryKHR* geometries, uint geometryCount, uint* primitiveCounts, AccelerationStructureBuildRangeInfoKHR* ranges, string name)
    {
        var context = device.Context;
        var build = new AccelerationStructureBuildGeometryInfoKHR
        {
            SType = StructureType.AccelerationStructureBuildGeometryInfoKhr,
            Type = AccelerationStructureTypeKHR.BottomLevelKhr,
            Flags = BuildAccelerationStructureFlagsKHR.PreferFastTraceBitKhr,
            Mode = BuildAccelerationStructureModeKHR.BuildKhr,
            GeometryCount = geometryCount,
            PGeometries = geometries,
        };
        var sizes = new AccelerationStructureBuildSizesInfoKHR { SType = StructureType.AccelerationStructureBuildSizesInfoKhr };
        device.Api.GetAccelerationStructureBuildSizes(context.Device, AccelerationStructureBuildTypeKHR.DeviceKhr, &build, primitiveCounts, &sizes);

        var storage = GpuBuffer.Create(context, Math.Max(sizes.AccelerationStructureSize, 256),
            BufferUsageFlags.AccelerationStructureStorageBitKhr | BufferUsageFlags.ShaderDeviceAddressBit, MemoryUsage.GpuOnly, $"BLAS '{name}'");
        AccelerationStructureKHR structure = default;
        try
        {
            structure = device.Create(storage, AccelerationStructureTypeKHR.BottomLevelKhr, sizes.AccelerationStructureSize);
            var scratch = GpuBuffer.Create(context, sizes.BuildScratchSize + device.ScratchAlignment,
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.ShaderDeviceAddressBit, MemoryUsage.GpuOnly, "BLAS scratch");
            context.DeferDispose(scratch); // destroyed after the frames recorded now have completed

            barriers.Use(storage, BufferUse.AccelerationStructureBuild);
            barriers.Use(scratch, BufferUse.AccelerationStructureBuild);
            barriers.Flush();
            build.DstAccelerationStructure = structure;
            build.ScratchData = new DeviceOrHostAddressKHR { DeviceAddress = VkUtil.AlignUp(scratch.DeviceAddress, device.ScratchAlignment) };
            device.Api.CmdBuildAccelerationStructures(cmd, 1, &build, &ranges);
            context.SetDebugName(ObjectType.AccelerationStructureKhr, structure.Handle, $"BLAS '{name}'");
            return new BottomLevelStructure(device, storage, structure);
        }
        catch
        {
            if (structure.Handle != 0)
                device.Api.DestroyAccelerationStructure(context.Device, structure, null);
            storage.Dispose();
            throw;
        }
    }
}

/// <summary>
/// A top-level acceleration structure rebuilt from a list of <see cref="TlasInstance"/>s: instance data through the
/// staging ring into a device-local buffer, structure and scratch buffers grown (never shrunk) as needed, the old ones
/// retired through the context's deferred destruction.
/// </summary>
internal sealed unsafe class TopLevelStructure : IDisposable
{
    private readonly AccelerationStructureDevice _device;
    private readonly string _name;
    private GpuBuffer? _instanceBuffer;
    private GpuBuffer? _buffer;
    private GpuBuffer? _scratch;
    private AccelerationStructureKHR _structure;

    public TopLevelStructure(AccelerationStructureDevice device, string name)
    {
        _device = device;
        _name = name;
    }

    /// <summary>The structure (default before the first build).</summary>
    public AccelerationStructureKHR Handle => _structure;

    /// <summary>A structure exists.</summary>
    public bool IsBuilt => _structure.Handle != 0;

    /// <summary>Records the build over <paramref name="instances"/> (0 instances give an empty but valid structure).</summary>
    public void Build(CommandBuffer cmd, BarrierBatch barriers, ReadOnlySpan<TlasInstance> instances)
    {
        var context = _device.Context;
        var api = _device.Api;
        int count = instances.Length;
        ulong instanceBytes = (ulong)Math.Max(count, 1) * (ulong)sizeof(TlasInstance);
        if (_instanceBuffer is null || _instanceBuffer.Size < instanceBytes)
        {
            if (_instanceBuffer is not null)
                context.DeferDispose(_instanceBuffer);
            _instanceBuffer = GpuBuffer.Create(context, Math.Max(instanceBytes * 2, 64 * (ulong)sizeof(TlasInstance)),
                BufferUsageFlags.AccelerationStructureBuildInputReadOnlyBitKhr | BufferUsageFlags.ShaderDeviceAddressBit | BufferUsageFlags.TransferDstBit,
                MemoryUsage.GpuOnly, $"{_name} instances");
        }

        barriers.Use(_instanceBuffer, BufferUse.TransferDestination);
        barriers.Flush();
        if (count > 0)
        {
            var region = context.Upload.Allocate(instanceBytes, 16);
            fixed (TlasInstance* src = instances)
                System.Buffer.MemoryCopy(src, region.Pointer, (long)instanceBytes, (long)instanceBytes);
            var copy = new BufferCopy(region.Offset, 0, instanceBytes);
            context.Vk.CmdCopyBuffer(cmd, region.Buffer, _instanceBuffer.Handle, 1, &copy);
        }

        var geometry = new AccelerationStructureGeometryKHR
        {
            SType = StructureType.AccelerationStructureGeometryKhr,
            GeometryType = GeometryTypeKHR.InstancesKhr,
        };
        geometry.Geometry.Instances = new AccelerationStructureGeometryInstancesDataKHR
        {
            SType = StructureType.AccelerationStructureGeometryInstancesDataKhr,
            ArrayOfPointers = false,
            Data = new DeviceOrHostAddressConstKHR { DeviceAddress = _instanceBuffer.DeviceAddress },
        };
        var build = new AccelerationStructureBuildGeometryInfoKHR
        {
            SType = StructureType.AccelerationStructureBuildGeometryInfoKhr,
            Type = AccelerationStructureTypeKHR.TopLevelKhr,
            Flags = BuildAccelerationStructureFlagsKHR.PreferFastTraceBitKhr,
            Mode = BuildAccelerationStructureModeKHR.BuildKhr,
            GeometryCount = 1,
            PGeometries = &geometry,
        };
        uint primitiveCount = (uint)count;
        var sizes = new AccelerationStructureBuildSizesInfoKHR { SType = StructureType.AccelerationStructureBuildSizesInfoKhr };
        api.GetAccelerationStructureBuildSizes(context.Device, AccelerationStructureBuildTypeKHR.DeviceKhr, &build, &primitiveCount, &sizes);

        if (_buffer is null || _buffer.Size < sizes.AccelerationStructureSize)
        {
            if (_structure.Handle != 0)
            {
                var old = _structure;
                var device = context.Device;
                context.DeferDestroy(() => api.DestroyAccelerationStructure(device, old, null));
                _structure = default;
            }

            if (_buffer is not null)
                context.DeferDispose(_buffer);
            ulong size = Math.Max(sizes.AccelerationStructureSize * 2, 4096);
            _buffer = GpuBuffer.Create(context, size, BufferUsageFlags.AccelerationStructureStorageBitKhr | BufferUsageFlags.ShaderDeviceAddressBit,
                MemoryUsage.GpuOnly, _name);
            _structure = _device.Create(_buffer, AccelerationStructureTypeKHR.TopLevelKhr, size);
            context.SetDebugName(ObjectType.AccelerationStructureKhr, _structure.Handle, _name);
        }

        ulong scratchSize = sizes.BuildScratchSize + _device.ScratchAlignment;
        if (_scratch is null || _scratch.Size < scratchSize)
        {
            if (_scratch is not null)
                context.DeferDispose(_scratch);
            _scratch = GpuBuffer.Create(context, Math.Max(scratchSize * 2, 4096), BufferUsageFlags.StorageBufferBit | BufferUsageFlags.ShaderDeviceAddressBit,
                MemoryUsage.GpuOnly, $"{_name} scratch");
        }

        barriers.Use(_instanceBuffer, BufferUse.AccelerationStructureBuildInput);
        barriers.Use(_buffer, BufferUse.AccelerationStructureBuild);
        barriers.Use(_scratch, BufferUse.AccelerationStructureBuild);
        barriers.Flush();
        build.DstAccelerationStructure = _structure;
        build.ScratchData = new DeviceOrHostAddressKHR { DeviceAddress = VkUtil.AlignUp(_scratch.DeviceAddress, _device.ScratchAlignment) };
        var range = new AccelerationStructureBuildRangeInfoKHR { PrimitiveCount = primitiveCount };
        var rangePointer = &range;
        api.CmdBuildAccelerationStructures(cmd, 1, &build, &rangePointer);
    }

    /// <summary>Declares the ray-query read of the structure (compute shaders).</summary>
    public void DeclareRead(BarrierBatch barriers)
    {
        if (_buffer is not null)
            barriers.Use(_buffer, BufferUse.AccelerationStructureRead);
    }

    /// <summary>Destroys the structure now (the caller made sure no frame in flight uses it).</summary>
    public void Dispose()
    {
        if (_structure.Handle != 0)
            _device.Api.DestroyAccelerationStructure(_device.Context.Device, _structure, null);
        _structure = default;
        _buffer?.Dispose();
        _scratch?.Dispose();
        _instanceBuffer?.Dispose();
        _buffer = _scratch = _instanceBuffer = null;
    }

    /// <summary>Retires the structure and its buffers through the context's deferred destruction (frames may still use them).</summary>
    public void DisposeDeferred()
    {
        var context = _device.Context;
        if (_structure.Handle != 0)
        {
            var old = _structure;
            var api = _device.Api;
            var device = context.Device;
            context.DeferDestroy(() => api.DestroyAccelerationStructure(device, old, null));
        }

        _structure = default;
        if (_buffer is not null)
            context.DeferDispose(_buffer);
        if (_scratch is not null)
            context.DeferDispose(_scratch);
        if (_instanceBuffer is not null)
            context.DeferDispose(_instanceBuffer);
        _buffer = _scratch = _instanceBuffer = null;
    }
}
