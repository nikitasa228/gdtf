using Silk.NET.Vulkan;

namespace Candela.Rendering.Vulkan;

/// <summary>
/// A 2D <c>VkImage</c> (optimal tiling) with a default view over all mip levels, and its memory.
/// <see cref="Dispose"/> destroys immediately — use <see cref="VulkanContext.DeferDispose"/> for images in use.
/// </summary>
public sealed unsafe class GpuImage : IDisposable
{
    private readonly VulkanContext _context;
    private MemoryAllocation _allocation;
    private Image _handle;
    private ImageView _view;

    private GpuImage(VulkanContext context, Image handle, ImageView view, MemoryAllocation allocation, Format format, uint width,
        uint height, uint mipLevels, ImageUsageFlags usage, ImageAspectFlags aspect, string name)
    {
        _context = context;
        _handle = handle;
        _view = view;
        _allocation = allocation;
        Format = format;
        Width = width;
        Height = height;
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

    /// <summary>Creates a 2D image with memory and a view.</summary>
    public static GpuImage Create2D(VulkanContext context, uint width, uint height, Format format, ImageUsageFlags usage, string name,
        uint mipLevels = 1, bool dedicated = false)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (width == 0 || height == 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Image size must be positive.");
        var aspect = format is Format.D32Sfloat or Format.D16Unorm or Format.X8D24UnormPack32
            ? ImageAspectFlags.DepthBit
            : format is Format.D24UnormS8Uint or Format.D32SfloatS8Uint
                ? ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit
                : ImageAspectFlags.ColorBit;
        var info = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = format,
            Extent = new Extent3D(width, height, 1),
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
                ViewType = ImageViewType.Type2D,
                Format = format,
                SubresourceRange = new ImageSubresourceRange(aspect & ~ImageAspectFlags.StencilBit, 0, mipLevels, 0, 1),
            };
            ImageView view;
            VkUtil.Check(vk.CreateImageView(context.Device, &viewInfo, null, &view), $"vkCreateImageView '{name}'");
            context.SetDebugName(ObjectType.Image, image.Handle, name);
            context.SetDebugName(ObjectType.ImageView, view.Handle, name);
            return new GpuImage(context, image, view, allocation, format, width, height, mipLevels, usage, aspect, name);
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
