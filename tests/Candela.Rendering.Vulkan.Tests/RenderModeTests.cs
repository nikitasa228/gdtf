namespace Candela.Rendering.Vulkan.Tests;

/// <summary>Complete frames of the procedural scene in every view mode run without validation errors.</summary>
public sealed class RenderModeTests
{
    [SkippableTheory]
    [InlineData(ViewShading.Full, false)]
    [InlineData(ViewShading.Colorless, false)]
    [InlineData(ViewShading.Wireframe, false)]
    [InlineData(ViewShading.Wireframe, true)]
    public void EveryViewModeRendersWithoutValidationErrors(ViewShading shading, bool edgeShader)
    {
        var renderer = TestRenderers.CreateOrSkip(options: new VulkanRendererOptions { ForceEdgeShaderWireframe = edgeShader });
        try
        {
            ProceduralTestScene.Build(renderer.World);
            using var target = renderer.CreateTarget(320, 240);
            var camera = ProceduralTestScene.CreateCamera();
            var view = new ViewOptions { Shading = shading };
            var settings = new PreviewSettings();
            for (int i = 0; i < 3; i++)
                renderer.Render(target, camera, view, settings, 1f / 60f);
            var image = renderer.ReadPixels(target);
            Assert.Equal(320, image.Width);
            Assert.Equal(240 * 320 * 4, image.Pixels.Length);
            ImageFiles.WritePng(image, Path.Combine(TestRenderers.OutputDirectory, $"mode-{shading}{(edgeShader ? "-edges" : "")}.png"));
            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }
}
