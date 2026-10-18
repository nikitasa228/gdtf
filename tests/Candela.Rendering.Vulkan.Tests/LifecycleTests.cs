using System.Collections.Concurrent;
using System.Numerics;
using Candela.Core.Geometry;

namespace Candela.Rendering.Vulkan.Tests;

/// <summary>Resizing, creating and destroying targets and scene objects, shutdown without leaks, allocation-free frames.</summary>
public sealed class LifecycleTests
{
    [SkippableFact]
    public void ResizeRecreatesTheTargetImages()
    {
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            ProceduralTestScene.Build(renderer.World);
            var camera = ProceduralTestScene.CreateCamera();
            var view = new ViewOptions();
            var settings = new PreviewSettings();
            using var target = renderer.CreateVulkanTarget(320, 240);
            renderer.Render(target, camera, view, settings, 0f);
            Assert.NotNull(renderer.Pick(target, 160, 160));

            int version = target.DisplayVersion;
            target.Resize(320, 240); // same size: nothing happens
            Assert.Equal(version, target.DisplayVersion);
            Assert.True(target.HasFrame);

            target.Resize(200, 96);
            Assert.Equal((200, 96), (target.Width, target.Height));
            Assert.Equal(version + 1, target.DisplayVersion);
            Assert.False(target.HasFrame);
            Assert.Null(renderer.Pick(target, 10, 10)); // contents undefined until the next frame
            var blank = renderer.ReadPixels(target);
            Assert.Equal((200, 96), (blank.Width, blank.Height));
            Assert.All(blank.Pixels, b => Assert.Equal(0, b));

            // Several resizes between frames, then frames in every mode (old images retired while frames are in flight).
            foreach (var (w, h) in new[] { (64, 64), (1, 1), (0, -5), (333, 177) })
            {
                target.Resize(w, h);
                foreach (var shading in new[] { ViewShading.Full, ViewShading.Colorless, ViewShading.Wireframe })
                    renderer.Render(target, camera, new ViewOptions { Shading = shading }, settings, 0f);
            }

            Assert.Equal((333, 177), (target.Width, target.Height));
            target.Resize(0, -5);
            Assert.Equal((1, 1), (target.Width, target.Height)); // clamped to at least one pixel
            target.Resize(240, 180);
            renderer.Render(target, camera, view, settings, 0f);
            var image = renderer.ReadPixels(target);
            Assert.Equal((240, 180), (image.Width, image.Height));
            Assert.Equal(240 * 180 * 4, image.Pixels.Length);
            Assert.True(CountNonBackground(image) > 1000, "the resized target shows the scene");
            Assert.NotNull(renderer.Pick(target, 120, 120));
            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableFact]
    public void SeveralTargetsShareOneWorld()
    {
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            ProceduralTestScene.Build(renderer.World);
            var camera = ProceduralTestScene.CreateCamera();
            var settings = new PreviewSettings();
            var full = new ViewOptions { Shading = ViewShading.Full };
            var wire = new ViewOptions { Shading = ViewShading.Wireframe };
            using var a = renderer.CreateTarget(160, 120);
            using var b = renderer.CreateTarget(200, 100);

            // Each alone ...
            renderer.Render(a, camera, full, settings, 0f);
            var aloneA = renderer.ReadPixels(a);
            renderer.Render(b, camera, wire, settings, 0f);
            var aloneB = renderer.ReadPixels(b);

            // ... and both in one frame (the editor's case: several viewports recorded into one command buffer).
            var frame = renderer.Context.BeginFrame();
            renderer.Render(a, camera, full, settings, 0f);
            renderer.Render(b, camera, wire, settings, 0f);
            renderer.Context.SubmitFrame(frame);
            Assert.Equal(aloneA.Pixels, renderer.ReadPixels(a).Pixels);
            Assert.Equal(aloneB.Pixels, renderer.ReadPixels(b).Pixels);
            Assert.Equal(ProceduralTestScene.PickIds.Floor, renderer.Pick(a, 40, 100)?.PickId);
            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableFact]
    public void DestroyingEverythingReturnsTheMemory()
    {
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            var camera = ProceduralTestScene.CreateCamera();
            var settings = new PreviewSettings();
            var view = new ViewOptions();
            using var target = renderer.CreateTarget(160, 120);
            renderer.Render(target, camera, view, settings, 0f);
            renderer.Context.WaitForSubmittedFrames();
            var baseline = renderer.Context.Allocator.Statistics;

            for (int round = 0; round < 3; round++)
            {
                var scene = ProceduralTestScene.Build(renderer.World);
                var texture = renderer.World.CreateTexture(SceneTests.Checkerboard(96, 64), srgb: true);
                using var extra = renderer.CreateTarget(64 + round * 16, 48);
                foreach (var shading in new[] { ViewShading.Full, ViewShading.Colorless, ViewShading.Wireframe })
                {
                    renderer.Render(target, camera, new ViewOptions { Shading = shading }, settings, 0f);
                    renderer.Render(extra, camera, new ViewOptions { Shading = shading }, settings, 0f);
                }

                renderer.World.DestroyTexture(texture);
                scene.Destroy();
            }

            // Retired objects are destroyed once the frames that used them have completed.
            for (int i = 0; i < VulkanContext.FramesInFlight + 1; i++)
                renderer.Render(target, camera, view, settings, 0f);
            renderer.Context.WaitForSubmittedFrames();
            var after = renderer.Context.Allocator.Statistics;
            Assert.Equal(baseline.AllocationCount, after.AllocationCount);
            Assert.Equal(baseline.DedicatedCount, after.DedicatedCount);
            Assert.Equal(baseline.UsedBytes, after.UsedBytes);
            Assert.Equal(new RenderWorldStatistics(0, 0, 0, 0, 0, 0), renderer.VulkanWorld.Statistics);
            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableFact]
    public void DisposeWithLiveObjectsIsLeakFree()
    {
        var log = new ConcurrentQueue<string>();
        var renderer = TestRenderers.CreateOrSkip(log);
        var context = renderer.Context;
        ProceduralTestScene.Build(renderer.World);
        var texture = renderer.World.CreateTexture(SceneTests.Checkerboard(37, 19), srgb: false);
        renderer.World.CreateMaterial(new MaterialData { Name = "textured", BaseColorTexture = "checker" },
            new SceneTests.ImageSource { ["checker"] = SceneTests.Checkerboard(64, 64) });
        var camera = ProceduralTestScene.CreateCamera();
        var first = renderer.CreateTarget(160, 120);
        var second = renderer.CreateTarget(96, 96);
        var third = renderer.CreateTarget(64, 32);
        foreach (var shading in new[] { ViewShading.Full, ViewShading.Colorless, ViewShading.Wireframe })
        {
            renderer.Render(first, camera, new ViewOptions { Shading = shading }, new PreviewSettings(), 0f);
            renderer.Render(second, camera, new ViewOptions { Shading = shading }, new PreviewSettings(), 0f);
        }

        third.Dispose();
        Assert.True(texture.IsValid);
        // Nothing else is destroyed explicitly: the renderer owns the world, the targets and the context.
        renderer.Dispose();
        renderer.Dispose(); // idempotent
        Assert.True(context.IsDisposed);
        TestVulkan.AssertNoValidationErrors(context); // the layer reports every leaked object at vkDestroyDevice/vkDestroyInstance
        Assert.DoesNotContain(log, m => m.Contains("still allocated", StringComparison.Ordinal));
        Assert.Throws<ObjectDisposedException>(() => renderer.CreateTarget(8, 8));
    }

    [SkippableFact]
    public void SteadyStateFramesDoNotAllocateManagedMemory()
    {
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            var scene = ProceduralTestScene.Build(renderer.World);
            var camera = ProceduralTestScene.CreateCamera();
            var settings = new PreviewSettings();
            var views = new[]
            {
                new ViewOptions { Shading = ViewShading.Full },
                new ViewOptions { Shading = ViewShading.Colorless },
                new ViewOptions { Shading = ViewShading.Wireframe },
            };
            using var target = renderer.CreateTarget(96, 64);
            var lights = scene.Lights;
            var lines = scene.Lines;
            var box = scene.Instances[2];
            var phases = new long[4];
            void Frame(int i)
            {
                // A typical editor frame: lights and lines replaced, one object moved, every view mode.
                long t0 = GC.GetAllocatedBytesForCurrentThread();
                lights[0].PeakCandela = 50000f + i;
                renderer.World.SetLights(lights);
                long t1 = GC.GetAllocatedBytesForCurrentThread();
                renderer.World.SetLines(lines);
                long t2 = GC.GetAllocatedBytesForCurrentThread();
                renderer.World.UpdateInstance(box, Matrix4x4.CreateTranslation(-3.2f, 1.6f + 0.001f * i, 0.5f), InstanceFlags.CastShadow);
                long t3 = GC.GetAllocatedBytesForCurrentThread();
                renderer.Render(target, camera, views[i % views.Length], settings, 1f / 60f);
                long t4 = GC.GetAllocatedBytesForCurrentThread();
                phases[0] += t1 - t0;
                phases[1] += t2 - t1;
                phases[2] += t3 - t2;
                phases[3] += t4 - t3;
            }

            int frameIndex = 0;
            for (; frameIndex < 40; frameIndex++)
                Frame(frameIndex); // warm-up: pipelines, descriptor sets, staging, JIT
            renderer.Context.WaitForSubmittedFrames();

            // A per-frame allocation shows up in every window; one-off runtime events (tiered JIT promoting a method and
            // starting its background worker) do not recur, so one allocation-free window proves the steady state.
            var windows = new List<string>();
            for (int window = 0; window < 4; window++)
            {
                Array.Clear(phases);
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int end = frameIndex + 30; frameIndex < end; frameIndex++)
                    Frame(frameIndex);
                long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                if (allocated == 0)
                    break;
                windows.Add($"{allocated} bytes (SetLights {phases[0]}, SetLines {phases[1]}, UpdateInstance {phases[2]}, Render {phases[3]})");
            }

            Assert.True(windows.Count < 4, "managed memory allocated in every window of 30 steady-state frames: " + string.Join("; ", windows));
            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    private static int CountNonBackground(ImageData image)
    {
        int n = 0;
        for (int i = 0; i < image.Pixels.Length; i += 4)
        {
            if (image.Pixels[i] > 40 || image.Pixels[i + 1] > 40 || image.Pixels[i + 2] > 40)
                n++;
        }

        return n;
    }
}
