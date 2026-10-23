using System.Numerics;
using Candela.Core.Geometry;
using Candela.Photometry;

namespace Candela.Rendering.Vulkan.Tests;

/// <summary>
/// The editor's lighting baseline in the renderer. Reference drawings (an alpha-blended textured material on an
/// <see cref="InstanceFlags.Reference"/> instance) show their image unlit at their own opacity in the Full and Colorless
/// views; ghost geometry keeps the fixed 50 %. The luminance of the surroundings (<see cref="AtmosphereState.AmbientLuminance"/>)
/// lights surfaces that no light reaches: a white Lambertian surface open to it has its luminance, in the real-time view
/// (unoccluded) and in the path tracer (traced), while the background and the haze stay as they were.
/// </summary>
public sealed class AmbientAndReferenceTests
{
    private const int Size = 96;

    /// <summary>Linear Rec.709 colours of the drawing's quadrants: red top-left, green top-right, blue bottom-left, white bottom-right.</summary>
    private static readonly Vector3[] QuadrantColors = [new(1f, 0f, 0f), new(0f, 1f, 0f), new(0f, 0f, 1f), Vector3.One];

    /// <summary>
    /// Centres of those quadrants on a 2 × 2 m plane (<see cref="ProceduralMeshes.Plane"/>: U along +X, V along −Y, so
    /// the image's top row lies at +Y).
    /// </summary>
    private static readonly Vector3[] QuadrantPoints = [new(-0.5f, 0.5f, 0f), new(0.5f, 0.5f, 0f), new(-0.5f, -0.5f, 0f), new(0.5f, -0.5f, 0f)];

    /// <summary>An 8 × 8 sRGB image of four colour quadrants (4 × 4 texels each, so the quadrant centres sample one colour).</summary>
    private static ImageData Quadrants()
    {
        const int n = 8;
        var pixels = new byte[n * n * 4];
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                var c = QuadrantColors[(y < n / 2 ? 0 : 2) + (x < n / 2 ? 0 : 1)];
                int i = 4 * (y * n + x);
                pixels[i] = (byte)(c.X * 255f);
                pixels[i + 1] = (byte)(c.Y * 255f);
                pixels[i + 2] = (byte)(c.Z * 255f);
                pixels[i + 3] = 255;
            }
        }

        return new ImageData { Width = n, Height = n, Pixels = pixels };
    }

    /// <summary>The material of a reference drawing (as Candela.Assets' image planes make it): the image as base colour, blended at the opacity.</summary>
    private static MaterialData Drawing(float opacity) => new()
    {
        Name = "Reference drawing",
        BaseColorFactor = new Vector4(1f, 1f, 1f, opacity),
        BaseColorTexture = "drawing",
        AlphaMode = AlphaMode.Blend,
        Roughness = 1f,
        DoubleSided = true,
        UvScale = new Vector2(0.5f), // the 2 m plane's UVs are in metres: the image once over the plane
    };

    private static float Luminance709(Vector3 rgb) => 0.2126f * rgb.X + 0.7152f * rgb.Y + 0.0722f * rgb.Z;

    /// <summary>The display pixel shows the quadrant's colour: its dominant channels, not black.</summary>
    private static void AssertShowsColor(ImageData display, int x, int y, Vector3 color, string what)
    {
        int i = 4 * (y * display.Width + x);
        var rgb = new Vector3(display.Pixels[i], display.Pixels[i + 1], display.Pixels[i + 2]);
        Assert.True(rgb.X + rgb.Y + rgb.Z > 90f, $"{what}: the drawing is (nearly) black there: {rgb}");
        if (color == Vector3.One)
        {
            Assert.True(MathF.Min(rgb.X, MathF.Min(rgb.Y, rgb.Z)) > 0.8f * MathF.Max(rgb.X, MathF.Max(rgb.Y, rgb.Z)), $"{what}: white shows as {rgb}");
            return;
        }

        // the channel of the quadrant's colour stands out against the two others
        float own = Vector3.Dot(rgb, color);
        float others = Vector3.Dot(rgb, Vector3.One - color);
        Assert.True(own > 1.5f * others / 2f + 40f, $"{what}: {color} shows as {rgb}");
    }

    [SkippableFact]
    public void A_reference_drawing_shows_its_image_unlit_at_its_own_opacity()
    {
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            var world = renderer.World;
            var images = new SceneTests.ImageSource { ["drawing"] = Quadrants() };
            var plane = world.CreateMesh(ProceduralMeshes.Plane(2f, 2f));
            var material = world.CreateMaterial(Drawing(0.6f), images);
            var instance = world.CreateInstance(plane, new[] { material }, Matrix4x4.CreateTranslation(0f, 0f, 0.01f), InstanceFlags.Reference, 1);
            // a bright stage light and the ambient do not change a drawing: it is shown as it is
            world.SetLights([StageSceneLights.Overhead(20000f)]);
            world.SetAtmosphere(new AtmosphereState { BaseScattering = 0f, AmbientLuminance = 30f });
            var view = new TopDownView(Size, Size, 3f);
            using var target = renderer.CreateTarget(Size, Size);
            var full = new ViewOptions { ShowGrid = false, BackgroundLuminance = 0f };

            renderer.Render(target, view.Camera, full, new PreviewSettings { Bloom = false }, 0f);
            var hdr = renderer.ReadHdr(target);
            var display = renderer.ReadPixels(target);
            for (int q = 0; q < 4; q++)
            {
                var (x, y, _) = view.NearestPixel(QuadrantPoints[q]);
                // Full: the base colour as a display value (pre-exposed), at the drawing's opacity over the black background
                var expected = ColorSpaces.Rec709ToRec2020(QuadrantColors[q]) * 0.6f / hdr.Exposure;
                var measured = hdr.At(x, y);
                Assert.True(Vector3.Distance(measured, expected) <= 0.02f * expected.Length(), $"Full, quadrant {q}: {measured} instead of {expected}");
                AssertShowsColor(display, x, y, QuadrantColors[q], $"Full, quadrant {q}");
            }

            // Colorless: also the image (display-linear Rec.709) at 60 % over the dark grey background, not grey shading.
            renderer.Render(target, view.Camera, new ViewOptions { Shading = ViewShading.Colorless, ShowGrid = false }, new PreviewSettings(), 0f);
            var colorless = renderer.ReadHdr(target);
            var colorlessDisplay = renderer.ReadPixels(target);
            const float background = 0.022f;
            for (int q = 0; q < 4; q++)
            {
                var (x, y, _) = view.NearestPixel(QuadrantPoints[q]);
                var expected = QuadrantColors[q] * 0.6f + new Vector3(0.4f * background);
                Assert.True(Vector3.Distance(colorless.At(x, y), expected) < 0.01f, $"Colorless, quadrant {q}: {colorless.At(x, y)} instead of {expected}");
                AssertShowsColor(colorlessDisplay, x, y, QuadrantColors[q], $"Colorless, quadrant {q}");
            }

            // At opacity 1 the drawing covers what lies behind it completely (not the 50 % of ghost geometry), and the
            // display shows the image's own pixels: the display-referred colour is not tone mapped, whichever tone mapper.
            world.UpdateMaterial(material, Drawing(1f), images);
            foreach (var toneMapper in new[] { ToneMapper.AgX, ToneMapper.Aces, ToneMapper.Neutral })
            {
                renderer.Render(target, view.Camera, full, new PreviewSettings { Bloom = false, ToneMapper = toneMapper }, 0f);
                hdr = renderer.ReadHdr(target);
                display = renderer.ReadPixels(target);
                for (int q = 0; q < 4; q++)
                {
                    var (x, y, _) = view.NearestPixel(QuadrantPoints[q]);
                    var expected = ColorSpaces.Rec709ToRec2020(QuadrantColors[q]) / hdr.Exposure;
                    Assert.True(Vector3.Distance(hdr.At(x, y), expected) <= 0.02f * expected.Length(), $"opaque drawing, quadrant {q}: {hdr.At(x, y)}");
                    int i = 4 * (y * display.Width + x);
                    var shown = new Vector3(display.Pixels[i], display.Pixels[i + 1], display.Pixels[i + 2]);
                    Assert.True(Vector3.Distance(shown, QuadrantColors[q] * 255f) <= 4f, $"{toneMapper}, quadrant {q}: the image's pixel shows as {shown}");
                }
            }

            // The same material on a normal (lit) instance is a print lit by the stage, not the display value.
            world.UpdateInstance(instance, Matrix4x4.CreateTranslation(0f, 0f, 0.01f), InstanceFlags.None);
            renderer.Render(target, view.Camera, full, new PreviewSettings { Bloom = false }, 0f);
            hdr = renderer.ReadHdr(target);
            var (wx, wy, _) = view.NearestPixel(QuadrantPoints[3]);
            Assert.True(MathF.Abs(hdr.LuminanceAt(wx, wy) * hdr.Exposure - 1f) > 0.1f, "a lit print shows the stage light, not the display value");
            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableFact]
    public void The_path_tracer_shows_a_reference_drawing_to_the_camera_only()
    {
        // A drawing at 60 % hangs 1 m over a grey floor under a spot. The final render shows the image as the real-time
        // view does (the base colour as a display value) over 40 % of the floor, and the floor under it is lit as if the
        // drawing were not there: it neither shadows nor reflects light.
        var renderer = FinalRenderSupport.CreateOrSkip();
        try
        {
            var world = renderer.World;
            var floor = TopDownView.WhiteLambertian();
            floor.BaseColorFactor = new Vector4(0.5f, 0.5f, 0.5f, 1f);
            FinalRenderSupport.Floor(world, floor, 8f);
            world.SetLights([StageSceneLights.Overhead(4000f)]);
            world.SetAtmosphere(new AtmosphereState { BaseScattering = 0f });
            var images = new SceneTests.ImageSource { ["drawing"] = Quadrants() };
            var plane = world.CreateMesh(ProceduralMeshes.Plane(2f, 2f));
            var material = world.CreateMaterial(Drawing(0.6f), images);
            var at = Matrix4x4.CreateTranslation(0f, 0f, 1f);
            var drawing = world.CreateInstance(plane, new[] { material }, at, InstanceFlags.Reference, 1);
            const int size = 48;
            var view = new TopDownView(size, size, 3f);
            var settings = FinalRenderSupport.Settings(size, size, 64, bounces: 3);
            var options = new FinalRenderOptions { BackgroundLuminance = 0f };

            HdrImage withDrawing, floorOnly;
            float exposure;
            using (var render = FinalRenderSupport.RenderToCompletion(renderer, view.Camera, settings, options))
            {
                exposure = render.Exposure;
                withDrawing = FinalRenderSupport.Luminance(render.ResultHdr(), render.Exposure);
                FinalRenderSupport.Save(render, "pt-reference-drawing");
            }

            world.UpdateInstance(drawing, at, InstanceFlags.Reference | InstanceFlags.Hidden);
            using (var render = FinalRenderSupport.RenderToCompletion(renderer, view.Camera, settings, options))
            {
                Assert.Equal(exposure, render.Exposure);
                floorOnly = FinalRenderSupport.Luminance(render.ResultHdr(), render.Exposure);
            }

            for (int q = 0; q < 4; q++)
            {
                var (x, y, _) = view.NearestPixel(QuadrantPoints[q] + new Vector3(0f, 0f, 1f));
                var floorColor = floorOnly.MeanColor(x, y, 2);
                Assert.True(ColorSpaces.LuminanceRec2020(floorColor) > 1f, $"quadrant {q}: the floor is not lit ({floorColor})");
                var expected = ColorSpaces.Rec709ToRec2020(QuadrantColors[q]) * 0.6f / exposure + 0.4f * floorColor;
                var measured = withDrawing.MeanColor(x, y, 2);
                Assert.True(Vector3.Distance(measured, expected) <= 0.05f * expected.Length(), $"final render, quadrant {q}: {measured} instead of {expected}");
            }

            // Beside the drawing the floor is the same with and without it (no light reflected from it, no shadow).
            var (sx, sy, _) = view.NearestPixel(new Vector3(1.3f, 0f, 0f));
            Assert.InRange(withDrawing.MeanLuminance(sx, sy, 1), floorOnly.MeanLuminance(sx, sy, 1) * 0.95f, floorOnly.MeanLuminance(sx, sy, 1) * 1.05f);
            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableFact]
    public void A_reference_drawing_does_not_drive_the_real_time_auto_exposure()
    {
        // A drawing is written after the exposure (a display value): metering it as scene light would chase its own
        // tail (the exposure darkens the stage until it is black). The lit floor keeps its exposure with the drawing.
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            var world = renderer.World;
            var floor = TopDownView.WhiteLambertian();
            floor.BaseColorFactor = new Vector4(0.5f, 0.5f, 0.5f, 1f);
            FinalRenderSupport.Floor(world, floor, 8f);
            world.SetLights([StageSceneLights.Overhead(4000f)]);
            world.SetAtmosphere(new AtmosphereState { BaseScattering = 0f });
            var images = new SceneTests.ImageSource { ["drawing"] = Quadrants() };
            var plane = world.CreateMesh(ProceduralMeshes.Plane(2f, 2f));
            var material = world.CreateMaterial(Drawing(1f), images);
            // 1 m × 1 m in a corner of the view, 1 m above the floor: it hides little of the lit floor the meter reads
            var at = Matrix4x4.CreateScale(0.5f) * Matrix4x4.CreateTranslation(0.9f, 0.9f, 1f);
            var drawing = world.CreateInstance(plane, new[] { material }, at, InstanceFlags.Reference, 1);
            var view = new TopDownView(Size, Size, 3f);
            using var target = renderer.CreateTarget(Size, Size);
            var full = new ViewOptions { ShowGrid = false, BackgroundLuminance = 0f };
            var preview = new PreviewSettings { Bloom = false, AutoExposure = true };

            float Adapted()
            {
                for (int frame = 0; frame < 40; frame++)
                    renderer.Render(target, view.Camera, full, preview, 0.5f);
                return renderer.ReadHdr(target).Exposure;
            }

            float withDrawing = Adapted();
            var display = renderer.ReadPixels(target);
            world.UpdateInstance(drawing, at, InstanceFlags.Reference | InstanceFlags.Hidden);
            float floorOnly = Adapted();
            Assert.InRange(MathF.Log2(withDrawing / floorOnly), -0.25f, 0.25f);
            for (int q = 0; q < 4; q++)
            {
                var (x, y, _) = view.NearestPixel(Vector3.Transform(QuadrantPoints[q], at));
                AssertShowsColor(display, x, y, QuadrantColors[q], $"auto exposure, quadrant {q}");
            }

            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableFact]
    public void The_path_tracer_shows_a_reference_drawing_as_the_real_time_view_whatever_the_exposure()
    {
        // The final render's auto exposure meters the scene light only and ends far from the camera's EV 2: the drawing
        // still shows the same display pixels as the real-time view (an opaque drawing over the lit floor: its image's
        // colours, not tone mapped), and the exposure is the one of the floor alone.
        var renderer = FinalRenderSupport.CreateOrSkip();
        try
        {
            var world = renderer.World;
            var floor = TopDownView.WhiteLambertian();
            floor.BaseColorFactor = new Vector4(0.5f, 0.5f, 0.5f, 1f);
            FinalRenderSupport.Floor(world, floor, 8f);
            world.SetLights([StageSceneLights.Overhead(4000f)]);
            world.SetAtmosphere(new AtmosphereState { BaseScattering = 0f });
            var images = new SceneTests.ImageSource { ["drawing"] = Quadrants() };
            var plane = world.CreateMesh(ProceduralMeshes.Plane(2f, 2f));
            var material = world.CreateMaterial(Drawing(1f), images);
            // 1 m × 1 m in a corner of the view, 1 m above the floor: it hides little of the lit floor the meter reads
            var at = Matrix4x4.CreateScale(0.5f) * Matrix4x4.CreateTranslation(0.9f, 0.9f, 1f);
            var drawing = world.CreateInstance(plane, new[] { material }, at, InstanceFlags.Reference, 1);
            const int size = 48;
            var view = new TopDownView(size, size, 3f, ev100: 2f);
            var settings = FinalRenderSupport.Settings(size, size, 16, bounces: 2);
            var options = new FinalRenderOptions { BackgroundLuminance = 0f, AutoExposure = true };

            using var target = renderer.CreateTarget(size, size);
            renderer.Render(target, view.Camera, new ViewOptions { ShowGrid = false, BackgroundLuminance = 0f }, new PreviewSettings { Bloom = false }, 0f);
            var realTime = renderer.ReadPixels(target);

            ImageData finalDisplay;
            float exposure;
            using (var render = FinalRenderSupport.RenderToCompletion(renderer, view.Camera, settings, options))
            {
                finalDisplay = render.Preview();
                exposure = render.Exposure;
                FinalRenderSupport.Save(render, "pt-reference-drawing-auto-exposure");
            }

            float camera = Core.Math.Units.ExposureFromEv100(2f);
            Assert.True(MathF.Abs(MathF.Log2(exposure / camera)) > 3f, $"the auto exposure stayed at the camera's EV ({exposure} vs {camera})");
            for (int q = 0; q < 4; q++)
            {
                var (x, y, _) = view.NearestPixel(Vector3.Transform(QuadrantPoints[q], at));
                int i = 4 * (y * size + x);
                var pt = new Vector3(finalDisplay.Pixels[i], finalDisplay.Pixels[i + 1], finalDisplay.Pixels[i + 2]);
                var rt = new Vector3(realTime.Pixels[i], realTime.Pixels[i + 1], realTime.Pixels[i + 2]);
                Assert.True(Vector3.Distance(pt, rt) <= 6f, $"quadrant {q}: the final render shows {pt}, the real-time view {rt}");
                AssertShowsColor(finalDisplay, x, y, QuadrantColors[q], $"final render, quadrant {q}");
            }

            world.UpdateInstance(drawing, at, InstanceFlags.Reference | InstanceFlags.Hidden);
            using (var render = FinalRenderSupport.RenderToCompletion(renderer, view.Camera, settings, options))
            {
                render.Preview();
                Assert.InRange(MathF.Log2(render.Exposure / exposure), -0.25f, 0.25f);
            }

            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableFact]
    public void The_ambient_luminance_lights_surfaces_in_the_full_view()
    {
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            var world = renderer.World;
            // A grey Lambertian floor (IOR 1: no specular) and a white mirror-like metal square, both away from any light.
            var floor = TopDownView.WhiteLambertian();
            floor.BaseColorFactor = new Vector4(0.5f, 0.5f, 0.5f, 1f);
            FinalRenderSupport.Floor(world, floor, 6f);
            var metalMesh = world.CreateMesh(ProceduralMeshes.Plane(1f, 1f));
            var metal = world.CreateMaterial(new MaterialData { Name = "chrome", BaseColorFactor = Vector4.One, Metallic = 1f, Roughness = 0.05f }, null);
            world.CreateInstance(metalMesh, new[] { metal }, Matrix4x4.CreateTranslation(1.5f, 1.5f, 0.01f), InstanceFlags.None, 2);
            var view = new TopDownView(Size, Size, 10f, ev100: 6f);
            using var target = renderer.CreateTarget(Size, Size);
            var options = new ViewOptions { ShowGrid = false, BackgroundLuminance = 0f };
            var settings = new PreviewSettings { Bloom = false };
            var (fx, fy, _) = view.NearestPixel(new Vector3(-1f, -1f, 0f));
            var (mx, my, _) = view.NearestPixel(new Vector3(1.5f, 1.5f, 0f));
            var (bx, by, _) = view.NearestPixel(new Vector3(4.5f, 4.5f, 0f)); // beyond the 6 m floor: the background

            world.SetAtmosphere(new AtmosphereState { BaseScattering = 0f, AmbientLuminance = 0f });
            renderer.Render(target, view.Camera, options, settings, 0f);
            var dark = renderer.ReadHdr(target);
            var darkColorless = RenderColorless();
            Assert.True(dark.LuminanceAt(fx, fy) < 1e-3f, $"no light, no ambient: {dark.LuminanceAt(fx, fy)} cd/m²");

            // 30 cd/m² of surroundings: the floor reflects ρ·L = 15 cd/m² (a uniform environment over a Lambertian surface)
            // and the chrome nearly all of it; the background and the Colorless view do not change.
            world.SetAtmosphere(new AtmosphereState { BaseScattering = 0f, AmbientLuminance = 30f });
            renderer.Render(target, view.Camera, options, settings, 0f);
            var lit = renderer.ReadHdr(target);
            Assert.InRange(lit.LuminanceAt(fx, fy), 15f * 0.99f, 15f * 1.01f);
            Assert.InRange(lit.LuminanceAt(mx, my), 30f * 0.9f, 30f * 1.01f);
            Assert.True(lit.LuminanceAt(bx, by) < 1e-3f, $"the background shows the ambient: {lit.LuminanceAt(bx, by)} cd/m²");
            var litColorless = RenderColorless();
            Assert.Equal(darkColorless.Rgba, litColorless.Rgba);

            // In haze the surroundings light the surfaces, not the air: the background stays black, and the floor seen
            // through 10 m of haze (σ = 0.05 /m) keeps its 15 cd/m² times the transmittance e^(−0.5).
            world.SetAtmosphere(new AtmosphereState { BaseScattering = 0.05f, AmbientLuminance = 30f });
            for (int frame = 0; frame < 3; frame++)
                renderer.Render(target, view.Camera, options, settings, frame / 30f);
            var hazy = renderer.ReadHdr(target);
            Assert.True(hazy.LuminanceAt(bx, by) < 1e-3f, $"the haze glows with the ambient: {hazy.LuminanceAt(bx, by)} cd/m²");
            float transmitted = 15f * MathF.Exp(-0.05f * 10f);
            Assert.InRange(hazy.LuminanceAt(fx, fy), transmitted * 0.97f, transmitted * 1.03f);

            // Damaged values light nothing.
            world.SetAtmosphere(new AtmosphereState { BaseScattering = 0f, AmbientLuminance = float.NaN });
            renderer.Render(target, view.Camera, options, settings, 0f);
            Assert.True(renderer.ReadHdr(target).LuminanceAt(fx, fy) < 1e-3f);
            world.SetAtmosphere(new AtmosphereState { BaseScattering = 0f, AmbientLuminance = -5f });
            renderer.Render(target, view.Camera, options, settings, 0f);
            Assert.True(renderer.ReadHdr(target).LuminanceAt(fx, fy) < 1e-3f);
            TestVulkan.AssertNoValidationErrors(renderer.Context);

            HdrReadback RenderColorless()
            {
                renderer.Render(target, view.Camera, new ViewOptions { Shading = ViewShading.Colorless, ShowGrid = false }, settings, 0f);
                return renderer.ReadHdr(target);
            }
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableFact]
    public void The_ambient_luminance_lights_surfaces_in_the_path_tracer()
    {
        var renderer = FinalRenderSupport.CreateOrSkip();
        try
        {
            var world = renderer.World;
            var floor = TopDownView.WhiteLambertian();
            floor.BaseColorFactor = new Vector4(0.5f, 0.5f, 0.5f, 1f);
            FinalRenderSupport.Floor(world, floor, 6f);
            var view = new TopDownView(32, 32, 10f, ev100: 6f);
            var settings = FinalRenderSupport.Settings(32, 32, 16, bounces: 4);
            var options = new FinalRenderOptions { BackgroundLuminance = 0f };
            var (fx, fy, _) = view.NearestPixel(new Vector3(-1f, -1f, 0f));
            var (bx, by, _) = view.NearestPixel(new Vector3(4.5f, 4.5f, 0f));

            world.SetAtmosphere(new AtmosphereState { BaseScattering = 0f, AmbientLuminance = 0f });
            using (var dark = FinalRenderSupport.RenderToCompletion(renderer, view.Camera, settings, options))
            {
                var hdr = FinalRenderSupport.Luminance(dark.ResultHdr(), dark.Exposure);
                Assert.True(hdr.MeanLuminance(fx, fy, 1) < 1e-3f, $"no light, no ambient: {hdr.MeanLuminance(fx, fy, 1)} cd/m²");
            }

            // An open floor under 30 cd/m² of surroundings: every ray from it escapes to them, ρ·L = 15 cd/m²; the
            // background itself is not the ambient.
            world.SetAtmosphere(new AtmosphereState { BaseScattering = 0f, AmbientLuminance = 30f });
            using (var lit = FinalRenderSupport.RenderToCompletion(renderer, view.Camera, settings, options))
            {
                var hdr = FinalRenderSupport.Luminance(lit.ResultHdr(), lit.Exposure);
                Assert.InRange(hdr.MeanLuminance(fx, fy, 1), 15f * 0.97f, 15f * 1.03f);
                Assert.True(hdr.MeanLuminance(bx, by, 1) < 1e-3f, $"the background shows the ambient: {hdr.MeanLuminance(bx, by, 1)} cd/m²");
            }

            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableFact]
    public void The_ambient_light_reaches_surfaces_through_haze_alike_in_both_renderers()
    {
        // The default stage haze (0.02/m) scatters as much of a uniform surroundings' light into a ray as out of it, so
        // an open floor under 30 cd/m² keeps ρ·L however far the haze reaches (the path tracer's haze box extends 30 m
        // beyond the scene); only the camera's own 10 m through the haze dims it, in both renderers.
        var renderer = FinalRenderSupport.CreateOrSkip();
        try
        {
            var world = renderer.World;
            var floor = TopDownView.WhiteLambertian();
            floor.BaseColorFactor = new Vector4(0.5f, 0.5f, 0.5f, 1f);
            FinalRenderSupport.Floor(world, floor, 6f);
            world.SetAtmosphere(new AtmosphereState { BaseScattering = 0.02f, AmbientLuminance = 30f });
            var view = new TopDownView(32, 32, 10f, ev100: 6f);
            var (fx, fy, _) = view.NearestPixel(new Vector3(-1f, -1f, 0f));

            using var target = renderer.CreateTarget(32, 32);
            var full = new ViewOptions { ShowGrid = false, BackgroundLuminance = 0f };
            var preview = new PreviewSettings { Bloom = false, TemporalAccumulation = false };
            for (int frame = 0; frame < 4; frame++)
                renderer.Render(target, view.Camera, full, preview, 0f);
            float realTime = renderer.ReadHdr(target).LuminanceAt(fx, fy);

            using var render = FinalRenderSupport.RenderToCompletion(renderer, view.Camera, FinalRenderSupport.Settings(32, 32, 32, bounces: 4),
                new FinalRenderOptions { BackgroundLuminance = 0f });
            float traced = FinalRenderSupport.Luminance(render.ResultHdr(), render.Exposure).MeanLuminance(fx, fy, 1);

            // The real-time view: the floor's ρ·L dimmed by the camera's 10 m of haze. The path tracer also scatters the
            // floor's own light back towards the camera in that haze (at most the floor's luminance); attenuated on its
            // way to the floor, its ambient was about half of the real-time view's (6.2 instead of 12.3 cd/m²).
            float transmittance = MathF.Exp(-0.02f * (10f - view.Camera.Near));
            float expected = 15f * transmittance;
            Assert.InRange(realTime, expected * 0.95f, expected * 1.05f);
            Assert.InRange(traced, expected * 0.95f, expected + (1f - transmittance) * 15f);
            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableFact]
    public void A_lambertian_sphere_in_ambient_surroundings_matches_between_the_views()
    {
        // The furnace with the ambient instead of the background: a white Lambertian sphere (convex, so nothing occludes
        // its surroundings) has exactly the ambient luminance in the real-time view and in the path tracer.
        var renderer = FinalRenderSupport.CreateOrSkip();
        try
        {
            var world = renderer.World;
            var mesh = world.CreateMesh(ProceduralMeshes.Sphere(1f, 96, 48));
            world.CreateInstance(mesh, new[] { world.CreateMaterial(TopDownView.WhiteLambertian(), null) }, Matrix4x4.Identity, InstanceFlags.None, 1);
            world.SetAtmosphere(new AtmosphereState { BaseScattering = 0f, AmbientLuminance = 1f });
            var camera = RenderCamera.LookAt(new Vector3(0f, -6f, 0f), Vector3.Zero, 30f);
            camera.Ev100 = 0f;
            int c = 32;

            using var target = renderer.CreateTarget(64, 64);
            renderer.Render(target, camera, new ViewOptions { ShowGrid = false, BackgroundLuminance = 0f }, new PreviewSettings { Bloom = false }, 0f);
            var realTime = renderer.ReadHdr(target);
            Assert.InRange(realTime.LuminanceAt(c, c), 0.99f, 1.01f);
            Assert.InRange(realTime.LuminanceAt(c + 8, c - 6), 0.99f, 1.01f);

            using var render = FinalRenderSupport.RenderToCompletion(renderer, camera, FinalRenderSupport.Settings(64, 64, 16, bounces: 4),
                new FinalRenderOptions { BackgroundLuminance = 0f });
            var traced = FinalRenderSupport.Luminance(render.ResultHdr(), render.Exposure);
            Assert.InRange(traced.MeanLuminance(c, c, 2), 0.97f, 1.03f);
            Assert.True(traced.MeanLuminance(2, 2, 1) < 1e-3f, "the background is not the ambient");
            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    /// <summary>Lights for the tests (a bright light straight down over the origin).</summary>
    private static class StageSceneLights
    {
        public static SpotLight Overhead(float candela) =>
            StageScene.Spot(new Vector3(0f, 0f, 5f), Vector3.Zero, candela, 40f, 60f, shadows: false);
    }
}
