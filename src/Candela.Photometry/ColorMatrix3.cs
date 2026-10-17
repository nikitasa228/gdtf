using System.Globalization;
using System.Numerics;

namespace Candela.Photometry;

/// <summary>
/// A 3×3 colour matrix in the column-vector convention of colour science: <c>out = M · in</c>, i.e.
/// <c>out.X = M11·in.X + M12·in.Y + M13·in.Z</c>. (System.Numerics matrices use row vectors; see
/// <see cref="ToMatrix4x4"/> for the equivalent embedding.)
/// </summary>
public readonly struct ColorMatrix3 : IEquatable<ColorMatrix3>
{
    /// <summary>Row 1, column 1.</summary>
    public readonly float M11;

    /// <summary>Row 1, column 2.</summary>
    public readonly float M12;

    /// <summary>Row 1, column 3.</summary>
    public readonly float M13;

    /// <summary>Row 2, column 1.</summary>
    public readonly float M21;

    /// <summary>Row 2, column 2.</summary>
    public readonly float M22;

    /// <summary>Row 2, column 3.</summary>
    public readonly float M23;

    /// <summary>Row 3, column 1.</summary>
    public readonly float M31;

    /// <summary>Row 3, column 2.</summary>
    public readonly float M32;

    /// <summary>Row 3, column 3.</summary>
    public readonly float M33;

    /// <summary>Creates a matrix from its elements in row-major order.</summary>
    public ColorMatrix3(float m11, float m12, float m13, float m21, float m22, float m23, float m31, float m32, float m33)
    {
        M11 = m11;
        M12 = m12;
        M13 = m13;
        M21 = m21;
        M22 = m22;
        M23 = m23;
        M31 = m31;
        M32 = m32;
        M33 = m33;
    }

    /// <summary>The identity matrix.</summary>
    public static ColorMatrix3 Identity => new(1, 0, 0, 0, 1, 0, 0, 0, 1);

    /// <summary>First row (M11, M12, M13).</summary>
    public Vector3 Row1 => new(M11, M12, M13);

    /// <summary>Second row (M21, M22, M23). For an RGB→XYZ matrix these are the luminance coefficients.</summary>
    public Vector3 Row2 => new(M21, M22, M23);

    /// <summary>Third row (M31, M32, M33).</summary>
    public Vector3 Row3 => new(M31, M32, M33);

    /// <summary>Applies the matrix to a column vector: <c>M · v</c>.</summary>
    public Vector3 Transform(Vector3 v) => new(
        M11 * v.X + M12 * v.Y + M13 * v.Z,
        M21 * v.X + M22 * v.Y + M23 * v.Z,
        M31 * v.X + M32 * v.Y + M33 * v.Z);

    /// <summary>Matrix product <c>a · b</c> (apply <paramref name="b"/> first, then <paramref name="a"/>).</summary>
    public static ColorMatrix3 operator *(ColorMatrix3 a, ColorMatrix3 b) => FromDouble(Multiply(a.ToDouble(), b.ToDouble()));

    /// <summary>The inverse matrix, computed in double precision.</summary>
    /// <exception cref="InvalidOperationException">The matrix is singular.</exception>
    public ColorMatrix3 Inverse() => FromDouble(Invert(ToDouble()));

    /// <summary>
    /// The same linear map as a <see cref="Matrix4x4"/> for <see cref="Vector3.Transform(Vector3, Matrix4x4)"/>
    /// (row-vector convention, so the 3×3 block is transposed).
    /// </summary>
    public Matrix4x4 ToMatrix4x4() => new(
        M11, M21, M31, 0,
        M12, M22, M32, 0,
        M13, M23, M33, 0,
        0, 0, 0, 1);

    /// <inheritdoc/>
    public bool Equals(ColorMatrix3 other) =>
        M11.Equals(other.M11) && M12.Equals(other.M12) && M13.Equals(other.M13) &&
        M21.Equals(other.M21) && M22.Equals(other.M22) && M23.Equals(other.M23) &&
        M31.Equals(other.M31) && M32.Equals(other.M32) && M33.Equals(other.M33);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is ColorMatrix3 other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Row1, Row2, Row3);

    /// <summary>Equality of all elements.</summary>
    public static bool operator ==(ColorMatrix3 left, ColorMatrix3 right) => left.Equals(right);

    /// <summary>Inequality of any element.</summary>
    public static bool operator !=(ColorMatrix3 left, ColorMatrix3 right) => !left.Equals(right);

    /// <inheritdoc/>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"[{M11:F7} {M12:F7} {M13:F7}; {M21:F7} {M22:F7} {M23:F7}; {M31:F7} {M32:F7} {M33:F7}]");

    internal double[] ToDouble() => [M11, M12, M13, M21, M22, M23, M31, M32, M33];

    internal static ColorMatrix3 FromDouble(double[] m) => new(
        (float)m[0], (float)m[1], (float)m[2],
        (float)m[3], (float)m[4], (float)m[5],
        (float)m[6], (float)m[7], (float)m[8]);

    internal static double[] Multiply(double[] a, double[] b)
    {
        var r = new double[9];
        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 3; j++)
                r[i * 3 + j] = a[i * 3] * b[j] + a[i * 3 + 1] * b[3 + j] + a[i * 3 + 2] * b[6 + j];
        }

        return r;
    }

    internal static double[] Invert(double[] m)
    {
        double a = m[0], b = m[1], c = m[2], d = m[3], e = m[4], f = m[5], g = m[6], h = m[7], i = m[8];
        double c11 = e * i - f * h, c12 = -(d * i - f * g), c13 = d * h - e * g;
        double det = a * c11 + b * c12 + c * c13;
        if (det == 0 || !double.IsFinite(det))
            throw new InvalidOperationException("Colour matrix is singular.");
        double k = 1.0 / det;
        return new[]
        {
            c11 * k, -(b * i - c * h) * k, (b * f - c * e) * k,
            c12 * k, (a * i - c * g) * k, -(a * f - c * d) * k,
            c13 * k, -(a * h - b * g) * k, (a * e - b * d) * k,
        };
    }
}
