using System.Numerics;
using Candela.Core.Geometry;
using Candela.Photometry;

namespace Candela.Rendering.Vulkan.Tests;

/// <summary>
/// One-bounce indirect light: a strongly lit red wall bleeds red onto the white floor in front of it. The floor's red
/// to green ratio rises compared with <see cref="IndirectLighting.Off"/>, and the added luminance agrees with a CPU
/// integration of the light the wall reflects (Lambertian wall lit as <see cref="LightMeter"/> says).
/// </summary>
public sealed class IndirectTests
{
    private const int Width = 200;
    private const int Height = 150;
    private const float WallX = 2f;

    private static readonly Vector3 FloorAlbedo = new(0.8f);
    private static readonly Vector3 WallAlbedo = new(0.8f, 0.08f, 0.06f);

    /// <summary>Floor points in front of the wall (outside the spot's cone) where the bleeding is measured.</summary>
    private static readonly Vector3[] Probes =
    {
        new(1.0f, 0f, 0f), new(1.4f, 0.6f, 0f), new(0.6f, -0.5f, 0f), new(1.6f, -0.9f, 0f), new(0.2f, 0.3f, 0f),
    };

    /// <summary>Half size of the pixel box averaged around each probe.</summary>
    private const int Box = 4;

    [SkippableFact]
    public void ReflectiveShadowMapsBleedTheWallColourOntoTheFloor() => MeasureBleeding(IndirectLighting.ReflectiveShadowMaps, 1, 1, 0.12f);

    [SkippableFact]
    public void RayTracedGiBleedsTheWallColourOntoTheFloor()
    {
        var renderer = TestRenderers.CreateOrSkip();
        bool supported = renderer.Info.SupportsRayTracing;
        TestRenderers.DisposeAndAssertClean(renderer);
        Skip.IfNot(supported, "the device has no ray query support (RayTraced falls back to reflective shadow maps)");
        // Noisy (a few rays per pixel and frame, temporally accumulated): the converged image is averaged over frames.
        MeasureBleeding(IndirectLighting.RayTraced, 30, 12, 0.2f);
    }

    [SkippableFact]
    public void RayTracedFallsBackWithoutRayTracingAndOffDisablesIt()
    {
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            var lights = BuildScene(renderer);
            var view = new TopDownView(Width, Height, 7f, ev100: 6f);
            using var target = renderer.CreateTarget(Width, Height);
            var off = Floor(renderer, target, view, IndirectLighting.Off, 1, 1);
            Assert.Equal(0, renderer.LastStatistics.VirtualPointLights);
            var rsm = Floor(renderer, target, view, IndirectLighting.ReflectiveShadowMaps, 1, 1);
            Assert.True(renderer.LastStatistics.VirtualPointLights > 0);
            Assert.True(Ratio(rsm[0]) > Ratio(off[0]) * 1.2f);

            // Without shadow-casting lights there are no reflective shadow maps: indirect light is off.
            for (int i = 0; i < lights.Length; i++)
                lights[i].CastShadows = false;
            renderer.World.SetLights(lights);
            var unshadowed = Floor(renderer, target, view, IndirectLighting.ReflectiveShadowMaps, 1, 1);
            Assert.Equal(0, renderer.LastStatistics.VirtualPointLights);
            Assert.InRange(Ratio(unshadowed[0]), Ratio(off[0]) * 0.98f, Ratio(off[0]) * 1.02f);
            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableTheory]
    [InlineData(IndirectLighting.ReflectiveShadowMaps, 1, 0.05f)]
    [InlineData(IndirectLighting.RayTraced, 40, 0.1f)]
    public void RedFloorBleedsOntoAWhiteWall(IndirectLighting mode, int frames, float tolerance)
    {
        // The other way round: a spot lights a red floor in front of a white wall (vertical receiver, horizontal
        // reflector); the wall's added luminance against a CPU integral of the floor's reflected light.
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            Skip.If(mode == IndirectLighting.RayTraced && !renderer.Info.SupportsRayTracing, "the device has no ray query support");
            var scene = new StageScene(renderer);
            var red = new Vector3(0.8f, 0.08f, 0.06f);
            var white = new Vector3(0.8f);
            scene.Floor(12f, scene.Lambert(red));
            scene.Box(new Vector3(0.2f, 6f, 4f), new Vector3(WallX + 0.1f, 0f, 2f), scene.Lambert(white, "white wall"));
            var lights = new[] { StageScene.Spot(new Vector3(0.5f, 0f, 4f), new Vector3(0.5f, 0f, 0f), 60000f, 25f, 35f) };
            renderer.World.SetLights(lights);
            const int w = 160, h = 120;
            using var target = renderer.CreateTarget(w, h);
            // Orthographic, looking at the wall face along +x.
            var camera = RenderCamera.LookAt(new Vector3(-6f, 0f, 2f), new Vector3(WallX, 0f, 2f));
            camera.Projection = ProjectionKind.Orthographic;
            camera.OrthoHeight = 4f;
            camera.Near = 0.1f;
            camera.Far = 50f;
            camera.Ev100 = 6f;
            var options = new ViewOptions { ShowGrid = false, BackgroundLuminance = 0f };
            HdrReadback Render(IndirectLighting m, int n)
            {
                var settings = new PreviewSettings { Indirect = m, Bloom = false, Volumetrics = false };
                for (int i = 0; i < n; i++)
                    renderer.Render(target, camera, options, settings, 1f / 30f);
                return renderer.ReadHdr(target);
            }

            var off = Render(IndirectLighting.Off, 1);
            var on = Render(mode, frames);
            TestVulkan.AssertNoValidationErrors(renderer.Context);
            Matrix4x4.Invert(camera.View * ReverseZ.Orthographic(camera.OrthoHeight, w / (float)h, camera.Near, camera.Far), out var inverse);
            Vector3 WallPoint(int px, int py)
            {
                float nx = (px + 0.5f) / w * 2f - 1f, ny = (py + 0.5f) / h * 2f - 1f;
                var a = Vector4.Transform(new Vector4(nx, ny, 1f, 1f), inverse);
                var b = Vector4.Transform(new Vector4(nx, ny, 0.5f, 1f), inverse);
                var pa = new Vector3(a.X, a.Y, a.Z) / a.W;
                var pb = new Vector3(b.X, b.Y, b.Z) / b.W;
                return pa + (pb - pa) * ((WallX - pa.X) / (pb.X - pa.X));
            }

            var reference = lights.Select(l => new Photometry.SpotLight(l.Position, l.Direction, l.PeakCandela,
                BeamDistribution.Create(l.BeamType, l.BeamAngle, l.FieldAngle))).ToArray();
            var floor = new List<(Vector3 Position, float Illuminance)>();
            for (float x = -6f + WallCell / 2; x < WallX; x += WallCell)
            {
                for (float y = -6f + WallCell / 2; y < 6f; y += WallCell)
                {
                    var p = new Vector3(x, y, 0f);
                    float e = LightMeter.IlluminanceAt(reference, p, Vector3.UnitZ);
                    if (e > 0f)
                        floor.Add((p, e));
                }
            }

            var redAlbedo = ColorSpaces.Rec709ToRec2020(red);
            var whiteAlbedo = ColorSpaces.Rec709ToRec2020(white);
            foreach (var (px, py) in new[] { (80, 60), (80, 80), (60, 70), (100, 90), (80, 40) })
            {
                var p = WallPoint(px, py);
                double irradiance = 0;
                foreach (var (q, e) in floor)
                {
                    var d = p - q;
                    float d2 = d.LengthSquared();
                    var dir = d / MathF.Sqrt(d2);
                    if (dir.Z > 0f && dir.X > 0f)
                        irradiance += e / MathF.PI * dir.Z * dir.X / d2 * WallCell * WallCell;
                }

                float predicted = ColorSpaces.LuminanceRec2020(whiteAlbedo / MathF.PI * (float)irradiance * redAlbedo);
                float measured = ColorSpaces.LuminanceRec2020(on.At(px, py) - off.At(px, py));
                Assert.True(MathF.Abs(measured - predicted) <= tolerance * predicted,
                    $"{mode}, wall point {p}: indirect {measured:F3} cd/m², CPU integral {predicted:F3} cd/m²");
                Assert.True(Ratio(on.At(px, py)) > 2f, $"the wall turns red ({Ratio(on.At(px, py)):F2})");
            }
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    private static void MeasureBleeding(IndirectLighting mode, int warmUp, int samples, float tolerance)
    {
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            var lights = BuildScene(renderer);
            var view = new TopDownView(Width, Height, 7f, ev100: 6f);
            using var target = renderer.CreateTarget(Width, Height);
            var off = Floor(renderer, target, view, IndirectLighting.Off, 1, 1);
            var on = Floor(renderer, target, view, mode, warmUp, samples);
            TestVulkan.AssertNoValidationErrors(renderer.Context);

            var reference = lights.Select(l => new Photometry.SpotLight(l.Position, l.Direction, l.PeakCandela,
                BeamDistribution.Create(l.BeamType, l.BeamAngle, l.FieldAngle), LightColor.FromRec2020(l.Color))).ToArray();
            var wall = WallCells(reference);
            var wallAlbedo = ColorSpaces.Rec709ToRec2020(WallAlbedo);
            var floorAlbedo = ColorSpaces.Rec709ToRec2020(FloorAlbedo);
            var report = new List<string>();
            bool ok = true;
            for (int i = 0; i < Probes.Length; i++)
            {
                // Red over green rises: the floor takes on the wall's colour.
                Assert.True(Ratio(on[i]) > Ratio(off[i]) * 1.3f, $"{mode} probe {Probes[i]}: R/G {Ratio(on[i]):F3} vs {Ratio(off[i]):F3} without");

                // The added light matches the light the wall reflects, integrated on the CPU over the same floor points.
                var (px, py, _) = view.NearestPixel(Probes[i]);
                double irradiance = 0;
                for (int dy = -Box; dy <= Box; dy++)
                {
                    for (int dx = -Box; dx <= Box; dx++)
                        irradiance += WallIrradiance(wall, view.FloorPointOf(px + dx, py + dy));
                }

                irradiance /= (2 * Box + 1) * (2 * Box + 1);
                var expected = floorAlbedo / MathF.PI * (float)irradiance * wallAlbedo;
                float measured = ColorSpaces.LuminanceRec2020(on[i] - off[i]), predicted = ColorSpaces.LuminanceRec2020(expected);
                ok &= MathF.Abs(measured - predicted) <= tolerance * predicted;
                report.Add($"{Probes[i]}: indirect {measured:F3} cd/m², CPU integral {predicted:F3} cd/m²");
            }

            Assert.True(ok, $"{mode}: " + string.Join("; ", report));
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    /// <summary>
    /// A white floor, a red wall (face at x = 2, facing −x, 6 m wide, 4 m high), a narrow spot that lights only the
    /// wall and a dim wide light from above that gives the floor a white base.
    /// </summary>
    private static SpotLight[] BuildScene(VulkanRenderer renderer)
    {
        var scene = new StageScene(renderer);
        scene.Floor(12f, scene.Lambert(FloorAlbedo));
        scene.Box(new Vector3(0.2f, 6f, 4f), new Vector3(WallX + 0.1f, 0f, 2f), scene.Lambert(WallAlbedo, "red wall"));
        var lights = new[]
        {
            StageScene.Spot(new Vector3(-1f, 0f, 3f), new Vector3(WallX, 0f, 1.6f), 60000f, 20f, 30f),
            StageScene.Spot(new Vector3(-1f, 0f, 7f), new Vector3(0.5f, 0f, 0f), 1500f, 60f, 90f),
        };
        renderer.World.SetLights(lights);
        return lights;
    }

    /// <summary>
    /// Luminance per Rec.2020 channel around the probes, cd/m²: the mean over the probe's pixel box, after
    /// <paramref name="warmUp"/> frames, of <paramref name="samples"/> frames five frames apart.
    /// </summary>
    private static Vector3[] Floor(VulkanRenderer renderer, IRenderTarget target, TopDownView view, IndirectLighting mode, int warmUp, int samples)
    {
        var settings = new PreviewSettings { Indirect = mode, Bloom = false, Volumetrics = false };
        var options = new ViewOptions { ShowGrid = false, BackgroundLuminance = 0f };
        for (int i = 0; i < warmUp; i++)
            renderer.Render(target, view.Camera, options, settings, 1f / 30f);
        var result = new Vector3[Probes.Length];
        for (int s = 0; s < samples; s++)
        {
            for (int i = 0; s > 0 && i < 5; i++)
                renderer.Render(target, view.Camera, options, settings, 1f / 30f);
            var hdr = renderer.ReadHdr(target);
            for (int i = 0; i < Probes.Length; i++)
            {
                var (px, py, _) = view.NearestPixel(Probes[i]);
                var sum = Vector3.Zero;
                for (int dy = -Box; dy <= Box; dy++)
                {
                    for (int dx = -Box; dx <= Box; dx++)
                        sum += hdr.At(px + dx, py + dy);
                }

                result[i] += sum / ((2 * Box + 1) * (2 * Box + 1) * samples);
            }
        }

        return result;
    }

    private static float Ratio(Vector3 rgb) => rgb.X / MathF.Max(rgb.Y, 1e-9f);

    /// <summary>Centres and illuminance (lx, from the lights) of the wall face's 2.5 cm cells.</summary>
    private static (Vector3 Position, float Illuminance)[] WallCells(Photometry.SpotLight[] lights)
    {
        var cells = new List<(Vector3, float)>();
        for (float y = -3f + WallCell / 2; y < 3f; y += WallCell)
        {
            for (float z = WallCell / 2; z < 4f; z += WallCell)
            {
                var x = new Vector3(WallX, y, z);
                float e = LightMeter.IlluminanceAt(lights, x, -Vector3.UnitX);
                if (e > 0f)
                    cells.Add((x, e));
            }
        }

        return cells.ToArray();
    }

    private const float WallCell = 0.025f;

    /// <summary>
    /// Irradiance at floor point <paramref name="p"/> from the wall face per unit wall albedo, lx: the wall is a
    /// Lambertian reflector of the lights' illuminance (radiance E/π).
    /// </summary>
    private static float WallIrradiance((Vector3 Position, float Illuminance)[] wall, Vector3 p)
    {
        double sum = 0;
        foreach (var (x, e) in wall)
        {
            var toP = p - x;
            float d2 = toP.LengthSquared();
            var dir = toP / MathF.Sqrt(d2);
            float cosWall = -dir.X, cosFloor = -dir.Z;
            if (cosWall <= 0f || cosFloor <= 0f)
                continue;
            sum += e / MathF.PI * cosWall * cosFloor / d2;
        }

        return (float)(sum * WallCell * WallCell);
    }
}
