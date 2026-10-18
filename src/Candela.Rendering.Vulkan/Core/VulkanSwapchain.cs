using System.Numerics;
using Silk.NET.Core;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace Candela.Rendering.Vulkan;

/// <summary>Options of a <see cref="VulkanSwapchain"/>.</summary>
public sealed class VulkanSwapchainOptions
{
    /// <summary>
    /// True = wait for vertical blank (<c>FIFO</c>, always supported); false = <c>MAILBOX</c> when available,
    /// otherwise <c>IMMEDIATE</c>, otherwise <c>FIFO</c>.
    /// </summary>
    public bool VSync { get; set; } = true;

    /// <summary>
    /// Preferred image format. Null = the first of <c>B8G8R8A8_UNORM</c> / <c>R8G8B8A8_UNORM</c> (sRGB non-linear
    /// colour space) the surface supports. A UNORM format is the right choice for the editor: Dear ImGui outputs
    /// sRGB-encoded colours and the render targets' display images hold sRGB-encoded values, so both pass through
    /// unchanged. An <c>_SRGB</c> format would encode them a second time.
    /// </summary>
    public Format? PreferredFormat { get; set; }

    /// <summary>Usage added to <c>COLOR_ATTACHMENT</c> when the surface supports it (e.g. <c>TRANSFER_DST</c> for blits).</summary>
    public ImageUsageFlags AdditionalUsage { get; set; }
}

/// <summary>A swapchain image acquired for the current frame.</summary>
/// <param name="Index">Index of the image in the swapchain.</param>
/// <param name="Image">The image (owned by the swapchain).</param>
/// <param name="View">A colour view of the image.</param>
/// <param name="Format">The swapchain format.</param>
/// <param name="Width">Width, pixels.</param>
/// <param name="Height">Height, pixels.</param>
public readonly record struct SwapchainImage(int Index, Image Image, ImageView View, Format Format, int Width, int Height);

/// <summary>
/// Presentation for the editor window: a <c>VkSwapchainKHR</c> on a surface the caller created (for example with
/// <c>glfwCreateWindowSurface</c>) on the device of a shared <see cref="VulkanContext"/>, recreated on resize, with
/// correct acquire/present synchronisation. Main thread only.
/// </summary>
/// <remarks>
/// <para><b>Per frame</b> (after <see cref="VulkanContext.BeginFrame"/> and the viewports'
/// <see cref="IRenderer.Render"/> calls): <see cref="TryAcquire"/> → <see cref="BeginRendering"/> (transition to
/// <c>COLOR_ATTACHMENT_OPTIMAL</c>, clear, <c>vkCmdBeginRendering</c>) → record Dear ImGui
/// (<c>ImGui_ImplVulkan_RenderDrawData(drawData, frame.CommandBuffer)</c>) → <see cref="EndRendering"/> →
/// <see cref="Present"/>, which transitions the image to <c>PRESENT_SRC_KHR</c>, submits the frame and queues the
/// presentation. When <see cref="TryAcquire"/> returns false (window minimised or swapchain out of date) the caller
/// skips the UI drawing and still calls <see cref="Present"/>, which then only submits the frame; it then calls
/// <see cref="Recreate"/> with the new window size when <see cref="NeedsRecreation"/> is set.</para>
/// <para><b>Synchronisation.</b> One "image acquired" binary semaphore per frame-in-flight slot (reusable once the
/// slot's fence has signalled, which <see cref="VulkanContext.BeginFrame"/> waits for) and one "rendering finished"
/// semaphore per swapchain image (reusable once that image is acquired again, so a semaphore is never re-signalled
/// while a presentation may still wait on it). The frame's submission waits for the acquisition at
/// <c>COLOR_ATTACHMENT_OUTPUT</c>; the layout transition in <see cref="BeginRendering"/> starts at the same stage, which
/// chains it after the wait.</para>
/// <para><b>Recreation</b> (<see cref="Recreate"/>, resize) waits for the device to be idle: it happens only when the
/// window changes, never per frame.</para>
/// </remarks>
public sealed unsafe class VulkanSwapchain : IDisposable
{
    private readonly VulkanContext _context;
    private readonly KhrSurface _surfaceApi;
    private readonly KhrSwapchain _swapchainApi;
    private readonly VulkanSwapchainOptions _options;
    private readonly Semaphore[] _acquired = new Semaphore[VulkanContext.FramesInFlight];
    private Image[] _images = Array.Empty<Image>();
    private ImageView[] _views = Array.Empty<ImageView>();
    private Semaphore[] _renderFinished = Array.Empty<Semaphore>();
    private SwapchainKHR _swapchain;
    private AcquireState _state;
    private VulkanFrame? _acquireFrame;
    private ulong _acquireFrameNumber;
    private uint _imageIndex;
    private bool _disposed;

    /// <summary>Creates the swapchain on <paramref name="surface"/> with the given window size.</summary>
    /// <param name="context">Context created with the windowing system's surface extensions (<c>VK_KHR_surface</c> …).</param>
    /// <param name="surface">The <c>VkSurfaceKHR</c> handle. The swapchain does not take ownership of it.</param>
    /// <param name="width">Framebuffer width in pixels (used when the surface lets the application choose).</param>
    /// <param name="height">Framebuffer height in pixels.</param>
    /// <param name="options">Options; null = defaults (vsync, UNORM format).</param>
    /// <exception cref="VulkanException">The context has no swapchain support or its queue cannot present to the surface.</exception>
    public VulkanSwapchain(VulkanContext context, ulong surface, int width, int height, VulkanSwapchainOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.ThrowIfDisposed();
        if (surface == 0)
            throw new ArgumentException("A VkSurfaceKHR handle is required.", nameof(surface));
        if (context.SurfaceApi is null || context.SwapchainApi is null)
            throw new VulkanException("The Vulkan context was created without VK_KHR_surface; pass the windowing system's instance extensions " +
                                      "in VulkanContextOptions.InstanceExtensions.");
        _context = context;
        _surfaceApi = context.SurfaceApi;
        _swapchainApi = context.SwapchainApi;
        _options = options ?? new VulkanSwapchainOptions();
        Surface = new SurfaceKHR(surface);

        Bool32 supported = false;
        VkUtil.Check(_surfaceApi.GetPhysicalDeviceSurfaceSupport(context.PhysicalDevice, context.QueueFamilyIndex, Surface, &supported),
            "vkGetPhysicalDeviceSurfaceSupportKHR");
        if (!supported)
            throw new VulkanException($"The queue family of {context.Info.DeviceName} cannot present to this surface; create the context with " +
                                      "VulkanContextOptions.CreateSurface so the device is chosen for it.");

        try
        {
            var semaphoreInfo = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };
            for (int i = 0; i < _acquired.Length; i++)
            {
                Semaphore semaphore;
                VkUtil.Check(context.Vk.CreateSemaphore(context.Device, &semaphoreInfo, null, &semaphore), "vkCreateSemaphore");
                _acquired[i] = semaphore;
                context.SetDebugName(ObjectType.Semaphore, semaphore.Handle, $"swapchain image acquired {i}");
            }

            Build(width, height);
        }
        catch
        {
            DestroyAll();
            throw;
        }
    }

    private enum AcquireState
    {
        None,
        Acquired,
        Rendering,
        Rendered,
    }

    /// <summary>The surface (owned by the caller).</summary>
    public SurfaceKHR Surface { get; }

    /// <summary>The swapchain handle (null handle while the window is minimised).</summary>
    public SwapchainKHR Handle => _swapchain;

    /// <summary>Image format (the colour attachment format for Dear ImGui's dynamic-rendering pipeline).</summary>
    public Format Format { get; private set; }

    /// <summary>Colour space of the images.</summary>
    public ColorSpaceKHR ColorSpace { get; private set; }

    /// <summary>Presentation mode in use.</summary>
    public PresentModeKHR PresentMode { get; private set; }

    /// <summary>Current width in pixels (0 while minimised).</summary>
    public int Width { get; private set; }

    /// <summary>Current height in pixels (0 while minimised).</summary>
    public int Height { get; private set; }

    /// <summary>Number of swapchain images (<c>ImGui_ImplVulkan_InitInfo.ImageCount</c>).</summary>
    public int ImageCount => _images.Length;

    /// <summary>Minimum image count of the surface (<c>ImGui_ImplVulkan_InitInfo.MinImageCount</c>).</summary>
    public int MinImageCount { get; private set; }

    /// <summary>Increments on every (re)creation.</summary>
    public int Version { get; private set; }

    /// <summary>
    /// True when the last acquisition or presentation reported the swapchain out of date or suboptimal; call
    /// <see cref="Recreate"/> with the current framebuffer size.
    /// </summary>
    public bool NeedsRecreation { get; private set; }

    /// <summary>The views of the swapchain images (index = <see cref="SwapchainImage.Index"/>).</summary>
    public ReadOnlySpan<ImageView> ImageViews => _views;

    /// <summary>
    /// Recreates the swapchain for a new framebuffer size (waits for the device to be idle). A size of 0 (minimised
    /// window) releases the images until a later call with a non-zero size.
    /// </summary>
    public void Recreate(int width, int height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_state != AcquireState.None)
            throw new InvalidOperationException("Cannot recreate the swapchain between TryAcquire and Present.");
        _context.WaitIdle();
        Build(width, height);
    }

    /// <summary>
    /// Acquires the next image for <paramref name="frame"/>. Returns false (and nothing is acquired) when the window
    /// is minimised or the swapchain is out of date; <see cref="Present"/> must still be called to submit the frame.
    /// </summary>
    public bool TryAcquire(VulkanFrame frame, out SwapchainImage image)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(frame);
        if (!ReferenceEquals(_context.CurrentFrame, frame))
            throw new InvalidOperationException("TryAcquire needs the frame that is being recorded.");
        if (_state != AcquireState.None)
            throw new InvalidOperationException("An image is already acquired; present it first.");
        image = default;
        if (_swapchain.Handle == 0)
            return false;

        uint index;
        var result = _swapchainApi.AcquireNextImage(_context.Device, _swapchain, ulong.MaxValue, _acquired[frame.SlotIndex], default, &index);
        if (result == Result.ErrorOutOfDateKhr)
        {
            NeedsRecreation = true;
            return false;
        }

        if (result == Result.SuboptimalKhr)
            NeedsRecreation = true; // the semaphore is signalled: use the image, recreate after presenting
        else
            VkUtil.Check(result, "vkAcquireNextImageKHR");

        _imageIndex = index;
        _acquireFrame = frame;
        _acquireFrameNumber = frame.Number;
        _state = AcquireState.Acquired;
        image = new SwapchainImage((int)index, _images[index], _views[index], Format, Width, Height);
        return true;
    }

    /// <summary>
    /// Transitions the acquired image to <c>COLOR_ATTACHMENT_OPTIMAL</c> and begins dynamic rendering into it,
    /// clearing it to <paramref name="clearColor"/> (values as stored in the UNORM image, i.e. sRGB-encoded).
    /// </summary>
    public void BeginRendering(VulkanFrame frame, Vector4 clearColor)
    {
        RequireState(frame, AcquireState.Acquired, nameof(BeginRendering));
        var vk = _context.Vk;
        var cmd = frame.CommandBuffer;
        var barrier = new ImageMemoryBarrier2
        {
            SType = StructureType.ImageMemoryBarrier2,
            // Starts at the stage the submission waits for the acquisition, which chains the transition after it.
            SrcStageMask = PipelineStageFlags2.ColorAttachmentOutputBit,
            SrcAccessMask = AccessFlags2.None,
            DstStageMask = PipelineStageFlags2.ColorAttachmentOutputBit,
            DstAccessMask = AccessFlags2.ColorAttachmentReadBit | AccessFlags2.ColorAttachmentWriteBit,
            OldLayout = ImageLayout.Undefined,
            NewLayout = ImageLayout.ColorAttachmentOptimal,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = _images[_imageIndex],
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
        };
        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            ImageMemoryBarrierCount = 1,
            PImageMemoryBarriers = &barrier,
        };
        vk.CmdPipelineBarrier2(cmd, &dependency);

        var attachment = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = _views[_imageIndex],
            ImageLayout = ImageLayout.ColorAttachmentOptimal,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            ClearValue = new ClearValue(new ClearColorValue(clearColor.X, clearColor.Y, clearColor.Z, clearColor.W)),
        };
        var rendering = new RenderingInfo
        {
            SType = StructureType.RenderingInfo,
            RenderArea = new Rect2D(new Offset2D(0, 0), new Extent2D((uint)Width, (uint)Height)),
            LayerCount = 1,
            ColorAttachmentCount = 1,
            PColorAttachments = &attachment,
        };
        vk.CmdBeginRendering(cmd, &rendering);
        var viewport = new Viewport(0f, 0f, Width, Height, 0f, 1f);
        var scissor = new Rect2D(new Offset2D(0, 0), new Extent2D((uint)Width, (uint)Height));
        vk.CmdSetViewport(cmd, 0, 1, &viewport);
        vk.CmdSetScissor(cmd, 0, 1, &scissor);
        _state = AcquireState.Rendering;
    }

    /// <summary>Ends the dynamic rendering begun by <see cref="BeginRendering"/>.</summary>
    public void EndRendering(VulkanFrame frame)
    {
        RequireState(frame, AcquireState.Rendering, nameof(EndRendering));
        _context.Vk.CmdEndRendering(frame.CommandBuffer);
        _state = AcquireState.Rendered;
    }

    /// <summary>
    /// Submits <paramref name="frame"/> and, when an image was acquired, presents it: transition to
    /// <c>PRESENT_SRC_KHR</c>, submission waiting for the acquisition and signalling the image's "rendering finished"
    /// semaphore, <c>vkQueuePresentKHR</c>. Returns false when the swapchain should be recreated
    /// (<see cref="NeedsRecreation"/>).
    /// </summary>
    public bool Present(VulkanFrame frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(frame);
        if (_state == AcquireState.None)
        {
            _context.SubmitFrame(frame);
            return !NeedsRecreation && _swapchain.Handle != 0;
        }

        if (!ReferenceEquals(frame, _acquireFrame) || frame.Number != _acquireFrameNumber)
            throw new InvalidOperationException("Present needs the frame the image was acquired for.");
        if (_state == AcquireState.Rendering)
            throw new InvalidOperationException("EndRendering must be called before Present.");

        var vk = _context.Vk;
        var barrier = new ImageMemoryBarrier2
        {
            SType = StructureType.ImageMemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.ColorAttachmentOutputBit,
            SrcAccessMask = _state == AcquireState.Rendered ? AccessFlags2.ColorAttachmentWriteBit : AccessFlags2.None,
            // The transition must complete inside the first scope of the "rendering finished" semaphore signal
            // (ALL_COMMANDS below), so its destination stage is a real stage rather than NONE; presentation itself is
            // ordered by the semaphore and needs no access mask.
            DstStageMask = PipelineStageFlags2.ColorAttachmentOutputBit,
            DstAccessMask = AccessFlags2.None,
            OldLayout = _state == AcquireState.Rendered ? ImageLayout.ColorAttachmentOptimal : ImageLayout.Undefined,
            NewLayout = ImageLayout.PresentSrcKhr,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = _images[_imageIndex],
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
        };
        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            ImageMemoryBarrierCount = 1,
            PImageMemoryBarriers = &barrier,
        };
        vk.CmdPipelineBarrier2(frame.CommandBuffer, &dependency);

        var finished = _renderFinished[_imageIndex];
        Span<SemaphoreSubmitInfo> waits = stackalloc SemaphoreSubmitInfo[1];
        waits[0] = new SemaphoreSubmitInfo
        {
            SType = StructureType.SemaphoreSubmitInfo,
            Semaphore = _acquired[frame.SlotIndex],
            StageMask = PipelineStageFlags2.ColorAttachmentOutputBit,
        };
        Span<SemaphoreSubmitInfo> signals = stackalloc SemaphoreSubmitInfo[1];
        signals[0] = new SemaphoreSubmitInfo
        {
            SType = StructureType.SemaphoreSubmitInfo,
            Semaphore = finished,
            StageMask = PipelineStageFlags2.AllCommandsBit,
        };
        _state = AcquireState.None;
        _acquireFrame = null;
        _context.SubmitFrame(frame, waits, signals);

        var swapchain = _swapchain;
        uint index = _imageIndex;
        var present = new PresentInfoKHR
        {
            SType = StructureType.PresentInfoKhr,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &finished,
            SwapchainCount = 1,
            PSwapchains = &swapchain,
            PImageIndices = &index,
        };
        var result = _swapchainApi.QueuePresent(_context.Queue, &present);
        if (result is Result.ErrorOutOfDateKhr or Result.SuboptimalKhr)
        {
            NeedsRecreation = true;
            return false;
        }

        VkUtil.Check(result, "vkQueuePresentKHR");
        return !NeedsRecreation;
    }

    /// <summary>Waits for the device and destroys the swapchain, its views and semaphores (not the surface).</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_context.IsDisposed)
            return;
        _context.WaitIdle();
        DestroyAll();
    }

    private void RequireState(VulkanFrame frame, AcquireState expected, string operation)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(frame);
        if (_state != expected || !ReferenceEquals(frame, _acquireFrame) || frame.Number != _acquireFrameNumber)
            throw new InvalidOperationException($"{operation} is not valid now (state {_state}); acquire an image for this frame first.");
    }

    private void Build(int width, int height)
    {
        var vk = _context.Vk;
        var physical = _context.PhysicalDevice;
        SurfaceCapabilitiesKHR caps;
        VkUtil.Check(_surfaceApi.GetPhysicalDeviceSurfaceCapabilities(physical, Surface, &caps), "vkGetPhysicalDeviceSurfaceCapabilitiesKHR");

        // The surface either dictates the extent (most platforms: the window size) or lets the application choose it.
        Extent2D extent;
        if (caps.CurrentExtent.Width != uint.MaxValue)
            extent = caps.CurrentExtent;
        else if (width <= 0 || height <= 0)
            extent = default;
        else
        {
            extent = new Extent2D(
                Math.Clamp((uint)width, caps.MinImageExtent.Width, caps.MaxImageExtent.Width),
                Math.Clamp((uint)height, caps.MinImageExtent.Height, caps.MaxImageExtent.Height));
        }

        var old = _swapchain;
        DestroyImageResources();
        if (extent.Width == 0 || extent.Height == 0)
        {
            // Minimised: no swapchain until the window has a size again.
            if (old.Handle != 0)
                _swapchainApi.DestroySwapchain(_context.Device, old, null);
            _swapchain = default;
            Width = Height = 0;
            NeedsRecreation = false;
            Version++;
            return;
        }

        var (format, colorSpace) = ChooseFormat(physical);
        var presentMode = ChoosePresentMode(physical);
        uint imageCount = caps.MinImageCount + 1;
        if (caps.MaxImageCount > 0)
            imageCount = Math.Min(imageCount, caps.MaxImageCount);
        var usage = ImageUsageFlags.ColorAttachmentBit | (_options.AdditionalUsage & caps.SupportedUsageFlags);
        var alpha = CompositeAlphaFlagsKHR.OpaqueBitKhr;
        foreach (var candidate in new[]
                 {
                     CompositeAlphaFlagsKHR.OpaqueBitKhr, CompositeAlphaFlagsKHR.InheritBitKhr, CompositeAlphaFlagsKHR.PreMultipliedBitKhr,
                     CompositeAlphaFlagsKHR.PostMultipliedBitKhr,
                 })
        {
            if ((caps.SupportedCompositeAlpha & candidate) != 0)
            {
                alpha = candidate;
                break;
            }
        }

        var info = new SwapchainCreateInfoKHR
        {
            SType = StructureType.SwapchainCreateInfoKhr,
            Surface = Surface,
            MinImageCount = imageCount,
            ImageFormat = format,
            ImageColorSpace = colorSpace,
            ImageExtent = extent,
            ImageArrayLayers = 1,
            ImageUsage = usage,
            ImageSharingMode = SharingMode.Exclusive,
            PreTransform = caps.CurrentTransform,
            CompositeAlpha = alpha,
            PresentMode = presentMode,
            Clipped = true,
            OldSwapchain = old,
        };
        SwapchainKHR swapchain;
        var result = _swapchainApi.CreateSwapchain(_context.Device, &info, null, &swapchain);
        if (old.Handle != 0)
            _swapchainApi.DestroySwapchain(_context.Device, old, null);
        _swapchain = default;
        VkUtil.Check(result, "vkCreateSwapchainKHR");
        _swapchain = swapchain;
        _context.SetDebugName(ObjectType.SwapchainKhr, swapchain.Handle, "editor swapchain");

        uint count = 0;
        VkUtil.Check(_swapchainApi.GetSwapchainImages(_context.Device, swapchain, &count, null), "vkGetSwapchainImagesKHR");
        var images = new Image[count];
        fixed (Image* p = images)
            VkUtil.Check(_swapchainApi.GetSwapchainImages(_context.Device, swapchain, &count, p), "vkGetSwapchainImagesKHR");
        _images = images;
        _views = new ImageView[count];
        _renderFinished = new Semaphore[count];
        var semaphoreInfo = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };
        for (int i = 0; i < count; i++)
        {
            var viewInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = images[i],
                ViewType = ImageViewType.Type2D,
                Format = format,
                SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
            };
            ImageView view;
            VkUtil.Check(vk.CreateImageView(_context.Device, &viewInfo, null, &view), "vkCreateImageView (swapchain)");
            _views[i] = view;
            Semaphore semaphore;
            VkUtil.Check(vk.CreateSemaphore(_context.Device, &semaphoreInfo, null, &semaphore), "vkCreateSemaphore");
            _renderFinished[i] = semaphore;
            _context.SetDebugName(ObjectType.Image, images[i].Handle, $"swapchain image {i}");
            _context.SetDebugName(ObjectType.Semaphore, semaphore.Handle, $"swapchain rendering finished {i}");
        }

        Format = format;
        ColorSpace = colorSpace;
        PresentMode = presentMode;
        Width = (int)extent.Width;
        Height = (int)extent.Height;
        MinImageCount = (int)caps.MinImageCount;
        NeedsRecreation = false;
        Version++;
        _context.Log(VulkanLogLevel.Info, $"Swapchain {Width} x {Height}, {count} images, {format}, {presentMode}.");
    }

    private (Format Format, ColorSpaceKHR ColorSpace) ChooseFormat(PhysicalDevice physical)
    {
        uint count = 0;
        VkUtil.Check(_surfaceApi.GetPhysicalDeviceSurfaceFormats(physical, Surface, &count, null), "vkGetPhysicalDeviceSurfaceFormatsKHR");
        var formats = new SurfaceFormatKHR[count];
        fixed (SurfaceFormatKHR* p = formats)
            VkUtil.Check(_surfaceApi.GetPhysicalDeviceSurfaceFormats(physical, Surface, &count, p), "vkGetPhysicalDeviceSurfaceFormatsKHR");
        if (count == 0)
            throw new VulkanException("The surface reports no formats.");
        if (count == 1 && formats[0].Format == Format.Undefined)
            return (_options.PreferredFormat ?? Format.B8G8R8A8Unorm, formats[0].ColorSpace);

        Span<Format> wanted = stackalloc Format[3];
        int n = 0;
        if (_options.PreferredFormat is { } preferred)
            wanted[n++] = preferred;
        wanted[n++] = Format.B8G8R8A8Unorm;
        wanted[n++] = Format.R8G8B8A8Unorm;
        foreach (var w in wanted[..n])
        {
            foreach (var f in formats)
            {
                if (f.Format == w && f.ColorSpace == ColorSpaceKHR.SpaceSrgbNonlinearKhr)
                    return (f.Format, f.ColorSpace);
            }
        }

        foreach (var f in formats)
        {
            if (f.ColorSpace == ColorSpaceKHR.SpaceSrgbNonlinearKhr)
                return (f.Format, f.ColorSpace);
        }

        return (formats[0].Format, formats[0].ColorSpace);
    }

    private PresentModeKHR ChoosePresentMode(PhysicalDevice physical)
    {
        if (_options.VSync)
            return PresentModeKHR.FifoKhr;
        uint count = 0;
        VkUtil.Check(_surfaceApi.GetPhysicalDeviceSurfacePresentModes(physical, Surface, &count, null), "vkGetPhysicalDeviceSurfacePresentModesKHR");
        var modes = new PresentModeKHR[count];
        fixed (PresentModeKHR* p = modes)
            VkUtil.Check(_surfaceApi.GetPhysicalDeviceSurfacePresentModes(physical, Surface, &count, p), "vkGetPhysicalDeviceSurfacePresentModesKHR");
        if (Array.IndexOf(modes, PresentModeKHR.MailboxKhr) >= 0)
            return PresentModeKHR.MailboxKhr;
        if (Array.IndexOf(modes, PresentModeKHR.ImmediateKhr) >= 0)
            return PresentModeKHR.ImmediateKhr;
        return PresentModeKHR.FifoKhr;
    }

    private void DestroyImageResources()
    {
        var vk = _context.Vk;
        foreach (var view in _views)
        {
            if (view.Handle != 0)
                vk.DestroyImageView(_context.Device, view, null);
        }

        foreach (var semaphore in _renderFinished)
        {
            if (semaphore.Handle != 0)
                vk.DestroySemaphore(_context.Device, semaphore, null);
        }

        _views = Array.Empty<ImageView>();
        _renderFinished = Array.Empty<Semaphore>();
        _images = Array.Empty<Image>();
    }

    private void DestroyAll()
    {
        DestroyImageResources();
        if (_swapchain.Handle != 0)
            _swapchainApi.DestroySwapchain(_context.Device, _swapchain, null);
        _swapchain = default;
        for (int i = 0; i < _acquired.Length; i++)
        {
            if (_acquired[i].Handle != 0)
                _context.Vk.DestroySemaphore(_context.Device, _acquired[i], null);
            _acquired[i] = default;
        }
    }
}
