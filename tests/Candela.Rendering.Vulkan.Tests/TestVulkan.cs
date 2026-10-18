using System.Collections.Concurrent;

namespace Candela.Rendering.Vulkan.Tests;

/// <summary>
/// Creates Vulkan contexts for tests: validation and synchronization validation always on (when the layer is
/// installed), caches in the test output folder, messages collected for assertions. Tests call
/// <see cref="CreateContextOrSkip"/> and are skipped with a clear message when no Vulkan device exists.
/// </summary>
internal static class TestVulkan
{
    public static string CacheDirectory => Path.Combine(AppContext.BaseDirectory, "vk-cache");

    public static VulkanContextOptions Options(ConcurrentQueue<string>? log = null, IReadOnlyList<string>? instanceExtensions = null,
        Func<nint, ulong>? createSurface = null) => new()
        {
            EnableValidation = true,
            SynchronizationValidation = true,
            CacheDirectory = CacheDirectory,
            InstanceExtensions = instanceExtensions ?? Array.Empty<string>(),
            CreateSurface = createSurface,
            MinimumLogLevel = VulkanLogLevel.Warning,
            Log = (level, message) => log?.Enqueue($"{level}: {message}"),
        };

    /// <summary>Creates a validated context or skips the test.</summary>
    public static VulkanContext CreateContextOrSkip(ConcurrentQueue<string>? log = null)
    {
        bool ok = VulkanContext.TryCreate(Options(log), out var context, out string reason);
        Skip.IfNot(ok, $"No Vulkan device available: {reason}");
        return context!;
    }

    /// <summary>Asserts that the validation layer reported no error (message list in the failure text).</summary>
    public static void AssertNoValidationErrors(VulkanContext context)
    {
        Assert.True(context.ValidationErrorCount == 0,
            $"{context.ValidationErrorCount} validation error(s):\n" + string.Join("\n\n", context.ValidationErrors));
    }
}
