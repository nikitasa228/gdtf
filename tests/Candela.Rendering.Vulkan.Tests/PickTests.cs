using System.Numerics;

namespace Candela.Rendering.Vulkan.Tests;

/// <summary>Picking returns the instance's pick id, its world position (within 1 cm) and the surface normal.</summary>
public sealed class PickTests
{
    private const int Width = 320;
    private const int Height = 240;

    [SkippableTheory]
    [InlineData(ProjectionKind.Perspective)]
    [InlineData(ProjectionKind.Orthographic)]
    public void PickReturnsIdPositionAndNormal(ProjectionKind projection)
    {
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            ProceduralTestScene.Build(renderer.World);
            var camera = ProceduralTestScene.CreateCamera();
            camera.Projection = projection;
            camera.OrthoHeight = 16f;
            camera.Near = projection == ProjectionKind.Orthographic ? 0.5f : 0.05f;
            camera.Far = 200f;
            using var target = renderer.CreateTarget(Width, Height);
            Assert.Null(renderer.Pick(target, 10, 10)); // nothing rendered yet
            foreach (var shading in new[] { ViewShading.Full, ViewShading.Colorless, ViewShading.Wireframe })
            {
                renderer.Render(target, camera, new ViewOptions { Shading = shading }, new PreviewSettings(), 0f);
                var viewProjection = ViewProjection(camera);

                // Gold sphere: centre (0.3, 1.6, 0.6), radius 0.6.
                var sphereCentre = new Vector3(0.3f, 1.6f, 0.6f);
                var (sx, sy) = PixelOf(viewProjection, sphereCentre);
                var hit = renderer.Pick(target, sx, sy);
                Assert.NotNull(hit);
                Assert.Equal(ProceduralTestScene.PickIds.GoldSphere, hit!.Value.PickId);
                var (origin, direction) = Ray(viewProjection, sx, sy);
                var expected = IntersectSphere(origin, direction, sphereCentre, 0.6f);
                Assert.InRange(Vector3.Distance(hit.Value.WorldPosition, expected), 0f, 0.01f);
                var expectedNormal = Vector3.Normalize(expected - sphereCentre);
                Assert.True(Vector3.Dot(hit.Value.Normal, expectedNormal) > 0.95f, $"normal {hit.Value.Normal} vs {expectedNormal}");

                // Floor in front of the objects.
                var floorPoint = new Vector3(-1.5f, -1.0f, 0f);
                var (fx, fy) = PixelOf(viewProjection, floorPoint);
                var floorHit = renderer.Pick(target, fx, fy);
                Assert.NotNull(floorHit);
                Assert.Equal(ProceduralTestScene.PickIds.Floor, floorHit!.Value.PickId);
                (origin, direction) = Ray(viewProjection, fx, fy);
                var floorExpected = origin + direction * (-origin.Z / direction.Z);
                Assert.InRange(Vector3.Distance(floorHit.Value.WorldPosition, floorExpected), 0f, 0.01f);
                Assert.True(Vector3.Dot(floorHit.Value.Normal, Vector3.UnitZ) > 0.999f, $"floor normal {floorHit.Value.Normal}");
                float viewDepth = Vector3.Dot(floorExpected - camera.Position, Vector3.Normalize(new Vector3(0f, 1.5f, 1.0f) - camera.Position));
                Assert.InRange(MathF.Abs(floorHit.Value.Depth - viewDepth), 0f, 0.01f);

                // Background above the wall, and outside the target.
                Assert.Null(renderer.Pick(target, 2, 2));
                Assert.Null(renderer.Pick(target, -1, 5));
                Assert.Null(renderer.Pick(target, Width, 5));
            }

            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableFact]
    public void NotPickableInstancesAreTransparentToPicking()
    {
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            var world = renderer.World;
            var box = world.CreateMesh(ProceduralMeshes.Box(Vector3.One));
            var material = world.CreateMaterial(new Core.Geometry.MaterialData(), null);
            var instance = world.CreateInstance(box, new[] { material }, Matrix4x4.Identity, InstanceFlags.None, 42);
            var camera = RenderCamera.LookAt(new Vector3(0f, -5f, 0f), Vector3.Zero);
            using var target = renderer.CreateTarget(64, 48);
            renderer.Render(target, camera, new ViewOptions(), new PreviewSettings(), 0f);
            Assert.Equal(42u, renderer.Pick(target, 32, 24)!.Value.PickId);
            world.UpdateInstance(instance, Matrix4x4.Identity, InstanceFlags.NotPickable);
            renderer.Render(target, camera, new ViewOptions(), new PreviewSettings(), 0f);
            Assert.Null(renderer.Pick(target, 32, 24));
            world.UpdateInstance(instance, Matrix4x4.Identity, InstanceFlags.Hidden);
            renderer.Render(target, camera, new ViewOptions(), new PreviewSettings(), 0f);
            Assert.Null(renderer.Pick(target, 32, 24));
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    private static Matrix4x4 ViewProjection(RenderCamera camera)
    {
        float aspect = Width / (float)Height;
        var projection = camera.Projection == ProjectionKind.Orthographic
            ? ReverseZ.Orthographic(camera.OrthoHeight, aspect, camera.Near, camera.Far)
            : ReverseZ.Perspective(camera.FovY, aspect, camera.Near);
        return camera.View * projection;
    }

    private static (int X, int Y) PixelOf(Matrix4x4 viewProjection, Vector3 p)
    {
        var clip = Vector4.Transform(new Vector4(p, 1f), viewProjection);
        return ((int)MathF.Floor((clip.X / clip.W * 0.5f + 0.5f) * Width), (int)MathF.Floor((clip.Y / clip.W * 0.5f + 0.5f) * Height));
    }

    private static (Vector3 Origin, Vector3 Direction) Ray(Matrix4x4 viewProjection, int x, int y)
    {
        Matrix4x4.Invert(viewProjection, out var inverse);
        float nx = (x + 0.5f) / Width * 2f - 1f, ny = (y + 0.5f) / Height * 2f - 1f;
        var a = Vector4.Transform(new Vector4(nx, ny, 1f, 1f), inverse);
        var b = Vector4.Transform(new Vector4(nx, ny, 0.5f, 1f), inverse);
        var pa = new Vector3(a.X, a.Y, a.Z) / a.W;
        var pb = new Vector3(b.X, b.Y, b.Z) / b.W;
        return (pa, Vector3.Normalize(pb - pa));
    }

    private static Vector3 IntersectSphere(Vector3 origin, Vector3 direction, Vector3 centre, float radius)
    {
        var oc = origin - centre;
        float b = Vector3.Dot(oc, direction);
        float c = oc.LengthSquared() - radius * radius;
        float t = -b - MathF.Sqrt(b * b - c);
        return origin + direction * t;
    }
}
