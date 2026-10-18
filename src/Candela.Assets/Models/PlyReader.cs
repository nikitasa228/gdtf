using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;
using Candela.Core.Geometry;

namespace Candela.Assets;

/// <summary>
/// Reader for Stanford PLY (ASCII, binary little- and big-endian) producing a <see cref="RawModel"/>. Assimp's PLY reader
/// is not used: it hangs or crashes the process on truncated and damaged files, which are common for scans and exports.
/// Every count and index is validated against the data actually present before anything is allocated, so a damaged file
/// ends in an <see cref="AssetImportException"/> or in warnings, never in a crash.
/// <list type="bullet">
/// <item>Vertex properties: <c>x y z</c> (required), <c>nx ny nz</c>, texture coordinates <c>s t</c> / <c>u v</c> /
/// <c>texture_u texture_v</c> / <c>texture_s texture_t</c>; colours and unknown properties are skipped.</item>
/// <item>Faces: the list <c>vertex_indices</c> (or <c>vertex_index</c>), polygons fan-triangulated; a per-corner
/// <c>texcoord</c> list (MeshLab) is honoured by unwelding the corners.</item>
/// <item>Other elements (edges, materials, …) are skipped. PLY has no units or materials: Z up, metres (or guessed).</item>
/// </list>
/// </summary>
internal static class PlyReader
{
    private const int MaxHeaderBytes = 1 << 16;
    private const int MaxPolygonCorners = 1 << 16;

    private enum Encoding
    {
        Ascii,
        BinaryLittleEndian,
        BinaryBigEndian,
    }

    private enum ScalarType
    {
        Int8,
        UInt8,
        Int16,
        UInt16,
        Int32,
        UInt32,
        Float32,
        Float64,
    }

    private sealed record Property(string Name, ScalarType Type, bool IsList, ScalarType CountType);

    private sealed class Element
    {
        public Element(string name, long count)
        {
            Name = name;
            Count = count;
        }

        public string Name { get; }
        public long Count { get; }
        public List<Property> Properties { get; } = new();
    }

    public static RawModel Read(ImportContext ctx)
    {
        var data = ctx.Files.MainData;
        var (encoding, elements, bodyStart) = ParseHeader(data);
        var body = new BodyReader(data, bodyStart, encoding);

        var positions = Array.Empty<Vector3>();
        Vector3[]? normals = null;
        Vector2[]? uvs = null;
        var indices = new List<uint>();
        List<Vector2>? cornerUvs = null;
        bool vertexSeen = false, faceSeen = false;
        int skippedFaces = 0, invalidVertices = 0;

        foreach (var element in elements)
        {
            if (element.Name == "vertex" && !vertexSeen)
            {
                vertexSeen = true;
                ReadVertices(body, element, out positions, out normals, out uvs, ref invalidVertices);
            }
            else if (element.Name == "face" && !faceSeen)
            {
                faceSeen = true;
                ReadFaces(body, element, positions.Length, indices, ref cornerUvs, ref skippedFaces);
            }
            else
            {
                for (long i = 0; i < element.Count; i++)
                    foreach (var p in element.Properties) body.Skip(p);
            }
        }

        if (!vertexSeen || positions.Length == 0) throw new AssetImportException("The PLY file contains no vertices.");
        if (skippedFaces > 0) ctx.Warn($"{skippedFaces} PLY face(s) with invalid vertex indices were skipped.");
        if (invalidVertices > 0) ctx.Warn($"{invalidVertices} PLY vertices with invalid coordinates were ignored.");
        if (indices.Count == 0) throw new AssetImportException("The PLY file contains no faces (point clouds cannot be imported as surfaces).");

        var mesh = new MeshData { Name = Path.GetFileNameWithoutExtension(ctx.Files.MainFile), Positions = positions, Indices = indices.ToArray() };
        if (normals is not null) mesh.Normals = normals;
        if (uvs is not null) mesh.TexCoords = uvs;
        if (cornerUvs is not null) Unweld(mesh, cornerUvs);
        MeshTools.RemoveInvalidTriangles(mesh);
        if (mesh.Indices.Length == 0) throw new AssetImportException("The PLY file contains no valid triangles.");
        if (mesh.Normals.Length != mesh.Positions.Length) MeshTools.GenerateNormals(mesh, ctx.Options.SmoothingAngleDegrees);

        var raw = new RawModel { Name = mesh.Name, UpAxis = SourceUpAxis.Z, UnitScale = 1f, UnitsKnown = false };
        raw.Parts.Add(new RawPart(mesh.Name, mesh, Matrix4x4.Identity));
        return raw;
    }

    private static (Encoding Encoding, List<Element> Elements, int BodyStart) ParseHeader(byte[] data)
    {
        int limit = System.Math.Min(data.Length, MaxHeaderBytes);
        int end = -1, bodyStart = -1;
        ReadOnlySpan<byte> marker = "end_header"u8;
        for (int i = 0; i + marker.Length <= limit; i++)
        {
            if ((i == 0 || data[i - 1] == '\n') && data.AsSpan(i, marker.Length).SequenceEqual(marker))
            {
                end = i;
                int j = i + marker.Length;
                while (j < data.Length && data[j] is (byte)' ' or (byte)'\t' or (byte)'\r') j++;
                if (j < data.Length && data[j] == '\n') j++;
                bodyStart = j;
                break;
            }
        }
        if (data.Length < 4 || !data.AsSpan(0, 3).SequenceEqual("ply"u8) || data[3] is not ((byte)'\n' or (byte)'\r'))
            throw new AssetImportException("The file is not a PLY file (it does not start with 'ply').");
        if (end < 0) throw new AssetImportException("The PLY header is incomplete (no 'end_header'); the file is damaged or truncated.");

        var header = System.Text.Encoding.ASCII.GetString(data, 0, end);
        Encoding? encoding = null;
        var elements = new List<Element>();
        foreach (var rawLine in header.Split('\n'))
        {
            var t = rawLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (t.Length == 0) continue;
            switch (t[0])
            {
                case "format":
                    if (t.Length < 2) throw Damaged("format line");
                    encoding = t[1] switch
                    {
                        "ascii" => Encoding.Ascii,
                        "binary_little_endian" => Encoding.BinaryLittleEndian,
                        "binary_big_endian" => Encoding.BinaryBigEndian,
                        _ => throw new AssetImportException($"Unknown PLY format '{t[1]}'."),
                    };
                    break;
                case "element":
                    if (t.Length < 3 || !long.TryParse(t[2], NumberStyles.None, CultureInfo.InvariantCulture, out long count))
                        throw Damaged("element line");
                    elements.Add(new Element(t[1], count));
                    break;
                case "property":
                    if (elements.Count == 0) throw Damaged("property before any element");
                    if (t.Length >= 5 && t[1] == "list")
                        elements[^1].Properties.Add(new Property(t[4], Scalar(t[3]), true, Scalar(t[2])));
                    else if (t.Length >= 3 && t[1] != "list")
                        elements[^1].Properties.Add(new Property(t[2], Scalar(t[1]), false, ScalarType.UInt8));
                    else throw Damaged("property line");
                    break;
            }
        }
        if (encoding is null) throw Damaged("missing format line");

        // Every element instance needs at least one byte per property (binary: the scalar size; ASCII: a digit and a
        // separator), so a count that cannot fit in the remaining data is rejected before anything is allocated.
        long remaining = data.Length - bodyStart;
        long minimum = 0;
        foreach (var e in elements)
        {
            long perItem = 0;
            foreach (var p in e.Properties)
                perItem += encoding == Encoding.Ascii ? 2 : Size(p.IsList ? p.CountType : p.Type);
            if (e.Count > 0 && perItem == 0) throw Damaged($"element '{e.Name}' has no properties");
            if (perItem > 0 && e.Count > (remaining - minimum) / perItem + (encoding == Encoding.Ascii ? 1 : 0))
                throw new AssetImportException($"The PLY file is truncated: it declares {e.Count} '{e.Name}' elements that are not in the file.");
            minimum += perItem * e.Count;
        }
        return (encoding.Value, elements, bodyStart);
    }

    private static void ReadVertices(BodyReader body, Element element, out Vector3[] positions, out Vector3[]? normals,
        out Vector2[]? uvs, ref int invalid)
    {
        int n = checked((int)element.Count);
        var props = element.Properties;
        int ix = props.FindIndex(p => p.Name == "x" && !p.IsList), iy = props.FindIndex(p => p.Name == "y" && !p.IsList);
        int iz = props.FindIndex(p => p.Name == "z" && !p.IsList);
        if (ix < 0 || iy < 0 || iz < 0) throw new AssetImportException("The PLY vertices have no x, y, z coordinates.");
        int inx = props.FindIndex(p => p.Name == "nx"), iny = props.FindIndex(p => p.Name == "ny"), inz = props.FindIndex(p => p.Name == "nz");
        bool hasNormals = inx >= 0 && iny >= 0 && inz >= 0;
        int iu = -1, iv = -1;
        foreach (var (u, v) in new[] { ("s", "t"), ("u", "v"), ("texture_u", "texture_v"), ("texture_s", "texture_t") })
        {
            iu = props.FindIndex(p => p.Name == u && !p.IsList);
            iv = props.FindIndex(p => p.Name == v && !p.IsList);
            if (iu >= 0 && iv >= 0) break;
        }
        bool hasUv = iu >= 0 && iv >= 0;

        positions = new Vector3[n];
        normals = hasNormals ? new Vector3[n] : null;
        uvs = hasUv ? new Vector2[n] : null;
        var values = new double[props.Count];
        bool normalsValid = hasNormals;
        for (int i = 0; i < n; i++)
        {
            for (int k = 0; k < props.Count; k++)
            {
                if (props[k].IsList) body.Skip(props[k]);
                else values[k] = body.Scalar(props[k].Type);
            }
            var pos = new Vector3((float)values[ix], (float)values[iy], (float)values[iz]);
            if (!MeshTools.IsFinite(pos))
            {
                invalid++;
                pos = new Vector3(float.NaN); // dropped with its triangles by RemoveInvalidTriangles
            }
            positions[i] = pos;
            if (normals is not null)
            {
                var nv = new Vector3((float)values[inx], (float)values[iny], (float)values[inz]);
                if (!MeshTools.IsFinite(nv) || nv.LengthSquared() < 1e-12f) normalsValid = false;
                else normals[i] = Vector3.Normalize(nv);
            }
            if (uvs is not null)
            {
                // PLY texture coordinates follow OpenGL (V up); Candela/glTF has V down.
                var uv = new Vector2((float)values[iu], 1f - (float)values[iv]);
                uvs[i] = float.IsFinite(uv.X) && float.IsFinite(uv.Y) ? uv : Vector2.Zero;
            }
        }
        if (!normalsValid) normals = null;
    }

    private static void ReadFaces(BodyReader body, Element element, int vertexCount, List<uint> indices, ref List<Vector2>? cornerUvs,
        ref int skipped)
    {
        var props = element.Properties;
        int iIdx = props.FindIndex(p => p.IsList && p.Name is "vertex_indices" or "vertex_index");
        if (iIdx < 0) throw new AssetImportException("The PLY faces have no vertex_indices list.");
        int iTex = props.FindIndex(p => p.IsList && p.Name == "texcoord");
        var corners = new List<long>(8);
        var texcoords = new List<double>(16);
        for (long f = 0; f < element.Count; f++)
        {
            corners.Clear();
            texcoords.Clear();
            for (int k = 0; k < props.Count; k++)
            {
                var p = props[k];
                if (k == iIdx) body.List(p, corners);
                else if (k == iTex) body.ListValues(p, texcoords);
                else body.Skip(p);
            }
            if (corners.Count < 3) continue;
            bool valid = true;
            foreach (var c in corners)
                if (c < 0 || c >= vertexCount) valid = false;
            if (!valid)
            {
                skipped++;
                continue;
            }
            bool withUv = iTex >= 0 && texcoords.Count == 2 * corners.Count;
            if (withUv && cornerUvs is null)
            {
                // Faces read so far had no per-corner UVs: give them zeros.
                cornerUvs = new List<Vector2>(indices.Count + 3);
                for (int i = 0; i < indices.Count; i++) cornerUvs.Add(Vector2.Zero);
            }
            for (int t = 1; t + 1 < corners.Count; t++)
            {
                indices.Add((uint)corners[0]);
                indices.Add((uint)corners[t]);
                indices.Add((uint)corners[t + 1]);
                if (cornerUvs is not null)
                {
                    cornerUvs.Add(withUv ? CornerUv(texcoords, 0) : Vector2.Zero);
                    cornerUvs.Add(withUv ? CornerUv(texcoords, t) : Vector2.Zero);
                    cornerUvs.Add(withUv ? CornerUv(texcoords, t + 1) : Vector2.Zero);
                }
            }
        }
    }

    private static Vector2 CornerUv(List<double> texcoords, int corner)
    {
        var uv = new Vector2((float)texcoords[2 * corner], 1f - (float)texcoords[2 * corner + 1]);
        return float.IsFinite(uv.X) && float.IsFinite(uv.Y) ? uv : Vector2.Zero;
    }

    /// <summary>Gives every triangle corner its own vertex so per-corner texture coordinates can be stored.</summary>
    private static void Unweld(MeshData mesh, List<Vector2> cornerUvs)
    {
        var idx = mesh.Indices;
        var pos = new Vector3[idx.Length];
        var nrm = mesh.Normals.Length == mesh.Positions.Length ? new Vector3[idx.Length] : null;
        var uv = new Vector2[idx.Length];
        var newIdx = new uint[idx.Length];
        for (int i = 0; i < idx.Length; i++)
        {
            pos[i] = mesh.Positions[idx[i]];
            if (nrm is not null) nrm[i] = mesh.Normals[idx[i]];
            uv[i] = i < cornerUvs.Count ? cornerUvs[i] : Vector2.Zero;
            newIdx[i] = (uint)i;
        }
        mesh.Positions = pos;
        mesh.Normals = nrm ?? Array.Empty<Vector3>();
        mesh.TexCoords = uv;
        mesh.Indices = newIdx;
    }

    private static ScalarType Scalar(string name) => name switch
    {
        "char" or "int8" => ScalarType.Int8,
        "uchar" or "uint8" => ScalarType.UInt8,
        "short" or "int16" => ScalarType.Int16,
        "ushort" or "uint16" => ScalarType.UInt16,
        "int" or "int32" => ScalarType.Int32,
        "uint" or "uint32" => ScalarType.UInt32,
        "float" or "float32" => ScalarType.Float32,
        "double" or "float64" => ScalarType.Float64,
        _ => throw new AssetImportException($"Unknown PLY property type '{name}'."),
    };

    private static int Size(ScalarType t) => t switch
    {
        ScalarType.Int8 or ScalarType.UInt8 => 1,
        ScalarType.Int16 or ScalarType.UInt16 => 2,
        ScalarType.Int32 or ScalarType.UInt32 or ScalarType.Float32 => 4,
        _ => 8,
    };

    private static AssetImportException Damaged(string what) => new($"The PLY header is damaged ({what}).");

    private static AssetImportException Truncated() => new("The PLY file is truncated or damaged: its data ends before all declared elements were read.");

    /// <summary>Sequential reader of the PLY body (ASCII tokens or binary scalars) with bounds checks.</summary>
    private sealed class BodyReader
    {
        private readonly byte[] _data;
        private readonly Encoding _encoding;
        private int _pos;

        public BodyReader(byte[] data, int start, Encoding encoding)
        {
            _data = data;
            _pos = start;
            _encoding = encoding;
        }

        public double Scalar(ScalarType type)
        {
            if (_encoding == Encoding.Ascii)
            {
                var token = NextToken();
                if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) throw Truncated();
                return v;
            }
            int size = Size(type);
            if (_pos > _data.Length - size) throw Truncated();
            var s = _data.AsSpan(_pos, size);
            _pos += size;
            bool le = _encoding == Encoding.BinaryLittleEndian;
            return type switch
            {
                ScalarType.Int8 => (sbyte)s[0],
                ScalarType.UInt8 => s[0],
                ScalarType.Int16 => le ? BinaryPrimitives.ReadInt16LittleEndian(s) : BinaryPrimitives.ReadInt16BigEndian(s),
                ScalarType.UInt16 => le ? BinaryPrimitives.ReadUInt16LittleEndian(s) : BinaryPrimitives.ReadUInt16BigEndian(s),
                ScalarType.Int32 => le ? BinaryPrimitives.ReadInt32LittleEndian(s) : BinaryPrimitives.ReadInt32BigEndian(s),
                ScalarType.UInt32 => le ? BinaryPrimitives.ReadUInt32LittleEndian(s) : BinaryPrimitives.ReadUInt32BigEndian(s),
                ScalarType.Float32 => le ? BinaryPrimitives.ReadSingleLittleEndian(s) : BinaryPrimitives.ReadSingleBigEndian(s),
                _ => le ? BinaryPrimitives.ReadDoubleLittleEndian(s) : BinaryPrimitives.ReadDoubleBigEndian(s),
            };
        }

        /// <summary>Reads a list property as integers (vertex indices).</summary>
        public void List(Property p, List<long> values)
        {
            int count = ListCount(p);
            for (int i = 0; i < count; i++)
            {
                double v = Scalar(p.Type);
                values.Add(double.IsFinite(v) && v >= long.MinValue && v <= long.MaxValue ? (long)v : -1);
            }
        }

        /// <summary>Reads a list property as numbers.</summary>
        public void ListValues(Property p, List<double> values)
        {
            int count = ListCount(p);
            for (int i = 0; i < count; i++) values.Add(Scalar(p.Type));
        }

        public void Skip(Property p)
        {
            if (!p.IsList)
            {
                SkipScalar(p.Type);
                return;
            }
            int count = ListCount(p);
            for (int i = 0; i < count; i++) SkipScalar(p.Type);
        }

        private void SkipScalar(ScalarType type)
        {
            if (_encoding == Encoding.Ascii)
            {
                NextToken();
                return;
            }
            int size = Size(type);
            if (_pos > _data.Length - size) throw Truncated();
            _pos += size;
        }

        private int ListCount(Property p)
        {
            double c = Scalar(p.CountType);
            if (!(c >= 0) || c > MaxPolygonCorners) throw new AssetImportException($"The PLY file is damaged: a list of {c} entries.");
            int count = (int)c;
            // Each entry needs at least one byte (binary: its size), so a count beyond the data is rejected up front.
            long need = _encoding == Encoding.Ascii ? count : (long)count * Size(p.Type);
            if (need > _data.Length - _pos) throw Truncated();
            return count;
        }

        private ReadOnlySpan<byte> NextToken()
        {
            while (_pos < _data.Length && _data[_pos] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') _pos++;
            int start = _pos;
            while (_pos < _data.Length && _data[_pos] is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')) _pos++;
            if (_pos == start) throw Truncated();
            return _data.AsSpan(start, _pos - start);
        }
    }
}
