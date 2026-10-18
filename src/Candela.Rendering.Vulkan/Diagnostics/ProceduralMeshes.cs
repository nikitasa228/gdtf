using System.Numerics;
using Candela.Core.Geometry;

namespace Candela.Rendering.Vulkan;

/// <summary>
/// Simple procedural meshes (metres, Z up, counter-clockwise front faces, outward normals, UVs in metres so
/// textures tile at their real size). Used by the render test scene, thumbnails and tests.
/// </summary>
public static class ProceduralMeshes
{
    /// <summary>Axis-aligned box centred on the origin, 24 vertices (flat faces), one sub-mesh.</summary>
    /// <param name="size">Edge lengths along X, Y, Z, m.</param>
    public static MeshData Box(Vector3 size)
    {
        var half = 0.5f * size;
        var positions = new List<Vector3>(24);
        var normals = new List<Vector3>(24);
        var uvs = new List<Vector2>(24);
        var indices = new List<uint>(36);

        // Each face: normal n with in-plane axes u, v where u × v = n, so (−u−v, +u−v, +u+v, −u+v) is counter-clockwise.
        void Face(Vector3 n, Vector3 u, Vector3 v, float hn, float hu, float hv)
        {
            uint first = (uint)positions.Count;
            for (int k = 0; k < 4; k++)
            {
                float a = k is 1 or 2 ? 1f : -1f;
                float b = k >= 2 ? 1f : -1f;
                positions.Add(n * hn + u * (a * hu) + v * (b * hv));
                normals.Add(n);
                uvs.Add(new Vector2((a + 1f) * hu, (1f - b) * hv));
            }

            indices.AddRange(new[] { first, first + 1, first + 2, first, first + 2, first + 3 });
        }

        Face(Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, half.X, half.Y, half.Z);
        Face(-Vector3.UnitX, -Vector3.UnitY, Vector3.UnitZ, half.X, half.Y, half.Z);
        Face(Vector3.UnitY, -Vector3.UnitX, Vector3.UnitZ, half.Y, half.X, half.Z);
        Face(-Vector3.UnitY, Vector3.UnitX, Vector3.UnitZ, half.Y, half.X, half.Z);
        Face(Vector3.UnitZ, Vector3.UnitX, Vector3.UnitY, half.Z, half.X, half.Y);
        Face(-Vector3.UnitZ, Vector3.UnitX, -Vector3.UnitY, half.Z, half.X, half.Y);
        return new MeshData
        {
            Name = "box",
            Positions = positions.ToArray(),
            Normals = normals.ToArray(),
            TexCoords = uvs.ToArray(),
            Indices = indices.ToArray(),
        };
    }

    /// <summary>Rectangle in the XY plane at Z = 0 facing +Z, centred on the origin.</summary>
    /// <param name="width">Size along X, m.</param>
    /// <param name="depth">Size along Y, m.</param>
    public static MeshData Plane(float width, float depth)
    {
        float hx = 0.5f * width, hy = 0.5f * depth;
        return new MeshData
        {
            Name = "plane",
            Positions = new[] { new Vector3(-hx, -hy, 0f), new Vector3(hx, -hy, 0f), new Vector3(hx, hy, 0f), new Vector3(-hx, hy, 0f) },
            Normals = new[] { Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ },
            TexCoords = new[] { new Vector2(0f, depth), new Vector2(width, depth), new Vector2(width, 0f), new Vector2(0f, 0f) },
            Indices = new uint[] { 0, 1, 2, 0, 2, 3 },
        };
    }

    /// <summary>UV sphere centred on the origin with its poles on the Z axis.</summary>
    /// <param name="radius">Radius, m.</param>
    /// <param name="segments">Divisions around Z (≥ 3).</param>
    /// <param name="rings">Divisions from pole to pole (≥ 2).</param>
    public static MeshData Sphere(float radius, int segments = 48, int rings = 24)
    {
        segments = Math.Max(segments, 3);
        rings = Math.Max(rings, 2);
        int columns = segments + 1;
        var positions = new Vector3[(rings + 1) * columns];
        var normals = new Vector3[positions.Length];
        var uvs = new Vector2[positions.Length];
        for (int i = 0; i <= rings; i++)
        {
            float theta = MathF.PI * i / rings;
            for (int j = 0; j <= segments; j++)
            {
                float phi = 2f * MathF.PI * j / segments;
                var n = new Vector3(MathF.Sin(theta) * MathF.Cos(phi), MathF.Sin(theta) * MathF.Sin(phi), MathF.Cos(theta));
                int k = i * columns + j;
                normals[k] = n;
                positions[k] = n * radius;
                uvs[k] = new Vector2(phi * radius, theta * radius);
            }
        }

        var indices = new List<uint>(rings * segments * 6);
        for (int i = 0; i < rings; i++)
        {
            for (int j = 0; j < segments; j++)
            {
                uint v00 = (uint)(i * columns + j), v01 = v00 + 1;
                uint v10 = (uint)((i + 1) * columns + j), v11 = v10 + 1;
                if (i != rings - 1)
                    indices.AddRange(new[] { v00, v10, v11 });
                if (i != 0)
                    indices.AddRange(new[] { v00, v11, v01 });
            }
        }

        return new MeshData { Name = "sphere", Positions = positions, Normals = normals, TexCoords = uvs, Indices = indices.ToArray() };
    }
}
