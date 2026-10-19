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

    /// <summary>
    /// Looking into the beam, the lens shows the luminance that carries the light's intensity: L = I(θ) / (A cos θ_y)
    /// (times |x − y|² / |x − c|² for the lens 5 mm in front of the centre), on the axis and 10° off it (where the
    /// lens is seen foreshortened and the profile has fallen off).
    /// </summary>
    [SkippableTheory]
    [InlineData(0f)]
    [InlineData(10f)]
    public void LensSeenFromTheBeamHasTheLuminanceOfItsIntensity(float offAxisDeg)
    {
        var renderer = FinalRenderSupport.CreateOrSkip();
        try
        {
            renderer.World.SetAtmosphere(new AtmosphereState { BaseScattering = 0f });
            const float radius = 0.1f;
            var position = new Vector3(0f, 0f, Height);
            var light = ProceduralTestScene.Spot(position, Vector3.Zero, LightColor.White, PeakCandela, BeamDeg, FieldDeg);
            light.SourceRadius = radius;
            renderer.World.SetLights(new[] { light });
            float angle = offAxisDeg * MathF.PI / 180f;
            var eye = position + Height * new Vector3(MathF.Sin(angle), 0f, -MathF.Cos(angle));
            var camera = RenderCamera.LookAt(eye, position, 0.5f);
            camera.Ev100 = 18f;
            using var render = FinalRenderSupport.RenderToCompletion(renderer, camera, FinalRenderSupport.Settings(8, 8, 4),
                new FinalRenderOptions { BackgroundLuminance = 0f });
            var hdr = FinalRenderSupport.Luminance(render.RawResultHdr(), render.Exposure);
            var beam = BeamDistribution.Create(BeamType.Wash, BeamDeg * MathF.PI / 180f, FieldDeg * MathF.PI / 180f);
            var lensCentre = position + light.Direction * PathTracerScene.LensOffset;
            float dy = Vector3.Distance(eye, lensCentre), dc = Vector3.Distance(eye, position);
            float expected = PeakCandela * beam.Relative(angle) * dy * dy / (MathF.PI * radius * radius * MathF.Cos(angle) * dc * dc);
            float measured = hdr.MeanLuminance(4, 4, 2);
            Assert.True(MathF.Abs(measured - expected) / expected < 0.01f, $"lens {measured} cd/m², expected {expected} cd/m²");
            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    /// <summary>
    /// A translucent white surface (a diffusing gel or a frosted screen hung flat: transmission t, no Fresnel) reflects
    /// the share 1 − t of the direct light like a Lambertian surface, whichever lobe the path continues with — the
    /// transmitted share goes through (into the black surroundings here).
    /// </summary>
    [SkippableTheory]
    [InlineData(0.5f)]
    [InlineData(0.9f)]
    public void TranslucentSurfacesReflectTheirShareOfTheDirectLight(float transmission)
    {
        var renderer = FinalRenderSupport.CreateOrSkip();
        try
        {
            var view = new TopDownView(40, 30, 8f);
            var material = TopDownView.WhiteLambertian();
            material.Transmission = transmission;
            material.DoubleSided = true;
            FinalRenderSupport.Floor(renderer.World, material);
            var light = ProceduralTestScene.Spot(new Vector3(0f, 0f, Height), Vector3.Zero, LightColor.White, PeakCandela, BeamDeg, FieldDeg);
            renderer.World.SetLights(new[] { light });
            using var render = FinalRenderSupport.RenderToCompletion(renderer, view.Camera, FinalRenderSupport.Settings(view.Width, view.Height, 64),
                new FinalRenderOptions { BackgroundLuminance = 0f });
            var hdr = FinalRenderSupport.Luminance(render.RawResultHdr(), render.Exposure);
            var beam = BeamDistribution.Create(BeamType.Wash, BeamDeg * MathF.PI / 180f, FieldDeg * MathF.PI / 180f);
            var (cx, cy, centre) = view.NearestPixel(Vector3.Zero);
            float expected = (1f - transmission) * PhotometricTests.FloorLuminance(light, beam.Relative, centre);
            float measured = hdr.MeanLuminance(cx, cy, 2);
            Assert.True(MathF.Abs(measured - expected) / expected < 0.04f, $"translucent floor {measured} cd/m², expected {expected} cd/m²");
            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    /// <summary>
    /// A lens far too small for float densities (its area underflows) lights the floor like a point light: no
    /// infinite lens luminance or NaN multiple-importance weight wipes out the samples.
    /// </summary>
    [SkippableTheory]
    [InlineData(0f)]
    [InlineData(1e-25f)]
    [InlineData(1e-9f)]
    public void MicroscopicLensesLightLikeAPoint(float lensRadius)
    {
        var renderer = FinalRenderSupport.CreateOrSkip();
        try
        {
            var view = new TopDownView(80, 60, 8f);
            FinalRenderSupport.Floor(renderer.World, TopDownView.WhiteLambertian());
            var light = ProceduralTestScene.Spot(new Vector3(0f, 0f, Height), Vector3.Zero, LightColor.White, PeakCandela, BeamDeg, FieldDeg);
            light.SourceRadius = lensRadius;
            renderer.World.SetLights(new[] { light });
            using var render = FinalRenderSupport.RenderToCompletion(renderer, view.Camera, FinalRenderSupport.Settings(view.Width, view.Height, 4),
                new FinalRenderOptions { BackgroundLuminance = 0f });
            var hdr = FinalRenderSupport.Luminance(render.RawResultHdr(), render.Exposure);
            var beam = BeamDistribution.Create(BeamType.Wash, BeamDeg * MathF.PI / 180f, FieldDeg * MathF.PI / 180f);
            var (cx, cy, centre) = view.NearestPixel(Vector3.Zero);
            float expected = PhotometricTests.FloorLuminance(light, beam.Relative, centre);
            float measured = hdr.MeanLuminance(cx, cy, 1);
            Assert.True(MathF.Abs(measured - expected) / expected < 0.03f, $"floor {measured} cd/m², expected {expected} cd/m²");
            Assert.All(hdr.Rgba, v => Assert.True(float.IsFinite(v)));
            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }
}
