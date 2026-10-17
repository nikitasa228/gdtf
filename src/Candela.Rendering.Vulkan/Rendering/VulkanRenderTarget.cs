using System.Numerics;
using Silk.NET.Vulkan;

namespace Candela.Rendering.Vulkan;

/// <summary>What the last frame of a target was rendered with (for <see cref="IRenderer.Pick"/> and readbacks).</summary>
internal struct LastFrameInfo
{
    public bool Valid;
    public Matrix4x4 ViewProjection;
    public Matrix4x4 InverseViewProjection;
    public Vector3 CameraPosition;
    public Vector3 CameraForward;
    public bool Orthographic;

    /// <summary>Exposure the HDR image was pre-exposed with.</summary>
    public float Exposure;

    public ViewShading Shading;
}

/// <summary>
/// A viewport/offscreen image the renderer draws into. Owns the per-view GPU resources: reverse-Z depth (D32),
/// instance pick ids (R32_UINT), the pre-exposed HDR image (RGBA16F, linear Rec.2020), the display image
/// (RGBA8 UNORM holding sRGB-encoded colour), the per-view constants and the clustered light lists, plus the view
/// descriptor set (set 1). Resizing recreates the images; the old ones are destroyed once no frame in flight uses
/// them. Main thread only.
/// </summary>
/// <remarks>
/// <para><b>Showing a target in Dear ImGui.</b> After every <see cref="IRenderer.Render"/> the display image is in
/// <see cref="DisplayImageLayout"/> (<c>SHADER_READ_ONLY_OPTIMAL</c>) and the barrier that makes it readable by
/// fragment shaders has been recorded, so the editor can sample it later in the same frame or in any later
/// submission on <see cref="VulkanContext.Queue"/>. Register it once with
/// <c>ImGui_ImplVulkan_AddTexture(target.DisplaySampler, target.DisplayImageView, target.DisplayImageLayout)</c> and
/// draw it with <c>ImGui.Image</c>. <see cref="Resize"/> replaces the image (and increments
/// <see cref="DisplayVersion"/>): register the new view and remove the old ImGui texture through
/// <see cref="VulkanContext.DeferDestroy"/> so it is freed only after the frames that still sample it. The image
/// holds sRGB-encoded values in a UNORM format, which is what ImGui's shaders pass straight to a UNORM swapchain.
/// </para>
/// </remarks>
public sealed unsafe class VulkanRenderTarget : IRenderTarget
{
    private readonly VulkanRenderer _renderer;
    private readonly VulkanContext _context;
    private DescriptorPool _viewPool;
    private bool _disposed;

    internal VulkanRenderTarget(VulkanRenderer renderer, int width, int height)
    {
        _renderer = renderer;
        _context = renderer.Context;
        (Width, Height) = Clamp(width, height);
        try
        {
            FrameUniform = GpuBuffer.Create(_context, (ulong)sizeof(GpuFrameData), BufferUsageFlags.UniformBufferBit | BufferUsageFlags.TransferDstBit,
                MemoryUsage.GpuOnly, "view constants");
            Clusters = GpuBuffer.Create(_context, ShaderConstants.ClusterCount * 8ul, BufferUsageFlags.StorageBufferBit, MemoryUsage.GpuOnly,
                "clusters");
            ClusterCounter = GpuBuffer.Create(_context, (ulong)sizeof(GpuClusterCounter), BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
                MemoryUsage.GpuOnly, "cluster counter");
            ClusterIndexCapacity = ShaderConstants.ClusterCount * 16;
            ClusterLights = CreateClusterLights(ClusterIndexCapacity);
            CreateImages();
            CreateViewSet();
        }
        catch
        {
            DisposeResources();
            throw;
        }
    }

    /// <inheritdoc/>
    public int Width { get; private set; }

    /// <inheritdoc/>
    public int Height { get; private set; }

    /// <summary>The display image (RGBA8 UNORM, sRGB-encoded colour, top row first).</summary>
    public Image DisplayImage => Display.Handle;

    /// <summary>View of the display image for <c>ImGui_ImplVulkan_AddTexture</c>.</summary>
    public ImageView DisplayImageView => Display.View;

    /// <summary>Linear, clamp-to-edge sampler for the display image (shared by all targets of the renderer).</summary>
    public Sampler DisplaySampler => _renderer.DisplaySampler;

    /// <summary>Layout of the display image after every <see cref="IRenderer.Render"/>.</summary>
    public ImageLayout DisplayImageLayout => ImageLayout.ShaderReadOnlyOptimal;

    /// <summary>Format of the display image.</summary>
    public Format DisplayFormat => TargetFormats.Display;

    /// <summary>Increments whenever the display image is recreated (re-register it with ImGui).</summary>
    public int DisplayVersion { get; private set; }

    /// <summary>True once a frame has been rendered into the target since it was created or resized.</summary>
    public bool HasFrame => Last.Valid;

    internal bool IsDisposed => _disposed;

    internal VulkanRenderer Owner => _renderer;

    internal GpuImage Depth { get; private set; } = null!;

    internal GpuImage PickIds { get; private set; } = null!;

    internal GpuImage Hdr { get; private set; } = null!;

    internal GpuImage Display { get; private set; } = null!;

    internal GpuBuffer FrameUniform { get; } = null!;

    internal GpuBuffer Clusters { get; } = null!;

    internal GpuBuffer ClusterLights { get; private set; } = null!;

    internal GpuBuffer ClusterCounter { get; } = null!;

    /// <summary>Entries in <see cref="ClusterLights"/>.</summary>
    internal uint ClusterIndexCapacity { get; private set; }

    internal DescriptorSet ViewSet { get; private set; }

    internal LastFrameInfo Last;

    /// <inheritdoc/>
    public void Resize(int width, int height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var (w, h) = Clamp(width, height);
        if (w == Width && h == Height)
            return;
        Width = w;
        Height = h;
        RetireImages();
        RetireViewSet();
        CreateImages();
        CreateViewSet();
        Last.Valid = false;
        DisplayVersion++;
    }

    /// <summary>
    /// Makes the cluster index buffer large enough for <paramref name="lightCount"/> lights: every cluster can hold
    /// min(lights, MAX_LIGHTS_PER_CLUSTER) entries, so the light lists never overflow the buffer (at most
    /// 3456 × 512 × 4 bytes ≈ 7 MB per view). Replacing the buffer replaces the view set.
    /// </summary>
    internal void EnsureClusterCapacity(int lightCount)
    {
        uint perCluster = (uint)Math.Clamp(lightCount, 16, (int)ShaderConstants.MaxLightsPerCluster);
        uint needed = ShaderConstants.ClusterCount * perCluster;
        if (needed <= ClusterIndexCapacity)
            return;
        uint capacity = Math.Min(Math.Max(needed, ClusterIndexCapacity * 2), ShaderConstants.ClusterCount * ShaderConstants.MaxLightsPerCluster);
        _context.DeferDispose(ClusterLights);
        ClusterLights = CreateClusterLights(capacity);
        ClusterIndexCapacity = capacity;
        RetireViewSet();
        CreateViewSet();
    }

    /// <summary>Destroys the target's GPU resources after the frames that may still use them.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _renderer.OnTargetDisposed(this);
        if (_context.IsDisposed)
            return;
        RetireImages();
        RetireViewSet();
        _context.DeferDispose(FrameUniform);
        _context.DeferDispose(Clusters);
        _context.DeferDispose(ClusterLights);
        _context.DeferDispose(ClusterCounter);
    }

    /// <summary>Destroys everything immediately (renderer shutdown, device idle).</summary>
    internal void DisposeNow()
    {
        _disposed = true;
        DisposeResources();
    }

    private (int Width, int Height) Clamp(int width, int height)
    {
        int max = (int)Math.Min(_context.Capabilities.MaxImageDimension2D, 16384u);
        return (Math.Clamp(width, 1, max), Math.Clamp(height, 1, max));
    }

    private GpuBuffer CreateClusterLights(uint capacity) =>
        GpuBuffer.Create(_context, capacity * 4ul, BufferUsageFlags.StorageBufferBit, MemoryUsage.GpuOnly, "cluster light lists");

    private void CreateImages()
    {
        uint w = (uint)Width, h = (uint)Height;
        Depth = GpuImage.Create2D(_context, w, h, TargetFormats.Depth,
            ImageUsageFlags.DepthStencilAttachmentBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit, "depth", dedicated: true);
        PickIds = GpuImage.Create2D(_context, w, h, TargetFormats.PickId,
            ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit, "pick ids", dedicated: true);
        Hdr = GpuImage.Create2D(_context, w, h, TargetFormats.Hdr,
            ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit, "hdr", dedicated: true);
        Display = GpuImage.Create2D(_context, w, h, TargetFormats.Display,
            ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit, "display", dedicated: true);
    }

    private void RetireImages()
    {
        if (Depth is not null)
            _context.DeferDispose(Depth);
        if (PickIds is not null)
            _context.DeferDispose(PickIds);
        if (Hdr is not null)
            _context.DeferDispose(Hdr);
        if (Display is not null)
            _context.DeferDispose(Display);
        Depth = PickIds = Hdr = Display = null!;
    }

    private void CreateViewSet()
    {
        var vk = _context.Vk;
        var sizes = stackalloc DescriptorPoolSize[3];
        sizes[0] = new DescriptorPoolSize(DescriptorType.UniformBuffer, 1);
        sizes[1] = new DescriptorPoolSize(DescriptorType.StorageBuffer, 3);
        sizes[2] = new DescriptorPoolSize(DescriptorType.CombinedImageSampler, 1);
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = 1,
            PoolSizeCount = 3,
            PPoolSizes = sizes,
        };
        DescriptorPool pool;
        VkUtil.Check(vk.CreateDescriptorPool(_context.Device, &poolInfo, null, &pool), "vkCreateDescriptorPool (view)");
        _viewPool = pool;
        var layout = _renderer.Pipelines.ViewSetLayout;
        var alloc = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = pool,
            DescriptorSetCount = 1,
            PSetLayouts = &layout,
        };
        DescriptorSet set;
        VkUtil.Check(vk.AllocateDescriptorSets(_context.Device, &alloc, &set), "vkAllocateDescriptorSets (view)");
        ViewSet = set;

        var buffers = stackalloc DescriptorBufferInfo[4];
        buffers[0] = new DescriptorBufferInfo(FrameUniform.Handle, 0, FrameUniform.Size);
        buffers[1] = new DescriptorBufferInfo(Clusters.Handle, 0, Vk.WholeSize);
        buffers[2] = new DescriptorBufferInfo(ClusterLights.Handle, 0, Vk.WholeSize);
        buffers[3] = new DescriptorBufferInfo(ClusterCounter.Handle, 0, Vk.WholeSize);
        var image = new DescriptorImageInfo(_renderer.HdrSampler, Hdr.View, ImageLayout.ShaderReadOnlyOptimal);
        var writes = stackalloc WriteDescriptorSet[5];
        writes[0] = Write(set, ShaderConstants.BindingFrame, DescriptorType.UniformBuffer, &buffers[0]);
        writes[1] = Write(set, ShaderConstants.BindingClusters, DescriptorType.StorageBuffer, &buffers[1]);
        writes[2] = Write(set, ShaderConstants.BindingClusterLights, DescriptorType.StorageBuffer, &buffers[2]);
        writes[3] = Write(set, ShaderConstants.BindingClusterCounter, DescriptorType.StorageBuffer, &buffers[3]);
        writes[4] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = set,
            DstBinding = ShaderConstants.BindingHdr,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.CombinedImageSampler,
            PImageInfo = &image,
        };
        vk.UpdateDescriptorSets(_context.Device, 5, writes, 0, null);
    }

    private static WriteDescriptorSet Write(DescriptorSet set, uint binding, DescriptorType type, DescriptorBufferInfo* info) => new()
    {
        SType = StructureType.WriteDescriptorSet,
        DstSet = set,
        DstBinding = binding,
        DescriptorCount = 1,
        DescriptorType = type,
        PBufferInfo = info,
    };

    private void RetireViewSet()
    {
        if (_viewPool.Handle == 0)
            return;
        var pool = _viewPool;
        var context = _context;
        _context.DeferDestroy(() => context.Vk.DestroyDescriptorPool(context.Device, pool, null));
        _viewPool = default;
        ViewSet = default;
    }

    private void DisposeResources()
    {
        Depth?.Dispose();
        PickIds?.Dispose();
        Hdr?.Dispose();
        Display?.Dispose();
        FrameUniform?.Dispose();
        Clusters?.Dispose();
        ClusterLights?.Dispose();
        ClusterCounter?.Dispose();
        if (_viewPool.Handle != 0)
            _context.Vk.DestroyDescriptorPool(_context.Device, _viewPool, null);
        _viewPool = default;
    }
}
