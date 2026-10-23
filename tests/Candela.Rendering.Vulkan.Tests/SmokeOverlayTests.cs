using System.Numerics;
using Candela.Core.Geometry;

namespace Candela.Rendering.Vulkan.Tests;

/// <summary>
/// The smoke overlay: the analysis value of a surface is the scattering coefficient of the air next to it (haze plus
/// smoke grid, 1/m) in every view mode; the scene is greyed, a smoke cloud shows as fog in the gradient's colours and
/// the emitters are marked with arrows over everything — coloured and long when running, short and grey when off.
/// </summary>
public sealed class SmokeOverlayTests
{
    private const int Width = 240;
    private const int Height = 180;

    /// <summary>A grid whose density rises linearly along X from 0 to 1 over the box (trilinear interpolation is exact).</summary>
    private static VolumeGrid LinearGrid(Vector3 min, Vector3 max, int nx, int ny, int nz)
    {
        var values = new float[nx * ny * nz];
        for (int z = 0; z < nz; z++)
        {
            for (int y = 0; y < ny; y++)
            {
                for (int x = 0; x < nx; x++)
                    values[(z * ny + y) * nx + x] = (x + 0.5f) / nx;
            }
        }

        return new VolumeGrid { Min = min, Max = max, SizeX = nx, SizeY = ny, SizeZ = nz, Values = values };
    }

    /// <summary>A round cloud of smoke (density 1 at its centre, Gaussian with the given radius).</summary>
    private static VolumeGrid Cloud(Vector3 centre, float radius, int n = 16)
    {
        var min = centre - new Vector3(2f * radius);
        var max = centre + new Vector3(2f * radius);
        var values = new float[n * n * n];
        for (int z = 0; z < n; z++)
        {
            for (int y = 0; y < n; y++)
            {
                for (int x = 0; x < n; x++)
                {
                    var p = min + (new Vector3(x, y, z) + new Vector3(0.5f)) * (max - min) / n;
                    float d = Vector3.Distance(p, centre) / radius;
                    values[(z * n + y) * n + x] = MathF.Exp(-d * d);
                }
            }
        }

        return new VolumeGrid { Min = min, Max = max, SizeX = n, SizeY = n, SizeZ = n, Values = values };
    }

    [SkippableTheory]
    [InlineData(ViewShading.Full)]
    [InlineData(ViewShading.Colorless)]
    [InlineData(ViewShading.Wireframe)]
    public void SurfacesReadTheHazePlusTheSmokeNextToThem(ViewShading shading)
    {
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            var scene = new StageScene(renderer);
            scene.Floor(20f, scene.Lambert(new Vector3(0.5f)), InstanceFlags.None);
            var min = new Vector3(-4f, -3f, -1f);
            var max = new Vector3(4f, 3f, 3f);
            renderer.World.SetAtmosphere(new AtmosphereState
            {
                BaseScattering = 0.02f,
                SmokeDensity = LinearGrid(min, max, 16, 8, 4),
                SmokeScatteringAtFullDensity = 0.5f,
            });
            var view = new TopDownView(Width, Height, 10f);
            using var target = renderer.CreateTarget(Width, Height);
            renderer.Render(target, view.Camera, new ViewOptions { Shading = shading, ShowGrid = false, Overlay = OverlayMode.Smoke },
                new PreviewSettings(), 0f);
            var sigma = renderer.ReadAnalysis(target);
            TestVulkan.AssertNoValidationErrors(renderer.Context);
            for (float x = -3.5f; x <= 3.5f; x += 0.7f)
            {
                for (float y = -2.5f; y <= 2.5f; y += 1f)
                {
                    var (px, py, centre) = view.NearestPixel(new Vector3(x, y, 0f));
                    float expected = 0.02f + 0.5f * (centre.X - min.X) / (max.X - min.X);
                    float measured = sigma[py * Width + px];
                    Assert.True(MathF.Abs(measured - expected) <= 0.004f, $"({x:F1}, {y:F1}): {measured:F4} /m, expected {expected:F4} /m");
                }
            }

            // Outside the smoke box: the haze alone.
            var (ox, oy, _) = view.NearestPixel(new Vector3(-6f, 0f, 0f));
            Assert.InRange(sigma[oy * Width + ox], 0.0195f, 0.0205f);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableFact]
    public void SceneIsGreyedSmokeShowsAsColouredFogAndEmittersAreMarked()
    {
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            ProceduralTestScene.Build(renderer.World);
            var cloud = new Vector3(0f, 0.8f, 2.2f);
            renderer.World.SetAtmosphere(new AtmosphereState
            {
                BaseScattering = 0f,
                SmokeDensity = Cloud(cloud, 0.7f),
                SmokeScatteringAtFullDensity = 0.8f,
            });
            var hazer = new Vector3(-4.6f, -0.5f, 0.3f);
            var hidden = new Vector3(-1.2f, 3.2f, 0.2f); // inside the black stage box (0.8 m high)
            var fan = new Vector3(4.6f, -0.5f, 1f);
            renderer.World.SetSmokeEmitters(new[]
            {
                new SmokeEmitterMarker(hazer, Vector3.UnitZ, 1f, SmokeEmitterMarkerKind.Hazer, Guid.NewGuid()),
                new SmokeEmitterMarker(hidden, Vector3.UnitZ, 1f, SmokeEmitterMarkerKind.Hazer, Guid.NewGuid()),
                new SmokeEmitterMarker(fan, -Vector3.UnitX, 0f, SmokeEmitterMarkerKind.Fan, Guid.NewGuid()),
            });
            using var target = renderer.CreateTarget(Width, Height);
            var camera = ProceduralTestScene.CreateCamera();
            var settings = new PreviewSettings { VolumetricSlices = 64 };
            var stage = new StageScene(renderer);
            var normal = stage.Render(target, camera, new ViewOptions { ShowGrid = false }, settings, frames: 4);
            var smoke = stage.Render(target, camera, new ViewOptions { ShowGrid = false, Overlay = OverlayMode.Smoke }, settings, frames: 4);
            TestVulkan.AssertNoValidationErrors(renderer.Context);
            GoldenImageTests.CompareWithGolden(smoke, "overlay-smoke");

            (int X, int Y) Pixel(Vector3 p)
            {
                var vp = camera.View * ReverseZ.Perspective(camera.FovY, Width / (float)Height, camera.Near);
                var c = Vector4.Transform(new Vector4(p, 1f), vp);
                return ((int)((c.X / c.W * 0.5f + 0.5f) * Width), (int)((c.Y / c.W * 0.5f + 0.5f) * Height));
            }

            Vector3 Rgb(ImageData image, (int X, int Y) p)
            {
                int i = (p.Y * Width + p.X) * 4;
                return new Vector3(image.Pixels[i], image.Pixels[i + 1], image.Pixels[i + 2]);
            }

            // The most saturated pixel of the 5 x 5 neighbourhood (a line is a few pixels wide and anti-aliased).
            Vector3 Strongest(ImageData image, Vector3 point)
            {
                var (cx, cy) = Pixel(point);
                var best = Vector3.Zero;
                float bestScore = -1f;
                for (int y = Math.Max(cy - 2, 0); y <= Math.Min(cy + 2, Height - 1); y++)
                {
                    for (int x = Math.Max(cx - 2, 0); x <= Math.Min(cx + 2, Width - 1); x++)
                    {
                        var c = Rgb(image, (x, y));
                        float score = c.Z - c.X + 0.01f * (c.X + c.Y + c.Z);
                        if (score > bestScore)
                        {
                            bestScore = score;
                            best = c;
                        }
                    }
                }

                return best;
            }

            static float Saturation(Vector3 c) => MathF.Max(c.X, MathF.Max(c.Y, c.Z)) - MathF.Min(c.X, MathF.Min(c.Y, c.Z));

            // Greyed: the red pool on the floor (clear air) is grey and darker than in the normal view.
            var pool = Pixel(new Vector3(-3.0f, 0.6f, 0f));
            Assert.True(Rgb(normal, pool).X > Rgb(normal, pool).Y + 30, "the pool is red in the normal view");
            Assert.True(Saturation(Rgb(smoke, pool)) <= 3f, $"pool in the smoke overlay {Rgb(smoke, pool)}");
            Assert.True(Rgb(smoke, pool).X < Rgb(normal, pool).X, "darkened");

            // The cloud is coloured fog (the gradient's dense end), where the normal view has none of these colours.
            var fog = Rgb(smoke, Pixel(cloud));
            Assert.True(Saturation(fog) > 40f, $"cloud in the smoke overlay {fog}");

            // The running hazer: a long cyan arrow; the one inside the box shows through it; the fan that is off is grey.
            var arrow = Strongest(smoke, hazer + Vector3.UnitZ * 1.5f);
            Assert.True(arrow.Z > 150f && arrow.Z > arrow.X + 80f, $"hazer arrow {arrow}");
            var through = Strongest(smoke, hidden + Vector3.UnitZ * 0.4f);
            Assert.True(through.Z > through.X + 60f, $"hazer inside the box {through}");
            var off = Rgb(smoke, Pixel(fan - Vector3.UnitX * 0.25f));
            Assert.True(Saturation(off) <= 12f && off.X > 100f, $"fan that is off {off}");
            // Short when off: nothing of it where a running fan's arrow would reach.
            var reach = Rgb(smoke, Pixel(fan - Vector3.UnitX * 1.6f));
            Assert.True(reach.X < 100f, $"off arrow too long {reach}");
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableFact]
    public void MarkersAreBuiltOnlyForTheSmokeOverlayAndSkipInvalidEmitters()
    {
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            using var lines = new SceneBuffer<LineVertex>(renderer.Context, Silk.NET.Vulkan.BufferUsageFlags.VertexBufferBit, "test markers");
            var markers = new List<SmokeEmitterMarker>
            {
                new(new Vector3(0f, 0f, 1f), Vector3.UnitX, 1f, SmokeEmitterMarkerKind.FogMachine, Guid.NewGuid()),
                new(new Vector3(float.NaN, 0f, 1f), Vector3.UnitX, 1f, SmokeEmitterMarkerKind.Fan, Guid.NewGuid()),
                new(new Vector3(2f, 0f, 1f), new Vector3(float.NaN), 0.5f, SmokeEmitterMarkerKind.Flame, Guid.NewGuid()),
            };
            SmokeMarkers.Build(markers, lines, active: false);
            Assert.Equal(0, lines.Count);
            SmokeMarkers.Build(markers, lines, active: true);
            Assert.Equal(2 * SmokeMarkers.SegmentsPerMarker * 2, lines.Count);

            // The shaft of the fog machine: from the nozzle along +X, as long as full output asks.
            var items = lines.Items;
            int shaft = SmokeMarkers.RingSegments * 2;
            Assert.Equal(new Vector3(0f, 0f, 1f), items[shaft].Position);
            Assert.Equal(new Vector3(SmokeMarkers.MaxArrowLength, 0f, 1f), items[shaft + 1].Position);
            Assert.Equal(new Vector4(1f, 1f, 1f, 1f), items[shaft].Color);
            // The flame with an invalid direction points up, half-way between the shortest and the longest arrow.
            int second = SmokeMarkers.SegmentsPerMarker * 2 + shaft;
            Assert.Equal(new Vector3(2f, 0f, 1f + SmokeMarkers.ArrowLength(0.5f)), items[second + 1].Position);
            Assert.All(items.ToArray(), v => Assert.True(float.IsFinite(v.Position.X) && float.IsFinite(v.Width) && v.Width > 0f));
            Assert.Equal(SmokeMarkers.MinArrowLength, SmokeMarkers.ArrowLength(float.NaN));
            Assert.Equal(SmokeMarkers.MaxArrowLength, SmokeMarkers.ArrowLength(7f));
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }
}
