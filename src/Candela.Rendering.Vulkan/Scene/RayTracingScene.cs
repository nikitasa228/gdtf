using Silk.NET.Vulkan;

namespace Candela.Rendering.Vulkan;

/// <summary>
/// Acceleration structures of the world for ray queries (devices with <see cref="DeviceCapabilities.RayTracing"/>):
/// one bottom-level structure per mesh (one geometry per sub-mesh, built from the mesh's own vertex/index buffer the
/// first time the mesh is needed; released when the mesh is destroyed), the instance list of the visible instances, a
/// geometry table that lets the shaders fetch vertices and materials of a hit (buffer device addresses, sub-mesh →
/// material slot), and the top-level structure over the instances that the ray-traced GI traces. Reference instances
/// (<see cref="InstanceFlags.Reference"/>: drawings, ghost geometry) carry only <see cref="ReferenceMask"/>, so only rays
/// that ask for them (the path tracer's camera rays) see them; they neither occlude nor reflect light. The path tracer
/// composes its own top-level structure from <see cref="Instances"/> plus its light geometry. Main thread only.
/// </summary>
internal sealed unsafe class RayTracingScene : IDisposable
{
    /// <summary>Cull-mask bit of the scene's instances (every ray that should see geometry includes it).</summary>
    public const byte SceneMask = 0x01;

    /// <summary>
    /// Cull-mask bit of instances that cast shadows (<see cref="InstanceFlags.CastShadow"/>), in addition to
    /// <see cref="SceneMask"/>: shadow rays of the path tracer test only these, like the real-time shadow maps.
    /// </summary>
    public const byte ShadowCasterMask = 0x08;

    /// <summary>
    /// Cull-mask bit of reference instances (<see cref="InstanceFlags.Reference"/>), alone: they are shown to the camera
    /// (the path tracer's camera rays include this bit) and are invisible to every ray that carries light.
    /// </summary>
    public const byte ReferenceMask = 0x10;

    private readonly VulkanContext _context;
    private readonly VulkanRenderWorld _world;
    private readonly Dictionary<MeshRecord, BottomLevelStructure> _blas = new(ReferenceEqualityComparer.Instance);
    private readonly List<MeshRecord> _deadMeshes = new();
    private readonly SceneBuffer<uint> _geometry;
    private readonly TopLevelStructure _tlas;
    private BottomLevelStructure.TriangleGeometry[] _subMeshGeometries = new BottomLevelStructure.TriangleGeometry[8];
    private TlasInstance[] _instances = new TlasInstance[64];
    private ulong _preparedFrame;
    private ulong _preparedVersion;
    private ulong _builtFrame;
    private ulong _tlasVersion;

    public RayTracingScene(VulkanContext context, VulkanRenderWorld world)
    {
        _context = context;
        _world = world;
        Device = new AccelerationStructureDevice(context);
        _geometry = new SceneBuffer<uint>(context, BufferUsageFlags.StorageBufferBit, "ray tracing geometry table", 256);
        _tlas = new TopLevelStructure(Device, "TLAS");
    }

    /// <summary>The device's acceleration-structure functions (shared with the path tracer).</summary>
    public AccelerationStructureDevice Device { get; }

    /// <summary>The top-level structure of the scene's instances (default before the first build).</summary>
    public AccelerationStructureKHR Tlas => _tlas.Handle;

    /// <summary>A top-level structure exists.</summary>
    public bool HasTlas => _tlas.IsBuilt;

    /// <summary>Geometry table of the instances (see rt_geometry.glsl).</summary>
    public GpuBuffer GeometryTable => _geometry.Buffer;

    /// <summary>Instances in the last top-level build.</summary>
    public int InstanceCount { get; private set; }

    /// <summary>
    /// The traceable instances of the last <see cref="PrepareGeometry"/> (custom index = instance slot, mask
    /// <see cref="SceneMask"/>, plus <see cref="ShadowCasterMask"/> for shadow casters; <see cref="ReferenceMask"/> alone
    /// for reference instances).
    /// </summary>
    public ReadOnlySpan<TlasInstance> Instances => _instances.AsSpan(0, InstanceCount);

    /// <summary>
    /// <see cref="VulkanRenderWorld.GeometryVersion"/> that <see cref="Instances"/> and the geometry table reflect: a
    /// top-level structure built from them is current while this value has not changed.
    /// </summary>
    public ulong PreparedVersion => _preparedVersion;

    /// <summary>
    /// Builds missing bottom-level structures and refreshes the instance list and the geometry table for
    /// <paramref name="frame"/> when the world's geometry changed (at most once per frame).
    /// </summary>
    public void PrepareGeometry(VulkanFrame frame, BarrierBatch barriers)
    {
        if (_preparedFrame == frame.Number)
            return;
        _preparedFrame = frame.Number;
        var cmd = frame.CommandBuffer;
        barriers.CommandBuffer = cmd;
        ReleaseDestroyedMeshes();
        if (_preparedVersion == _world.GeometryVersion && _preparedVersion != 0)
            return;

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
            // Bottom-level builds finish before the top-level build reads them, and their memory is made visible to
            // the ray queries that traverse them (through the TLAS) in compute shaders of this and later frames.
            barriers.AddGlobal(PipelineStageFlags2.AccelerationStructureBuildBitKhr, AccessFlags2.AccelerationStructureWriteBitKhr,
                PipelineStageFlags2.AccelerationStructureBuildBitKhr | PipelineStageFlags2.ComputeShaderBit, AccessFlags2.AccelerationStructureReadBitKhr);
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
            byte mask = (instance.Flags & InstanceFlags.Reference) != 0 ? ReferenceMask
                : (instance.Flags & InstanceFlags.CastShadow) != 0 ? (byte)(SceneMask | ShadowCasterMask)
                : SceneMask;
            _instances[count++] = TlasInstance.Create(instance.World, (uint)index, mask, blas.Address);
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

        _preparedVersion = _world.GeometryVersion;
    }

    /// <summary>
    /// Prepares the geometry and rebuilds the scene's top-level structure for <paramref name="frame"/> when the
    /// geometry changed (once per frame, whatever the number of views).
    /// </summary>
    public void Build(VulkanFrame frame, BarrierBatch barriers)
    {
        PrepareGeometry(frame, barriers);
        if (_builtFrame == frame.Number || (_tlas.IsBuilt && _tlasVersion == _preparedVersion))
            return;
        _builtFrame = frame.Number;
        _tlasVersion = _preparedVersion;
        _tlas.Build(frame.CommandBuffer, barriers, Instances);
    }

    /// <summary>Declares the reads of the ray-query shaders (scene TLAS, geometry table, mesh buffers).</summary>
    public void DeclareReads(BarrierBatch barriers)
    {
        _tlas.DeclareRead(barriers);
        DeclareGeometryReads(barriers);
    }

    /// <summary>Declares the reads of the geometry table and the mesh buffers (hit shading through buffer addresses).</summary>
    public void DeclareGeometryReads(BarrierBatch barriers)
    {
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
            blas.Dispose();
        _blas.Clear();
        _tlas.Dispose();
        _geometry.Dispose();
        Device.Dispose();
    }

    private static bool Traceable(InstanceRecord instance) =>
        (instance.Flags & InstanceFlags.Hidden) == 0 && !instance.Mesh.Destroyed && instance.Mesh.Geometry is not null &&
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
            _context.DeferDispose(blas);
        }
    }

    private void BuildBlas(CommandBuffer cmd, BarrierBatch barriers, MeshRecord mesh)
    {
        int subMeshCount = mesh.SubMeshes.Length;
        if (_subMeshGeometries.Length < subMeshCount)
            _subMeshGeometries = new BottomLevelStructure.TriangleGeometry[Math.Max(subMeshCount, _subMeshGeometries.Length * 2)];
        for (int i = 0; i < subMeshCount; i++)
        {
            var sub = mesh.SubMeshes[i];
            _subMeshGeometries[i] = new BottomLevelStructure.TriangleGeometry(mesh.IndexOffset + (ulong)sub.IndexStart * 4, Math.Max(sub.IndexCount, 0),
                Opaque: true);
        }

        _blas[mesh] = BottomLevelStructure.BuildTriangles(Device, cmd, barriers, mesh.Geometry!, mesh.VertexCount, (ulong)sizeof(GpuVertex),
            _subMeshGeometries.AsSpan(0, subMeshCount), mesh.Name);
    }
}
