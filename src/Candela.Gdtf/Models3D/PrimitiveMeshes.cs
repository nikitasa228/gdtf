using System.Numerics;
using Candela.Core.Geometry;

namespace Candela.Gdtf;

/// <summary>
/// Procedural meshes for GDTF built-in primitive types, sized to the model's declared Length (X) × Width (Y) ×
/// Height (Z) in metres and centred on the geometry origin. Shapes follow the hanging orientation GDTF uses: yokes open
/// downwards (−Z), heads and conventional barrels point their lens down, scanner mirrors sit at the bottom.
/// </summary>
/// <remarks>
/// The bounding box of every primitive is exactly the declared size. Box faces have flat normals, round surfaces smooth
/// normals; front faces are counter-clockwise seen from outside.
/// </remarks>
public static class PrimitiveMeshes
{
    /// <summary>Smallest extent used for an axis declared as zero (1 mm), so flat primitives stay renderable.</summary>
    public const float MinimumExtent = 0.001f;

    private const int Segments = 24;

    /// <summary>
    /// Creates the mesh of a primitive type, or null when there is nothing to draw (all extents zero). Undefined gives a
    /// plain box (used as a placeholder for a missing model file).
    /// </summary>
    public static ModelData? Create(GdtfPrimitiveType type, Vector3 size, string name = "")
    {
        if (!(size.X > 0 || size.Y > 0 || size.Z > 0) || !float.IsFinite(size.X + size.Y + size.Z)) return null;
        size = Vector3.Max(size, new Vector3(MinimumExtent));

        var b = new MeshBuilder();
        switch (type)
        {
            case GdtfPrimitiveType.Cylinder:
            case GdtfPrimitiveType.Pigtail:
            case GdtfPrimitiveType.Head:
                b.Cylinder(Vector2.Zero, 0.5f, 0.5f, -0.5f, 0.5f);
                break;
            case GdtfPrimitiveType.Sphere:
                b.Sphere(new Vector3(0.5f));
                break;
            case GdtfPrimitiveType.Base:
            case GdtfPrimitiveType.Base1_1:
                b.Box(new Vector3(-0.5f, -0.5f, -0.5f), new Vector3(0.5f, 0.5f, 0.3f));
                b.Box(new Vector3(-0.35f, -0.35f, 0.3f), new Vector3(0.35f, 0.35f, 0.5f));
                break;
            case GdtfPrimitiveType.Yoke:
                b.Box(new Vector3(-0.5f, -0.5f, 0.35f), new Vector3(0.5f, 0.5f, 0.5f));
                b.Box(new Vector3(-0.5f, -0.5f, -0.5f), new Vector3(-0.38f, 0.5f, 0.35f));
                b.Box(new Vector3(0.38f, -0.5f, -0.5f), new Vector3(0.5f, 0.5f, 0.35f));
                break;
            case GdtfPrimitiveType.Scanner:
            case GdtfPrimitiveType.Scanner1_1:
                b.Box(new Vector3(-0.5f, -0.5f, -0.1f), new Vector3(0.5f, 0.5f, 0.5f));
                b.MirrorWedge(-0.4f, 0.4f, -0.5f, 0.5f, -0.5f, -0.1f);
                break;
            case GdtfPrimitiveType.Conventional:
            case GdtfPrimitiveType.Conventional1_1:
                b.Cylinder(Vector2.Zero, 0.4f, 0.4f, -0.2f, 0.5f);
                b.Cylinder(Vector2.Zero, 0.5f, 0.5f, -0.5f, -0.2f);
                break;
            default: // Cube, Undefined placeholder
                b.Box(new Vector3(-0.5f), new Vector3(0.5f));
                break;
        }

        var mesh = b.ToMesh(name, size);
        return new ModelData
        {
            Name = name,
            Parts = { new ModelPart { Name = name, Mesh = mesh } },
            Materials = { new MaterialData { Name = "gdtf-primitive", BaseColorFactor = new Vector4(0.06f, 0.06f, 0.06f, 1f), Roughness = 0.55f } },
        };
    }

    /// <summary>An axis-aligned box mesh with flat normals.</summary>
    public static MeshData Box(Vector3 min, Vector3 max, string name = "box")
    {
        var b = new MeshBuilder();
        b.Box(min, max);
        return b.ToMesh(name, Vector3.One);
    }

    /// <summary>A capped cylinder along Z with an elliptical cross-section.</summary>
    public static MeshData Cylinder(float radiusX, float radiusY, float zMin, float zMax, string name = "cylinder")
    {
        var b = new MeshBuilder();
        b.Cylinder(Vector2.Zero, radiusX, radiusY, zMin, zMax);
        return b.ToMesh(name, Vector3.One);
    }

    /// <summary>An ellipsoid centred on the origin.</summary>
    public static MeshData Sphere(Vector3 radii, string name = "sphere")
    {
        var b = new MeshBuilder();
        b.Sphere(radii);
        return b.ToMesh(name, Vector3.One);
    }

    /// <summary>Accumulates triangles; orientation of every face is checked against an outward direction.</summary>
    private sealed class MeshBuilder
    {
        private readonly List<Vector3> _positions = new();
        private readonly List<Vector3> _normals = new();
        private readonly List<Vector2> _uvs = new();
        private readonly List<uint> _indices = new();

        public void Box(Vector3 min, Vector3 max)
        {
            Span<Vector3> p = stackalloc Vector3[8];
            for (int i = 0; i < 8; i++)
                p[i] = new Vector3((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z);
            Quad(p[0], p[2], p[6], p[4], -Vector3.UnitX);
            Quad(p[1], p[5], p[7], p[3], Vector3.UnitX);
            Quad(p[0], p[4], p[5], p[1], -Vector3.UnitY);
            Quad(p[2], p[3], p[7], p[6], Vector3.UnitY);
            Quad(p[0], p[1], p[3], p[2], -Vector3.UnitZ);
            Quad(p[4], p[6], p[7], p[5], Vector3.UnitZ);
        }

        public void Cylinder(Vector2 centre, float rx, float ry, float z0, float z1)
        {
            uint baseIndex = (uint)_positions.Count;
            for (int i = 0; i <= Segments; i++)
            {
                float a = 2 * MathF.PI * i / Segments;
                float cx = MathF.Cos(a), sy = MathF.Sin(a);
                var n = Vector3.Normalize(new Vector3(cx / rx, sy / ry, 0));
                var pxy = new Vector2(centre.X + rx * cx, centre.Y + ry * sy);
                float u = (float)i / Segments;
                Add(new Vector3(pxy, z0), n, new Vector2(u, 1));
                Add(new Vector3(pxy, z1), n, new Vector2(u, 0));
            }
            for (uint i = 0; i < Segments; i++)
            {
                uint a0 = baseIndex + 2 * i, a1 = a0 + 1, b0 = a0 + 2, b1 = a0 + 3;
                Tri(a0, b0, b1, Outward(a0, b0, b1, centre));
                Tri(a0, b1, a1, Outward(a0, b1, a1, centre));
            }
            Cap(centre, rx, ry, z0, -Vector3.UnitZ);
            Cap(centre, rx, ry, z1, Vector3.UnitZ);
        }

        public void Sphere(Vector3 r)
        {
            const int rings = Segments / 2;
            uint baseIndex = (uint)_positions.Count;
            for (int j = 0; j <= rings; j++)
            {
                float theta = MathF.PI * j / rings;
                float st = MathF.Sin(theta), ct = MathF.Cos(theta);
                for (int i = 0; i <= Segments; i++)
                {
                    float phi = 2 * MathF.PI * i / Segments;
                    var unit = new Vector3(st * MathF.Cos(phi), st * MathF.Sin(phi), ct);
                    var p = unit * r;
                    var n = Vector3.Normalize(new Vector3(unit.X / r.X, unit.Y / r.Y, unit.Z / r.Z));
                    Add(p, n, new Vector2((float)i / Segments, (float)j / rings));
                }
            }
            uint stride = Segments + 1;
            for (uint j = 0; j < rings; j++)
                for (uint i = 0; i < Segments; i++)
                {
                    uint a = baseIndex + j * stride + i, b = a + stride, c = b + 1, d = a + 1;
                    if (j > 0) Tri(a, b, d, _positions[(int)a] + _positions[(int)b] + _positions[(int)d]);
                    if (j < rings - 1) Tri(d, b, c, _positions[(int)d] + _positions[(int)b] + _positions[(int)c]);
                }
        }

        /// <summary>Triangular prism along X: top at z1 (y0…y1), mirror slope down to z0 at y1, back wall at y1.</summary>
        public void MirrorWedge(float x0, float x1, float y0, float y1, float z0, float z1)
        {
            var a0 = new Vector3(x0, y0, z1); var b0 = new Vector3(x0, y1, z1); var c0 = new Vector3(x0, y1, z0);
            var a1 = new Vector3(x1, y0, z1); var b1 = new Vector3(x1, y1, z1); var c1 = new Vector3(x1, y1, z0);
            var slope = Vector3.Normalize(new Vector3(0, -(z1 - z0), -(y1 - y0)));
            Quad(a0, a1, b1, b0, Vector3.UnitZ);
            Quad(b0, b1, c1, c0, Vector3.UnitY);
            Quad(a0, c0, c1, a1, slope);
            Triangle(a0, b0, c0, -Vector3.UnitX);
            Triangle(a1, c1, b1, Vector3.UnitX);
        }

        private void Cap(Vector2 centre, float rx, float ry, float z, Vector3 n)
        {
            uint c = Add(new Vector3(centre, z), n, new Vector2(0.5f, 0.5f));
            uint first = (uint)_positions.Count;
            for (int i = 0; i <= Segments; i++)
            {
                float a = 2 * MathF.PI * i / Segments;
                Add(new Vector3(centre.X + rx * MathF.Cos(a), centre.Y + ry * MathF.Sin(a), z), n,
                    new Vector2(0.5f + 0.5f * MathF.Cos(a), 0.5f + 0.5f * MathF.Sin(a)));
            }
            for (uint i = 0; i < Segments; i++) Tri(c, first + i, first + i + 1, n);
        }

        private Vector3 Outward(uint a, uint b, uint c, Vector2 centre)
        {
            var m = (_positions[(int)a] + _positions[(int)b] + _positions[(int)c]) / 3f;
            return new Vector3(m.X - centre.X, m.Y - centre.Y, 0);
        }

        private void Quad(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, Vector3 n)
        {
            uint i0 = Add(p0, n, new Vector2(0, 1)), i1 = Add(p1, n, new Vector2(1, 1));
            uint i2 = Add(p2, n, new Vector2(1, 0)), i3 = Add(p3, n, new Vector2(0, 0));
            Tri(i0, i1, i2, n);
            Tri(i0, i2, i3, n);
        }

        private void Triangle(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 n)
        {
            uint i0 = Add(p0, n, Vector2.Zero), i1 = Add(p1, n, Vector2.UnitX), i2 = Add(p2, n, Vector2.UnitY);
            Tri(i0, i1, i2, n);
        }

        private uint Add(Vector3 p, Vector3 n, Vector2 uv)
        {
            _positions.Add(p);
            _normals.Add(n);
            _uvs.Add(uv);
            return (uint)(_positions.Count - 1);
        }

        /// <summary>Adds a triangle, flipping it when its winding disagrees with <paramref name="outward"/>.</summary>
        private void Tri(uint a, uint b, uint c, Vector3 outward)
        {
            var pa = _positions[(int)a];
            var face = Vector3.Cross(_positions[(int)b] - pa, _positions[(int)c] - pa);
            if (face.LengthSquared() < 1e-24f) return;
            _indices.Add(a);
            if (Vector3.Dot(face, outward) >= 0)
            {
                _indices.Add(b);
                _indices.Add(c);
            }
            else
            {
                _indices.Add(c);
                _indices.Add(b);
            }
        }

        public MeshData ToMesh(string name, Vector3 scale)
        {
            var positions = new Vector3[_positions.Count];
            var normals = new Vector3[_normals.Count];
            for (int i = 0; i < positions.Length; i++)
            {
                positions[i] = _positions[i] * scale;
                var n = _normals[i] / scale;
                normals[i] = n.LengthSquared() > 1e-30f ? Vector3.Normalize(n) : Vector3.UnitZ;
            }
            return new MeshData
            {
                Name = name,
                Positions = positions,
                Normals = normals,
                TexCoords = _uvs.ToArray(),
                Indices = _indices.ToArray(),
                SubMeshes = { new SubMesh(0, _indices.Count, 0) },
            };
        }
    }
}
