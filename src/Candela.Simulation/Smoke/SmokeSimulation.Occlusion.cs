using System.Numerics;

namespace Candela.Simulation;

public sealed partial class SmokeSimulation
{
    /// <summary>
    /// Summed-volume table of the solid cells ((nx + 1) × (ny + 1) × (nz + 1), x fastest): entry (i, j, k) counts the
    /// solid cells with indices below (i, j, k). Lets the jets ask in O(1) whether a box of cells holds any solid. Empty
    /// while there are no obstacles.
    /// </summary>
    private int[] _solidPrefix = Array.Empty<int>();

    /// <summary>Rebuilds <see cref="_solidPrefix"/> after the solids changed (O(cells)).</summary>
    private void BuildSolidPrefix()
    {
        if (!_hasObstacles)
        {
            _solidPrefix = Array.Empty<int>();
            return;
        }

        int px = _nx + 1, py = _ny + 1, pz = _nz + 1;
        if (_solidPrefix.Length != px * py * pz)
            _solidPrefix = new int[px * py * pz];
        var s = _solidPrefix;
        Array.Clear(s);
        for (int k = 1; k < pz; k++)
        {
            for (int j = 1; j < py; j++)
            {
                for (int i = 1; i < px; i++)
                {
                    int n = i + px * (j + py * k);
                    s[n] = _solid[CellIndex(i - 1, j - 1, k - 1)]
                        + s[n - 1] + s[n - px] + s[n - px * py]
                        - s[n - 1 - px] - s[n - 1 - px * py] - s[n - px - px * py]
                        + s[n - 1 - px - px * py];
                }
            }
        }
    }

    /// <summary>Number of solid cells in the inclusive index box [i0, i1] × [j0, j1] × [k0, k1] (clamped to the grid).</summary>
    private int SolidCount(int i0, int j0, int k0, int i1, int j1, int k1)
    {
        i0 = Math.Max(i0, 0);
        j0 = Math.Max(j0, 0);
        k0 = Math.Max(k0, 0);
        i1 = Math.Min(i1, _nx - 1) + 1;
        j1 = Math.Min(j1, _ny - 1) + 1;
        k1 = Math.Min(k1, _nz - 1) + 1;
        if (i0 >= i1 || j0 >= j1 || k0 >= k1)
            return 0;
        int px = _nx + 1, pxy = px * (_ny + 1);
        var s = _solidPrefix;
        return s[i1 + px * j1 + pxy * k1] - s[i0 + px * j1 + pxy * k1] - s[i1 + px * j0 + pxy * k1] - s[i1 + px * j1 + pxy * k0]
            + s[i0 + px * j0 + pxy * k1] + s[i0 + px * j1 + pxy * k0] + s[i1 + px * j0 + pxy * k0] - s[i0 + px * j0 + pxy * k0];
    }

    /// <summary>
    /// Whether the straight segment between grid positions <paramref name="a"/> and <paramref name="b"/> (grid units,
    /// cell (i, j, k) = [i, i + 1]³) crosses no solid cell other than the one containing <paramref name="a"/>. A box
    /// query rules out most segments at once; otherwise the cells are walked with a 3D DDA (Amanatides &amp; Woo 1987).
    /// </summary>
    private bool SegmentClear(Vector3 a, Vector3 b)
    {
        int ia = (int)MathF.Floor(a.X), ja = (int)MathF.Floor(a.Y), ka = (int)MathF.Floor(a.Z);
        int ib = (int)MathF.Floor(b.X), jb = (int)MathF.Floor(b.Y), kb = (int)MathF.Floor(b.Z);
        if (SolidCount(Math.Min(ia, ib), Math.Min(ja, jb), Math.Min(ka, kb), Math.Max(ia, ib), Math.Max(ja, jb), Math.Max(ka, kb)) == 0)
            return true;
        var d = b - a;
        int si = d.X > 0f ? 1 : -1, sj = d.Y > 0f ? 1 : -1, sk = d.Z > 0f ? 1 : -1;
        float adx = MathF.Abs(d.X), ady = MathF.Abs(d.Y), adz = MathF.Abs(d.Z);
        float tMaxX = adx > 0f ? (si > 0 ? ia + 1 - a.X : a.X - ia) / adx : float.PositiveInfinity;
        float tMaxY = ady > 0f ? (sj > 0 ? ja + 1 - a.Y : a.Y - ja) / ady : float.PositiveInfinity;
        float tMaxZ = adz > 0f ? (sk > 0 ? ka + 1 - a.Z : a.Z - ka) / adz : float.PositiveInfinity;
        float tDeltaX = adx > 0f ? 1f / adx : float.PositiveInfinity;
        float tDeltaY = ady > 0f ? 1f / ady : float.PositiveInfinity;
        float tDeltaZ = adz > 0f ? 1f / adz : float.PositiveInfinity;
        int i = ia, j = ja, k = ka;
        int steps = Math.Abs(ib - ia) + Math.Abs(jb - ja) + Math.Abs(kb - ka);
        for (int n = 0; n < steps; n++)
        {
            float t;
            if (tMaxX <= tMaxY && tMaxX <= tMaxZ)
            {
                t = tMaxX;
                i += si;
                tMaxX += tDeltaX;
            }
            else if (tMaxY <= tMaxZ)
            {
                t = tMaxY;
                j += sj;
                tMaxY += tDeltaY;
            }
            else
            {
                t = tMaxZ;
                k += sk;
                tMaxZ += tDeltaZ;
            }

            if (t > 1f)
                break;
            if ((uint)i >= (uint)_nx || (uint)j >= (uint)_ny || (uint)k >= (uint)_nz)
                break; // left the box (convex): the rest of the segment is outside too
            if (_solid[CellIndex(i, j, k)] != 0)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Walks a jet's axis through the solids. <c>Clear</c> is the distance, m, at which the axis first runs into a solid
    /// cell — the jet is not imposed beyond it, so a fan pointed at a wall does not blow through it; it is
    /// <paramref name="reach"/> when nothing is in the way. Solid cells right at the outlet (the device's own body, when
    /// the engine passes fixture bodies as obstacles) are crossed for up to max(D, 2h) first; <c>Exit</c> is where the
    /// axis leaves them (0 for an outlet in the open). A jet still inside a solid after that is buried: Clear = 0.
    /// </summary>
    private (float Exit, float Clear) CentrelineClearance(Vector3 position, Vector3 direction, float diameter, float reach)
    {
        if (!_hasObstacles)
            return (0f, reach);
        var a = (position - _origin) * _invH;
        var b = (position + direction * reach - _origin) * _invH;
        float length = reach; // metres per unit of the DDA parameter t
        float exitAllowance = MathF.Max(diameter, 2f * _h);
        int i = (int)MathF.Floor(a.X), j = (int)MathF.Floor(a.Y), k = (int)MathF.Floor(a.Z);
        int ib = (int)MathF.Floor(b.X), jb = (int)MathF.Floor(b.Y), kb = (int)MathF.Floor(b.Z);
        var d = b - a;
        int si = d.X > 0f ? 1 : -1, sj = d.Y > 0f ? 1 : -1, sk = d.Z > 0f ? 1 : -1;
        float adx = MathF.Abs(d.X), ady = MathF.Abs(d.Y), adz = MathF.Abs(d.Z);
        float tMaxX = adx > 0f ? (si > 0 ? i + 1 - a.X : a.X - i) / adx : float.PositiveInfinity;
        float tMaxY = ady > 0f ? (sj > 0 ? j + 1 - a.Y : a.Y - j) / ady : float.PositiveInfinity;
        float tMaxZ = adz > 0f ? (sk > 0 ? k + 1 - a.Z : a.Z - k) / adz : float.PositiveInfinity;
        float tDeltaX = adx > 0f ? 1f / adx : float.PositiveInfinity;
        float tDeltaY = ady > 0f ? 1f / ady : float.PositiveInfinity;
        float tDeltaZ = adz > 0f ? 1f / adz : float.PositiveInfinity;
        int steps = Math.Abs(ib - i) + Math.Abs(jb - j) + Math.Abs(kb - k);
        bool inside = (uint)i < (uint)_nx && (uint)j < (uint)_ny && (uint)k < (uint)_nz;
        bool leftOutlet = !inside || _solid[CellIndex(i, j, k)] == 0;
        float t = 0f, exit = 0f;
        for (int n = 0; n <= steps; n++)
        {
            if (n > 0)
            {
                if (tMaxX <= tMaxY && tMaxX <= tMaxZ)
                {
                    t = tMaxX;
                    i += si;
                    tMaxX += tDeltaX;
                }
                else if (tMaxY <= tMaxZ)
                {
                    t = tMaxY;
                    j += sj;
                    tMaxY += tDeltaY;
                }
                else
                {
                    t = tMaxZ;
                    k += sk;
                    tMaxZ += tDeltaZ;
                }

                if (t > 1f)
                    break;
            }

            inside = (uint)i < (uint)_nx && (uint)j < (uint)_ny && (uint)k < (uint)_nz;
            bool solid = inside && _solid[CellIndex(i, j, k)] != 0;
            if (!leftOutlet)
            {
                if (!solid)
                {
                    leftOutlet = true;
                    exit = t * length;
                }
                else if (t * length > exitAllowance)
                {
                    return (0f, 0f); // buried in a solid
                }

                continue;
            }

            if (solid)
                return (exit, t * length);
        }

        return (exit, leftOutlet ? reach : 0f);
    }
}
