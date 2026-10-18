using System.Collections.Concurrent;
using System.Numerics;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;

namespace Candela.Rendering.Vulkan.Tests;

/// <summary>
/// The editor's presentation path on a <c>VK_EXT_headless_surface</c> (no window needed): the context is created with
/// the surface factory, viewports render into the open frame, the swapchain acquires, clears, presents, is recreated on
/// resize and while minimised, and the render targets' display images are left ready for Dear ImGui.
/// </summary>
public sealed unsafe class SwapchainTests
{
    private const string HeadlessSurfaceExtension = "VK_EXT_headless_surface";

    [SkippableFact]
    public void HeadlessSwapchainPresentsViewportsAndRecreates()
    {
        var log = new ConcurrentQueue<string>();
        using var loader = Vk.GetApi();
        ulong surfaceHandle = 0;
        ulong CreateSurface(nint instanceHandle)
        {
            var instance = new Instance(instanceHandle);
            if (!loader.TryGetInstanceExtension(instance, out ExtHeadlessSurface headless))
                return 0;
            var info = new HeadlessSurfaceCreateInfoEXT { SType = StructureType.HeadlessSurfaceCreateInfoExt };
            SurfaceKHR surface;
            if (headless.CreateHeadlessSurface(instance, &info, null, &surface) != Result.Success)
                return 0;
            surfaceHandle = surface.Handle;
            return surface.Handle;
        }

        var options = TestVulkan.Options(log, new[] { "VK_KHR_surface", HeadlessSurfaceExtension }, CreateSurface);
        bool ok = VulkanContext.TryCreate(options, out var context, out string reason);
        Skip.IfNot(ok, $"No Vulkan device with {HeadlessSurfaceExtension} available: {reason}");
        var ctx = context!;
        try
        {
            Assert.NotEqual(0ul, surfaceHandle);
            Assert.Equal(surfaceHandle, ctx.PresentationSurface);
            Assert.True(ctx.Capabilities.Swapchain);
            RunFrames(ctx, surfaceHandle);
        }
        finally
        {
            if (surfaceHandle != 0 && ctx.SurfaceApi is not null && !ctx.IsDisposed)
                ctx.SurfaceApi.DestroySurface(ctx.Instance, new SurfaceKHR(surfaceHandle), null);
            ctx.Dispose();
        }

        TestVulkan.AssertNoValidationErrors(ctx);
    }

    private static void RunFrames(VulkanContext context, ulong surface)
    {
        using var renderer = new VulkanRenderer(context);
        using var swapchain = new VulkanSwapchain(context, surface, 320, 240);
        Assert.Equal(320, swapchain.Width);
        Assert.Equal(240, swapchain.Height);
        Assert.True(swapchain.ImageCount >= swapchain.MinImageCount);
        Assert.Equal(swapchain.ImageCount, swapchain.ImageViews.Length);
        Assert.True(swapchain.Format is Format.B8G8R8A8Unorm or Format.R8G8B8A8Unorm, $"format {swapchain.Format}");

        ProceduralTestScene.Build(renderer.World);
        var camera = ProceduralTestScene.CreateCamera();
        using var left = renderer.CreateVulkanTarget(160, 120);
        using var right = renderer.CreateVulkanTarget(96, 64);
        var full = new ViewOptions { Shading = ViewShading.Full };
        var colorless = new ViewOptions { Shading = ViewShading.Colorless };
        var settings = new PreviewSettings();
        var clear = new Vector4(0.1f, 0.1f, 0.12f, 1f);

        void Frame(bool drawUi = true)
        {
            var frame = context.BeginFrame();
            renderer.Render(left, camera, full, settings, 1f / 60f);   // records into the open frame
            renderer.Render(right, camera, colorless, settings, 1f / 60f);
            Assert.Same(frame, context.CurrentFrame);
            if (swapchain.TryAcquire(frame, out var image))
            {
                Assert.Equal(swapchain.Width, image.Width);
                Assert.NotEqual(0ul, image.View.Handle);
                if (drawUi)
                {
                    swapchain.BeginRendering(frame, clear);
                    swapchain.EndRendering(frame); // Dear ImGui records here, sampling left/right display images
                }
            }

            swapchain.Present(frame);
            Assert.Null(context.CurrentFrame);
        }

        for (int i = 0; i < 4; i++)
            Frame();
        Frame(drawUi: false); // acquired but nothing drawn: still transitioned to PRESENT_SRC

        // ImGui handoff: sampler + view + layout of the display image of each viewport.
        foreach (var target in new[] { left, right })
        {
            Assert.True(target.HasFrame);
            Assert.NotEqual(0ul, target.DisplayImageView.Handle);
            Assert.NotEqual(0ul, target.DisplaySampler.Handle);
            Assert.Equal(ImageLayout.ShaderReadOnlyOptimal, target.DisplayImageLayout);
        }

        // Window resized: viewport and swapchain follow.
        int version = swapchain.Version;
        int displayVersion = left.DisplayVersion;
        swapchain.Recreate(400, 300);
        left.Resize(200, 150);
        Assert.Equal(400, swapchain.Width);
        Assert.Equal(version + 1, swapchain.Version);
        Assert.Equal(displayVersion + 1, left.DisplayVersion);
        for (int i = 0; i < 3; i++)
            Frame();

        // Minimised: no image, the frame is still submitted.
        swapchain.Recreate(0, 0);
        Assert.Equal(0, swapchain.Width);
        var minimised = context.BeginFrame();
        renderer.Render(left, camera, full, settings, 0f);
        Assert.False(swapchain.TryAcquire(minimised, out _));
        Assert.False(swapchain.Present(minimised));

        swapchain.Recreate(320, 240);
        for (int i = 0; i < 3; i++)
            Frame();

        // Misuse is reported, not silently recorded.
        var frame = context.BeginFrame();
        Assert.Throws<InvalidOperationException>(() => swapchain.BeginRendering(frame, clear));
        Assert.True(swapchain.TryAcquire(frame, out _));
        Assert.Throws<InvalidOperationException>(() => swapchain.TryAcquire(frame, out _));
        Assert.Throws<InvalidOperationException>(() => swapchain.Recreate(10, 10));
        swapchain.BeginRendering(frame, clear);
        Assert.Throws<InvalidOperationException>(() => swapchain.Present(frame));
        swapchain.EndRendering(frame);
        swapchain.Present(frame);

        context.WaitForSubmittedFrames();
        TestVulkan.AssertNoValidationErrors(context);
    }
}
