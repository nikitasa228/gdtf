using System.Numerics;
using Candela.Core.Geometry;
using Candela.Photometry;
using Xunit.Abstractions;

namespace Candela.Rendering.Vulkan.Tests;

public sealed class BeamProbeTests
{
    private const int Width = 160;
    private const int Height = 120;
    private const float Haze = 0.02f;
    private const float Anisotropy = 0.7f;
    private static readonly Vector3 LightPosition = new(0f, 0f, 4f);

    private readonly ITestOutputHelper _output;

    public BeamProbeTests(ITestOutputHelper output) => _output = output;

    /// <summary>General CPU reference: GpuLight profile (iris included), prism facets, lens near field, modulation per facet.</summary>
    internal static double General(SpotLight light, Vector3 origin, Vector3 direction, double sigma, double g, double tMax = 60,
        Func<Vector3, Vector3, int, double>? modulation = null, int steps = 100000)
    {
        LightPacker.Pack(light, 0, out var l);
        int facets = Math.Max((int)l.PrismFacets, 1);
        bool prism = l.PrismFacets > 0;
        double spread = prism ? Math.Atan2(l.PrismSinSpread, l.PrismCosSpread) : 0;
        double halfAngle = Math.Clamp(l.BoundingAngle - spread, 1e-4, 1.5);
        double lens = l.SourceRadius;
        double apex = lens / Math.Max(Math.Tan(halfAngle), 1e-4);
        double nearField = lens > 0 ? 3 * apex : 0;
        double intensity = 0.2627 * l.Intensity.X + 0.6780 * l.Intensity.Y + 0.0593 * l.Intensity.Z;
        var axes = new Vector3[facets];
        for (int k = 0; k < facets; k++)
        {
            if (!prism)
            {
                axes[k] = l.Direction;
                continue;
            }

            double phi = l.PrismRotation + 2 * Math.PI * k / facets;
            axes[k] = Vector3.Normalize(l.Right * (float)(l.PrismSinSpread * Math.Cos(phi)) + l.Up * (float)(l.PrismSinSpread * Math.Sin(phi)) +
                                        l.Direction * l.PrismCosSpread);
        }

        double dt = tMax / steps, sum = 0;
        for (int i = 0; i < steps; i++)
        {
            double t = (i + 0.5) * dt;
            var x = origin + direction * (float)t;
            var rel = x - l.Position;
            double d = rel.Length();
            for (int k = 0; k < facets; k++)
            {
                var axis = axes[k];
                double z = Vector3.Dot(rel, axis);
                if (z <= 1e-6)
                    continue;
                var radial = rel - axis * (float)z;
                double rho = radial.Length();
                double s = 1;
                if (z < nearField)
                {
                    double zeta = z * Math.Tan(halfAngle);
                    s = zeta / Math.Pow(Math.Pow(lens, 4) + Math.Pow(zeta, 4), 0.25);
                }

                double theta = Math.Atan2(s * rho, z);
                double f = Profile(l, theta);
                if (f <= 0)
                    continue;
                double r2 = z * z + s * s * rho * rho;
                var omega = Vector3.Normalize(axis * (float)z + radial * (float)s);
                double cos = Vector3.Dot(omega, -direction);
                double phase = (1 - g * g) / (4 * Math.PI * Math.Pow(1 + g * g - 2 * g * cos, 1.5));
                double m = modulation?.Invoke(x, omega, k) ?? 1.0;
                sum += sigma * phase * intensity * f * s * s / r2 * Math.Exp(-sigma * d) * Math.Exp(-sigma * t) * m * dt;
            }
        }

        return sum;
    }

    private static double Profile(in GpuLight l, double theta)
    {
        double gval;
        if (l.Shape == ShaderConstants.ShapeUniformCone)
        {
            gval = theta * l.InvHalfBeam <= 1 ? 1 : 0;
        }
        else
        {
            if (theta >= l.Cutoff)
                return 0;
            double r = theta * l.InvHalfBeam;
            gval = r > 0 ? Math.Exp(-Math.Log(2) * Math.Pow(r, l.Exponent)) : 1;
            if (theta > l.HalfField)
            {
                double s = (theta - l.HalfField) * l.InvWindow;
                gval *= 1 - s * s * (3 - 2 * s);
            }
        }

        if (theta > l.IrisSoftStart)
        {
            double x = Math.Clamp((theta - l.IrisSoftStart) / (l.IrisAngle - l.IrisSoftStart), 0, 1);
            gval *= 1 - x * x * (3 - 2 * x);
        }

        return gval;
    }

    /// <summary>Gate coordinates of a light direction (light frame; no prism).</summary>
    private static Vector2 Gate(in SpotLight light, Vector3 omega)
    {
        LightPacker.Pack(light, 0, out var l);
        float z = Vector3.Dot(omega, l.Direction);
        return new Vector2(Vector3.Dot(omega, l.Right), Vector3.Dot(omega, l.Up)) * (l.GateScale / z);
    }

    private static ImageData GoboFrom(Func<float, float, bool> open, int size = 256)
    {
        var pixels = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float u = (x + 0.5f) / size * 2f - 1f, v = 1f - (y + 0.5f) / size * 2f;
                int i = (y * size + x) * 4;
                pixels[i] = pixels[i + 1] = pixels[i + 2] = 255;
                pixels[i + 3] = open(u, v) ? (byte)255 : (byte)0;
            }
        }

        return new ImageData { Width = size, Height = size, Pixels = pixels };
    }

    private float Centre(VulkanRenderer renderer, RenderCamera camera, int frames, PreviewSettings settings, out HdrReadback hdr)
    {
        using var target = renderer.CreateTarget(Width, Height);
        for (int i = 0; i < frames; i++)
            renderer.Render(target, camera, new ViewOptions { ShowGrid = false, BackgroundLuminance = 0f }, settings, 1f / 60f);
        hdr = renderer.ReadHdr(target);
        return 0.25f * (hdr.LuminanceAt(Width / 2 - 1, Height / 2 - 1) + hdr.LuminanceAt(Width / 2, Height / 2 - 1) +
                        hdr.LuminanceAt(Width / 2 - 1, Height / 2) + hdr.LuminanceAt(Width / 2, Height / 2));
    }

    private double CentreReference(SpotLight light, RenderCamera camera, Func<Vector3, Vector3, int, double>? mod = null)
    {
        double sum = 0;
        foreach (var (x, y) in new[] { (Width / 2 - 1, Height / 2 - 1), (Width / 2, Height / 2 - 1), (Width / 2 - 1, Height / 2), (Width / 2, Height / 2) })
        {
            var (o, d) = BeamReference.PixelRay(camera, Width, Height, x, y);
            sum += General(light, o, d, Haze, Anisotropy, 60, mod, 40000);
        }

        return 0.25 * sum;
    }

    private void Report(string name, double measured, double expected)
    {
        _output.WriteLine($"{name}: measured {measured:0.0000}, reference {expected:0.0000}, error {(measured - expected) / expected:P2}");
    }

    [SkippableFact]
    public void Probe()
    {
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            renderer.World.SetAtmosphere(new AtmosphereState { BaseScattering = Haze, Anisotropy = Anisotropy });
            var noTemporal = new PreviewSettings { TemporalAccumulation = false, Bloom = false };
            var temporal = new PreviewSettings { TemporalAccumulation = true, Bloom = false };

            // 1. HG direction: oblique rays towards and away from the source through a 40° wash.
            var wash = StageScene.Spot(LightPosition, Vector3.Zero, 10000f, 40f, 60f, shadows: false);
            renderer.World.SetLights(new[] { wash });
            foreach (var (eye, look, name) in new[]
                     {
                         (new Vector3(0f, -3f, 1f), new Vector3(0f, 0f, 3f), "towards source"),
                         (new Vector3(0f, -3f, 3.5f), new Vector3(0f, 0f, 0.5f), "away from source"),
                     })
            {
                var cam = RenderCamera.LookAt(eye, look, 30f);
                cam.Ev100 = 2f;
                Report("wash " + name, Centre(renderer, cam, 1, noTemporal, out _), CentreReference(wash, cam));
            }

            // 2. Prism, 3 facets.
            var prism = StageScene.Spot(LightPosition, Vector3.Zero, 30000f, 15f, 22f, shadows: false, type: BeamType.Spot);
            prism.PrismFacets = 3;
            prism.PrismSpread = 0.2f;
            renderer.World.SetLights(new[] { prism });
            {
                var cam = RenderCamera.LookAt(new Vector3(0f, -6f, 2f), new Vector3(0f, 0f, 2f), 40f);
                cam.Ev100 = 2f;
                Report("prism centre", Centre(renderer, cam, 1, noTemporal, out _), CentreReference(prism, cam));
                var cam2 = RenderCamera.LookAt(new Vector3(0f, -6f, 2f), new Vector3(0.6f, 0f, 2f), 40f);
                cam2.Ev100 = 2f;
                Report("prism off-centre", Centre(renderer, cam2, 1, noTemporal, out _), CentreReference(prism, cam2));
            }

            // 3. Iris half closed (no gobo: unmodulated path).
            var iris = StageScene.Spot(LightPosition, Vector3.Zero, 30000f, 20f, 30f, shadows: false, type: BeamType.Spot);
            iris.Iris = 0.5f;
            renderer.World.SetLights(new[] { iris });
            foreach (float x in new[] { 0f, 0.25f, 0.33f, 0.4f })
            {
                var cam = RenderCamera.LookAt(new Vector3(x, -6f, 2f), new Vector3(x, 0f, 2f), 20f);
                cam.Ev100 = 2f;
                Report($"iris x={x}", Centre(renderer, cam, 1, noTemporal, out _), CentreReference(iris, cam));
            }

            // 4. Far-field narrow beam, gobos open in the centre / a ring.
            foreach (var (shape, name) in new (Func<float, float, bool>, string)[]
                     {
                         ((u, v) => u * u + v * v < 0.45f * 0.45f, "centre disc"),
                         ((u, v) => u * u + v * v > 0.5f * 0.5f && u * u + v * v < 0.9f * 0.9f, "ring"),
                         ((u, v) => ((int)MathF.Floor((v + 1f) * 4f) & 1) == 0, "stripes"),
                     })
            {
                var narrow = StageScene.Spot(LightPosition, Vector3.Zero, 100000f, 6f, 9f, shadows: false, type: BeamType.Spot);
                narrow.SourceRadius = 0f;
                narrow.Gobo = renderer.World.CreateTexture(GoboFrom(shape), srgb: true);
                renderer.World.SetLights(new[] { narrow });
                var cam = RenderCamera.LookAt(new Vector3(0f, -3f, 2f), new Vector3(0f, 0f, 2f), 20f);
                cam.Ev100 = 2f;
                var copy = narrow;
                double Cookie(Vector3 x, Vector3 w, int k)
                {
                    var p = Gate(copy, w);
                    return Math.Abs(p.X) <= 1 && Math.Abs(p.Y) <= 1 && shape(p.X, p.Y) ? 1 : 0;
                }

                double expected = CentreReference(narrow, cam, Cookie);
                Report($"far gobo {name} 1 frame", Centre(renderer, cam, 1, noTemporal, out _), expected);
                Report($"far gobo {name} 32 frames", Centre(renderer, cam, 32, temporal, out _), expected);

                // Same in the near field (lens 8 cm): the crossing 1.5 m below the lens.
                narrow.SourceRadius = 0.08f;
                renderer.World.SetLights(new[] { narrow });
                var camNear = RenderCamera.LookAt(new Vector3(0f, -3f, 2.5f), new Vector3(0f, 0f, 2.5f), 20f);
                camNear.Ev100 = 2f;
                copy = narrow;
                double expectedNear = CentreReference(narrow, camNear, Cookie);
                Report($"near gobo {name} 1 frame", Centre(renderer, camNear, 1, noTemporal, out _), expectedNear);
                Report($"near gobo {name} 32 frames", Centre(renderer, camNear, 32, temporal, out _), expectedNear);
                renderer.World.DestroyTexture(narrow.Gobo);
            }

            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableFact]
    public void ProbeImage()
    {
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            renderer.World.SetAtmosphere(new AtmosphereState { BaseScattering = Haze, Anisotropy = Anisotropy });
            var settings = new PreviewSettings { TemporalAccumulation = false, Bloom = false, Indirect = IndirectLighting.Off };
            var view = new ViewOptions { ShowGrid = false, BackgroundLuminance = 0f };

            // Full image of a diagonal narrow beam against the reference (tile culling, upsampling).
            var beam = StageScene.Spot(new Vector3(-3f, 2f, 5f), new Vector3(2f, 0f, 0f), 50000f, 8f, 12f, shadows: false, type: BeamType.Spot);
            renderer.World.SetLights(new[] { beam });
            const int w = 128, h = 96;
            using (var target = renderer.CreateTarget(w, h))
            {
                var cam = RenderCamera.LookAt(new Vector3(0f, -8f, 2.5f), new Vector3(0f, 0f, 2.5f), 50f);
                cam.Ev100 = 2f;
                renderer.Render(target, cam, view, settings, 1f / 60f);
                var hdr = renderer.ReadHdr(target);
                double worst = 0, peak = 0, sumM = 0, sumR = 0;
                var reference = new double[w * h];
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        var (o, d) = BeamReference.PixelRay(cam, w, h, x, y);
                        reference[y * w + x] = General(beam, o, d, Haze, Anisotropy, 40, null, 4000);
                        peak = Math.Max(peak, reference[y * w + x]);
                    }
                }

                int bad = 0;
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        double m = hdr.LuminanceAt(x, y), r = reference[y * w + x];
                        if (y >= 40)
                        {
                            sumM += m;
                            sumR += r;
                        }
                        double e = Math.Abs(m - r) / peak;
                        worst = Math.Max(worst, e);
                        if (e > 0.05)
                        {
                            bad++;
                            if (bad < 40 && y >= 34)
                                _output.WriteLine($"  px ({x},{y}) measured {m:0.000} reference {r:0.000}");
                        }
                    }
                }

                _output.WriteLine($"diagonal beam: peak {peak:0.000}, worst {worst:P1} of peak, pixels > 5 %: {bad}, total ratio {sumM / sumR:0.0000}");
            }

            // Wall in front of the beam's lower half: no beam light on the wall.
            var scene = new StageScene(renderer);
            var wall = scene.Box(new Vector3(6f, 0.1f, 2f), new Vector3(0f, -1f, 1f), scene.Lambert(new Vector3(0.0f)), InstanceFlags.None);
            var down = StageScene.Spot(LightPosition, Vector3.Zero, 30000f, 20f, 30f, shadows: false, type: BeamType.Spot);
            renderer.World.SetLights(new[] { down });
            using (var target = renderer.CreateTarget(Width, Height))
            {
                var cam = RenderCamera.LookAt(new Vector3(0f, -6f, 2f), new Vector3(0f, 0f, 2f), 40f);
                cam.Ev100 = 2f;
                renderer.Render(target, cam, view, settings, 1f / 60f);
                var hdr = renderer.ReadHdr(target);
                ImageFiles.WritePng(renderer.ReadPixels(target), Path.Combine(TestRenderers.OutputDirectory, "probe-wall.png"));
                // Wall covers z < 2 at y=-1: rows below the wall top. Wall top z=2 projects at the centre row roughly; check rows well below.
                double maxWall = 0, maxAbove = 0;
                for (int y = Height / 2 + 6; y < Height; y++)
                    for (int x = 0; x < Width; x++)
                        maxWall = Math.Max(maxWall, hdr.LuminanceAt(x, y));
                for (int y = 0; y < Height / 2 - 6; y++)
                    for (int x = 0; x < Width; x++)
                        maxAbove = Math.Max(maxAbove, hdr.LuminanceAt(x, y));
                _output.WriteLine($"wall: max on wall {maxWall:0.0000}, max above {maxAbove:0.0000}");
            }

            renderer.World.DestroyInstance(wall);

            // A long beam over the background, pointing away from the camera.
            var longBeam = StageScene.Spot(new Vector3(0f, 0f, 3f), new Vector3(0f, 200f, 3f), 2000000f, 4f, 6f, shadows: false, type: BeamType.Spot);
            renderer.World.SetLights(new[] { longBeam });
            using (var target = renderer.CreateTarget(Width, Height))
            {
                var cam = RenderCamera.LookAt(new Vector3(-30f, -10f, 3f), new Vector3(0f, 40f, 3f), 60f);
                cam.Ev100 = 2f;
                renderer.Render(target, cam, view, settings, 1f / 60f);
                var hdr = renderer.ReadHdr(target);
                ImageFiles.WritePng(renderer.ReadPixels(target), Path.Combine(TestRenderers.OutputDirectory, "probe-long.png"));
                var row = new System.Text.StringBuilder();
                for (int x = 0; x < Width; x += 8)
                {
                    var (o, d) = BeamReference.PixelRay(cam, Width, Height, x, Height / 2);
                    row.Append($"{x}: {hdr.LuminanceAt(x, Height / 2):0.000}/{General(longBeam, o, d, Haze, Anisotropy, 400, null, 40000):0.000}  ");
                }

                _output.WriteLine("long beam row: " + row);
            }

            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }
}
