using Candela.Core.Geometry;
using Candela.Rendering.Vulkan;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace Candela.App.Shell;

/// <summary>
/// Copies the acquired swapchain image of a frame into host memory just before it is presented (the screenshot path of
/// <c>--screenshot</c>): after the UI has been recorded, the image goes COLOR_ATTACHMENT → TRANSFER_SRC, is copied into
/// a readback buffer and returns to COLOR_ATTACHMENT, so <see cref="VulkanSwapchain.Present"/> finds the layout it expects.
/// The swapchain must have been created with <c>TRANSFER_SRC</c> usage.
/// </summary>
internal sealed unsafe class SwapchainCapture : IDisposable
{
    private readonly VulkanContext _context;
    private GpuBuffer? _buffer;
    private int _width;
    private int _height;
    private Format _format;
    private bool _pending;

    /// <summary>Creates a capture helper for <paramref name="context"/>.</summary>
    public SwapchainCapture(VulkanContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    /// <summary>True when a copy was recorded and has not been read back yet.</summary>
    public bool HasPendingCopy => _pending;

    /// <summary>Records the copy of <paramref name="image"/> into <paramref name="frame"/> (between EndRendering and Present).</summary>
    public void Record(VulkanFrame frame, SwapchainImage image)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (image.Width <= 0 || image.Height <= 0)
            return;
        ulong size = (ulong)image.Width * (ulong)image.Height * 4;
        if (_buffer is null || _buffer.Size < size)
        {
            if (_buffer is not null)
                _context.DeferDispose(_buffer);
            _buffer = GpuBuffer.Create(_context, size, BufferUsageFlags.TransferDstBit, MemoryUsage.Readback, "screenshot readback");
        }

        _width = image.Width;
        _height = image.Height;
        _format = image.Format;
        var vk = _context.Vk;
        var cmd = frame.CommandBuffer;
        var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1);

        var toTransfer = new ImageMemoryBarrier2
        {
            SType = StructureType.ImageMemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.ColorAttachmentOutputBit,
            SrcAccessMask = AccessFlags2.ColorAttachmentWriteBit,
            DstStageMask = PipelineStageFlags2.CopyBit,
            DstAccessMask = AccessFlags2.TransferReadBit,
            OldLayout = ImageLayout.ColorAttachmentOptimal,
            NewLayout = ImageLayout.TransferSrcOptimal,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image.Image,
            SubresourceRange = range,
        };
        var dependency = new DependencyInfo { SType = StructureType.DependencyInfo, ImageMemoryBarrierCount = 1, PImageMemoryBarriers = &toTransfer };
        vk.CmdPipelineBarrier2(cmd, &dependency);

        var region = new BufferImageCopy
        {
            BufferOffset = 0,
            BufferRowLength = 0,
            BufferImageHeight = 0,
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            ImageOffset = new Offset3D(0, 0, 0),
            ImageExtent = new Extent3D((uint)image.Width, (uint)image.Height, 1),
        };
        vk.CmdCopyImageToBuffer(cmd, image.Image, ImageLayout.TransferSrcOptimal, _buffer.Handle, 1, &region);

        var back = new ImageMemoryBarrier2
        {
            SType = StructureType.ImageMemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.CopyBit,
            SrcAccessMask = AccessFlags2.None,
            DstStageMask = PipelineStageFlags2.ColorAttachmentOutputBit,
            DstAccessMask = AccessFlags2.ColorAttachmentWriteBit,
            OldLayout = ImageLayout.TransferSrcOptimal,
            NewLayout = ImageLayout.ColorAttachmentOptimal,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image.Image,
            SubresourceRange = range,
        };
        var hostRead = new BufferMemoryBarrier2
        {
            SType = StructureType.BufferMemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.CopyBit,
            SrcAccessMask = AccessFlags2.TransferWriteBit,
            DstStageMask = PipelineStageFlags2.HostBit,
            DstAccessMask = AccessFlags2.HostReadBit,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Buffer = _buffer.Handle,
            Offset = 0,
            Size = Vk.WholeSize,
        };
        var dependency2 = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            ImageMemoryBarrierCount = 1,
            PImageMemoryBarriers = &back,
            BufferMemoryBarrierCount = 1,
            PBufferMemoryBarriers = &hostRead,
        };
        vk.CmdPipelineBarrier2(cmd, &dependency2);
        _pending = true;
    }

    /// <summary>
    /// Waits for the submitted frames and returns the captured image as top-down RGBA8 (sRGB-encoded, opaque), or null
    /// when nothing was captured. Call after <see cref="VulkanSwapchain.Present"/>.
    /// </summary>
    public ImageData? Read()
    {
        if (!_pending || _buffer is null)
            return null;
        _context.WaitForSubmittedFrames();
        _pending = false;
        int bytes = _width * _height * 4;
        _context.Allocator.Invalidate(_buffer.Allocation, 0, (ulong)bytes);
        var pixels = new byte[bytes];
        new ReadOnlySpan<byte>(_buffer.MappedPointer, bytes).CopyTo(pixels);
        bool bgra = _format is Format.B8G8R8A8Unorm or Format.B8G8R8A8Srgb;
        for (int i = 0; i < bytes; i += 4)
        {
            if (bgra)
                (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
            pixels[i + 3] = 255;
        }

        return new ImageData { Width = _width, Height = _height, Format = PixelFormat.Rgba8, Pixels = pixels };
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_buffer is not null)
            _context.DeferDispose(_buffer);
        _buffer = null;
    }

    /// <summary>The readback buffer handle (diagnostics).</summary>
    internal VkBuffer BufferHandle => _buffer?.Handle ?? default;
}
