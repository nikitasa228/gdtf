using System.Collections.Concurrent;
using System.Numerics;
using Candela.Core.Geometry;
using Candela.Photometry;
using Xunit.Abstractions;

namespace Candela.Rendering.Vulkan.Tests;

/// <summary>
/// Regression tests of the volumetric-beam review: gobo patterns with radial structure in the far field (the sampling of
/// the cookie along the beam crossing), beams thinner than the half-resolution sample spacing (thin-beam
/// anti-aliasing), the iris edge in the air, a camera inside a beam, temporal accumulation over the background and a
/// flame atlas that would not fit in memory.
/// </summary>
public sealed class BeamReviewTests
{
    private const float Haze = 0.02f;
    private const float Anisotropy = 0.7f;
    private static readonly Vector3 LightPosition = new(0f, 0f, 4f);
    private static readonly ViewOptions Dark = new() { ShowGrid = false, BackgroundLuminance = 0f };

    private readonly ITestOutputHelper _output;

    public BeamReviewTests(ITestOutputHelper output) => _output = output;

    [SkippableTheory]
    [InlineData(0f, 0.25f)]
    [InlineData(0.5f, 0.9f)]
    [InlineData(0f, 0.7f)]
    public void RadialGoboInTheFarFieldMatchesTheIntegral(float inner, float outer)
    {
        // A 6° point-source beam (far field only) with a gobo that opens a disc or a ring of the gate, crossed through
        // its axis 2 m below the light and seen through a narrow lens (the crossing is many pixels wide). The cookie
        // samples follow the profile within each segment; a sample whose inversion of the profile integral had
        // converged used to be bisected away again, which shifted ~7 % of them and biased radial patterns by ±10 %.
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            renderer.World.SetAtmosphere(new AtmosphereState { BaseScattering = Haze, Anisotropy = Anisotropy });
            bool Open(float u, float v) => u * u + v * v >= inner * inner && u * u + v * v < outer * outer;
            var light = StageScene.Spot(LightPosition, Vector3.Zero, 100000f, 6f, 9f, shadows: false, type: BeamType.Spot);
            light.SourceRadius = 0f;
            light.Gobo = renderer.World.CreateTexture(Gobo(Open), srgb: true);
            renderer.World.SetLights(new[] { light });
            var camera = RenderCamera.LookAt(new Vector3(0f, -3f, 2f), new Vector3(0f, 0f, 2f), 5f);
            camera.Ev100 = 2f;
            const int width = 160, height = 120;
            using var target = renderer.CreateTarget(width, height);
            var settings = new PreviewSettings { TemporalAccumulation = false, Bloom = false };
            const int frames = 48;
            double measured = 0;
            for (int i = 0; i < frames; i++)
            {
                renderer.Render(target, camera, Dark, settings, 1f / 60f);
                measured += CentreLuminance(renderer.ReadHdr(target)) / frames;
            }

            var copy = light;
            double Cookie(Vector3 x, Vector3 w, int k)
            {
                var p = BeamReference.Gate(copy, w);
                return MathF.Abs(p.X) <= 1f && MathF.Abs(p.Y) <= 1f && Open(p.X, p.Y) ? 1.0 : 0.0;
            }

            double expected = CentreReference(light, camera, width, height, Cookie);
            double open = CentreReference(light, camera, width, height, null);
            _output.WriteLine($"gobo {inner}..{outer}: measured {measured:0.0000}, reference {expected:0.0000}, open beam {open:0.0000} cd/m²");
            Assert.True(expected > 0.2 * open && expected < 0.95 * open, "the gobo blocks part of the crossing");
            Assert.InRange(Math.Abs(measured - expected) / expected, 0, 0.04);
            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void BeamThinnerThanASampleKeepsItsLightUpToTheSource(bool diagonal)
    {
        // A 4° beam seen from 30 m: towards its lens it is narrower than the spacing of the half-resolution samples
        // (2 pixels). It used to vanish there (horizontal: every sample row above or below it) or break into dots
        // (diagonal); widened to 1.5 samples with its flux kept, the light summed across the beam (over the rows of a
        // column) follows the reference everywhere along the beam.
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            renderer.World.SetAtmosphere(new AtmosphereState { BaseScattering = Haze, Anisotropy = Anisotropy });
            var target = diagonal ? new Vector3(0f, 200f, 60f) : new Vector3(0f, 200f, 3f);
            var light = StageScene.Spot(new Vector3(0f, 0f, 3f), target, 2000000f, 4f, 6f, shadows: false, type: BeamType.Spot);
            renderer.World.SetLights(new[] { light });
            const int width = 160, height = 120;
            using var rt = renderer.CreateTarget(width, height);
            var camera = RenderCamera.LookAt(new Vector3(-30f, -10f, 3f), new Vector3(0f, 40f, 3f), 60f);
            camera.Ev100 = 2f;
            renderer.Render(rt, camera, Dark, new PreviewSettings { TemporalAccumulation = false, Bloom = false, Indirect = IndirectLighting.Off }, 1f / 60f);
            var hdr = renderer.ReadHdr(rt);
            TestVulkan.AssertNoValidationErrors(renderer.Context);
            var origin = Project(camera, width, height, light.Position);
            var far = Project(camera, width, height, light.Position + Vector3.Normalize(target - light.Position) * 60f);
            double worst = 0;
            int checkedColumns = 0;
            for (int x = 100; x < width; x += 6)
            {
                // The beam's axis in this column and a window of rows around it.
                float s = (x + 0.5f - far.X) / (origin.X - far.X);
                if (s < 0f || s > 1f)
                    continue;
                int row = (int)(far.Y + s * (origin.Y - far.Y));
                double measured = 0, reference = 0;
                for (int y = Math.Max(row - 7, 0); y <= Math.Min(row + 7, height - 1); y++)
                {
                    measured += hdr.LuminanceAt(x, y);
                    for (int sub = 0; sub < 8; sub++)
                    {
                        var (o, d) = BeamReference.PixelRay(camera, width, height, x, y - 0.4375f + 0.125f * sub);
                        reference += BeamReference.InScatteringPacked(light, o, d, Haze, Anisotropy, 400, steps: 8000) / 8;
                    }
                }

                double error = Math.Abs(measured - reference) / reference;
                _output.WriteLine($"column {x} (axis row {row}): measured {measured:0.000}, reference {reference:0.000}, {error:P1}");
                worst = Math.Max(worst, error);
                checkedColumns++;
            }

            Assert.True(checkedColumns >= 6, $"{checkedColumns} columns");
            Assert.InRange(worst, 0, 0.06);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableTheory]
    [InlineData(0f)]
    [InlineData(0.2f)]
    [InlineData(0.235f)]
    public void IrisEdgeInTheAirMatchesTheIntegral(float offset)
    {
        // A 20°/30° spot with the iris half closed (7.5° half-angle, soft from 6.75°), crossed 2 m below the lens at
        // offsets up to the iris's soft edge, seen through a narrow lens.
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            renderer.World.SetAtmosphere(new AtmosphereState { BaseScattering = Haze, Anisotropy = Anisotropy });
            var light = StageScene.Spot(LightPosition, Vector3.Zero, 30000f, 20f, 30f, shadows: false, type: BeamType.Spot);
            light.Iris = 0.5f;
            renderer.World.SetLights(new[] { light });
            const int width = 160, height = 120;
            var camera = RenderCamera.LookAt(new Vector3(offset, -6f, 2f), new Vector3(offset, 0f, 2f), 4f);
            camera.Ev100 = 2f;
            using var rt = renderer.CreateTarget(width, height);
            renderer.Render(rt, camera, Dark, new PreviewSettings { TemporalAccumulation = false, Bloom = false }, 1f / 60f);
            double measured = CentreLuminance(renderer.ReadHdr(rt));
            double expected = CentreReference(light, camera, width, height, null);
            var axisCamera = RenderCamera.LookAt(new Vector3(0f, -6f, 2f), new Vector3(0f, 0f, 2f), 4f);
            double onAxis = CentreReference(light, axisCamera, width, height, null);
            _output.WriteLine($"offset {offset} m: measured {measured:0.0000}, reference {expected:0.0000}, on the axis {onAxis:0.0000} cd/m²");
            Assert.InRange(Math.Abs(measured - expected), 0, 0.02 * onAxis + 0.03 * expected);
            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void CameraInsideTheBeamMatchesTheIntegral(bool towardsSource)
    {
        // The eye inside a 40° wash 2 m below the lens: looking across the beam, and up towards the source (where the
        // forward-peaked phase function makes the haze brightest).
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            renderer.World.SetAtmosphere(new AtmosphereState { BaseScattering = Haze, Anisotropy = Anisotropy });
            var light = StageScene.Spot(LightPosition, Vector3.Zero, 10000f, 40f, 60f, shadows: false);
            renderer.World.SetLights(new[] { light });
            const int width = 160, height = 120;
            var eye = new Vector3(0.2f, -0.1f, 2f);
            var look = towardsSource ? new Vector3(0.6f, 0.3f, 3.2f) : new Vector3(0.2f, 3f, 2f);
            var camera = RenderCamera.LookAt(eye, look, 30f);
            camera.Ev100 = 2f;
            using var rt = renderer.CreateTarget(width, height);
            renderer.Render(rt, camera, Dark, new PreviewSettings { TemporalAccumulation = false, Bloom = false }, 1f / 60f);
            double measured = CentreLuminance(renderer.ReadHdr(rt));
            double expected = CentreReference(light, camera, width, height, null);
            _output.WriteLine($"measured {measured:0.0000}, reference {expected:0.0000} cd/m²");
            Assert.True(expected > 0.1, $"reference {expected}");
            Assert.InRange(Math.Abs(measured - expected) / expected, 0, 0.05);
            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableTheory]
    [InlineData(0f)]
    [InlineData(0.6f)]
    [InlineData(1.5f)]
    public void LookingUpANarrowBeamFromAfarMatchesTheIntegral(float aboveLens)
    {
        // The eye 20 m down a 6° beam, 0.4 m off its axis, looking back at the fixture at a low resolution: the view
        // rays run inside the beam for metres, so the thin-beam widening must not dim them (a widening of the whole
        // beam lost 7–18 % here). Looking past the lens the centre pixels follow the integral; looking into the lens
        // (smaller than a half-resolution sample) its light is spread over the samples around it, so the light of the
        // pixels around the lens follows the integral.
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            renderer.World.SetAtmosphere(new AtmosphereState { BaseScattering = Haze, Anisotropy = Anisotropy });
            var light = StageScene.Spot(new Vector3(0f, 0f, 3f), new Vector3(0f, 40f, 3f), 200000f, 6f, 9f, shadows: false, type: BeamType.Spot);
            renderer.World.SetLights(new[] { light });
            const int width = 160, height = 120;
            var camera = RenderCamera.LookAt(new Vector3(0.4f, 20f, 3.1f), new Vector3(0f, 0f, 3f + aboveLens), 30f);
            camera.Ev100 = 2f;
            using var rt = renderer.CreateTarget(width, height);
            renderer.Render(rt, camera, Dark, new PreviewSettings { TemporalAccumulation = false, Bloom = false }, 1f / 60f);
            var hdr = renderer.ReadHdr(rt);
            TestVulkan.AssertNoValidationErrors(renderer.Context);
            if (aboveLens > 0f)
            {
                double measured = CentreLuminance(hdr);
                double expected = CentreReference(light, camera, width, height, null);
                _output.WriteLine($"{aboveLens} m above the lens: measured {measured:0.0000}, reference {expected:0.0000} cd/m²");
                Assert.True(expected > 0.1, $"reference {expected}");
                Assert.InRange(Math.Abs(measured - expected) / expected, 0, 0.05);
                return;
            }

            double windowMeasured = 0, windowReference = 0;
            for (int y = height / 2 - 8; y < height / 2 + 8; y++)
            {
                for (int x = width / 2 - 8; x < width / 2 + 8; x++)
                {
                    windowMeasured += hdr.LuminanceAt(x, y);
                    for (int sy = 0; sy < 4; sy++)
                    {
                        for (int sx = 0; sx < 4; sx++)
                        {
                            var (o, d) = BeamReference.PixelRay(camera, width, height, x - 0.375f + 0.25f * sx, y - 0.375f + 0.25f * sy);
                            windowReference += BeamReference.InScatteringPacked(light, o, d, Haze, Anisotropy, 30, steps: 6000) / 16;
                        }
                    }
                }
            }

            _output.WriteLine($"16 × 16 pixels around the lens: measured {windowMeasured:0}, reference {windowReference:0} cd/m² (sum)");
            Assert.InRange(Math.Abs(windowMeasured - windowReference) / windowReference, 0, 0.05);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableFact]
    public void UnobstructedShadowedBeamIsAsSmoothAsAnUnshadowedOne()
    {
        // A shadow-casting spot over a floor with nothing in its beam: the shadow map modulates every sample by 1, so a
        // single frame must equal the same beam without a shadow map — no sampling noise (the cookie samples' weighted
        // mean is applied to the exact integral of each segment, a ratio estimator).
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            var scene = new StageScene(renderer);
            scene.Floor(20f, scene.Lambert(new Vector3(0.3f)));
            renderer.World.SetAtmosphere(new AtmosphereState { BaseScattering = Haze, Anisotropy = Anisotropy });
            var camera = RenderCamera.LookAt(new Vector3(0f, -7f, 2f), new Vector3(0f, 0f, 2f), 40f);
            camera.Ev100 = 3f;
            var settings = new PreviewSettings { TemporalAccumulation = false, Bloom = false, Indirect = IndirectLighting.Off };
            HdrReadback Render(bool shadows)
            {
                var light = StageScene.Spot(LightPosition, new Vector3(0.5f, 0f, 0f), 30000f, 20f, 30f, shadows: shadows, type: BeamType.Spot);
                renderer.World.SetLights(new[] { light });
                using var rt = renderer.CreateTarget(160, 120);
                renderer.Render(rt, camera, Dark, settings, 1f / 60f);
                Assert.Equal(shadows ? 1 : 0, renderer.LastStatistics.ShadowedLights);
                return renderer.ReadHdr(rt);
            }

            var plain = Render(false);
            var shadowed = Render(true);
            double peak = 0, worst = 0;
            for (int y = 0; y < 60; y++)
            {
                for (int x = 0; x < plain.Width; x++)
                    peak = Math.Max(peak, plain.LuminanceAt(x, y));
            }

            // The haze above the floor (the upper half of the image): the floor itself is lit with the shadow test.
            for (int y = 0; y < 60; y++)
            {
                for (int x = 0; x < plain.Width; x++)
                    worst = Math.Max(worst, Math.Abs(shadowed.LuminanceAt(x, y) - plain.LuminanceAt(x, y)) / peak);
            }

            _output.WriteLine($"peak {peak:0.000} cd/m², largest difference {worst:P2} of the peak");
            Assert.True(peak > 0.1, $"peak {peak}");
            Assert.InRange(worst, 0, 0.0005);
            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableFact]
    public void TemporalAccumulationSettlesGoboBeamsOverTheBackground()
    {
        // A gobo beam against the empty background (no geometry): the temporal pass reprojects the background at the
        // largest depth the half-float images hold, so the noise of the gobo samples settles there as well.
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            renderer.World.SetAtmosphere(new AtmosphereState { BaseScattering = Haze, Anisotropy = Anisotropy });
            var gobo = renderer.World.CreateTexture(StageScene.GoboImage(), srgb: true);
            var light = StageScene.Spot(new Vector3(0f, 0f, 6f), new Vector3(0f, 3f, 0f), 100000f, 12f, 16f, shadows: false, type: BeamType.Spot);
            light.Gobo = gobo;
            renderer.World.SetLights(new[] { light });
            var camera = RenderCamera.LookAt(new Vector3(0f, -8f, 3f), new Vector3(0f, 0f, 3f), 50f);
            camera.Ev100 = 2f;
            double Change(bool temporal)
            {
                var settings = new PreviewSettings { TemporalAccumulation = temporal, Bloom = false, Indirect = IndirectLighting.Off };
                using var rt = renderer.CreateTarget(128, 96);
                for (int i = 0; i < 24; i++)
                    renderer.Render(rt, camera, Dark, settings, 1f / 60f);
                var a = renderer.ReadHdr(rt);
                renderer.Render(rt, camera, Dark, settings, 1f / 60f);
                var b = renderer.ReadHdr(rt);
                double difference = 0, sum = 0;
                for (int y = 0; y < a.Height; y++)
                {
                    for (int x = 0; x < a.Width; x++)
                    {
                        difference += Math.Abs(a.LuminanceAt(x, y) - b.LuminanceAt(x, y));
                        sum += a.LuminanceAt(x, y);
                    }
                }

                return difference / sum;
            }

            double single = Change(false), accumulated = Change(true);
            _output.WriteLine($"frame-to-frame change: single frames {single:P2}, accumulated {accumulated:P2}");
            Assert.True(single > 0.002, "the gobo samples are noisy in single frames");
            Assert.True(accumulated < 0.5 * single, "the accumulated beam settles");
            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableFact]
    public void FlameAtlasBeyondItsBudgetSkipsGridsInsteadOfFailing()
    {
        // Three tiny flame grids whose shapes (2047 × 1 × 1, 1 × 2048 × 1, 1 × 1 × 2047) would pack into an atlas of
        // 2048 × 2048 × 2047 texels (8.6 billion: the frame failed with an overflow). The atlas keeps its texel budget:
        // the third grid is skipped with a warning and the frame renders.
        var log = new ConcurrentQueue<string>();
        var renderer = TestRenderers.CreateOrSkip(log);
        try
        {
            static VolumeGrid Grid(int x, int y, int z, Vector3 min)
            {
                var values = new float[x * y * z];
                Array.Fill(values, 1500f);
                return new VolumeGrid { Min = min, Max = min + new Vector3(0.5f), SizeX = x, SizeY = y, SizeZ = z, Values = values };
            }

            int limit = (int)Math.Min(renderer.Context.Capabilities.MaxImageDimension3D, 2048u);
            renderer.World.SetAtmosphere(new AtmosphereState
            {
                BaseScattering = 0.01f,
                FlameTemperature = Grid(limit - 1, 1, 1, new Vector3(-1f, 0f, 0.5f)),
                FlameTemperatures = new[] { Grid(1, limit, 1, new Vector3(0f, 0f, 0.5f)), Grid(1, 1, limit - 1, new Vector3(1f, 0f, 0.5f)) },
            });
            using var rt = renderer.CreateTarget(96, 64);
            var camera = RenderCamera.LookAt(new Vector3(0f, -4f, 1f), new Vector3(0f, 0f, 0.8f), 60f);
            renderer.Render(rt, camera, Dark, new PreviewSettings { Bloom = false }, 1f / 60f);
            foreach (float v in renderer.ReadHdr(rt).Rgba)
                Assert.True(float.IsFinite(v));
            Assert.Contains(log, m => m.Contains("Flame grids beyond", StringComparison.Ordinal));
            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    /// <summary>A gobo image: opaque where <paramref name="open"/> is false (gate coordinates −1 … 1, +v up).</summary>
    private static ImageData Gobo(Func<float, float, bool> open, int size = 256)
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

    /// <summary>Mean luminance of the four centre pixels.</summary>
    private static double CentreLuminance(HdrReadback hdr)
    {
        int cx = hdr.Width / 2, cy = hdr.Height / 2;
        return 0.25 * (hdr.LuminanceAt(cx - 1, cy - 1) + hdr.LuminanceAt(cx, cy - 1) + hdr.LuminanceAt(cx - 1, cy) + hdr.LuminanceAt(cx, cy));
    }

    /// <summary>The reference of <see cref="CentreLuminance"/>: the mean of the four centre pixels' integrals.</summary>
    private static double CentreReference(SpotLight light, RenderCamera camera, int width, int height, Func<Vector3, Vector3, int, double>? modulation)
    {
        double sum = 0;
        foreach (var (x, y) in new[] { (width / 2 - 1, height / 2 - 1), (width / 2, height / 2 - 1), (width / 2 - 1, height / 2), (width / 2, height / 2) })
        {
            var (o, d) = BeamReference.PixelRay(camera, width, height, x, y);
            sum += BeamReference.InScatteringPacked(light, o, d, Haze, Anisotropy, 60, modulation, 40000);
        }

        return 0.25 * sum;
    }

    /// <summary>Pixel position (x right, y down) of a world point.</summary>
    private static Vector2 Project(RenderCamera camera, int width, int height, Vector3 p)
    {
        var view = Vector3.Transform(p, camera.View);
        float tan = MathF.Tan(0.5f * camera.FovY);
        float aspect = width / (float)height;
        float nx = view.X / (-view.Z * tan * aspect), ny = view.Y / (-view.Z * tan);
        return new Vector2((nx * 0.5f + 0.5f) * width, (0.5f - ny * 0.5f) * height);
    }
}
