using System.Numerics;
using Candela.Photometry;
using Xunit.Abstractions;

namespace Candela.Rendering.Vulkan.Tests;

public sealed class BeamReviewProbe
{
    private readonly ITestOutputHelper _output;

    public BeamReviewProbe(ITestOutputHelper output) => _output = output;

    [SkippableFact]
    public void LongBeam()
    {
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            renderer.World.SetAtmosphere(new AtmosphereState { BaseScattering = 0.02f, Anisotropy = 0.7f });
            var settings = new PreviewSettings { TemporalAccumulation = false, Bloom = false, Indirect = IndirectLighting.Off };
            var view = new ViewOptions { ShowGrid = false, BackgroundLuminance = 0f };
            const int w = 160, h = 120;
            foreach (float lens in new[] { 0.08f, 0f })
            {
                var longBeam = StageScene.Spot(new Vector3(0f, 0f, 3f), new Vector3(0f, 200f, 3f), 2000000f, 4f, 6f, shadows: false, type: BeamType.Spot);
                longBeam.SourceRadius = lens;
                renderer.World.SetLights(new[] { longBeam });
                using var target = renderer.CreateTarget(w, h);
                var cam = RenderCamera.LookAt(new Vector3(-30f, -10f, 3f), new Vector3(0f, 40f, 3f), 60f);
                cam.Ev100 = 2f;
                renderer.Render(target, cam, view, settings, 1f / 60f);
                var hdr = renderer.ReadHdr(target);
                var row = new System.Text.StringBuilder();
                for (int x = 96; x < w; x += 4)
                {
                    double measured = 0, reference = 0;
                    for (int y = h / 2 - 8; y < h / 2 + 8; y++)
                    {
                        measured += hdr.LuminanceAt(x, y);
                        for (int sy = 0; sy < 4; sy++)
                        {
                            var (o, d) = BeamReference.PixelRay(cam, w, h, x, y - 0.375f + 0.25f * sy);
                            reference += 0.25 * BeamProbeTests.General(longBeam, o, d, 0.02, 0.7, 400, null, 20000);
                        }
                    }

                    row.Append($"{x}: {measured:0.000}/{reference:0.000}  ");
                }

                _output.WriteLine($"lens {lens}: " + row);
                _output.WriteLine($"stats beams {renderer.LastStatistics.RaymarchedBeams} froxel {renderer.LastStatistics.FroxelLights} pairs {renderer.LastStatistics.BeamTilePairs}");
            }
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableFact]
    public void BackgroundTemporal()
    {
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            renderer.World.SetAtmosphere(new AtmosphereState { BaseScattering = 0.02f, Anisotropy = 0.7f });
            var view = new ViewOptions { ShowGrid = false, BackgroundLuminance = 0f };
            var gobo = renderer.World.CreateTexture(StageScene.GoboImage(), srgb: true);
            var beam = StageScene.Spot(new Vector3(0f, 0f, 6f), new Vector3(0f, 3f, 0f), 100000f, 12f, 16f, shadows: false, type: BeamType.Spot);
            beam.Gobo = gobo;
            renderer.World.SetLights(new[] { beam });
            const int w = 128, h = 96;
            var cam = RenderCamera.LookAt(new Vector3(0f, -8f, 3f), new Vector3(0f, 0f, 3f), 50f);
            cam.Ev100 = 2f;
            foreach (bool temporal in new[] { false, true })
            {
                var settings = new PreviewSettings { TemporalAccumulation = temporal, Bloom = false, Indirect = IndirectLighting.Off };
                using var target = renderer.CreateTarget(w, h);
                for (int i = 0; i < 24; i++)
                    renderer.Render(target, cam, view, settings, 1f / 60f);
                var a = renderer.ReadHdr(target);
                renderer.Render(target, cam, view, settings, 1f / 60f);
                var b = renderer.ReadHdr(target);
                double diff = 0, sum = 0;
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        diff += Math.Abs(a.LuminanceAt(x, y) - b.LuminanceAt(x, y));
                        sum += a.LuminanceAt(x, y);
                    }

                _output.WriteLine($"temporal {temporal}: frame-to-frame change {diff / sum:P2}");
            }

            renderer.World.DestroyTexture(gobo);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }
}
