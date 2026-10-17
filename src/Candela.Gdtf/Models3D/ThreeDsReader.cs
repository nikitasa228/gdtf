using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using Candela.Core.Geometry;

namespace Candela.Gdtf;

/// <summary>
/// Reader for Autodesk 3DS files as embedded in GDTF archives (<c>models/3ds/*.3ds</c>): main/editor/object/tri-mesh
/// chunks, vertices, faces, per-face materials, smoothing groups, texture coordinates and materials with diffuse
/// colour, transparency, shininess, two-sidedness and texture map names.
/// </summary>
/// <remarks>
/// Coordinates are returned as stored (3DS is Z-up; GDTF files use metres or, very often, millimetres — the model
/// loader fits the result to the declared model size). Vertices of a 3DS object are already in scene space, so every
/// part has an identity transform. Normals follow the smoothing groups; without smoothing groups they are smoothed
/// across edges flatter than <see cref="AutoSmoothAngleDegrees"/>. Texture V is flipped to a top-left origin. Malformed
/// chunks end the affected object or list early; the method only throws when the data is not a 3DS file at all.
/// </remarks>
public static class ThreeDsReader
{
    /// <summary>Crease angle used to compute normals when a mesh has no smoothing groups.</summary>
    public const float AutoSmoothAngleDegrees = 40f;

    private const ushort Main = 0x4D4D, Editor = 0x3D3D, NamedObject = 0x4000, TriMesh = 0x4100;
    private const ushort VertexList = 0x4110, FaceList = 0x4120, FaceMaterial = 0x4130, TexCoordList = 0x4140;
    private const ushort SmoothGroups = 0x4150, MaterialBlock = 0xAFFF, MaterialName = 0xA000, Diffuse = 0xA020;
    private const ushort Shininess = 0xA040, Transparency = 0xA050, TwoSided = 0xA081, TextureMap = 0xA200;
    private const ushort MapFileName = 0xA300, MapUScale = 0xA356, MapVScale = 0xA354;
    private const ushort ColorF = 0x0010, Color24 = 0x0011, LinColor24 = 0x0012, LinColorF = 0x0013;
    private const ushort PercentInt = 0x0030, PercentFloat = 0x0031;

    /// <summary>Parses a 3DS file. <paramref name="warnings"/> receives non-fatal problems (may be null).</summary>
    /// <exception cref="InvalidDataException">The data does not start with a 3DS main or editor chunk.</exception>
    public static ModelData Read(ReadOnlySpan<byte> data, string name = "", Action<string>? warnings = null)
    {
        if (data.Length < 6) throw new InvalidDataException("3DS data is too short.");
        ushort first = BinaryPrimitives.ReadUInt16LittleEndian(data);
        if (first != Main && first != Editor) throw new InvalidDataException($"Not a 3DS file (first chunk 0x{first:X4}).");

        var state = new State(warnings);
        var model = new ModelData { Name = name };
        int end = (int)System.Math.Min(ChunkLength(data, 0), (uint)data.Length);
        if (first == Main) WalkMain(data, 6, end, state, model);
        else WalkEditor(data, 6, end, state, model);
        model.Materials.AddRange(state.Materials);
        if (state.NeedsDefaultMaterial)
        {
            // Faces without a material were given slot -1; point them at a default material appended last.
            int slot = model.Materials.Count;
            model.Materials.Add(DefaultMaterial());
            foreach (var part in model.Parts)
                for (int i = 0; i < part.Mesh.SubMeshes.Count; i++)
                    if (part.Mesh.SubMeshes[i].MaterialSlot < 0) part.Mesh.SubMeshes[i] = part.Mesh.SubMeshes[i] with { MaterialSlot = slot };
        }
        return model;
    }

    private sealed class State
    {
        public State(Action<string>? warn) => Warn = warn;
        public Action<string>? Warn { get; }
        public List<MaterialData> Materials { get; } = new();
        public Dictionary<string, int> MaterialIndex { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool NeedsDefaultMaterial { get; set; }
    }

    private static uint ChunkLength(ReadOnlySpan<byte> d, int at) =>
        at + 6 <= d.Length ? BinaryPrimitives.ReadUInt32LittleEndian(d[(at + 2)..]) : 0;

    /// <summary>Iterates the chunks in [start, end): yields (id, body start, chunk end).</summary>
    private static bool NextChunk(ReadOnlySpan<byte> d, ref int at, int end, out ushort id, out int body, out int chunkEnd)
    {
        id = 0;
        body = chunkEnd = 0;
        if (at + 6 > end) return false;
        id = BinaryPrimitives.ReadUInt16LittleEndian(d[at..]);
        uint len = BinaryPrimitives.ReadUInt32LittleEndian(d[(at + 2)..]);
        if (len < 6 || at + (long)len > end) return false;
        body = at + 6;
        chunkEnd = at + (int)len;
        at = chunkEnd;
        return true;
    }

    private static void WalkMain(ReadOnlySpan<byte> d, int at, int end, State s, ModelData model)
    {
        while (NextChunk(d, ref at, end, out var id, out var body, out var chunkEnd))
            if (id == Editor) WalkEditor(d, body, chunkEnd, s, model);
    }

    private static void WalkEditor(ReadOnlySpan<byte> d, int at, int end, State s, ModelData model)
    {
        // Materials first (objects reference them by name and may come before them in the file).
        int scan = at;
        while (NextChunk(d, ref scan, end, out var id, out var body, out var chunkEnd))
            if (id == MaterialBlock) ReadMaterial(d, body, chunkEnd, s);
        while (NextChunk(d, ref at, end, out var id, out var body, out var chunkEnd))
        {
            if (id != NamedObject) continue;
            int nameEnd = ReadCString(d, body, chunkEnd, out string objectName);
            int inner = nameEnd;
            while (NextChunk(d, ref inner, chunkEnd, out var cid, out var cbody, out var cend))
            {
                if (cid != TriMesh) continue;
                try
                {
                    var part = ReadTriMesh(d, cbody, cend, objectName, s);
                    if (part is not null) model.Parts.Add(part);
                }
                catch (Exception e) when (e is ArgumentOutOfRangeException or IndexOutOfRangeException or InvalidDataException)
                {
                    s.Warn?.Invoke($"3DS object '{objectName}' is malformed and was skipped ({e.Message}).");
                }
            }
        }
    }

    private static int ReadCString(ReadOnlySpan<byte> d, int at, int end, out string text)
    {
        if (at >= end)
        {
            text = "";
            return end;
        }
        int zero = d[at..end].IndexOf((byte)0);
        if (zero < 0)
        {
            text = Encoding.Latin1.GetString(d[at..end]);
            return end;
        }
        var raw = d.Slice(at, zero);
        text = IsUtf8(raw) ? Encoding.UTF8.GetString(raw) : Encoding.Latin1.GetString(raw);
        return at + zero + 1;
    }

    private static bool IsUtf8(ReadOnlySpan<byte> raw)
    {
        try
        {
            _ = new UTF8Encoding(false, true).GetCharCount(raw);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    // ---------------------------------------------------------------- materials

    private static void ReadMaterial(ReadOnlySpan<byte> d, int at, int end, State s)
    {
        string name = $"material{s.Materials.Count}";
        var mat = new MaterialData { Roughness = 0.6f };
        while (NextChunk(d, ref at, end, out var id, out var body, out var chunkEnd))
        {
            switch (id)
            {
                case MaterialName:
                    ReadCString(d, body, chunkEnd, out name);
                    break;
                case Diffuse:
                    if (ReadColor(d, body, chunkEnd) is { } c) mat.BaseColorFactor = new Vector4(c, mat.BaseColorFactor.W);
                    break;
                case Shininess:
                    if (ReadPercent(d, body, chunkEnd) is { } sh) mat.Roughness = System.Math.Clamp(1f - 0.9f * sh, 0.05f, 1f);
                    break;
                case Transparency:
                    if (ReadPercent(d, body, chunkEnd) is { } tr && tr > 0.001f)
                    {
                        float alpha = System.Math.Clamp(1f - tr, 0f, 1f);
                        mat.BaseColorFactor = mat.BaseColorFactor with { W = alpha };
                        mat.AlphaMode = AlphaMode.Blend;
                    }
                    break;
                case TwoSided:
                    mat.DoubleSided = true;
                    break;
                case TextureMap:
                    ReadTextureMap(d, body, chunkEnd, mat);
                    break;
            }
        }
        mat.Name = name;
        if (!s.MaterialIndex.ContainsKey(name)) s.MaterialIndex[name] = s.Materials.Count;
        s.Materials.Add(mat);
    }

    private static void ReadTextureMap(ReadOnlySpan<byte> d, int at, int end, MaterialData mat)
    {
        var uv = mat.UvScale;
        while (NextChunk(d, ref at, end, out var id, out var body, out var chunkEnd))
        {
            if (id == MapFileName)
            {
                ReadCString(d, body, chunkEnd, out var file);
                if (!string.IsNullOrWhiteSpace(file)) mat.BaseColorTexture = file; // resolved to a key by the model loader
            }
            else if (id == MapUScale && chunkEnd - body >= 4)
            {
                uv.X = BinaryPrimitives.ReadSingleLittleEndian(d[body..]);
            }
            else if (id == MapVScale && chunkEnd - body >= 4)
            {
                uv.Y = BinaryPrimitives.ReadSingleLittleEndian(d[body..]);
            }
        }
        if (float.IsFinite(uv.X) && float.IsFinite(uv.Y) && uv.X != 0 && uv.Y != 0) mat.UvScale = uv;
    }

    /// <summary>First colour sub-chunk as linear RGB (gamma-encoded 3DS colours are decoded with the sRGB curve).</summary>
    private static Vector3? ReadColor(ReadOnlySpan<byte> d, int at, int end)
    {
        Vector3? gamma = null, linear = null;
        while (NextChunk(d, ref at, end, out var id, out var body, out var chunkEnd))
        {
            int n = chunkEnd - body;
            switch (id)
            {
                case Color24 when n >= 3: gamma ??= new Vector3(d[body], d[body + 1], d[body + 2]) / 255f; break;
                case ColorF when n >= 12: gamma ??= ReadVec3(d, body); break;
                case LinColor24 when n >= 3: linear ??= new Vector3(d[body], d[body + 1], d[body + 2]) / 255f; break;
                case LinColorF when n >= 12: linear ??= ReadVec3(d, body); break;
            }
        }
        if (linear is { } l && IsFinite(l)) return Vector3.Clamp(l, Vector3.Zero, Vector3.One);
        if (gamma is { } g && IsFinite(g))
        {
            g = Vector3.Clamp(g, Vector3.Zero, Vector3.One);
            return new Vector3(SrgbToLinear(g.X), SrgbToLinear(g.Y), SrgbToLinear(g.Z));
        }
        return null;
    }

    private static float? ReadPercent(ReadOnlySpan<byte> d, int at, int end)
    {
        while (NextChunk(d, ref at, end, out var id, out var body, out var chunkEnd))
        {
            int n = chunkEnd - body;
            if (id == PercentInt && n >= 2) return System.Math.Clamp(BinaryPrimitives.ReadInt16LittleEndian(d[body..]) / 100f, 0f, 1f);
            if (id == PercentFloat && n >= 4)
            {
                float f = BinaryPrimitives.ReadSingleLittleEndian(d[body..]);
                if (float.IsFinite(f)) return System.Math.Clamp(f / 100f, 0f, 1f);
            }
        }
        return null;
    }

    internal static float SrgbToLinear(float c) => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);

    internal static MaterialData DefaultMaterial() =>
        new() { Name = "default", BaseColorFactor = new Vector4(0.18f, 0.18f, 0.18f, 1f), Roughness = 0.6f };

    // ---------------------------------------------------------------- meshes

    private static Vector3 ReadVec3(ReadOnlySpan<byte> d, int at) => new(
        BinaryPrimitives.ReadSingleLittleEndian(d[at..]),
        BinaryPrimitives.ReadSingleLittleEndian(d[(at + 4)..]),
        BinaryPrimitives.ReadSingleLittleEndian(d[(at + 8)..]));

    private static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    private static ModelPart? ReadTriMesh(ReadOnlySpan<byte> d, int at, int end, string objectName, State s)
    {
        Vector3[]? vertices = null;
        Vector2[]? uvs = null;
        int[]? faces = null; // 3 per face
        uint[]? smoothing = null;
        int[]? faceMaterial = null; // material slot per face, -1 = default
        int faceCount = 0;

        while (NextChunk(d, ref at, end, out var id, out var body, out var chunkEnd))
        {
            switch (id)
            {
                case VertexList:
                {
                    int n = BinaryPrimitives.ReadUInt16LittleEndian(d[body..]);
                    n = System.Math.Min(n, (chunkEnd - body - 2) / 12);
                    vertices = new Vector3[n];
                    for (int i = 0; i < n; i++) vertices[i] = ReadVec3(d, body + 2 + 12 * i);
                    break;
                }
                case TexCoordList:
                {
                    int n = BinaryPrimitives.ReadUInt16LittleEndian(d[body..]);
                    n = System.Math.Min(n, (chunkEnd - body - 2) / 8);
                    uvs = new Vector2[n];
                    for (int i = 0; i < n; i++)
                    {
                        float u = BinaryPrimitives.ReadSingleLittleEndian(d[(body + 2 + 8 * i)..]);
                        float v = BinaryPrimitives.ReadSingleLittleEndian(d[(body + 6 + 8 * i)..]);
                        uvs[i] = new Vector2(float.IsFinite(u) ? u : 0, float.IsFinite(v) ? 1f - v : 0);
                    }
                    break;
                }
                case FaceList:
                {
                    faceCount = BinaryPrimitives.ReadUInt16LittleEndian(d[body..]);
                    faceCount = System.Math.Min(faceCount, (chunkEnd - body - 2) / 8);
                    faces = new int[faceCount * 3];
                    for (int i = 0; i < faceCount; i++)
                    {
                        int p = body + 2 + 8 * i;
                        faces[3 * i] = BinaryPrimitives.ReadUInt16LittleEndian(d[p..]);
                        faces[3 * i + 1] = BinaryPrimitives.ReadUInt16LittleEndian(d[(p + 2)..]);
                        faces[3 * i + 2] = BinaryPrimitives.ReadUInt16LittleEndian(d[(p + 4)..]);
                    }
                    faceMaterial = new int[faceCount];
                    Array.Fill(faceMaterial, -1);
                    int sub = body + 2 + 8 * faceCount;
                    while (NextChunk(d, ref sub, chunkEnd, out var sid, out var sbody, out var send))
                    {
                        if (sid == FaceMaterial)
                        {
                            int p = ReadCString(d, sbody, send, out var matName);
                            if (p + 2 > send) continue;
                            int n = BinaryPrimitives.ReadUInt16LittleEndian(d[p..]);
                            n = System.Math.Min(n, (send - p - 2) / 2);
                            // Exporters often reference a material block they never wrote ("Material - 0");
                            // such faces simply get the default material.
                            if (!s.MaterialIndex.TryGetValue(matName, out int slot)) slot = -1;
                            for (int i = 0; i < n; i++)
                            {
                                int f = BinaryPrimitives.ReadUInt16LittleEndian(d[(p + 2 + 2 * i)..]);
                                if (f < faceCount) faceMaterial[f] = slot;
                            }
                        }
                        else if (sid == SmoothGroups)
                        {
                            int n = System.Math.Min(faceCount, (send - sbody) / 4);
                            smoothing = new uint[faceCount];
                            for (int i = 0; i < n; i++) smoothing[i] = BinaryPrimitives.ReadUInt32LittleEndian(d[(sbody + 4 * i)..]);
                        }
                    }
                    break;
                }
            }
        }

        if (vertices is null || faces is null || vertices.Length == 0 || faceCount == 0) return null;
        if (uvs is not null && uvs.Length != vertices.Length) uvs = null;
        var mesh = BuildMesh(vertices, uvs, faces, faceCount, smoothing, faceMaterial!, s, objectName);
        return mesh is null ? null : new ModelPart { Name = objectName, Mesh = mesh, Transform = Matrix4x4.Identity };
    }

    private static MeshData? BuildMesh(
        Vector3[] vertices, Vector2[]? uvs, int[] faces, int faceCount, uint[]? smoothing, int[] faceMaterial, State s, string objectName)
    {
        // Valid faces and their (area-weighted) normals.
        var valid = new bool[faceCount];
        var faceNormals = new Vector3[faceCount];
        int bad = 0;
        for (int f = 0; f < faceCount; f++)
        {
            int a = faces[3 * f], b = faces[3 * f + 1], c = faces[3 * f + 2];
            if (a >= vertices.Length || b >= vertices.Length || c >= vertices.Length) { bad++; continue; }
            if (a == b || b == c || a == c) continue;
            var n = Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]);
            if (!IsFinite(n) || n.LengthSquared() < 1e-30f) continue;
            valid[f] = true;
            faceNormals[f] = n;
        }
        if (bad > 0) s.Warn?.Invoke($"3DS object '{objectName}': {bad} face(s) with out-of-range vertex indices skipped.");

        // Vertex → faces adjacency (CSR).
        var start = new int[vertices.Length + 1];
        for (int f = 0; f < faceCount; f++)
            if (valid[f])
                for (int k = 0; k < 3; k++) start[faces[3 * f + k] + 1]++;
        for (int i = 0; i < vertices.Length; i++) start[i + 1] += start[i];
        var adjacency = new int[start[^1]];
        var fill = (int[])start.Clone();
        for (int f = 0; f < faceCount; f++)
            if (valid[f])
                for (int k = 0; k < 3; k++) adjacency[fill[faces[3 * f + k]]++] = f;

        float cosCrease = MathF.Cos(AutoSmoothAngleDegrees * MathF.PI / 180f);
        var unitNormals = new Vector3[faceCount];
        for (int f = 0; f < faceCount; f++) if (valid[f]) unitNormals[f] = Vector3.Normalize(faceNormals[f]);

        var positions = new List<Vector3>(vertices.Length);
        var normals = new List<Vector3>(vertices.Length);
        var texcoords = uvs is null ? null : new List<Vector2>(vertices.Length);
        var keyToIndex = new Dictionary<(int Vertex, long Key), int>(vertices.Length);
        var cornerIndex = new int[faceCount * 3];

        for (int f = 0; f < faceCount; f++)
        {
            if (!valid[f]) continue;
            uint group = smoothing?[f] ?? 0;
            for (int k = 0; k < 3; k++)
            {
                int v = faces[3 * f + k];
                Vector3 n;
                long key;
                if (smoothing is not null)
                {
                    if (group == 0)
                    {
                        n = unitNormals[f];
                        key = -1 - f; // flat: unique per face
                    }
                    else
                    {
                        n = Vector3.Zero;
                        for (int i = start[v]; i < start[v + 1]; i++)
                        {
                            int g = adjacency[i];
                            if ((smoothing[g] & group) != 0) n += faceNormals[g];
                        }
                        key = group;
                    }
                }
                else
                {
                    n = Vector3.Zero;
                    for (int i = start[v]; i < start[v + 1]; i++)
                    {
                        int g = adjacency[i];
                        if (Vector3.Dot(unitNormals[g], unitNormals[f]) >= cosCrease) n += faceNormals[g];
                    }
                    n = n.LengthSquared() > 1e-30f ? Vector3.Normalize(n) : unitNormals[f];
                    key = QuantizeNormal(n);
                }
                if (!keyToIndex.TryGetValue((v, key), out int index))
                {
                    index = positions.Count;
                    keyToIndex[(v, key)] = index;
                    positions.Add(vertices[v]);
                    normals.Add(n.LengthSquared() > 1e-30f ? Vector3.Normalize(n) : unitNormals[f]);
                    texcoords?.Add(uvs![v]);
                }
                cornerIndex[3 * f + k] = index;
            }
        }
        if (positions.Count == 0) return null;

        // Group faces by material into sub-meshes.
        const int defaultSlot = -1;
        var bySlot = new SortedDictionary<int, List<int>>();
        for (int f = 0; f < faceCount; f++)
        {
            if (!valid[f]) continue;
            int slot = faceMaterial[f] >= 0 ? faceMaterial[f] : defaultSlot;
            if (slot == defaultSlot) s.NeedsDefaultMaterial = true;
            if (!bySlot.TryGetValue(slot, out var list)) bySlot[slot] = list = new List<int>();
            list.Add(f);
        }
        var indices = new uint[bySlot.Values.Sum(l => l.Count) * 3];
        var subMeshes = new List<SubMesh>(bySlot.Count);
        int w = 0;
        foreach (var (slot, list) in bySlot)
        {
            int startIndex = w;
            foreach (int f in list)
            {
                indices[w++] = (uint)cornerIndex[3 * f];
                indices[w++] = (uint)cornerIndex[3 * f + 1];
                indices[w++] = (uint)cornerIndex[3 * f + 2];
            }
            subMeshes.Add(new SubMesh(startIndex, w - startIndex, slot));
        }

        return new MeshData
        {
            Name = objectName,
            Positions = positions.ToArray(),
            Normals = normals.ToArray(),
            TexCoords = texcoords?.ToArray(),
            Indices = indices,
            SubMeshes = subMeshes,
        };
    }

    private static long QuantizeNormal(Vector3 n)
    {
        long q(float c) => (long)MathF.Round((c + 1f) * 1000f) & 0x1FFFF;
        return (q(n.X) << 34) | (q(n.Y) << 17) | q(n.Z);
    }
}
