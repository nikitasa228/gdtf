using System.Numerics;
using Candela.Core.Geometry;
using Candela.Photometry;

namespace Candela.Rendering.Vulkan.Tests;

/// <summary>
/// The GPU reproduces the photometric model. Spot lights: one 10 000 cd spot straight down from 5 m onto a white
/// Lambertian floor (albedo 1, roughness 1, IOR 1 → no specular); luminance L = ρ·E/π with E = I(θ)·cos α / d².
/// Emissive surfaces and the background show their luminance in cd/m². Values are read from the pre-tone-mapping HDR
/// image divided by the exposure.
/// </summary>
public sealed class PhotometricTests
{
    private const float Height = 5f;
    private const float PeakCandela = 10000f;
    private const float BeamDeg = 50f;
    private const float FieldDeg = 80f;
    private const int Width = 320;
    private const int ImageHeight = 240;

    [SkippableFact]
    public void PoolCentreAndOffAxisLuminanceMatchThePhotometricModel()
    {
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            var view = new TopDownView(Width, ImageHeight, 8f);
            var light = ProceduralTestScene.Spot(new Vector3(0f, 0f, Height), Vector3.Zero, LightColor.White, PeakCandela, BeamDeg, FieldDeg);
            var hdr = RenderFloor(renderer, view, light);
            Assert.Equal(Core.Math.Units.ExposureFromEv100(7f), hdr.Exposure, 6);

            var beam = BeamDistribution.Create(BeamType.Wash, BeamDeg * MathF.PI / 180f, FieldDeg * MathF.PI / 180f);
            float Expected(Vector3 p) => FloorLuminance(light, beam.Relative, p);

            // Pool centre: exactly 10000 / 25 / π cd/m² under the light.
            var (cx, cy, centre) = view.NearestPixel(Vector3.Zero);
            float centreLuminance = hdr.LuminanceAt(cx, cy);
            float centreExpected = Expected(centre);
            Assert.InRange(MathF.Abs(centreExpected - PeakCandela / 25f / MathF.PI) / centreExpected, 0f, 2e-3f);
            Assert.InRange(MathF.Abs(centreLuminance - centreExpected) / centreExpected, 0f, 0.02f);

            // White light on a white Lambertian surface: equal channels (Rec.2020 white is (1, 1, 1) at luminance 1).
            var rgb = hdr.At(cx, cy);
            Assert.InRange(MathF.Abs(rgb.X - rgb.Z) / rgb.Y, 0f, 0.01f);

            // 30° off axis: L(θ) = L(0) · profile(θ) · cos³θ.
            float r = Height * MathF.Tan(30f * MathF.PI / 180f);
            foreach (var direction in new[] { Vector3.UnitX, -Vector3.UnitY, Vector3.Normalize(new Vector3(-1f, 1f, 0f)) })
            {
                var (px, py, p) = view.NearestPixel(direction * r);
                float measured = hdr.LuminanceAt(px, py);
                float expected = Expected(p);
                Assert.InRange(MathF.Abs(measured - expected) / expected, 0f, 0.03f);
            }

            // The model itself: profile(30°)·cos³(30°) relative to the centre.
            float theta30 = 30f * MathF.PI / 180f;
            float ratio = Expected(Vector3.UnitX * r) / Expected(Vector3.Zero);
            Assert.Equal(beam.Relative(theta30) * MathF.Pow(MathF.Cos(theta30), 3f), ratio, 4);

            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableFact]
    public void ColouredLightKeepsItsLuminanceAndChromaticity()
    {
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            var view = new TopDownView(Width, ImageHeight, 8f);
            var color = LightColor.FromRec709(new Vector3(0.1f, 0.3f, 1f));
            var light = ProceduralTestScene.Spot(new Vector3(0f, 0f, Height), Vector3.Zero, color, PeakCandela, BeamDeg, FieldDeg);
            var hdr = RenderFloor(renderer, view, light);
            var (cx, cy, _) = view.NearestPixel(Vector3.Zero);
            var measured = hdr.At(cx, cy);
            float expectedLuminance = PeakCandela / 25f / MathF.PI;
            Assert.InRange(MathF.Abs(hdr.LuminanceAt(cx, cy) - expectedLuminance) / expectedLuminance, 0f, 0.02f);
            var expected = color.Rgb * expectedLuminance;
            Assert.InRange(Vector3.Distance(measured, expected) / expectedLuminance, 0f, 0.02f);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableFact]
    public void IrisNarrowsAndFrostWidensTheBeam()
    {
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            var view = new TopDownView(Width, ImageHeight, 8f);
            var open = ProceduralTestScene.Spot(new Vector3(0f, 0f, Height), Vector3.Zero, LightColor.White, PeakCandela, 20f, 30f);
            float At(HdrReadback hdr, float thetaDeg)
            {
                var (x, y, _) = view.NearestPixel(new Vector3(Height * MathF.Tan(thetaDeg * MathF.PI / 180f), 0f, 0f));
                return hdr.LuminanceAt(x, y);
            }

            var openHdr = RenderFloor(renderer, view, open);
            float openCentre = At(openHdr, 0f), openEdge = At(openHdr, 13f);

            // Beyond the cut-off of the open beam the light contributes nothing.
            var beam = BeamDistribution.Create(BeamType.Wash, open.BeamAngle, open.FieldAngle);
            Assert.InRange(At(openHdr, beam.CutoffAngle * 180f / MathF.PI + 2f), 0f, 1e-4f * openCentre);

            var iris = open;
            iris.Iris = 0.4f; // aperture half-angle 0.4 · 15° = 6°
            var irisHdr = RenderFloor(renderer, view, iris);
            Assert.InRange(MathF.Abs(At(irisHdr, 0f) - openCentre) / openCentre, 0f, 0.02f); // a gate stop: centre unchanged
            Assert.InRange(At(irisHdr, 8f), 0f, 1e-3f * openCentre);                           // outside the aperture: dark
            Assert.True(openEdge > 0.05f * openCentre);

            var frost = open;
            frost.Frost = 1f;
            var frostHdr = RenderFloor(renderer, view, frost);
            Assert.True(At(frostHdr, 0f) < 0.9f * openCentre, "frost lowers the peak");
            Assert.True(At(frostHdr, 20f) > 2f * At(openHdr, 20f), "frost widens the beam");
            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableFact]
    public void EmissiveSurfaceAndBackgroundShowTheirLuminance()
    {
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            var world = renderer.World;
            var panel = world.CreateMesh(ProceduralMeshes.Plane(2f, 2f));
            var led = world.CreateMaterial(new MaterialData
            {
                Name = "LED wall",
                BaseColorFactor = new Vector4(0f, 0f, 0f, 1f),
                Roughness = 1f,
                Ior = 1f,
                EmissiveColor = new Vector3(1f, 0.5f, 0.25f),
                EmissiveLuminance = 150f,
            }, null);
            world.CreateInstance(panel, new[] { led }, Matrix4x4.Identity, InstanceFlags.None, 1);

            var view = new TopDownView(Width, ImageHeight, 8f, ev100: 9f);
            using var target = renderer.CreateTarget(Width, ImageHeight);
            var options = new ViewOptions { Shading = ViewShading.Full, ShowGrid = false, BackgroundLuminance = 0.5f };
            renderer.Render(target, view.Camera, options, new PreviewSettings(), 0f);
            var hdr = renderer.ReadHdr(target);

            // Emissive: luminance 150 cd/m² times the luminance of the (Rec.709) emissive colour.
            var (cx, cy, _) = view.NearestPixel(Vector3.Zero);
            float colourLuminance = ColorSpaces.LuminanceRec2020(ColorSpaces.Rec709ToRec2020(new Vector3(1f, 0.5f, 0.25f)));
            float expected = 150f * colourLuminance;
            Assert.InRange(MathF.Abs(hdr.LuminanceAt(cx, cy) - expected) / expected, 0f, 0.02f);

            // Background (outside the 2 m panel): ViewOptions.BackgroundLuminance.
            var (bx, by, _) = view.NearestPixel(new Vector3(3f, 3f, 0f));
            Assert.InRange(MathF.Abs(hdr.LuminanceAt(bx, by) - 0.5f) / 0.5f, 0f, 0.02f);
            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    /// <summary>Luminance of the white Lambertian floor at <paramref name="p"/> lit by <paramref name="light"/>.</summary>
    internal static float FloorLuminance(in SpotLight light, Func<float, float> relative, Vector3 p)
    {
        var toPoint = p - light.Position;
        float d = toPoint.Length();
        float cosAlpha = MathF.Max(-toPoint.Z / d, 0f);                     // incidence on the floor (normal +Z)
        float theta = MathF.Acos(Math.Clamp(Vector3.Dot(toPoint / d, light.Direction), -1f, 1f));
        float illuminance = light.PeakCandela * relative(theta) * cosAlpha / (d * d);
        return illuminance / MathF.PI;                                       // ρ = 1
    }

    /// <summary>Renders a 40 m white Lambertian floor lit by one light (no grid) and reads the HDR luminance.</summary>
    private static HdrReadback RenderFloor(VulkanRenderer renderer, TopDownView view, SpotLight light)
    {
        var world = renderer.World;
        var floor = world.CreateMesh(ProceduralMeshes.Plane(40f, 40f));
        var white = world.CreateMaterial(TopDownView.WhiteLambertian(), null);
        var instance = world.CreateInstance(floor, new[] { white }, Matrix4x4.Identity, InstanceFlags.None, 1);
        world.SetLights(new[] { light });
        using var target = renderer.CreateTarget(view.Width, view.Height);
        renderer.Render(target, view.Camera, new ViewOptions { Shading = ViewShading.Full, ShowGrid = false, BackgroundLuminance = 0f },
            new PreviewSettings(), 0f);
        var hdr = renderer.ReadHdr(target);
        world.DestroyInstance(instance);
        world.DestroyMaterial(white);
        world.DestroyMesh(floor);
        return hdr;
    }
}
