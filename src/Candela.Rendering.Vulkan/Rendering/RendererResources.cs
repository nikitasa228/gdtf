using System.Numerics;
using Silk.NET.Vulkan;

namespace Candela.Rendering.Vulkan;

/// <summary>
/// Samplers and placeholder resources shared by every render target of a renderer. The placeholders (1×1 images, a
/// 1×1×1 volume, a 1×1 depth image, a small zeroed buffer) are bound wherever a feature is off, so every descriptor of
/// every view set is always valid and in the layout its shaders expect; they are initialised once with defined
/// contents (zero, transmittance 1) and never written again.
/// </summary>
internal sealed unsafe class RendererResources : IDisposable
{
    private readonly VulkanContext _context;

    public RendererResources(VulkanContext context, BarrierBatch barriers)
    {
        _context = context;
        try
        {
            LinearClamp = CreateSampler(Filter.Linear, SamplerMipmapMode.Linear, compare: false, "linear clamp sampler");
            NearestClamp = CreateSampler(Filter.Nearest, SamplerMipmapMode.Nearest, compare: false, "nearest clamp sampler");
            // Bilinear PCF needs linear filtering of D32_SFLOAT, which Vulkan does not guarantee: fall back to nearest.
            var depthFeatures = context.Vk.GetPhysicalDeviceFormatProperties(context.PhysicalDevice, TargetFormats.Depth).OptimalTilingFeatures;
            DepthLinearFiltering = (depthFeatures & FormatFeatureFlags.SampledImageFilterLinearBit) != 0;
            ShadowCompare = CreateSampler(DepthLinearFiltering ? Filter.Linear : Filter.Nearest, SamplerMipmapMode.Nearest, compare: true,
                "shadow comparison sampler");

            const ImageUsageFlags usage = ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit;
            Dummy2D = GpuImage.Create2D(context, 1, 1, TargetFormats.HdrStorage, usage, "placeholder 2D");
            Dummy3D = GpuImage.Create3D(context, 1, 1, 1, TargetFormats.HdrStorage, usage, "placeholder volume");
            // The read-only depth layout requires the attachment usage.
            DummyDepth = GpuImage.Create2D(context, 1, 1, TargetFormats.Depth, usage | ImageUsageFlags.DepthStencilAttachmentBit, "placeholder depth");
            DummyBuffer = GpuBuffer.Create(context, 1024, BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit, MemoryUsage.GpuOnly,
                "placeholder buffer");

            var cmd = context.Immediate.Begin();
            barriers.CommandBuffer = cmd;
            barriers.Use(Dummy2D, ImageUse.TransferDestination, discard: true);
            barriers.Use(Dummy3D, ImageUse.TransferDestination, discard: true);
            barriers.Use(DummyDepth, ImageUse.TransferDestination, discard: true);
            barriers.Use(DummyBuffer, BufferUse.TransferDestination);
            barriers.Flush();
            var zero = new ClearColorValue(0f, 0f, 0f, 0f);
            var transparentVolume = new ClearColorValue(0f, 0f, 0f, 1f); // in-scattering 0, transmittance 1
            var colorRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1);
            context.Vk.CmdClearColorImage(cmd, Dummy2D.Handle, ImageLayout.TransferDstOptimal, &zero, 1, &colorRange);
            context.Vk.CmdClearColorImage(cmd, Dummy3D.Handle, ImageLayout.TransferDstOptimal, &transparentVolume, 1, &colorRange);
            var depthClear = new ClearDepthStencilValue(0f, 0);
            var depthRange = new ImageSubresourceRange(ImageAspectFlags.DepthBit, 0, 1, 0, 1);
            context.Vk.CmdClearDepthStencilImage(cmd, DummyDepth.Handle, ImageLayout.TransferDstOptimal, &depthClear, 1, &depthRange);
            context.Vk.CmdFillBuffer(cmd, DummyBuffer.Handle, 0, Vk.WholeSize, 0);
            barriers.Use(Dummy2D, ImageUse.SampledAnyShader);
            barriers.Use(Dummy3D, ImageUse.SampledAnyShader);
            barriers.Use(DummyDepth, ImageUse.DepthSampledAnyShader);
            barriers.Use(DummyBuffer, BufferUse.ShaderRead);
            barriers.Flush();
            context.Immediate.SubmitAndWait();
            Dummy2D.NeedsInitialization = Dummy3D.NeedsInitialization = DummyDepth.NeedsInitialization = false;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>Bilinear (trilinear for volumes, with mips), clamp-to-edge.</summary>
    public Sampler LinearClamp { get; }

    /// <summary>Point sampling, clamp-to-edge (exact texel reads, integer images).</summary>
    public Sampler NearestClamp { get; }

    /// <summary>Depth comparison (reverse-Z: lit when reference ≥ stored) with bilinear PCF when the device can filter depth, clamp-to-edge.</summary>
    public Sampler ShadowCompare { get; }

    /// <summary>The device filters D32_SFLOAT linearly (hardware bilinear PCF).</summary>
    public bool DepthLinearFiltering { get; }

    /// <summary>1×1 RGBA16F zero image (sampled layout).</summary>
    public GpuImage Dummy2D { get; } = null!;

    /// <summary>1×1×1 RGBA16F volume holding (0, 0, 0, 1): no in-scattering, full transmittance.</summary>
    public GpuImage Dummy3D { get; } = null!;

    /// <summary>1×1 depth image holding 0 (the far plane in reverse-Z).</summary>
    public GpuImage DummyDepth { get; } = null!;

    /// <summary>A zeroed storage buffer.</summary>
    public GpuBuffer DummyBuffer { get; } = null!;

    public void Dispose()
    {
        Dummy2D?.Dispose();
        Dummy3D?.Dispose();
        DummyDepth?.Dispose();
        DummyBuffer?.Dispose();
        var vk = _context.Vk;
        if (LinearClamp.Handle != 0)
            vk.DestroySampler(_context.Device, LinearClamp, null);
        if (NearestClamp.Handle != 0)
            vk.DestroySampler(_context.Device, NearestClamp, null);
        if (ShadowCompare.Handle != 0)
            vk.DestroySampler(_context.Device, ShadowCompare, null);
    }

    private Sampler CreateSampler(Filter filter, SamplerMipmapMode mipmap, bool compare, string name)
    {
        var info = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = filter,
            MinFilter = filter,
            MipmapMode = mipmap,
            AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge,
            CompareEnable = compare,
            CompareOp = CompareOp.GreaterOrEqual,
            MinLod = 0f,
            MaxLod = compare ? 0f : Vk.LodClampNone,
        };
        Sampler sampler;
        VkUtil.Check(_context.Vk.CreateSampler(_context.Device, &info, null, &sampler), "vkCreateSampler");
        _context.SetDebugName(ObjectType.Sampler, sampler.Handle, name);
        return sampler;
    }
}

/// <summary>What the per-view resources of a target must provide for one frame (derived from the view and settings).</summary>
internal struct ViewRequirements
{
    /// <summary>Internal (scene) resolution.</summary>
    public int RenderWidth;

    public int RenderHeight;

    /// <summary>The scene is rendered below the display resolution (overlay depth needed).</summary>
    public bool Scaled;

    /// <summary>Lux or noise analysis image and the gradient table.</summary>
    public bool Analysis;

    /// <summary>Selection mask at display resolution.</summary>
    public bool Selection;

    /// <summary>Froxel grid size (0 = no volumetrics).</summary>
    public int VolumeX;

    public int VolumeY;

    public int VolumeZ;

    /// <summary>Indirect irradiance resolution (0 = no indirect light).</summary>
    public int IndirectWidth;

    public int IndirectHeight;

    /// <summary>History of the ray-traced indirect light.</summary>
    public bool IndirectHistory;

    /// <summary>Virtual point lights (0 = none).</summary>
    public int VplCapacity;

    /// <summary>Bloom pyramid levels (0 = no bloom).</summary>
    public int BloomLevels;

    /// <summary>Luminance histogram and exposure read-back buffers.</summary>
    public bool AutoExposure;
}

/// <summary>Small numeric helpers of the stage-2 renderer.</summary>
internal static class RenderMath
{
    /// <summary>Halton low-discrepancy sequence value of <paramref name="index"/> (≥ 1) in base <paramref name="b"/>, in [0, 1).</summary>
    public static float Halton(int index, int b)
    {
        float f = 1f, r = 0f;
        int i = index;
        while (i > 0)
        {
            f /= b;
            r += f * (i % b);
            i /= b;
        }

        return r;
    }

    /// <summary>Converts an sRGB-encoded colour with alpha to a vector of sRGB values (alpha kept).</summary>
    public static Vector4 Sanitize(Vector4 c) => new(
        float.IsFinite(c.X) ? Math.Clamp(c.X, 0f, 1f) : 0f,
        float.IsFinite(c.Y) ? Math.Clamp(c.Y, 0f, 1f) : 0f,
        float.IsFinite(c.Z) ? Math.Clamp(c.Z, 0f, 1f) : 0f,
        float.IsFinite(c.W) ? Math.Clamp(c.W, 0f, 1f) : 1f);
}
