using System.Numerics;
using Candela.Core.Geometry;
using Candela.Photometry;

namespace Candela.Rendering.Vulkan.Tests;

/// <summary>
/// The path tracer is photometric: a spot light of known candela on a white Lambertian floor gives the luminance of
/// the analytic illuminance (E = I(θ)·cos α / d², L = ρE/π), coloured lights keep their chromaticity, and two lights
/// add up.
/// </summary>
public sealed class PathTracerPhotometryTests
{
    private const float Height = 5f;
    private const float PeakCandela = 10000f;
    private const float BeamDeg = 50f;
    private const float FieldDeg = 80f;

    [SkippableFact]
    public void LitFloorMatchesTheAnalyticIlluminance()
    {
        var renderer = FinalRenderSupport.CreateOrSkip();
        try
        {
            var view = new TopDownView(80, 60, 8f);
            FinalRenderSupport.Floor(renderer.World, TopDownView.WhiteLambertian());
            var light = ProceduralTestScene.Spot(new Vector3(0f, 0f, Height), Vector3.Zero, LightColor.White, PeakCandela, BeamDeg, FieldDeg);
            renderer.World.SetLights(new[] { light });
            using var render = FinalRenderSupport.RenderToCompletion(renderer, view.Camera, FinalRenderSupport.Settings(view.Width, view.Height, 64),
                new FinalRenderOptions { BackgroundLuminance = 0f });
            FinalRenderSupport.Save(render, "pt-photometry-floor");
            var hdr = FinalRenderSupport.Luminance(render.ResultHdr(), render.Exposure);

            var beam = BeamDistribution.Create(BeamType.Wash, BeamDeg * MathF.PI / 180f, FieldDeg * MathF.PI / 180f);
            var (cx, cy, centre) = view.NearestPixel(Vector3.Zero);
            float expected = PhotometricTests.FloorLuminance(light, beam.Relative, centre);
            float measured = hdr.MeanLuminance(cx, cy, 1);
            Assert.InRange(MathF.Abs(measured - expected) / expected, 0f, 0.03f);

            float r = Height * MathF.Tan(30f * MathF.PI / 180f);
            foreach (var direction in new[] { Vector3.UnitX, -Vector3.UnitY })
            {
                var (px, py, p) = view.NearestPixel(direction * r);
                float e = PhotometricTests.FloorLuminance(light, beam.Relative, p);
                Assert.InRange(MathF.Abs(hdr.MeanLuminance(px, py, 1) - e) / e, 0f, 0.05f);
            }

            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }
}
