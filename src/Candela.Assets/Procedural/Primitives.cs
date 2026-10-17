using System.Numerics;
using Candela.Core.Geometry;

namespace Candela.Assets;

/// <summary>
/// Simple solids for helpers, gizmos and icons. All are Z-up, metres, counter-clockwise front faces, with smooth
/// normals where the surface is curved, tangents, and UVs in metres × <c>uvScale</c>. Box, cylinder, cone, sphere and
/// capsule are centred on the origin (their axis is Z); the arrow starts at the origin and points along +Z.
/// Segment counts are clamped to sensible ranges; non-positive or non-finite sizes become 1 mm.
/// </summary>
public static class Primitives
{
    /// <summary>Axis-aligned box of the given size centred on the origin (24 vertices, flat faces).</summary>
    public static MeshData Box(Vector3 size, float uvScale = 1f)
    {
        var s = new Vector3(Size(size.X), Size(size.Y), Size(size.Z));
        var b = new MeshBuilder(24, 36);
        b.AddBox(-s / 2, s / 2, Uv(uvScale));
        return b.ToMesh("Box");
    }

    /// <summary>Capped cylinder of <paramref name="radius"/> around Z from −height/2 to +height/2.</summary>
    public static MeshData Cylinder(float radius, float height, int segments = 24, float uvScale = 1f)
    {
        float r = Size(radius), h = Size(height);
        int n = Segments(segments);
        var b = new MeshBuilder(4 * n + 8, 12 * n);
        b.AddTube(new Vector3(0, 0, -h / 2), new Vector3(0, 0, h / 2), r, n, true, true, Uv(uvScale), Vector3.UnitX);
        return b.ToMesh("Cylinder");
    }

    /// <summary>Cone with its base (radius) at −height/2 and its apex at +height/2, base capped.</summary>
    public static MeshData Cone(float radius, float height, int segments = 24, float uvScale = 1f)
    {
        float r = Size(radius), h = Size(height);
        int n = Segments(segments);
        var b = new MeshBuilder(3 * n + 4, 6 * n);
        AddCone(b, new Vector3(0, 0, -h / 2), h, r, n, Uv(uvScale));
        b.AddDisc(new Vector3(0, 0, -h / 2), -Vector3.UnitZ, Vector3.UnitX, r, n, Uv(uvScale));
        return b.ToMesh("Cone");
    }

    /// <summary>UV sphere (<paramref name="segments"/> around Z, <paramref name="rings"/> from pole to pole).</summary>
    public static MeshData Sphere(float radius, int segments = 32, int rings = 16, float uvScale = 1f)
    {
        float r = Size(radius);
        int n = Segments(segments);
        int m = System.Math.Clamp(rings, 2, 128);
        var b = new MeshBuilder((n + 1) * (m + 1), n * m * 6);
        AddSphereBands(b, r, n, m, 0f, Uv(uvScale));
        return b.ToMesh("Sphere");
    }

    /// <summary>
    /// Capsule of total length <paramref name="height"/> (including both hemispherical ends, at least 2 × radius)
    /// around Z, centred on the origin.
    /// </summary>
    public static MeshData Capsule(float radius, float height, int segments = 24, int rings = 16, float uvScale = 1f)
    {
        float r = Size(radius);
        float h = System.Math.Max(Size(height), 2 * r);
        int n = Segments(segments);
        int m = System.Math.Clamp(rings + (rings & 1), 2, 128);
        var b = new MeshBuilder((n + 1) * (m + 2), n * (m + 1) * 6);
        AddSphereBands(b, r, n, m, h - 2 * r, Uv(uvScale));
        return b.ToMesh("Capsule");
    }

    /// <summary>
    /// Arrow from the origin to (0, 0, <paramref name="length"/>): a capped shaft and a cone head of
    /// <paramref name="headLength"/> and <paramref name="headRadius"/>. Bounds: ±headRadius in X/Y, 0 … length in Z.
    /// </summary>
    public static MeshData Arrow(float length, float shaftRadius, float headLength, float headRadius, int segments = 16)
    {
        float len = Size(length);
        float hl = System.Math.Clamp(Size(headLength), WallMesh.MinimumSize, len);
        float hr = Size(headRadius);
        float sr = System.Math.Min(Size(shaftRadius), hr);
        int n = Segments(segments);
        var b = new MeshBuilder(8 * n + 16, 24 * n);
        float shaftEnd = len - hl;
        if (shaftEnd > 1e-6f) b.AddTube(Vector3.Zero, new Vector3(0, 0, shaftEnd), sr, n, true, false, 1f, Vector3.UnitX);
        AddCone(b, new Vector3(0, 0, shaftEnd), hl, hr, n, 1f);
        b.AddDisc(new Vector3(0, 0, shaftEnd), -Vector3.UnitZ, Vector3.UnitX, hr, n, 1f);
        return b.ToMesh("Arrow");
    }

    /// <summary>Cone side from a base centre along +Z (apex vertices duplicated per segment for smooth shading).</summary>
    private static void AddCone(MeshBuilder b, Vector3 baseCenter, float height, float radius, int n, float uvScale)
    {
        float slant = MathF.Sqrt(height * height + radius * radius);
        float nz = radius / slant, nr = height / slant;
        var apex = baseCenter + new Vector3(0, 0, height);
        float circumference = 2 * MathF.PI * radius;
        int first = b.VertexCount;
        for (int i = 0; i <= n; i++)
        {
            float t = i / (float)n;
            float a = t * 2 * MathF.PI;
            var radial = new Vector3(MathF.Cos(a), MathF.Sin(a), 0);
            var normal = Vector3.Normalize(radial * nr + Vector3.UnitZ * nz);
            b.AddVertex(baseCenter + radial * radius, normal, new Vector2(t * circumference * uvScale, slant * uvScale));
            float tm = (i + 0.5f) / n;
            float am = tm * 2 * MathF.PI;
            var radialMid = new Vector3(MathF.Cos(am), MathF.Sin(am), 0);
            b.AddVertex(apex, Vector3.Normalize(radialMid * nr + Vector3.UnitZ * nz), new Vector2(tm * circumference * uvScale, 0));
        }
        for (int i = 0; i < n; i++)
        {
            int base0 = first + i * 2, tip = base0 + 1, base1 = base0 + 2;
            b.AddTriangle(base0, base1, tip);
        }
    }

    /// <summary>
    /// Latitude bands of a sphere of radius r; with <paramref name="cylinder"/> &gt; 0 the northern hemisphere is moved
    /// up and the southern down by half of it and the equator ring is doubled, forming a capsule.
    /// </summary>
    private static void AddSphereBands(MeshBuilder b, float r, int n, int m, float cylinder, float uvScale)
    {
        int first = b.VertexCount;
        var thetas = new List<(float Theta, float Offset)>(m + 2);
        for (int k = 0; k <= m; k++)
        {
            float theta = k * MathF.PI / m;
            if (cylinder > 0 && k == m / 2)
            {
                thetas.Add((theta, cylinder / 2));
                thetas.Add((theta, -cylinder / 2));
            }
            else thetas.Add((theta, cylinder > 0 ? (k < m / 2 ? cylinder / 2 : -cylinder / 2) : 0f));
        }
        float circumference = 2 * MathF.PI * r;
        float arc = 0f;
        for (int k = 0; k < thetas.Count; k++)
        {
            var (theta, offset) = thetas[k];
            if (k > 0)
            {
                var prev = thetas[k - 1];
                arc += (theta - prev.Theta) * r + MathF.Abs(prev.Offset - offset);
            }
            float st = MathF.Sin(theta), ct = MathF.Cos(theta);
            for (int s = 0; s <= n; s++)
            {
                float t = s / (float)n;
                float phi = t * 2 * MathF.PI;
                var nrm = new Vector3(st * MathF.Cos(phi), st * MathF.Sin(phi), ct);
                b.AddVertex(nrm * r + new Vector3(0, 0, offset), nrm, new Vector2(t * circumference * uvScale, arc * uvScale));
            }
        }
        int stride = n + 1;
        for (int k = 0; k + 1 < thetas.Count; k++)
        {
            bool northPole = k == 0;
            bool southPole = k + 1 == thetas.Count - 1;
            for (int s = 0; s < n; s++)
            {
                int v00 = first + k * stride + s, v01 = v00 + 1, v10 = v00 + stride, v11 = v10 + 1;
                // At the poles one triangle of each quad collapses to a line; skip it.
                if (!southPole) b.AddTriangle(v00, v10, v11);
                if (!northPole) b.AddTriangle(v00, v11, v01);
            }
        }
    }

    private static int Segments(int s) => System.Math.Clamp(s, 3, 256);

    private static float Size(float v) => float.IsFinite(v) && v > 0 ? System.Math.Min(v, WallMesh.MaximumSize) : WallMesh.MinimumSize;

    private static float Uv(float s) => float.IsFinite(s) && s > 0 ? s : 1f;
}
