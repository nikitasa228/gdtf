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
