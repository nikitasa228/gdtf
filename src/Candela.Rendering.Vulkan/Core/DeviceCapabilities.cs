using Silk.NET.Vulkan;

namespace Candela.Rendering.Vulkan;

/// <summary>
/// What the selected physical device offers beyond the required Vulkan 1.3 baseline, plus the limits the renderer
/// sizes its resources by. Optional features are enabled on the logical device exactly when they are reported here.
/// </summary>
public sealed record DeviceCapabilities
{
    /// <summary>Device name as reported by the driver.</summary>
    public required string DeviceName { get; init; }

    /// <summary>Driver name (e.g. "NVIDIA", "radv", "llvmpipe").</summary>
    public required string DriverName { get; init; }

    /// <summary>Driver version/info string.</summary>
    public required string DriverInfo { get; init; }

    /// <summary>Device API version.</summary>
    public required uint ApiVersion { get; init; }

    /// <summary>Kind of device (discrete, integrated, virtual, CPU).</summary>
    public required PhysicalDeviceType DeviceType { get; init; }

    /// <summary>PCI vendor id.</summary>
    public required uint VendorId { get; init; }

    /// <summary>Size of all device-local heaps, bytes.</summary>
    public required ulong DeviceLocalMemoryBytes { get; init; }

    /// <summary><c>fillModeNonSolid</c>: wireframe through <c>VK_POLYGON_MODE_LINE</c>.</summary>
    public required bool FillModeNonSolid { get; init; }

    /// <summary><c>wideLines</c>: line widths other than 1.</summary>
    public required bool WideLines { get; init; }

    /// <summary><c>samplerAnisotropy</c> and the maximum anisotropy.</summary>
    public required bool SamplerAnisotropy { get; init; }

    /// <summary>Maximum sampler anisotropy (1 when unsupported).</summary>
    public required float MaxSamplerAnisotropy { get; init; }

    /// <summary>Ray query + acceleration structures + buffer device address are all available (and enabled).</summary>
    public required bool RayTracing { get; init; }

    /// <summary><c>bufferDeviceAddress</c> is enabled (implied by <see cref="RayTracing"/>).</summary>
    public required bool BufferDeviceAddress { get; init; }

    /// <summary><c>VK_KHR_swapchain</c> is enabled (the caller asked for surface extensions).</summary>
    public required bool Swapchain { get; init; }

    /// <summary>The graphics queue supports timestamps (GPU pass timings).</summary>
    public required bool Timestamps { get; init; }

    /// <summary>Nanoseconds per timestamp tick.</summary>
    public required float TimestampPeriod { get; init; }

    /// <summary>Size of sampled-image descriptors usable in the bindless array (update-after-bind limits).</summary>
    public required uint MaxBindlessTextures { get; init; }

    /// <summary>Minimum alignment of storage buffer offsets.</summary>
    public required ulong MinStorageBufferOffsetAlignment { get; init; }

    /// <summary>Minimum alignment of uniform buffer offsets.</summary>
    public required ulong MinUniformBufferOffsetAlignment { get; init; }

    /// <summary>Granularity between linear and optimal resources in one allocation.</summary>
    public required ulong BufferImageGranularity { get; init; }

    /// <summary>Atom size for flushing non-coherent mapped memory.</summary>
    public required ulong NonCoherentAtomSize { get; init; }

    /// <summary>Optimal alignment of buffer offsets for buffer → image copies.</summary>
    public required ulong OptimalBufferCopyOffsetAlignment { get; init; }

    /// <summary>Maximum 2D image size.</summary>
    public required uint MaxImageDimension2D { get; init; }

    /// <summary>Maximum number of device memory allocations.</summary>
    public required uint MaxMemoryAllocationCount { get; init; }

    /// <summary>Maximum compute work group count in X/Y/Z.</summary>
    public required uint MaxComputeWorkGroupCountX { get; init; }

    /// <summary>Range of line widths (wide lines).</summary>
    public required float MaxLineWidth { get; init; }

    /// <summary>Pipeline cache UUID of the driver (keys the persisted pipeline cache).</summary>
    public required Guid PipelineCacheUuid { get; init; }

    /// <summary>Software rasteriser (lavapipe, SwiftShader, WARP-like).</summary>
    public bool IsSoftware => DeviceType == PhysicalDeviceType.Cpu;
}
