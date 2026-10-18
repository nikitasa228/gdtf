using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;

namespace Candela.Rendering.Vulkan;

/// <summary>
/// Acceleration structures of the world for ray queries (devices with <see cref="DeviceCapabilities.RayTracing"/>):
/// one bottom-level structure per mesh (one geometry per sub-mesh, built from the mesh's own vertex/index buffer the
/// first time the mesh is needed; released when the mesh is destroyed) and a top-level structure over the visible,
/// non-reference instances, rebuilt once per frame when ray-traced GI runs. A geometry table lets the shaders fetch
/// vertices and materials of a hit (buffer device addresses, sub-mesh → material slot). Main thread only.
/// </summary>
internal sealed unsafe class RayTracingScene : IDisposable
{
    private readonly VulkanContext _context;
    private readonly VulkanRenderWorld _world;
    private readonly KhrAccelerationStructure _api;
    private readonly ulong _scratchAlignment;
    private readonly Dictionary<MeshRecord, Blas> _blas = new(ReferenceEqualityComparer.Instance);
    private readonly List<MeshRecord> _deadMeshes = new();
    private readonly SceneBuffer<uint> _geometry;
    private AccelerationStructureInstance[] _instances = new AccelerationStructureInstance[64];
    private GpuBuffer? _instanceBuffer;
    private GpuBuffer? _tlasBuffer;
    private GpuBuffer? _tlasScratch;
    private AccelerationStructureKHR _tlas;
    private ulong _builtFrame;

    public RayTracingScene(VulkanContext context, VulkanRenderWorld world)
    {
        _context = context;
        _world = world;
        if (!context.Vk.TryGetDeviceExtension(context.Instance, context.Device, out KhrAccelerationStructure api))
            throw new VulkanException(Result.ErrorExtensionNotPresent, "VK_KHR_acceleration_structure functions");
        _api = api;
        var asProperties = new PhysicalDeviceAccelerationStructurePropertiesKHR { SType = StructureType.PhysicalDeviceAccelerationStructurePropertiesKhr };
        var properties = new PhysicalDeviceProperties2 { SType = StructureType.PhysicalDeviceProperties2, PNext = &asProperties };
        context.Vk.GetPhysicalDeviceProperties2(context.PhysicalDevice, &properties);
        _scratchAlignment = Math.Max(asProperties.MinAccelerationStructureScratchOffsetAlignment, 1u);
        _geometry = new SceneBuffer<uint>(context, BufferUsageFlags.StorageBufferBit, "ray tracing geometry table", 256);
    }

    /// <summary>The top-level structure (default before the first build).</summary>
    public AccelerationStructureKHR Tlas => _tlas;

    /// <summary>A top-level structure exists.</summary>
    public bool HasTlas => _tlas.Handle != 0;

    /// <summary>Geometry table of the instances (see rt_gi.comp).</summary>
    public GpuBuffer GeometryTable => _geometry.Buffer;

    /// <summary>Instances in the last top-level build.</summary>
    public int InstanceCount { get; private set; }

    /// <summary>
    /// Builds missing bottom-level structures and rebuilds the top-level structure and the geometry table for
    /// <paramref name="frame"/> (once per frame, whatever the number of views).
    /// </summary>
    public void Build(VulkanFrame frame, BarrierBatch barriers)
    {
        if (_builtFrame == frame.Number)
            return;
        _builtFrame = frame.Number;
        var cmd = frame.CommandBuffer;
        barriers.CommandBuffer = cmd;
        ReleaseDestroyedMeshes();

        // Bottom level: every mesh of a live instance that has none yet.
        bool builtBlas = false;
        foreach (int index in _world.LiveInstances)
        {
            var instance = _world.InstanceAt(index);
            var mesh = instance.Mesh;
            if (!Traceable(instance) || _blas.ContainsKey(mesh))
                continue;
            BuildBlas(cmd, barriers, mesh);
            builtBlas = true;
        }

        if (builtBlas)
        {
            // Bottom-level builds finish before the top-level build reads them.
            barriers.AddGlobal(PipelineStageFlags2.AccelerationStructureBuildBitKhr, AccessFlags2.AccelerationStructureWriteBitKhr,
                PipelineStageFlags2.AccelerationStructureBuildBitKhr, AccessFlags2.AccelerationStructureReadBitKhr);
            barriers.Flush();
        }

        // Instances and the geometry table.
        int count = 0;
        int tableSize = Math.Max(_world.InstanceSlotCount, 1) * 8;
        _geometry.Resize(tableSize);
        foreach (int index in _world.LiveInstances)
        {
            var instance = _world.InstanceAt(index);
            if (!Traceable(instance) || !_blas.TryGetValue(instance.Mesh, out var blas))
                continue;
            if (count == _instances.Length)
                Array.Resize(ref _instances, _instances.Length * 2);
            _instances[count++] = Instance(instance.World, (uint)index, blas.Address);
            var mesh = instance.Mesh;
            int header = index * 8;
            ulong vertexAddress = mesh.Geometry!.DeviceAddress;
            ulong indexAddress = vertexAddress + mesh.IndexOffset;
            int subMeshes = _geometry.Count;
            _geometry.Resize(subMeshes + 2 * mesh.SubMeshes.Length);
            _geometry[header] = (uint)vertexAddress;
            _geometry[header + 1] = (uint)(vertexAddress >> 32);
            _geometry[header + 2] = (uint)indexAddress;
            _geometry[header + 3] = (uint)(indexAddress >> 32);
            _geometry[header + 4] = (uint)subMeshes;
            _geometry[header + 5] = (uint)mesh.SubMeshes.Length;
            for (int s = 0; s < mesh.SubMeshes.Length; s++)
            {
                _geometry[subMeshes + 2 * s] = (uint)mesh.SubMeshes[s].IndexStart;
                _geometry[subMeshes + 2 * s + 1] = (uint)mesh.SubMeshes[s].MaterialSlot;
            }
        }

        InstanceCount = count;
        _geometry.MarkDirty(0, _geometry.Count);
        if (_geometry.PrepareUpload(barriers))
        {
            barriers.Flush();
            _geometry.RecordUpload(cmd);
        }

        BuildTlas(cmd, barriers, count);
    }

    /// <summary>Declares the reads of the ray-query shaders (TLAS, geometry table, mesh buffers).</summary>
    public void DeclareReads(BarrierBatch barriers)
    {
        if (_tlasBuffer is not null)
            barriers.Use(_tlasBuffer, BufferUse.AccelerationStructureRead);
        barriers.Use(_geometry.Buffer, BufferUse.ComputeRead);
        foreach (var pair in _blas)
        {
            if (pair.Key.Geometry is { } geometry)
                barriers.Use(geometry, BufferUse.ComputeRead);
        }
    }

    public void Dispose()
    {
        foreach (var blas in _blas.Values)
            blas.Dispose(_api, _context.Device);
        _blas.Clear();
        if (_tlas.Handle != 0)
            _api.DestroyAccelerationStructure(_context.Device, _tlas, null);
        _tlas = default;
        _tlasBuffer?.Dispose();
        _tlasScratch?.Dispose();
        _instanceBuffer?.Dispose();
        _geometry.Dispose();
        _api.Dispose();
    }

    private static bool Traceable(InstanceRecord instance) =>
        (instance.Flags & (InstanceFlags.Hidden | InstanceFlags.Reference)) == 0 && !instance.Mesh.Destroyed && instance.Mesh.Geometry is not null &&
        instance.Mesh.IndexCount > 0;

    private void ReleaseDestroyedMeshes()
    {
        _deadMeshes.Clear();
        foreach (var mesh in _blas.Keys)
        {
            if (mesh.Destroyed)
                _deadMeshes.Add(mesh);
        }

        foreach (var mesh in _deadMeshes)
        {
            var blas = _blas[mesh];
            _blas.Remove(mesh);
            var api = _api;
            var device = _context.Device;
            _context.DeferDestroy(() => blas.Dispose(api, device));
        }
    }

    private void BuildBlas(CommandBuffer cmd, BarrierBatch barriers, MeshRecord mesh)
    {
        var geometryBuffer = mesh.Geometry!;
        int subMeshCount = mesh.SubMeshes.Length;
        var geometries = stackalloc AccelerationStructureGeometryKHR[subMeshCount];
        var ranges = stackalloc AccelerationStructureBuildRangeInfoKHR[subMeshCount];
        var primitiveCounts = stackalloc uint[subMeshCount];
        ulong vertexAddress = geometryBuffer.DeviceAddress;
        for (int i = 0; i < subMeshCount; i++)
        {
            var sub = mesh.SubMeshes[i];
            geometries[i] = new AccelerationStructureGeometryKHR
            {
                SType = StructureType.AccelerationStructureGeometryKhr,
                GeometryType = GeometryTypeKHR.TrianglesKhr,
                Flags = GeometryFlagsKHR.OpaqueBitKhr,
            };
            geometries[i].Geometry.Triangles = new AccelerationStructureGeometryTrianglesDataKHR
            {
                SType = StructureType.AccelerationStructureGeometryTrianglesDataKhr,
                VertexFormat = Format.R32G32B32Sfloat,
                VertexData = new DeviceOrHostAddressConstKHR { DeviceAddress = vertexAddress },
                VertexStride = (ulong)sizeof(GpuVertex),
                MaxVertex = Math.Max(mesh.VertexCount, 1u) - 1,
                IndexType = IndexType.Uint32,
                IndexData = new DeviceOrHostAddressConstKHR { DeviceAddress = vertexAddress + mesh.IndexOffset },
            };
            uint primitives = (uint)Math.Max(sub.IndexCount, 0) / 3;
            primitiveCounts[i] = primitives;
            ranges[i] = new AccelerationStructureBuildRangeInfoKHR
            {
                PrimitiveCount = primitives,
                PrimitiveOffset = (uint)sub.IndexStart * 4,
            };
        }

        var build = new AccelerationStructureBuildGeometryInfoKHR
        {
            SType = StructureType.AccelerationStructureBuildGeometryInfoKhr,
            Type = AccelerationStructureTypeKHR.BottomLevelKhr,
            Flags = BuildAccelerationStructureFlagsKHR.PreferFastTraceBitKhr,
            Mode = BuildAccelerationStructureModeKHR.BuildKhr,
            GeometryCount = (uint)subMeshCount,
            PGeometries = geometries,
        };
        var sizes = new AccelerationStructureBuildSizesInfoKHR { SType = StructureType.AccelerationStructureBuildSizesInfoKhr };
        _api.GetAccelerationStructureBuildSizes(_context.Device, AccelerationStructureBuildTypeKHR.DeviceKhr, &build, primitiveCounts, &sizes);

        var storage = GpuBuffer.Create(_context, Math.Max(sizes.AccelerationStructureSize, 256),
            BufferUsageFlags.AccelerationStructureStorageBitKhr | BufferUsageFlags.ShaderDeviceAddressBit, MemoryUsage.GpuOnly, $"BLAS '{mesh.Name}'");
        var structure = CreateStructure(storage, AccelerationStructureTypeKHR.BottomLevelKhr, sizes.AccelerationStructureSize);
        var scratch = GpuBuffer.Create(_context, sizes.BuildScratchSize + _scratchAlignment,
            BufferUsageFlags.StorageBufferBit | BufferUsageFlags.ShaderDeviceAddressBit, MemoryUsage.GpuOnly, "BLAS scratch");
        _context.DeferDispose(scratch); // destroyed after this frame completes

        barriers.Use(geometryBuffer, BufferUse.AccelerationStructureBuildInput);
        barriers.Use(storage, BufferUse.AccelerationStructureBuild);
        barriers.Use(scratch, BufferUse.AccelerationStructureBuild);
        barriers.Flush();
        build.DstAccelerationStructure = structure;
        build.ScratchData = new DeviceOrHostAddressKHR { DeviceAddress = VkUtil.AlignUp(scratch.DeviceAddress, _scratchAlignment) };
        var rangePointer = ranges;
        _api.CmdBuildAccelerationStructures(cmd, 1, &build, &rangePointer);
        var addressInfo = new AccelerationStructureDeviceAddressInfoKHR
        {
            SType = StructureType.AccelerationStructureDeviceAddressInfoKhr,
            AccelerationStructure = structure,
        };
        ulong address = _api.GetAccelerationStructureDeviceAddress(_context.Device, &addressInfo);
        _blas[mesh] = new Blas(storage, structure, address);
        _context.SetDebugName(ObjectType.AccelerationStructureKhr, structure.Handle, $"BLAS '{mesh.Name}'");
    }

    private void BuildTlas(CommandBuffer cmd, BarrierBatch barriers, int count)
    {
        // Instance data through the staging ring into a device-local buffer.
        ulong instanceBytes = (ulong)Math.Max(count, 1) * (ulong)sizeof(AccelerationStructureInstance);
        if (_instanceBuffer is null || _instanceBuffer.Size < instanceBytes)
        {
            if (_instanceBuffer is not null)
                _context.DeferDispose(_instanceBuffer);
            _instanceBuffer = GpuBuffer.Create(_context, Math.Max(instanceBytes * 2, 64 * (ulong)sizeof(AccelerationStructureInstance)),
                BufferUsageFlags.AccelerationStructureBuildInputReadOnlyBitKhr | BufferUsageFlags.ShaderDeviceAddressBit | BufferUsageFlags.TransferDstBit,
                MemoryUsage.GpuOnly, "TLAS instances");
        }

        barriers.Use(_instanceBuffer, BufferUse.TransferDestination);
        barriers.Flush();
        if (count > 0)
        {
            var region = _context.Upload.Allocate(instanceBytes, 16);
            fixed (AccelerationStructureInstance* src = _instances)
                System.Buffer.MemoryCopy(src, region.Pointer, (long)instanceBytes, (long)instanceBytes);
            var copy = new BufferCopy(region.Offset, 0, instanceBytes);
            _context.Vk.CmdCopyBuffer(cmd, region.Buffer, _instanceBuffer.Handle, 1, &copy);
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
        _api.GetAccelerationStructureBuildSizes(_context.Device, AccelerationStructureBuildTypeKHR.DeviceKhr, &build, &primitiveCount, &sizes);

        if (_tlasBuffer is null || _tlasBuffer.Size < sizes.AccelerationStructureSize)
        {
            if (_tlas.Handle != 0)
            {
                var old = _tlas;
                var api = _api;
                var device = _context.Device;
                _context.DeferDestroy(() => api.DestroyAccelerationStructure(device, old, null));
                _tlas = default;
            }

            if (_tlasBuffer is not null)
                _context.DeferDispose(_tlasBuffer);
            ulong size = Math.Max(sizes.AccelerationStructureSize * 2, 4096);
            _tlasBuffer = GpuBuffer.Create(_context, size, BufferUsageFlags.AccelerationStructureStorageBitKhr | BufferUsageFlags.ShaderDeviceAddressBit,
                MemoryUsage.GpuOnly, "TLAS");
            _tlas = CreateStructure(_tlasBuffer, AccelerationStructureTypeKHR.TopLevelKhr, size);
            _context.SetDebugName(ObjectType.AccelerationStructureKhr, _tlas.Handle, "TLAS");
        }

        ulong scratchSize = sizes.BuildScratchSize + _scratchAlignment;
        if (_tlasScratch is null || _tlasScratch.Size < scratchSize)
        {
            if (_tlasScratch is not null)
                _context.DeferDispose(_tlasScratch);
            _tlasScratch = GpuBuffer.Create(_context, Math.Max(scratchSize * 2, 4096), BufferUsageFlags.StorageBufferBit | BufferUsageFlags.ShaderDeviceAddressBit,
                MemoryUsage.GpuOnly, "TLAS scratch");
        }

        barriers.Use(_instanceBuffer, BufferUse.AccelerationStructureBuildInput);
        barriers.Use(_tlasBuffer, BufferUse.AccelerationStructureBuild);
        barriers.Use(_tlasScratch, BufferUse.AccelerationStructureBuild);
        barriers.Flush();
        build.DstAccelerationStructure = _tlas;
        build.ScratchData = new DeviceOrHostAddressKHR { DeviceAddress = VkUtil.AlignUp(_tlasScratch.DeviceAddress, _scratchAlignment) };
        var range = new AccelerationStructureBuildRangeInfoKHR { PrimitiveCount = primitiveCount };
        var rangePointer = &range;
        _api.CmdBuildAccelerationStructures(cmd, 1, &build, &rangePointer);
    }

    private AccelerationStructureKHR CreateStructure(GpuBuffer buffer, AccelerationStructureTypeKHR type, ulong size)
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
        VkUtil.Check(_api.CreateAccelerationStructure(_context.Device, &info, null, &structure), "vkCreateAccelerationStructureKHR");
        return structure;
    }

    /// <summary>VkAccelerationStructureInstanceKHR: 3×4 row-major object-to-world transform (column-vector convention).</summary>
    private static AccelerationStructureInstance Instance(in Matrix4x4 w, uint customIndex, ulong blasAddress)
    {
        // System.Numerics uses p' = p·W; the instance transform is M with p' = M·p, i.e. M = Wᵀ (first three rows).
        var i = new AccelerationStructureInstance
        {
            CustomIndexAndMask = (customIndex & 0xFFFFFFu) | (0xFFu << 24),
            SbtOffsetAndFlags = (uint)GeometryInstanceFlagsKHR.TriangleFacingCullDisableBitKhr << 24,
            AccelerationStructureReference = blasAddress,
        };
        i.Transform[0] = w.M11;
        i.Transform[1] = w.M21;
        i.Transform[2] = w.M31;
        i.Transform[3] = w.M41;
        i.Transform[4] = w.M12;
        i.Transform[5] = w.M22;
        i.Transform[6] = w.M32;
        i.Transform[7] = w.M42;
        i.Transform[8] = w.M13;
        i.Transform[9] = w.M23;
        i.Transform[10] = w.M33;
        i.Transform[11] = w.M43;
        return i;
    }

    /// <summary>Layout of <c>VkAccelerationStructureInstanceKHR</c> (64 bytes; the bit fields pack low bits first).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct AccelerationStructureInstance
    {
        public fixed float Transform[12];
        public uint CustomIndexAndMask;
        public uint SbtOffsetAndFlags;
        public ulong AccelerationStructureReference;
    }

    private sealed class Blas
    {
        public Blas(GpuBuffer storage, AccelerationStructureKHR structure, ulong address)
        {
            Storage = storage;
            Structure = structure;
            Address = address;
        }

        public GpuBuffer Storage { get; }

        public AccelerationStructureKHR Structure { get; }

        public ulong Address { get; }

        public void Dispose(KhrAccelerationStructure api, Device device)
        {
            api.DestroyAccelerationStructure(device, Structure, null);
            Storage.Dispose();
        }
    }
}
