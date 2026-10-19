using System.Numerics;
using Candela.App.Viewport;
using Candela.Core.Math;
using Candela.Rendering;

namespace Candela.App.Tests;

public sealed class CameraTests
{
    private static readonly Vector2 Size = new(1200, 800);

    private static void Near(Vector3 expected, Vector3 actual, float tolerance = 1e-4f) =>
        Assert.True(Vector3.Distance(expected, actual) <= tolerance, $"expected {expected}, got {actual}");

    private static void Near(Vector2 expected, Vector2 actual, float tolerance) =>
        Assert.True(Vector2.Distance(expected, actual) <= tolerance, $"expected {expected}, got {actual}");

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(0.7f, 0.3f)]
    [InlineData(-2.5f, -0.9f)]
    [InlineData(1.2f, 1.5707964f)]
    [InlineData(3.1f, -1.5707964f)]
    public void Basis_is_orthonormal_and_right_handed(float yaw, float pitch)
    {
        var camera = new ViewportCamera { Yaw = yaw, Pitch = pitch };
        var f = camera.Forward;
        var r = camera.Right;
        var u = camera.Up;
        Assert.Equal(1f, f.Length(), 4);
        Assert.Equal(1f, r.Length(), 4);
        Assert.Equal(1f, u.Length(), 4);
        Assert.Equal(0f, Vector3.Dot(f, r), 4);
        Assert.Equal(0f, Vector3.Dot(f, u), 4);
        Assert.Equal(0f, Vector3.Dot(r, u), 4);
        // Screen right × screen up = towards the viewer (−forward).
        Near(-f, Vector3.Cross(r, u));
        // The world up axis never points down on screen.
        Assert.True(Vector3.Dot(u, Vector3.UnitZ) >= -1e-5f);
    }

    [Fact]
    public void Default_camera_looks_at_the_stage_from_the_audience()
    {
        var camera = new ViewportCamera();
        Assert.Equal(ProjectionKind.Perspective, camera.Projection);
        Assert.True(camera.Forward.Y > 0.9f, "looks into the stage (+Y)");
        Assert.True(camera.Forward.Z < 0f, "looks slightly down");
        Assert.True(camera.Eye.Y < 0f, "the eye is in the audience");
        Assert.True(camera.Eye.Z > 1f, "the eye is above the floor");
    }

    [Theory]
    [InlineData(nameof(ViewDirection.Top))]
    [InlineData(nameof(ViewDirection.Bottom))]
    [InlineData(nameof(ViewDirection.Front))]
    [InlineData(nameof(ViewDirection.Back))]
    [InlineData(nameof(ViewDirection.Left))]
    [InlineData(nameof(ViewDirection.Right))]
    public void SnapTo_looks_at_the_cube_face_from_outside_orthographically(string name)
    {
        var view = Enum.Parse<ViewDirection>(name);
        var camera = new ViewportCamera();
        camera.SnapTo(view);
        Assert.Equal(ProjectionKind.Orthographic, camera.Projection);
        Assert.Equal(view, camera.SnappedView);
        Near(-ViewCube.NormalOf(view), camera.Forward);
    }

    [Fact]
    public void Axis_views_have_the_conventional_screen_axes()
    {
        var camera = new ViewportCamera();
        camera.SnapTo(ViewDirection.Top);
        Near(Vector3.UnitX, camera.Right);
        Near(Vector3.UnitY, camera.Up); // plan view: up on screen is into the stage
        camera.SnapTo(ViewDirection.Front);
        Near(Vector3.UnitX, camera.Right);
        Near(Vector3.UnitZ, camera.Up);
        camera.SnapTo(ViewDirection.Right);
        Near(Vector3.UnitY, camera.Right); // from stage right the stage depth runs to the right
        Near(Vector3.UnitZ, camera.Up);
        camera.SnapTo(ViewDirection.Left);
        Near(-Vector3.UnitY, camera.Right);
        camera.SnapTo(ViewDirection.Back);
        Near(-Vector3.UnitX, camera.Right);
    }

    [Fact]
    public void Snapping_from_perspective_keeps_the_apparent_size()
    {
        var camera = new ViewportCamera { Distance = 12f };
        float before = camera.MetersPerPixel(Size.Y);
        camera.SnapTo(ViewDirection.Top);
        Assert.Equal(before, camera.MetersPerPixel(Size.Y), 4);
    }

    [Fact]
    public void Toggle_projection_round_trips_and_keeps_the_apparent_size()
    {
        var camera = new ViewportCamera { Distance = 7.5f };
        float mpp = camera.MetersPerPixel(Size.Y);
        camera.ToggleProjection();
        Assert.Equal(ProjectionKind.Orthographic, camera.Projection);
        Assert.Equal(mpp, camera.MetersPerPixel(Size.Y), 4);
        camera.ToggleProjection();
        Assert.Equal(ProjectionKind.Perspective, camera.Projection);
        Assert.Equal(7.5f, camera.Distance, 3);
        Assert.Null(camera.SnappedView);
    }

    [Fact]
    public void Orbit_rotates_around_the_target_and_leaves_the_snap()
    {
        var camera = new ViewportCamera();
        camera.SnapTo(ViewDirection.Front);
        camera.ToggleProjection();
        var target = camera.Target;
        float distance = Vector3.Distance(camera.Eye, target);
        camera.Orbit(150f, -40f);
        Assert.Null(camera.SnappedView);
        Assert.Equal(target, camera.Target);
        Assert.Equal(distance, Vector3.Distance(camera.Eye, target), 3);
        Assert.Equal(150f * 0.3f * MathF.PI / 180f, camera.Yaw, 4);
    }

    [Fact]
    public void Pitch_is_clamped_to_straight_down_and_up()
    {
        var camera = new ViewportCamera();
        camera.Orbit(0f, 100000f);
        Assert.Equal(ViewportCamera.MaxPitch, camera.Pitch, 5);
        Near(-Vector3.UnitZ, camera.Forward);
        camera.Orbit(0f, -200000f);
        Assert.Equal(-ViewportCamera.MaxPitch, camera.Pitch, 5);
    }

    [Fact]
    public void Non_finite_input_is_ignored()
    {
        var camera = new ViewportCamera();
        var target = camera.Target;
        camera.Yaw = float.NaN;
        camera.Pitch = float.PositiveInfinity;
        camera.Distance = float.NaN;
        camera.Target = new Vector3(float.NaN, 0, 0);
        camera.Zoom(float.NaN);
        camera.Fly(Vector3.UnitY, float.NaN);
        Assert.Equal(target, camera.Target);
        Assert.True(float.IsFinite(camera.Yaw) && float.IsFinite(camera.Pitch) && float.IsFinite(camera.Distance));
    }

    [Theory]
    [InlineData(ProjectionKind.Perspective)]
    [InlineData(ProjectionKind.Orthographic)]
    public void Pan_moves_the_scene_with_the_mouse(ProjectionKind projection)
    {
        var camera = new ViewportCamera { Yaw = 0.4f, Pitch = 0.5f, Projection = projection };
        var point = camera.Target;
        Assert.True(camera.WorldToScreen(point, Size, out var before));
        camera.Pan(37f, -21f, Size.Y);
        Assert.True(camera.WorldToScreen(point, Size, out var after));
        Near(before + new Vector2(37f, -21f), after, 0.05f);
    }

    [Theory]
    [InlineData(ProjectionKind.Perspective)]
    [InlineData(ProjectionKind.Orthographic)]
    public void Zoom_towards_a_point_keeps_it_under_the_cursor(ProjectionKind projection)
    {
        var camera = new ViewportCamera { Yaw = -0.3f, Pitch = 0.6f, Projection = projection };
        var cursor = new Vector2(830f, 260f);
        var ray = camera.RayThrough(cursor, Size);
        var point = ViewportCamera.IntersectGround(ray) ?? throw new InvalidOperationException("the ray misses the floor");
        float before = projection == ProjectionKind.Perspective ? camera.Distance : camera.OrthoHeight;
        camera.Zoom(3f, 1f, point);
        float after = projection == ProjectionKind.Perspective ? camera.Distance : camera.OrthoHeight;
        Assert.True(after < before, "zooming in shrinks the distance / visible height");
        Assert.Equal(before * MathF.Pow(0.88f, 3f), after, 3);
        Assert.True(camera.WorldToScreen(point, Size, out var pixel));
        Near(cursor, pixel, 0.1f);
    }

    [Fact]
    public void Zoom_is_clamped()
    {
        var camera = new ViewportCamera();
        for (int i = 0; i < 500; i++)
            camera.Zoom(10f);
        Assert.Equal(ViewportCamera.MinDistance, camera.Distance, 5);
        for (int i = 0; i < 500; i++)
            camera.Zoom(-10f);
        Assert.Equal(ViewportCamera.MaxDistance, camera.Distance, 1);
    }

    [Theory]
    [InlineData(ProjectionKind.Perspective)]
    [InlineData(ProjectionKind.Orthographic)]
    public void Ray_through_a_projected_point_hits_it(ProjectionKind projection)
    {
        var camera = new ViewportCamera { Yaw = 0.9f, Pitch = 0.35f, Projection = projection };
        var world = new Vector3(1.3f, 2.2f, 0.7f);
        Assert.True(camera.WorldToScreen(world, Size, out var pixel));
        var ray = camera.RayThrough(pixel, Size);
        // Distance from the point to the ray.
        var toPoint = world - ray.Origin;
        var closest = ray.Origin + ray.Direction * Vector3.Dot(toPoint, ray.Direction);
        Assert.True(Vector3.Distance(world, closest) < 2e-3f, $"miss by {Vector3.Distance(world, closest)} m");
    }

    [Fact]
    public void Target_projects_to_the_viewport_centre()
    {
        var camera = new ViewportCamera { Yaw = 2f, Pitch = -0.2f };
        Assert.True(camera.WorldToScreen(camera.Target, Size, out var pixel));
        Near(Size * 0.5f, pixel, 0.01f);
    }

    [Fact]
    public void Points_behind_the_camera_do_not_project()
    {
        var camera = new ViewportCamera();
        Assert.False(camera.WorldToScreen(camera.Eye - camera.Forward * 3f, Size, out _));
    }

    [Theory]
    [InlineData(ProjectionKind.Perspective, 1.5f)]
    [InlineData(ProjectionKind.Perspective, 0.6f)]
    [InlineData(ProjectionKind.Orthographic, 1.5f)]
    [InlineData(ProjectionKind.Orthographic, 0.6f)]
    public void Frame_fits_the_box_into_the_view(ProjectionKind projection, float aspect)
    {
        var camera = new ViewportCamera { Yaw = 0.5f, Pitch = 0.4f, Projection = projection };
        var box = new Aabb(new Vector3(4f, 9f, 0f), new Vector3(10f, 12f, 3f));
        var size = new Vector2(900f * aspect, 900f);
        camera.Frame(box, aspect);
        Near(box.Center, camera.Target);
        for (int i = 0; i < 8; i++)
        {
            var corner = new Vector3((i & 1) != 0 ? box.Max.X : box.Min.X, (i & 2) != 0 ? box.Max.Y : box.Min.Y, (i & 4) != 0 ? box.Max.Z : box.Min.Z);
            Assert.True(camera.WorldToScreen(corner, size, out var p));
            Assert.InRange(p.X, 0f, size.X);
            Assert.InRange(p.Y, 0f, size.Y);
        }
    }

    [Fact]
    public void Frame_ignores_empty_boxes()
    {
        var camera = new ViewportCamera();
        var target = camera.Target;
        camera.Frame(Aabb.Empty, 1.5f);
        Assert.Equal(target, camera.Target);
    }

    [Fact]
    public void Look_keeps_the_eye_and_turns_the_view()
    {
        var camera = new ViewportCamera { Yaw = 0.2f, Pitch = 0.1f };
        var eye = camera.Eye;
        camera.Look(80f, 30f);
        Near(eye, camera.Eye, 1e-3f);
        Assert.NotEqual(0.2f, camera.Yaw);
    }

    [Fact]
    public void Fly_moves_eye_and_target_together()
    {
        var camera = new ViewportCamera { Yaw = 0f, Pitch = 0f };
        var forward = camera.Forward;
        var target = camera.Target;
        float d = camera.Distance;
        camera.Fly(new Vector3(0f, 1f, 0f), 0.5f);
        var moved = camera.Target - target;
        Assert.True(Vector3.Dot(Vector3.Normalize(moved), forward) > 0.999f);
        Assert.Equal(d, camera.Distance);
    }

    [Fact]
    public void Ground_intersection()
    {
        var hit = ViewportCamera.IntersectGround(new Ray(new Vector3(1f, 2f, 5f), Vector3.Normalize(new Vector3(0f, 1f, -1f))));
        Assert.NotNull(hit);
        Near(new Vector3(1f, 7f, 0f), hit!.Value);
        Assert.Null(ViewportCamera.IntersectGround(new Ray(new Vector3(0f, 0f, 5f), Vector3.UnitZ)));
        Assert.Null(ViewportCamera.IntersectGround(new Ray(new Vector3(0f, 0f, 5f), Vector3.UnitX)));
    }

    [Fact]
    public void Render_camera_receives_the_view()
    {
        var camera = new ViewportCamera { Projection = ProjectionKind.Orthographic, OrthoHeight = 14f, Ev100 = 9.5f };
        var render = camera.ToRenderCamera();
        Assert.Equal(ProjectionKind.Orthographic, render.Projection);
        Assert.Equal(14f, render.OrthoHeight);
        Assert.Equal(9.5f, render.Ev100);
        Assert.Equal(camera.ViewMatrix, render.View);
        Assert.True(render.Near > 0f && render.Far > render.Near);
    }

    [Fact]
    public void CopyFrom_copies_everything()
    {
        var a = new ViewportCamera { Yaw = 1f, Pitch = 0.2f, Distance = 33f, Target = new Vector3(1, 2, 3) };
        a.SnapTo(ViewDirection.Left);
        var b = new ViewportCamera();
        b.CopyFrom(a);
        Assert.Equal(a.ViewMatrix, b.ViewMatrix);
        Assert.Equal(a.Projection, b.Projection);
        Assert.Equal(a.SnappedView, b.SnappedView);
        Assert.Equal(a.OrthoHeight, b.OrthoHeight);
    }
}
