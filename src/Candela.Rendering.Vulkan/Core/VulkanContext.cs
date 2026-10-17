using System.Text;
using Candela.Core.Settings;
using Silk.NET.Core;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.KHR;

namespace Candela.Rendering.Vulkan;

/// <summary>
/// The Vulkan instance, the selected GPU, the logical device with one graphics+compute queue, and the services every
/// GPU module shares: device-memory allocator, staging ring, deferred destruction, frames in flight, pipeline cache.
/// Public so the editor can create its swapchain and Dear ImGui backend on the same device
/// (<see cref="Instance"/>, <see cref="PhysicalDevice"/>, <see cref="Device"/>, <see cref="Queue"/>,
/// <see cref="QueueFamilyIndex"/>). All methods are main-thread only.
/// </summary>
public sealed unsafe class VulkanContext : IDisposable
{
    /// <summary>Frames that may be recorded/executing at the same time.</summary>
    public const int FramesInFlight = 2;

    /// <summary>Initial size of the staging ring.</summary>
    internal const ulong InitialUploadRingSize = 8ul << 20;

    private const string ValidationLayerName = "VK_LAYER_KHRONOS_validation";
    private static readonly uint RequiredApiVersion = VkUtil.MakeVersion(1, 3, 0);

    private readonly Action<VulkanLogLevel, string> _log;
    private readonly VulkanLogLevel _minimumLevel;
    private readonly ValidationSink _sink;
    private DebugMessenger? _messenger;
    private ulong _frameNumber;
    private bool _disposed;

    private VulkanContext(VulkanContextOptions options)
    {
        _minimumLevel = options.MinimumLogLevel;
        var userLog = options.Log;
        _log = userLog is not null
            ? (level, message) =>
            {
                if (level >= _minimumLevel)
                    userLog(level, message);
            }
        : (level, message) =>
            {
                if (level >= _minimumLevel)
                    Console.Error.WriteLine($"[vulkan {level.ToString().ToLowerInvariant()}] {message}");
            };
        _sink = new ValidationSink(_log, VulkanLogLevel.Debug);
        CacheDirectory = options.CacheDirectory ?? ConfigPaths.CacheDir;

        try
        {
            Vk = LoadApi();
            CreateInstance(options);
            if (options.CreateSurface is not null)
                PresentationSurface = options.CreateSurface((nint)Instance.Handle);
            SelectDeviceAndCreate(options);
            Allocator = new DeviceMemoryAllocator(Vk, PhysicalDevice, Device, Limits, Capabilities.BufferDeviceAddress);
            PipelineCacheStore = new PipelineCacheStore(Vk, Device, Properties.VendorID, Properties.DeviceID,
                Capabilities.PipelineCacheUuid, CacheDirectory, _log);
            Deletion = new DeferredDeletionQueue();
            Frames = new FrameScheduler(this);
            Upload = new UploadRing(this, InitialUploadRingSize);
            Immediate = new ImmediateSubmitter(this);
        }
        catch
        {
            DisposeCore();
            throw;
        }
    }

    /// <summary>The Silk.NET Vulkan API object (function table bound to this instance and device).</summary>
    public Vk Vk { get; } = null!;

    /// <summary>The instance.</summary>
    public Instance Instance { get; private set; }

    /// <summary>The selected physical device.</summary>
    public PhysicalDevice PhysicalDevice { get; private set; }

    /// <summary>The logical device.</summary>
    public Device Device { get; private set; }

    /// <summary>The graphics + compute queue every submission goes to (the editor must submit here too).</summary>
    public Queue Queue { get; private set; }

    /// <summary>Family index of <see cref="Queue"/>.</summary>
    public uint QueueFamilyIndex { get; private set; }

    /// <summary>Features and limits of the selected device.</summary>
    public DeviceCapabilities Capabilities { get; private set; } = null!;

    /// <summary>Summary for the settings window.</summary>
    public RendererInfo Info { get; private set; } = null!;

    /// <summary>Device-memory sub-allocator.</summary>
    public DeviceMemoryAllocator Allocator { get; } = null!;

    /// <summary>Pipeline cache (persisted in <see cref="CacheDirectory"/>).</summary>
    public PipelineCache PipelineCache => PipelineCacheStore.Cache;

    /// <summary>Directory for the pipeline cache and SPIR-V cache.</summary>
    public string CacheDirectory { get; }

    /// <summary>Surface created by <see cref="VulkanContextOptions.CreateSurface"/> (0 = none). Not owned by the context.</summary>
    public ulong PresentationSurface { get; private set; }

    /// <summary><c>VK_KHR_surface</c> functions (null without surface extensions).</summary>
    public KhrSurface? SurfaceApi { get; private set; }

    /// <summary><c>VK_KHR_swapchain</c> functions (null when the device was created without swapchain support).</summary>
    public KhrSwapchain? SwapchainApi { get; private set; }

    /// <summary><c>VK_EXT_debug_utils</c> functions (object names, command labels), null when unavailable.</summary>
    public ExtDebugUtils? DebugUtils { get; private set; }

    /// <summary>The validation layer is active.</summary>
    public bool ValidationEnabled { get; private set; }

    /// <summary>Validation errors reported so far (also after <see cref="Dispose"/>, which reports leaked objects).</summary>
    public int ValidationErrorCount => _sink.ErrorCount;

    /// <summary>Validation warnings reported so far.</summary>
    public int ValidationWarningCount => _sink.WarningCount;

    /// <summary>The first validation error messages (at most 64).</summary>
    public IReadOnlyList<string> ValidationErrors => _sink.Errors;

    /// <summary>Number of frames begun so far.</summary>
    public ulong FrameNumber => _frameNumber;

    internal PhysicalDeviceProperties Properties { get; private set; }

    internal PhysicalDeviceLimits Limits => Properties.Limits;

    internal PipelineCacheStore PipelineCacheStore { get; } = null!;

    internal DeferredDeletionQueue Deletion { get; } = null!;

    internal FrameScheduler Frames { get; } = null!;

    internal UploadRing Upload { get; } = null!;

    internal ImmediateSubmitter Immediate { get; } = null!;

    /// <summary>Creates the context. Throws <see cref="VulkanUnavailableException"/> when no suitable device exists.</summary>
    public static VulkanContext Create(VulkanContextOptions? options = null) => new(options ?? new VulkanContextOptions());

    /// <summary>Creates the context, or returns false with the reason (used by tests and the CLI to skip gracefully).</summary>
    public static bool TryCreate(VulkanContextOptions? options, out VulkanContext? context, out string reason)
    {
        try
        {
            context = Create(options);
            reason = "";
            return true;
        }
        catch (VulkanUnavailableException e)
        {
            context = null;
            reason = e.Message;
            return false;
        }
    }

    /// <summary>Writes a message to the context's logger.</summary>
    public void Log(VulkanLogLevel level, string message) => _log(level, message);

    /// <summary>Names a Vulkan object for debuggers and validation messages (no-op without debug utils).</summary>
    public void SetDebugName(ObjectType type, ulong handle, string name)
    {
        if (DebugUtils is null || handle == 0 || string.IsNullOrEmpty(name))
            return;
        int len = Encoding.UTF8.GetByteCount(name);
        Span<byte> bytes = len < 256 ? stackalloc byte[len + 1] : new byte[len + 1];
        Encoding.UTF8.GetBytes(name, bytes);
        bytes[len] = 0;
        fixed (byte* p = bytes)
        {
            var info = new DebugUtilsObjectNameInfoEXT
            {
                SType = StructureType.DebugUtilsObjectNameInfoExt,
                ObjectType = type,
                ObjectHandle = handle,
                PObjectName = p,
            };
            DebugUtils.SetDebugUtilsObjectName(Device, &info);
        }
    }

    /// <summary>
    /// Destroys <paramref name="resource"/> once no frame in flight can use it (see <see cref="DeferredDeletionQueue"/>).
    /// Submissions of the editor on <see cref="Queue"/> are covered because frame fences also order earlier submissions.
    /// </summary>
    public void DeferDispose(IDisposable resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        if (_disposed)
        {
            resource.Dispose();
            return;
        }

        Deletion.Enqueue(resource, _frameNumber);
    }

    /// <summary>Runs <paramref name="destroy"/> once no frame in flight can use the objects it destroys.</summary>
    public void DeferDestroy(Action destroy) => DeferDispose(new DisposeAction(destroy));

    /// <summary>Waits until the device is idle (shutdown, swapchain recreation). Never used per frame.</summary>
    public void WaitIdle()
    {
        if (Device.Handle != 0)
            VkUtil.Check(Vk.DeviceWaitIdle(Device), "vkDeviceWaitIdle");
    }

    /// <summary>Waits for the GPU, destroys everything still owned by the context, then the device and instance.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        DisposeCore();
    }

    internal ulong AdvanceFrameNumber() => ++_frameNumber;

    internal void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private void DisposeCore()
    {
        _disposed = true;
        if (Device.Handle != 0)
        {
            Vk.DeviceWaitIdle(Device);
            Deletion?.DrainAll();
            Immediate?.Dispose();
            Frames?.Dispose();
            Upload?.Dispose();
            Deletion?.DrainAll();
            PipelineCacheStore?.Dispose();
            if (Allocator is not null)
            {
                var stats = Allocator.Statistics;
                if (stats.AllocationCount > 0 || stats.DedicatedCount > 0)
                    _log(VulkanLogLevel.Warning, $"Device memory still allocated at shutdown: {stats}.");
                Allocator.Dispose();
            }

            Vk.DestroyDevice(Device, null);
            Device = default;
        }

        _messenger?.Detach();
        if (Instance.Handle != 0)
        {
            Vk.DestroyInstance(Instance, null);
            Instance = default;
        }

        _messenger?.Dispose();
        _messenger = null;
        Vk?.Dispose();
    }

    private static Vk LoadApi()
    {
        try
        {
            var vk = Vk.GetApi();
            uint version = 0;
            vk.EnumerateInstanceVersion(ref version);
            return vk;
        }
        catch (Exception e) when (e is not VulkanException)
        {
            throw new VulkanUnavailableException($"The Vulkan loader could not be loaded ({e.GetType().Name}: {e.Message}). " +
                                                 "Install a Vulkan driver (GPU vendor driver, or Mesa lavapipe for software rendering).");
        }
    }

    private void CreateInstance(VulkanContextOptions options)
    {
        uint apiVersion = 0;
        Vk.EnumerateInstanceVersion(ref apiVersion);
        if (VkUtil.MajorMinor(apiVersion) < RequiredApiVersion)
            throw new VulkanUnavailableException($"Vulkan loader {VkUtil.VersionString(apiVersion)} is older than the required 1.3.");

        var layers = EnumerateLayers();
        var available = EnumerateInstanceExtensions(null);
        bool validation = options.ValidationRequested && layers.Contains(ValidationLayerName);
        if (options.ValidationRequested && !validation)
            _log(VulkanLogLevel.Warning, "Validation requested but VK_LAYER_KHRONOS_validation is not installed.");

        var extensions = new List<string>();
        foreach (var e in options.InstanceExtensions)
        {
            if (!available.Contains(e))
                throw new VulkanUnavailableException($"Required instance extension {e} is not available.");
            if (!extensions.Contains(e))
                extensions.Add(e);
        }

        bool debugUtils = available.Contains(ExtDebugUtils.ExtensionName);
        if (debugUtils && !extensions.Contains(ExtDebugUtils.ExtensionName))
            extensions.Add(ExtDebugUtils.ExtensionName);
        bool portability = available.Contains("VK_KHR_portability_enumeration");
        if (portability)
            extensions.Add("VK_KHR_portability_enumeration");
        bool syncValidation = false;
        if (validation && options.SynchronizationValidation)
        {
            var layerExtensions = EnumerateInstanceExtensions(ValidationLayerName);
            if (layerExtensions.Contains("VK_EXT_validation_features"))
            {
                extensions.Add("VK_EXT_validation_features");
                syncValidation = true;
            }
        }

        var appName = NativeStringArray.AllocUtf8(options.ApplicationName);
        var engineName = NativeStringArray.AllocUtf8("Candela");
        using var layerNames = new NativeStringArray(validation ? new[] { ValidationLayerName } : Array.Empty<string>());
        using var extensionNames = new NativeStringArray(extensions);
        try
        {
            var app = new ApplicationInfo
            {
                SType = StructureType.ApplicationInfo,
                PApplicationName = appName,
                ApplicationVersion = VkUtil.MakeVersion(0, 2, 0),
                PEngineName = engineName,
                EngineVersion = VkUtil.MakeVersion(0, 2, 0),
                ApiVersion = Vk.Version13,
            };
            var info = new InstanceCreateInfo
            {
                SType = StructureType.InstanceCreateInfo,
                PApplicationInfo = &app,
                EnabledLayerCount = layerNames.Count,
                PpEnabledLayerNames = layerNames.Pointer,
                EnabledExtensionCount = extensionNames.Count,
                PpEnabledExtensionNames = extensionNames.Pointer,
                Flags = portability ? InstanceCreateFlags.EnumeratePortabilityBitKhr : 0,
            };

            DebugUtilsMessengerCreateInfoEXT messengerInfo = default;
            var enables = stackalloc ValidationFeatureEnableEXT[1];
            enables[0] = ValidationFeatureEnableEXT.SynchronizationValidationExt;
            var features = new ValidationFeaturesEXT
            {
                SType = StructureType.ValidationFeaturesExt,
                EnabledValidationFeatureCount = 1,
                PEnabledValidationFeatures = enables,
            };
            if (validation && debugUtils)
            {
                _messenger = new DebugMessenger(_sink);
                messengerInfo = _messenger.CreateInfo;
                info.PNext = &messengerInfo; // covers vkCreateInstance / vkDestroyInstance (leak reports)
            }

            if (syncValidation)
            {
                features.PNext = info.PNext;
                info.PNext = &features;
            }

            Instance instance;
            var result = Vk.CreateInstance(&info, null, &instance);
            if (result != Result.Success)
                throw new VulkanUnavailableException($"vkCreateInstance failed ({result}).");
            Instance = instance;
            ValidationEnabled = validation;
        }
        finally
        {
            System.Runtime.InteropServices.NativeMemory.Free(appName);
            System.Runtime.InteropServices.NativeMemory.Free(engineName);
        }

        if (debugUtils && Vk.TryGetInstanceExtension(Instance, out ExtDebugUtils utils))
        {
            DebugUtils = utils;
            _messenger?.Attach(utils, Instance);
        }

        if (extensions.Contains(KhrSurface.ExtensionName) && Vk.TryGetInstanceExtension(Instance, out KhrSurface surface))
            SurfaceApi = surface;
        _log(VulkanLogLevel.Info, $"Vulkan instance created (validation {(validation ? "on" : "off")}{(syncValidation ? " + sync" : "")}).");
    }

    private HashSet<string> EnumerateLayers()
    {
        uint count = 0;
        Vk.EnumerateInstanceLayerProperties(&count, null);
        var props = new LayerProperties[count];
        fixed (LayerProperties* p = props)
            Vk.EnumerateInstanceLayerProperties(&count, p);
        var set = new HashSet<string>();
        for (int i = 0; i < count; i++)
        {
            fixed (byte* name = props[i].LayerName)
                set.Add(VkUtil.FixedString(name, 256));
        }

        return set;
    }

    private HashSet<string> EnumerateInstanceExtensions(string? layer)
    {
        byte* layerName = layer is null ? null : NativeStringArray.AllocUtf8(layer);
        try
        {
            uint count = 0;
            Vk.EnumerateInstanceExtensionProperties(layerName, &count, null);
            var props = new ExtensionProperties[count];
            fixed (ExtensionProperties* p = props)
                Vk.EnumerateInstanceExtensionProperties(layerName, &count, p);
            var set = new HashSet<string>();
            for (int i = 0; i < count; i++)
            {
                fixed (byte* name = props[i].ExtensionName)
                    set.Add(VkUtil.FixedString(name, 256));
            }

            return set;
        }
        finally
        {
            if (layerName != null)
                System.Runtime.InteropServices.NativeMemory.Free(layerName);
        }
    }

    private HashSet<string> EnumerateDeviceExtensions(PhysicalDevice device)
    {
        uint count = 0;
        Vk.EnumerateDeviceExtensionProperties(device, (byte*)null, &count, null);
        var props = new ExtensionProperties[count];
        fixed (ExtensionProperties* p = props)
            Vk.EnumerateDeviceExtensionProperties(device, (byte*)null, &count, p);
        var set = new HashSet<string>();
        for (int i = 0; i < count; i++)
        {
            fixed (byte* name = props[i].ExtensionName)
                set.Add(VkUtil.FixedString(name, 256));
        }

        return set;
    }

    /// <summary>Everything known about one physical device during selection.</summary>
    private sealed class DeviceProbe
    {
        public PhysicalDevice Device;
        public PhysicalDeviceProperties Properties;
        public PhysicalDeviceVulkan12Properties Properties12;
        public PhysicalDeviceFeatures Features;
        public PhysicalDeviceVulkan11Features Features11;
        public PhysicalDeviceVulkan12Features Features12;
        public PhysicalDeviceVulkan13Features Features13;
        public bool AccelerationStructure;
        public bool RayQuery;
        public HashSet<string> Extensions = new();
        public int QueueFamily = -1;
        public uint TimestampValidBits;
        public ulong DeviceLocalBytes;
        public string Name = "";
        public List<string> Missing = new();
    }

    private void SelectDeviceAndCreate(VulkanContextOptions options)
    {
        uint count = 0;
        Vk.EnumeratePhysicalDevices(Instance, &count, null);
        if (count == 0)
            throw new VulkanUnavailableException("No Vulkan physical device found.");
        var devices = new PhysicalDevice[count];
        fixed (PhysicalDevice* p = devices)
            Vk.EnumeratePhysicalDevices(Instance, &count, p);

        bool wantSwapchain = SurfaceApi is not null;
        var probes = new List<DeviceProbe>();
        var candidates = new List<DeviceCandidate>();
        for (int i = 0; i < devices.Length; i++)
        {
            var probe = Probe(devices[i], wantSwapchain);
            probes.Add(probe);
            candidates.Add(new DeviceCandidate(i, probe.Name, probe.Properties.DeviceType, probe.Properties.ApiVersion,
                probe.DeviceLocalBytes, probe.Missing));
        }

        var chosen = DeviceSelector.Select(candidates, options.PreferredDeviceName);
        if (chosen is null)
        {
            var reasons = string.Join("; ", probes.Select(p => $"{p.Name}: missing {string.Join(", ", p.Missing)}"));
            throw new VulkanUnavailableException($"No Vulkan device meets the requirements (Vulkan 1.3 core features). {reasons}");
        }

        if (!string.IsNullOrWhiteSpace(options.PreferredDeviceName) &&
            !chosen.Name.Contains(options.PreferredDeviceName.Trim(), StringComparison.OrdinalIgnoreCase))
            _log(VulkanLogLevel.Warning, $"Preferred GPU '{options.PreferredDeviceName}' not found or not suitable; using {chosen.Name}.");

        var selected = probes[chosen.Index];
        CreateDevice(selected, options, wantSwapchain);
    }

    private DeviceProbe Probe(PhysicalDevice device, bool wantSwapchain)
    {
        var probe = new DeviceProbe { Device = device, Extensions = EnumerateDeviceExtensions(device) };
        probe.AccelerationStructure = probe.Extensions.Contains(KhrAccelerationStructure.ExtensionName) &&
                                      probe.Extensions.Contains("VK_KHR_deferred_host_operations");
        probe.RayQuery = probe.Extensions.Contains("VK_KHR_ray_query");

        var props12 = new PhysicalDeviceVulkan12Properties { SType = StructureType.PhysicalDeviceVulkan12Properties };
        var props2 = new PhysicalDeviceProperties2 { SType = StructureType.PhysicalDeviceProperties2, PNext = &props12 };
        Vk.GetPhysicalDeviceProperties2(device, &props2);
        probe.Properties = props2.Properties;
        probe.Properties12 = props12;
        probe.Properties12.PNext = null;
        probe.Name = VkUtil.FixedString(props2.Properties.DeviceName, 256);

        var f13 = new PhysicalDeviceVulkan13Features { SType = StructureType.PhysicalDeviceVulkan13Features };
        var f12 = new PhysicalDeviceVulkan12Features { SType = StructureType.PhysicalDeviceVulkan12Features, PNext = &f13 };
        var f11 = new PhysicalDeviceVulkan11Features { SType = StructureType.PhysicalDeviceVulkan11Features, PNext = &f12 };
        var asFeatures = new PhysicalDeviceAccelerationStructureFeaturesKHR { SType = StructureType.PhysicalDeviceAccelerationStructureFeaturesKhr };
        var rqFeatures = new PhysicalDeviceRayQueryFeaturesKHR { SType = StructureType.PhysicalDeviceRayQueryFeaturesKhr };
        if (probe.AccelerationStructure)
        {
            asFeatures.PNext = f13.PNext;
            f13.PNext = &asFeatures;
        }

        if (probe.RayQuery)
        {
            rqFeatures.PNext = f13.PNext;
            f13.PNext = &rqFeatures;
        }

        var features2 = new PhysicalDeviceFeatures2 { SType = StructureType.PhysicalDeviceFeatures2, PNext = &f11 };
        bool api13 = VkUtil.MajorMinor(probe.Properties.ApiVersion) >= RequiredApiVersion;
        if (api13)
            Vk.GetPhysicalDeviceFeatures2(device, &features2);
        probe.Features = features2.Features;
        probe.Features11 = f11;
        probe.Features12 = f12;
        probe.Features13 = f13;
        probe.Features11.PNext = probe.Features12.PNext = probe.Features13.PNext = null;
        probe.AccelerationStructure &= asFeatures.AccelerationStructure;
        probe.RayQuery &= rqFeatures.RayQuery;

        var memory = Vk.GetPhysicalDeviceMemoryProperties(device);
        for (int h = 0; h < memory.MemoryHeapCount; h++)
        {
            if ((memory.MemoryHeaps[h].Flags & MemoryHeapFlags.DeviceLocalBit) != 0)
                probe.DeviceLocalBytes += memory.MemoryHeaps[h].Size;
        }

        uint familyCount = 0;
        Vk.GetPhysicalDeviceQueueFamilyProperties(device, &familyCount, null);
        var families = new QueueFamilyProperties[familyCount];
        fixed (QueueFamilyProperties* fp = families)
            Vk.GetPhysicalDeviceQueueFamilyProperties(device, &familyCount, fp);
        for (int q = 0; q < families.Length; q++)
        {
            var flags = families[q].QueueFlags;
            if ((flags & QueueFlags.GraphicsBit) == 0 || (flags & QueueFlags.ComputeBit) == 0)
                continue;
            if (PresentationSurface != 0 && SurfaceApi is not null)
            {
                Bool32 supported = false;
                SurfaceApi.GetPhysicalDeviceSurfaceSupport(device, (uint)q, new SurfaceKHR(PresentationSurface), &supported);
                if (!supported)
                    continue;
            }

            probe.QueueFamily = q;
            probe.TimestampValidBits = families[q].TimestampValidBits;
            break;
        }

        var missing = probe.Missing;
        if (!api13)
            missing.Add($"Vulkan 1.3 (device has {VkUtil.VersionString(probe.Properties.ApiVersion)})");
        else
        {
            if (!f13.DynamicRendering)
                missing.Add("dynamicRendering");
            if (!f13.Synchronization2)
                missing.Add("synchronization2");
            if (!f12.RuntimeDescriptorArray)
                missing.Add("runtimeDescriptorArray");
            if (!f12.ShaderSampledImageArrayNonUniformIndexing)
                missing.Add("shaderSampledImageArrayNonUniformIndexing");
            if (!f12.DescriptorBindingPartiallyBound)
                missing.Add("descriptorBindingPartiallyBound");
            if (!f12.DescriptorBindingVariableDescriptorCount)
                missing.Add("descriptorBindingVariableDescriptorCount");
            if (!f12.DescriptorBindingSampledImageUpdateAfterBind)
                missing.Add("descriptorBindingSampledImageUpdateAfterBind");
            if (!f12.TimelineSemaphore)
                missing.Add("timelineSemaphore");
            if (!f12.ScalarBlockLayout)
                missing.Add("scalarBlockLayout");
        }

        if (probe.QueueFamily < 0)
            missing.Add(PresentationSurface != 0 ? "a graphics+compute queue that can present to the surface" : "a graphics+compute queue");
        if (wantSwapchain && !probe.Extensions.Contains(KhrSwapchain.ExtensionName))
            missing.Add(KhrSwapchain.ExtensionName);
        if (api13)
        {
            var depth = Vk.GetPhysicalDeviceFormatProperties(device, Format.D32Sfloat).OptimalTilingFeatures;
            const FormatFeatureFlags needed = FormatFeatureFlags.DepthStencilAttachmentBit | FormatFeatureFlags.SampledImageBit |
                                              FormatFeatureFlags.TransferSrcBit;
            if ((depth & needed) != needed)
                missing.Add("D32_SFLOAT depth attachment that can be sampled and copied");
        }

        return probe;
    }

    private void CreateDevice(DeviceProbe probe, VulkanContextOptions options, bool wantSwapchain)
    {
        bool rayTracing = options.EnableRayTracing && probe.AccelerationStructure && probe.RayQuery && probe.Features12.BufferDeviceAddress;
        var extensions = new List<string>();
        if (wantSwapchain)
            extensions.Add(KhrSwapchain.ExtensionName);
        if (probe.Extensions.Contains("VK_KHR_portability_subset"))
            extensions.Add("VK_KHR_portability_subset");
        if (rayTracing)
        {
            extensions.Add(KhrAccelerationStructure.ExtensionName);
            extensions.Add("VK_KHR_ray_query");
            extensions.Add("VK_KHR_deferred_host_operations");
        }

        var enabled = new PhysicalDeviceFeatures
        {
            FillModeNonSolid = probe.Features.FillModeNonSolid,
            WideLines = probe.Features.WideLines,
            SamplerAnisotropy = probe.Features.SamplerAnisotropy,
        };
        var f13 = new PhysicalDeviceVulkan13Features
        {
            SType = StructureType.PhysicalDeviceVulkan13Features,
            DynamicRendering = true,
            Synchronization2 = true,
        };
        var f12 = new PhysicalDeviceVulkan12Features
        {
            SType = StructureType.PhysicalDeviceVulkan12Features,
            PNext = &f13,
            RuntimeDescriptorArray = true,
            ShaderSampledImageArrayNonUniformIndexing = true,
            DescriptorBindingPartiallyBound = true,
            DescriptorBindingVariableDescriptorCount = true,
            DescriptorBindingSampledImageUpdateAfterBind = true,
            TimelineSemaphore = true,
            ScalarBlockLayout = true,
            BufferDeviceAddress = rayTracing,
        };
        var f11 = new PhysicalDeviceVulkan11Features { SType = StructureType.PhysicalDeviceVulkan11Features, PNext = &f12 };
        var asFeatures = new PhysicalDeviceAccelerationStructureFeaturesKHR
        {
            SType = StructureType.PhysicalDeviceAccelerationStructureFeaturesKhr,
            AccelerationStructure = true,
        };
        var rqFeatures = new PhysicalDeviceRayQueryFeaturesKHR
        {
            SType = StructureType.PhysicalDeviceRayQueryFeaturesKhr,
            PNext = &asFeatures,
            RayQuery = true,
        };
        if (rayTracing)
            f13.PNext = &rqFeatures;

        var features2 = new PhysicalDeviceFeatures2 { SType = StructureType.PhysicalDeviceFeatures2, PNext = &f11, Features = enabled };
        float priority = 1f;
        var queueInfo = new DeviceQueueCreateInfo
        {
            SType = StructureType.DeviceQueueCreateInfo,
            QueueFamilyIndex = (uint)probe.QueueFamily,
            QueueCount = 1,
            PQueuePriorities = &priority,
        };
        using var extensionNames = new NativeStringArray(extensions);
        var info = new DeviceCreateInfo
        {
            SType = StructureType.DeviceCreateInfo,
            PNext = &features2,
            QueueCreateInfoCount = 1,
            PQueueCreateInfos = &queueInfo,
            EnabledExtensionCount = extensionNames.Count,
            PpEnabledExtensionNames = extensionNames.Pointer,
        };
        Device device;
        var result = Vk.CreateDevice(probe.Device, &info, null, &device);
        if (result != Result.Success)
            throw new VulkanUnavailableException($"vkCreateDevice failed on {probe.Name} ({result}).");
        Device = device;
        PhysicalDevice = probe.Device;
        Properties = probe.Properties;
        QueueFamilyIndex = (uint)probe.QueueFamily;
        Queue queue;
        Vk.GetDeviceQueue(device, QueueFamilyIndex, 0, &queue);
        Queue = queue;
        if (wantSwapchain && Vk.TryGetDeviceExtension(Instance, device, out KhrSwapchain swapchain))
            SwapchainApi = swapchain;

        var props = probe.Properties;
        var props12 = probe.Properties12;
        var limits = props.Limits;
        uint bindless = Math.Min(props12.MaxDescriptorSetUpdateAfterBindSampledImages, props12.MaxPerStageDescriptorUpdateAfterBindSampledImages);
        bindless = Math.Min(bindless, props12.MaxPerStageUpdateAfterBindResources > 64 ? props12.MaxPerStageUpdateAfterBindResources - 64 : 0);
        bindless = Math.Min(bindless, 16384u);
        var uuidBytes = new byte[16];
        for (int i = 0; i < 16; i++)
            uuidBytes[i] = props.PipelineCacheUuid[i];

        string driverName = VkUtil.FixedString(props12.DriverName, 256);
        string driverInfo = VkUtil.FixedString(props12.DriverInfo, 256);
        Capabilities = new DeviceCapabilities
        {
            DeviceName = probe.Name,
            DriverName = driverName,
            DriverInfo = driverInfo,
            ApiVersion = props.ApiVersion,
            DeviceType = props.DeviceType,
            VendorId = props.VendorID,
            DeviceLocalMemoryBytes = probe.DeviceLocalBytes,
            FillModeNonSolid = probe.Features.FillModeNonSolid,
            WideLines = probe.Features.WideLines,
            SamplerAnisotropy = probe.Features.SamplerAnisotropy,
            MaxSamplerAnisotropy = probe.Features.SamplerAnisotropy ? limits.MaxSamplerAnisotropy : 1f,
            RayTracing = rayTracing,
            BufferDeviceAddress = rayTracing,
            Swapchain = SwapchainApi is not null,
            Timestamps = probe.TimestampValidBits > 0 && limits.TimestampPeriod > 0,
            TimestampPeriod = limits.TimestampPeriod,
            MaxBindlessTextures = bindless,
            MinStorageBufferOffsetAlignment = limits.MinStorageBufferOffsetAlignment,
            MinUniformBufferOffsetAlignment = limits.MinUniformBufferOffsetAlignment,
            BufferImageGranularity = limits.BufferImageGranularity,
            NonCoherentAtomSize = limits.NonCoherentAtomSize,
            OptimalBufferCopyOffsetAlignment = limits.OptimalBufferCopyOffsetAlignment,
            MaxImageDimension2D = limits.MaxImageDimension2D,
            MaxMemoryAllocationCount = limits.MaxMemoryAllocationCount,
            MaxComputeWorkGroupCountX = limits.MaxComputeWorkGroupCount[0],
            MaxLineWidth = limits.LineWidthRange[1],
            PipelineCacheUuid = new Guid(uuidBytes),
        };
        Info = new RendererInfo(
            probe.Name,
            string.IsNullOrEmpty(driverInfo) ? driverName : $"{driverName} ({driverInfo})",
            VkUtil.VersionString(props.ApiVersion),
            props.DeviceType == PhysicalDeviceType.Cpu,
            rayTracing,
            probe.Features.FillModeNonSolid,
            probe.DeviceLocalBytes);
        SetDebugName(ObjectType.Queue, (ulong)queue.Handle, "graphics+compute queue");
        _log(VulkanLogLevel.Info, $"Using {probe.Name} ({props.DeviceType}, {Info.DriverName}, Vulkan {Info.ApiVersion}); " +
                                  $"wireframe {(probe.Features.FillModeNonSolid ? "polygon mode" : "edge shader")}, ray tracing {(rayTracing ? "on" : "off")}.");
    }
}
