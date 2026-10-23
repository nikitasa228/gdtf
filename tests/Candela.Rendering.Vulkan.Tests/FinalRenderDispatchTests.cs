using System.Numerics;

namespace Candela.Rendering.Vulkan.Tests;

/// <summary>
/// <see cref="VulkanFinalRender.DispatchSeconds"/>, the planned dispatch time an interactive caller (the editor's render
/// job) shortens: it is clamped to <see cref="VulkanFinalRender.MinDispatchSeconds"/> …
/// <see cref="VulkanFinalRender.MaxDispatchSeconds"/>, and the smaller bands it leads to give the same image as the
/// default (every pixel one sample per pass).
/// </summary>
public sealed class FinalRenderDispatchTests
{
    [SkippableFact]
    public void A_shorter_dispatch_time_is_clamped_and_renders_the_same_image()
    {
        var renderer = FinalRenderSupport.CreateOrSkip();
        try
        {
            var scene = new StageScene(renderer);
            scene.Floor(10f, scene.Lambert(new Vector3(0.7f)));
            scene.Box(new Vector3(1f, 1f, 1f), new Vector3(0.5f, 0.5f, 0.5f), scene.Lambert(new Vector3(0.8f), "box"));
            renderer.World.SetLights(new[] { StageScene.Spot(new Vector3(0.5f, -1f, 4.5f), new Vector3(-0.5f, 0.5f, 0f), 15000f, 50f, 70f) });
            var camera = RenderCamera.LookAt(new Vector3(3f, -5f, 3f), new Vector3(-0.5f, 0.5f, 0.8f), 50f);
            camera.Ev100 = 6f;
            var settings = FinalRenderSupport.Settings(64, 48, 4);
            var options = new FinalRenderOptions { Seed = 7, BackgroundLuminance = 0f };

            byte[] Render(double? dispatchSeconds)
            {
                using var render = renderer.StartFinalRender(camera, settings, options);
                Assert.Equal(VulkanFinalRender.MaxDispatchSeconds, render.DispatchSeconds);
                if (dispatchSeconds is { } seconds)
                {
                    render.DispatchSeconds = seconds;
                    // tiny bands: the measured cost of a whole 64 × 48 pass is far below 5 ms on most devices, so the
                    // band limit makes the passes split as a short dispatch time does on a large image
                    render.MaxRowsPerDispatch = 8;
                }

                for (int i = 0; i < 10000 && !render.IsComplete; i++)
                    render.Advance(TimeSpan.FromMilliseconds(30));
                Assert.True(render.IsComplete);
                Assert.Equal((4, 4), render.SampleCountRange());
                return render.RawResultHdr().Pixels;
            }

            var reference = Render(null);
            var split = Render(VulkanFinalRender.MinDispatchSeconds);
            Assert.Equal(reference, split);

            using var clamped = renderer.StartFinalRender(camera, settings, options);
            clamped.DispatchSeconds = 0;
            Assert.Equal(VulkanFinalRender.MinDispatchSeconds, clamped.DispatchSeconds);
            clamped.DispatchSeconds = 10;
            Assert.Equal(VulkanFinalRender.MaxDispatchSeconds, clamped.DispatchSeconds);
            clamped.DispatchSeconds = double.NaN;
            Assert.Equal(VulkanFinalRender.MaxDispatchSeconds, clamped.DispatchSeconds);
            clamped.DispatchSeconds = 0.02;
            Assert.Equal(0.02, clamped.DispatchSeconds);
            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }
}
