using System.Numerics;
using Candela.Core.Geometry;

namespace Candela.Assets;

/// <summary>
/// Growable vertex/index buffers for the procedural generators. Texture coordinates follow glTF: U to the right,
/// V downwards (top row of an image at V = 0); generators express them in metres times a UV scale.
/// </summary>
internal sealed class MeshBuilder
{
    private Vector3[] _positions;
    private Vector3[] _normals;
    private Vector2[] _uvs;
    private uint[] _indices;
    private int _vertexCount;
    private int _indexCount;

    public MeshBuilder(int vertexCapacity = 64, int indexCapacity = 96)
    {
        _positions = new Vector3[System.Math.Max(vertexCapacity, 4)];
        _normals = new Vector3[_positions.Length];
        _uvs = new Vector2[_positions.Length];
        _indices = new uint[System.Math.Max(indexCapacity, 6)];
    }

    public int VertexCount => _vertexCount;
    public int IndexCount => _indexCount;

    public int AddVertex(Vector3 position, Vector3 normal, Vector2 uv)
    {
        if (_vertexCount == _positions.Length)
        {
            int size = _positions.Length * 2;
            Array.Resize(ref _positions, size);
            Array.Resize(ref _normals, size);
            Array.Resize(ref _uvs, size);
        }
        _positions[_vertexCount] = position;
        _normals[_vertexCount] = normal;
        _uvs[_vertexCount] = uv;
        return _vertexCount++;
    }

    public void AddTriangle(int a, int b, int c)
    {
        if (_indexCount + 3 > _indices.Length) Array.Resize(ref _indices, _indices.Length * 2);
        _indices[_indexCount++] = (uint)a;
        _indices[_indexCount++] = (uint)b;
        _indices[_indexCount++] = (uint)c;
    }

    /// <summary>Two triangles a-b-c, a-c-d (counter-clockwise when seen from the front).</summary>
    public void AddQuad(int a, int b, int c, int d)
    {
        AddTriangle(a, b, c);
        AddTriangle(a, c, d);
    }

    /// <summary>
    /// A planar rectangle with outward normal <paramref name="normal"/>, its "up" direction <paramref name="up"/>
    /// (in the face plane), centred at <paramref name="center"/>. U runs along <c>cross(up, normal)</c> (to the right
    /// when looking at the face), V runs down; both start at 0 on the top-left corner and grow in metres × uvScale.
    /// </summary>
    public void AddRectangle(Vector3 center, Vector3 normal, Vector3 up, float width, float height, float uvScale)
    {
        var right = Vector3.Cross(up, normal);
        var hw = right * (width * 0.5f);
        var hh = up * (height * 0.5f);
        float u1 = width * uvScale, v1 = height * uvScale;
        int tl = AddVertex(center - hw + hh, normal, new Vector2(0, 0));
        int bl = AddVertex(center - hw - hh, normal, new Vector2(0, v1));
        int br = AddVertex(center + hw - hh, normal, new Vector2(u1, v1));
        int tr = AddVertex(center + hw + hh, normal, new Vector2(u1, 0));
        AddQuad(tl, bl, br, tr);
    }

    /// <summary>
    /// An axis-aligned box from <paramref name="min"/> to <paramref name="max"/> with 24 vertices (flat faces) and
    /// per-face UVs in metres × uvScale (vertical faces: up = +Z; top/bottom faces: up = +Y).
    /// </summary>
    public void AddBox(Vector3 min, Vector3 max, float uvScale)
    {
        var c = (min + max) * 0.5f;
        var s = max - min;
        AddRectangle(new Vector3(c.X, min.Y, c.Z), -Vector3.UnitY, Vector3.UnitZ, s.X, s.Z, uvScale); // front (−Y)
        AddRectangle(new Vector3(c.X, max.Y, c.Z), Vector3.UnitY, Vector3.UnitZ, s.X, s.Z, uvScale); // back (+Y)
        AddRectangle(new Vector3(min.X, c.Y, c.Z), -Vector3.UnitX, Vector3.UnitZ, s.Y, s.Z, uvScale); // left (−X)
        AddRectangle(new Vector3(max.X, c.Y, c.Z), Vector3.UnitX, Vector3.UnitZ, s.Y, s.Z, uvScale); // right (+X)
        AddRectangle(new Vector3(c.X, c.Y, max.Z), Vector3.UnitZ, Vector3.UnitY, s.X, s.Y, uvScale); // top (+Z)
        AddRectangle(new Vector3(c.X, c.Y, min.Z), -Vector3.UnitZ, Vector3.UnitY, s.X, s.Y, uvScale); // bottom (−Z)
    }

    /// <summary>
    /// An open or capped tube between two points with a circular cross-section of <paramref name="segments"/> sides
    /// (seam vertex duplicated for UVs). U runs around the tube (arc length in metres × uvScale), V along it.
    /// The first ring vertex lies along <paramref name="referenceAxis"/> projected onto the cross-section plane, so
    /// callers can align ring vertices with bounding-box axes.
    /// </summary>
    public void AddTube(Vector3 a, Vector3 b, float radius, int segments, bool capStart, bool capEnd, float uvScale, Vector3 referenceAxis)
    {
        var axis = b - a;
        float length = axis.Length();
        if (!(length > 1e-9f) || !(radius > 0f)) return;
        var dir = axis / length;
        var x = referenceAxis - dir * Vector3.Dot(referenceAxis, dir);
        x = x.LengthSquared() > 1e-12f ? Vector3.Normalize(x) : MeshTools.AnyPerpendicular(dir);
        var y = Vector3.Cross(dir, x);
        int baseA = _vertexCount;
        float circumference = 2f * MathF.PI * radius;
        for (int i = 0; i <= segments; i++)
        {
            float t = i / (float)segments;
            float ang = t * 2f * MathF.PI;
            var radial = x * MathF.Cos(ang) + y * MathF.Sin(ang);
            AddVertex(a + radial * radius, radial, new Vector2(t * circumference * uvScale, 0));
            AddVertex(b + radial * radius, radial, new Vector2(t * circumference * uvScale, length * uvScale));
        }
        for (int i = 0; i < segments; i++)
        {
            int a0 = baseA + i * 2, b0 = a0 + 1, a1 = a0 + 2, b1 = a0 + 3;
            AddQuad(a0, a1, b1, b0);
        }
        if (capStart) AddDisc(a, -dir, x, radius, segments, uvScale);
        if (capEnd) AddDisc(b, dir, x, radius, segments, uvScale);
    }

    /// <summary>A flat disc (triangle fan) facing <paramref name="normal"/>; UVs are planar in metres × uvScale.</summary>
    public void AddDisc(Vector3 center, Vector3 normal, Vector3 xAxis, float radius, int segments, float uvScale)
    {
        var x = xAxis - normal * Vector3.Dot(xAxis, normal);
        x = x.LengthSquared() > 1e-12f ? Vector3.Normalize(x) : MeshTools.AnyPerpendicular(normal);
        var y = Vector3.Cross(normal, x);
        int c = AddVertex(center, normal, new Vector2(radius * uvScale, radius * uvScale));
        int first = _vertexCount;
        for (int i = 0; i < segments; i++)
        {
            float ang = i * 2f * MathF.PI / segments;
            float cx = MathF.Cos(ang), sy = MathF.Sin(ang);
            AddVertex(center + (x * cx + y * sy) * radius, normal, new Vector2((cx + 1) * radius * uvScale, (1 - sy) * radius * uvScale));
        }
        for (int i = 0; i < segments; i++) AddTriangle(c, first + i, first + (i + 1) % segments);
    }

    /// <summary>Copies the buffers into a <see cref="MeshData"/>, optionally generating tangents.</summary>
    public MeshData ToMesh(string name, bool tangents = true)
    {
        var mesh = new MeshData
        {
            Name = name,
            Positions = _positions.AsSpan(0, _vertexCount).ToArray(),
            Normals = _normals.AsSpan(0, _vertexCount).ToArray(),
            TexCoords = _uvs.AsSpan(0, _vertexCount).ToArray(),
            Indices = _indices.AsSpan(0, _indexCount).ToArray(),
        };
        if (tangents) MeshTools.GenerateTangents(mesh);
        return mesh;
    }
}
