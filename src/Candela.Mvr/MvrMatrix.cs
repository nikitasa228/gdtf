using System.Globalization;
using System.Numerics;
using System.Text;
using Candela.Core.Math;

namespace Candela.Mvr;

/// <summary>
/// Conversion between MVR matrix strings and System.Numerics, and decomposition into Core transforms.
/// </summary>
/// <remarks>
/// <para>
/// MVR writes a transform as four groups of three numbers, <c>{ux,uy,uz}{vx,vy,vz}{wx,wy,wz}{ox,oy,oz}</c>: the images
/// of the local X, Y and Z axes followed by the origin, in <b>millimetres</b>. A local point maps to
/// <c>p' = px·u + py·v + pz·w + o</c>, which is exactly the System.Numerics row-vector convention, so u, v, w become
/// rows 1–3 and o (divided by 1000) row 4 (M41…M43). Child transforms are relative to their parent and compose as
/// <c>world = local * parentWorld</c>, the same as Core entities.
/// </para>
/// <para>
/// Tolerated variants: a 4×4 matrix written as four groups of four numbers is accepted either as rows (last column
/// 0,0,0,1) or as the GDTF column-vector layout (last row 0,0,0,1), and is converted accordingly.
/// </para>
/// </remarks>
public static class MvrMatrix
{
    /// <summary>MVR length unit (millimetre) in metres.</summary>
    public const double MetresPerMillimetre = 0.001;

    /// <summary>Scale factors this close to ±1 are snapped to exactly ±1 (float noise in exported files).</summary>
    public const float UnitScaleSnap = 1e-4f;

    /// <summary>Axes whose normalised dot products exceed this are reported as sheared.</summary>
    public const float ShearTolerance = 1e-3f;

    /// <summary>
    /// Parses an MVR matrix string into a row-vector matrix in metres. Returns false (with identity) for malformed text,
    /// a wrong number of values, or values that are not finite.
    /// </summary>
    public static bool TryParse(string? text, out Matrix4x4 matrix)
    {
        matrix = Matrix4x4.Identity;
        if (string.IsNullOrWhiteSpace(text)) return false;
        Span<double> v = stackalloc double[16];
        Span<int> sizes = stackalloc int[4];
        if (!TryReadGroups(text, v, sizes, out int groups, out int count)) return false;

        Matrix4x4 m;
        if (groups == 4 && count == 12 && sizes[0] == 3 && sizes[1] == 3 && sizes[2] == 3 && sizes[3] == 3)
        {
            m = Create(v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8], v[9], v[10], v[11]);
        }
        else if (groups == 4 && count == 16 && sizes[0] == 4 && sizes[1] == 4 && sizes[2] == 4 && sizes[3] == 4 &&
                 v[3] == 0 && v[7] == 0 && v[11] == 0 && v[15] == 1)
        {
            m = Create(v[0], v[1], v[2], v[4], v[5], v[6], v[8], v[9], v[10], v[12], v[13], v[14]);
        }
        else if (groups == 4 && count == 16 && sizes[0] == 4 && sizes[1] == 4 && sizes[2] == 4 && sizes[3] == 4 &&
                 v[12] == 0 && v[13] == 0 && v[14] == 0 && v[15] == 1)
        {
            m = Create(v[0], v[4], v[8], v[1], v[5], v[9], v[2], v[6], v[10], v[3], v[7], v[11]);
        }
        else
        {
            return false;
        }
        // Values that are finite as doubles can still overflow single precision (e.g. 1e300 mm).
        if (!IsFinite(m)) return false;
        matrix = m;
        return true;
    }

    /// <summary>True when every element of the affine part is finite.</summary>
    internal static bool IsFinite(Matrix4x4 m) =>
        float.IsFinite(m.M11) && float.IsFinite(m.M12) && float.IsFinite(m.M13) &&
        float.IsFinite(m.M21) && float.IsFinite(m.M22) && float.IsFinite(m.M23) &&
        float.IsFinite(m.M31) && float.IsFinite(m.M32) && float.IsFinite(m.M33) &&
        float.IsFinite(m.M41) && float.IsFinite(m.M42) && float.IsFinite(m.M43);

    /// <summary>
    /// Formats a row-vector matrix (metres) as an MVR matrix string in millimetres. Rotation values use the shortest
    /// round-trip float text and translations are the metre values shifted by three decimal places, so
    /// <see cref="TryParse"/> gives back the identical float matrix and output is deterministic.
    /// </summary>
    public static string Format(Matrix4x4 m)
    {
        var sb = new StringBuilder(160);
        sb.Append('{').Append(F(m.M11)).Append(',').Append(F(m.M12)).Append(',').Append(F(m.M13)).Append('}');
        sb.Append('{').Append(F(m.M21)).Append(',').Append(F(m.M22)).Append(',').Append(F(m.M23)).Append('}');
        sb.Append('{').Append(F(m.M31)).Append(',').Append(F(m.M32)).Append(',').Append(F(m.M33)).Append('}');
        sb.Append('{').Append(Millimetres(m.M41)).Append(',').Append(Millimetres(m.M42)).Append(',').Append(Millimetres(m.M43)).Append('}');
        return sb.ToString();
    }

    /// <summary>True when every element differs from the identity by at most <paramref name="tolerance"/> (translation in metres).</summary>
    public static bool IsIdentity(Matrix4x4 m, float tolerance = 1e-6f) =>
        MathF.Abs(m.M11 - 1) <= tolerance && MathF.Abs(m.M12) <= tolerance && MathF.Abs(m.M13) <= tolerance &&
        MathF.Abs(m.M21) <= tolerance && MathF.Abs(m.M22 - 1) <= tolerance && MathF.Abs(m.M23) <= tolerance &&
        MathF.Abs(m.M31) <= tolerance && MathF.Abs(m.M32) <= tolerance && MathF.Abs(m.M33 - 1) <= tolerance &&
        MathF.Abs(m.M41) <= tolerance && MathF.Abs(m.M42) <= tolerance && MathF.Abs(m.M43) <= tolerance;

    /// <summary>Determinant of the upper-left 3×3 block (negative for mirror transforms).</summary>
    public static float Determinant3(Matrix4x4 m) =>
        Vector3.Dot(Vector3.Cross(new Vector3(m.M11, m.M12, m.M13), new Vector3(m.M21, m.M22, m.M23)), new Vector3(m.M31, m.M32, m.M33));

    /// <summary>
    /// Splits an affine matrix into a Core <see cref="Transform"/> (translation, proper rotation, per-axis scale).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rotation is rebuilt with Gram–Schmidt giving the local Z axis priority (the beam axis of GDTF fixtures), so
    /// the result is exactly orthonormal even when the file's axes carry float noise; shear that a TRS transform cannot
    /// hold is dropped and reported through <see cref="MvrDecomposition.Shear"/>. Scales within
    /// <see cref="UnitScaleSnap"/> of 1 are snapped to 1.
    /// </para>
    /// <para>
    /// Mirror matrices (determinant &lt; 0, written by some CAD tools for mirrored symbols) are kept exactly: the mirror
    /// becomes a negative X scale with a proper rotation (<c>u = −|u|·x</c>), so <see cref="Transform.ToMatrix"/>
    /// reproduces the original matrix, children keep their world positions and the beam axis keeps its direction.
    /// Degenerate matrices (a zero-length or coplanar axis) keep their translation with identity rotation and scale.
    /// </para>
    /// </remarks>
    public static MvrDecomposition Decompose(Matrix4x4 m)
    {
        var u = new Vector3(m.M11, m.M12, m.M13);
        var v = new Vector3(m.M21, m.M22, m.M23);
        var w = new Vector3(m.M31, m.M32, m.M33);
        var translation = new Vector3(m.M41, m.M42, m.M43);
        if (!float.IsFinite(translation.X) || !float.IsFinite(translation.Y) || !float.IsFinite(translation.Z)) translation = Vector3.Zero;

        float sx = u.Length(), sy = v.Length(), sz = w.Length();
        if (!(sx > 1e-9f && sy > 1e-9f && sz > 1e-9f) || !float.IsFinite(sx) || !float.IsFinite(sy) || !float.IsFinite(sz))
            return new MvrDecomposition(new Transform(translation), false, 0, true);

        float det = Vector3.Dot(Vector3.Cross(u, v), w);
        if (!(MathF.Abs(det) > 1e-6f * sx * sy * sz))
            return new MvrDecomposition(new Transform(translation), false, 0, true);

        bool mirrored = det < 0;
        var un = u / sx;
        var vn = v / sy;
        var wn = w / sz;
        float shear = MathF.Max(MathF.Abs(Vector3.Dot(un, vn)), MathF.Max(MathF.Abs(Vector3.Dot(un, wn)), MathF.Abs(Vector3.Dot(vn, wn))));

        var z = wn;
        var y = vn - Vector3.Dot(vn, z) * z;
        float yl = y.Length();
        if (!(yl > 1e-6f)) return new MvrDecomposition(new Transform(translation), false, shear, true);
        y /= yl;
        var x = Vector3.Cross(y, z);

        var rotation = new Matrix4x4(
            x.X, x.Y, x.Z, 0,
            y.X, y.Y, y.Z, 0,
            z.X, z.Y, z.Z, 0,
            0, 0, 0, 1);
        var q = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(rotation));
        var scale = new Vector3(Snap(sx), Snap(sy), Snap(sz));
        if (mirrored) scale.X = -scale.X;
        return new MvrDecomposition(new Transform(translation, q, scale), mirrored, shear, false);
    }

    /// <summary>Row-vector matrix from the twelve MVR values (axes unitless, origin in millimetres).</summary>
    internal static Matrix4x4 Create(
        double ux, double uy, double uz, double vx, double vy, double vz, double wx, double wy, double wz,
        double oxMm, double oyMm, double ozMm) =>
        new(
            (float)ux, (float)uy, (float)uz, 0,
            (float)vx, (float)vy, (float)vz, 0,
            (float)wx, (float)wy, (float)wz, 0,
            (float)(oxMm * MetresPerMillimetre), (float)(oyMm * MetresPerMillimetre), (float)(ozMm * MetresPerMillimetre), 1);

    private static float Snap(float s) => MathF.Abs(s - 1f) <= UnitScaleSnap ? 1f : s;

    private static string F(float value) => value == 0 ? "0" : value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>Metres → millimetre text by shifting the shortest round-trip decimal representation.</summary>
    internal static string Millimetres(float metres)
    {
        if (metres == 0 || !float.IsFinite(metres)) return "0";
        string r = metres.ToString("R", CultureInfo.InvariantCulture);
        if (decimal.TryParse(r, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
        {
            try
            {
                return (d * 1000m).ToString("0.############################", CultureInfo.InvariantCulture);
            }
            catch (OverflowException)
            {
                // fall through to double formatting
            }
        }
        return ((double)metres * 1000.0).ToString("R", CultureInfo.InvariantCulture);
    }

    private static bool TryReadGroups(string text, Span<double> values, Span<int> sizes, out int groups, out int count)
    {
        groups = 0;
        count = 0;
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }
            if (c != '{' || groups >= 4) return false;
            int close = text.IndexOf('}', i + 1);
            if (close < 0) return false;
            var inner = text.AsSpan(i + 1, close - i - 1);
            int size = 0;
            while (true)
            {
                int comma = inner.IndexOf(',');
                var part = (comma < 0 ? inner : inner[..comma]).Trim();
                if (count >= values.Length) return false;
                if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) || !double.IsFinite(d)) return false;
                values[count++] = d;
                size++;
                if (comma < 0) break;
                inner = inner[(comma + 1)..];
            }
            sizes[groups++] = size;
            i = close + 1;
        }
        return groups > 0;
    }
}

/// <summary>Result of <see cref="MvrMatrix.Decompose"/>.</summary>
/// <param name="Transform">Translation (metres), proper rotation and scale (negative X scale for mirrors).</param>
/// <param name="Mirrored">True when the matrix had a negative determinant (kept as a negative X scale).</param>
/// <param name="Shear">Largest absolute dot product between the normalised axes (0 for orthogonal axes); dropped.</param>
/// <param name="Degenerate">True when the axes were zero, not finite or coplanar; only the translation was kept.</param>
public readonly record struct MvrDecomposition(Transform Transform, bool Mirrored, float Shear, bool Degenerate)
{
    /// <summary>True when the axes were not orthogonal beyond <see cref="MvrMatrix.ShearTolerance"/>.</summary>
    public bool IsSheared => Shear > MvrMatrix.ShearTolerance;
}
