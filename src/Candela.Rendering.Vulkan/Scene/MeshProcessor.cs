using System.Numerics;
using Candela.Core.Geometry;

namespace Candela.Rendering.Vulkan;

/// <summary>
/// CPU preparation of <see cref="MeshData"/> for the GPU: validation, interleaving into <see cref="GpuVertex"/>,
/// normals when missing (area-weighted), tangents when missing (per-triangle UV derivatives accumulated per vertex,
/// Gram–Schmidt orthogonalised against the normal, bitangent sign in w), bounding sphere, and the unique edge list the
/// edge-shader wireframe draws on devices without <c>fillModeNonSolid</c>.
/// </summary>
internal static class MeshProcessor
{
    /// <summary>Checks array lengths, index bounds and sub-mesh ranges; throws <see cref="ArgumentException"/> with a readable message.</summary>
    public static void Validate(MeshData mesh)
    {
        int n = mesh.Positions.Length;
        string name = string.IsNullOrEmpty(mesh.Name) ? "<unnamed>" : mesh.Name;
        if (mesh.Normals.Length != 0 && mesh.Normals.Length != n)
            throw new ArgumentException($"Mesh '{name}': {mesh.Normals.Length} normals for {n} positions.", nameof(mesh));
        if (mesh.TexCoords is not null && mesh.TexCoords.Length != n)
            throw new ArgumentException($"Mesh '{name}': {mesh.TexCoords.Length} texture coordinates for {n} positions.", nameof(mesh));
        if (mesh.Tangents is not null && mesh.Tangents.Length != n)
            throw new ArgumentException($"Mesh '{name}': {mesh.Tangents.Length} tangents for {n} positions.", nameof(mesh));
        if (mesh.Indices.Length % 3 != 0)
            throw new ArgumentException($"Mesh '{name}': index count {mesh.Indices.Length} is not a multiple of 3.", nameof(mesh));
        foreach (uint i in mesh.Indices)
        {
            if (i >= (uint)n)
                throw new ArgumentException($"Mesh '{name}': index {i} out of range ({n} vertices).", nameof(mesh));
        }

        foreach (var sub in mesh.SubMeshes)
        {
            if (sub.IndexStart < 0 || sub.IndexCount < 0 || sub.IndexStart + sub.IndexCount > mesh.Indices.Length || sub.MaterialSlot < 0)
                throw new ArgumentException($"Mesh '{name}': sub-mesh [{sub.IndexStart}, +{sub.IndexCount}) slot {sub.MaterialSlot} is out of range.", nameof(mesh));
        }
    }

    /// <summary>Interleaved vertices with normals and tangents filled in.</summary>
    public static GpuVertex[] BuildVertices(MeshData mesh)
    {
        int n = mesh.Positions.Length;
        var positions = mesh.Positions;
        var normals = mesh.Normals.Length == n ? mesh.Normals : ComputeNormals(positions, mesh.Indices);
        var uvs = mesh.TexCoords;
        var tangents = mesh.Tangents ?? ComputeTangents(positions, normals, uvs, mesh.Indices);
        var vertices = new GpuVertex[n];
        for (int i = 0; i < n; i++)
        {
            var p = positions[i];
            var nrm = normals[i];
            float len = nrm.Length();
            nrm = len > 1e-20f && float.IsFinite(len) ? nrm / len : Vector3.UnitZ;
            vertices[i] = new GpuVertex
            {
                Position = float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z) ? p : Vector3.Zero,
                Normal = nrm,
                Uv = uvs is null ? Vector2.Zero : uvs[i],
                Tangent = tangents[i],
            };
        }

        return vertices;
    }

    /// <summary>Area-weighted vertex normals (as <see cref="MeshData.ComputeNormals"/>, without modifying the mesh).</summary>
    public static Vector3[] ComputeNormals(Vector3[] positions, uint[] indices)
    {
        var n = new Vector3[positions.Length];
        for (int i = 0; i + 2 < indices.Length; i += 3)
        {
            uint a = indices[i], b = indices[i + 1], c = indices[i + 2];
            var fn = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]);
            n[a] += fn;
            n[b] += fn;
            n[c] += fn;
        }

        for (int i = 0; i < n.Length; i++)
            n[i] = n[i].LengthSquared() > 1e-20f ? Vector3.Normalize(n[i]) : Vector3.UnitZ;
        return n;
    }

    /// <summary>
    /// Per-vertex tangents from UV derivatives (Lengyel 2001). Vertices without usable UVs get an arbitrary tangent
    /// perpendicular to the normal (w = 1), which is correct for untextured surfaces.
    /// </summary>
    public static Vector4[] ComputeTangents(Vector3[] positions, Vector3[] normals, Vector2[]? uvs, uint[] indices)
    {
        int n = positions.Length;
        var tan = new Vector3[n];
        var bitan = new Vector3[n];
        if (uvs is not null)
        {
            for (int i = 0; i + 2 < indices.Length; i += 3)
            {
                uint a = indices[i], b = indices[i + 1], c = indices[i + 2];
                var e1 = positions[b] - positions[a];
                var e2 = positions[c] - positions[a];
                var d1 = uvs[b] - uvs[a];
                var d2 = uvs[c] - uvs[a];
                float r = d1.X * d2.Y - d2.X * d1.Y;
                if (MathF.Abs(r) < 1e-12f || !float.IsFinite(r))
                    continue;
                float inv = 1f / r;
                var t = (e1 * d2.Y - e2 * d1.Y) * inv;
                var bt = (e2 * d1.X - e1 * d2.X) * inv;
                tan[a] += t;
                tan[b] += t;
                tan[c] += t;
                bitan[a] += bt;
                bitan[b] += bt;
                bitan[c] += bt;
            }
        }

        var result = new Vector4[n];
        for (int i = 0; i < n; i++)
        {
            var nrm = normals[i];
            float nl = nrm.Length();
            nrm = nl > 1e-20f && float.IsFinite(nl) ? nrm / nl : Vector3.UnitZ;
            var t = tan[i] - nrm * Vector3.Dot(nrm, tan[i]);
            float tl = t.Length();
            if (!(tl > 1e-12f) || !float.IsFinite(tl))
            {
                result[i] = new Vector4(AnyPerpendicular(nrm), 1f);
                continue;
            }

            t /= tl;
            float w = Vector3.Dot(Vector3.Cross(nrm, t), bitan[i]) < 0f ? -1f : 1f;
            result[i] = new Vector4(t, w);
        }

        return result;
    }

    /// <summary>Bounding sphere (centre of the AABB, radius to the farthest vertex) in object space.</summary>
    public static Vector4 BoundingSphere(Vector3[] positions)
    {
        if (positions.Length == 0)
            return Vector4.Zero;
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var p in positions)
        {
            if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z))
                continue;
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }

        if (min.X > max.X)
            return Vector4.Zero;
        var centre = 0.5f * (min + max);
        float r2 = 0f;
        foreach (var p in positions)
        {
            if (float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z))
                r2 = MathF.Max(r2, Vector3.DistanceSquared(p, centre));
        }

        return new Vector4(centre, MathF.Sqrt(r2));
    }

    /// <summary>
    /// Unique triangle edges as endpoint pairs (object space). Vertices that share a position (split for normals or
    /// UVs) are merged, so every visible edge is drawn exactly once.
    /// </summary>
    public static Vector3[] BuildEdges(MeshData mesh)
    {
        var positions = mesh.Positions;
        var ids = new int[positions.Length];
        var unique = new Dictionary<Vector3, int>(positions.Length);
        for (int i = 0; i < positions.Length; i++)
        {
            if (!unique.TryGetValue(positions[i], out int id))
            {
                id = unique.Count;
                unique.Add(positions[i], id);
            }

            ids[i] = id;
        }

        var seen = new HashSet<long>();
        var edges = new List<Vector3>();
        var indices = mesh.Indices;
        for (int t = 0; t + 2 < indices.Length; t += 3)
        {
            for (int e = 0; e < 3; e++)
            {
                uint a = indices[t + e], b = indices[t + (e + 1) % 3];
                int ia = ids[a], ib = ids[b];
                if (ia == ib)
                    continue;
                long key = ia < ib ? ((long)ia << 32) | (uint)ib : ((long)ib << 32) | (uint)ia;
                if (!seen.Add(key))
                    continue;
                edges.Add(positions[a]);
                edges.Add(positions[b]);
            }
        }

        return edges.ToArray();
    }

    private static Vector3 AnyPerpendicular(Vector3 n)
    {
        var axis = MathF.Abs(n.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY;
        return Vector3.Normalize(axis - n * Vector3.Dot(n, axis));
    }
}
