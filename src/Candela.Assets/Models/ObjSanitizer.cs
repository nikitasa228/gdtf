using System.Buffers;
using System.Buffers.Text;
using System.Globalization;

namespace Candela.Assets;

/// <summary>
/// Rewrites OBJ and MTL files into a strictly well-formed subset before Assimp parses them. Assimp's OBJ/MTL parsers
/// read past the end of their buffers on malformed statements (a face corner such as <c>2/</c>, a truncated last line)
/// and crash the whole process; a damaged download or a hand-edited file must only produce warnings. The rewrite keeps
/// everything Candela imports and drops what Assimp would mishandle:
/// <list type="bullet">
/// <item><c>v</c>/<c>vt</c>/<c>vn</c> with non-numeric or non-finite values become placeholders (so indices stay
/// valid); faces using an invalid vertex are dropped.</item>
/// <item>Face corners are validated (<c>v</c>, <c>v/t</c>, <c>v//n</c>, <c>v/t/n</c>, negative = relative), resolved to
/// absolute indices and written uniformly per face (a texture or normal index is kept only when every corner has a valid
/// one); faces with fewer than three valid corners are dropped. Points and lines (<c>p</c>, <c>l</c>) and free-form
/// geometry statements are removed — Candela imports surfaces only.</item>
/// <item>MTL: colours and scalars must be numeric; texture statements keep only well-formed options; <c>Tr</c>
/// (transparency) becomes <c>d = 1 − Tr</c> when the material has no <c>d</c> (Assimp ignores <c>Tr</c>).</item>
/// </list>
/// Line continuations (<c>\</c>) are joined and the output always ends with a newline. Names (objects, groups,
/// materials, texture paths) are copied byte for byte, so non-ASCII names still match between OBJ and MTL.
/// </summary>
internal static class ObjSanitizer
{
    /// <summary>Counts of what was dropped while sanitising, for warnings.</summary>
    internal sealed class Report
    {
        public int InvalidElements { get; set; }
        public int DroppedFaces { get; set; }
        public int DroppedLinesAndPoints { get; set; }
        public int DroppedStatements { get; set; }

        /// <summary>Faces written to the sanitised OBJ.</summary>
        public int Faces { get; set; }

        /// <summary>Named materials written to sanitised MTL files.</summary>
        public int Materials { get; set; }
    }

    private static readonly byte[][] s_passThroughObj =
    {
        "o"u8.ToArray(), "g"u8.ToArray(), "s"u8.ToArray(), "usemtl"u8.ToArray(), "mtllib"u8.ToArray(),
    };

    private static readonly byte[][] s_colourKeys =
    {
        "Ka"u8.ToArray(), "Kd"u8.ToArray(), "Ks"u8.ToArray(), "Ke"u8.ToArray(), "Tf"u8.ToArray(),
    };

    private static readonly byte[][] s_scalarKeys =
    {
        "Ns"u8.ToArray(), "Ni"u8.ToArray(), "illum"u8.ToArray(), "Pr"u8.ToArray(), "Pm"u8.ToArray(), "Ps"u8.ToArray(),
        "Pc"u8.ToArray(), "Pcr"u8.ToArray(), "aniso"u8.ToArray(), "anisor"u8.ToArray(), "sharpness"u8.ToArray(),
    };

    // Texture statements (matched case-insensitively) and the spelling Assimp's MTL reader understands.
    private static readonly (byte[] Alias, byte[] Canonical)[] s_textureKeys =
    {
        ("map_Ka"u8.ToArray(), "map_Ka"u8.ToArray()), ("map_Kd"u8.ToArray(), "map_Kd"u8.ToArray()),
        ("map_Ks"u8.ToArray(), "map_Ks"u8.ToArray()), ("map_Ke"u8.ToArray(), "map_Ke"u8.ToArray()),
        ("map_emissive"u8.ToArray(), "map_Ke"u8.ToArray()), ("map_Ns"u8.ToArray(), "map_ns"u8.ToArray()),
        ("map_d"u8.ToArray(), "map_d"u8.ToArray()), ("map_bump"u8.ToArray(), "map_bump"u8.ToArray()),
        ("bump"u8.ToArray(), "bump"u8.ToArray()), ("map_disp"u8.ToArray(), "map_disp"u8.ToArray()),
        ("disp"u8.ToArray(), "disp"u8.ToArray()), ("refl"u8.ToArray(), "refl"u8.ToArray()),
        ("map_refl"u8.ToArray(), "refl"u8.ToArray()), ("norm"u8.ToArray(), "map_Kn"u8.ToArray()),
        ("map_norm"u8.ToArray(), "map_Kn"u8.ToArray()), ("map_Kn"u8.ToArray(), "map_Kn"u8.ToArray()),
    };

    // Texture options and how many values follow them (−n: between 1 and n numbers).
    private static readonly (byte[] Name, int Values, bool Numeric)[] s_textureOptions =
    {
        ("-blendu"u8.ToArray(), 1, false), ("-blendv"u8.ToArray(), 1, false), ("-cc"u8.ToArray(), 1, false),
        ("-clamp"u8.ToArray(), 1, false), ("-imfchan"u8.ToArray(), 1, false), ("-type"u8.ToArray(), 1, false),
        ("-bm"u8.ToArray(), 1, true), ("-boost"u8.ToArray(), 1, true), ("-texres"u8.ToArray(), 1, true),
        ("-mm"u8.ToArray(), 2, true), ("-o"u8.ToArray(), -3, true), ("-s"u8.ToArray(), -3, true), ("-t"u8.ToArray(), -3, true),
    };

    /// <summary>Sanitised copy of an OBJ file.</summary>
    public static byte[] SanitizeObj(ReadOnlySpan<byte> data, Report report)
    {
        // First pass: validity of every vertex, texture coordinate and normal. Positive face indices may refer forward
        // (Assimp resolves them after reading the whole file); negative ones are relative to what precedes the face.
        var vertexValid = new List<bool>();
        var texValid = new List<bool>();
        var normalValid = new List<bool>();
        Span<double> numbers = stackalloc double[4];
        var scan = new LineReader(data);
        while (scan.TryNext(out var statement))
        {
            var rest = statement;
            var key = NextToken(ref rest);
            if (key.SequenceEqual("v"u8)) vertexValid.Add(ReadNumbers(rest, numbers, 3) >= 3);
            else if (key.SequenceEqual("vt"u8)) texValid.Add(ReadNumbers(rest, numbers, 2) >= 1);
            else if (key.SequenceEqual("vn"u8)) normalValid.Add(ReadNumbers(rest, numbers, 3) >= 3);
        }

        var output = new ArrayBufferWriter<byte>(data.Length + 64);
        int vertexCount = 0, texCount = 0, normalCount = 0;
        var corners = new List<(long V, long T, long N)>(8);
        Span<byte> buffer = stackalloc byte[64];
        var lines = new LineReader(data);
        while (lines.TryNext(out var line))
        {
            var rest = line;
            var key = NextToken(ref rest);
            if (key.IsEmpty || key[0] == (byte)'#') continue;
            if (key.SequenceEqual("v"u8))
            {
                int n = ReadNumbers(rest, numbers, 3);
                bool ok = n >= 3;
                vertexCount++;
                if (ok) WriteStatement(output, "v"u8, numbers[..3]);
                else
                {
                    report.InvalidElements++;
                    Write(output, "v 0 0 0\n"u8);
                }
            }
            else if (key.SequenceEqual("vt"u8))
            {
                int n = ReadNumbers(rest, numbers, 2);
                texCount++;
                if (n >= 1)
                {
                    if (n == 1) numbers[1] = 0;
                    WriteStatement(output, "vt"u8, numbers[..2]);
                }
                else
                {
                    report.InvalidElements++;
                    Write(output, "vt 0 0\n"u8);
                }
            }
            else if (key.SequenceEqual("vn"u8))
            {
                int n = ReadNumbers(rest, numbers, 3);
                normalCount++;
                if (n >= 3) WriteStatement(output, "vn"u8, numbers[..3]);
                else
                {
                    report.InvalidElements++;
                    Write(output, "vn 0 0 1\n"u8);
                }
            }
            else if (key.SequenceEqual("f"u8) || key.SequenceEqual("fo"u8))
            {
                if (!ReadFace(rest, vertexValid, vertexCount, texValid, texCount, normalValid, normalCount, corners))
                {
                    report.DroppedFaces++;
                    continue;
                }
                bool allT = true, allN = true;
                foreach (var c in corners)
                {
                    allT &= c.T > 0;
                    allN &= c.N > 0;
                }
                Write(output, "f"u8);
                foreach (var c in corners)
                {
                    int w = 0;
                    buffer[w++] = (byte)' ';
                    w += Format(c.V, buffer[w..]);
                    if (allT || allN) buffer[w++] = (byte)'/';
                    if (allT) w += Format(c.T, buffer[w..]);
                    if (allN)
                    {
                        buffer[w++] = (byte)'/';
                        w += Format(c.N, buffer[w..]);
                    }
                    Write(output, buffer[..w]);
                }
                Write(output, "\n"u8);
                report.Faces++;
            }
            else if (key.SequenceEqual("l"u8) || key.SequenceEqual("p"u8))
            {
                report.DroppedLinesAndPoints++;
            }
            else if (IsAny(key, s_passThroughObj))
            {
                // Names are copied verbatim; a statement without a name is dropped (Assimp mis-reads it).
                if (Trim(rest).IsEmpty) continue;
                Write(output, line);
                Write(output, "\n"u8);
            }
            else report.DroppedStatements++;
        }
        return output.WrittenSpan.ToArray();
    }

    /// <summary>Sanitised copy of an MTL file.</summary>
    public static byte[] SanitizeMtl(ReadOnlySpan<byte> data, Report report)
    {
        // First pass: which materials have their own dissolve (d) statement.
        var hasDissolve = new List<bool>();
        var scan = new LineReader(data);
        while (scan.TryNext(out var line))
        {
            var rest = line;
            var key = NextToken(ref rest);
            if (key.SequenceEqual("newmtl"u8)) hasDissolve.Add(false);
            else if (key.SequenceEqual("d"u8) && hasDissolve.Count > 0) hasDissolve[^1] = true;
        }

        var output = new ArrayBufferWriter<byte>(data.Length + 64);
        Span<double> numbers = stackalloc double[4];
        int material = -1;
        bool inMaterial = false;
        var lines = new LineReader(data);
        while (lines.TryNext(out var line))
        {
            var rest = line;
            var key = NextToken(ref rest);
            if (key.IsEmpty || key[0] == (byte)'#') continue;
            if (key.SequenceEqual("newmtl"u8))
            {
                material++;
                // A nameless material cannot be used by the OBJ; its statements are dropped with it.
                inMaterial = !Trim(rest).IsEmpty;
                if (!inMaterial)
                {
                    report.DroppedStatements++;
                    continue;
                }
                report.Materials++;
                Write(output, line);
                Write(output, "\n"u8);
            }
            else if (!inMaterial)
            {
                // Statements outside a material crash Assimp's MTL reader (it has no current material).
                report.DroppedStatements++;
            }
            else if (IsAny(key, s_colourKeys))
            {
                int n = ReadNumbers(rest, numbers, 3);
                if (n == 1) numbers[1] = numbers[2] = numbers[0];
                if (n is 1 or 3) WriteStatement(output, key, numbers[..3]);
                else report.DroppedStatements++;
            }
            else if (key.SequenceEqual("d"u8))
            {
                var value = rest;
                var first = NextToken(ref value);
                if (first.SequenceEqual("-halo"u8)) rest = value;
                if (ReadNumbers(rest, numbers, 1) == 1) WriteStatement(output, "d"u8, numbers[..1]);
                else report.DroppedStatements++;
            }
            else if (key.SequenceEqual("Tr"u8))
            {
                if (hasDissolve[material]) continue;
                if (ReadNumbers(rest, numbers, 1) == 1)
                {
                    numbers[0] = 1 - System.Math.Clamp(numbers[0], 0, 1);
                    WriteStatement(output, "d"u8, numbers[..1]);
                }
                else report.DroppedStatements++;
            }
            else if (IsAny(key, s_scalarKeys))
            {
                if (ReadNumbers(rest, numbers, 1) == 1) WriteStatement(output, key, numbers[..1]);
                else report.DroppedStatements++;
            }
            else if (TextureKey(key) is { } canonical)
            {
                if (!WriteTexture(output, canonical, rest)) report.DroppedStatements++;
            }
            else report.DroppedStatements++;
        }
        return output.WrittenSpan.ToArray();
    }

    /// <summary>Writes a texture statement with its well-formed options; false when no file name remains.</summary>
    private static bool WriteTexture(ArrayBufferWriter<byte> output, ReadOnlySpan<byte> key, ReadOnlySpan<byte> rest)
    {
        var options = new ArrayBufferWriter<byte>(64);
        Span<double> numbers = stackalloc double[3];
        while (true)
        {
            var probe = rest;
            var token = NextToken(ref probe);
            if (token.IsEmpty || token[0] != (byte)'-' || token.Length < 2 || IsNumber(token)) break;
            int option = -1;
            for (int i = 0; i < s_textureOptions.Length; i++)
            {
                if (token.SequenceEqual(s_textureOptions[i].Name))
                {
                    option = i;
                    break;
                }
            }
            rest = probe;
            if (option < 0) return false; // unknown option: its arity is unknown, so the path cannot be found reliably
            var (name, values, numeric) = s_textureOptions[option];
            if (!numeric)
            {
                var value = NextToken(ref rest);
                if (value.IsEmpty) return false;
                Write(options, " "u8);
                Write(options, name);
                Write(options, " "u8);
                Write(options, value);
                continue;
            }
            int max = System.Math.Abs(values), count = 0;
            while (count < max)
            {
                var peek = rest;
                var value = NextToken(ref peek);
                if (value.IsEmpty || !TryNumber(value, out numbers[count])) break;
                rest = peek;
                count++;
            }
            if (count == 0 || (values > 0 && count != values)) return false;
            Write(options, " "u8);
            Write(options, name);
            WriteNumbers(options, numbers[..count]);
        }
        var path = Trim(rest);
        if (path.IsEmpty) return false;
        Write(output, key);
        Write(output, options.WrittenSpan);
        Write(output, " "u8);
        Write(output, path);
        Write(output, "\n"u8);
        return true;
    }

    private static bool ReadFace(ReadOnlySpan<byte> rest, List<bool> vertexValid, int vertexCount, List<bool> texValid, int texCount,
        List<bool> normalValid, int normalCount, List<(long V, long T, long N)> corners)
    {
        corners.Clear();
        while (true)
        {
            var token = NextToken(ref rest);
            if (token.IsEmpty) break;
            int s1 = token.IndexOf((byte)'/');
            var vPart = s1 < 0 ? token : token[..s1];
            ReadOnlySpan<byte> tPart = token[..0], nPart = token[..0];
            if (s1 >= 0)
            {
                var after = token[(s1 + 1)..];
                int s2 = after.IndexOf((byte)'/');
                tPart = s2 < 0 ? after : after[..s2];
                if (s2 >= 0)
                {
                    nPart = after[(s2 + 1)..];
                    if (nPart.IndexOf((byte)'/') >= 0) return false;
                }
            }
            long v = Resolve(vPart, vertexValid, vertexCount);
            if (v <= 0) return false;
            corners.Add((v, tPart.IsEmpty ? 0 : Resolve(tPart, texValid, texCount), nPart.IsEmpty ? 0 : Resolve(nPart, normalValid, normalCount)));
        }
        return corners.Count >= 3;
    }

    /// <summary>
    /// 1-based absolute index of a reference to a valid element, or 0. Negative references count back from the
    /// <paramref name="preceding"/> elements defined before the face.
    /// </summary>
    private static long Resolve(ReadOnlySpan<byte> token, List<bool> valid, int preceding)
    {
        if (!Utf8Parser.TryParse(token, out long index, out int consumed) || consumed != token.Length || index == 0) return 0;
        long absolute = index > 0 ? index : preceding + 1 + index;
        if (absolute < 1 || absolute > valid.Count || !valid[(int)(absolute - 1)]) return 0;
        return absolute;
    }

    /// <summary>Reads up to <paramref name="max"/> finite numbers; stops at the first token that is not one.</summary>
    private static int ReadNumbers(ReadOnlySpan<byte> rest, Span<double> numbers, int max)
    {
        int n = 0;
        while (n < max)
        {
            var token = NextToken(ref rest);
            if (token.IsEmpty || !TryNumber(token, out numbers[n])) break;
            n++;
        }
        return n;
    }

    private static bool TryNumber(ReadOnlySpan<byte> token, out double value) =>
        double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value)
        && System.Math.Abs(value) < 1e30;

    private static bool IsNumber(ReadOnlySpan<byte> token) => TryNumber(token, out _);

    private static void WriteStatement(ArrayBufferWriter<byte> output, ReadOnlySpan<byte> key, ReadOnlySpan<double> values)
    {
        Write(output, key);
        WriteNumbers(output, values);
        Write(output, "\n"u8);
    }

    private static void WriteNumbers(ArrayBufferWriter<byte> output, ReadOnlySpan<double> values)
    {
        Span<byte> buffer = stackalloc byte[40];
        foreach (var v in values)
        {
            buffer[0] = (byte)' ';
            // "R" round-trips and never uses a decimal comma; exponents ("1E-05") are understood by Assimp.
            if (!v.TryFormat(buffer[1..], out int written, "R", CultureInfo.InvariantCulture)) written = 0;
            Write(output, buffer[..(written + 1)]);
        }
    }

    private static int Format(long value, Span<byte> destination) =>
        Utf8Formatter.TryFormat(value, destination, out int written) ? written : 0;

    private static void Write(ArrayBufferWriter<byte> output, ReadOnlySpan<byte> bytes) => output.Write(bytes);

    private static byte[]? TextureKey(ReadOnlySpan<byte> key)
    {
        foreach (var (alias, canonical) in s_textureKeys)
            if (System.Text.Ascii.EqualsIgnoreCase(key, alias)) return canonical;
        return null;
    }

    private static bool IsAny(ReadOnlySpan<byte> key, byte[][] keys)
    {
        foreach (var k in keys)
            if (key.SequenceEqual(k)) return true;
        return false;
    }

    private static ReadOnlySpan<byte> NextToken(ref ReadOnlySpan<byte> rest)
    {
        int i = 0;
        while (i < rest.Length && IsSpace(rest[i])) i++;
        int start = i;
        while (i < rest.Length && !IsSpace(rest[i])) i++;
        var token = rest[start..i];
        rest = rest[i..];
        return token;
    }

    private static ReadOnlySpan<byte> Trim(ReadOnlySpan<byte> s)
    {
        int a = 0, b = s.Length;
        while (a < b && IsSpace(s[a])) a++;
        while (b > a && IsSpace(s[b - 1])) b--;
        return s[a..b];
    }

    private static bool IsSpace(byte b) => b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\f' or (byte)'\v' or 0;

    /// <summary>
    /// Logical lines: split at LF, trimmed, empty lines skipped, backslash continuations joined (only a continued line
    /// allocates).
    /// </summary>
    private ref struct LineReader
    {
        private ReadOnlySpan<byte> _data;

        public LineReader(ReadOnlySpan<byte> data)
        {
            _data = data;
        }

        public bool TryNext(out ReadOnlySpan<byte> line)
        {
            while (!_data.IsEmpty)
            {
                var trimmed = Trim(TakeLine());
                if (trimmed.IsEmpty) continue;
                if (trimmed[^1] != (byte)'\\')
                {
                    line = trimmed;
                    return true;
                }
                var joined = new ArrayBufferWriter<byte>(trimmed.Length * 2);
                joined.Write(trimmed[..^1]);
                while (!_data.IsEmpty)
                {
                    var next = Trim(TakeLine());
                    bool more = !next.IsEmpty && next[^1] == (byte)'\\';
                    joined.Write(" "u8);
                    joined.Write(more ? next[..^1] : next);
                    if (!more) break;
                }
                line = Trim(joined.WrittenSpan);
                if (!line.IsEmpty) return true;
            }
            line = default;
            return false;
        }

        private ReadOnlySpan<byte> TakeLine()
        {
            int nl = _data.IndexOf((byte)'\n');
            var line = nl < 0 ? _data : _data[..nl];
            _data = nl < 0 ? ReadOnlySpan<byte>.Empty : _data[(nl + 1)..];
            return line;
        }
    }
}
