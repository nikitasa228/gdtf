using System.Numerics;
using Candela.Core.Geometry;
using Xunit.Abstractions;

namespace Candela.Rendering.Vulkan.Tests;

/// <summary>
/// Area emitters: an emissive panel (an LED wall, a screen) lights a white Lambertian floor with the illuminance of its
/// luminance and form factor, E = L·∫cos θ·cos θ'/r² dA (found by BSDF samples only, so this is also a check of their
/// weights).
/// </summary>
public sealed class PathTracerEmitterTests
{
    private const float PanelLuminance = 2000f;
    private const float PanelHeight = 1f;
    private const float PanelSize = 1f;

    private readonly ITestOutputHelper _output;

    public PathTracerEmitterTests(ITestOutputHelper output) => _output = output;

    /// <summary>Luminance of the white Lambertian floor at p under the downward-facing panel: L_floor = L·∫cos cos / r² dA / π.</summary>
    private static double FloorLuminance(Vector3 p)
    {
        const int n = 200;
        double sum = 0, cell = PanelSize / (double)n;
        for (int j = 0; j < n; j++)
        {
            for (int i = 0; i < n; i++)
            {
                var y = new Vector3((float)(-0.5 * PanelSize + (i + 0.5) * cell), (float)(-0.5 * PanelSize + (j + 0.5) * cell), PanelHeight);
                var d = y - p;
                double r2 = d.LengthSquared();
                double cos = d.Z / Math.Sqrt(r2); // the same at both ends (floor and panel are parallel)
                sum += cos * cos / r2 * cell * cell;
            }
        }

        return PanelLuminance * sum / Math.PI;
    }

    [SkippableFact]
    public void EmissivePanelLightsTheFloorLikeItsFormFactor()
    {
        var renderer = FinalRenderSupport.CreateOrSkip();
        try
        {
            var scene = new StageScene(renderer);
            scene.Floor(10f, scene.Lambert(Vector3.One, "white"));
            var panel = renderer.World.CreateMaterial(new MaterialData
            {
                Name = "LED panel",
                BaseColorFactor = new Vector4(0f, 0f, 0f, 1f),
                Ior = 1f,
                Roughness = 1f,
                EmissiveColor = Vector3.One,
                EmissiveLuminance = PanelLuminance,
            }, null);
            scene.Place(renderer.World.CreateMesh(ProceduralMeshes.Plane(PanelSize, PanelSize)), panel,
                Matrix4x4.CreateRotationX(MathF.PI) * Matrix4x4.CreateTranslation(0f, 0f, PanelHeight), InstanceFlags.CastShadow, 9);
            renderer.World.SetLights(Array.Empty<SpotLight>());
            // a low camera looking at the floor under the panel
            var camera = RenderCamera.LookAt(new Vector3(2.5f, 0f, 0.4f), Vector3.Zero, 6f);
            camera.Ev100 = 8f;
            const int size = 16;
            using var render = FinalRenderSupport.RenderToCompletion(renderer, camera, FinalRenderSupport.Settings(size, size, 1024, bounces: 1),
                new FinalRenderOptions { BackgroundLuminance = 0f, Seed = 4 });
            FinalRenderSupport.Save(render, "pt-emissive-panel");
            var hdr = FinalRenderSupport.Luminance(render.RawResultHdr(), render.Exposure);
            float measured = hdr.MeanLuminance(size / 2, size / 2, 2);
            double expected = 0;
            for (int y = size / 2 - 2; y <= size / 2 + 2; y++)
            {
                for (int x = size / 2 - 2; x <= size / 2 + 2; x++)
                {
                    var (o, d) = BeamReference.PixelRay(camera, size, size, x, y);
                    expected += FloorLuminance(o + d * (-o.Z / d.Z)) / 25.0;
                }
            }

            _output.WriteLine($"floor under the panel: path tracer {measured:0.0} cd/m², form factor {expected:0.0} cd/m²");
            Assert.InRange(Math.Abs(measured - expected) / expected, 0, 0.03);
            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }
}
