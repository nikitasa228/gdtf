using System.Numerics;
using Candela.Core.Geometry;
using Silk.NET.Vulkan;

namespace Candela.Rendering.Vulkan;

/// <summary>A mesh: vertices and indices in one device-local buffer, plus the optional edge list.</summary>
internal sealed class MeshRecord
{
    public required string Name { get; init; }
    public GpuBuffer? Geometry { get; set; }
    public uint VertexCount { get; init; }
    public uint IndexCount { get; init; }

    /// <summary>Byte offset of the 32-bit indices in <see cref="Geometry"/>.</summary>
    public ulong IndexOffset { get; init; }

    public required SubMesh[] SubMeshes { get; init; }

    /// <summary>Number of material slots (highest slot + 1).</summary>
    public int SlotCount { get; init; }

    /// <summary>Object-space bounding sphere (centre, radius).</summary>
    public Vector4 BoundingSphere { get; init; }

    /// <summary>Unique edges as endpoint pairs (edge-shader wireframe), or null.</summary>
    public GpuBuffer? Edges { get; set; }

    public uint EdgeCount { get; init; }
    public bool Destroyed { get; set; }
}

/// <summary>A material: its GPU data, how it is drawn and the cached textures it holds.</summary>
internal sealed class MaterialRecord
{
    public GpuMaterial Gpu;
    public DrawKind Kind;
    public bool DoubleSided;
    public int Index;
    public int InstanceReferences;
    public bool Destroyed;
    public readonly List<TextureRecord> Textures = new();
}

/// <summary>A placed mesh.</summary>
internal sealed class InstanceRecord
{
    public required MeshRecord Mesh;
    public Matrix4x4 World;
    public InstanceFlags Flags;
    public uint PickId;
    public int Index;
    public int SlotOffset;
    public int SlotCount;
    public required MaterialRecord?[] Materials;
    public Vector4 WorldSphere;
    public int LivePosition;
}

/// <summary>Counts of live objects in a <see cref="VulkanRenderWorld"/> (diagnostics and leak tests).</summary>
/// <param name="Meshes">Meshes.</param>
/// <param name="Textures">Textures (user textures and cached material images, without the three defaults).</param>
/// <param name="Materials">Materials.</param>
/// <param name="Instances">Instances.</param>
/// <param name="Lights">Lights of the current frame.</param>
/// <param name="LineSegments">Line segments of the current frame.</param>
public readonly record struct RenderWorldStatistics(int Meshes, int Textures, int Materials, int Instances, int Lights, int LineSegments);

/// <summary>
/// The retained GPU scene behind <see cref="IRenderWorld"/>. Meshes live in device-local vertex/index buffers;
/// textures (full mip chains) in one bindless descriptor array; materials, instances, instance material slots and
/// lights in device-local storage buffers mirrored on the CPU and updated incrementally through the staging ring.
/// Handles stay valid until destroyed (stale handles are rejected). Every change is recorded into the next frame's
/// command buffer by <see cref="PrepareFrame"/> with the barriers it needs; nothing waits for the device.
/// One world is shared by all render targets of a renderer. Main thread only.
/// </summary>
public sealed unsafe class VulkanRenderWorld : IRenderWorld, IDisposable
{
    private const BufferUsageFlags StorageUsage = BufferUsageFlags.StorageBufferBit;

    private static readonly uint[] BufferBindings =
    {
        ShaderConstants.BindingMaterials, ShaderConstants.BindingInstances, ShaderConstants.BindingMaterialSlots, ShaderConstants.BindingLights,
    };
    private readonly VulkanContext _context;
    private readonly bool _buildEdges;
    private readonly HandleTable<MeshRecord> _meshes = new();
    private readonly HandleTable<TextureRecord> _userTextures = new();
    private readonly Dictionary<(string Key, bool Srgb), TextureRecord> _imageCache = new();
    private readonly HandleTable<MaterialRecord> _materials = new(firstIndex: 1);
    private readonly HandleTable<InstanceRecord> _instances = new();
    private readonly TextureStore _textures;
    private readonly SceneBuffer<GpuMaterial> _materialBuffer;
    private readonly SceneBuffer<GpuInstance> _instanceBuffer;
    private readonly SceneBuffer<uint> _slotBuffer;
    private readonly SceneBuffer<GpuLight> _lightBuffer;
    private readonly SceneBuffer<LineVertex> _lineBuffer;
    private readonly RangeAllocator _slotRanges = new();
    private readonly List<(GpuBuffer Destination, Buffer Source, ulong SourceOffset, ulong Size)> _pendingGeometry = new();
    private readonly List<GpuBuffer> _uploadedGeometry = new();
    private readonly List<(DescriptorPool Pool, int Free)> _pools = new();
    private readonly FrameSets[] _frameSets = new FrameSets[VulkanContext.FramesInFlight];
    private readonly List<NoiseSource> _noiseSources = new();
    private readonly HashSet<string> _missingImages = new(StringComparer.Ordinal);
    private int[] _live = new int[64];
    private int _liveCount;
    private DescriptorSetLayout _setLayout;
    private bool _disposed;

    internal VulkanRenderWorld(VulkanContext context, bool buildEdges)
    {
        _context = context;
        _buildEdges = buildEdges;
        _textures = new TextureStore(context);
        try
        {
            _materialBuffer = new SceneBuffer<GpuMaterial>(context, StorageUsage, "materials", 64);
            _instanceBuffer = new SceneBuffer<GpuInstance>(context, StorageUsage, "instances", 256);
            _slotBuffer = new SceneBuffer<uint>(context, StorageUsage, "instance material slots", 1024);
            _lightBuffer = new SceneBuffer<GpuLight>(context, StorageUsage, "lights", 64);
            _lineBuffer = new SceneBuffer<LineVertex>(context, BufferUsageFlags.VertexBufferBit, "lines", 256);
            _materialBuffer.Resize(1);
            _materialBuffer[0] = MaterialPacker.Default;
            _materialBuffer.MarkDirty(0);
            for (int i = 0; i < _frameSets.Length; i++)
                _frameSets[i] = new FrameSets();
            CreateSetLayout();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>Current object counts.</summary>
    public RenderWorldStatistics Statistics => new(_meshes.Count, _textures.Count, _materials.Count, _instances.Count, LightCount, LineSegmentCount);

    /// <summary>The atmosphere of the current frame (stored for the stage-2 volumetric renderer).</summary>
    public AtmosphereState? Atmosphere { get; private set; }

    /// <summary>Noise sources of the current frame (stored for the stage-2 noise overlay).</summary>
    public IReadOnlyList<NoiseSource> NoiseSources => _noiseSources;

    internal DescriptorSetLayout SceneSetLayout => _setLayout;

    /// <summary>The scene descriptor set of the frame being recorded (valid after <see cref="PrepareFrame"/>).</summary>
    internal DescriptorSet CurrentSceneSet { get; private set; }

    internal int LightCount { get; private set; }

    internal int LineSegmentCount { get; private set; }

    internal GpuBuffer LineBuffer => _lineBuffer.Buffer;

    internal GpuBuffer MaterialBuffer => _materialBuffer.Buffer;

    internal GpuBuffer InstanceBuffer => _instanceBuffer.Buffer;

    internal GpuBuffer MaterialSlotBuffer => _slotBuffer.Buffer;

    internal GpuBuffer LightBuffer => _lightBuffer.Buffer;

    internal bool BuildsEdges => _buildEdges;

    internal ReadOnlySpan<int> LiveInstances => _live.AsSpan(0, _liveCount);

    internal ReadOnlySpan<GpuLight> Lights => _lightBuffer.Items;

    internal InstanceRecord InstanceAt(int index) => _instances.AtIndex(index)!;

    internal MaterialRecord? MaterialAt(int index) => _materials.AtIndex(index);

    internal TextureRecord? TextureFor(TextureHandle handle) => _userTextures.TryGet(handle.Value, out _);

    // ------------------------------------------------------------------ meshes

    /// <inheritdoc/>
    public MeshHandle CreateMesh(MeshData mesh)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(mesh);
        MeshProcessor.Validate(mesh);
        var subMeshes = mesh.SubMeshes.Count > 0 ? mesh.SubMeshes.ToArray() : new[] { new SubMesh(0, mesh.Indices.Length, 0) };
        int slotCount = 1;
        foreach (var s in subMeshes)
            slotCount = Math.Max(slotCount, s.MaterialSlot + 1);
        string name = string.IsNullOrEmpty(mesh.Name) ? "mesh" : mesh.Name;

        GpuBuffer? geometry = null, edges = null;
        uint edgeCount = 0;
        ulong indexOffset = 0;
        try
        {
            if (mesh.Positions.Length > 0 && mesh.Indices.Length > 0)
            {
                var vertices = MeshProcessor.BuildVertices(mesh);
                ulong vertexBytes = (ulong)vertices.Length * (ulong)sizeof(GpuVertex);
                indexOffset = VkUtil.AlignUp(vertexBytes, 16);
                ulong total = indexOffset + (ulong)mesh.Indices.Length * sizeof(uint);
                geometry = GpuBuffer.Create(_context, total,
                    BufferUsageFlags.VertexBufferBit | BufferUsageFlags.IndexBufferBit | BufferUsageFlags.TransferDstBit,
                    MemoryUsage.GpuOnly, $"mesh '{name}'");
                var region = _context.Upload.Allocate(total, 16);
                fixed (GpuVertex* v = vertices)
                    System.Buffer.MemoryCopy(v, region.Pointer, (long)total, (long)vertexBytes);
                fixed (uint* idx = mesh.Indices)
                    System.Buffer.MemoryCopy(idx, region.Pointer + indexOffset, (long)(total - indexOffset), mesh.Indices.Length * sizeof(uint));
                _pendingGeometry.Add((geometry, region.Buffer, region.Offset, total));

                if (_buildEdges)
                {
                    var edgePoints = MeshProcessor.BuildEdges(mesh);
                    if (edgePoints.Length > 0)
                    {
                        ulong edgeBytes = (ulong)edgePoints.Length * (ulong)sizeof(Vector3);
                        edges = GpuBuffer.Create(_context, edgeBytes, BufferUsageFlags.VertexBufferBit | BufferUsageFlags.TransferDstBit,
                            MemoryUsage.GpuOnly, $"mesh '{name}' edges");
                        var edgeRegion = _context.Upload.Allocate(edgeBytes, 16);
                        fixed (Vector3* e = edgePoints)
                            System.Buffer.MemoryCopy(e, edgeRegion.Pointer, (long)edgeBytes, (long)edgeBytes);
                        _pendingGeometry.Add((edges, edgeRegion.Buffer, edgeRegion.Offset, edgeBytes));
                        edgeCount = (uint)(edgePoints.Length / 2);
                    }
                }
            }
        }
        catch
        {
            geometry?.Dispose();
            edges?.Dispose();
            throw;
        }

        var record = new MeshRecord
        {
            Name = name,
            Geometry = geometry,
            VertexCount = (uint)mesh.Positions.Length,
            IndexCount = (uint)mesh.Indices.Length,
            IndexOffset = indexOffset,
            SubMeshes = subMeshes,
            SlotCount = slotCount,
            BoundingSphere = MeshProcessor.BoundingSphere(mesh.Positions),
            Edges = edges,
            EdgeCount = edgeCount,
        };
        return new MeshHandle(_meshes.Add(record, out _));
    }

    /// <inheritdoc/>
    public void DestroyMesh(MeshHandle mesh)
    {
        var record = _meshes.Remove(mesh.Value, out _);
        if (record is null)
            return;
        record.Destroyed = true; // instances still referencing it are skipped
        if (record.Geometry is not null)
            _context.DeferDispose(record.Geometry);
        if (record.Edges is not null)
            _context.DeferDispose(record.Edges);
        record.Geometry = null;
        record.Edges = null;
    }

    // ------------------------------------------------------------------ textures

    /// <inheritdoc/>
    public TextureHandle CreateTexture(ImageData image, bool srgb)
    {
        ThrowIfDisposed();
        var record = _textures.Create(image, srgb, "texture");
        return new TextureHandle(_userTextures.Add(record, out _));
    }

    /// <inheritdoc/>
    public void DestroyTexture(TextureHandle texture)
    {
        var record = _userTextures.Remove(texture.Value, out _);
        if (record is not null)
            _textures.Destroy(record);
    }

    // ------------------------------------------------------------------ materials

    /// <inheritdoc/>
    public MaterialHandle CreateMaterial(MaterialData material, IImageSource? images)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(material);
        var record = new MaterialRecord();
        int handle = _materials.Add(record, out int index);
        record.Index = index;
        if (_materialBuffer.Count <= index)
            _materialBuffer.Resize(index + 1);
        Apply(record, material, images);
        return new MaterialHandle(handle);
    }

    /// <inheritdoc/>
    public void UpdateMaterial(MaterialHandle handle, MaterialData material, IImageSource? images)
    {
        ArgumentNullException.ThrowIfNull(material);
        var record = _materials.Get(handle.Value, out _, "material");
        var previous = record.Textures.ToArray();
        record.Textures.Clear();
        Apply(record, material, images); // acquire the new textures before releasing the old (shared images stay alive)
        foreach (var t in previous)
            ReleaseCached(t);
    }

    /// <inheritdoc/>
    public void DestroyMaterial(MaterialHandle material)
    {
        var record = _materials.Remove(material.Value, out int index, freeSlot: false);
        if (record is null)
            return;
        record.Destroyed = true;
        foreach (var t in record.Textures)
            ReleaseCached(t);
        record.Textures.Clear();
        // Instances that still use it draw the default material until they are given another one.
        record.Gpu = MaterialPacker.Default;
        record.Kind = DrawKind.Opaque;
        record.DoubleSided = false;
        _materialBuffer[index] = MaterialPacker.Default;
        _materialBuffer.MarkDirty(index);
        if (record.InstanceReferences == 0)
            _materials.FreeSlot(index);
    }

    // ------------------------------------------------------------------ instances

    /// <inheritdoc/>
    public InstanceHandle CreateInstance(MeshHandle mesh, ReadOnlySpan<MaterialHandle> materials, Matrix4x4 world, InstanceFlags flags, uint pickId)
    {
        ThrowIfDisposed();
        var meshRecord = _meshes.Get(mesh.Value, out _, "mesh");
        var record = new InstanceRecord
        {
            Mesh = meshRecord,
            World = world,
            Flags = flags,
            PickId = pickId,
            SlotCount = meshRecord.SlotCount,
            Materials = new MaterialRecord?[meshRecord.SlotCount],
        };
        int handle = _instances.Add(record, out int index);
        record.Index = index;
        record.SlotOffset = _slotRanges.Allocate(record.SlotCount);
        if (_slotBuffer.Count < _slotRanges.End)
            _slotBuffer.Resize(_slotRanges.End);
        if (_instanceBuffer.Count <= index)
            _instanceBuffer.Resize(index + 1);
        AssignMaterials(record, materials);
        WriteInstance(record);

        if (_liveCount == _live.Length)
            Array.Resize(ref _live, _live.Length * 2);
        record.LivePosition = _liveCount;
        _live[_liveCount++] = index;
        return new InstanceHandle(handle);
    }

    /// <inheritdoc/>
    public void UpdateInstance(InstanceHandle instance, Matrix4x4 world, InstanceFlags flags)
    {
        var record = _instances.Get(instance.Value, out _, "instance");
        record.World = world;
        record.Flags = flags;
        WriteInstance(record);
    }

    /// <inheritdoc/>
    public void SetInstanceMaterials(InstanceHandle instance, ReadOnlySpan<MaterialHandle> materials)
    {
        var record = _instances.Get(instance.Value, out _, "instance");
        AssignMaterials(record, materials);
        WriteInstance(record);
    }

    /// <inheritdoc/>
    public void DestroyInstance(InstanceHandle instance)
    {
        var record = _instances.Remove(instance.Value, out int index);
        if (record is null)
            return;
        for (int i = 0; i < record.Materials.Length; i++)
        {
            ReleaseMaterialReference(record.Materials[i]);
            record.Materials[i] = null;
        }

        _slotRanges.Free(record.SlotOffset, record.SlotCount);
        _instanceBuffer[index] = new GpuInstance { Flags = (uint)InstanceFlags.Hidden };
        _instanceBuffer.MarkDirty(index);

        int last = _live[--_liveCount];
        _live[record.LivePosition] = last;
        var moved = _instances.AtIndex(last);
        if (moved is not null)
            moved.LivePosition = record.LivePosition;
    }

    // ------------------------------------------------------------------ per-frame data

    /// <inheritdoc/>
    public void SetLights(ReadOnlySpan<SpotLight> lights)
    {
        ThrowIfDisposed();
        _lightBuffer.Resize(lights.Length);
        for (int i = 0; i < lights.Length; i++)
            LightPacker.Pack(lights[i], out _lightBuffer[i]);
        _lightBuffer.MarkDirty(0, lights.Length);
        LightCount = lights.Length;
    }

    /// <inheritdoc/>
    public void SetLines(ReadOnlySpan<LineVertex> segments)
    {
        ThrowIfDisposed();
        int n = segments.Length & ~1;
        _lineBuffer.Set(segments[..n]);
        LineSegmentCount = n / 2;
    }

    /// <inheritdoc/>
    public void SetAtmosphere(AtmosphereState atmosphere) => Atmosphere = atmosphere;

    /// <inheritdoc/>
    public void SetNoiseSources(ReadOnlySpan<NoiseSource> sources)
    {
        _noiseSources.Clear();
        foreach (var s in sources)
            _noiseSources.Add(s);
    }

    /// <summary>Destroys every GPU object of the world (the device must be idle; called by the renderer).</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        for (int i = 0; i < _meshes.HighWater; i++)
        {
            var m = _meshes.AtIndex(i);
            m?.Geometry?.Dispose();
            m?.Edges?.Dispose();
        }

        for (int i = 0; i < _userTextures.HighWater; i++)
            _userTextures.AtIndex(i)?.Image.Dispose();
        foreach (var t in _imageCache.Values)
            t.Image.Dispose();
        _imageCache.Clear();
        _materialBuffer?.Dispose();
        _instanceBuffer?.Dispose();
        _slotBuffer?.Dispose();
        _lightBuffer?.Dispose();
        _lineBuffer?.Dispose();
        _textures.Dispose();
        var vk = _context.Vk;
        foreach (var (pool, _) in _pools)
            vk.DestroyDescriptorPool(_context.Device, pool, null);
        _pools.Clear();
        if (_setLayout.Handle != 0)
            vk.DestroyDescriptorSetLayout(_context.Device, _setLayout, null);
        _setLayout = default;
    }

    /// <summary>
    /// Records every pending change into the frame's command buffer (scene buffer updates, mesh and texture uploads,
    /// mip generation) with the barriers that order them against earlier frames and make them visible to the
    /// shaders, then brings the frame slot's scene descriptor set up to date. Called by every
    /// <see cref="IRenderer.Render"/> before its passes; cheap when nothing changed.
    /// </summary>
    internal void PrepareFrame(VulkanFrame frame, BarrierBatch barriers)
    {
        ThrowIfDisposed();
        var cmd = frame.CommandBuffer;
        barriers.CommandBuffer = cmd;
        bool materials = _materialBuffer.PrepareUpload(barriers);
        bool instances = _instanceBuffer.PrepareUpload(barriers);
        bool slots = _slotBuffer.PrepareUpload(barriers);
        bool lights = _lightBuffer.PrepareUpload(barriers);
        bool lines = _lineBuffer.PrepareUpload(barriers);
        bool geometry = _pendingGeometry.Count > 0;
        barriers.Flush();

        _materialBuffer.RecordUpload(cmd);
        _instanceBuffer.RecordUpload(cmd);
        _slotBuffer.RecordUpload(cmd);
        _lightBuffer.RecordUpload(cmd);
        _lineBuffer.RecordUpload(cmd);
        if (geometry)
        {
            foreach (var (dst, src, srcOffset, size) in _pendingGeometry)
            {
                var copy = new BufferCopy(srcOffset, 0, size);
                _context.Vk.CmdCopyBuffer(cmd, src, dst.Handle, 1, &copy);
                _uploadedGeometry.Add(dst);
            }

            _pendingGeometry.Clear();
        }

        if (materials || instances || slots || lights || lines || geometry)
        {
            // One global barrier makes every copy of this frame visible to its consumers.
            BarrierBatch.Describe(BufferUse.ShaderRead, out var shaderStages, out var shaderAccess, out _);
            BarrierBatch.Describe(BufferUse.GeometryInput, out var inputStages, out var inputAccess, out _);
            barriers.AddGlobal(PipelineStageFlags2.CopyBit, AccessFlags2.TransferWriteBit, shaderStages | inputStages, shaderAccess | inputAccess);
            barriers.Flush();
            MarkUploaded(materials, _materialBuffer.Buffer, BufferUse.ShaderRead);
            MarkUploaded(instances, _instanceBuffer.Buffer, BufferUse.ShaderRead);
            MarkUploaded(slots, _slotBuffer.Buffer, BufferUse.ShaderRead);
            MarkUploaded(lights, _lightBuffer.Buffer, BufferUse.ShaderRead);
            MarkUploaded(lines, _lineBuffer.Buffer, BufferUse.VertexInput);
            foreach (var g in _uploadedGeometry)
                BarrierBatch.MarkWritten(ref g.State, PipelineStageFlags2.CopyBit, AccessFlags2.TransferWriteBit, BufferUse.GeometryInput);
            _uploadedGeometry.Clear();
        }

        _textures.RecordUploads(cmd, barriers);
        UpdateSceneSet(frame);
    }

    // ------------------------------------------------------------------ helpers

    private static void MarkUploaded(bool uploaded, GpuBuffer buffer, BufferUse use)
    {
        if (uploaded)
            BarrierBatch.MarkWritten(ref buffer.State, PipelineStageFlags2.CopyBit, AccessFlags2.TransferWriteBit, use);
    }

    private void Apply(MaterialRecord record, MaterialData material, IImageSource? images)
    {
        uint flags = 0;
        uint baseColor = Acquire(record, material.BaseColorTexture, srgb: true, images, ShaderConstants.MaterialBaseColorTexture, ShaderConstants.TextureWhite, ref flags);
        uint normal = Acquire(record, material.NormalTexture, srgb: false, images, ShaderConstants.MaterialNormalTexture, ShaderConstants.TextureFlatNormal, ref flags);
        uint mr = Acquire(record, material.MetallicRoughnessTexture, srgb: false, images, ShaderConstants.MaterialMetallicRoughnessTexture, ShaderConstants.TextureWhite, ref flags);
        uint occlusion = Acquire(record, material.OcclusionTexture, srgb: false, images, ShaderConstants.MaterialOcclusionTexture, ShaderConstants.TextureWhite, ref flags);
        uint emissive = Acquire(record, material.EmissiveTexture, srgb: true, images, ShaderConstants.MaterialEmissiveTexture, ShaderConstants.TextureWhite, ref flags);
        record.Gpu = MaterialPacker.Pack(material, new MaterialTextureSlots(baseColor, normal, mr, occlusion, emissive), flags);
        record.Kind = MaterialPacker.KindOf(record.Gpu);
        record.DoubleSided = material.DoubleSided;
        _materialBuffer[record.Index] = record.Gpu;
        _materialBuffer.MarkDirty(record.Index);
    }

    private uint Acquire(MaterialRecord record, string? key, bool srgb, IImageSource? images, uint flag, uint fallback, ref uint flags)
    {
        if (string.IsNullOrEmpty(key) || images is null)
            return fallback;
        if (!_imageCache.TryGetValue((key, srgb), out var texture))
        {
            ImageData? image;
            try
            {
                image = images.Load(key);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
            {
                image = null;
                if (_missingImages.Add(key))
                    _context.Log(VulkanLogLevel.Warning, $"Texture '{key}' could not be loaded: {e.Message}");
            }

            if (image is null)
            {
                if (_missingImages.Add(key))
                    _context.Log(VulkanLogLevel.Warning, $"Texture '{key}' not found; using the default.");
                return fallback;
            }

            texture = _textures.Create(image, srgb, key);
            texture.CacheKey = key;
            _imageCache.Add((key, srgb), texture);
        }

        texture.References++;
        record.Textures.Add(texture);
        flags |= flag;
        return texture.Slot;
    }

    private void ReleaseCached(TextureRecord texture)
    {
        if (--texture.References > 0 || texture.CacheKey is null)
            return;
        _imageCache.Remove((texture.CacheKey, texture.Srgb));
        _textures.Destroy(texture);
    }

    private void AssignMaterials(InstanceRecord record, ReadOnlySpan<MaterialHandle> materials)
    {
        for (int slot = 0; slot < record.SlotCount; slot++)
        {
            var material = slot < materials.Length ? _materials.TryGet(materials[slot].Value, out _) : null;
            var previous = record.Materials[slot];
            if (ReferenceEquals(previous, material))
                continue;
            if (material is not null)
                material.InstanceReferences++;
            ReleaseMaterialReference(previous);
            record.Materials[slot] = material;
            _slotBuffer[record.SlotOffset + slot] = material is null ? 0u : (uint)material.Index;
        }

        _slotBuffer.MarkDirty(record.SlotOffset, record.SlotCount);
    }

    private void ReleaseMaterialReference(MaterialRecord? material)
    {
        if (material is null)
            return;
        if (--material.InstanceReferences == 0 && material.Destroyed)
            _materials.FreeSlot(material.Index);
    }

    private void WriteInstance(InstanceRecord record)
    {
        var world = record.World;
        if (!Matrix4x4.Invert(world, out var inverse))
            inverse = world;
        var normal = Matrix4x4.Transpose(inverse);
        normal.M41 = normal.M42 = normal.M43 = 0f;
        normal.M14 = normal.M24 = normal.M34 = 0f;
        normal.M44 = 1f;

        var sphere = record.Mesh.BoundingSphere;
        var centre = Vector3.Transform(new Vector3(sphere.X, sphere.Y, sphere.Z), world);
        float sx = new Vector3(world.M11, world.M12, world.M13).LengthSquared();
        float sy = new Vector3(world.M21, world.M22, world.M23).LengthSquared();
        float sz = new Vector3(world.M31, world.M32, world.M33).LengthSquared();
        float scale = MathF.Sqrt(MathF.Max(sx, MathF.Max(sy, sz)));
        record.WorldSphere = new Vector4(centre, sphere.W * scale);

        _instanceBuffer[record.Index] = new GpuInstance
        {
            World = world,
            NormalMatrix = normal,
            Flags = (uint)record.Flags,
            PickId = record.PickId,
            MaterialSlotOffset = (uint)record.SlotOffset,
            MaterialSlotCount = (uint)record.SlotCount,
            BoundingSphere = record.WorldSphere,
        };
        _instanceBuffer.MarkDirty(record.Index);
    }

    private void CreateSetLayout()
    {
        const ShaderStageFlags bufferStages = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit | ShaderStageFlags.ComputeBit;
        var bindings = stackalloc DescriptorSetLayoutBinding[5];
        var flags = stackalloc DescriptorBindingFlags[5];
        for (int i = 0; i < BufferBindings.Length; i++)
        {
            bindings[i] = new DescriptorSetLayoutBinding(BufferBindings[i], DescriptorType.StorageBuffer, 1, bufferStages);
            flags[i] = 0;
        }

        bindings[4] = new DescriptorSetLayoutBinding(ShaderConstants.BindingTextures, DescriptorType.CombinedImageSampler, _textures.Capacity,
            ShaderStageFlags.FragmentBit | ShaderStageFlags.ComputeBit);
        flags[4] = DescriptorBindingFlags.PartiallyBoundBit | DescriptorBindingFlags.UpdateAfterBindBit | DescriptorBindingFlags.VariableDescriptorCountBit;
        var flagsInfo = new DescriptorSetLayoutBindingFlagsCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutBindingFlagsCreateInfo,
            BindingCount = 5,
            PBindingFlags = flags,
        };
        var info = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            PNext = &flagsInfo,
            Flags = DescriptorSetLayoutCreateFlags.UpdateAfterBindPoolBit,
            BindingCount = 5,
            PBindings = bindings,
        };
        DescriptorSetLayout layout;
        VkUtil.Check(_context.Vk.CreateDescriptorSetLayout(_context.Device, &info, null, &layout), "vkCreateDescriptorSetLayout (scene)");
        _setLayout = layout;
    }

    private (DescriptorSet Set, DescriptorPool Pool) AllocateSceneSet()
    {
        var vk = _context.Vk;
        uint count = _textures.Capacity;
        var layout = _setLayout;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            for (int i = 0; i < _pools.Count; i++)
            {
                if (_pools[i].Free == 0)
                    continue;
                var variable = new DescriptorSetVariableDescriptorCountAllocateInfo
                {
                    SType = StructureType.DescriptorSetVariableDescriptorCountAllocateInfo,
                    DescriptorSetCount = 1,
                    PDescriptorCounts = &count,
                };
                var alloc = new DescriptorSetAllocateInfo
                {
                    SType = StructureType.DescriptorSetAllocateInfo,
                    PNext = &variable,
                    DescriptorPool = _pools[i].Pool,
                    DescriptorSetCount = 1,
                    PSetLayouts = &layout,
                };
                DescriptorSet set;
                var result = vk.AllocateDescriptorSets(_context.Device, &alloc, &set);
                if (result == Result.Success)
                {
                    _pools[i] = (_pools[i].Pool, _pools[i].Free - 1);
                    return (set, _pools[i].Pool);
                }

                if (result is not (Result.ErrorOutOfPoolMemory or Result.ErrorFragmentedPool))
                    VkUtil.Check(result, "vkAllocateDescriptorSets (scene)");
                _pools[i] = (_pools[i].Pool, 0);
            }

            CreatePool();
        }

        throw new VulkanException(Result.ErrorOutOfPoolMemory, "Scene descriptor set allocation failed");
    }

    private void CreatePool()
    {
        const uint sets = VulkanContext.FramesInFlight * 2;
        var sizes = stackalloc DescriptorPoolSize[2];
        sizes[0] = new DescriptorPoolSize(DescriptorType.StorageBuffer, 4 * sets);
        sizes[1] = new DescriptorPoolSize(DescriptorType.CombinedImageSampler, _textures.Capacity * sets);
        var info = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            Flags = DescriptorPoolCreateFlags.UpdateAfterBindBit | DescriptorPoolCreateFlags.FreeDescriptorSetBit,
            MaxSets = sets,
            PoolSizeCount = 2,
            PPoolSizes = sizes,
        };
        DescriptorPool pool;
        VkUtil.Check(_context.Vk.CreateDescriptorPool(_context.Device, &info, null, &pool), "vkCreateDescriptorPool (scene)");
        _pools.Add((pool, (int)sets));
    }

    private void UpdateSceneSet(VulkanFrame frame)
    {
        var fs = _frameSets[frame.SlotIndex];
        var vk = _context.Vk;
        if (fs.UsedInFrame != frame.Number && fs.Retired.Count > 0)
        {
            // Sets retired during an earlier frame of this slot: that frame has completed.
            foreach (var (set, pool) in fs.Retired)
            {
                var s = set;
                VkUtil.Check(vk.FreeDescriptorSets(_context.Device, pool, 1, &s), "vkFreeDescriptorSets");
                for (int i = 0; i < _pools.Count; i++)
                {
                    if (_pools[i].Pool.Handle == pool.Handle)
                        _pools[i] = (pool, _pools[i].Free + 1);
                }
            }

            fs.Retired.Clear();
        }

        uint version = _materialBuffer.Version + _instanceBuffer.Version + _slotBuffer.Version + _lightBuffer.Version;
        if (fs.Set.Handle == 0 || (fs.Version != version && fs.UsedInFrame == frame.Number))
        {
            // A buffer was replaced after this slot's set was bound in the current command buffer: the bound set
            // must not change, so the rest of the frame uses a fresh one.
            if (fs.Set.Handle != 0)
                fs.Retired.Add((fs.Set, fs.Pool));
            (fs.Set, fs.Pool) = AllocateSceneSet();
            WriteBuffers(fs.Set);
            _textures.WriteAll(fs.Set, ShaderConstants.BindingTextures, frame.SlotIndex);
        }
        else
        {
            if (fs.Version != version)
                WriteBuffers(fs.Set);
            _textures.WriteDirty(fs.Set, ShaderConstants.BindingTextures, frame.SlotIndex); // update-after-bind binding
        }

        fs.Version = version;
        fs.UsedInFrame = frame.Number;
        CurrentSceneSet = fs.Set;
    }

    private void WriteBuffers(DescriptorSet set)
    {
        var infos = stackalloc DescriptorBufferInfo[4];
        infos[0] = new DescriptorBufferInfo(_materialBuffer.Buffer.Handle, 0, Vk.WholeSize);
        infos[1] = new DescriptorBufferInfo(_instanceBuffer.Buffer.Handle, 0, Vk.WholeSize);
        infos[2] = new DescriptorBufferInfo(_slotBuffer.Buffer.Handle, 0, Vk.WholeSize);
        infos[3] = new DescriptorBufferInfo(_lightBuffer.Buffer.Handle, 0, Vk.WholeSize);
        var writes = stackalloc WriteDescriptorSet[4];
        for (int i = 0; i < 4; i++)
        {
            writes[i] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = set,
                DstBinding = BufferBindings[i],
                DescriptorCount = 1,
                DescriptorType = DescriptorType.StorageBuffer,
                PBufferInfo = &infos[i],
            };
        }

        _context.Vk.UpdateDescriptorSets(_context.Device, 4, writes, 0, null);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    /// <summary>Scene descriptor set state of one frame-in-flight slot.</summary>
    private sealed class FrameSets
    {
        public DescriptorSet Set;
        public DescriptorPool Pool;
        public uint Version;
        public ulong UsedInFrame;
        public readonly List<(DescriptorSet Set, DescriptorPool Pool)> Retired = new();
    }
}
