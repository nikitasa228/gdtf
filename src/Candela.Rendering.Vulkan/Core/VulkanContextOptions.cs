namespace Candela.Rendering.Vulkan;

/// <summary>Severity of a message from the Vulkan backend (validation layer, device selection, shader compiler).</summary>
public enum VulkanLogLevel
{
    /// <summary>Verbose diagnostics.</summary>
    Debug,

    /// <summary>Informational (selected device, enabled features).</summary>
    Info,

    /// <summary>Something is suboptimal or a fallback is used.</summary>
    Warning,

    /// <summary>A validation error or failure.</summary>
    Error,
}

/// <summary>Options for <see cref="VulkanContext.Create"/>.</summary>
public sealed class VulkanContextOptions
{
    /// <summary>Name of the environment variable that enables validation when <see cref="EnableValidation"/> is null.</summary>
    public const string ValidationEnvironmentVariable = "CANDELA_VK_VALIDATION";

    /// <summary>
    /// Enables <c>VK_LAYER_KHRONOS_validation</c> and the <c>VK_EXT_debug_utils</c> messenger when they are installed.
    /// Null = enabled when the environment variable <c>CANDELA_VK_VALIDATION=1</c> is set.
    /// </summary>
    public bool? EnableValidation { get; set; }

    /// <summary>
    /// Also enables the validation layer's synchronization validation (hazard detection for barriers). Slower; used by
    /// the tests. Ignored without validation.
    /// </summary>
    public bool SynchronizationValidation { get; set; }

    /// <summary>
    /// Instance extensions the caller needs, e.g. the surface extensions reported by the windowing system
    /// (<c>glfwGetRequiredInstanceExtensions</c>). When <c>VK_KHR_surface</c> is among them the device is created with
    /// <c>VK_KHR_swapchain</c>.
    /// </summary>
    public IReadOnlyList<string> InstanceExtensions { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Optional surface factory called right after the instance is created (argument: the <c>VkInstance</c> handle;
    /// result: a <c>VkSurfaceKHR</c> handle, 0 = none). When it returns a surface, only devices whose graphics queue
    /// can present to it are considered. The context does not take ownership of the surface.
    /// </summary>
    public Func<nint, ulong>? CreateSurface { get; set; }

    /// <summary>Substring of the preferred device name (case-insensitive), e.g. <c>AppSettings.PreferredGpu</c>. Empty = automatic.</summary>
    public string PreferredDeviceName { get; set; } = "";

    /// <summary>Enables ray query + acceleration structures when the device supports them (needed by the stage-3 path tracer).</summary>
    public bool EnableRayTracing { get; set; } = true;

    /// <summary>Directory of the persisted pipeline cache and SPIR-V cache. Null = <c>ConfigPaths.CacheDir</c>.</summary>
    public string? CacheDirectory { get; set; }

    /// <summary>Receives log messages (validation messages included). Null = warnings and errors go to standard error.</summary>
    public Action<VulkanLogLevel, string>? Log { get; set; }

    /// <summary>Lowest level passed to <see cref="Log"/>.</summary>
    public VulkanLogLevel MinimumLogLevel { get; set; } = VulkanLogLevel.Warning;

    /// <summary>Application name reported to the driver.</summary>
    public string ApplicationName { get; set; } = "Candela";

    /// <summary>Whether validation is requested, resolving the environment variable.</summary>
    internal bool ValidationRequested =>
        EnableValidation ?? Environment.GetEnvironmentVariable(ValidationEnvironmentVariable) is "1" or "true" or "TRUE" or "True";
}
