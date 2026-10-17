using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.Core;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;

namespace Candela.Rendering.Vulkan;

/// <summary>
/// Collects messages of the validation layer: counts errors and warnings (tests assert zero errors) and forwards
/// them to the context's logger. Callbacks can arrive on any thread.
/// </summary>
internal sealed class ValidationSink
{
    private const int MaxStoredMessages = 64;
    private readonly List<string> _errors = new();
    private readonly Action<VulkanLogLevel, string> _log;
    private readonly VulkanLogLevel _minimumLevel;
    private int _errorCount;
    private int _warningCount;

    public ValidationSink(Action<VulkanLogLevel, string> log, VulkanLogLevel minimumLevel)
    {
        _log = log;
        _minimumLevel = minimumLevel;
    }

    public int ErrorCount => Volatile.Read(ref _errorCount);

    public int WarningCount => Volatile.Read(ref _warningCount);

    public IReadOnlyList<string> Errors
    {
        get
        {
            lock (_errors)
                return _errors.ToArray();
        }
    }

    public void Report(VulkanLogLevel level, string message)
    {
        if (level == VulkanLogLevel.Error)
        {
            Interlocked.Increment(ref _errorCount);
            lock (_errors)
            {
                if (_errors.Count < MaxStoredMessages)
                    _errors.Add(message);
            }
        }
        else if (level == VulkanLogLevel.Warning)
        {
            Interlocked.Increment(ref _warningCount);
        }

        if (level >= _minimumLevel)
            _log(level, message);
    }
}

/// <summary>
/// The <c>VK_EXT_debug_utils</c> messenger. The native callback is a static <see cref="UnmanagedCallersOnlyAttribute"/>
/// function; the sink is reached through a <see cref="GCHandle"/> passed as user data, which stays allocated until
/// <see cref="Dispose"/> (after the instance is destroyed, because instance creation/destruction also report through
/// the same callback chained into <c>VkInstanceCreateInfo</c>).
/// </summary>
internal sealed unsafe class DebugMessenger : IDisposable
{
    private GCHandle _handle;
    private ExtDebugUtils? _api;
    private Instance _instance;
    private DebugUtilsMessengerEXT _messenger;

    public DebugMessenger(ValidationSink sink)
    {
        _handle = GCHandle.Alloc(sink);
    }

    /// <summary>Create-info for the messenger (also chained into the instance create-info).</summary>
    public DebugUtilsMessengerCreateInfoEXT CreateInfo => new()
    {
        SType = StructureType.DebugUtilsMessengerCreateInfoExt,
        MessageSeverity = DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt | DebugUtilsMessageSeverityFlagsEXT.WarningBitExt |
                          DebugUtilsMessageSeverityFlagsEXT.InfoBitExt | DebugUtilsMessageSeverityFlagsEXT.VerboseBitExt,
        MessageType = DebugUtilsMessageTypeFlagsEXT.GeneralBitExt | DebugUtilsMessageTypeFlagsEXT.ValidationBitExt |
                      DebugUtilsMessageTypeFlagsEXT.PerformanceBitExt,
        PfnUserCallback = new PfnDebugUtilsMessengerCallbackEXT(&Callback),
        PUserData = (void*)GCHandle.ToIntPtr(_handle),
    };

    /// <summary>Creates the persistent messenger on <paramref name="instance"/>.</summary>
    public void Attach(ExtDebugUtils api, Instance instance)
    {
        var info = CreateInfo;
        DebugUtilsMessengerEXT messenger;
        VkUtil.Check(api.CreateDebugUtilsMessenger(instance, &info, null, &messenger), "vkCreateDebugUtilsMessengerEXT");
        _api = api;
        _instance = instance;
        _messenger = messenger;
    }

    /// <summary>Destroys the messenger (call before the instance is destroyed).</summary>
    public void Detach()
    {
        if (_api is not null && _messenger.Handle != 0)
            _api.DestroyDebugUtilsMessenger(_instance, _messenger, null);
        _messenger = default;
        _api = null;
    }

    /// <summary>Releases the GC handle (call after the instance is destroyed).</summary>
    public void Dispose()
    {
        Detach();
        if (_handle.IsAllocated)
            _handle.Free();
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static Bool32 Callback(DebugUtilsMessageSeverityFlagsEXT severity, DebugUtilsMessageTypeFlagsEXT types,
        DebugUtilsMessengerCallbackDataEXT* data, void* userData)
    {
        try
        {
            if (userData == null || data == null)
                return Vk.False;
            if (GCHandle.FromIntPtr((nint)userData).Target is not ValidationSink sink)
                return Vk.False;

            var level = severity switch
            {
                _ when (severity & DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt) != 0 => VulkanLogLevel.Error,
                _ when (severity & DebugUtilsMessageSeverityFlagsEXT.WarningBitExt) != 0 => VulkanLogLevel.Warning,
                _ when (severity & DebugUtilsMessageSeverityFlagsEXT.InfoBitExt) != 0 => VulkanLogLevel.Info,
                _ => VulkanLogLevel.Debug,
            };

            // Performance hints are advice, not errors: never counted as validation errors.
            if ((types & DebugUtilsMessageTypeFlagsEXT.PerformanceBitExt) != 0 && level > VulkanLogLevel.Info)
                level = VulkanLogLevel.Info;

            string id = data->PMessageIdName != null ? Marshal.PtrToStringUTF8((nint)data->PMessageIdName) ?? "" : "";
            string text = data->PMessage != null ? Marshal.PtrToStringUTF8((nint)data->PMessage) ?? "" : "";
            sink.Report(level, id.Length > 0 ? $"[{id}] {text}" : text);
        }
        catch (Exception)
        {
            // Never let an exception cross the native boundary.
        }

        return Vk.False;
    }
}
