using System.Runtime.InteropServices;
using Candela.App.Diagnostics;
using Candela.Rendering.Vulkan;
using Hexa.NET.ImGui;
using Hexa.NET.ImGui.Backends.GLFW;
using Hexa.NET.ImGui.Backends.Vulkan;
using Hexa.NET.ImGuizmo;
using Silk.NET.Vulkan;
using HexaVk = Hexa.NET.ImGui.Backends.Vulkan;

namespace Candela.App.Shell;

/// <summary>
/// Dear ImGui for the editor: the context (docking always on; multi-viewport — panels torn off into their own OS
/// windows — unless disabled), the native GLFW platform backend and the Vulkan renderer backend on the shared context
/// (dynamic rendering into the main swapchain; the backend creates the swapchains of secondary windows itself), and
/// registration of render-target images as ImGui textures. The GLFW, Vulkan and ImGuizmo native libraries each carry
/// their own copy of ImGui's code, so the context is handed to each of them. Main thread only.
/// </summary>
internal sealed unsafe class ImGuiHost : IDisposable
{
    /// <summary>Vulkan API version reported to the backend (core dynamic rendering).</summary>
    private const uint VulkanApi13 = (1u << 22) | (3u << 12);

    /// <summary>Combined image samplers in the backend's descriptor pool (font atlas + one per registered texture).</summary>
    private const uint DescriptorPoolSize = 512;

    private readonly VulkanContext _context;
    private readonly AppLog _log;
    private ImGuiContextPtr _imgui;
    private uint* _colorFormats;
    private bool _glfwReady;
    private bool _vulkanReady;
    private bool _disposed;

    private ImGuiHost(VulkanContext context, AppLog log, bool multiViewport)
    {
        _context = context;
        _log = log;
        MultiViewport = multiViewport;
    }

    /// <summary>Whether torn-off panels become separate OS windows.</summary>
    public bool MultiViewport { get; }

    /// <summary>The ImGui context.</summary>
    public ImGuiContextPtr Context => _imgui;

    /// <summary>Creates the context and initialises both backends for <paramref name="window"/> and its swapchain.</summary>
    public static ImGuiHost Create(MainWindow window, GpuDevice gpu, bool multiViewport, AppLog log)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(gpu);
        var host = new ImGuiHost(gpu.Context, log, multiViewport);
        try
        {
            host.Initialize(window, gpu);
            return host;
        }
        catch
        {
            host.Dispose();
            throw;
        }
    }

    /// <summary>Starts a UI frame (backends first, then ImGui and ImGuizmo).</summary>
    public void NewFrame()
    {
        ImGuiImplVulkan.NewFrame();
        ImGuiImplGLFW.NewFrame();
        ImGui.NewFrame();
        ImGuizmo.BeginFrame();
    }

    /// <summary>Records the main viewport's draw data into <paramref name="commandBuffer"/> (inside dynamic rendering).</summary>
    public void RenderDrawData(CommandBuffer commandBuffer)
    {
        var drawData = ImGui.GetDrawData();
        if (drawData.IsNull)
            return;
        ImGuiImplVulkan.RenderDrawData(drawData, new VkCommandBuffer(commandBuffer.Handle), default);
    }

    /// <summary>Updates and renders the secondary OS windows of torn-off panels (after the main window was presented).</summary>
    public void RenderPlatformWindows()
    {
        if (!MultiViewport)
            return;
        ImGui.UpdatePlatformWindows();
        ImGui.RenderPlatformWindowsDefault();
    }

    /// <summary>Registers a sampled image as an ImGui texture; returns its id for <c>ImGui.Image</c>.</summary>
    public ImTextureID AddTexture(Sampler sampler, ImageView view, ImageLayout layout)
    {
        var set = ImGuiImplVulkan.AddTexture(new VkSampler((nint)sampler.Handle), new VkImageView((nint)view.Handle), new VkImageLayout((nint)layout));
        return new ImTextureID((ulong)(nint)GetHandle(set));
    }

    /// <summary>
    /// Releases a texture registered with <see cref="AddTexture"/> once no frame in flight can still sample it (the
    /// descriptor set is freed through the context's deferred deletion queue).
    /// </summary>
    public void RemoveTextureDeferred(ImTextureID texture)
    {
        ulong handle = (ulong)texture;
        if (handle == 0 || _disposed)
            return;
        _context.DeferDestroy(() =>
        {
            if (_vulkanReady)
                ImGuiImplVulkan.RemoveTexture(new VkDescriptorSet((nint)handle));
        });
    }

    /// <summary>Shuts the backends down and destroys the context (the GPU must be idle).</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (!_context.IsDisposed)
        {
            _context.WaitIdle();
            // Descriptor sets of viewport textures were queued for deferred removal; free them while the backend lives.
            _context.WaitForSubmittedFrames();
        }

        if (!_imgui.IsNull)
            ImGui.SetCurrentContext(_imgui);
        if (_vulkanReady)
        {
            if (MultiViewport)
                ImGui.DestroyPlatformWindows();
            ImGuiImplVulkan.Shutdown();
            _vulkanReady = false;
        }

        if (_glfwReady)
        {
            ImGuiImplGLFW.Shutdown();
            _glfwReady = false;
        }

        if (!_imgui.IsNull)
        {
            ImGui.DestroyContext(_imgui);
            _imgui = default;
        }

        if (_colorFormats != null)
        {
            NativeMemory.Free(_colorFormats);
            _colorFormats = null;
        }
    }

    private static nint GetHandle(HexaVk.VkDescriptorSet set) => *(nint*)&set;

    private void Initialize(MainWindow window, GpuDevice gpu)
    {
        _imgui = ImGui.CreateContext();
        ImGui.SetCurrentContext(_imgui);
        ImGuiImplGLFW.SetCurrentContext(_imgui);
        ImGuiImplVulkan.SetCurrentContext(_imgui);
        ImGuizmo.SetImGuiContext(_imgui);
        // Larger, bolder gizmos than ImGuizmo's defaults (readable on high-resolution viewports).
        ImGuizmo.SetGizmoSizeClipSpace(0.16f);
        var gizmoStyle = ImGuizmo.GetStyle();
        gizmoStyle.TranslationLineThickness = 4f;
        gizmoStyle.TranslationLineArrowSize = 8f;
        gizmoStyle.RotationLineThickness = 3f;
        gizmoStyle.RotationOuterLineThickness = 3.5f;
        gizmoStyle.CenterCircleSize = 7f;

        var io = ImGui.GetIO();
        io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard | ImGuiConfigFlags.DockingEnable;
        if (MultiViewport)
            io.ConfigFlags |= ImGuiConfigFlags.ViewportsEnable;
        // Layouts are stored by the editor (named layouts + tab styles), not by ImGui's own ini file.
        io.IniFilename = null;
        io.ConfigDpiScaleFonts = true;
        io.ConfigDpiScaleViewports = true;
        io.ConfigWindowsMoveFromTitleBarOnly = true;
        io.ConfigDockingTransparentPayload = true;
        io.ConfigViewportsNoTaskBarIcon = false;
        io.ConfigErrorRecovery = true;
        io.ConfigErrorRecoveryEnableAssert = false;
        io.ConfigErrorRecoveryEnableTooltip = true;

        if (!ImGuiImplGLFW.InitForVulkan(window.BackendHandle, true))
            throw new InvalidOperationException("The Dear ImGui GLFW backend could not be initialised.");
        _glfwReady = true;

        _colorFormats = (uint*)NativeMemory.Alloc((nuint)sizeof(uint));
        _colorFormats[0] = (uint)gpu.Swapchain.Format;
        var info = new ImGuiImplVulkanInitInfo
        {
            ApiVersion = VulkanApi13,
            Instance = new VkInstance(gpu.Context.Instance.Handle),
            PhysicalDevice = new VkPhysicalDevice(gpu.Context.PhysicalDevice.Handle),
            Device = new VkDevice(gpu.Context.Device.Handle),
            QueueFamily = gpu.Context.QueueFamilyIndex,
            Queue = new VkQueue(gpu.Context.Queue.Handle),
            DescriptorPoolSize = DescriptorPoolSize,
            MinImageCount = (uint)Math.Max(2, gpu.Swapchain.MinImageCount),
            ImageCount = (uint)Math.Max(2, Math.Max(gpu.Swapchain.ImageCount, gpu.Swapchain.MinImageCount)),
            MSAASamples = (uint)SampleCountFlags.Count1Bit,
            PipelineCache = new VkPipelineCache((nint)gpu.Context.PipelineCache.Handle),
            UseDynamicRendering = 1,
            PipelineRenderingCreateInfo = new HexaVk.VkPipelineRenderingCreateInfo
            {
                SType = (uint)StructureType.PipelineRenderingCreateInfo,
                ColorAttachmentCount = 1,
                PColorAttachmentFormats = _colorFormats,
            },
        };
        if (!ImGuiImplVulkan.Init(ref info))
            throw new InvalidOperationException("The Dear ImGui Vulkan backend could not be initialised.");
        _vulkanReady = true;
        _log.Write(LogLevel.Debug, "UI", $"Dear ImGui {ImGui.GetVersionS()} ready (multi-viewport {(MultiViewport ? "on" : "off")}).");
    }
}
