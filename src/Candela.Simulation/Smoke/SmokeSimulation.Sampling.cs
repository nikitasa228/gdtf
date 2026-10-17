using System.Numerics;
using System.Runtime.CompilerServices;

namespace Candela.Simulation;

public sealed partial class SmokeSimulation
{
    // Positions inside the sampling code are in grid units: g = (world − origin) / h, so cell (i, j, k) spans
    // [i, i + 1] × [j, j + 1] × [k, k + 1]. Cell-centred values sit at g = index + 0.5; the u faces at (i, j + ½, k + ½),
    // v faces at (i + ½, j, k + ½) and w faces at (i + ½, j + ½, k).

    /// <summary>
    /// Trilinear interpolation of a field with <paramref name="sx"/> × <paramref name="sy"/> × <paramref name="sz"/> samples
    /// (x fastest) at fractional sample coordinates; coordinates are clamped to the sample range (NaN → 0).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Trilinear(float[] f, int sx, int sy, int sz, float x, float y, float z)
    {
        x = ClampCoord(x, sx - 1);
        y = ClampCoord(y, sy - 1);
        z = ClampCoord(z, sz - 1);
        int i0 = (int)x, j0 = (int)y, k0 = (int)z;
        int i1 = i0 < sx - 1 ? i0 + 1 : i0;
        int j1 = j0 < sy - 1 ? j0 + 1 : j0;
        int k1 = k0 < sz - 1 ? k0 + 1 : k0;
        float fx = x - i0, fy = y - j0, fz = z - k0;
        int r00 = sx * (j0 + sy * k0), r10 = sx * (j1 + sy * k0), r01 = sx * (j0 + sy * k1), r11 = sx * (j1 + sy * k1);
        float c00 = f[r00 + i0] + (f[r00 + i1] - f[r00 + i0]) * fx;
        float c10 = f[r10 + i0] + (f[r10 + i1] - f[r10 + i0]) * fx;
        float c01 = f[r01 + i0] + (f[r01 + i1] - f[r01 + i0]) * fx;
        float c11 = f[r11 + i0] + (f[r11 + i1] - f[r11 + i0]) * fx;
        float c0 = c00 + (c10 - c00) * fy;
        float c1 = c01 + (c11 - c01) * fy;
        return c0 + (c1 - c0) * fz;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float ClampCoord(float v, int max) => v >= 0f ? (v <= max ? v : max) : 0f;

    /// <summary>Velocity (m/s) at grid position <paramref name="g"/> from the given staggered component arrays.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Vector3 SampleVelocity(float[] u, float[] v, float[] w, Vector3 g) => new(
        Trilinear(u, _nx + 1, _ny, _nz, g.X, g.Y - 0.5f, g.Z - 0.5f),
        Trilinear(v, _nx, _ny + 1, _nz, g.X - 0.5f, g.Y, g.Z - 0.5f),
        Trilinear(w, _nx, _ny, _nz + 1, g.X - 0.5f, g.Y - 0.5f, g.Z));

    private Vector3 SampleVelocity(Vector3 g) => SampleVelocity(_u, _v, _w, g);

    /// <summary>Cell-centred value at grid position <paramref name="g"/> for queries (no ambient test; solids ignored).</summary>
    private float SampleScalarPlain(float[] f, float x, float y, float z)
    {
        if (!_hasObstacles)
            return Trilinear(f, _nx, _ny, _nz, x, y, z);
        int ci = (int)ClampCoord(x + 0.5f, _nx - 1), cj = (int)ClampCoord(y + 0.5f, _ny - 1), ck = (int)ClampCoord(z + 0.5f, _nz - 1);
        if (_solid[CellIndex(ci, cj, ck)] != 0)
            return 0f;
        SampleTwo(f, f, new Vector3(x + 0.5f, y + 0.5f, z + 0.5f), out float a, out _, out _, out _, out _, out _);
        return a;
    }

    /// <summary>
    /// Samples two cell-centred fields at grid position <paramref name="g"/> with shared weights, returning the min and
    /// max of the contributing corners (the MacCormack limiter bounds). Outside an open face the air is ambient (0).
    /// Solid corners are skipped and the remaining weights renormalised, so smoke is neither pulled into walls nor
    /// diluted by them.
    /// </summary>
    private void SampleTwo(float[] a, float[] b, Vector3 g, out float va, out float minA, out float maxA,
        out float vb, out float minB, out float maxB)
    {
        if (g.X < _ambientLo.X || g.Y < _ambientLo.Y || g.Z < _ambientLo.Z
            || g.X > _ambientHi.X || g.Y > _ambientHi.Y || g.Z > _ambientHi.Z)
        {
            va = minA = maxA = vb = minB = maxB = 0f;
            return;
        }

        float x = ClampCoord(g.X - 0.5f, _nx - 1), y = ClampCoord(g.Y - 0.5f, _ny - 1), z = ClampCoord(g.Z - 0.5f, _nz - 1);
        int i0 = (int)x, j0 = (int)y, k0 = (int)z;
        int di = i0 < _nx - 1 ? 1 : 0, dj = j0 < _ny - 1 ? _nx : 0, dk = k0 < _nz - 1 ? _nx * _ny : 0;
        float fx = x - i0, fy = y - j0, fz = z - k0;
        int c000 = CellIndex(i0, j0, k0);
        int c100 = c000 + di, c010 = c000 + dj, c110 = c010 + di;
        int c001 = c000 + dk, c101 = c001 + di, c011 = c001 + dj, c111 = c011 + di;
        float gx = 1f - fx, gy = 1f - fy, gz = 1f - fz;
        float w000 = gx * gy * gz, w100 = fx * gy * gz, w010 = gx * fy * gz, w110 = fx * fy * gz;
        float w001 = gx * gy * fz, w101 = fx * gy * fz, w011 = gx * fy * fz, w111 = fx * fy * fz;

        if (_hasObstacles)
        {
            var s = _solid;
            if (s[c000] != 0) w000 = 0f;
            if (s[c100] != 0) w100 = 0f;
            if (s[c010] != 0) w010 = 0f;
            if (s[c110] != 0) w110 = 0f;
            if (s[c001] != 0) w001 = 0f;
            if (s[c101] != 0) w101 = 0f;
            if (s[c011] != 0) w011 = 0f;
            if (s[c111] != 0) w111 = 0f;
            float sum = w000 + w100 + w010 + w110 + w001 + w101 + w011 + w111;
            if (!(sum > 1e-6f))
            {
                va = minA = maxA = vb = minB = maxB = 0f;
                return;
            }

            float inv = 1f / sum;
            w000 *= inv; w100 *= inv; w010 *= inv; w110 *= inv;
            w001 *= inv; w101 *= inv; w011 *= inv; w111 *= inv;
            minA = minB = float.PositiveInfinity;
            maxA = maxB = float.NegativeInfinity;
            Bound(a, b, c000, s, ref minA, ref maxA, ref minB, ref maxB);
            Bound(a, b, c100, s, ref minA, ref maxA, ref minB, ref maxB);
            Bound(a, b, c010, s, ref minA, ref maxA, ref minB, ref maxB);
            Bound(a, b, c110, s, ref minA, ref maxA, ref minB, ref maxB);
            Bound(a, b, c001, s, ref minA, ref maxA, ref minB, ref maxB);
            Bound(a, b, c101, s, ref minA, ref maxA, ref minB, ref maxB);
            Bound(a, b, c011, s, ref minA, ref maxA, ref minB, ref maxB);
            Bound(a, b, c111, s, ref minA, ref maxA, ref minB, ref maxB);
        }
        else
        {
            minA = MathF.Min(MathF.Min(MathF.Min(a[c000], a[c100]), MathF.Min(a[c010], a[c110])),
                MathF.Min(MathF.Min(a[c001], a[c101]), MathF.Min(a[c011], a[c111])));
            maxA = MathF.Max(MathF.Max(MathF.Max(a[c000], a[c100]), MathF.Max(a[c010], a[c110])),
                MathF.Max(MathF.Max(a[c001], a[c101]), MathF.Max(a[c011], a[c111])));
            minB = MathF.Min(MathF.Min(MathF.Min(b[c000], b[c100]), MathF.Min(b[c010], b[c110])),
                MathF.Min(MathF.Min(b[c001], b[c101]), MathF.Min(b[c011], b[c111])));
            maxB = MathF.Max(MathF.Max(MathF.Max(b[c000], b[c100]), MathF.Max(b[c010], b[c110])),
                MathF.Max(MathF.Max(b[c001], b[c101]), MathF.Max(b[c011], b[c111])));
        }

        va = w000 * a[c000] + w100 * a[c100] + w010 * a[c010] + w110 * a[c110]
           + w001 * a[c001] + w101 * a[c101] + w011 * a[c011] + w111 * a[c111];
        vb = w000 * b[c000] + w100 * b[c100] + w010 * b[c010] + w110 * b[c110]
           + w001 * b[c001] + w101 * b[c101] + w011 * b[c011] + w111 * b[c111];

        static void Bound(float[] a, float[] b, int c, byte[] solid, ref float minA, ref float maxA, ref float minB, ref float maxB)
        {
            if (solid[c] != 0)
                return;
            minA = MathF.Min(minA, a[c]);
            maxA = MathF.Max(maxA, a[c]);
            minB = MathF.Min(minB, b[c]);
            maxB = MathF.Max(maxB, b[c]);
        }
    }
}
