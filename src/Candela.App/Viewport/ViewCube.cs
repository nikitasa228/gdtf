using System.Numerics;

namespace Candela.App.Viewport;

/// <summary>One face of the view cube projected to the screen.</summary>
/// <param name="View">The axis view a click on this face snaps to.</param>
/// <param name="C0">First corner in screen pixels.</param>
/// <param name="C1">Second corner.</param>
/// <param name="C2">Third corner.</param>
/// <param name="C3">Fourth corner (the corners go around the face).</param>
/// <param name="Depth">View-space depth of the face centre (smaller = nearer the viewer), for back-to-front drawing.</param>
/// <param name="Facing">Cosine between the face normal and the direction to the viewer (≤ 0 = back face).</param>
internal readonly record struct ViewCubeFace(ViewDirection View, Vector2 C0, Vector2 C1, Vector2 C2, Vector2 C3, float Depth, float Facing)
{
    /// <summary>Centre of the face on screen.</summary>
    public Vector2 Center => (C0 + C1 + C2 + C3) * 0.25f;

    /// <summary>True when the point lies inside the (convex) projected face.</summary>
    public bool Contains(Vector2 p)
    {
        // Same sign of the cross product along all four edges (either winding).
        float a = Cross(C1 - C0, p - C0), b = Cross(C2 - C1, p - C1), c = Cross(C3 - C2, p - C2), d = Cross(C0 - C3, p - C3);
        bool allPos = a >= 0 && b >= 0 && c >= 0 && d >= 0;
        bool allNeg = a <= 0 && b <= 0 && c <= 0 && d <= 0;
        return allPos || allNeg;
    }

    private static float Cross(Vector2 u, Vector2 v) => u.X * v.Y - u.Y * v.X;
}

/// <summary>
/// One of the four arrows around the view cube: it points from the cube's centre towards a screen edge and snaps to the
/// face lying in that direction (from the Front view the up arrow leads to Top, the right arrow to Right). The arrows
/// make every orthographic view reachable from the corner even when the camera looks straight at one face, where the
/// cube shows only that face.
/// </summary>
/// <param name="View">The axis view a click snaps to.</param>
/// <param name="Tip">Arrow tip in screen pixels (outer point).</param>
/// <param name="Left">First base corner.</param>
/// <param name="Right">Second base corner.</param>
/// <param name="Center">Centre of the arrow (hit testing).</param>
internal readonly record struct ViewCubeArrow(ViewDirection View, Vector2 Tip, Vector2 Left, Vector2 Right, Vector2 Center);

/// <summary>
/// The corner view cube of a viewport (requirement: "in the corner choose which view to fix — orthographic top / right /
/// front"): the unit cube turned like the scene, drawn orthographically, with one labelled face per axis view. Clicking a
/// visible face snaps the camera to that view. Face normals: Top +Z, Bottom −Z, Front −Y (the audience side), Back +Y,
/// Left −X, Right +X — clicking a face looks at it from outside, e.g. Front shows the stage from the audience.
/// Pure geometry (no ImGui), so it is unit-tested.
/// </summary>
internal static class ViewCube
{
    /// <summary>Outward normal of the face that snaps to <paramref name="view"/>.</summary>
    public static Vector3 NormalOf(ViewDirection view) => view switch
    {
        ViewDirection.Top => Vector3.UnitZ,
        ViewDirection.Bottom => -Vector3.UnitZ,
        ViewDirection.Front => -Vector3.UnitY,
        ViewDirection.Back => Vector3.UnitY,
        ViewDirection.Left => -Vector3.UnitX,
        ViewDirection.Right => Vector3.UnitX,
        _ => Vector3.UnitZ,
    };

    /// <summary>
    /// Projects the six faces for a camera with screen axes <paramref name="right"/>, <paramref name="up"/> and view
    /// direction <paramref name="forward"/> (unit vectors) into a square of side <paramref name="size"/> pixels centred at
    /// <paramref name="center"/>. Faces are returned back to front, so drawing them in order is correct; back faces
    /// (Facing ≤ 0) are included for completeness.
    /// </summary>
    public static ViewCubeFace[] Project(Vector3 right, Vector3 up, Vector3 forward, Vector2 center, float size)
    {
        var faces = new ViewCubeFace[FaceCount];
        Project(right, up, forward, center, size, faces);
        return faces;
    }

    /// <summary>Number of faces.</summary>
    public const int FaceCount = 6;

    /// <summary>The six axis views in declaration order.</summary>
    public static readonly ViewDirection[] AllViews = Enum.GetValues<ViewDirection>();

    /// <summary>Allocation-free <see cref="Project(Vector3, Vector3, Vector3, Vector2, float)"/> into <paramref name="faces"/> (length 6).</summary>
    public static void Project(Vector3 right, Vector3 up, Vector3 forward, Vector2 center, float size, Span<ViewCubeFace> faces)
    {
        if (faces.Length < FaceCount)
            throw new ArgumentException("Six faces are needed.", nameof(faces));
        float half = size * 0.5f / MathF.Sqrt(3f) * 1.25f; // the projected cube fits the square in any orientation
        int n = 0;
        foreach (var view in AllViews)
        {
            var normal = NormalOf(view);
            // In-plane axes of the face: pick a tangent not parallel to the normal.
            var t1 = MathF.Abs(normal.Z) > 0.5f ? Vector3.UnitX : Vector3.UnitZ;
            var t2 = Vector3.Cross(normal, t1);
            t1 = Vector3.Cross(t2, normal);
            var c = normal;
            Vector2 P(Vector3 w) => center + new Vector2(Vector3.Dot(w, right), -Vector3.Dot(w, up)) * half;
            var c0 = P(c - t1 - t2);
            var c1 = P(c + t1 - t2);
            var c2 = P(c + t1 + t2);
            var c3 = P(c - t1 + t2);
            float depth = Vector3.Dot(c, forward); // larger = further away
            float facing = -Vector3.Dot(normal, forward);
            faces[n++] = new ViewCubeFace(view, c0, c1, c2, c3, depth, facing);
        }

        faces[..FaceCount].Sort(static (a, b) => b.Depth.CompareTo(a.Depth));
    }

    /// <summary>Number of neighbour arrows (up, right, down, left).</summary>
    public const int ArrowCount = 4;

    /// <summary>
    /// The four neighbour arrows of a cube drawn at <paramref name="center"/> inside a disc of <paramref name="radius"/>
    /// pixels: each points up, right, down or left on screen (an isosceles triangle of <paramref name="arrowSize"/>
    /// pixels whose tip touches the disc's edge) and snaps to the face whose outward normal is closest to that screen
    /// direction in the world (screen right <paramref name="right"/>, screen up <paramref name="up"/>).
    /// </summary>
    public static void Arrows(Vector3 right, Vector3 up, Vector2 center, float radius, float arrowSize, Span<ViewCubeArrow> arrows)
    {
        if (arrows.Length < ArrowCount)
            throw new ArgumentException("Four arrows are needed.", nameof(arrows));
        // Screen directions (y down) and the matching world directions.
        ReadOnlySpan<Vector2> screen = stackalloc Vector2[] { new(0, -1), new(1, 0), new(0, 1), new(-1, 0) };
        for (int i = 0; i < ArrowCount; i++)
        {
            var d = screen[i];
            var world = right * d.X - up * d.Y;
            var tip = center + d * radius;
            var baseCentre = center + d * (radius - arrowSize);
            var side = new Vector2(-d.Y, d.X) * (arrowSize * 0.75f);
            arrows[i] = new ViewCubeArrow(NeighbourOf(world), tip, baseCentre + side, baseCentre - side, center + d * (radius - arrowSize * 0.5f));
        }
    }

    /// <summary>
    /// True when <paramref name="forward"/> (unit view direction) looks straight along a world axis, so the cube shows a
    /// single face and the neighbour arrows are needed to reach the other views.
    /// </summary>
    public static bool IsAxisAligned(Vector3 forward)
    {
        var a = Vector3.Abs(forward);
        return MathF.Max(a.X, MathF.Max(a.Y, a.Z)) > 0.9995f;
    }

    /// <summary>The axis view whose face normal points most along <paramref name="direction"/> (world space).</summary>
    public static ViewDirection NeighbourOf(Vector3 direction)
    {
        var best = ViewDirection.Top;
        float bestDot = float.NegativeInfinity;
        foreach (var view in AllViews)
        {
            float dot = Vector3.Dot(NormalOf(view), direction);
            if (dot > bestDot + 1e-5f)
            {
                bestDot = dot;
                best = view;
            }
        }

        return best;
    }

    /// <summary>The arrow within <paramref name="hitRadius"/> pixels of <paramref name="point"/>, or -1.</summary>
    public static int HitTestArrow(ReadOnlySpan<ViewCubeArrow> arrows, Vector2 point, float hitRadius)
    {
        int found = -1;
        float best = hitRadius * hitRadius;
        for (int i = 0; i < arrows.Length; i++)
        {
            float d = Vector2.DistanceSquared(arrows[i].Center, point);
            if (d <= best)
            {
                best = d;
                found = i;
            }
        }

        return found;
    }

    /// <summary>The view of the front-most visible face under <paramref name="point"/>, or null.</summary>
    public static ViewDirection? HitTest(ReadOnlySpan<ViewCubeFace> facesBackToFront, Vector2 point)
    {
        for (int i = facesBackToFront.Length - 1; i >= 0; i--)
        {
            var f = facesBackToFront[i];
            if (f.Facing > 0.02f && f.Contains(point))
                return f.View;
        }

        return null;
    }
}
