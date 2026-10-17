using System.Globalization;
using System.Numerics;
using System.Text;

namespace Candela.Gdtf;

/// <summary>
/// Conversion of GDTF matrix strings to System.Numerics.
/// </summary>
/// <remarks>
/// GDTF writes a 4×4 transform as four rows <c>{a,b,c,d}{e,f,g,h}{i,j,k,l}{m,n,o,p}</c> of a matrix that multiplies
/// column vectors (<c>p' = M·p</c>): the rotation/scale is the upper-left 3×3 block and the translation in metres is
/// the fourth column (d, h, l). System.Numerics multiplies row vectors (<c>p' = p·M</c>) with the translation in
/// M41…M43, so the GDTF matrix is transposed: M11 = a, M12 = e, M13 = i, M21 = b, …, M41 = d, M42 = h, M43 = l.
/// Composition then reads child-to-world: <c>world = local * parentWorld</c>. Verified on real fixtures: in the
/// Robe MegaPointe file the yoke sits at z = −0.087 m below the base and the head another −0.284 m below the yoke,
/// i.e. GDTF models fixtures hanging with the beam pointing along −Z.
/// </remarks>
public static class GdtfMatrix
{
    /// <summary>
    /// Parses a GDTF matrix string. Accepts 4×4 (<c>{…4}{…4}{…4}{…4}</c>), 3 rows of 4 (last row assumed 0,0,0,1) and the
    /// MVR-style 4 rows of 3 (<c>{u}{v}{w}{o}</c>, already row-vector layout). Returns false for anything else.
    /// </summary>
    public static bool TryParse(string? text, out Matrix4x4 matrix)
    {
        matrix = Matrix4x4.Identity;
        if (string.IsNullOrWhiteSpace(text)) return false;
        Span<float> v = stackalloc float[16];
        Span<int> groupSizes = stackalloc int[4];
        if (!TryReadGroups(text, v, groupSizes, out int groups, out int count)) return false;

        if (groups == 4 && count == 16 && Uniform(groupSizes, groups, 4))
        {
            matrix = FromGdtfRows(v);
            return true;
        }
        if (groups == 3 && count == 12 && Uniform(groupSizes, groups, 4))
        {
            Span<float> full = stackalloc float[16];
            v[..12].CopyTo(full);
            full[12] = 0;
            full[13] = 0;
            full[14] = 0;
            full[15] = 1;
            matrix = FromGdtfRows(full);
            return true;
        }
        if (groups == 4 && count == 12 && Uniform(groupSizes, groups, 3))
        {
            matrix = new Matrix4x4(
                v[0], v[1], v[2], 0,
                v[3], v[4], v[5], 0,
                v[6], v[7], v[8], 0,
                v[9], v[10], v[11], 1);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Parses a GDTF 3×3 rotation <c>{a,b,c}{d,e,f}{g,h,i}</c> (column-vector convention, like positions) into the
    /// rotation block of a row-vector <see cref="Matrix4x4"/> (transposed).
    /// </summary>
    public static bool TryParseRotation(string? text, out Matrix4x4 rotation, out float[] raw)
    {
        rotation = Matrix4x4.Identity;
        raw = new float[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 };
        if (string.IsNullOrWhiteSpace(text)) return false;
        Span<float> v = stackalloc float[16];
        Span<int> groupSizes = stackalloc int[4];
        if (!TryReadGroups(text, v, groupSizes, out int groups, out int count) || groups != 3 || count != 9 || !Uniform(groupSizes, groups, 3))
            return false;
        raw = v[..9].ToArray();
        rotation = new Matrix4x4(
            v[0], v[3], v[6], 0,
            v[1], v[4], v[7], 0,
            v[2], v[5], v[8], 0,
            0, 0, 0, 1);
        return true;
    }

    /// <summary>Builds the System.Numerics matrix from 16 numbers in GDTF row order (transposes).</summary>
    public static Matrix4x4 FromGdtfRows(ReadOnlySpan<float> rows)
    {
        if (rows.Length < 16) throw new ArgumentException("A GDTF 4x4 matrix has 16 numbers.", nameof(rows));
        return new Matrix4x4(
            rows[0], rows[4], rows[8], rows[12],
            rows[1], rows[5], rows[9], rows[13],
            rows[2], rows[6], rows[10], rows[14],
            rows[3], rows[7], rows[11], rows[15]);
    }

    /// <summary>Formats a System.Numerics matrix back into the GDTF text form (transposing again).</summary>
    public static string Format(Matrix4x4 m)
    {
        var sb = new StringBuilder(160);
        void Row(float a, float b, float c, float d) =>
            sb.Append(CultureInfo.InvariantCulture, $"{{{a:0.######},{b:0.######},{c:0.######},{d:0.######}}}");
        Row(m.M11, m.M21, m.M31, m.M41);
        Row(m.M12, m.M22, m.M32, m.M42);
        Row(m.M13, m.M23, m.M33, m.M43);
        Row(m.M14, m.M24, m.M34, m.M44);
        return sb.ToString();
    }

    private static bool Uniform(ReadOnlySpan<int> sizes, int groups, int size)
    {
        for (int i = 0; i < groups; i++)
            if (sizes[i] != size) return false;
        return true;
    }

    /// <summary>Reads up to 4 brace groups of up to 4 numbers each (16 total).</summary>
    private static bool TryReadGroups(string text, Span<float> values, Span<int> groupSizes, out int groups, out int count)
    {
        groups = 0;
        count = 0;
        var s = text.AsSpan();
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (char.IsWhiteSpace(c) || c == ',' || c == ';') { i++; continue; }
            if (c != '{') return false;
            int close = s[i..].IndexOf('}');
            if (close < 0 || groups >= 4) return false;
            var inner = s.Slice(i + 1, close - 1);
            int n = 0;
            while (!inner.IsEmpty)
            {
                int comma = inner.IndexOf(',');
                var part = (comma < 0 ? inner : inner[..comma]).Trim();
                inner = comma < 0 ? ReadOnlySpan<char>.Empty : inner[(comma + 1)..];
                if (part.IsEmpty) continue;
                if (count >= 16 || !float.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) || !float.IsFinite(f))
                    return false;
                values[count++] = f;
                n++;
            }
            groupSizes[groups++] = n;
            i += close + 1;
        }
        return groups > 0;
    }
}
