using System.Numerics;
using Candela.Core.Geometry;
using Candela.Photometry;

namespace Candela.Rendering.Vulkan.Tests;

/// <summary>
/// The GPU spot light reproduces the photometric model: one 10 000 cd spot straight down from 5 m onto a white
/// Lambertian floor (albedo 1, roughness 1, IOR 1 → no specular). Luminance L = ρ·E/π with E = I(θ)·cos α / d².
/// </summary>
public sealed class PhotometricTests
{
    private const float Height = 5f;
    private const float PeakCandela = 10000f;
    private const float BeamDeg = 50f;
    private const float FieldDeg = 80f;
    private const int Width = 320;
    private const int ImageHeight = 240;
    private const float OrthoHeight = 8f;

    [SkippableFact]
    public void PoolCentreAndOffAxisLuminanceMatchThePhotometricModel()
    {
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            var world = renderer.World;
            var floor = world.CreateMesh(ProceduralMeshes.Plane(40f, 40f));
            var white = world.CreateMaterial(new MaterialData
            {
                Name = "white lambertian",
                BaseColorFactor = Vector4.One,
                Metallic = 0f,
                Roughness = 1f,
                Ior = 1f,
            }, null);
            world.CreateInstance(floor, new[] { white }, Matrix4x4.Identity, InstanceFlags.None, 1);
            var light = ProceduralTestScene.Spot(new Vector3(0f, 0f, Height), Vector3.Zero, LightColor.White, PeakCandela, BeamDeg, FieldDeg);
            world.SetLights(new[] { light });

            var camera = RenderCamera.LookAt(new Vector3(0f, 0f, 10f), Vector3.Zero);
            camera.Projection = ProjectionKind.Orthographic;
            camera.OrthoHeight = OrthoHeight;
            camera.Near = 0.1f;
            camera.Far = 100f;
            camera.Ev100 = 7f;
            using var target = renderer.CreateTarget(Width, ImageHeight);
            renderer.Render(target, camera, new ViewOptions { Shading = ViewShading.Full, ShowGrid = false }, new PreviewSettings(), 0f);
            var hdr = renderer.ReadHdr(target);
            Assert.Equal(Core.Math.Units.ExposureFromEv100(7f), hdr.Exposure, 6);

            var beam = BeamDistribution.Create(BeamType.Wash, BeamDeg * MathF.PI / 180f, FieldDeg * MathF.PI / 180f);
            float Expected(Vector3 p)
            {
                var toPoint = p - light.Position;
                float d = toPoint.Length();
                float cosAlpha = Height / d;                  // incidence on the floor
                float theta = MathF.Acos(Vector3.Dot(toPoint / d, light.Direction));
                float illuminance = PeakCandela * beam.Relative(theta) * cosAlpha / (d * d);
                return illuminance / MathF.PI;                // ρ = 1
            }

            // Pool centre: exactly 10000 / 25 / π cd/m² under the light.
            var (cx, cy, centre) = NearestPixel(camera, Vector3.Zero);
            float centreLuminance = hdr.LuminanceAt(cx, cy);
            float centreExpected = Expected(centre);
            Assert.InRange(MathF.Abs(centreExpected - PeakCandela / 25f / MathF.PI) / centreExpected, 0f, 2e-3f);
            Assert.InRange(MathF.Abs(centreLuminance - centreExpected) / centreExpected, 0f, 0.02f);

            // White light on a white Lambertian surface: equal channels (Rec.2020 white is (1, 1, 1) at luminance 1).
            var rgb = hdr.At(cx, cy);
            Assert.InRange(MathF.Abs(rgb.X - rgb.Z) / rgb.Y, 0f, 0.01f);

            // 30° off axis: L(θ) = L(0) · profile(θ) · cos³θ.
            float r = Height * MathF.Tan(30f * MathF.PI / 180f);
            foreach (var direction in new[] { Vector3.UnitX, -Vector3.UnitY, Vector3.Normalize(new Vector3(-1f, 1f, 0f)) })
            {
                var (px, py, p) = NearestPixel(camera, direction * r);
                float measured = hdr.LuminanceAt(px, py);
                float expected = Expected(p);
                Assert.InRange(MathF.Abs(measured - expected) / expected, 0f, 0.03f);
            }

            // The model itself: profile(30°)·cos³(30°) relative to the centre.
            float theta30 = 30f * MathF.PI / 180f;
            float ratio = Expected(Vector3.UnitX * r) / Expected(Vector3.Zero);
            Assert.Equal(beam.Relative(theta30) * MathF.Pow(MathF.Cos(theta30), 3f), ratio, 4);
            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    /// <summary>The pixel whose centre is closest to a point of the floor, and that pixel centre on the floor.</summary>
    private static (int X, int Y, Vector3 Point) NearestPixel(RenderCamera camera, Vector3 point)
    {
        var viewProjection = camera.View * ReverseZ.Orthographic(OrthoHeight, Width / (float)ImageHeight, camera.Near, camera.Far);
        var clip = Vector4.Transform(new Vector4(point, 1f), viewProjection);
        float ndcX = clip.X / clip.W, ndcY = clip.Y / clip.W;
        int x = (int)MathF.Floor((ndcX * 0.5f + 0.5f) * Width);
        int y = (int)MathF.Floor((ndcY * 0.5f + 0.5f) * ImageHeight);
        Matrix4x4.Invert(viewProjection, out var inverse);
        var a = Vector4.Transform(new Vector4((x + 0.5f) / Width * 2f - 1f, (y + 0.5f) / ImageHeight * 2f - 1f, 1f, 1f), inverse);
        var b = Vector4.Transform(new Vector4((x + 0.5f) / Width * 2f - 1f, (y + 0.5f) / ImageHeight * 2f - 1f, 0.5f, 1f), inverse);
        var pa = new Vector3(a.X, a.Y, a.Z) / a.W;
        var pb = new Vector3(b.X, b.Y, b.Z) / b.W;
        float t = -pa.Z / (pb.Z - pa.Z);
        return (x, y, pa + (pb - pa) * t);
    }
}
