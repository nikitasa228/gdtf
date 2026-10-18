using System.Numerics;
using Candela.Core.Geometry;

namespace Candela.Assets;

/// <summary>
/// Mesh post-processing shared by the importers and the procedural generators: tangent frames, flat normals,
/// rigid/affine transforms (with winding correction for mirrors) and merging into one mesh with sub-meshes.
/// </summary>
public static class MeshTools
{
    /// <summary>Positions with more faces around them than this use clustered normals (see <see cref="GenerateNormals"/>).</summary>
    private const int MaxExactValence = 64;

    /// <summary>Most orientation clusters per high-valence position (and most normal variants searched per vertex).</summary>
    private const int MaxClusters = 64;

    /// <summary>A deep copy of <paramref name="mesh"/> (every array and the sub-mesh list are copied).</summary>
    public static MeshData Clone(MeshData mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        return new MeshData
        {
            Name = mesh.Name,
            Positions = (Vector3[])mesh.Positions.Clone(),
            Normals = (Vector3[])mesh.Normals.Clone(),
            TexCoords = mesh.TexCoords is null ? null : (Vector2[])mesh.TexCoords.Clone(),
            Tangents = mesh.Tangents is null ? null : (Vector4[])mesh.Tangents.Clone(),
            Indices = (uint[])mesh.Indices.Clone(),
            SubMeshes = new List<SubMesh>(mesh.SubMeshes),
        };
    }

    /// <summary>
    /// Computes per-vertex tangents (Lengyel's method, accumulated per triangle, Gram–Schmidt orthogonalised against the
    /// normal). <c>w</c> follows glTF: <c>cross(normal, tangent.xyz) * w</c> points towards decreasing V, i.e. "up" in a
    /// top-row-first image, which is the +Y (green) direction of a glTF/OpenGL normal map. Vertices without usable
    /// texture coordinates get an arbitrary tangent perpendicular to the normal. Normals must already be present.
    /// </summary>
    public static void GenerateTangents(MeshData mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var pos = mesh.Positions;
        var nrm = mesh.Normals;
        int n = pos.Length;
        if (nrm.Length != n) throw new InvalidOperationException($"Mesh '{mesh.Name}' needs normals before tangents.");
        var tangents = new Vector4[n];
        var uv = mesh.TexCoords;
        if (uv is null || uv.Length != n)
        {
            for (int i = 0; i < n; i++) tangents[i] = new Vector4(AnyPerpendicular(nrm[i]), 1f);
            mesh.Tangents = tangents;
            return;
        }

        var tan1 = new Vector3[n];
        var tan2 = new Vector3[n];
        var idx = mesh.Indices;
        for (int i = 0; i + 2 < idx.Length; i += 3)
        {
            uint a = idx[i], b = idx[i + 1], c = idx[i + 2];
            if (a >= n || b >= n || c >= n) continue;
            Vector3 e1 = pos[b] - pos[a], e2 = pos[c] - pos[a];
            Vector2 d1 = uv[b] - uv[a], d2 = uv[c] - uv[a];
            float det = d1.X * d2.Y - d2.X * d1.Y;
            if (!(MathF.Abs(det) > 1e-20f) || !float.IsFinite(det)) continue;
            float r = 1f / det;
            var sdir = (e1 * d2.Y - e2 * d1.Y) * r;
            var tdir = (e2 * d1.X - e1 * d2.X) * r;
            if (!IsFinite(sdir) || !IsFinite(tdir)) continue;
            tan1[a] += sdir;
            tan1[b] += sdir;
            tan1[c] += sdir;
            tan2[a] += tdir;
            tan2[b] += tdir;
            tan2[c] += tdir;
        }

        for (int i = 0; i < n; i++)
        {
            var nn = nrm[i];
            var t = tan1[i] - nn * Vector3.Dot(nn, tan1[i]);
            if (t.LengthSquared() < 1e-24f || !IsFinite(t))
            {
                tangents[i] = new Vector4(AnyPerpendicular(nn), 1f);
                continue;
            }
            t = Vector3.Normalize(t);
            // tan2 points towards increasing V (down in the image); glTF's bitangent points up, hence the inverted test.
            float w = Vector3.Dot(Vector3.Cross(nn, t), tan2[i]) < 0f ? 1f : -1f;
            tangents[i] = new Vector4(t, w);
        }
        mesh.Tangents = tangents;
    }

    /// <summary>
    /// Computes vertex normals for a mesh without them, smoothing across edges whose dihedral angle is at most
    /// <paramref name="smoothingAngleDegrees"/> and splitting vertices at sharper edges (a box keeps flat faces, a
    /// cylinder's side is smooth). Faces are area-weighted; vertices at the same position (texture seams) smooth
    /// together. Indices are rewritten when vertices are split; texture coordinates are carried over, tangents dropped
    /// (regenerate them afterwards) and sub-mesh ranges stay valid. Ordinary vertices (valence ≤ 64) compare every pair of
    /// faces around them; around high-valence positions (the centre of a finely divided disc or a cone apex) faces are
    /// first grouped into at most 64 clusters of similar orientation and each corner takes its cluster's normal, so the
    /// cost stays linear in the number of faces (O(Σ min(valence, 64) · valence)).
    /// </summary>
    public static void GenerateNormals(MeshData mesh, float smoothingAngleDegrees)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var pos = mesh.Positions;
        var idx = mesh.Indices;
        int vertexCount = pos.Length;
        int faceCount = idx.Length / 3;
        float angle = float.IsFinite(smoothingAngleDegrees) ? System.Math.Clamp(smoothingAngleDegrees, 0f, 180f) : 66f;
        float cosLimit = MathF.Cos(angle * MathF.PI / 180f) - 1e-4f;

        // Area-weighted (unnormalised) and unit face normals.
        var weighted = new Vector3[faceCount];
        var unit = new Vector3[faceCount];
        for (int f = 0; f < faceCount; f++)
        {
            uint a = idx[3 * f], b = idx[3 * f + 1], c = idx[3 * f + 2];
            if (a >= vertexCount || b >= vertexCount || c >= vertexCount) continue;
            var n = Vector3.Cross(pos[b] - pos[a], pos[c] - pos[a]);
            if (!IsFinite(n) || n.LengthSquared() < 1e-30f) continue;
            weighted[f] = n;
            unit[f] = Vector3.Normalize(n);
        }

        // Weld by position so faces on both sides of a texture seam smooth together.
        var weld = new int[vertexCount];
        var first = new Dictionary<Vector3, int>(vertexCount);
        for (int v = 0; v < vertexCount; v++)
        {
            if (!first.TryGetValue(pos[v], out int w)) first[pos[v]] = w = v;
            weld[v] = w;
        }

        // Faces around each welded position (CSR layout).
        var start = new int[vertexCount + 1];
        for (int i = 0; i < faceCount * 3; i++)
            if (idx[i] < vertexCount) start[weld[idx[i]] + 1]++;
        for (int v = 0; v < vertexCount; v++) start[v + 1] += start[v];
        var faces = new int[start[vertexCount]];
        var cornerSlot = new int[faceCount * 3];
        var fill = (int[])start.Clone();
        for (int i = 0; i < faceCount * 3; i++)
        {
            if (idx[i] >= vertexCount) continue;
            int slot = fill[weld[idx[i]]]++;
            faces[slot] = i / 3;
            cornerSlot[i] = slot;
        }

        // High-valence positions: greedy clustering of the surrounding faces by orientation (see the remarks).
        int[]? clusterOf = null;
        List<Vector3>? clusterSum = null;
        for (int p = 0; p < vertexCount; p++)
        {
            int valence = start[p + 1] - start[p];
            if (valence <= MaxExactValence) continue;
            clusterOf ??= new int[faces.Length];
            clusterSum ??= new List<Vector3>();
            int firstCluster = clusterSum.Count;
            var representatives = new List<Vector3>(MaxClusters);
            for (int k = start[p]; k < start[p + 1]; k++)
            {
                int g = faces[k];
                var u = unit[g];
                if (u == Vector3.Zero)
                {
                    clusterOf[k] = -1;
                    continue;
                }
                int found = -1;
                for (int c = 0; c < representatives.Count; c++)
                {
                    if (Vector3.Dot(representatives[c], u) >= cosLimit)
                    {
                        found = firstCluster + c;
                        break;
                    }
                }
                if (found < 0)
                {
                    found = clusterSum.Count;
                    clusterSum.Add(Vector3.Zero);
                    // Beyond the cluster limit a face keeps its own normal (singleton cluster, not searched again).
                    if (representatives.Count < MaxClusters) representatives.Add(u);
                }
                clusterOf[k] = found;
                clusterSum[found] += weighted[g];
            }
        }

        // One normal per face corner; corners of the same vertex with (nearly) equal normals share an output vertex.
        var outPos = new List<Vector3>(vertexCount);
        var outNrm = new List<Vector3>(vertexCount);
        var srcUv = mesh.TexCoords is { } t && t.Length == vertexCount ? t : null;
        var outUv = srcUv is null ? null : new List<Vector2>(vertexCount);
        var variants = new int[vertexCount];
        Array.Fill(variants, -1);
        var nextVariant = new List<int>(vertexCount);
        var newIdx = new uint[idx.Length];
        for (int i = 0; i < idx.Length; i++)
        {
            uint v = idx[i];
            if (v >= vertexCount || i >= faceCount * 3)
            {
                newIdx[i] = 0;
                continue;
            }
            int f = i / 3;
            var n = Vector3.Zero;
            var own = unit[f];
            int p = weld[v];
            if (start[p + 1] - start[p] > MaxExactValence)
            {
                int c = clusterOf![cornerSlot[i]];
                if (c >= 0) n = clusterSum![c];
            }
            else
            {
                for (int k = start[p]; k < start[p + 1]; k++)
                {
                    int g = faces[k];
                    if (g == f || Vector3.Dot(own, unit[g]) >= cosLimit) n += weighted[g];
                }
            }
            if (n.LengthSquared() < 1e-30f) n = own.LengthSquared() > 0 ? own : Vector3.UnitZ;
            n = Vector3.Normalize(n);

            // Reuse an output vertex with the same normal (the search is bounded; a miss only duplicates a vertex).
            int found = -1, searched = 0;
            for (int o = variants[v]; o >= 0 && searched < MaxClusters; o = nextVariant[o], searched++)
            {
                if (Vector3.Dot(outNrm[o], n) > 0.9999f)
                {
                    found = o;
                    break;
                }
            }
            if (found < 0)
            {
                found = outPos.Count;
                outPos.Add(pos[v]);
                outNrm.Add(n);
                outUv?.Add(srcUv![v]);
                nextVariant.Add(variants[v]);
                variants[v] = found;
            }
            newIdx[i] = (uint)found;
        }
        mesh.Positions = outPos.ToArray();
        mesh.Normals = outNrm.ToArray();
        mesh.TexCoords = outUv?.ToArray();
        mesh.Tangents = null;
        mesh.Indices = newIdx;
    }

    /// <summary>
    /// Replaces the mesh by an unwelded copy with one face normal per triangle (glTF requires flat normals when a file
    /// has none). Texture coordinates are carried over; tangents are dropped (regenerate them afterwards).
    /// </summary>
    public static void ComputeFlatNormals(MeshData mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var idx = mesh.Indices;
        var src = mesh.Positions;
        var srcUv = mesh.TexCoords is { } t && t.Length == src.Length ? t : null;
        int count = idx.Length - idx.Length % 3;
        var p = new Vector3[count];
        var nn = new Vector3[count];
        var uv = srcUv is null ? null : new Vector2[count];
        var newIdx = new uint[count];
        for (int i = 0; i < count; i += 3)
        {
            uint a = idx[i], b = idx[i + 1], c = idx[i + 2];
            if (a >= src.Length || b >= src.Length || c >= src.Length)
            {
                a = b = c = 0;
            }
            Vector3 pa = src.Length > 0 ? src[a] : default, pb = src.Length > 0 ? src[b] : default, pc = src.Length > 0 ? src[c] : default;
            var fn = Vector3.Cross(pb - pa, pc - pa);
            fn = fn.LengthSquared() > 1e-30f ? Vector3.Normalize(fn) : Vector3.UnitZ;
            p[i] = pa;
            p[i + 1] = pb;
            p[i + 2] = pc;
            nn[i] = nn[i + 1] = nn[i + 2] = fn;
            if (uv is not null && srcUv is not null && src.Length > 0)
            {
                uv[i] = srcUv[a];
                uv[i + 1] = srcUv[b];
                uv[i + 2] = srcUv[c];
            }
            newIdx[i] = (uint)i;
            newIdx[i + 1] = (uint)(i + 1);
            newIdx[i + 2] = (uint)(i + 2);
        }
        mesh.Positions = p;
        mesh.Normals = nn;
        mesh.TexCoords = uv;
        mesh.Tangents = null;
        mesh.Indices = newIdx;
    }

    /// <summary>
    /// Transforms a mesh in place by an affine matrix (System.Numerics row-vector convention). Normals use the inverse
    /// transpose; when the matrix mirrors (negative determinant) the triangle winding is flipped and tangent handedness
    /// inverted so front faces stay counter-clockwise.
    /// </summary>
    public static void Transform(MeshData mesh, Matrix4x4 matrix)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if (matrix.IsIdentity) return;
        var pos = mesh.Positions;
        for (int i = 0; i < pos.Length; i++) pos[i] = Vector3.Transform(pos[i], matrix);

        var linear = matrix with { M41 = 0, M42 = 0, M43 = 0 };
        Matrix4x4 normalMatrix = Matrix4x4.Invert(linear, out var inv) ? Matrix4x4.Transpose(inv) : linear;
        var nrm = mesh.Normals;
        for (int i = 0; i < nrm.Length; i++)
        {
            var v = Vector3.TransformNormal(nrm[i], normalMatrix);
            nrm[i] = v.LengthSquared() > 1e-30f ? Vector3.Normalize(v) : Vector3.UnitZ;
        }

        float det = Determinant3(matrix);
        if (mesh.Tangents is { } tan)
        {
            float flip = det < 0 ? -1f : 1f;
            for (int i = 0; i < tan.Length; i++)
            {
                var t = Vector3.TransformNormal(new Vector3(tan[i].X, tan[i].Y, tan[i].Z), linear);
                t = t.LengthSquared() > 1e-30f ? Vector3.Normalize(t) : Vector3.UnitX;
                tan[i] = new Vector4(t, tan[i].W * flip);
            }
        }
        if (det < 0) FlipWinding(mesh);
    }

    /// <summary>Reverses the vertex order of every triangle (front ↔ back faces).</summary>
    public static void FlipWinding(MeshData mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var idx = mesh.Indices;
        for (int i = 0; i + 2 < idx.Length; i += 3) (idx[i + 1], idx[i + 2]) = (idx[i + 2], idx[i + 1]);
    }

    /// <summary>Determinant of the upper-left 3×3 block (negative = the matrix mirrors).</summary>
    public static float Determinant3(in Matrix4x4 m) =>
        m.M11 * (m.M22 * m.M33 - m.M23 * m.M32)
        - m.M12 * (m.M21 * m.M33 - m.M23 * m.M31)
        + m.M13 * (m.M21 * m.M32 - m.M22 * m.M31);

    /// <summary>
    /// Merges meshes (already in a common space) into one mesh whose sub-meshes are grouped by material slot, so a
    /// renderer draws one range per material instead of one mesh per file object. Each source mesh's own sub-meshes are
    /// honoured; a source without sub-meshes uses its <paramref name="sources"/> slot. Missing texture coordinates or
    /// tangents of individual sources are filled with zeros / regenerated by the caller.
    /// </summary>
    public static MeshData Merge(string name, IReadOnlyList<(MeshData Mesh, int MaterialSlot)> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Count == 1 && sources[0].Mesh.SubMeshes.Count == 0)
        {
            var only = sources[0].Mesh;
            only.Name = name;
            only.SubMeshes = new List<SubMesh> { new(0, only.Indices.Length, sources[0].MaterialSlot) };
            return only;
        }

        int vertexCount = 0, indexCount = 0;
        bool anyUv = false;
        foreach (var (m, _) in sources)
        {
            vertexCount += m.Positions.Length;
            indexCount += m.Indices.Length;
            anyUv |= m.TexCoords is not null;
        }
        if ((long)vertexCount > uint.MaxValue) throw new InvalidOperationException("Merged mesh exceeds the 32-bit index range.");

        var pos = new Vector3[vertexCount];
        var nrm = new Vector3[vertexCount];
        var uv = anyUv ? new Vector2[vertexCount] : null;
        var baseVertex = new int[sources.Count];
        int v = 0;
        for (int s = 0; s < sources.Count; s++)
        {
            var m = sources[s].Mesh;
            baseVertex[s] = v;
            m.Positions.CopyTo(pos, v);
            if (m.Normals.Length == m.Positions.Length) m.Normals.CopyTo(nrm, v);
            if (uv is not null && m.TexCoords is { } t && t.Length == m.Positions.Length) t.CopyTo(uv, v);
            v += m.Positions.Length;
        }

        // (slot, source, start, count) ranges, stably grouped by slot.
        var ranges = new List<(int Slot, int Source, int Start, int Count)>();
        for (int s = 0; s < sources.Count; s++)
        {
            var m = sources[s].Mesh;
            if (m.SubMeshes.Count == 0) ranges.Add((sources[s].MaterialSlot, s, 0, m.Indices.Length));
            else foreach (var sm in m.SubMeshes) ranges.Add((sm.MaterialSlot, s, sm.IndexStart, sm.IndexCount));
        }
        var ordered = ranges.Select((r, i) => (r, i)).OrderBy(x => x.r.Slot).ThenBy(x => x.i).Select(x => x.r).ToList();

        var idx = new uint[indexCount];
        var subMeshes = new List<SubMesh>();
        int w = 0;
        foreach (var r in ordered)
        {
            var src = sources[r.Source].Mesh.Indices;
            uint bv = (uint)baseVertex[r.Source];
            int start = w;
            int end = System.Math.Min(r.Start + r.Count, src.Length);
            for (int i = r.Start; i < end; i++) idx[w++] = src[i] + bv;
            int count = w - start;
            if (count == 0) continue;
            if (subMeshes.Count > 0 && subMeshes[^1].MaterialSlot == r.Slot && subMeshes[^1].IndexStart + subMeshes[^1].IndexCount == start)
                subMeshes[^1] = subMeshes[^1] with { IndexCount = subMeshes[^1].IndexCount + count };
            else subMeshes.Add(new SubMesh(start, count, r.Slot));
        }
        if (w != idx.Length) Array.Resize(ref idx, w);

        return new MeshData
        {
            Name = name,
            Positions = pos,
            Normals = nrm,
            TexCoords = uv,
            Indices = idx,
            SubMeshes = subMeshes,
        };
    }

    /// <summary>
    /// Removes triangles that reference missing vertices or have non-finite corners (and a trailing partial triangle),
    /// keeping sub-mesh ranges consistent. Returns the number of triangles dropped.
    /// </summary>
    public static int RemoveInvalidTriangles(MeshData mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var idx = mesh.Indices;
        var pos = mesh.Positions;
        int n = pos.Length;
        var ranges = mesh.SubMeshes.Count > 0
            ? mesh.SubMeshes
            : new List<SubMesh> { new(0, idx.Length, 0) };
        var newRanges = new List<SubMesh>(ranges.Count);
        var output = new uint[idx.Length];
        int w = 0, dropped = 0;
        foreach (var r in ranges)
        {
            int start = System.Math.Clamp(r.IndexStart, 0, idx.Length);
            int end = System.Math.Clamp(r.IndexStart + r.IndexCount, start, idx.Length);
            int outStart = w;
            int i = start;
            for (; i + 2 < end; i += 3)
            {
                uint a = idx[i], b = idx[i + 1], c = idx[i + 2];
                if (a >= n || b >= n || c >= n || !IsFinite(pos[a]) || !IsFinite(pos[b]) || !IsFinite(pos[c]))
                {
                    dropped++;
                    continue;
                }
                output[w++] = a;
                output[w++] = b;
                output[w++] = c;
            }
            if (i < end) dropped++;
            if (w > outStart) newRanges.Add(new SubMesh(outStart, w - outStart, r.MaterialSlot));
        }
        if (dropped == 0 && w == idx.Length) return 0;
        Array.Resize(ref output, w);
        mesh.Indices = output;
        mesh.SubMeshes = mesh.SubMeshes.Count > 0 ? newRanges : new List<SubMesh>();
        return dropped;
    }

    /// <summary>A unit vector perpendicular to <paramref name="n"/> (stable choice of reference axis).</summary>
    public static Vector3 AnyPerpendicular(Vector3 n)
    {
        var reference = MathF.Abs(n.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitX;
        var t = Vector3.Cross(reference, n);
        if (t.LengthSquared() < 1e-24f) return Vector3.UnitX;
        return Vector3.Normalize(t);
    }

    internal static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
