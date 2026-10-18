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

    [SkippableFact]
    public void EveryShadowCasterWithinTheBudgetGetsAWorkingTile()
    {
        // Twelve equally important lights, each over its own box: every one must be shadowed. (The atlas holds four
        // full-size tiles; sizing every equally scored light at full size used to leave all but four unshadowed.)
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            var scene = new StageScene(renderer);
            var grey = scene.Lambert(new Vector3(0.6f));
            scene.Floor(30f, grey);
            var lights = new SpotLight[12];
            var boxes = new Vector3[lights.Length];
            for (int i = 0; i < lights.Length; i++)
            {
                var p = new Vector3(-7.5f + 5f * (i % 4), -5f + 5f * (i / 4), 4f);
                lights[i] = StageScene.Spot(p, p with { Z = 0f }, 8000f, 30f, 40f);
                boxes[i] = new Vector3(p.X - 0.5f, p.Y, 0.5f);
                scene.Box(new Vector3(0.5f, 0.5f, 1f), boxes[i], grey); // its shadow on the floor: x from -0.75 to -1 m off the light
            }

            renderer.World.SetLights(lights);
            var view = new TopDownView(Width, Height, 16f);
            using var target = renderer.CreateTarget(Width, Height);
            var settings = new PreviewSettings();
            renderer.Render(target, view.Camera, new ViewOptions { ShowGrid = false, Overlay = OverlayMode.Lux }, settings, 0f);
            Assert.Equal(lights.Length, renderer.LastStatistics.ShadowedLights);
            var lux = renderer.ReadAnalysis(target);
            var reference = lights.Select(l => new Photometry.SpotLight(l.Position, l.Direction, l.PeakCandela,
                BeamDistribution.Create(l.BeamType, l.BeamAngle, l.FieldAngle))).ToArray();
            for (int i = 0; i < lights.Length; i++)
            {
                // Behind the box as seen from its light: in the umbra.
                var (x, y, centre) = view.NearestPixel(new Vector3(lights[i].Position.X - 0.875f, lights[i].Position.Y, 0f));
                float unshadowed = LightMeter.IlluminanceAt(reference, centre, Vector3.UnitZ);
                Assert.True(lux[y * Width + x] < 0.02f * unshadowed, $"light {i}: {lux[y * Width + x]:F1} lx in the umbra, {unshadowed:F1} lx unshadowed");
            }

            // Reflective shadow maps of every shadowed light share one budget of virtual point lights.
            renderer.Render(target, view.Camera, new ViewOptions { ShowGrid = false }, settings, 0f);
            Assert.InRange(renderer.LastStatistics.VirtualPointLights, 1, LightPlanner.VplBudget);
            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableFact]
    public void AFullAtlasMakesRoomForNewLights()
    {
        // 256 tiles (the most the atlas has), then 256 other lights: the stale tiles are evicted for the new ones.
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            var scene = new StageScene(renderer);
            scene.Floor(40f, scene.Lambert(new Vector3(0.6f)));
            var view = new TopDownView(64, 64, 34f);
            using var target = renderer.CreateTarget(64, 64);
            var settings = new PreviewSettings { MaxShadowedLights = 256, Volumetrics = false, Indirect = IndirectLighting.Off, Bloom = false };
            SpotLight[] Lights(float z)
            {
                var lights = new SpotLight[256];
                for (int i = 0; i < lights.Length; i++)
                {
                    var p = new Vector3(-15f + 2f * (i % 16), -15f + 2f * (i / 16), z);
                    lights[i] = StageScene.Spot(p, p with { Z = 0f }, 5000f, 20f, 30f);
                    lights[i].FixtureId = Guid.NewGuid();
                }

                return lights;
            }

            renderer.World.SetLights(Lights(4f));
            renderer.Render(target, view.Camera, new ViewOptions { ShowGrid = false }, settings, 0f);
            Assert.Equal(256, renderer.LastStatistics.ShadowedLights);
            renderer.World.SetLights(Lights(5f));
            renderer.Render(target, view.Camera, new ViewOptions { ShowGrid = false }, settings, 0f);
            Assert.Equal(256, renderer.LastStatistics.ShadowedLights);
            Assert.Equal(256, renderer.LastStatistics.ShadowTilesRendered);
            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableFact]
    public void TwoViewsInOneFrameShareTheAtlasWithoutReRendering()
    {
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            var scene = new StageScene(renderer);
            var grey = scene.Lambert(new Vector3(0.6f));
            scene.Floor(40f, grey);
            scene.Box(Vector3.One, new Vector3(0f, 0f, 0.5f), grey);
            var lights = new SpotLight[8];
            for (int i = 0; i < lights.Length; i++)
            {
                var p = new Vector3(-10.5f + 3f * i, 0f, 6f);
                lights[i] = StageScene.Spot(p, p with { Z = 0f }, 20000f, 30f, 45f);
            }

            renderer.World.SetLights(lights);
            using var a = renderer.CreateTarget(96, 72);
            using var b = renderer.CreateTarget(96, 72);
            var near = RenderCamera.LookAt(new Vector3(-9f, -4f, 3f), new Vector3(-9f, 0f, 0f), 50f);
            var wide = RenderCamera.LookAt(new Vector3(0f, -25f, 12f), Vector3.Zero, 60f);
            var options = new ViewOptions { ShowGrid = false };
            var settings = new PreviewSettings { Volumetrics = false };
            int tiles = -1, shadowedA = 0, shadowedB = 0;
            for (int frame = 0; frame < 6; frame++)
            {
                var f = renderer.Context.BeginFrame();
                renderer.Render(a, near, options, settings, 1f / 60f);
                tiles = renderer.LastStatistics.ShadowTilesRendered;
                shadowedA = renderer.LastStatistics.ShadowedLights;
                renderer.Render(b, wide, options, settings, 1f / 60f);
                tiles += renderer.LastStatistics.ShadowTilesRendered;
                shadowedB = renderer.LastStatistics.ShadowedLights;
                renderer.Context.SubmitFrame(f);
            }

            Assert.Equal(0, tiles); // steady state: the views keep each other's tiles
            Assert.Equal(lights.Length, shadowedB);
            Assert.True(shadowedA > 0);
            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableFact]
    public void GrazingLightOnAShadowCastingFloorHasNoAcne()
    {
        // Robust bias: a floor that casts shadows itself, lit at 10-20 degrees elevation, keeps the unshadowed
        // illuminance (1 %) everywhere in the pools.
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            var scene = new StageScene(renderer);
            scene.Floor(30f, scene.Lambert(new Vector3(0.5f)), InstanceFlags.CastShadow);
            var lights = new[]
            {
                StageScene.Spot(new Vector3(-8f, 0f, 1.5f), Vector3.Zero, 200000f, 20f, 30f),
                StageScene.Spot(new Vector3(0f, -9f, 3f), new Vector3(0f, 1f, 0f), 100000f, 30f, 45f),
            };
            lights[0].SourceRadius = 0.1f;
            lights[1].SourceRadius = 0.05f;
            renderer.World.SetLights(lights);
            var view = new TopDownView(Width, Height, 12f);
            using var target = renderer.CreateTarget(Width, Height);
            renderer.Render(target, view.Camera, new ViewOptions { ShowGrid = false, Overlay = OverlayMode.Lux }, new PreviewSettings(), 0f);
            var lux = renderer.ReadAnalysis(target);
            TestVulkan.AssertNoValidationErrors(renderer.Context);
            var reference = lights.Select(l => new Photometry.SpotLight(l.Position, l.Direction, l.PeakCandela,
                BeamDistribution.Create(l.BeamType, l.BeamAngle, l.FieldAngle))).ToArray();
            int n = 0;
            for (int py = 0; py < Height; py += 3)
            {
                for (int px = 0; px < Width; px += 3)
                {
                    float expected = LightMeter.IlluminanceAt(reference, view.FloorPointOf(px, py), Vector3.UnitZ);
                    if (expected < 20f)
                        continue;
                    n++;
                    Assert.True(MathF.Abs(lux[py * Width + px] - expected) <= 0.01f * expected,
                        $"pixel ({px}, {py}): {lux[py * Width + px]:F1} lx, light meter {expected:F1} lx");
                }
            }

            Assert.True(n > 500, $"{n} lit points");
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableTheory]
    [InlineData(0f)]
    [InlineData(0.15f)]
    public void LitSphereHasNoSelfShadowAcne(float sourceRadius)
    {
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            var scene = new StageScene(renderer);
            var grey = scene.Lambert(new Vector3(0.5f));
            scene.Floor(20f, grey);
            var centre = new Vector3(0f, 0f, 1f);
            scene.Sphere(1f, centre, grey);
            var light = StageScene.Spot(new Vector3(-3f, -1f, 6f), centre, 50000f, 40f, 60f);
            light.SourceRadius = sourceRadius;
            renderer.World.SetLights(new[] { light });
            var view = new TopDownView(200, 200, 3f);
            using var target = renderer.CreateTarget(200, 200);
            renderer.Render(target, view.Camera, new ViewOptions { ShowGrid = false, Overlay = OverlayMode.Lux }, new PreviewSettings(), 0f);
            var lux = renderer.ReadAnalysis(target);
            TestVulkan.AssertNoValidationErrors(renderer.Context);
            var reference = new[] { new Photometry.SpotLight(light.Position, light.Direction, light.PeakCandela,
                BeamDistribution.Create(light.BeamType, light.BeamAngle, light.FieldAngle)) };
            int n = 0;
            for (int y = 0; y < 200; y += 2)
            {
                for (int x = 0; x < 200; x += 2)
                {
                    var floor = view.FloorPointOf(x, y);
                    float r2 = floor.X * floor.X + floor.Y * floor.Y;
                    if (r2 > 0.64f)
                        continue;
                    var p = new Vector3(floor.X, floor.Y, centre.Z + MathF.Sqrt(1f - r2)); // the sphere seen from above
                    var normal = Vector3.Normalize(p - centre);
                    if (Vector3.Dot(normal, Vector3.Normalize(light.Position - p)) < 0.35f)
                        continue; // away from the terminator (the mesh is tessellated)
                    float expected = LightMeter.IlluminanceAt(reference, p, normal);
                    Assert.True(MathF.Abs(lux[y * 200 + x] - expected) <= 0.04f * expected,
                        $"{p}: {lux[y * 200 + x]:F1} lx, light meter {expected:F1} lx");
                    n++;
                }
            }

            Assert.True(n > 500, $"{n} lit sphere points");
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
