using System.Collections.Concurrent;

namespace Candela.Rendering.Vulkan.Tests;

/// <summary>
/// Helpers to create validated renderers for tests (each test owns its renderer and context, so validation errors and
/// leak reports belong to exactly one test).
/// </summary>
internal static class TestRenderers
{
    /// <summary>Directory where tests write their images for inspection (CANDELA_TEST_OUTPUT or the test output folder).</summary>
    public static string OutputDirectory
    {
        get
        {
            string dir = Environment.GetEnvironmentVariable("CANDELA_TEST_OUTPUT") is { Length: > 0 } d
                ? d
                : Path.Combine(AppContext.BaseDirectory, "render-output");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>Creates a renderer with validation (skips the test when no Vulkan device exists).</summary>
    public static VulkanRenderer CreateOrSkip(ConcurrentQueue<string>? log = null, VulkanRendererOptions? options = null)
    {
        VulkanRenderer? renderer = null;
        string reason = "";
        try
        {
            renderer = VulkanRenderer.Create(TestVulkan.Options(log), options);
        }
        catch (VulkanUnavailableException e)
        {
            reason = e.Message;
        }

        Skip.If(renderer is null, $"No Vulkan device available: {reason}");
        return renderer!;
    }

    /// <summary>Disposes the renderer (and its context) and asserts that validation reported no error, leaks included.</summary>
    public static void DisposeAndAssertClean(VulkanRenderer renderer)
    {
        var context = renderer.Context;
        renderer.Dispose();
        TestVulkan.AssertNoValidationErrors(context);
    }
}
