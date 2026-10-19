using System.Diagnostics;
using System.Numerics;

namespace Candela.Rendering.Vulkan.Tests;

/// <summary>
/// The procedural stage scene path-traced in haze: everything the real-time view shows (pools, beams, the LED panel,
/// soft shadows, coloured light bounced onto neighbouring surfaces), finite and validation-clean; the images are
/// written next to the real-time view for comparison.
/// </summary>
public sealed class PathTracerSceneTests
{
    [SkippableFact]
    public void ProceduralStageInHazeRendersLikeTheRealTimeView()
    {
        var renderer = FinalRenderSupport.CreateOrSkip();
        try
        {
            var scene = ProceduralTestScene.Build(renderer.World);
            renderer.World.SetAtmosphere(new AtmosphereState { BaseScattering = 0.05f, Anisotropy = 0.7f });
            var camera = ProceduralTestScene.CreateCamera();
            const int width = 240, height = 180;

            using (var target = renderer.CreateTarget(width, height))
            {
                var view = new ViewOptions { ShowGrid = false, ShowSelectionOutline = false };
                for (int i = 0; i < 6; i++)
                    renderer.Render(target, camera, view, new PreviewSettings { Indirect = IndirectLighting.RayTraced }, 1f / 30f);
                ImageFiles.WritePng(renderer.ReadPixels(target), Path.Combine(TestRenderers.OutputDirectory, "pt-stage-realtime.png"));
            }

            var watch = Stopwatch.StartNew();
            using var render = FinalRenderSupport.RenderToCompletion(renderer, camera, FinalRenderSupport.Settings(width, height, 16));
            double seconds = watch.Elapsed.TotalSeconds;
            FinalRenderSupport.Save(render, "pt-stage-final");
            var hdr = FinalRenderSupport.Luminance(render.ResultHdr(), render.Exposure);
            foreach (float v in hdr.Rgba)
                Assert.True(float.IsFinite(v) && v >= 0f);
            Assert.True(seconds < 120, $"16 samples took {seconds:0.0} s");
            TestVulkan.AssertNoValidationErrors(renderer.Context);
            scene.Destroy();
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }
}
