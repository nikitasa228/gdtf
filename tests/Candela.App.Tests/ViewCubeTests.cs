using System.Numerics;
using Candela.App.Viewport;

namespace Candela.App.Tests;

public sealed class ViewCubeTests
{
    private static readonly Vector2 Center = new(500f, 300f);
    private const float CubeSize = 84f;

    private static ViewCubeFace[] Faces(ViewportCamera camera) =>
        ViewCube.Project(camera.Right, camera.Up, camera.Forward, Center, CubeSize);

    [Fact]
    public void Face_normals_are_the_six_axis_directions()
    {
        var normals = ViewCube.AllViews.Select(ViewCube.NormalOf).ToList();
        Assert.Equal(6, normals.Distinct().Count());
        foreach (var n in normals)
            Assert.Equal(1f, MathF.Abs(n.X) + MathF.Abs(n.Y) + MathF.Abs(n.Z));
        Assert.Equal(-Vector3.UnitY, ViewCube.NormalOf(ViewDirection.Front)); // the audience side
        Assert.Equal(Vector3.UnitZ, ViewCube.NormalOf(ViewDirection.Top));
        Assert.Equal(Vector3.UnitX, ViewCube.NormalOf(ViewDirection.Right));
    }

    [Theory]
    [InlineData(nameof(ViewDirection.Top))]
    [InlineData(nameof(ViewDirection.Bottom))]
    [InlineData(nameof(ViewDirection.Front))]
    [InlineData(nameof(ViewDirection.Back))]
    [InlineData(nameof(ViewDirection.Left))]
    [InlineData(nameof(ViewDirection.Right))]
    public void In_an_axis_view_only_that_face_is_visible_and_hit_in_the_centre(string name)
    {
        var view = Enum.Parse<ViewDirection>(name);
        var camera = new ViewportCamera();
        camera.SnapTo(view);
        var faces = Faces(camera);
        var visible = faces.Where(f => f.Facing > 0.02f).ToList();
        Assert.Single(visible);
        Assert.Equal(view, visible[0].View);
        Assert.Equal(1f, visible[0].Facing, 4);
        Assert.Equal(view, ViewCube.HitTest(faces, Center));
    }

    [Theory]
    [InlineData(0.6f, 0.5f)]
    [InlineData(-2.4f, 0.3f)]
    [InlineData(2.0f, -0.7f)]
    [InlineData(-0.8f, 1.2f)]
    public void Clicking_a_visible_face_snaps_to_it(float yaw, float pitch)
    {
        var camera = new ViewportCamera { Yaw = yaw, Pitch = pitch };
        var faces = Faces(camera);
        var visible = faces.Where(f => f.Facing > 0.2f).ToList();
        Assert.InRange(visible.Count, 2, 3); // an oblique view shows two or three faces
        foreach (var face in visible)
        {
            var hit = ViewCube.HitTest(faces, face.Center);
            Assert.Equal(face.View, hit);
            var snapped = new ViewportCamera { Yaw = yaw, Pitch = pitch };
            snapped.SnapTo(hit!.Value);
            // Looking at the clicked face from outside the cube.
            Assert.True(Vector3.Dot(snapped.Forward, -ViewCube.NormalOf(face.View)) > 0.9999f);
            Assert.Equal(face.View, snapped.SnappedView);
        }
    }

    [Fact]
    public void Faces_are_sorted_back_to_front()
    {
        var camera = new ViewportCamera { Yaw = 0.9f, Pitch = 0.4f };
        var faces = Faces(camera);
        for (int i = 1; i < faces.Length; i++)
            Assert.True(faces[i - 1].Depth >= faces[i].Depth);
        Assert.True(faces[^1].Facing > 0f, "the nearest face faces the viewer");
        Assert.True(faces[0].Facing < 0f, "the furthest face is a back face");
    }

    [Fact]
    public void Back_faces_and_points_outside_are_not_hit()
    {
        var camera = new ViewportCamera();
        camera.SnapTo(ViewDirection.Front);
        var faces = Faces(camera);
        Assert.Null(ViewCube.HitTest(faces, Center + new Vector2(CubeSize * 3f, 0f)));
        // Only the front face is visible from the front: its centre must never report the back face behind it.
        Assert.NotEqual(ViewDirection.Back, ViewCube.HitTest(faces, Center));
    }

    [Fact]
    public void The_projected_cube_stays_inside_its_square()
    {
        foreach (float yaw in new[] { 0f, 0.4f, 0.785f, 1.3f, 2.7f })
        {
            foreach (float pitch in new[] { -1.2f, -0.6f, 0f, 0.62f, 1.1f })
            {
                var camera = new ViewportCamera { Yaw = yaw, Pitch = pitch };
                foreach (var f in Faces(camera))
                {
                    foreach (var c in new[] { f.C0, f.C1, f.C2, f.C3 })
                    {
                        Assert.InRange(c.X, Center.X - CubeSize * 0.65f, Center.X + CubeSize * 0.65f);
                        Assert.InRange(c.Y, Center.Y - CubeSize * 0.65f, Center.Y + CubeSize * 0.65f);
                    }
                }
            }
        }
    }

    [Fact]
    public void Span_projection_matches_the_array_version_without_allocating()
    {
        var camera = new ViewportCamera { Yaw = 0.3f, Pitch = 0.2f };
        var expected = Faces(camera);
        Span<ViewCubeFace> faces = stackalloc ViewCubeFace[ViewCube.FaceCount];
        ViewCube.Project(camera.Right, camera.Up, camera.Forward, Center, CubeSize, faces); // warm-up
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
            ViewCube.Project(camera.Right, camera.Up, camera.Forward, Center, CubeSize, faces);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        Assert.True(faces.SequenceEqual(expected));
    }

    [Theory]
    // Arrows in screen order up, right, down, left: each leads to the face lying in that direction.
    [InlineData(nameof(ViewDirection.Top), nameof(ViewDirection.Back), nameof(ViewDirection.Right), nameof(ViewDirection.Front), nameof(ViewDirection.Left))]
    [InlineData(nameof(ViewDirection.Front), nameof(ViewDirection.Top), nameof(ViewDirection.Right), nameof(ViewDirection.Bottom), nameof(ViewDirection.Left))]
    [InlineData(nameof(ViewDirection.Right), nameof(ViewDirection.Top), nameof(ViewDirection.Back), nameof(ViewDirection.Bottom), nameof(ViewDirection.Front))]
    [InlineData(nameof(ViewDirection.Back), nameof(ViewDirection.Top), nameof(ViewDirection.Left), nameof(ViewDirection.Bottom), nameof(ViewDirection.Right))]
    [InlineData(nameof(ViewDirection.Bottom), nameof(ViewDirection.Front), nameof(ViewDirection.Right), nameof(ViewDirection.Back), nameof(ViewDirection.Left))]
    public void Arrows_of_an_axis_view_lead_to_the_neighbouring_faces(string from, string up, string right, string down, string left)
    {
        var camera = new ViewportCamera();
        camera.SnapTo(Enum.Parse<ViewDirection>(from));
        Assert.True(ViewCube.IsAxisAligned(camera.Forward));
        Span<ViewCubeArrow> arrows = stackalloc ViewCubeArrow[ViewCube.ArrowCount];
        ViewCube.Arrows(camera.Right, camera.Up, Center, 50f, 9f, arrows);
        var expected = new[] { up, right, down, left }.Select(Enum.Parse<ViewDirection>).ToArray();
        for (int i = 0; i < ViewCube.ArrowCount; i++)
            Assert.Equal(expected[i], arrows[i].View);

        // Every other axis view is one click away: the four arrows and the face itself cover five of six views.
        var reachable = new HashSet<ViewDirection>(expected) { camera.SnappedView!.Value };
        Assert.Equal(5, reachable.Count);
    }

    [Fact]
    public void Arrows_sit_outside_the_face_and_are_hit_only_near_their_centre()
    {
        var camera = new ViewportCamera();
        camera.SnapTo(ViewDirection.Top);
        var faces = Faces(camera);
        float radius = CubeSize * 0.6f;
        Span<ViewCubeArrow> arrows = stackalloc ViewCubeArrow[ViewCube.ArrowCount];
        ViewCube.Arrows(camera.Right, camera.Up, Center, radius, 9f, arrows);
        foreach (var arrow in arrows)
        {
            // Neither the tip nor the base corners cover the face (a click on the face must stay a face click).
            foreach (var p in new[] { arrow.Tip, arrow.Left, arrow.Right, arrow.Center })
                Assert.Null(ViewCube.HitTest(faces, p));
            Assert.True(Vector2.Distance(arrow.Tip, Center) <= radius + 0.01f, "the tip stays inside the cube's disc");
        }

        Assert.Equal(0, ViewCube.HitTestArrow(arrows, arrows[0].Center + new Vector2(3f, 2f), 11f));
        Assert.Equal(2, ViewCube.HitTestArrow(arrows, arrows[2].Center, 11f));
        Assert.Equal(-1, ViewCube.HitTestArrow(arrows, Center, 11f));
    }

    [Fact]
    public void Oblique_views_are_not_axis_aligned()
    {
        Assert.False(ViewCube.IsAxisAligned(new ViewportCamera { Yaw = 0.6f, Pitch = 0.5f }.Forward));
        Assert.False(ViewCube.IsAxisAligned(new ViewportCamera().Forward)); // the default view looks slightly down
        Assert.True(ViewCube.IsAxisAligned(-Vector3.UnitZ));
    }

    [Fact]
    public void Face_contains_handles_both_windings()
    {
        var cw = new ViewCubeFace(ViewDirection.Top, new(0, 0), new(10, 0), new(10, 10), new(0, 10), 0, 1);
        var ccw = new ViewCubeFace(ViewDirection.Top, new(0, 0), new(0, 10), new(10, 10), new(10, 0), 0, 1);
        Assert.True(cw.Contains(new Vector2(5, 5)));
        Assert.True(ccw.Contains(new Vector2(5, 5)));
        Assert.False(cw.Contains(new Vector2(11, 5)));
        Assert.False(ccw.Contains(new Vector2(-1, 5)));
        Assert.Equal(new Vector2(5, 5), cw.Center);
    }
}

public sealed class NavigationStyleTests
{
    [Theory]
    [InlineData("candela", NavigationStyles.Candela)]
    [InlineData(" BLENDER ", NavigationStyles.Blender)]
    [InlineData("Maya", NavigationStyles.Maya)]
    [InlineData("unknown", NavigationStyles.Candela)]
    [InlineData(null, NavigationStyles.Candela)]
    public void Normalize(string? input, string expected) => Assert.Equal(expected, NavigationStyles.Normalize(input));

    [Theory]
    // Candela: MMB orbit, Shift+MMB pan, Ctrl+MMB zoom, RMB fly, Alt+LMB orbit, Alt+Shift+LMB pan, LMB selects.
    [InlineData("Candela", (int)NavigationButton.Middle, (int)(NavigationModifiers.None), (int)NavigationOperation.Orbit)]
    [InlineData("Candela", (int)NavigationButton.Middle, (int)(NavigationModifiers.Shift), (int)NavigationOperation.Pan)]
    [InlineData("Candela", (int)NavigationButton.Middle, (int)(NavigationModifiers.Ctrl), (int)NavigationOperation.Zoom)]
    [InlineData("Candela", (int)NavigationButton.Right, (int)(NavigationModifiers.None), (int)NavigationOperation.Fly)]
    [InlineData("Candela", (int)NavigationButton.Left, (int)(NavigationModifiers.Alt), (int)NavigationOperation.Orbit)]
    [InlineData("Candela", (int)NavigationButton.Left, (int)(NavigationModifiers.Alt | NavigationModifiers.Shift), (int)NavigationOperation.Pan)]
    [InlineData("Candela", (int)NavigationButton.Left, (int)(NavigationModifiers.None), (int)NavigationOperation.None)]
    [InlineData("Candela", (int)NavigationButton.Left, (int)(NavigationModifiers.Shift), (int)NavigationOperation.None)]
    // Blender: MMB orbit, Shift+MMB pan, Ctrl+MMB zoom, Shift+RMB fly; RMB alone is free (context menu).
    [InlineData("Blender", (int)NavigationButton.Middle, (int)(NavigationModifiers.None), (int)NavigationOperation.Orbit)]
    [InlineData("Blender", (int)NavigationButton.Middle, (int)(NavigationModifiers.Shift), (int)NavigationOperation.Pan)]
    [InlineData("Blender", (int)NavigationButton.Middle, (int)(NavigationModifiers.Ctrl), (int)NavigationOperation.Zoom)]
    [InlineData("Blender", (int)NavigationButton.Right, (int)(NavigationModifiers.Shift), (int)NavigationOperation.Fly)]
    [InlineData("Blender", (int)NavigationButton.Right, (int)(NavigationModifiers.None), (int)NavigationOperation.None)]
    [InlineData("Blender", (int)NavigationButton.Left, (int)(NavigationModifiers.Alt), (int)NavigationOperation.None)]
    // Maya: Alt+LMB orbit, Alt+MMB pan, Alt+RMB zoom, RMB fly.
    [InlineData("Maya", (int)NavigationButton.Left, (int)(NavigationModifiers.Alt), (int)NavigationOperation.Orbit)]
    [InlineData("Maya", (int)NavigationButton.Middle, (int)(NavigationModifiers.Alt), (int)NavigationOperation.Pan)]
    [InlineData("Maya", (int)NavigationButton.Right, (int)(NavigationModifiers.Alt), (int)NavigationOperation.Zoom)]
    [InlineData("Maya", (int)NavigationButton.Right, (int)(NavigationModifiers.None), (int)NavigationOperation.Fly)]
    [InlineData("Maya", (int)NavigationButton.Left, (int)(NavigationModifiers.None), (int)NavigationOperation.None)]
    public void Resolve(string style, int button, int modifiers, int expected) =>
        Assert.Equal((NavigationOperation)expected, NavigationStyles.Resolve(style, (NavigationButton)button, (NavigationModifiers)modifiers));

    [Fact]
    public void Plain_left_button_always_selects()
    {
        foreach (var style in NavigationStyles.All)
            Assert.Equal(NavigationOperation.None, NavigationStyles.Resolve(style, NavigationButton.Left, NavigationModifiers.None));
    }
}
