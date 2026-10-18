using System.Numerics;
using Candela.Photometry;

namespace Candela.Rendering.Vulkan.Tests;

/// <summary>
/// 1000 spot lights over a white floor: frames render without validation errors and the clustered shading still adds
/// up every light (the HDR luminance at sample points equals the CPU sum over all lights).
/// </summary>
public sealed class ManyLightsTests
{
    private const int Width = 320;
    private const int Height = 240;
    private const int LightsX = 40;
    private const int LightsY = 25;

    [SkippableFact]
    public void ThousandLightsRenderWithoutValidationErrorsAndAddUp()
    {
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            var world = renderer.World;
            var floor = world.CreateMesh(ProceduralMeshes.Plane(40f, 30f));
            var white = world.CreateMaterial(TopDownView.WhiteLambertian(), null);
            world.CreateInstance(floor, new[] { white }, Matrix4x4.Identity, InstanceFlags.None, 1);

            var random = new Random(1234);
            var lights = new SpotLight[LightsX * LightsY];
            var profiles = new BeamDistribution[lights.Length];
            for (int y = 0; y < LightsY; y++)
            {
                for (int x = 0; x < LightsX; x++)
                {
                    int i = y * LightsX + x;
                    var position = new Vector3(-9.75f + x * 0.5f, -6f + y * 0.5f, 3f + 0.5f * random.NextSingle());
                    var aim = new Vector3(position.X + 0.6f * (random.NextSingle() - 0.5f), position.Y + 0.6f * (random.NextSingle() - 0.5f), 0f);
                    var color = LightColor.FromRec709(new Vector3(0.1f + random.NextSingle(), 0.1f + random.NextSingle(), 0.1f + random.NextSingle()));
                    float beam = 12f + 12f * random.NextSingle();
                    lights[i] = ProceduralTestScene.Spot(position, aim, color, 2000f + 6000f * random.NextSingle(), beam, beam * 1.5f);
                    profiles[i] = BeamDistribution.Create(LightPacker.DefaultBeamType, lights[i].BeamAngle, lights[i].FieldAngle);
                }
            }

            world.SetLights(lights);
            var view = new TopDownView(Width, Height, 14f, ev100: 10.5f);
            using var target = renderer.CreateTarget(Width, Height);
            var options = new ViewOptions { Shading = ViewShading.Full, ShowGrid = false, BackgroundLuminance = 0f };
            for (int frame = 0; frame < 3; frame++)
                renderer.Render(target, view.Camera, options, new PreviewSettings(), 1f / 60f);
            Assert.Equal(1000, renderer.LastStatistics.Lights);
            var hdr = renderer.ReadHdr(target);
            ImageFiles.WritePng(renderer.ReadPixels(target), Path.Combine(TestRenderers.OutputDirectory, "many-lights.png"));
            TestVulkan.AssertNoValidationErrors(renderer.Context);

            // Sample points on a grid inside the lit area; compare against the sum over all 1000 lights.
            float maxExpected = 0f;
            var samples = new List<(int X, int Y, float Expected)>();
            for (int sy = 0; sy < 7; sy++)
            {
                for (int sx = 0; sx < 9; sx++)
                {
                    var (px, py, p) = view.NearestPixel(new Vector3(-8.3f + sx * 2.07f, -5.1f + sy * 1.73f, 0f));
                    float expected = 0f;
                    for (int i = 0; i < lights.Length; i++)
                    {
                        var profile = profiles[i];
                        expected += PhotometricTests.FloorLuminance(lights[i], profile.Relative, p);
                    }

                    samples.Add((px, py, expected));
                    maxExpected = MathF.Max(maxExpected, expected);
                }
            }

            Assert.True(maxExpected > 100f, $"the floor should be lit (max {maxExpected} cd/m²)");
            foreach (var (px, py, expected) in samples)
            {
                float measured = hdr.LuminanceAt(px, py);
                float error = MathF.Abs(measured - expected);
                Assert.True(error <= 0.03f * expected + 0.002f * maxExpected,
                    $"pixel ({px}, {py}): {measured:F3} cd/m², expected {expected:F3} cd/m² from the sum over all lights");
            }
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }
}
