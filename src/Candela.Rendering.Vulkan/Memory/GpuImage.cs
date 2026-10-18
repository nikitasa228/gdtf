using Silk.NET.Vulkan;

namespace Candela.Rendering.Vulkan;

/// <summary>
/// A 2D or 3D <c>VkImage</c> (optimal tiling) with a default view over all mip levels, and its memory.
/// <see cref="Dispose"/> destroys immediately — use <see cref="VulkanContext.DeferDispose"/> for images in use.
/// </summary>
public sealed unsafe class GpuImage : IDisposable
{
    private readonly VulkanContext _context;
    private MemoryAllocation _allocation;
    private Image _handle;
    private ImageView _view;

    private GpuImage(VulkanContext context, Image handle, ImageView view, MemoryAllocation allocation, Format format, uint width,
        uint height, uint depth, uint mipLevels, ImageUsageFlags usage, ImageAspectFlags aspect, string name)
    {
        _context = context;
        _handle = handle;
        _view = view;
        _allocation = allocation;
        Format = format;
        Width = width;
        Height = height;
        Depth = depth;
        MipLevels = mipLevels;
        Usage = usage;
        Aspect = aspect;
        Name = name;
        State.Layout = ImageLayout.Undefined;
    }

    /// <summary>The image handle.</summary>
    public Image Handle => _handle;

    /// <summary>View over all mip levels.</summary>
    public ImageView View => _view;

    /// <summary>Pixel format.</summary>
    public Format Format { get; }

    /// <summary>Width of mip 0, pixels.</summary>
    public uint Width { get; }

    /// <summary>Height of mip 0, pixels.</summary>
    public uint Height { get; }

    /// <summary>Depth of mip 0 (1 for 2D images).</summary>
    public uint Depth { get; }

    /// <summary>True for a 3D image (volume).</summary>
    public bool Is3D => Depth > 1 || _is3D;

    /// <summary>Number of mip levels.</summary>
    public uint MipLevels { get; }

    /// <summary>Usage flags.</summary>
    public ImageUsageFlags Usage { get; }

    /// <summary>Aspect (colour or depth).</summary>
    public ImageAspectFlags Aspect { get; }

    /// <summary>Debug name.</summary>
    public string Name { get; }

    /// <summary>Current layout as tracked by the renderer (valid between frames).</summary>
    public ImageLayout CurrentLayout => State.Layout;

    /// <summary>Full subresource range.</summary>
    public ImageSubresourceRange FullRange => new(Aspect, 0, MipLevels, 0, 1);

    /// <summary>Synchronisation state for the barrier tracker.</summary>
    internal ResourceState State;

    /// <summary>True until the renderer has given the image defined contents (cleared or fully written).</summary>
    internal bool NeedsInitialization = true;

    private bool _is3D;

    /// <summary>Creates a 2D image with memory and a view.</summary>
    public static GpuImage Create2D(VulkanContext context, uint width, uint height, Format format, ImageUsageFlags usage, string name,
        uint mipLevels = 1, bool dedicated = false) =>
        Create(context, ImageType.Type2D, width, height, 1, format, usage, name, mipLevels, dedicated);

    /// <summary>Creates a 3D image (volume, one mip level) with memory and a view.</summary>
    public static GpuImage Create3D(VulkanContext context, uint width, uint height, uint depth, Format format, ImageUsageFlags usage, string name,
        bool dedicated = false)
    {
        var image = Create(context, ImageType.Type3D, width, height, depth, format, usage, name, 1, dedicated);
        image._is3D = true;
        return image;
    }

    private static GpuImage Create(VulkanContext context, ImageType type, uint width, uint height, uint depth, Format format, ImageUsageFlags usage,
        string name, uint mipLevels, bool dedicated)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (width == 0 || height == 0 || depth == 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Image size must be positive.");
        var aspect = format is Format.D32Sfloat or Format.D16Unorm or Format.X8D24UnormPack32
            ? ImageAspectFlags.DepthBit
            : format is Format.D24UnormS8Uint or Format.D32SfloatS8Uint
                ? ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit
                : ImageAspectFlags.ColorBit;
        var info = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = type,
            Format = format,
            Extent = new Extent3D(width, height, depth),
            MipLevels = mipLevels,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = usage,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
        };
        var vk = context.Vk;
        Image image;
        VkUtil.Check(vk.CreateImage(context.Device, &info, null, &image), $"vkCreateImage '{name}'");
        MemoryAllocation allocation = default;
        try
        {
            allocation = context.Allocator.AllocateAndBind(image, MemoryUsage.GpuOnly, dedicated);
            var viewInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = image,
                ViewType = type == ImageType.Type3D ? ImageViewType.Type3D : ImageViewType.Type2D,
                Format = format,
                SubresourceRange = new ImageSubresourceRange(aspect & ~ImageAspectFlags.StencilBit, 0, mipLevels, 0, 1),
            };
            ImageView view;
            VkUtil.Check(vk.CreateImageView(context.Device, &viewInfo, null, &view), $"vkCreateImageView '{name}'");
            context.SetDebugName(ObjectType.Image, image.Handle, name);
            context.SetDebugName(ObjectType.ImageView, view.Handle, name);
            return new GpuImage(context, image, view, allocation, format, width, height, depth, mipLevels, usage, aspect, name);
        }
        catch
        {
            vk.DestroyImage(context.Device, image, null);
            context.Allocator.Free(allocation);
            throw;
        }
    }

    /// <summary>Destroys the view, the image and frees the memory now.</summary>
    public void Dispose()
    {
        if (_handle.Handle == 0)
            return;
        var vk = _context.Vk;
        vk.DestroyImageView(_context.Device, _view, null);
        vk.DestroyImage(_context.Device, _handle, null);
        _context.Allocator.Free(_allocation);
        _handle = default;
        _view = default;
        _allocation = default;
    }
}
