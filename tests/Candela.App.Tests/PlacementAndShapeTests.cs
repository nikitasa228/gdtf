using System.Numerics;
using Candela.App.Editing;
using Candela.Core.Math;
using Candela.Core.SceneGraph.Components;

namespace Candela.App.Tests;

/// <summary>Where dropped items land and how walls and floors are drawn and resized (pure geometry).</summary>
public sealed class PlacementTests
{
    private static void Near(Vector3 expected, Vector3 actual, float tolerance = 1e-4f) =>
        Assert.True(Vector3.Distance(expected, actual) < tolerance, $"expected {expected}, got {actual}");

    [Fact]
    public void A_fixture_stands_on_a_floor_hangs_under_a_ceiling_and_points_away_from_a_wall()
    {
        // GDTF fixtures hang: the base is towards local +Z, the beam leaves along local −Z.
        var floor = Placement.ForFixture(new SurfaceHit(new Vector3(1, 2, 0), Vector3.UnitZ, SurfaceKind.Geometry));
        Near(new Vector3(1, 2, 0), floor.Position);
        Near(-Vector3.UnitZ, Vector3.Transform(Vector3.UnitZ, floor.Rotation)); // base down
        Near(Vector3.UnitZ, Vector3.Transform(-Vector3.UnitZ, floor.Rotation)); // beam up
        // Turned over about X, as the library thumbnails show a fixture standing.
        Near(Vector3.UnitX, Vector3.Transform(Vector3.UnitX, floor.Rotation));

        var ceiling = Placement.ForFixture(new SurfaceHit(new Vector3(0, 0, 6), -Vector3.UnitZ, SurfaceKind.Geometry));
        Near(Vector3.UnitZ, Vector3.Transform(Vector3.UnitZ, ceiling.Rotation));

        var wall = Placement.ForFixture(new SurfaceHit(new Vector3(0, 5, 2), -Vector3.UnitY, SurfaceKind.Geometry));
        Near(-Vector3.UnitY, Vector3.Transform(-Vector3.UnitZ, wall.Rotation)); // beam into the room
    }

    [Fact]
    public void Images_lie_on_the_surface_facing_out_of_it()
    {
        var onFloor = Placement.ForImage(SurfaceHit.Ground(new Vector3(3, 4, 0)));
        Near(new Vector3(3, 4, Placement.ImageLift), onFloor.Position);
        Near(Vector3.UnitZ, Vector3.Transform(-Vector3.UnitY, onFloor.Rotation)); // the picture's face looks up
        var onWall = Placement.ForImage(new SurfaceHit(new Vector3(0, 5, 2), -Vector3.UnitY, SurfaceKind.Geometry));
        Near(-Vector3.UnitY, Vector3.Transform(-Vector3.UnitY, onWall.Rotation));
        var model = Placement.ForModel(new SurfaceHit(new Vector3(0, 5, 2), -Vector3.UnitY, SurfaceKind.Geometry));
        Assert.Equal(Quaternion.Identity, model.Rotation);
    }

    [Fact]
    public void FromTo_turns_any_direction_into_any_other()
    {
        var rng = new Random(7);
        Vector3 Random() => Vector3.Normalize(new Vector3(rng.NextSingle() - 0.5f, rng.NextSingle() - 0.5f, rng.NextSingle() - 0.5f));
        for (int i = 0; i < 200; i++)
        {
            var a = Random();
            var b = Random();
            Near(b, Vector3.Transform(a, Placement.FromTo(a, b)), 1e-3f);
            Near(-a, Vector3.Transform(a, Placement.FromTo(a, -a)), 1e-3f);
        }

        Assert.Equal(Quaternion.Identity, Placement.FromTo(Vector3.UnitZ, Vector3.UnitZ));
        // Degenerate input never gives NaN.
        var q = Placement.FromTo(Vector3.Zero, new Vector3(float.NaN, 0, 0));
        Assert.True(float.IsFinite(q.X) && float.IsFinite(q.W));
    }

    [Fact]
    public void The_ground_under_a_ray()
    {
        Near(new Vector3(0, 10, 0), Placement.GroundUnder(new Vector3(0, 0, 10), Vector3.Normalize(new Vector3(0, 1, -1)))!.Value, 1e-3f);
        Assert.Null(Placement.GroundUnder(new Vector3(0, 0, 10), Vector3.UnitY)); // parallel
        Assert.Null(Placement.GroundUnder(new Vector3(0, 0, 10), Vector3.UnitZ)); // away from the ground
        Assert.Null(Placement.GroundUnder(new Vector3(float.NaN, 0, 1), -Vector3.UnitZ));
    }
}

public sealed class WallShapeTests
{
    private static void Near(Vector3 expected, Vector3 actual, float tolerance = 1e-4f) =>
        Assert.True(Vector3.Distance(expected, actual) < tolerance, $"expected {expected}, got {actual}");

    [Fact]
    public void A_dragged_rectangle_becomes_a_centred_floor()
    {
        var floor = WallShapes.FloorFromCorners(new Vector3(4, 1, 0.5f), new Vector3(-2, 3, 0.5f))!.Value;
        Near(new Vector3(1, 2, 0.5f), floor.Placement.Position);
        Assert.Equal(6f, floor.Width, 4);
        Assert.Equal(2f, floor.Length, 4);
        Assert.Null(WallShapes.FloorFromCorners(Vector3.Zero, new Vector3(0.01f, 5, 0)));
        Assert.Null(WallShapes.FloorFromCorners(Vector3.Zero, new Vector3(float.NaN, 5, 0)));
    }

    [Fact]
    public void A_dragged_line_becomes_a_wall_facing_the_viewer()
    {
        var eye = new Vector3(0, -10, 2);
        var wall = WallShapes.WallFromLine(new Vector3(-3, 0, 0), new Vector3(3, 0, 0), eye)!.Value;
        Assert.Equal(6f, wall.Width, 4);
        Near(Vector3.Zero, wall.Placement.Position);
        var front = Vector3.Transform(-Vector3.UnitY, wall.Placement.Rotation);
        Assert.True(Vector3.Dot(front, eye) > 0, "the front face looks towards the eye");
        // Drawn the other way round: still facing the viewer, and still spanning the line.
        var reversed = WallShapes.WallFromLine(new Vector3(3, 0, 0), new Vector3(-3, 0, 0), eye)!.Value;
        Assert.True(Vector3.Dot(Vector3.Transform(-Vector3.UnitY, reversed.Placement.Rotation), eye) > 0);
        var end = Vector3.Transform(new Vector3(reversed.Width / 2, 0, 0), reversed.Placement.ToMatrix());
        Assert.True(MathF.Abs(MathF.Abs(end.X) - 3f) < 1e-3f && MathF.Abs(end.Y) < 1e-3f);
        Assert.Null(WallShapes.WallFromLine(Vector3.Zero, new Vector3(0, 0.01f, 0), eye));
    }

    [Theory]
    [InlineData(1, 0, 0)]
    [InlineData(-1, 0, 0)]
    [InlineData(0, 1, 0)]
    [InlineData(0, -1, 0)]
    [InlineData(1, 1, 0)]
    [InlineData(-1, 1, 0)]
    public void Resizing_a_floor_keeps_the_opposite_side_in_place(int hx, int hy, int hz)
    {
        var floor = new WallComponent { Kind = WallKind.Floor, Width = 4, Height = 2 };
        var local = new Transform(new Vector3(10, 5, 1), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.7f), Vector3.One);
        var handle = new ShapeHandle(hx, hy, hz);
        var opposite = WallShapes.HandlePosition(floor, new ShapeHandle(-hx, -hy, hz));
        var oppositeWorld = Vector3.Transform(opposite, local.ToMatrix());
        var target = WallShapes.HandlePosition(floor, handle) + new Vector3(hx * 1.5f, hy * 0.5f, 0f);
        var (width, height, shift) = WallShapes.Resize(floor, handle, target);
        Assert.Equal(hx != 0 ? 5.5f : 4f, width, 4);
        Assert.Equal(hy != 0 ? 2.5f : 2f, height, 4);
        var resized = new WallComponent { Kind = WallKind.Floor, Width = width, Height = height };
        var moved = WallShapes.ShiftOrigin(local, shift);
        Near(oppositeWorld, Vector3.Transform(WallShapes.HandlePosition(resized, new ShapeHandle(-hx, -hy, hz)), moved.ToMatrix()), 1e-3f);
    }

    [Fact]
    public void Resizing_a_wall_changes_its_width_from_one_end_and_its_height_from_the_top()
    {
        var wall = new WallComponent { Kind = WallKind.Wall, Width = 6, Height = 3 };
        var (w, h, shift) = WallShapes.Resize(wall, new ShapeHandle(1, 0, 0), new Vector3(5, 0, 1.5f));
        Assert.Equal(8f, w, 4); // left end at −3 stays, right end to +5
        Assert.Equal(3f, h, 4);
        Near(new Vector3(1, 0, 0), shift);
        (w, h, shift) = WallShapes.Resize(wall, new ShapeHandle(0, 0, 1), new Vector3(0, 0, 4.2f));
        Assert.Equal(6f, w, 4);
        Assert.Equal(4.2f, h, 4);
        Assert.Equal(Vector3.Zero, shift);
        // Dragged through the opposite side: the minimum size, never negative.
        (w, _, _) = WallShapes.Resize(wall, new ShapeHandle(1, 0, 0), new Vector3(-10, 0, 0));
        Assert.Equal(WallShapes.MinimumSize, w, 4);
        // Snapping rounds the size.
        (w, _, _) = WallShapes.Resize(wall, new ShapeHandle(1, 0, 0), new Vector3(3.37f, 0, 0), snap: 0.5f);
        Assert.Equal(6.5f, w, 4);
        // Non-finite targets change nothing.
        (w, h, shift) = WallShapes.Resize(wall, new ShapeHandle(1, 0, 0), new Vector3(float.NaN, 0, 0));
        Assert.Equal((6f, 3f, Vector3.Zero), (w, h, shift));
    }

    [Fact]
    public void Handles_and_default_materials()
    {
        Assert.Equal(3, WallShapes.HandlesOf(WallKind.Wall).Count);
        Assert.Equal(8, WallShapes.HandlesOf(WallKind.Floor).Count);
        Assert.Equal(MaterialIds.WoodStage, WallShapes.DefaultMaterial(WallKind.Floor));
        Assert.Equal(MaterialIds.PaintedWallWhite, WallShapes.DefaultMaterial(WallKind.Wall));
        var wall = new WallComponent { Kind = WallKind.Wall, Width = 4, Height = 3 };
        Near(new Vector3(2, 0, 1.5f), WallShapes.HandlePosition(wall, new ShapeHandle(1, 0, 0)));
        Near(new Vector3(0, 0, 3), WallShapes.HandlePosition(wall, new ShapeHandle(0, 0, 1)));
        Assert.Equal(Vector3.UnitZ, WallShapes.HandleAxis(new ShapeHandle(0, 0, 1)));
        Assert.Equal(Vector3.Zero, WallShapes.HandleAxis(new ShapeHandle(1, 1, 0)));
    }

    [Fact]
    public void Closest_point_on_an_axis_and_plane_crossings()
    {
        // A ray from above looking down at x = 3 meets the X axis line at x = 3.
        var p = WallShapes.ClosestOnAxis(Vector3.Zero, Vector3.UnitX, new Ray(new Vector3(3, -5, 5), Vector3.Normalize(new Vector3(0, 1, -1))))!.Value;
        Near(new Vector3(3, 0, 0), p, 1e-3f);
        Assert.Null(WallShapes.ClosestOnAxis(Vector3.Zero, Vector3.UnitX, new Ray(new Vector3(0, 1, 0), Vector3.UnitX)));
        Near(new Vector3(2, 2, 1), WallShapes.OnPlaneZ(new Ray(new Vector3(2, 2, 5), -Vector3.UnitZ), 1f)!.Value);
        Assert.Null(WallShapes.OnPlaneZ(new Ray(new Vector3(2, 2, 5), Vector3.UnitZ), 1f));
        Assert.Equal(new Vector3(1.5f, -0.5f, 7f), WallShapes.SnapPoint(new Vector3(1.4f, -0.6f, 7f), 0.5f));
    }
}
