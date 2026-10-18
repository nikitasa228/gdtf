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

    /// <summary>Overlay of the frame (the analysis image holds lux or dB).</summary>
    public OverlayMode Overlay;

    /// <summary>Whether the frame computed the bloom pyramid.</summary>
    public bool Bloom;
}

/// <summary>
/// A viewport/offscreen image the renderer draws into. Owns the per-view GPU resources: the scene images at the
/// internal resolution (<see cref="PreviewSettings.ResolutionScale"/> × size) — reverse-Z depth (D32), instance pick
/// ids and instance indices (R32_UINT), octahedral normals (RG16F), the pre-exposed HDR image (RGBA16F, linear
/// Rec.2020) — the display image (RGBA8 UNORM holding sRGB-encoded colour), the per-view constants and clustered
/// light lists, and, while the features are in use, the froxel volumes, indirect-light images, virtual point lights,
/// bloom pyramid, analysis image and gradient table, selection mask, overlay depth and auto-exposure buffers. The
/// view descriptor set (set 1) and the compute pass sets (set 2) are rebuilt whenever a resource they reference is
/// replaced. Resizing recreates the images; old resources are destroyed once no frame in flight uses them. Main
/// thread only.
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
    private const int MaxPassSets = 24;

    /// <summary>Renders a feature's resources survive without being used.</summary>
    internal const int KeepUnusedRenders = 60;

    private const int SlotDisplayDepth = 0;
    private const int SlotAnalysis = 1;
    private const int SlotGradient = 2;
    private const int SlotSelection = 3;
    private const int SlotInjected = 4;
    private const int SlotHistory = 5;
    private const int SlotIntegrated = 6;
    private const int SlotIndirect = 7;
    private const int SlotIndirectHistory = 8;
    private const int SlotVpls = 9;
    private const int SlotBloom = 10;
    private const int SlotExposure = 11;
    private const int SlotIndirectScratch = 12;
    private readonly int[] _unusedRenders = new int[13];
    private readonly VulkanRenderer _renderer;
    private readonly VulkanContext _context;
    private readonly DescriptorSignature _signature = new();
    private readonly DescriptorWriter _writer = new();
    private readonly GpuImage?[] _bloom = new GpuImage?[8];
    private readonly DescriptorSet[] _bloomDownSets = new DescriptorSet[8];
    private readonly DescriptorSet[] _bloomUpSets = new DescriptorSet[8];
    private DescriptorPool _pool;
    private bool _disposed;

    internal VulkanRenderTarget(VulkanRenderer renderer, int width, int height)
    {
        _renderer = renderer;
        _context = renderer.Context;
        (Width, Height) = Clamp(width, height);
        RenderWidth = Width;
        RenderHeight = Height;
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
            LightViews = new SceneBuffer<GpuLightView>(_context, BufferUsageFlags.StorageBufferBit, "light views", 64);
            Lists = new SceneBuffer<uint>(_context, BufferUsageFlags.StorageBufferBit, "view light lists", 64);
            BeamLines = new SceneBuffer<LineVertex>(_context, BufferUsageFlags.VertexBufferBit, "beam outlines", 64);
            CreateSceneImages();
            CreateDisplayImage();
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

    /// <summary>Width of the scene images (display width × resolution scale).</summary>
    public int RenderWidth { get; private set; }

    /// <summary>Height of the scene images (display height × resolution scale).</summary>
    public int RenderHeight { get; private set; }

    internal bool IsDisposed => _disposed;

    internal VulkanRenderer Owner => _renderer;

    internal GpuImage Depth { get; private set; } = null!;

    internal GpuImage PickIds { get; private set; } = null!;

    internal GpuImage InstanceIds { get; private set; } = null!;

    internal GpuImage Normals { get; private set; } = null!;

    internal GpuImage Hdr { get; private set; } = null!;

    internal GpuImage Display { get; private set; } = null!;

    /// <summary>Scene depth upsampled to display resolution for the overlays (only while scaled).</summary>
    internal GpuImage? DisplayDepth { get; private set; }

    /// <summary>Per-pixel lux or dB of the analysis overlays (internal resolution).</summary>
    internal GpuImage? Analysis { get; private set; }

    /// <summary>Overlay gradient table (256 × 1, sRGB values + alpha).</summary>
    internal GpuImage? GradientLut { get; private set; }

    /// <summary>Hash of the gradient the table holds.</summary>
    internal int GradientHash { get; set; }

    /// <summary>Baked lux (0) and noise (1) gradients with their hashes, so switching overlays does not re-bake.</summary>
    internal Vector4[]?[] GradientBakes { get; } = new Vector4[]?[2];

    /// <summary>Hashes of <see cref="GradientBakes"/>.</summary>
    internal int[] GradientBakeHashes { get; } = new int[2];

    internal GpuImage? SelectionMask { get; private set; }

    internal GpuImage? FroxelInjected { get; private set; }

    internal GpuImage? FroxelHistory { get; private set; }

    internal GpuImage? FroxelIntegrated { get; private set; }

    internal GpuImage? Indirect { get; private set; }

    internal GpuImage? IndirectHistory { get; private set; }

    /// <summary>Ping-pong partner of <see cref="Indirect"/> for the ray-traced GI filter.</summary>
    internal GpuImage? IndirectScratch { get; private set; }

    internal GpuBuffer? Vpls { get; private set; }

    internal GpuBuffer? Exposure { get; private set; }

    internal GpuBuffer?[] ExposureReadback { get; } = new GpuBuffer?[VulkanContext.FramesInFlight];

    /// <summary>Frame number that wrote each exposure read-back buffer (0 = none).</summary>
    internal ulong[] ExposureReadbackFrame { get; } = new ulong[VulkanContext.FramesInFlight];

    internal int BloomLevels { get; private set; }

    internal GpuImage BloomLevel(int i) => _bloom[i]!;

    internal DescriptorSet BloomDownSet(int i) => _bloomDownSets[i];

    internal DescriptorSet BloomUpSet(int i) => _bloomUpSets[i];

    internal GpuBuffer FrameUniform { get; } = null!;

    internal GpuBuffer Clusters { get; } = null!;

    internal GpuBuffer ClusterLights { get; private set; } = null!;

    internal GpuBuffer ClusterCounter { get; } = null!;

    /// <summary>Per-light view data (shadow tile, ray-marched flag, VPL range).</summary>
    internal SceneBuffer<GpuLightView> LightViews { get; } = null!;

    /// <summary>Ray-marched beams followed by the lights with reflective shadow maps / GI lights.</summary>
    internal SceneBuffer<uint> Lists { get; } = null!;

    /// <summary>Beam outline line segments of the current frame.</summary>
    internal SceneBuffer<LineVertex> BeamLines { get; } = null!;

    /// <summary>Entries in <see cref="ClusterLights"/>.</summary>
    internal uint ClusterIndexCapacity { get; private set; }

    internal DescriptorSet ViewSet { get; private set; }

    internal DescriptorSet InjectSet { get; private set; }

    internal DescriptorSet IntegrateSet { get; private set; }

    internal DescriptorSet GatherSet { get; private set; }

    internal DescriptorSet RayTracingSet { get; private set; }

    /// <summary>Filter iteration sets: <see cref="Indirect"/> to <see cref="IndirectScratch"/> and back.</summary>
    internal DescriptorSet FilterToScratchSet { get; private set; }

    internal DescriptorSet FilterToIndirectSet { get; private set; }

    internal LastFrameInfo Last;

    // Temporal state.
    internal uint FrameIndex;
    internal Matrix4x4 PreviousViewProjection;
    internal bool HasPreviousFrame;
    internal bool VolumeHistoryValid;
    internal Vector4 VolumeHistoryKey;
    internal float VolumeHistoryExposure;
    internal bool IndirectHistoryValid;
    internal IndirectLighting IndirectHistoryMode;
    internal float AdaptedLog2Luminance = float.NaN;

    /// <inheritdoc/>
    public void Resize(int width, int height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var (w, h) = Clamp(width, height);
        if (w == Width && h == Height)
            return;
        Width = w;
        Height = h;
        RetireImage(Display);
        Display = null!;
        CreateDisplayImage();
        if (DisplayDepth is not null || SelectionMask is not null)
        {
            RetireImage(DisplayDepth);
            RetireImage(SelectionMask);
            DisplayDepth = SelectionMask = null;
        }

        Last.Valid = false;
        DisplayVersion++;
        InvalidateHistory();
    }

    /// <summary>
    /// Makes the cluster index buffer large enough for <paramref name="lightCount"/> lights: every cluster can hold
    /// min(lights, MAX_LIGHTS_PER_CLUSTER) entries, so the light lists never overflow the buffer (at most
    /// 3456 × 512 × 4 bytes ≈ 7 MB per view). Replacing the buffer rebuilds the view set.
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
    }

    /// <summary>Creates, resizes or releases the per-view resources for this frame's requirements.</summary>
    internal void Prepare(in ViewRequirements r)
    {
        int rw = Math.Clamp(r.RenderWidth, 1, Width), rh = Math.Clamp(r.RenderHeight, 1, Height);
        if (rw != RenderWidth || rh != RenderHeight)
        {
            RenderWidth = rw;
            RenderHeight = rh;
            RetireSceneImages();
            CreateSceneImages();
            Last.Valid = false;
            InvalidateHistory();
            RetireImage(Analysis);
            Analysis = null;
            RetireImage(Indirect);
            RetireImage(IndirectHistory);
            RetireImage(IndirectScratch);
            Indirect = IndirectHistory = IndirectScratch = null;
            for (int i = 0; i < BloomLevels; i++)
                RetireImage(_bloom[i]);
            BloomLevels = 0;
        }

        uint dw = (uint)Width, dh = (uint)Height;
        Ensure(SlotDisplayDepth, DisplayDepth, r.Scaled, dw, dh, 1, TargetFormats.Depth, ImageUsageFlags.DepthStencilAttachmentBit, "overlay depth",
            out var displayDepth);
        DisplayDepth = displayDepth;
        Ensure(SlotAnalysis, Analysis, r.Analysis, (uint)RenderWidth, (uint)RenderHeight, 1, TargetFormats.Analysis,
            ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit, "analysis", out var analysis);
        Analysis = analysis;
        Ensure(SlotGradient, GradientLut, r.Analysis || r.VolumeX > 0, 256, 1, 1, TargetFormats.HdrStorage,
            ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit, "overlay gradient", out var lut);
        if (!ReferenceEquals(lut, GradientLut))
            GradientHash = 0;
        GradientLut = lut;
        Ensure(SlotSelection, SelectionMask, r.Selection, dw, dh, 1, TargetFormats.SelectionMask,
            ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit, "selection mask", out var selection);
        SelectionMask = selection;

        bool volume = r.VolumeX > 0;
        var volumeUsage = ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit;
        Ensure(SlotInjected, FroxelInjected, volume, (uint)r.VolumeX, (uint)r.VolumeY, (uint)r.VolumeZ, TargetFormats.HdrStorage,
            volumeUsage | ImageUsageFlags.TransferSrcBit, "froxels (injected)", out var injected, is3D: true);
        if (!ReferenceEquals(injected, FroxelInjected))
            VolumeHistoryValid = false;
        FroxelInjected = injected;
        Ensure(SlotHistory, FroxelHistory, volume, (uint)r.VolumeX, (uint)r.VolumeY, (uint)r.VolumeZ, TargetFormats.HdrStorage,
            ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit, "froxels (history)", out var history, is3D: true);
        FroxelHistory = history;
        Ensure(SlotIntegrated, FroxelIntegrated, volume, (uint)r.VolumeX, (uint)r.VolumeY, (uint)r.VolumeZ, TargetFormats.HdrStorage, volumeUsage,
            "froxels (integrated)", out var integrated, is3D: true);
        FroxelIntegrated = integrated;
        if (!volume)
            VolumeHistoryValid = false;

        bool indirect = r.IndirectWidth > 0;
        Ensure(SlotIndirect, Indirect, indirect, (uint)Math.Max(r.IndirectWidth, 1), (uint)Math.Max(r.IndirectHeight, 1), 1, TargetFormats.HdrStorage,
            ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit, "indirect irradiance", out var indirectImage);
        if (!ReferenceEquals(indirectImage, Indirect))
            IndirectHistoryValid = false;
        Indirect = indirectImage;
        Ensure(SlotIndirectHistory, IndirectHistory, indirect && r.IndirectHistory, (uint)Math.Max(r.IndirectWidth, 1),
            (uint)Math.Max(r.IndirectHeight, 1), 1, TargetFormats.HdrStorage, ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit,
            "indirect history", out var indirectHistory);
        if (!ReferenceEquals(indirectHistory, IndirectHistory))
            IndirectHistoryValid = false;
        IndirectHistory = indirectHistory;
        Ensure(SlotIndirectScratch, IndirectScratch, indirect && r.IndirectHistory, (uint)Math.Max(r.IndirectWidth, 1),
            (uint)Math.Max(r.IndirectHeight, 1), 1, TargetFormats.HdrStorage, ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit,
            "indirect filter scratch", out var indirectScratch);
        IndirectScratch = indirectScratch;

        ulong vplBytes = (ulong)Math.Max(r.VplCapacity, 0) * (ulong)sizeof(GpuVpl);
        if (r.VplCapacity <= 0)
        {
            if (Vpls is not null && Unused(SlotVpls))
            {
                _context.DeferDispose(Vpls);
                Vpls = null;
            }
        }
        else if (Used(SlotVpls) && (Vpls is null || Vpls.Size < vplBytes))
        {
            if (Vpls is not null)
                _context.DeferDispose(Vpls);
            Vpls = GpuBuffer.Create(_context, vplBytes, BufferUsageFlags.StorageBufferBit, MemoryUsage.GpuOnly, "virtual point lights");
        }

        if (r.BloomLevels > 0 ? Used(SlotBloom) : Unused(SlotBloom))
            EnsureBloom(r.BloomLevels);
        if (r.AutoExposure ? Used(SlotExposure) : Unused(SlotExposure))
            EnsureExposure(r.AutoExposure);
    }

    /// <summary>
    /// Brings the view set and the pass sets up to date with the resources they reference (allocates fresh sets in a
    /// new pool when anything was replaced; the old pool is destroyed after the frames that may still use it).
    /// </summary>
    internal void UpdateDescriptors(RendererResources shared, VulkanRenderWorld world, ShadowAtlas? atlas, AccelerationStructureKHR tlas,
        GpuBuffer? rtData)
    {
        var atmosphere = world.AtmosphereGpu;
        var sig = _signature;
        sig.Begin();
        sig.Add(FrameUniform.Handle.Handle);
        sig.Add(Clusters.Handle.Handle);
        sig.Add(ClusterLights.Handle.Handle);
        sig.Add(ClusterCounter.Handle.Handle);
        sig.Add(Hdr.View.Handle);
        sig.Add(LightViews.Buffer.Handle.Handle);
        sig.Add(atlas?.Slots.Buffer.Handle.Handle ?? 0);
        sig.Add(atlas?.Depth?.View.Handle ?? 0);
        sig.Add(atlas?.RsmAlbedo?.View.Handle ?? 0);
        sig.Add(atlas?.RsmNormal?.View.Handle ?? 0);
        sig.Add(Vpls?.Handle.Handle ?? 0);
        sig.Add(Indirect?.View.Handle ?? 0);
        sig.Add(IndirectHistory?.View.Handle ?? 0);
        sig.Add(IndirectScratch?.View.Handle ?? 0);
        sig.Add(FroxelInjected?.View.Handle ?? 0);
        sig.Add(FroxelHistory?.View.Handle ?? 0);
        sig.Add(FroxelIntegrated?.View.Handle ?? 0);
        sig.Add(atmosphere.Smoke.Present ? atmosphere.Smoke.Image!.View.Handle : 0);
        sig.Add(atmosphere.Flame.Present ? atmosphere.Flame.Image!.View.Handle : 0);
        sig.Add(atmosphere.Noise.Present ? atmosphere.Noise.Image!.View.Handle : 0);
        sig.Add(atmosphere.FlameLut.Handle.Handle);
        sig.Add(atmosphere.NoiseSources.Buffer.Handle.Handle);
        sig.Add(GradientLut?.View.Handle ?? 0);
        sig.Add(Depth.View.Handle);
        sig.Add(Normals.View.Handle);
        sig.Add(InstanceIds.View.Handle);
        sig.Add(Analysis?.View.Handle ?? 0);
        sig.Add(Lists.Buffer.Handle.Handle);
        sig.Add(SelectionMask?.View.Handle ?? 0);
        sig.Add(Exposure?.Handle.Handle ?? 0);
        sig.Add(tlas.Handle);
        sig.Add(rtData?.Handle.Handle ?? 0);
        sig.Add((ulong)BloomLevels);
        for (int i = 0; i < BloomLevels; i++)
            sig.Add(_bloom[i]!.View.Handle);
        if (!sig.Changed() && _pool.Handle != 0)
            return;

        RetirePool();
        CreatePool();
        var vk = _context.Vk;
        var w = _writer;
        var set = ViewSet = Allocate(_renderer.Pipelines.ViewSetLayout);
        const ImageLayout read = ImageLayout.ShaderReadOnlyOptimal;
        const ImageLayout depthRead = ImageLayout.DepthStencilReadOnlyOptimal;
        var storage = DescriptorType.StorageBuffer;
        var image = DescriptorType.CombinedImageSampler;
        w.Buffer(set, ShaderConstants.BindingFrame, DescriptorType.UniformBuffer, FrameUniform.Handle, 0, FrameUniform.Size);
        w.Buffer(set, ShaderConstants.BindingClusters, storage, Clusters.Handle);
        w.Buffer(set, ShaderConstants.BindingClusterLights, storage, ClusterLights.Handle);
        w.Buffer(set, ShaderConstants.BindingClusterCounter, storage, ClusterCounter.Handle);
        w.Image(set, ShaderConstants.BindingHdr, image, shared.LinearClamp, Hdr.View, read);
        w.Buffer(set, ShaderConstants.BindingLightViews, storage, LightViews.Buffer.Handle);
        w.Buffer(set, ShaderConstants.BindingShadowSlots, storage, atlas?.Slots.Buffer.Handle ?? shared.DummyBuffer.Handle);
        var atlasDepth = atlas?.Depth ?? shared.DummyDepth;
        w.Image(set, ShaderConstants.BindingShadowMap, image, shared.ShadowCompare, atlasDepth.View, depthRead);
        w.Image(set, ShaderConstants.BindingShadowDepth, image, shared.NearestClamp, atlasDepth.View, depthRead);
        w.Image(set, ShaderConstants.BindingRsmAlbedo, image, shared.NearestClamp, (atlas?.RsmAlbedo ?? shared.Dummy2D).View, read);
        w.Image(set, ShaderConstants.BindingRsmNormal, image, shared.NearestClamp, (atlas?.RsmNormal ?? shared.Dummy2D).View, read);
        w.Buffer(set, ShaderConstants.BindingVpls, storage, (Vpls ?? shared.DummyBuffer).Handle);
        w.Image(set, ShaderConstants.BindingIndirect, image, shared.LinearClamp, (Indirect ?? shared.Dummy2D).View, read);
        w.Image(set, ShaderConstants.BindingVolume, image, shared.LinearClamp, (FroxelIntegrated ?? shared.Dummy3D).View, read);
        w.Image(set, ShaderConstants.BindingSmoke, image, shared.LinearClamp,
            (atmosphere.Smoke.Present ? atmosphere.Smoke.Image! : shared.Dummy3D).View, read);
        w.Image(set, ShaderConstants.BindingFlame, image, shared.LinearClamp,
            (atmosphere.Flame.Present ? atmosphere.Flame.Image! : shared.Dummy3D).View, read);
        w.Image(set, ShaderConstants.BindingNoiseField, image, shared.LinearClamp,
            (atmosphere.Noise.Present ? atmosphere.Noise.Image! : shared.Dummy3D).View, read);
        w.Buffer(set, ShaderConstants.BindingFlameLut, storage, atmosphere.FlameLut.Handle);
        w.Buffer(set, ShaderConstants.BindingNoiseSources, storage, atmosphere.NoiseSources.Buffer.Handle);
        w.Image(set, ShaderConstants.BindingGradientLut, image, shared.LinearClamp, (GradientLut ?? shared.Dummy2D).View, read);
        w.Image(set, ShaderConstants.BindingSceneDepth, image, shared.NearestClamp, Depth.View, depthRead);
        w.Image(set, ShaderConstants.BindingSceneNormals, image, shared.NearestClamp, Normals.View, read);
        w.Image(set, ShaderConstants.BindingInstanceIds, image, shared.NearestClamp, InstanceIds.View, read);
        w.Image(set, ShaderConstants.BindingBloom, image, shared.LinearClamp, (BloomLevels > 0 ? _bloom[0]! : shared.Dummy2D).View, read);
        w.Image(set, ShaderConstants.BindingAnalysis, image, shared.NearestClamp, (Analysis ?? shared.Dummy2D).View, read);
        w.Buffer(set, ShaderConstants.BindingRaymarchList, storage, Lists.Buffer.Handle);
        w.Image(set, ShaderConstants.BindingSelectionMask, image, shared.NearestClamp, (SelectionMask ?? shared.Dummy2D).View, read);
        w.Buffer(set, ShaderConstants.BindingExposure, storage, (Exposure ?? shared.DummyBuffer).Handle);

        InjectSet = IntegrateSet = GatherSet = RayTracingSet = FilterToScratchSet = FilterToIndirectSet = default;
        if (FroxelInjected is not null)
        {
            InjectSet = Allocate(_renderer.Pipelines.PassSetLayout);
            w.Image(InjectSet, ShaderConstants.BindingPassStorage0, DescriptorType.StorageImage, default, FroxelInjected.View, ImageLayout.General);
            w.Image(InjectSet, ShaderConstants.BindingPassSampled0, image, shared.LinearClamp, FroxelHistory!.View, read);
            IntegrateSet = Allocate(_renderer.Pipelines.PassSetLayout);
            w.Image(IntegrateSet, ShaderConstants.BindingPassStorage0, DescriptorType.StorageImage, default, FroxelIntegrated!.View, ImageLayout.General);
            w.Image(IntegrateSet, ShaderConstants.BindingPassSampled0, image, shared.NearestClamp, FroxelInjected.View, read);
        }

        if (Indirect is not null)
        {
            GatherSet = Allocate(_renderer.Pipelines.PassSetLayout);
            w.Image(GatherSet, ShaderConstants.BindingPassStorage0, DescriptorType.StorageImage, default, Indirect.View, ImageLayout.General);
            if (IndirectHistory is not null && tlas.Handle != 0 && rtData is not null)
            {
                RayTracingSet = Allocate(_renderer.Pipelines.PassSetLayout);
                w.Image(RayTracingSet, ShaderConstants.BindingPassStorage0, DescriptorType.StorageImage, default, Indirect.View, ImageLayout.General);
                w.Image(RayTracingSet, ShaderConstants.BindingPassSampled0, image, shared.LinearClamp, IndirectHistory.View, read);
                w.Buffer(RayTracingSet, ShaderConstants.BindingPassBuffer, storage, rtData.Handle);
                w.AccelerationStructure(RayTracingSet, ShaderConstants.BindingPassTlas, tlas);
                if (IndirectScratch is not null)
                {
                    FilterToScratchSet = Allocate(_renderer.Pipelines.PassSetLayout);
                    w.Image(FilterToScratchSet, ShaderConstants.BindingPassStorage0, DescriptorType.StorageImage, default, IndirectScratch.View,
                        ImageLayout.General);
                    w.Image(FilterToScratchSet, ShaderConstants.BindingPassSampled0, image, shared.NearestClamp, Indirect.View, read);
                    FilterToIndirectSet = Allocate(_renderer.Pipelines.PassSetLayout);
                    w.Image(FilterToIndirectSet, ShaderConstants.BindingPassStorage0, DescriptorType.StorageImage, default, Indirect.View,
                        ImageLayout.General);
                    w.Image(FilterToIndirectSet, ShaderConstants.BindingPassSampled0, image, shared.NearestClamp, IndirectScratch.View, read);
                }
            }
        }

        for (int i = 0; i < BloomLevels; i++)
        {
            _bloomDownSets[i] = Allocate(_renderer.Pipelines.PassSetLayout);
            w.Image(_bloomDownSets[i], ShaderConstants.BindingPassStorage0, DescriptorType.StorageImage, default, _bloom[i]!.View, ImageLayout.General);
            if (i > 0)
                w.Image(_bloomDownSets[i], ShaderConstants.BindingPassSampled0, image, shared.LinearClamp, _bloom[i - 1]!.View, read);
            if (i + 1 < BloomLevels)
            {
                _bloomUpSets[i] = Allocate(_renderer.Pipelines.PassSetLayout);
                w.Image(_bloomUpSets[i], ShaderConstants.BindingPassStorage0, DescriptorType.StorageImage, default, _bloom[i]!.View, ImageLayout.General);
                w.Image(_bloomUpSets[i], ShaderConstants.BindingPassSampled0, image, shared.LinearClamp, _bloom[i + 1]!.View, read);
            }
        }

        w.Flush(_context);
        sig.Commit();
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
        RetireSceneImages();
        RetireImage(Display);
        RetireImage(DisplayDepth);
        RetireImage(Analysis);
        RetireImage(GradientLut);
        RetireImage(SelectionMask);
        RetireImage(FroxelInjected);
        RetireImage(FroxelHistory);
        RetireImage(FroxelIntegrated);
        RetireImage(Indirect);
        RetireImage(IndirectHistory);
        RetireImage(IndirectScratch);
        for (int i = 0; i < BloomLevels; i++)
            RetireImage(_bloom[i]);
        BloomLevels = 0;
        RetirePool();
        _context.DeferDispose(FrameUniform);
        _context.DeferDispose(Clusters);
        _context.DeferDispose(ClusterLights);
        _context.DeferDispose(ClusterCounter);
        _context.DeferDispose(LightViews);
        _context.DeferDispose(Lists);
        _context.DeferDispose(BeamLines);
        if (Vpls is not null)
            _context.DeferDispose(Vpls);
        if (Exposure is not null)
            _context.DeferDispose(Exposure);
        foreach (var b in ExposureReadback)
        {
            if (b is not null)
                _context.DeferDispose(b);
        }
    }

    /// <summary>Destroys everything immediately (renderer shutdown, device idle).</summary>
    internal void DisposeNow()
    {
        _disposed = true;
        DisposeResources();
    }

    /// <summary>Forgets the temporal history (camera cut, resize, new resources).</summary>
    internal void InvalidateHistory()
    {
        HasPreviousFrame = false;
        VolumeHistoryValid = false;
        IndirectHistoryValid = false;
    }

    private (int Width, int Height) Clamp(int width, int height)
    {
        int max = (int)Math.Min(_context.Capabilities.MaxImageDimension2D, 16384u);
        return (Math.Clamp(width, 1, max), Math.Clamp(height, 1, max));
    }

    private GpuBuffer CreateClusterLights(uint capacity) =>
        GpuBuffer.Create(_context, capacity * 4ul, BufferUsageFlags.StorageBufferBit, MemoryUsage.GpuOnly, "cluster light lists");

    private void CreateSceneImages()
    {
        uint w = (uint)RenderWidth, h = (uint)RenderHeight;
        Depth = GpuImage.Create2D(_context, w, h, TargetFormats.Depth,
            ImageUsageFlags.DepthStencilAttachmentBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit, "depth", dedicated: true);
        PickIds = GpuImage.Create2D(_context, w, h, TargetFormats.PickId,
            ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit, "pick ids", dedicated: true);
        InstanceIds = GpuImage.Create2D(_context, w, h, TargetFormats.InstanceId,
            ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit, "instance ids", dedicated: true);
        Normals = GpuImage.Create2D(_context, w, h, TargetFormats.Normal,
            ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit, "normals", dedicated: true);
        Hdr = GpuImage.Create2D(_context, w, h, TargetFormats.Hdr,
            ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit, "hdr", dedicated: true);
    }

    private void CreateDisplayImage()
    {
        Display = GpuImage.Create2D(_context, (uint)Width, (uint)Height, TargetFormats.Display,
            ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit, "display", dedicated: true);
    }

    private void RetireSceneImages()
    {
        RetireImage(Depth);
        RetireImage(PickIds);
        RetireImage(InstanceIds);
        RetireImage(Normals);
        RetireImage(Hdr);
        Depth = PickIds = InstanceIds = Normals = Hdr = null!;
    }

    private void RetireImage(GpuImage? image)
    {
        if (image is not null)
            _context.DeferDispose(image);
    }

    /// <summary>Marks a resource slot as used this render.</summary>
    private bool Used(int slot)
    {
        _unusedRenders[slot] = 0;
        return true;
    }

    /// <summary>
    /// Counts a render that did not need a resource slot; true once it was unused for more than
    /// <see cref="KeepUnusedRenders"/> renders (then it is released, so views that toggle features or alternate view
    /// modes do not recreate resources every frame).
    /// </summary>
    private bool Unused(int slot) => ++_unusedRenders[slot] > KeepUnusedRenders;

    private void Ensure(int slot, GpuImage? current, bool wanted, uint w, uint h, uint d, Format format, ImageUsageFlags usage, string name,
        out GpuImage? result, bool is3D = false)
    {
        if (!wanted)
        {
            if (current is not null && !Unused(slot))
            {
                result = current;
                return;
            }

            RetireImage(current);
            result = null;
            return;
        }

        Used(slot);

        if (current is not null && current.Width == w && current.Height == h && current.Depth == d && current.Format == format)
        {
            result = current;
            return;
        }

        RetireImage(current);
        result = is3D
            ? GpuImage.Create3D(_context, w, h, d, format, usage, name)
            : GpuImage.Create2D(_context, w, h, format, usage, name, dedicated: (ulong)w * h >= 256 * 256);
    }

    private void EnsureBloom(int levels)
    {
        levels = Math.Clamp(levels, 0, _bloom.Length);
        uint w0 = (uint)Math.Max((RenderWidth + 1) / 2, 1), h0 = (uint)Math.Max((RenderHeight + 1) / 2, 1);
        bool same = levels == BloomLevels && (levels == 0 || (_bloom[0]!.Width == w0 && _bloom[0]!.Height == h0));
        if (same)
            return;
        for (int i = 0; i < BloomLevels; i++)
        {
            RetireImage(_bloom[i]);
            _bloom[i] = null;
        }

        uint w = w0, h = h0;
        for (int i = 0; i < levels; i++)
        {
            _bloom[i] = GpuImage.Create2D(_context, w, h, TargetFormats.HdrStorage,
                ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit, $"bloom {i}");
            w = Math.Max((w + 1) / 2, 1);
            h = Math.Max((h + 1) / 2, 1);
        }

        BloomLevels = levels;
    }

    private void EnsureExposure(bool wanted)
    {
        if (!wanted)
        {
            if (Exposure is null)
                return;
            _context.DeferDispose(Exposure);
            Exposure = null;
            for (int i = 0; i < ExposureReadback.Length; i++)
            {
                if (ExposureReadback[i] is { } b)
                    _context.DeferDispose(b);
                ExposureReadback[i] = null;
                ExposureReadbackFrame[i] = 0;
            }

            AdaptedLog2Luminance = float.NaN;
            return;
        }

        if (Exposure is not null)
            return;
        ulong size = ShaderConstants.HistogramBins * 4ul + (ulong)sizeof(GpuExposureResult);
        Exposure = GpuBuffer.Create(_context, size, BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit | BufferUsageFlags.TransferSrcBit,
            MemoryUsage.GpuOnly, "luminance histogram");
        for (int i = 0; i < ExposureReadback.Length; i++)
        {
            ExposureReadback[i] = GpuBuffer.Create(_context, (ulong)sizeof(GpuExposureResult), BufferUsageFlags.TransferDstBit, MemoryUsage.Readback,
                "exposure read-back");
            ExposureReadbackFrame[i] = 0;
        }
    }

    private void CreatePool()
    {
        var sizes = stackalloc DescriptorPoolSize[5];
        uint n = 0;
        sizes[n++] = new DescriptorPoolSize(DescriptorType.UniformBuffer, 2);
        sizes[n++] = new DescriptorPoolSize(DescriptorType.StorageBuffer, 24);
        sizes[n++] = new DescriptorPoolSize(DescriptorType.CombinedImageSampler, 64);
        sizes[n++] = new DescriptorPoolSize(DescriptorType.StorageImage, 32);
        if (_renderer.Pipelines.RayTracing)
            sizes[n++] = new DescriptorPoolSize(DescriptorType.AccelerationStructureKhr, 2);
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = MaxPassSets + 1,
            PoolSizeCount = n,
            PPoolSizes = sizes,
        };
        DescriptorPool pool;
        VkUtil.Check(_context.Vk.CreateDescriptorPool(_context.Device, &poolInfo, null, &pool), "vkCreateDescriptorPool (view)");
        _pool = pool;
    }

    private DescriptorSet Allocate(DescriptorSetLayout layout)
    {
        var alloc = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _pool,
            DescriptorSetCount = 1,
            PSetLayouts = &layout,
        };
        DescriptorSet set;
        VkUtil.Check(_context.Vk.AllocateDescriptorSets(_context.Device, &alloc, &set), "vkAllocateDescriptorSets (view)");
        return set;
    }

    private void RetirePool()
    {
        _signature.Invalidate();
        if (_pool.Handle == 0)
            return;
        var pool = _pool;
        var context = _context;
        _context.DeferDestroy(() => context.Vk.DestroyDescriptorPool(context.Device, pool, null));
        _pool = default;
        ViewSet = InjectSet = IntegrateSet = GatherSet = RayTracingSet = FilterToScratchSet = FilterToIndirectSet = default;
    }

    private void DisposeResources()
    {
        Depth?.Dispose();
        PickIds?.Dispose();
        InstanceIds?.Dispose();
        Normals?.Dispose();
        Hdr?.Dispose();
        Display?.Dispose();
        DisplayDepth?.Dispose();
        Analysis?.Dispose();
        GradientLut?.Dispose();
        SelectionMask?.Dispose();
        FroxelInjected?.Dispose();
        FroxelHistory?.Dispose();
        FroxelIntegrated?.Dispose();
        Indirect?.Dispose();
        IndirectHistory?.Dispose();
        IndirectScratch?.Dispose();
        for (int i = 0; i < _bloom.Length; i++)
        {
            _bloom[i]?.Dispose();
            _bloom[i] = null;
        }

        BloomLevels = 0;
        FrameUniform?.Dispose();
        Clusters?.Dispose();
        ClusterLights?.Dispose();
        ClusterCounter?.Dispose();
        LightViews?.Dispose();
        Lists?.Dispose();
        BeamLines?.Dispose();
        Vpls?.Dispose();
        Exposure?.Dispose();
        for (int i = 0; i < ExposureReadback.Length; i++)
        {
            ExposureReadback[i]?.Dispose();
            ExposureReadback[i] = null;
        }

        if (_pool.Handle != 0)
            _context.Vk.DestroyDescriptorPool(_context.Device, _pool, null);
        _pool = default;
    }
}
