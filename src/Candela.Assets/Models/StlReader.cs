using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using Candela.Core.Geometry;

namespace Candela.Assets;

/// <summary>
/// Reader for STL (binary and ASCII) producing a <see cref="RawModel"/>. Assimp's STL reader is not used: it crashes the
/// process on a truncated ASCII file (a bare <c>solid name</c> line is enough). STL carries no units, materials or
/// shared vertices; triangles are welded by exact position and normals are regenerated with the smoothing angle (facet
/// normals in STL files are frequently zero or inconsistent, and winding is what the renderer uses). Z up by CAD
/// convention.
/// </summary>
internal static class StlReader
{
    private const int BinaryHeaderSize = 84;
    private const int BinaryTriangleSize = 50;

    public static RawModel Read(ImportContext ctx)
    {
        var data = ctx.Files.MainData;
        var corners = new List<Vector3>();
        int invalid;
        if (IsBinary(data, out uint count)) invalid = ReadBinary(data, count, corners);
        else if (LooksLikeAscii(data)) invalid = ReadAscii(data, corners, ctx);
        else if (data.Length >= BinaryHeaderSize + BinaryTriangleSize && LooksLikeBinaryTriangles(data))
        {
            // A binary file whose triangle count does not match its size: truncated (partial download) or a wrong count.
            uint available = (uint)((data.Length - BinaryHeaderSize) / BinaryTriangleSize);
            ctx.Warn(count > available
                ? $"The binary STL file is truncated: {available} of {count} triangles were read."
                : $"The binary STL file declares {count} triangles but holds {available}; all {available} were read.");
            invalid = ReadBinary(data, available, corners);
        }
        else throw new AssetImportException("The STL file is damaged: it is neither binary STL nor ASCII STL text.");

        if (invalid > 0) ctx.Warn($"{invalid} STL triangle(s) with invalid coordinates were skipped.");
        if (corners.Count == 0) throw new AssetImportException("The STL file contains no triangles.");

        // Weld identical positions (STL repeats every corner per facet).
        var index = new Dictionary<Vector3, uint>(corners.Count / 4);
        var positions = new List<Vector3>(corners.Count / 4);
        var indices = new uint[corners.Count];
        for (int i = 0; i < corners.Count; i++)
        {
            var p = corners[i];
            if (!index.TryGetValue(p, out uint v))
            {
                v = (uint)positions.Count;
                index[p] = v;
                positions.Add(p);
            }
            indices[i] = v;
        }
        var name = Path.GetFileNameWithoutExtension(ctx.Files.MainFile);
        var mesh = new MeshData { Name = name, Positions = positions.ToArray(), Indices = indices };
        MeshTools.RemoveInvalidTriangles(mesh);
        if (mesh.Indices.Length == 0) throw new AssetImportException("The STL file contains no valid triangles.");
        MeshTools.GenerateNormals(mesh, ctx.Options.SmoothingAngleDegrees);

        var raw = new RawModel { Name = name, UpAxis = SourceUpAxis.Z, UnitScale = 1f, UnitsKnown = false };
        raw.Parts.Add(new RawPart(name, mesh, Matrix4x4.Identity));
        return raw;
    }

    /// <summary>
    /// Binary when the size matches the triangle count exactly (many binary files start their header with "solid"
    /// too, so the size is the reliable test).
    /// </summary>
    private static bool IsBinary(byte[] data, out uint count)
    {
        count = 0;
        if (data.Length < BinaryHeaderSize) return false;
        count = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(80, 4));
        if ((long)count * BinaryTriangleSize + BinaryHeaderSize == data.Length) return true;
        // Some exporters append data after the triangles; accept a binary file that holds at least the declared
        // triangles when the header is clearly not ASCII.
        return !LooksLikeAscii(data) && count > 0 && (long)count * BinaryTriangleSize + BinaryHeaderSize <= data.Length;
    }

    /// <summary>
    /// True when the records after the header look like STL triangles: at least 90 % of the (up to 64) first records
    /// have finite coordinates below 10⁷ in magnitude. Random or foreign data almost never does (a random float lies in
    /// that range with a probability of about 18 %, and a record holds twelve).
    /// </summary>
    private static bool LooksLikeBinaryTriangles(byte[] data)
    {
        int records = System.Math.Min(64, (data.Length - BinaryHeaderSize) / BinaryTriangleSize), plausible = 0;
        for (int t = 0; t < records; t++)
        {
            var s = data.AsSpan(BinaryHeaderSize + t * BinaryTriangleSize, 48);
            bool ok = true;
            for (int k = 0; k < 12 && ok; k++)
            {
                float v = F(s, 4 * k);
                ok = float.IsFinite(v) && MathF.Abs(v) < 1e7f && (v == 0 || MathF.Abs(v) > 1e-30f);
            }
            if (ok) plausible++;
        }
        return records > 0 && plausible >= records * 0.9;
    }

    private static bool LooksLikeAscii(byte[] data)
    {
        var span = data.AsSpan();
        int i = 0;
        while (i < span.Length && span[i] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') i++;
        if (span.Length - i < 5 || !System.Text.Ascii.EqualsIgnoreCase(span.Slice(i, 5), "solid"u8)) return false;
        // Binary files may start with "solid" as well; ASCII has no control bytes in its first kilobyte.
        int end = System.Math.Min(span.Length, i + 1024);
        for (int k = i; k < end; k++)
            if (span[k] < 9 || span[k] is > 13 and < 32) return false;
        return true;
    }

    private static int ReadBinary(byte[] data, uint count, List<Vector3> corners)
    {
        int invalid = 0;
        corners.Capacity = (int)System.Math.Min((long)count * 3, int.MaxValue / 2);
        for (long t = 0; t < count; t++)
        {
            var s = data.AsSpan((int)(BinaryHeaderSize + t * BinaryTriangleSize + 12), 36);
            var a = new Vector3(F(s, 0), F(s, 4), F(s, 8));
            var b = new Vector3(F(s, 12), F(s, 16), F(s, 20));
            var c = new Vector3(F(s, 24), F(s, 28), F(s, 32));
            if (!MeshTools.IsFinite(a) || !MeshTools.IsFinite(b) || !MeshTools.IsFinite(c))
            {
                invalid++;
                continue;
            }
            corners.Add(a);
            corners.Add(b);
            corners.Add(c);
        }
        return invalid;
    }

    private static float F(ReadOnlySpan<byte> s, int offset) => BinaryPrimitives.ReadSingleLittleEndian(s.Slice(offset, 4));

    /// <summary>
    /// Reads every <c>facet … outer loop vertex … endloop</c>; polygons with more than three vertices are
    /// fan-triangulated, incomplete facets (a truncated file) are dropped with a warning.
    /// </summary>
    private static int ReadAscii(byte[] data, List<Vector3> corners, ImportContext ctx)
    {
        int invalid = 0;
        var loop = new List<Vector3>(4);
        bool inLoop = false, loopValid = true;
        var span = data.AsSpan();
        int pos = 0;
        while (true)
        {
            var token = Next(span, ref pos);
            if (token.IsEmpty) break;
            if (System.Text.Ascii.EqualsIgnoreCase(token, "vertex"u8))
            {
                float x = Number(span, ref pos, ref loopValid), y = Number(span, ref pos, ref loopValid), z = Number(span, ref pos, ref loopValid);
                if (inLoop) loop.Add(new Vector3(x, y, z));
            }
            else if (System.Text.Ascii.EqualsIgnoreCase(token, "loop"u8))
            {
                inLoop = true;
                loopValid = true;
                loop.Clear();
            }
            else if (System.Text.Ascii.EqualsIgnoreCase(token, "endloop"u8))
            {
                if (inLoop && loop.Count >= 3)
                {
                    if (!loopValid) invalid++;
                    else
                    {
                        for (int k = 1; k + 1 < loop.Count; k++)
                        {
                            corners.Add(loop[0]);
                            corners.Add(loop[k]);
                            corners.Add(loop[k + 1]);
                        }
                    }
                }
                inLoop = false;
                loop.Clear();
            }
            // facet, normal (its three numbers are skipped as unknown tokens), outer, endfacet, solid, endsolid and names
            // carry nothing Candela needs.
        }
        if (inLoop)
        {
            ctx.Warn("The STL file ends in the middle of a facet (it is probably truncated); the incomplete facet was skipped.");
        }
        return invalid;
    }

    private static float Number(ReadOnlySpan<byte> span, ref int pos, ref bool valid)
    {
        int save = pos;
        var token = Next(span, ref pos);
        if (float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) && float.IsFinite(v)) return v;
        valid = false;
        // Leave a keyword where it is so the next iteration sees it ("vertex 1 2" followed by "endloop").
        if (!token.IsEmpty && !IsNumberLike(token)) pos = save;
        return 0f;
    }

    private static bool IsNumberLike(ReadOnlySpan<byte> token) => token[0] is (byte)'-' or (byte)'+' or (byte)'.' or >= (byte)'0' and <= (byte)'9';

    private static ReadOnlySpan<byte> Next(ReadOnlySpan<byte> span, ref int pos)
    {
        while (pos < span.Length && span[pos] <= (byte)' ') pos++;
        int start = pos;
        while (pos < span.Length && span[pos] > (byte)' ') pos++;
        return span[start..pos];
    }
}
