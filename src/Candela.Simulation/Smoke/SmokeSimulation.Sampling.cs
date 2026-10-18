using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

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
        int di = i0 < sx - 1 ? 1 : 0;
        int dj = j0 < sy - 1 ? sx : 0;
        int dk = k0 < sz - 1 ? sx * sy : 0;
        float fx = x - i0, fy = y - j0, fz = z - k0;
        // The clamps above keep every index inside the array, so the reads skip bounds checks (hot path).
        ref float p = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(f), i0 + sx * (j0 + sy * k0));
        float a = p, b = Unsafe.Add(ref p, di);
        float c = Unsafe.Add(ref p, dj), d = Unsafe.Add(ref p, dj + di);
        float e = Unsafe.Add(ref p, dk), g = Unsafe.Add(ref p, dk + di);
        float h = Unsafe.Add(ref p, dk + dj), q = Unsafe.Add(ref p, dk + dj + di);
        float c00 = a + (b - a) * fx;
        float c10 = c + (d - c) * fx;
        float c01 = e + (g - e) * fx;
        float c11 = h + (q - h) * fx;
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

    /// <summary>
    /// Velocity at grid position <paramref name="g"/> interpolated trilinearly from the cell-centred copy built by
    /// <see cref="CellVelocitySlice"/> (clamped to the outermost cell centres). Used for the midpoints of back-traces.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Vector3 SampleCellVelocity(Vector3 g)
    {
        float x = ClampCoord(g.X - 0.5f, _nx - 1), y = ClampCoord(g.Y - 0.5f, _ny - 1), z = ClampCoord(g.Z - 0.5f, _nz - 1);
        int i0 = (int)x, j0 = (int)y, k0 = (int)z;
        int di = i0 < _nx - 1 ? 1 : 0, dj = j0 < _ny - 1 ? _nx : 0, dk = k0 < _nz - 1 ? _nx * _ny : 0;
        float fx = x - i0, fy = y - j0, fz = z - k0;
        ref Vector3 p = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_cellVelocity), CellIndex(i0, j0, k0));
        var c00 = Vector3.Lerp(p, Unsafe.Add(ref p, di), fx);
        var c10 = Vector3.Lerp(Unsafe.Add(ref p, dj), Unsafe.Add(ref p, dj + di), fx);
        var c01 = Vector3.Lerp(Unsafe.Add(ref p, dk), Unsafe.Add(ref p, dk + di), fx);
        var c11 = Vector3.Lerp(Unsafe.Add(ref p, dk + dj), Unsafe.Add(ref p, dk + dj + di), fx);
        return Vector3.Lerp(Vector3.Lerp(c00, c10, fy), Vector3.Lerp(c01, c11, fy), fz);
    }

    /// <summary>Cell-centred value at grid position <paramref name="g"/> for queries (no ambient test; solids ignored).</summary>
    private float SampleScalarPlain(float[] f, float x, float y, float z)
    {
        if (!_hasObstacles)
            return Trilinear(f, _nx, _ny, _nz, x, y, z);
        int ci = (int)ClampCoord(x + 0.5f, _nx - 1), cj = (int)ClampCoord(y + 0.5f, _ny - 1), ck = (int)ClampCoord(z + 0.5f, _nz - 1);
        if (_solid[CellIndex(ci, cj, ck)] != 0)
            return 0f;
        SampleTwo(f, f, new Vector3(x + 0.5f, y + 0.5f, z + 0.5f), out float a, out _, out _, out _, out _, out _, withBounds: false);
        return a;
    }

    /// <summary>
    /// Samples two cell-centred fields at grid position <paramref name="g"/> with shared weights, returning the min and
    /// max of the contributing corners (the MacCormack limiter bounds). Outside an open face the air is ambient (0).
    /// Solid corners are skipped and the remaining weights renormalised, so smoke is neither pulled into walls nor
    /// diluted by them.
    /// </summary>
    private void SampleTwo(float[] a, float[] b, Vector3 g, out float va, out float minA, out float maxA,
        out float vb, out float minB, out float maxB, bool withBounds = true)
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
        // Offsets of the eight corners from c000; the clamps above keep them inside the grid, so reads are unchecked.
        int o100 = di, o010 = dj, o110 = dj + di, o001 = dk, o101 = dk + di, o011 = dk + dj, o111 = dk + dj + di;
        float gx = 1f - fx, gy = 1f - fy, gz = 1f - fz;
        float w000 = gx * gy * gz, w100 = fx * gy * gz, w010 = gx * fy * gz, w110 = fx * fy * gz;
        float w001 = gx * gy * fz, w101 = fx * gy * fz, w011 = gx * fy * fz, w111 = fx * fy * fz;
        ref float pa = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(a), c000);
        ref float pb = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(b), c000);
        float a000 = pa, a100 = Unsafe.Add(ref pa, o100), a010 = Unsafe.Add(ref pa, o010), a110 = Unsafe.Add(ref pa, o110);
        float a001 = Unsafe.Add(ref pa, o001), a101 = Unsafe.Add(ref pa, o101), a011 = Unsafe.Add(ref pa, o011);
        float a111 = Unsafe.Add(ref pa, o111);
        float b000 = pb, b100 = Unsafe.Add(ref pb, o100), b010 = Unsafe.Add(ref pb, o010), b110 = Unsafe.Add(ref pb, o110);
        float b001 = Unsafe.Add(ref pb, o001), b101 = Unsafe.Add(ref pb, o101), b011 = Unsafe.Add(ref pb, o011);
        float b111 = Unsafe.Add(ref pb, o111);

        bool s000 = false, s100 = false, s010 = false, s110 = false, s001 = false, s101 = false, s011 = false, s111 = false;
        if (_hasObstacles)
        {
            ref byte ps = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_solid), c000);
            s000 = ps != 0;
            s100 = Unsafe.Add(ref ps, o100) != 0;
            s010 = Unsafe.Add(ref ps, o010) != 0;
            s110 = Unsafe.Add(ref ps, o110) != 0;
            s001 = Unsafe.Add(ref ps, o001) != 0;
            s101 = Unsafe.Add(ref ps, o101) != 0;
            s011 = Unsafe.Add(ref ps, o011) != 0;
            s111 = Unsafe.Add(ref ps, o111) != 0;
            if (s000) w000 = 0f;
            if (s100) w100 = 0f;
            if (s010) w010 = 0f;
            if (s110) w110 = 0f;
            if (s001) w001 = 0f;
            if (s101) w101 = 0f;
            if (s011) w011 = 0f;
            if (s111) w111 = 0f;
            float sum = w000 + w100 + w010 + w110 + w001 + w101 + w011 + w111;
            if (!(sum > 1e-6f))
            {
                va = minA = maxA = vb = minB = maxB = 0f;
                return;
            }

            float inv = 1f / sum;
            w000 *= inv; w100 *= inv; w010 *= inv; w110 *= inv;
            w001 *= inv; w101 *= inv; w011 *= inv; w111 *= inv;
        }

        if (!withBounds)
        {
            minA = minB = maxA = maxB = 0f;
        }
        else if (_hasObstacles)
        {
            minA = minB = float.PositiveInfinity;
            maxA = maxB = float.NegativeInfinity;
            Bound(s000, a000, b000, ref minA, ref maxA, ref minB, ref maxB);
            Bound(s100, a100, b100, ref minA, ref maxA, ref minB, ref maxB);
            Bound(s010, a010, b010, ref minA, ref maxA, ref minB, ref maxB);
            Bound(s110, a110, b110, ref minA, ref maxA, ref minB, ref maxB);
            Bound(s001, a001, b001, ref minA, ref maxA, ref minB, ref maxB);
            Bound(s101, a101, b101, ref minA, ref maxA, ref minB, ref maxB);
            Bound(s011, a011, b011, ref minA, ref maxA, ref minB, ref maxB);
            Bound(s111, a111, b111, ref minA, ref maxA, ref minB, ref maxB);
        }
        else
        {
            minA = MathF.Min(MathF.Min(MathF.Min(a000, a100), MathF.Min(a010, a110)), MathF.Min(MathF.Min(a001, a101), MathF.Min(a011, a111)));
            maxA = MathF.Max(MathF.Max(MathF.Max(a000, a100), MathF.Max(a010, a110)), MathF.Max(MathF.Max(a001, a101), MathF.Max(a011, a111)));
            minB = MathF.Min(MathF.Min(MathF.Min(b000, b100), MathF.Min(b010, b110)), MathF.Min(MathF.Min(b001, b101), MathF.Min(b011, b111)));
            maxB = MathF.Max(MathF.Max(MathF.Max(b000, b100), MathF.Max(b010, b110)), MathF.Max(MathF.Max(b001, b101), MathF.Max(b011, b111)));
        }

        va = w000 * a000 + w100 * a100 + w010 * a010 + w110 * a110 + w001 * a001 + w101 * a101 + w011 * a011 + w111 * a111;
        vb = w000 * b000 + w100 * b100 + w010 * b010 + w110 * b110 + w001 * b001 + w101 * b101 + w011 * b011 + w111 * b111;

        static void Bound(bool solid, float a, float b, ref float minA, ref float maxA, ref float minB, ref float maxB)
        {
            if (solid)
                return;
            minA = MathF.Min(minA, a);
            maxA = MathF.Max(maxA, a);
            minB = MathF.Min(minB, b);
            maxB = MathF.Max(maxB, b);
        }
    }
}
