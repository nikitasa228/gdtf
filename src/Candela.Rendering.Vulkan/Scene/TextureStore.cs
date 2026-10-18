using System.Runtime.InteropServices;
using Candela.Core.Geometry;
using Silk.NET.Vulkan;

namespace Candela.Rendering.Vulkan;

/// <summary>A texture in the bindless array.</summary>
internal sealed class TextureRecord
{
    public TextureRecord(GpuImage image, uint slot, bool srgb)
    {
        Image = image;
        Slot = slot;
        Srgb = srgb;
    }

    public GpuImage Image { get; }

    /// <summary>Index in the bindless array (<c>textures[Slot]</c> in GLSL).</summary>
    public uint Slot { get; }

    public bool Srgb { get; }

    /// <summary>Key in the material image cache (null for textures created through <see cref="IRenderWorld.CreateTexture"/>).</summary>
    public string? CacheKey { get; set; }

    /// <summary>Materials using this cached texture.</summary>
    public int References { get; set; }

    public bool Destroyed { get; set; }
}

/// <summary>
/// The bindless texture array: slot allocation, image creation and upload (mip chains generated with
/// <c>vkCmdBlitImage</c> when the format supports linear blits, otherwise box-filtered on the CPU), and descriptor
/// bookkeeping for the per-frame-slot scene descriptor sets. Slots 0–2 hold the default white, black and flat-normal
/// textures. Destroyed slots point at the white texture until reused, so a stale index never reads a dead view.
/// RGBA8 images use <c>R8G8B8A8_SRGB</c> (colour) or <c>_UNORM</c> (data); float images are converted to
/// <c>R16G16B16A16_SFLOAT</c> (linear filtering of 32-bit float formats is optional in Vulkan). Main thread only.
/// </summary>
internal sealed unsafe class TextureStore : IDisposable
{
    private const int WriteChunk = 64;
    private readonly VulkanContext _context;
    private readonly Stack<uint> _freeSlots = new();
    private readonly ImageView[] _views;
    private readonly List<PendingUpload> _pending = new();
    private readonly bool[][] _dirty;
    private readonly List<uint>[] _dirtyLists;
    private readonly DescriptorImageInfo[] _imageInfos = new DescriptorImageInfo[WriteChunk];
    private readonly WriteDescriptorSet[] _writes = new WriteDescriptorSet[WriteChunk];
    private readonly Dictionary<Format, FormatFeatureFlags> _formatFeatures = new();
    private readonly TextureRecord[] _defaults = new TextureRecord[3];
    private readonly BufferImageCopy[] _copies = new BufferImageCopy[32];
    private uint _nextSlot;

    public TextureStore(VulkanContext context)
    {
        _context = context;
        Capacity = Math.Max(64u, Math.Min(context.Capabilities.MaxBindlessTextures, 8192u));
        _views = new ImageView[Capacity];
        _dirty = new bool[VulkanContext.FramesInFlight][];
        _dirtyLists = new List<uint>[VulkanContext.FramesInFlight];
        for (int i = 0; i < _dirty.Length; i++)
        {
            _dirty[i] = new bool[Capacity];
            _dirtyLists[i] = new List<uint>(64);
        }

        var caps = context.Capabilities;
        var samplerInfo = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Linear,
            MinFilter = Filter.Linear,
            MipmapMode = SamplerMipmapMode.Linear,
            AddressModeU = SamplerAddressMode.Repeat,
            AddressModeV = SamplerAddressMode.Repeat,
            AddressModeW = SamplerAddressMode.Repeat,
            AnisotropyEnable = caps.SamplerAnisotropy,
            MaxAnisotropy = caps.SamplerAnisotropy ? Math.Min(8f, caps.MaxSamplerAnisotropy) : 1f,
            MinLod = 0f,
            MaxLod = Vk.LodClampNone,
        };
        Sampler sampler;
        VkUtil.Check(context.Vk.CreateSampler(context.Device, &samplerInfo, null, &sampler), "vkCreateSampler");
        Sampler = sampler;
        context.SetDebugName(ObjectType.Sampler, sampler.Handle, "material sampler");

        _defaults[ShaderConstants.TextureWhite] = Create(Solid(255, 255, 255, 255), srgb: false, "default white");
        _defaults[ShaderConstants.TextureBlack] = Create(Solid(0, 0, 0, 255), srgb: false, "default black");
        _defaults[ShaderConstants.TextureFlatNormal] = Create(Solid(128, 128, 255, 255), srgb: false, "default flat normal");
    }

    /// <summary>Size of the bindless array (variable descriptor count of the scene sets).</summary>
    public uint Capacity { get; }

    /// <summary>Trilinear, anisotropic, repeating sampler of all material textures.</summary>
    public Sampler Sampler { get; }

    /// <summary>One past the highest slot ever used.</summary>
    public uint HighWater => _nextSlot;

    /// <summary>Live textures excluding the defaults.</summary>
    public int Count { get; private set; }

    /// <summary>Creates a texture and queues its upload (recorded by <see cref="RecordUploads"/> in the next frame).</summary>
    public TextureRecord Create(ImageData image, bool srgb, string name)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Width <= 0 || image.Height <= 0)
            throw new ArgumentException($"Texture '{name}' has no pixels ({image.Width}×{image.Height}).", nameof(image));
        int bytesPerPixel = image.Format == PixelFormat.RgbaFloat ? 16 : 4;
        if (image.Pixels.Length < (long)image.Width * image.Height * bytesPerPixel)
            throw new ArgumentException($"Texture '{name}': {image.Pixels.Length} bytes for {image.Width}×{image.Height} {image.Format}.", nameof(image));
        uint slot = AllocateSlot();

        // Pixel data in the GPU format: RGBA8 as is, float → half. Oversized images are halved on the CPU first.
        bool isFloat = image.Format == PixelFormat.RgbaFloat;
        var format = isFloat ? Format.R16G16B16A16Sfloat : srgb ? Format.R8G8B8A8Srgb : Format.R8G8B8A8Unorm;
        int texel = isFloat ? 8 : 4;
        int width = image.Width, height = image.Height;
        byte[] level0 = isFloat ? FloatToHalf(image.Pixels, width * height) : image.Pixels;
        uint maxDim = _context.Capabilities.MaxImageDimension2D;
        while (width > maxDim || height > maxDim)
            level0 = CpuMips.Downsample(level0, ref width, ref height, format);

        uint levels = VkUtil.MipCount((uint)width, (uint)height);
        const FormatFeatureFlags blitFeatures = FormatFeatureFlags.BlitSrcBit | FormatFeatureFlags.BlitDstBit |
                                                FormatFeatureFlags.SampledImageFilterLinearBit;
        bool gpuMips = levels == 1 || (FeaturesOf(format) & blitFeatures) == blitFeatures;

        GpuImage gpuImage;
        try
        {
            gpuImage = GpuImage.Create2D(_context, (uint)width, (uint)height, format,
                ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit | ImageUsageFlags.TransferSrcBit, name, levels);
        }
        catch
        {
            _freeSlots.Push(slot);
            throw;
        }

        // Stage level 0 (GPU mips) or the whole chain (CPU mips).
        var levelSizes = new (uint W, uint H, ulong Offset)[gpuMips ? 1 : levels];
        ulong total = 0;
        for (int i = 0; i < levelSizes.Length; i++)
        {
            uint w = Math.Max(1u, (uint)width >> i), h = Math.Max(1u, (uint)height >> i);
            levelSizes[i] = (w, h, total);
            total = VkUtil.AlignUp(total + (ulong)w * h * (ulong)texel, 16);
        }

        var region = _context.Upload.Allocate(total, 16);
        var dst = region.Span;
        level0.AsSpan(0, width * height * texel).CopyTo(dst);
        if (!gpuMips)
        {
            byte[] current = level0;
            int w = width, h = height;
            for (int i = 1; i < levelSizes.Length; i++)
            {
                current = CpuMips.Downsample(current, ref w, ref h, format);
                current.AsSpan(0, w * h * texel).CopyTo(dst[(int)levelSizes[i].Offset..]);
            }
        }

        var record = new TextureRecord(gpuImage, slot, srgb);
        _pending.Add(new PendingUpload(record, region.Buffer, region.Offset, levelSizes, gpuMips));
        SetView(slot, gpuImage.View);
        if (slot >= ShaderConstants.TextureFlatNormal + 1)
            Count++;
        return record;
    }

    /// <summary>Destroys a texture after the frames in flight; its slot shows the white texture until reused.</summary>
    public void Destroy(TextureRecord texture)
    {
        if (texture.Destroyed)
            return;
        texture.Destroyed = true;
        _context.DeferDispose(texture.Image);
        SetView(texture.Slot, _defaults[ShaderConstants.TextureWhite].Image.View);
        _freeSlots.Push(texture.Slot);
        Count--;
    }

    /// <summary>True when uploads are waiting for the next frame.</summary>
    public bool HasPendingUploads => _pending.Count > 0;

    /// <summary>Records the queued uploads and mip generation into <paramref name="cmd"/>.</summary>
    public void RecordUploads(CommandBuffer cmd, BarrierBatch barriers)
    {
        if (_pending.Count == 0)
            return;
        var vk = _context.Vk;
        const PipelineStageFlags2 transfer = PipelineStageFlags2.CopyBit | PipelineStageFlags2.BlitBit;
        BarrierBatch.Describe(ImageUse.SampledAnyShader, out var readStages, out var readAccess, out _, out _);
        foreach (var p in _pending)
        {
            var image = p.Texture.Image;
            if (p.Texture.Destroyed)
                continue;
            uint levels = image.MipLevels;
            barriers.AddImageRange(image.Handle, new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, levels, 0, 1),
                PipelineStageFlags2.None, AccessFlags2.None, transfer, AccessFlags2.TransferWriteBit, ImageLayout.Undefined,
                ImageLayout.TransferDstOptimal);
            barriers.Flush();

            for (int i = 0; i < p.Levels.Length; i++)
            {
                _copies[i] = new BufferImageCopy
                {
                    BufferOffset = p.SourceOffset + p.Levels[i].Offset,
                    ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, (uint)i, 0, 1),
                    ImageExtent = new Extent3D(p.Levels[i].W, p.Levels[i].H, 1),
                };
            }

            fixed (BufferImageCopy* copies = _copies)
                vk.CmdCopyBufferToImage(cmd, p.Source, image.Handle, ImageLayout.TransferDstOptimal, (uint)p.Levels.Length, copies);
            if (p.GpuMips && levels > 1)
            {
                for (uint i = 1; i < levels; i++)
                {
                    barriers.AddImageRange(image.Handle, new ImageSubresourceRange(ImageAspectFlags.ColorBit, i - 1, 1, 0, 1),
                        transfer, AccessFlags2.TransferWriteBit, PipelineStageFlags2.BlitBit, AccessFlags2.TransferReadBit,
                        ImageLayout.TransferDstOptimal, ImageLayout.TransferSrcOptimal);
                    barriers.Flush();
                    int sw = (int)Math.Max(1u, image.Width >> (int)(i - 1)), sh = (int)Math.Max(1u, image.Height >> (int)(i - 1));
                    int dw = (int)Math.Max(1u, image.Width >> (int)i), dh = (int)Math.Max(1u, image.Height >> (int)i);
                    var blit = new ImageBlit
                    {
                        SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, i - 1, 0, 1),
                        DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, i, 0, 1),
                    };
                    blit.SrcOffsets[1] = new Offset3D(sw, sh, 1);
                    blit.DstOffsets[1] = new Offset3D(dw, dh, 1);
                    vk.CmdBlitImage(cmd, image.Handle, ImageLayout.TransferSrcOptimal, image.Handle, ImageLayout.TransferDstOptimal, 1,
                        &blit, Filter.Linear);
                }

                barriers.AddImageRange(image.Handle, new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, levels - 1, 0, 1),
                    PipelineStageFlags2.BlitBit, AccessFlags2.None, readStages, readAccess, ImageLayout.TransferSrcOptimal,
                    ImageLayout.ShaderReadOnlyOptimal);
                barriers.AddImageRange(image.Handle, new ImageSubresourceRange(ImageAspectFlags.ColorBit, levels - 1, 1, 0, 1),
                    transfer, AccessFlags2.TransferWriteBit, readStages, readAccess, ImageLayout.TransferDstOptimal,
                    ImageLayout.ShaderReadOnlyOptimal);
            }
            else
            {
                barriers.AddImageRange(image.Handle, image.FullRange, transfer, AccessFlags2.TransferWriteBit, readStages, readAccess,
                    ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal);
            }

            image.State = new ResourceState
            {
                Layout = ImageLayout.ShaderReadOnlyOptimal,
                WriteStages = transfer,
                WriteAccess = AccessFlags2.TransferWriteBit,
                ReadStages = readStages,
                VisibleStages = readStages,
                VisibleAccess = readAccess,
            };
        }

        barriers.Flush();
        _pending.Clear();
    }

    /// <summary>Writes every slot whose view changed since the set of <paramref name="frameSlot"/> was last updated.</summary>
    public void WriteDirty(DescriptorSet set, uint binding, int frameSlot)
    {
        var list = _dirtyLists[frameSlot];
        var flags = _dirty[frameSlot];
        int n = 0;
        foreach (uint slot in list)
        {
            flags[slot] = false;
            AddWrite(set, binding, slot, ref n);
        }

        list.Clear();
        FlushWrites(ref n);
    }

    /// <summary>Writes every used slot into a freshly allocated set (and clears the slot's dirty list).</summary>
    public void WriteAll(DescriptorSet set, uint binding, int frameSlot)
    {
        var list = _dirtyLists[frameSlot];
        var flags = _dirty[frameSlot];
        foreach (uint slot in list)
            flags[slot] = false;
        list.Clear();
        int n = 0;
        for (uint slot = 0; slot < _nextSlot; slot++)
        {
            if (_views[slot].Handle != 0)
                AddWrite(set, binding, slot, ref n);
        }

        FlushWrites(ref n);
    }

    /// <summary>Destroys every texture and the sampler (device idle).</summary>
    public void Dispose()
    {
        foreach (var d in _defaults)
            d?.Image.Dispose();
        _context.Vk.DestroySampler(_context.Device, Sampler, null);
    }

    private void AddWrite(DescriptorSet set, uint binding, uint slot, ref int n)
    {
        _imageInfos[n] = new DescriptorImageInfo(Sampler, _views[slot], ImageLayout.ShaderReadOnlyOptimal);
        _writes[n] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = set,
            DstBinding = binding,
            DstArrayElement = slot,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.CombinedImageSampler,
        };
        n++;
        if (n == WriteChunk)
            FlushWrites(ref n);
    }

    private void FlushWrites(ref int n)
    {
        if (n == 0)
            return;
        fixed (DescriptorImageInfo* infos = _imageInfos)
        fixed (WriteDescriptorSet* writes = _writes)
        {
            for (int i = 0; i < n; i++)
                writes[i].PImageInfo = &infos[i];
            _context.Vk.UpdateDescriptorSets(_context.Device, (uint)n, writes, 0, null);
        }

        n = 0;
    }

    private uint AllocateSlot()
    {
        if (_freeSlots.Count > 0)
            return _freeSlots.Pop();
        if (_nextSlot >= Capacity)
            throw new InvalidOperationException($"The bindless texture array is full ({Capacity} textures).");
        return _nextSlot++;
    }

    private void SetView(uint slot, ImageView view)
    {
        _views[slot] = view;
        for (int i = 0; i < _dirty.Length; i++)
        {
            if (!_dirty[i][slot])
            {
                _dirty[i][slot] = true;
                _dirtyLists[i].Add(slot);
            }
        }
    }

    private FormatFeatureFlags FeaturesOf(Format format)
    {
        if (!_formatFeatures.TryGetValue(format, out var features))
        {
            features = _context.Vk.GetPhysicalDeviceFormatProperties(_context.PhysicalDevice, format).OptimalTilingFeatures;
            _formatFeatures[format] = features;
        }

        return features;
    }

    private static ImageData Solid(byte r, byte g, byte b, byte a) => new() { Width = 1, Height = 1, Pixels = new[] { r, g, b, a } };

    private static byte[] FloatToHalf(byte[] pixels, int count)
    {
        var src = MemoryMarshal.Cast<byte, float>(pixels.AsSpan(0, count * 16));
        var result = new byte[count * 8];
        var dst = MemoryMarshal.Cast<byte, Half>(result.AsSpan());
        for (int i = 0; i < src.Length; i++)
        {
            float v = src[i];
            dst[i] = float.IsNaN(v) ? Half.Zero : (Half)Math.Clamp(v, -65504f, 65504f);
        }

        return result;
    }

    private sealed record PendingUpload(TextureRecord Texture, Buffer Source, ulong SourceOffset, (uint W, uint H, ulong Offset)[] Levels, bool GpuMips);
}

/// <summary>2×2 box-filter downsampling for the CPU mip fallback (sRGB data is averaged in linear light).</summary>
internal static class CpuMips
{
    private static readonly float[] _srgbToLinear = BuildSrgbTable();

    /// <summary>Halves an image (odd sizes round down, minimum 1) in the given GPU format.</summary>
    public static byte[] Downsample(byte[] src, ref int width, ref int height, Format format)
    {
        int w = Math.Max(1, width / 2), h = Math.Max(1, height / 2);
        bool half = format == Format.R16G16B16A16Sfloat;
        bool srgb = format == Format.R8G8B8A8Srgb;
        int texel = half ? 8 : 4;
        var dst = new byte[w * h * texel];
        var srcHalf = half ? MemoryMarshal.Cast<byte, Half>(src.AsSpan()) : default;
        var dstHalf = half ? MemoryMarshal.Cast<byte, Half>(dst.AsSpan()) : default;
        for (int y = 0; y < h; y++)
        {
            int y0 = Math.Min(2 * y, height - 1), y1 = Math.Min(2 * y + 1, height - 1);
            for (int x = 0; x < w; x++)
            {
                int x0 = Math.Min(2 * x, width - 1), x1 = Math.Min(2 * x + 1, width - 1);
                for (int c = 0; c < 4; c++)
                {
                    int i00 = (y0 * width + x0) * 4 + c, i01 = (y0 * width + x1) * 4 + c;
                    int i10 = (y1 * width + x0) * 4 + c, i11 = (y1 * width + x1) * 4 + c;
                    int o = (y * w + x) * 4 + c;
                    if (half)
                    {
                        float v = 0.25f * ((float)srcHalf[i00] + (float)srcHalf[i01] + (float)srcHalf[i10] + (float)srcHalf[i11]);
                        dstHalf[o] = (Half)v;
                    }
                    else if (srgb && c < 3)
                    {
                        float v = 0.25f * (_srgbToLinear[src[i00]] + _srgbToLinear[src[i01]] + _srgbToLinear[src[i10]] + _srgbToLinear[src[i11]]);
                        dst[o] = (byte)Math.Clamp(MathF.Round(Candela.Photometry.ColorSpaces.SrgbEncode(v) * 255f), 0f, 255f);
                    }
                    else
                    {
                        dst[o] = (byte)((src[i00] + src[i01] + src[i10] + src[i11] + 2) / 4);
                    }
                }
            }
        }

        width = w;
        height = h;
        return dst;
    }

    private static float[] BuildSrgbTable()
    {
        var t = new float[256];
        for (int i = 0; i < 256; i++)
            t[i] = Candela.Photometry.ColorSpaces.SrgbDecode(i / 255f);
        return t;
    }
}
