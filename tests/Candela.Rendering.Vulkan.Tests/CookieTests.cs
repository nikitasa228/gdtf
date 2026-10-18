using System.Numerics;
using Candela.Photometry;

namespace Candela.Rendering.Vulkan.Tests;

/// <summary>
/// Light cookies projected straight down onto a white floor (top view): gobo pattern and rotation, framing blades
/// cutting straight edges, prism copies of the beam, the iris shrinking the spot, frost widening it. Golden images plus
/// illuminance checks through the lux overlay (the cookie applies to surface light, the lux overlay and the
/// volumetrics alike).
/// </summary>
public sealed class CookieTests
{
    private const int Width = 200;
    private const int Height = 150;
    private const float Height0 = 5f;
    private const float PeakCandela = 20000f;

    [SkippableFact]
    public void GoboIsProjectedOntoTheFloor()
    {
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            var gobo = renderer.World.CreateTexture(StageScene.GoboImage(), srgb: true);
            var light = Light(20f, 26f);
            light.Gobo = gobo;
            var (image, lux, view) = RenderBoth(renderer, light);
            GoldenImageTests.CompareWithGolden(image, "cookie-gobo");

            // The bow tie is open along ±X and closed along ±Y (half the field radius from the centre).
            float r = 0.5f * Height0 * MathF.Tan(13f * MathF.PI / 180f);
            float open = LuxAt(lux, view, new Vector3(r, 0f, 0f));
            float closed = LuxAt(lux, view, new Vector3(0f, -r, 0f));
            float meter = Meter(light, new Vector3(r, 0f, 0f));
            Assert.InRange(open / meter, 0.8f, 1.01f);
            Assert.InRange(closed, 0f, 0.05f * meter);

            // Rotating the gobo by 90° swaps open and closed.
            light.GoboRotation = MathF.PI / 2f;
            var (_, rotated, _) = RenderBoth(renderer, light);
            Assert.InRange(LuxAt(rotated, view, new Vector3(r, 0f, 0f)), 0f, 0.05f * meter);
            Assert.InRange(LuxAt(rotated, view, new Vector3(0f, -r, 0f)) / meter, 0.8f, 1.01f);
            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableFact]
    public void FramingBladesCutStraightEdges()
    {
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            var light = Light(20f, 26f);
            light.BladeInsertion = new Vector4(0.5f, 0.35f, 0f, 0f); // A (top) half in, B (right) a third in
            light.BladeAngle = new Vector4(0f, 0.25f, 0f, 0f);       // B tilted
            var (image, lux, view) = RenderBoth(renderer, light);
            GoldenImageTests.CompareWithGolden(image, "cookie-blades");

            // Blade A: the edge is a horizontal line at y = (1 - 0.5) x the cut-off offset in gate units.
            var beam = BeamDistribution.Create(BeamType.Spot, light.BeamAngle, light.FieldAngle);
            float gateTan = MathF.Tan(0.5f * beam.FieldAngle);
            float edgeY = 0.5f * MathF.Tan(beam.CutoffAngle) / gateTan * gateTan * Height0;
            foreach (float x in new[] { -0.4f, 0f, 0.2f })
            {
                Assert.InRange(LuxAt(lux, view, new Vector3(x, edgeY + 0.12f, 0f)), 0f, 0.01f * Meter(light, Vector3.Zero));
                var below = new Vector3(x, edgeY - 0.15f, 0f);
                Assert.InRange(LuxAt(lux, view, below) / Meter(light, below), 0.95f, 1.01f);
            }

            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableFact]
    public void ThreeFacetPrismMakesThreeSpots()
    {
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            var light = Light(10f, 13f);
            light.PrismFacets = 3;
            light.PrismSpread = 10f * MathF.PI / 180f;
            light.PrismRotation = 0.3f;
            var (image, lux, view) = RenderBoth(renderer, light);
            GoldenImageTests.CompareWithGolden(image, "cookie-prism");

            // Each facet carries a third of the intensity around its own axis.
            float r = Height0 * MathF.Tan(light.PrismSpread);
            var single = Light(10f, 13f);
            for (int k = 0; k < 3; k++)
            {
                float a = light.PrismRotation + 2f * MathF.PI * k / 3f;
                var centre = new Vector3(r * MathF.Cos(a), r * MathF.Sin(a), 0f);
                float expected = PeakCandela / 3f * Height0 / MathF.Pow(centre.Length() * centre.Length() + Height0 * Height0, 1.5f);
                Assert.InRange(LuxAt(lux, view, centre) / expected, 0.9f, 1.05f);
            }

            Assert.InRange(LuxAt(lux, view, Vector3.Zero), 0f, 0.05f * Meter(single, Vector3.Zero) / 3f); // dark between the spots
            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableFact]
    public void IrisShrinksTheSpot()
    {
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            // Two lights side by side: open (left) and iris at 35 % (right).
            var open = Light(20f, 26f, new Vector3(-1.3f, 0f, Height0));
            var iris = Light(20f, 26f, new Vector3(1.3f, 0f, Height0));
            iris.Iris = 0.35f;
            var (image, lux, view) = RenderBoth(renderer, open, iris);
            GoldenImageTests.CompareWithGolden(image, "cookie-iris");

            float aperture = Height0 * MathF.Tan(0.35f * 13f * MathF.PI / 180f);
            var centre = new Vector3(1.3f, 0f, 0f);
            Assert.InRange(LuxAt(lux, view, centre) / Meter(iris, centre), 0.98f, 1.01f);                 // centre unchanged
            Assert.InRange(LuxAt(lux, view, centre + new Vector3(aperture * 1.25f, 0f, 0f)), 0f, 1f); // outside the aperture
            var openPoint = new Vector3(-1.3f + aperture * 1.25f, 0f, 0f);
            Assert.InRange(LuxAt(lux, view, openPoint) / Meter(open, openPoint), 0.98f, 1.01f);          // the open beam is lit there
            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableFact]
    public void FrostWidensTheBeamAndBlursTheGobo()
    {
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            var gobo = renderer.World.CreateTexture(StageScene.GoboImage(), srgb: true);
            var sharp = Light(20f, 26f);
            sharp.Gobo = gobo;
            var (_, sharpLux, view) = RenderBoth(renderer, sharp);
            var frosted = sharp;
            frosted.Frost = 1f;
            var (_, frostLux, _) = RenderBoth(renderer, frosted);

            float r = 0.5f * Height0 * MathF.Tan(13f * MathF.PI / 180f);
            var closed = new Vector3(0f, -r, 0f);
            float far = 1.6f;
            // The closed sector fills in (blur) and light reaches beyond the sharp beam's cut-off (wider beam).
            Assert.True(LuxAt(frostLux, view, closed) > 5f * MathF.Max(LuxAt(sharpLux, view, closed), 0.5f), "frost blurs the gobo");
            Assert.True(LuxAt(frostLux, view, new Vector3(far, 0f, 0f)) > LuxAt(sharpLux, view, new Vector3(far, 0f, 0f)) + 1f, "frost widens the beam");
            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    private static SpotLight Light(float beamDeg, float fieldDeg, Vector3? position = null)
    {
        var p = position ?? new Vector3(0f, 0f, Height0);
        return StageScene.Spot(p, p with { Z = 0f }, PeakCandela, beamDeg, fieldDeg, shadows: false, type: BeamType.Spot);
    }

    private static float Meter(SpotLight light, Vector3 point) =>
        LightMeter.IlluminanceAt(new[]
        {
            new Photometry.SpotLight(light.Position, light.Direction, light.PeakCandela, BeamDistribution.Create(light.BeamType, light.BeamAngle, light.FieldAngle)),
        }, point, Vector3.UnitZ);

    private static float LuxAt(float[] lux, TopDownView view, Vector3 point)
    {
        var (x, y, _) = view.NearestPixel(point);
        return lux[y * view.Width + x];
    }

    /// <summary>Renders the floor lit by <paramref name="lights"/>: the display image (Full view) and the lux values.</summary>
    private static (Candela.Core.Geometry.ImageData Image, float[] Lux, TopDownView View) RenderBoth(VulkanRenderer renderer, params SpotLight[] lights)
    {
        var scene = new StageScene(renderer);
        var white = scene.Lambert(new Vector3(0.8f));
        var floor = scene.Floor(12f, white, InstanceFlags.None);
        renderer.World.SetLights(lights);
        var view = new TopDownView(Width, Height, 4f, ev100: 10.5f);
        using var target = renderer.CreateTarget(Width, Height);
        var settings = new PreviewSettings { Indirect = IndirectLighting.Off };
        var image = scene.Render(target, view.Camera, new ViewOptions { ShowGrid = false }, settings);
        renderer.Render(target, view.Camera, new ViewOptions { ShowGrid = false, Overlay = OverlayMode.Lux }, settings, 0f);
        var lux = renderer.ReadAnalysis(target);
        renderer.World.DestroyInstance(floor);
        renderer.World.DestroyMaterial(white);
        return (image, lux, view);
    }
}
