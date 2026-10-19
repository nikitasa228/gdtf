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
    /// A fixture's front (a cover or a lens mesh standing out of the head) 3 cm in front of its lens, inside the
    /// clearance shadow rays leave out: it shadows nothing, so it hides nothing either — looking into the beam the camera
    /// sees the lens's luminance through it, the floor gets the analytic illuminance; seen from outside the beam, where
    /// the lens is dark, the front itself shows (its emission here), not a black lens.
    /// </summary>
    [SkippableFact]
    public void LensBehindTheFixturesFrontStillShines()
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
            var scene = new StageScene(renderer);
            var front = renderer.World.CreateMaterial(new MaterialData
            {
                Name = "fixture front",
                BaseColorFactor = new Vector4(0.05f, 0.05f, 0.05f, 1f),
                EmissiveLuminance = 5f,
                Roughness = 1f,
                Ior = 1f,
            }, null);
            scene.Box(new Vector3(0.4f, 0.4f, 0.01f), position - new Vector3(0f, 0f, 0.03f), front, InstanceFlags.CastShadow);
            FinalRenderSupport.Floor(renderer.World, TopDownView.WhiteLambertian());
            var beam = BeamDistribution.Create(BeamType.Wash, BeamDeg * MathF.PI / 180f, FieldDeg * MathF.PI / 180f);

            // into the beam, from 4.5 m below the lens
            var eye = new Vector3(0f, 0f, 0.5f);
            var camera = RenderCamera.LookAt(eye, position, 0.5f);
            camera.Ev100 = 18f;
            using (var render = FinalRenderSupport.RenderToCompletion(renderer, camera, FinalRenderSupport.Settings(8, 8, 4),
                       new FinalRenderOptions { BackgroundLuminance = 0f }))
            {
                var lens = position + light.Direction * PathTracerScene.LensOffset;
                float dy = Vector3.Distance(eye, lens), dc = Vector3.Distance(eye, position);
                float expected = PeakCandela * dy * dy / (MathF.PI * radius * radius * dc * dc);
                float measured = FinalRenderSupport.Luminance(render.RawResultHdr(), render.Exposure).MeanLuminance(4, 4, 2);
                Assert.True(MathF.Abs(measured - expected) / expected < 0.01f, $"lens through the front {measured} cd/m², expected {expected} cd/m²");
            }

            // the floor under it
            var view = new TopDownView(40, 30, 8f);
            using (var render = FinalRenderSupport.RenderToCompletion(renderer, view.Camera, FinalRenderSupport.Settings(view.Width, view.Height, 32),
                       new FinalRenderOptions { BackgroundLuminance = 0f }))
            {
                var hdr = FinalRenderSupport.Luminance(render.RawResultHdr(), render.Exposure);
                var (cx, cy, centre) = view.NearestPixel(new Vector3(1f, 0f, 0f));
                float expected = PhotometricTests.FloorLuminance(light, beam.Relative, centre);
                float measured = hdr.MeanLuminance(cx, cy, 1);
                Assert.True(MathF.Abs(measured - expected) / expected < 0.03f, $"floor {measured} cd/m², expected {expected} cd/m²");
            }

            // from the side, 70° off the axis (outside the 80° field): the front, not a dark lens
            var side = position + 0.6f * new Vector3(MathF.Sin(70f * MathF.PI / 180f), 0f, -MathF.Cos(70f * MathF.PI / 180f));
            var sideCamera = RenderCamera.LookAt(side, position + new Vector3(0.04f, 0f, -0.03f), 2f);
            sideCamera.Ev100 = 3f;
            using (var render = FinalRenderSupport.RenderToCompletion(renderer, sideCamera, FinalRenderSupport.Settings(8, 8, 4, bounces: 0),
                       new FinalRenderOptions { BackgroundLuminance = 0f }))
            {
                float measured = FinalRenderSupport.Luminance(render.RawResultHdr(), render.Exposure).MeanLuminance(4, 4, 1);
                Assert.True(MathF.Abs(measured - 5f) / 5f < 0.02f, $"the fixture's front from the side {measured} cd/m², its emission 5 cd/m²");
            }

            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    /// <summary>
    /// Beams of one fixture share its front: a weak glow beam (a GDTF Glow geometry around the cells, as on Фокина's
    /// panels) whose lens lies a millimetre in front of a strong beam's lens must not hide it. Lenses are emitters, not
    /// occluders: looking into both beams the camera sees the sum of their luminances, and the floor under them gets
    /// both lights (next-event estimation and BSDF samples that pass one lens to reach the other agree).
    /// </summary>
    [SkippableFact]
    public void OverlappingLensesAddUp()
    {
        var renderer = FinalRenderSupport.CreateOrSkip();
        try
        {
            renderer.World.SetAtmosphere(new AtmosphereState { BaseScattering = 0f });
            const float radius = 0.1f;
            var strong = ProceduralTestScene.Spot(new Vector3(0f, 0f, Height), Vector3.Zero, LightColor.White, PeakCandela, BeamDeg, FieldDeg);
            strong.SourceRadius = radius;
            var glow = ProceduralTestScene.Spot(new Vector3(0.02f, 0f, Height - 0.001f), new Vector3(0.02f, 0f, 0f), LightColor.White, 50f, 100f, 160f);
            glow.SourceRadius = radius;
            float LensLuminance(SpotLight light, Vector3 eye)
            {
                var beam = BeamDistribution.Create(light.BeamType, light.BeamAngle, light.FieldAngle);
                var toEye = eye - light.Position;
                float angle = MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(toEye), light.Direction), -1f, 1f));
                var lens = light.Position + light.Direction * PathTracerScene.LensOffset;
                float dy = Vector3.Distance(eye, lens), dc = toEye.Length();
                return light.PeakCandela * beam.Relative(angle) * dy * dy / (MathF.PI * radius * radius * MathF.Cos(angle) * dc * dc);
            }

            var eye = new Vector3(0f, 0f, 0.5f);
            var camera = RenderCamera.LookAt(eye, strong.Position, 0.5f);
            camera.Ev100 = 18f;
            renderer.World.SetLights(new[] { strong, glow });
            using (var render = FinalRenderSupport.RenderToCompletion(renderer, camera, FinalRenderSupport.Settings(8, 8, 4),
                       new FinalRenderOptions { BackgroundLuminance = 0f }))
            {
                float measured = FinalRenderSupport.Luminance(render.RawResultHdr(), render.Exposure).MeanLuminance(4, 4, 2);
                float expected = LensLuminance(strong, eye) + LensLuminance(glow, eye);
                Assert.True(MathF.Abs(measured - expected) / expected < 0.01f, $"lenses {measured} cd/m², expected {expected} cd/m²");
            }

            // a glossy floor under both: the lens integral of the two lights
            FinalRenderSupport.Floor(renderer.World, new MaterialData
            {
                Name = "glossy",
                BaseColorFactor = new Vector4(0.9f, 0.9f, 0.9f, 1f),
                Metallic = 1f,
                Roughness = 0.2f,
            });
            var view = new TopDownView(16, 16, 0.4f);
            using (var render = FinalRenderSupport.RenderToCompletion(renderer, view.Camera, FinalRenderSupport.Settings(16, 16, 256, bounces: 1),
                       new FinalRenderOptions { BackgroundLuminance = 0f, Seed = 2 }))
            {
                var hdr = FinalRenderSupport.Luminance(render.RawResultHdr(), render.Exposure);
                renderer.World.SetLights(new[] { strong });
                using var strongOnly = FinalRenderSupport.RenderToCompletion(renderer, view.Camera, FinalRenderSupport.Settings(16, 16, 256, bounces: 1),
                    new FinalRenderOptions { BackgroundLuminance = 0f, Seed = 2 });
                renderer.World.SetLights(new[] { glow });
                using var glowOnly = FinalRenderSupport.RenderToCompletion(renderer, view.Camera, FinalRenderSupport.Settings(16, 16, 256, bounces: 1),
                    new FinalRenderOptions { BackgroundLuminance = 0f, Seed = 2 });
                float both = hdr.MeanLuminance(8, 8, 3);
                float sum = FinalRenderSupport.Luminance(strongOnly.RawResultHdr(), strongOnly.Exposure).MeanLuminance(8, 8, 3) +
                            FinalRenderSupport.Luminance(glowOnly.RawResultHdr(), glowOnly.Exposure).MeanLuminance(8, 8, 3);
                Assert.True(MathF.Abs(both - sum) / sum < 0.03f, $"floor under both lenses {both} cd/m², sum of each alone {sum} cd/m²");
            }

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
