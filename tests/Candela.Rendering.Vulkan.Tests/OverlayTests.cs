using System.Numerics;
using Candela.Core.Geometry;
using Candela.Photometry;

namespace Candela.Rendering.Vulkan.Tests;

/// <summary>
/// Analysis overlays and editor visuals: the lux overlay agrees with <see cref="LightMeter"/>, the noise overlay with
/// <see cref="Acoustics"/>, gradients and out-of-range colours, the cable overlay, the selection outline (also through
/// occluders), beam outlines and reference drawings.
/// </summary>
public sealed class OverlayTests
{
    private const int Width = 240;
    private const int Height = 180;

    [SkippableFact]
    public void LuxOverlayMatchesTheLightMeter()
    {
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            var scene = new StageScene(renderer);
            scene.Floor(20f, scene.Lambert(new Vector3(0.5f)), InstanceFlags.None);
            var lights = new[]
            {
                StageScene.Spot(new Vector3(-2f, -1f, 4f), new Vector3(-1f, 0f, 0f), 30000f, 25f, 40f, shadows: false),
                StageScene.Spot(new Vector3(2.5f, 1f, 6f), new Vector3(1f, 0.5f, 0f), 50000f, 15f, 20f,
                    LightColor.FromRec709(new Vector3(1f, 0.3f, 0.1f)), shadows: false, type: BeamType.Spot),
                StageScene.Spot(new Vector3(0f, 3f, 3f), new Vector3(0f, 1f, 0f), 2000f, 60f, 100f,
                    LightColor.FromCct(3200f), shadows: false, type: BeamType.Fresnel),
                StageScene.Spot(new Vector3(-3f, 2.5f, 2f), new Vector3(-3f, 2.5f, 0f), 800f, 120f, 160f, shadows: false, type: BeamType.Glow),
            };
            renderer.World.SetLights(lights);
            var view = new TopDownView(Width, Height, 10f);
            using var target = renderer.CreateTarget(Width, Height);
            renderer.Render(target, view.Camera, new ViewOptions { ShowGrid = false, Overlay = OverlayMode.Lux }, new PreviewSettings(), 0f);
            var lux = renderer.ReadAnalysis(target);
            TestVulkan.AssertNoValidationErrors(renderer.Context);

            var reference = lights.Select(l => new Photometry.SpotLight(l.Position, l.Direction, l.PeakCandela,
                BeamDistribution.Create(l.BeamType, l.BeamAngle, l.FieldAngle), LightColor.FromRec2020(l.Color))).ToArray();
            int checkedPoints = 0;
            for (float y = -4f; y <= 4f; y += 0.8f)
            {
                for (float x = -6f; x <= 6f; x += 0.9f)
                {
                    var (px, py, centre) = view.NearestPixel(new Vector3(x, y, 0f));
                    float expected = LightMeter.IlluminanceAt(reference, centre, Vector3.UnitZ);
                    if (expected < 5f)
                        continue; // only lit points (the 1 % criterion is meaningless at a few lux of tail)
                    float measured = lux[py * Width + px];
                    Assert.True(MathF.Abs(measured - expected) <= 0.01f * expected,
                        $"({x:F1}, {y:F1}): {measured:F2} lx, light meter {expected:F2} lx");
                    checkedPoints++;
                }
            }

            Assert.True(checkedPoints > 30, $"{checkedPoints} lit sample points");
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    /// <summary>
    /// 1320 beams overlapping around the origin (a dense rig like Фокина's 1295 cells, where the cluster lists used to be
    /// cut at 512 lights and floor tiles read a fraction of the light): the lux overlay equals the light meter within
    /// 1 % at every lit sample point, from above (orthographic) and in perspective (the depth slices), the busiest
    /// cluster holds all 1320 lights and nothing overflows. The first frame has only a few lights, so the cluster masks
    /// also grow on the way.
    /// </summary>
    [SkippableFact]
    public void LuxOverlayOfThirteenHundredOverlappingBeamsMatchesTheLightMeter()
    {
        const int count = 1320;
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            var scene = new StageScene(renderer);
            scene.Floor(30f, scene.Lambert(new Vector3(0.5f)), InstanceFlags.None);
            var rig = new OverlappingBeams(count);
            Assert.Equal(count, rig.LightsReaching(Vector3.Zero));
            var options = new ViewOptions { ShowGrid = false, Overlay = OverlayMode.Lux };
            var top = new TopDownView(Width, Height, 10f);
            using var target = renderer.CreateTarget(Width, Height);
            renderer.World.SetLights(rig.Lights.AsSpan(0, 12));
            renderer.Render(target, top.Camera, options, new PreviewSettings(), 0f);
            uint smallCapacity = ((VulkanRenderTarget)target).ClusterMaskCapacity;
            renderer.World.SetLights(rig.Lights);
            renderer.Render(target, top.Camera, options, new PreviewSettings(), 0f);
            var lux = renderer.ReadAnalysis(target);
            var lists = renderer.ReadLightLists(target);
            TestVulkan.AssertNoValidationErrors(renderer.Context);

            Assert.True(((VulkanRenderTarget)target).ClusterMaskCapacity > smallCapacity, "the cluster masks grew for 1320 lights");
            Assert.Equal(new LightListDiagnostics(count, count, count, 0), lists);
            int checkedPoints = 0;
            for (float y = -4f; y <= 4f; y += 0.53f)
            {
                for (float x = -6f; x <= 6f; x += 0.61f)
                {
                    var (px, py, point) = top.NearestPixel(new Vector3(x, y, 0f));
                    checkedPoints += CheckLux(lux[py * Width + px], LightMeter.IlluminanceAt(rig.Reference, point, Vector3.UnitZ), point) ? 1 : 0;
                }
            }

            Assert.True(checkedPoints > 200, $"{checkedPoints} lit sample points (orthographic)");

            // Perspective: the same floor seen at an angle, so the overlapping beams spread over many depth slices.
            var camera = RenderCamera.LookAt(new Vector3(0f, -9f, 6f), new Vector3(0f, 0.5f, 0f), 55f);
            renderer.Render(target, camera, options, new PreviewSettings(), 0f);
            lux = renderer.ReadAnalysis(target);
            TestVulkan.AssertNoValidationErrors(renderer.Context);
            Assert.False(renderer.ReadLightLists(target).HasOverflow);
            var frame = CameraFrame.Compute(camera, Width / (float)Height);
            checkedPoints = 0;
            for (int py = 3; py < Height; py += 7)
            {
                for (int px = 3; px < Width; px += 7)
                {
                    if (FloorPointOf(frame.InverseViewProjection, px, py) is not { } point || MathF.Abs(point.X) > 14f || MathF.Abs(point.Y) > 14f)
                        continue;
                    checkedPoints += CheckLux(lux[py * Width + px], LightMeter.IlluminanceAt(rig.Reference, point, Vector3.UnitZ), point) ? 1 : 0;
                }
            }

            Assert.True(checkedPoints > 200, $"{checkedPoints} lit sample points (perspective)");
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }

        static bool CheckLux(float measured, float expected, Vector3 point)
        {
            if (expected < 5f)
                return false; // only lit points (the 1 % criterion is meaningless at a few lux of tail)
            Assert.True(MathF.Abs(measured - expected) <= 0.01f * expected,
                $"({point.X:F2}, {point.Y:F2}): {measured:F1} lx, light meter {expected:F1} lx");
            return true;
        }
    }

    /// <summary>The floor point (Z = 0) seen through the centre of pixel (x, y), or null when the ray misses the floor.</summary>
    private static Vector3? FloorPointOf(Matrix4x4 inverseViewProjection, int x, int y)
    {
        float nx = (x + 0.5f) / Width * 2f - 1f, ny = (y + 0.5f) / Height * 2f - 1f;
        var a = Vector4.Transform(new Vector4(nx, ny, 1f, 1f), inverseViewProjection);
        var b = Vector4.Transform(new Vector4(nx, ny, 0.5f, 1f), inverseViewProjection);
        var pa = new Vector3(a.X, a.Y, a.Z) / a.W;
        var pb = new Vector3(b.X, b.Y, b.Z) / b.W;
        if (MathF.Abs(pb.Z - pa.Z) < 1e-9f)
            return null;
        float t = -pa.Z / (pb.Z - pa.Z);
        return t > 0f ? pa + (pb - pa) * t : null;
    }

    [SkippableFact]
    public void LuxOverlayMapsTheGradient()
    {
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            ProceduralTestScene.Build(renderer.World);
            using var target = renderer.CreateTarget(Width, Height);
            var view = new ViewOptions { Overlay = OverlayMode.Lux, ShowGrid = false };
            renderer.Render(target, ProceduralTestScene.CreateCamera(), view, new PreviewSettings(), 0f);
            var image = renderer.ReadPixels(target);
            TestVulkan.AssertNoValidationErrors(renderer.Context);
            GoldenImageTests.CompareWithGolden(image, "overlay-lux");

            // A dark corner of the floor is below RangeMin (1 lx): out-of-range grey.
            var lux = renderer.ReadAnalysis(target);
            int dark = Array.FindIndex(lux, v => v > 0f && v < 0.5f);
            if (dark >= 0)
            {
                int x = dark % Width, y = dark / Width;
                byte expected = (byte)MathF.Round(255f * view.LuxGradient.OutOfRangeColor.X);
                Assert.InRange(image.Pixels[(y * Width + x) * 4], expected - 3, expected + 3);
            }
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableFact]
    public void NoiseOverlayMatchesAcoustics()
    {
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            var scene = new StageScene(renderer);
            scene.Floor(20f, scene.Lambert(new Vector3(0.5f)), InstanceFlags.None);
            var sources = new[]
            {
                new NoiseSource(new Vector3(-2f, 0f, 1f), 60f),
                new NoiseSource(new Vector3(2.5f, 1.5f, 3f), 72f),
                new NoiseSource(new Vector3(0f, -3f, 0.5f), 45f),
            };
            renderer.World.SetNoiseSources(sources);
            var view = new TopDownView(Width, Height, 10f);
            using var target = renderer.CreateTarget(Width, Height);
            renderer.Render(target, view.Camera, new ViewOptions { ShowGrid = false, Overlay = OverlayMode.Noise }, new PreviewSettings(), 0f);
            var spl = renderer.ReadAnalysis(target);
            TestVulkan.AssertNoValidationErrors(renderer.Context);
            var reference = sources.Select(s => new SoundSource(s.Position, s.DecibelsAt1m)).ToArray();
            for (float y = -4f; y <= 4f; y += 1f)
            {
                for (float x = -6f; x <= 6f; x += 1.1f)
                {
                    var (px, py, centre) = view.NearestPixel(new Vector3(x, y, 0f));
                    float expected = Acoustics.SplAt(centre, reference);
                    Assert.InRange(spl[py * Width + px], expected - 0.5f, expected + 0.5f);
                }
            }
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableFact]
    public void NoiseFieldReplacesTheSourcesWhenGiven()
    {
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            var scene = new StageScene(renderer);
            scene.Floor(20f, scene.Lambert(new Vector3(0.5f)), InstanceFlags.None);
            renderer.World.SetNoiseSources(new[] { new NoiseSource(new Vector3(0f, 0f, 1f), 90f) }); // ignored with a field

            // A field rising 4 dB per metre along X (linear: trilinear interpolation between voxel centres is exact).
            const int nx = 16, ny = 8, nz = 4;
            var min = new Vector3(-4f, -3f, -1f);
            var max = new Vector3(4f, 3f, 3f);
            var values = new float[nx * ny * nz];
            for (int z = 0; z < nz; z++)
            {
                for (int y = 0; y < ny; y++)
                {
                    for (int x = 0; x < nx; x++)
                        values[(z * ny + y) * nx + x] = 50f + 4f * (min.X + (x + 0.5f) * (max.X - min.X) / nx);
                }
            }

            renderer.World.SetAtmosphere(new AtmosphereState
            {
                BaseScattering = 0f,
                NoiseField = new VolumeGrid { Min = min, Max = max, SizeX = nx, SizeY = ny, SizeZ = nz, Values = values },
            });
            var view = new TopDownView(Width, Height, 10f);
            using var target = renderer.CreateTarget(Width, Height);
            renderer.Render(target, view.Camera, new ViewOptions { ShowGrid = false, Overlay = OverlayMode.Noise }, new PreviewSettings(), 0f);
            var spl = renderer.ReadAnalysis(target);
            TestVulkan.AssertNoValidationErrors(renderer.Context);
            for (float x = -3.5f; x <= 3.5f; x += 0.7f)
            {
                for (float y = -2.5f; y <= 2.5f; y += 1f)
                {
                    var (px, py, centre) = view.NearestPixel(new Vector3(x, y, 0f));
                    Assert.InRange(spl[py * Width + px], 50f + 4f * centre.X - 0.5f, 50f + 4f * centre.X + 0.5f);
                }
            }

            // Outside the field's box there is no level (silence): out of range.
            var (ox, oy, _) = view.NearestPixel(new Vector3(-6f, 0f, 0f));
            Assert.True(spl[oy * Width + ox] < -1e6f, $"outside the field: {spl[oy * Width + ox]} dB");
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableFact]
    public void NoiseOverlayColoursSurfacesAndFog()
    {
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            ProceduralTestScene.Build(renderer.World);
            renderer.World.SetNoiseSources(new[] { new NoiseSource(new Vector3(-3f, 2f, 1f), 70f), new NoiseSource(new Vector3(3f, 3f, 4f), 50f) });
            using var target = renderer.CreateTarget(Width, Height);
            var view = new ViewOptions { Overlay = OverlayMode.Noise, ShowGrid = false };
            var image = new StageScene(renderer).Render(target, ProceduralTestScene.CreateCamera(), view,
                new PreviewSettings { VolumetricSlices = 64 }, frames: 4);
            TestVulkan.AssertNoValidationErrors(renderer.Context);
            GoldenImageTests.CompareWithGolden(image, "overlay-noise");
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableFact]
    public void CableOverlayGreysTheSceneAndHighlightsCabledDevices()
    {
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            var scene = ProceduralTestScene.Build(renderer.World);
            var steel = scene.Instances[5];
            renderer.World.UpdateInstance(steel, Matrix4x4.CreateScale(0.6f, 0.6f, 2f) * Matrix4x4.CreateTranslation(1.8f, 3f, 1f),
                InstanceFlags.CastShadow | InstanceFlags.CableHighlight);
            using var target = renderer.CreateTarget(Width, Height);
            var camera = ProceduralTestScene.CreateCamera();
            var normal = new StageScene(renderer).Render(target, camera, new ViewOptions { ShowGrid = false }, new PreviewSettings());
            var cables = new StageScene(renderer).Render(target, camera, new ViewOptions { ShowGrid = false, Overlay = OverlayMode.Cables },
                new PreviewSettings());
            TestVulkan.AssertNoValidationErrors(renderer.Context);
            GoldenImageTests.CompareWithGolden(cables, "overlay-cables");

            // The red pool on the floor becomes grey and darker; the highlighted steel box keeps its colours.
            int Index(Vector3 p)
            {
                var vp = camera.View * ReverseZ.Perspective(camera.FovY, Width / (float)Height, camera.Near);
                var c = Vector4.Transform(new Vector4(p, 1f), vp);
                int x = (int)((c.X / c.W * 0.5f + 0.5f) * Width), y = (int)((c.Y / c.W * 0.5f + 0.5f) * Height);
                return (y * Width + x) * 4;
            }

            int red = Index(new Vector3(-3.0f, 0.6f, 0f));
            Assert.True(normal.Pixels[red] > normal.Pixels[red + 1] + 30, "the pool is red in the normal view");
            Assert.InRange(Math.Abs(cables.Pixels[red] - cables.Pixels[red + 1]), 0, 3);
            Assert.True(cables.Pixels[red] < normal.Pixels[red], "darkened");
            int box = Index(new Vector3(1.8f, 2.69f, 1.2f));
            for (int c = 0; c < 3; c++)
                Assert.InRange(cables.Pixels[box + c], normal.Pixels[box + c] - 3, normal.Pixels[box + c] + 3);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableFact]
    public void SelectionOutlineIsOrangeAndShowsThroughOccluders()
    {
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            var scene = new StageScene(renderer);
            scene.Floor(14f, scene.Lambert(new Vector3(0.4f)));
            var front = scene.Box(new Vector3(1.2f, 0.4f, 1.6f), new Vector3(-0.3f, -1f, 0.8f), scene.Lambert(new Vector3(0.7f, 0.7f, 0.75f)));
            scene.Sphere(0.7f, new Vector3(0.3f, 1f, 0.7f), scene.Lambert(new Vector3(0.75f, 0.3f, 0.2f)), InstanceFlags.CastShadow | InstanceFlags.Selected, 4);
            renderer.World.SetLights(new[] { StageScene.Spot(new Vector3(1f, -3f, 5f), new Vector3(0f, 0f, 0.5f), 20000f, 40f, 60f) });
            using var target = renderer.CreateTarget(Width, Height);
            var camera = RenderCamera.LookAt(new Vector3(-0.5f, -6f, 1.2f), new Vector3(0f, 0.5f, 0.7f), 40f);
            camera.Ev100 = 8f;
            var view = new ViewOptions { ShowGrid = false };
            var image = scene.Render(target, camera, view, new PreviewSettings());
            TestVulkan.AssertNoValidationErrors(renderer.Context);
            GoldenImageTests.CompareWithGolden(image, "selection-outline");

            // Count fully orange outline pixels (#FF7A00) and dimmed ones (blended over the occluder).
            int bright = 0, dimmed = 0;
            for (int i = 0; i < image.Pixels.Length; i += 4)
            {
                int r = image.Pixels[i], g = image.Pixels[i + 1], b = image.Pixels[i + 2];
                if (r > 240 && g > 105 && g < 140 && b < 20)
                    bright++;
                else if (r > g + 60 && g > b + 25 && r < 230 && image.Pixels[i] > 100)
                    dimmed++;
            }

            Assert.True(bright > 40, $"{bright} orange outline pixels");
            Assert.True(dimmed > 10, $"{dimmed} dimmed outline pixels through the occluder");

            // Without the option nothing orange is drawn.
            var plain = scene.Render(target, camera, new ViewOptions { ShowGrid = false, ShowSelectionOutline = false }, new PreviewSettings());
            int orange = 0;
            for (int i = 0; i < plain.Pixels.Length; i += 4)
            {
                if (plain.Pixels[i] > 240 && plain.Pixels[i + 1] > 105 && plain.Pixels[i + 1] < 140 && plain.Pixels[i + 2] < 20)
                    orange++;
            }

            Assert.Equal(0, orange);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableFact]
    public void BeamOutlinesDrawTheConesOfEveryLight()
    {
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            var scene = ProceduralTestScene.Build(renderer.World);
            using var target = renderer.CreateTarget(Width, Height);
            var camera = ProceduralTestScene.CreateCamera();
            var without = new StageScene(renderer).Render(target, camera, new ViewOptions { Shading = ViewShading.Colorless }, new PreviewSettings());
            var with = new StageScene(renderer).Render(target, camera, new ViewOptions { Shading = ViewShading.Colorless, ShowBeamOutlines = true },
                new PreviewSettings());
            TestVulkan.AssertNoValidationErrors(renderer.Context);
            GoldenImageTests.CompareWithGolden(with, "beam-outlines");
            int changed = 0;
            for (int i = 0; i < with.Pixels.Length; i += 4)
            {
                if (Math.Abs(with.Pixels[i] - without.Pixels[i]) + Math.Abs(with.Pixels[i + 2] - without.Pixels[i + 2]) > 60)
                    changed++;
            }

            Assert.True(changed > 400, $"{changed} pixels show beam outlines");
            // Eight generator lines and a 24-segment ring per light, two vertices per segment.
            Assert.Equal(scene.Lights.Length * 2 * (8 + 24), ((VulkanRenderTarget)target).BeamLines.Count);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableFact]
    public void ReferenceDrawingsAreUnlitAndSemiTransparent()
    {
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            var world = renderer.World;
            var plane = world.CreateMesh(ProceduralMeshes.Plane(2f, 2f));
            var material = world.CreateMaterial(new MaterialData { BaseColorFactor = new Vector4(1f, 0f, 0f, 1f) }, null);
            world.CreateInstance(plane, new[] { material }, Matrix4x4.CreateTranslation(0f, 0f, 0.01f), InstanceFlags.Reference, 1);
            var view = new TopDownView(64, 48, 4f);
            using var target = renderer.CreateTarget(64, 48);
            renderer.Render(target, view.Camera, new ViewOptions { ShowGrid = false, BackgroundLuminance = 0f }, new PreviewSettings(), 0f);
            var hdr = renderer.ReadHdr(target);
            var (x, y, _) = view.NearestPixel(Vector3.Zero);
            // Unlit: the base colour as a display value (independent of lights and exposure), half covering the black background.
            var expected = ColorSpaces.Rec709ToRec2020(new Vector3(1f, 0f, 0f)) * 0.5f / hdr.Exposure;
            var measured = hdr.At(x, y);
            Assert.InRange(Vector3.Distance(measured, expected) / expected.Length(), 0f, 0.02f);

            // Colorless: also unlit and semi-transparent (display-linear Rec.709 over the dark grey background), not shaded grey.
            renderer.Render(target, view.Camera, new ViewOptions { Shading = ViewShading.Colorless, ShowGrid = false }, new PreviewSettings(), 0f);
            var colorless = renderer.ReadHdr(target).At(x, y);
            const float background = 0.022f;
            var expectedColorless = new Vector3(0.5f + 0.5f * background, 0.5f * background, 0.5f * background);
            Assert.InRange(Vector3.Distance(colorless, expectedColorless), 0f, 0.01f);
            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }
}
