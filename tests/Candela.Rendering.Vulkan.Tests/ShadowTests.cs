using System.Numerics;
using Candela.Photometry;

namespace Candela.Rendering.Vulkan.Tests;

/// <summary>
/// Spot-light shadows: a box casts a shadow on the floor (golden image), points in the umbra receive (near) zero direct
/// light while lit points keep the unshadowed illuminance, InstanceFlags.CastShadow is respected, and cached shadow
/// tiles are re-rendered only when their light or a caster inside their frustum changes.
/// </summary>
public sealed class ShadowTests
{
    private const int Width = 240;
    private const int Height = 180;

    private static readonly Vector3 LightPosition = new(2f, -2f, 5f);

    [SkippableFact]
    public void BoxCastsAShadowOnTheFloor()
    {
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            var scene = BuildBoxScene(renderer, InstanceFlags.CastShadow);
            using var target = renderer.CreateTarget(Width, Height);
            var camera = RenderCamera.LookAt(new Vector3(0.5f, -7f, 4.5f), new Vector3(0f, 0.3f, 0f), 45f);
            camera.Ev100 = 8.5f;
            var image = scene.Render(target, camera, new ViewOptions { ShowGrid = false }, new PreviewSettings { Indirect = IndirectLighting.Off });
            TestVulkan.AssertNoValidationErrors(renderer.Context);
            Assert.Equal(1, renderer.LastStatistics.ShadowedLights);
            GoldenImageTests.CompareWithGolden(image, "shadow-box");
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableFact]
    public void UmbraReceivesNoDirectLightAndLitPointsAreUnchanged()
    {
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            BuildBoxScene(renderer, InstanceFlags.CastShadow);
            var view = new TopDownView(Width, Height, 8f);
            using var target = renderer.CreateTarget(Width, Height);
            var options = new ViewOptions { ShowGrid = false, Overlay = OverlayMode.Lux };
            renderer.Render(target, view.Camera, options, new PreviewSettings(), 0f);
            var lux = renderer.ReadAnalysis(target);
            TestVulkan.AssertNoValidationErrors(renderer.Context);

            var light = StageScene.Spot(LightPosition, Vector3.Zero, 20000f, 40f, 60f);
            var reference = new Photometry.SpotLight(light.Position, light.Direction, light.PeakCandela,
                BeamDistribution.Create(light.BeamType, light.BeamAngle, light.FieldAngle));
            float Lux(Vector3 p)
            {
                var (x, y, _) = view.NearestPixel(p);
                return lux[y * Width + x];
            }

            // The umbra: floor points whose ray to the light passes through the middle of the box (top face at z = 1).
            foreach (var p in new[] { new Vector3(-0.8f, 0.8f, 0f), new Vector3(-0.3f, 0.85f, 0f), new Vector3(-0.75f, 0.25f, 0f) })
            {
                float expectedUnshadowed = LightMeter.IlluminanceAt(new[] { reference }, p, Vector3.UnitZ);
                Assert.True(expectedUnshadowed > 300f, $"the point {p} is inside the beam ({expectedUnshadowed} lx unshadowed)");
                Assert.InRange(Lux(p), 0f, 0.01f * expectedUnshadowed);
            }

            // Lit floor away from the box: unshadowed illuminance (1 %).
            foreach (var p in new[] { new Vector3(1.2f, -1.0f, 0f), new Vector3(0.9f, 0.8f, 0f), new Vector3(-1.5f, -1.2f, 0f) })
            {
                var (x, y, centre) = view.NearestPixel(p);
                float expected = LightMeter.IlluminanceAt(new[] { reference }, centre, Vector3.UnitZ);
                Assert.InRange(MathF.Abs(lux[y * Width + x] - expected) / expected, 0f, 0.01f);
            }
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableFact]
    public void InstancesWithoutCastShadowDoNotShadow()
    {
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            BuildBoxScene(renderer, InstanceFlags.None);
            var view = new TopDownView(Width, Height, 8f);
            using var target = renderer.CreateTarget(Width, Height);
            renderer.Render(target, view.Camera, new ViewOptions { ShowGrid = false, Overlay = OverlayMode.Lux }, new PreviewSettings(), 0f);
            var lux = renderer.ReadAnalysis(target);
            var light = StageScene.Spot(LightPosition, Vector3.Zero, 20000f, 40f, 60f);
            var reference = new Photometry.SpotLight(light.Position, light.Direction, light.PeakCandela,
                BeamDistribution.Create(light.BeamType, light.BeamAngle, light.FieldAngle));
            var (x, y, centre) = view.NearestPixel(new Vector3(-0.8f, 0.8f, 0f));
            float expected = LightMeter.IlluminanceAt(new[] { reference }, centre, Vector3.UnitZ);
            Assert.InRange(MathF.Abs(lux[y * Width + x] - expected) / expected, 0f, 0.01f);
            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableFact]
    public void ShadowTilesAreCachedUntilTheLightOrACasterChanges()
    {
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            var scene = new StageScene(renderer);
            var grey = scene.Lambert(new Vector3(0.6f));
            scene.Floor(12f, grey);
            var box = scene.Box(Vector3.One, new Vector3(0f, 0f, 0.5f), grey);
            var farBox = scene.Box(Vector3.One, new Vector3(30f, 30f, 0.5f), grey); // outside the light's frustum
            var lights = new[] { StageScene.Spot(LightPosition, Vector3.Zero, 20000f, 40f, 60f) };
            renderer.World.SetLights(lights);
            var view = new TopDownView(96, 72, 8f);
            using var target = renderer.CreateTarget(96, 72);
            var options = new ViewOptions { ShowGrid = false };
            var settings = new PreviewSettings { Indirect = IndirectLighting.Off };
            int Frame()
            {
                renderer.Render(target, view.Camera, options, settings, 0f);
                return renderer.LastStatistics.ShadowTilesRendered;
            }

            Assert.Equal(1, Frame());                 // first frame renders the tile
            Assert.Equal(0, Frame());                 // nothing changed: cached
            renderer.World.SetLights(lights);         // same lights again (a new frame of DMX): cached
            Assert.Equal(0, Frame());
            lights[0].PeakCandela = 5000f;            // dimmer, colour, cookie: cached
            lights[0].Color = LightColor.FromRec709(new Vector3(1f, 0.2f, 0.1f)).Rgb;
            lights[0].GoboRotation = 0.7f;
            renderer.World.SetLights(lights);
            Assert.Equal(0, Frame());
            renderer.World.UpdateInstance(farBox, Matrix4x4.CreateTranslation(31f, 30f, 0.5f), InstanceFlags.CastShadow);
            Assert.Equal(0, Frame());                 // a caster outside the frustum moved
            renderer.World.UpdateInstance(box, Matrix4x4.CreateTranslation(0.2f, 0f, 0.5f), InstanceFlags.CastShadow);
            Assert.Equal(1, Frame());                 // a caster inside moved
            renderer.World.UpdateInstance(box, Matrix4x4.CreateTranslation(0.2f, 0f, 0.5f), InstanceFlags.CastShadow | InstanceFlags.Selected);
            Assert.Equal(0, Frame());                 // selection does not change shadows
            lights[0].Position += new Vector3(0f, 0.1f, 0f);
            renderer.World.SetLights(lights);
            Assert.Equal(1, Frame());                 // the light moved
            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableFact]
    public void MoreShadowedLightsThanTheBudgetAreLimitedByPriority()
    {
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            var scene = new StageScene(renderer);
            var grey = scene.Lambert(new Vector3(0.6f));
            scene.Floor(20f, grey);
            var lights = new SpotLight[6];
            for (int i = 0; i < lights.Length; i++)
            {
                var p = new Vector3(-5f + 2f * i, 0f, 5f);
                lights[i] = StageScene.Spot(p, p with { Z = 0f }, 2000f * (i + 1), 30f, 45f);
            }

            renderer.World.SetLights(lights);
            var view = new TopDownView(96, 72, 14f);
            using var target = renderer.CreateTarget(96, 72);
            renderer.Render(target, view.Camera, new ViewOptions { ShowGrid = false }, new PreviewSettings { MaxShadowedLights = 3 }, 0f);
            Assert.Equal(3, renderer.LastStatistics.ShadowedLights);
            // The three brightest lights (equal size and distance otherwise) got the tiles.
            var planner = renderer.Planner;
            var order = planner.Order;
            Assert.Equal(new[] { 5, 4, 3 }, new[] { order[0], order[1], order[2] });
            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    private static StageScene BuildBoxScene(VulkanRenderer renderer, InstanceFlags boxFlags)
    {
        var scene = new StageScene(renderer);
        var grey = scene.Lambert(new Vector3(0.6f));
        var white = scene.Lambert(new Vector3(0.8f));
        scene.Floor(12f, grey);
        scene.Box(Vector3.One, new Vector3(0f, 0f, 0.5f), white, boxFlags);
        renderer.World.SetLights(new[] { StageScene.Spot(LightPosition, Vector3.Zero, 20000f, 40f, 60f) });
        return scene;
    }
}
