using System.Numerics;
using Candela.Core.Geometry;

namespace Candela.Rendering.Vulkan.Tests;

/// <summary>
/// White furnace tests: in surroundings of uniform luminance, a white surface that conserves energy (and a medium that
/// only scatters) can never be brighter than the surroundings, and a perfect one is invisible. A Lambertian sphere of
/// albedo 1 reflects exactly the background; dielectric and metallic GGX spheres lose a little (single scattering);
/// a box of smoke and a room-sized haze with albedo 1 vanish.
/// </summary>
public sealed class PathTracerFurnaceTests
{
    private const int Size = 64;

    private static RenderCamera Camera()
    {
        var camera = RenderCamera.LookAt(new Vector3(0f, -6f, 0f), Vector3.Zero, 30f);
        camera.Ev100 = 0f;
        return camera;
    }

    /// <summary>Mean luminance and the largest 4 x 4-block mean inside the disc of the sphere's image (radius in pixels).</summary>
    private static (float Mean, float MaxBlock) SphereStatistics(HdrImage hdr, float radiusPixels)
    {
        double sum = 0;
        int n = 0;
        float maxBlock = 0f;
        float c = (Size - 1) / 2f;
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                if (MathF.Sqrt((x - c) * (x - c) + (y - c) * (y - c)) > radiusPixels)
                    continue;
                sum += hdr.LuminanceAt(x, y);
                n++;
                if (x % 4 == 0 && y % 4 == 0 && MathF.Sqrt((x + 2 - c) * (x + 2 - c) + (y + 2 - c) * (y + 2 - c)) < radiusPixels - 3f)
                    maxBlock = MathF.Max(maxBlock, hdr.MeanLuminance(x + 1, y + 1, 1));
            }
        }

        return ((float)(sum / n), maxBlock);
    }

    [SkippableTheory]
    [InlineData(1f, 0f, 1f, 1f, 0.999f)]     // Lambertian, albedo 1: exactly the background
    [InlineData(1.5f, 0f, 0.5f, 1f, 0.94f)]  // white plastic: diffuse + GGX specular, a few % lost at grazing angles
    [InlineData(1.5f, 1f, 0.3f, 1f, 0.94f)]  // white (F0 = 1) rough metal: single-scattering GGX loses a little
    [InlineData(1.5f, 1f, 0.02f, 1f, 0.97f)] // a mirror
    public void WhiteSphereConservesEnergy(float ior, float metallic, float roughness, float maximum, float minimum)
    {
        var renderer = FinalRenderSupport.CreateOrSkip();
        try
        {
            var world = renderer.World;
            var mesh = world.CreateMesh(ProceduralMeshes.Sphere(1f, 96, 48));
            var material = world.CreateMaterial(new MaterialData
            {
                Name = "furnace",
                BaseColorFactor = Vector4.One,
                Metallic = metallic,
                Roughness = roughness,
                Ior = ior,
            }, null);
            world.CreateInstance(mesh, new[] { material }, Matrix4x4.Identity, InstanceFlags.None, 1);
            using var render = FinalRenderSupport.RenderToCompletion(renderer, Camera(), FinalRenderSupport.Settings(Size, Size, 32, bounces: 16),
                new FinalRenderOptions { BackgroundLuminance = 1f });
            var hdr = FinalRenderSupport.Luminance(render.ResultHdr(), render.Exposure);
            // the sphere (radius 1 at 6 m) spans 2 atan(1/6) / 30° of the image height
            float radius = Size / 2f * MathF.Tan(MathF.Asin(1f / 6f)) / MathF.Tan(15f * MathF.PI / 180f);
            var (mean, maxBlock) = SphereStatistics(hdr, radius - 1.5f);
            Assert.InRange(mean, minimum, maximum * 1.005f);
            Assert.True(maxBlock <= maximum * 1.03f, $"brightest block {maxBlock}");
            // the background around it
            Assert.InRange(hdr.MeanLuminance(2, 2, 1), 0.995f, 1.005f);
            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableFact]
    public void ScatteringOnlySmokeAndHazeAreInvisible()
    {
        var renderer = FinalRenderSupport.CreateOrSkip();
        try
        {
            var world = renderer.World;
            // A 4 m cube of smoke of optical depth 4 (density 1 x 1/m) in clear air.
            var grid = new VolumeGrid
            {
                Min = new Vector3(-2f),
                Max = new Vector3(2f),
                SizeX = 4,
                SizeY = 4,
                SizeZ = 4,
                Values = Enumerable.Repeat(1f, 64).ToArray(),
            };
            world.SetAtmosphere(new AtmosphereState { BaseScattering = 0f, Anisotropy = 0.5f, SmokeDensity = grid, SmokeScatteringAtFullDensity = 1f });
            var settings = FinalRenderSupport.Settings(Size, Size, 32, bounces: 8, volumeBounces: 64);
            using (var render = FinalRenderSupport.RenderToCompletion(renderer, Camera(), settings, new FinalRenderOptions { BackgroundLuminance = 1f }))
            {
                var hdr = FinalRenderSupport.Luminance(render.ResultHdr(), render.Exposure);
                float centre = hdr.MeanLuminance(Size / 2, Size / 2, 8);
                Assert.InRange(centre, 0.97f, 1.03f);
            }

            // Uniform haze filling the scene's media box around the camera (no surfaces at all).
            world.SetAtmosphere(new AtmosphereState { BaseScattering = 0.1f, Anisotropy = 0.7f });
            using (var render = FinalRenderSupport.RenderToCompletion(renderer, Camera(), settings, new FinalRenderOptions { BackgroundLuminance = 1f }))
            {
                var hdr = FinalRenderSupport.Luminance(render.ResultHdr(), render.Exposure);
                Assert.InRange(hdr.MeanLuminance(Size / 2, Size / 2, 16), 0.97f, 1.03f);
            }

            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }
}
