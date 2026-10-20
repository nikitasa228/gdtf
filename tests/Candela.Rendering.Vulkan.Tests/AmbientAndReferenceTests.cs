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

            // At opacity 1 the drawing covers what lies behind it completely (not the 50 % of ghost geometry).
            world.UpdateMaterial(material, Drawing(1f), images);
            renderer.Render(target, view.Camera, full, new PreviewSettings { Bloom = false }, 0f);
            hdr = renderer.ReadHdr(target);
            for (int q = 0; q < 4; q++)
            {
                var (x, y, _) = view.NearestPixel(QuadrantPoints[q]);
                var expected = ColorSpaces.Rec709ToRec2020(QuadrantColors[q]) / hdr.Exposure;
                Assert.True(Vector3.Distance(hdr.At(x, y), expected) <= 0.02f * expected.Length(), $"opaque drawing, quadrant {q}: {hdr.At(x, y)}");
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

            // In haze the surroundings light the surfaces, not the air: the background stays black.
            world.SetAtmosphere(new AtmosphereState { BaseScattering = 0.05f, AmbientLuminance = 30f });
            for (int frame = 0; frame < 3; frame++)
                renderer.Render(target, view.Camera, options, settings, frame / 30f);
            var hazy = renderer.ReadHdr(target);
            Assert.True(hazy.LuminanceAt(bx, by) < 1e-3f, $"the haze glows with the ambient: {hazy.LuminanceAt(bx, by)} cd/m²");
            Assert.InRange(hazy.LuminanceAt(fx, fy), 15f * 0.9f, 15f * 1.01f);

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
