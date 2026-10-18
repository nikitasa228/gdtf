using Candela.App.Diagnostics;
using Candela.Rendering.Vulkan;
using Silk.NET.Vulkan;

namespace Candela.App.Shell;

/// <summary>Options for creating the editor's GPU objects.</summary>
/// <param name="PreferredDevice">Substring of the preferred GPU name (empty = automatic).</param>
/// <param name="Validation">Enables the Vulkan validation layer.</param>
/// <param name="CaptureSupport">Adds <c>TRANSFER_SRC</c> to the swapchain images so frames can be copied (screenshots).</param>
internal readonly record struct GpuDeviceOptions(string PreferredDevice, bool Validation, bool CaptureSupport);

/// <summary>
/// The editor's GPU objects for the main window: the shared <see cref="VulkanContext"/> (instance with GLFW's surface
/// extensions, device chosen so it can present to the window), the <see cref="VulkanRenderer"/> every viewport renders
/// with and the main window's <see cref="VulkanSwapchain"/>. Secondary OS windows (torn-off panels) get their swapchains
/// from the Dear ImGui Vulkan backend. Disposal follows docs/rendering.md: swapchain, renderer, surface, context — the
/// owners of render targets and ImGui textures release theirs first.
/// </summary>
internal sealed unsafe class GpuDevice : IDisposable
{
    private bool _disposed;

    private GpuDevice(VulkanContext context, VulkanRenderer renderer, VulkanSwapchain swapchain, ulong surface)
    {
        Context = context;
        Renderer = renderer;
        Swapchain = swapchain;
        Surface = surface;
    }

    /// <summary>The shared Vulkan context.</summary>
    public VulkanContext Context { get; }

    /// <summary>The renderer of all viewports (does not own the context).</summary>
    public VulkanRenderer Renderer { get; }

    /// <summary>The main window's swapchain.</summary>
    public VulkanSwapchain Swapchain { get; }

    /// <summary>The main window's <c>VkSurfaceKHR</c>.</summary>
    public ulong Surface { get; }

    /// <summary>
    /// Creates the context, renderer and swapchain for <paramref name="window"/>. Throws
    /// <see cref="VulkanUnavailableException"/> when no Vulkan 1.3 device can present to the window, and other
    /// <see cref="VulkanException"/>s for driver failures.
    /// </summary>
    public static GpuDevice Create(MainWindow window, GpuDeviceOptions options, AppLog log)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(log);
        var extensions = GlfwPlatform.RequiredVulkanExtensions();
        if (extensions.Count == 0)
            throw new VulkanUnavailableException("GLFW reports no Vulkan surface support (no Vulkan loader or ICD).");

        ulong surface = 0;
        var contextOptions = new VulkanContextOptions
        {
            InstanceExtensions = extensions,
            CreateSurface = instance =>
            {
                surface = GlfwPlatform.CreateSurface(instance, window.Handle);
                return surface;
            },
            PreferredDeviceName = options.PreferredDevice ?? "",
            EnableValidation = options.Validation ? true : null,
            Log = (level, message) => log.Write(Map(level), "Vulkan", message),
            MinimumLogLevel = VulkanLogLevel.Info,
            ApplicationName = "Candela Editor",
        };

        var context = VulkanContext.Create(contextOptions);
        VulkanRenderer? renderer = null;
        try
        {
            if (surface == 0)
                throw new VulkanUnavailableException("No window surface could be created for the main window.");
            renderer = new VulkanRenderer(context);
            var (w, h) = window.FramebufferSize;
            var swapchainOptions = new VulkanSwapchainOptions
            {
                VSync = true,
                AdditionalUsage = options.CaptureSupport ? ImageUsageFlags.TransferSrcBit : 0,
            };
            var swapchain = new VulkanSwapchain(context, surface, w, h, swapchainOptions);
            log.Info("Vulkan", $"Device: {context.Info.DeviceName} ({context.Info.DriverName}, Vulkan {context.Info.ApiVersion})" +
                               (context.Info.IsSoftwareRenderer ? " — software renderer" : ""));
            return new GpuDevice(context, renderer, swapchain, surface);
        }
        catch
        {
            renderer?.Dispose();
            if (surface != 0 && context.SurfaceApi is { } api)
                api.DestroySurface(context.Instance, new SurfaceKHR(surface), null);
            context.Dispose();
            throw;
        }
    }

    /// <summary>Recreates the swapchain for the window's current framebuffer size (waits idle; only on resize).</summary>
    public void RecreateSwapchain(MainWindow window)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var (w, h) = window.FramebufferSize;
        Swapchain.Recreate(w, h);
    }

    /// <summary>Waits for the GPU and destroys the swapchain, the renderer, the surface and the context.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (!Context.IsDisposed)
            Context.WaitIdle();
        Swapchain.Dispose();
        Renderer.Dispose();
        if (Surface != 0 && Context.SurfaceApi is { } api && !Context.IsDisposed)
            api.DestroySurface(Context.Instance, new SurfaceKHR(Surface), null);
        Context.Dispose();
    }

    private static LogLevel Map(VulkanLogLevel level) => level switch
    {
        VulkanLogLevel.Debug => LogLevel.Debug,
        VulkanLogLevel.Info => LogLevel.Info,
        VulkanLogLevel.Warning => LogLevel.Warning,
        _ => LogLevel.Error,
    };
}
