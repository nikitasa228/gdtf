using System.Collections.Concurrent;
using Silk.NET.Vulkan;

namespace Candela.Rendering.Vulkan.Tests;

public sealed class ContextTests
{
    [SkippableFact]
    public void CreatesInstanceAndDeviceWithValidationAndNoErrors()
    {
        var log = new ConcurrentQueue<string>();
        var context = TestVulkan.CreateContextOrSkip(log);
        try
        {
            Assert.True(context.ValidationEnabled, "The validation layer must be installed for the tests.");
            Assert.NotEqual(0ul, (ulong)context.Device.Handle);
            Assert.False(string.IsNullOrEmpty(context.Info.DeviceName));
            Assert.StartsWith("1.", context.Info.ApiVersion);
            Assert.True(context.Capabilities.MaxBindlessTextures >= 256);
            Assert.Equal(context.Capabilities.FillModeNonSolid, context.Info.SupportsWireframe);
        }
        finally
        {
            context.Dispose();
        }

        TestVulkan.AssertNoValidationErrors(context);
    }

    [Fact]
    public void DeviceSelectionPrefersDiscreteThenPreferredName()
    {
        var none = Array.Empty<string>();
        var candidates = new List<DeviceCandidate>
        {
            new(0, "llvmpipe (LLVM 20)", PhysicalDeviceType.Cpu, Vk.Version13, 8ul << 30, none),
            new(1, "Intel(R) UHD Graphics 770", PhysicalDeviceType.IntegratedGpu, Vk.Version13, 2ul << 30, none),
            new(2, "NVIDIA GeForce RTX 4070", PhysicalDeviceType.DiscreteGpu, Vk.Version13, 12ul << 30, none),
            new(3, "AMD Radeon RX 9070", PhysicalDeviceType.DiscreteGpu, Vk.Version13, 16ul << 30, new[] { "dynamicRendering" }),
        };

        Assert.Equal(2, DeviceSelector.Select(candidates, "")!.Index);
        Assert.Equal(1, DeviceSelector.Select(candidates, "intel")!.Index);
        Assert.Equal(0, DeviceSelector.Select(candidates, "LLVMPIPE")!.Index);
        // A preferred device that misses requirements is never chosen.
        Assert.Equal(2, DeviceSelector.Select(candidates, "Radeon")!.Index);
        Assert.Null(DeviceSelector.Select(new[] { candidates[3] }, ""));
        // Same type: more device-local memory wins.
        var twoDiscrete = new List<DeviceCandidate>
        {
            new(0, "A", PhysicalDeviceType.DiscreteGpu, Vk.Version13, 8ul << 30, none),
            new(1, "B", PhysicalDeviceType.DiscreteGpu, Vk.Version13, 24ul << 30, none),
        };
        Assert.Equal(1, DeviceSelector.Select(twoDiscrete, "")!.Index);
    }

    [SkippableFact]
    public void DisposeWithoutResourcesIsLeakFree()
    {
        var context = TestVulkan.CreateContextOrSkip();
        context.Dispose();
        context.Dispose(); // idempotent
        TestVulkan.AssertNoValidationErrors(context);
    }
}
