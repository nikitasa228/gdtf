using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Candela.Simulation;

/// <summary>
/// Geometric multigrid V-cycle used as the preconditioner of the pressure conjugate gradient (MGPCG, McAdams, Sifakis
/// &amp; Teran 2010). The fine level is the simulation grid; each coarser level halves the resolution. A coarse cell takes
/// part when any of its eight children does; a coarse face is open to flow when any of the fine faces it covers is
/// (so thin walls stay closed on every level and open domain faces stay open). The coarse operator is the same
/// 7-point Poisson stencil re-discretised on that topology: n·x_c − Σ x_nb, with n the number of open faces (an open
/// domain face counts, its outside value being 0) and the sum over neighbours behind open faces.
/// <para>
/// One V-cycle: symmetric red-black Gauss–Seidel smoothing (red then black before the coarse correction, black then
/// red after it), restriction of the residual by summing the children (× ½, which accounts for the h² scaling of the
/// stencil: b_H = H²·f = 4·mean(b_h)), piecewise-constant prolongation, and on the coarsest level symmetric sweeps
/// until it is practically solved. Restriction is ½ × the transpose of prolongation and the smoothing is mirrored, so
/// the preconditioner is symmetric, as conjugate gradients require.
/// </para>
/// <para>
/// Three algebraic shortcuts give exactly the textbook result with about half the memory traffic: with a zero initial
/// guess the first red sweep is x = b/n on red cells; right after a black Gauss–Seidel sweep the black residuals are
/// exactly zero, so only red residuals are restricted; and the prolongated value of a red cell is only ever read by the
/// next black sweep (the red sweep after it overwrites it), so that sweep adds the parent's correction to each red
/// neighbour on the fly instead of a separate prolongation pass.
/// </para>
/// <para>Deterministic: every parallel loop writes only its own slice of the level it works on.</para>
/// </summary>
internal sealed class PressureMultigrid
{
    /// <summary>Face flag meaning "fluid can flow through" (same value as the simulation's <c>FaceFree</c>).</summary>
    private const byte Open = 0;

    private const byte Closed = 1;

    // Neighbour-coupling bits of a cell's mask.
    private const byte XMinus = 1, XPlus = 2, YMinus = 4, YPlus = 8, ZMinus = 16, ZPlus = 32;

    /// <summary>Levels whose grid is at most this many cells along every axis are not coarsened further.</summary>
    private const int CoarsestSize = 4;

    /// <summary>Symmetric red-black sweep pairs on the coarsest level (≤ 64 cells: practically an exact solve).</summary>
    private const int CoarsestSweeps = 16;

    /// <summary>Levels with fewer cells than this are processed on one thread.</summary>
    private const int ParallelThreshold = 8192;

    private readonly Level[] _levels;
    private readonly ParallelOptions _parallelOptions;
    private readonly bool _allowParallel;
    private readonly Action<int> _initRedSlice, _redSlice, _blackSlice, _correctedBlackSlice, _restrictSlice;
    private Level _current = null!;
    private Level _coarse = null!;

    /// <summary>One grid of the hierarchy.</summary>
    private sealed class Level
    {
        public int Nx, Ny, Nz;

        /// <summary>Solution and right-hand side (on the fine level: the caller's z and r).</summary>
        public float[] X = Array.Empty<float>(), B = Array.Empty<float>();

        public float[] Diag = Array.Empty<float>(), InvDiag = Array.Empty<float>();

        /// <summary>Per cell: which of the six neighbours are coupled (bits <c>XMinus</c> … <c>ZPlus</c>).</summary>
        public byte[] Mask = Array.Empty<byte>();

        /// <summary>Face flags along X ((Nx + 1) × Ny × Nz), Y (Nx × (Ny + 1) × Nz) and Z (Nx × Ny × (Nz + 1)).</summary>
        public byte[] Fx = Array.Empty<byte>(), Fy = Array.Empty<byte>(), Fz = Array.Empty<byte>();

        public int Cells => Nx * Ny * Nz;
    }

    /// <summary>Builds the level hierarchy for an <paramref name="nx"/> × <paramref name="ny"/> × <paramref name="nz"/> grid.</summary>
    public PressureMultigrid(int nx, int ny, int nz, ParallelOptions parallelOptions, bool allowParallel)
    {
        _parallelOptions = parallelOptions;
        _allowParallel = allowParallel;
        var levels = new List<Level> { new() { Nx = nx, Ny = ny, Nz = nz, Mask = new byte[nx * ny * nz] } };
        while (Math.Max(nx, Math.Max(ny, nz)) > CoarsestSize)
        {
            nx = (nx + 1) / 2;
            ny = (ny + 1) / 2;
            nz = (nz + 1) / 2;
            int cells = nx * ny * nz;
            levels.Add(new Level
            {
                Nx = nx,
                Ny = ny,
                Nz = nz,
                X = new float[cells],
                B = new float[cells],
                Diag = new float[cells],
                InvDiag = new float[cells],
                Mask = new byte[cells],
                Fx = new byte[(nx + 1) * ny * nz],
                Fy = new byte[nx * (ny + 1) * nz],
                Fz = new byte[nx * ny * (nz + 1)],
            });
        }

        _levels = levels.ToArray();
        _initRedSlice = InitRedSlice;
        _redSlice = k => SmoothSlice(k, 0);
        _blackSlice = k => SmoothSlice(k, 1);
        _correctedBlackSlice = CorrectedBlackSlice;
        _restrictSlice = RestrictSlice;
    }

    /// <summary>Number of levels including the fine grid.</summary>
    public int LevelCount => _levels.Length;

    /// <summary>
    /// Takes the fine level's topology (face flags with 0 = open, stencil diagonal; the arrays are referenced, not
    /// copied) and derives every coarser level. Call again whenever the solids change.
    /// </summary>
    public void SetTopology(byte[] fx, byte[] fy, byte[] fz, float[] diag, float[] invDiag)
    {
        var fine = _levels[0];
        fine.Fx = fx;
        fine.Fy = fy;
        fine.Fz = fz;
        fine.Diag = diag;
        fine.InvDiag = invDiag;
        BuildMasks(fine);
        for (int l = 1; l < _levels.Length; l++)
        {
            Coarsen(_levels[l - 1], _levels[l]);
            BuildMasks(_levels[l]);
        }
    }

    /// <summary>z = M⁻¹·r: one V-cycle with zero initial guess.</summary>
    public void Apply(float[] r, float[] z)
    {
        var fine = _levels[0];
        fine.B = r;
        fine.X = z;
        VCycle(0);
    }

    private void VCycle(int l)
    {
        var level = _levels[l];
        if (l == _levels.Length - 1)
        {
            Array.Clear(level.X, 0, level.Cells);
            for (int n = 0; n < CoarsestSweeps; n++)
            {
                Run(level, null, _redSlice);
                Run(level, null, _blackSlice);
            }

            for (int n = 0; n < CoarsestSweeps; n++)
            {
                Run(level, null, _blackSlice);
                Run(level, null, _redSlice);
            }

            return;
        }

        var coarse = _levels[l + 1];
        Run(level, coarse, _initRedSlice);
        Run(level, coarse, _blackSlice);
        Run(coarse, null, _restrictSlice, fine: level);
        VCycle(l + 1);
        Run(level, coarse, _correctedBlackSlice);
        Run(level, coarse, _redSlice);
    }

    /// <summary>Runs <paramref name="body"/> over the slices of <paramref name="level"/> (or of the coarse level for restriction).</summary>
    private void Run(Level level, Level? coarse, Action<int> body, Level? fine = null)
    {
        if (fine is not null)
        {
            _current = fine;
            _coarse = level;
        }
        else
        {
            _current = level;
            _coarse = coarse!;
        }

        if (_allowParallel && (fine ?? level).Cells >= ParallelThreshold)
        {
            Parallel.For(0, level.Nz, _parallelOptions, body);
        }
        else
        {
            for (int k = 0; k < level.Nz; k++)
                body(k);
        }
    }

    /// <summary>
    /// Σ x over the coupled neighbours of the cell at <paramref name="x"/> (a reference into the level's array). A mask
    /// bit is only set when that neighbour exists, so the unchecked reads stay inside the array (hot path).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float NeighbourSum(ref float x, byte mask, int nx, int slab)
    {
        float s = 0f;
        if ((mask & XMinus) != 0) s += Unsafe.Add(ref x, -1);
        if ((mask & XPlus) != 0) s += Unsafe.Add(ref x, 1);
        if ((mask & YMinus) != 0) s += Unsafe.Add(ref x, -nx);
        if ((mask & YPlus) != 0) s += Unsafe.Add(ref x, nx);
        if ((mask & ZMinus) != 0) s += Unsafe.Add(ref x, -slab);
        if ((mask & ZPlus) != 0) s += Unsafe.Add(ref x, slab);
        return s;
    }

    private static ref float At(float[] a, int index) => ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(a), index);

    /// <summary>First red sweep from a zero guess: x = b/n on red cells ((i + j + k) even), 0 on black cells.</summary>
    private void InitRedSlice(int k)
    {
        var L = _current;
        int nx = L.Nx, ny = L.Ny;
        var x = L.X;
        var b = L.B;
        var inv = L.InvDiag;
        for (int j = 0; j < ny; j++)
        {
            int row = nx * (j + ny * k);
            int red = (j + k) & 1;
            for (int i = 0; i < nx; i++)
            {
                int c = row + i;
                x[c] = (i & 1) == red ? b[c] * inv[c] : 0f;
            }
        }
    }

    /// <summary>
    /// Gauss–Seidel update of the cells of one colour ((i + j + k) mod 2 = <paramref name="colour"/>) in slice k:
    /// x = (b + Σ x_nb) / n. Cells of one colour only read cells of the other, so slices can run in parallel.
    /// </summary>
    private void SmoothSlice(int k, int colour)
    {
        var L = _current;
        int nx = L.Nx, ny = L.Ny, slab = nx * ny;
        var x = L.X;
        var b = L.B;
        var inv = L.InvDiag;
        var mask = L.Mask;
        for (int j = 0; j < ny; j++)
        {
            int row = nx * (j + ny * k);
            for (int i = (j + k + colour) & 1; i < nx; i += 2)
            {
                int c = row + i;
                float invDiag = inv[c];
                if (invDiag != 0f)
                {
                    ref float xc = ref At(x, c);
                    xc = (b[c] + NeighbourSum(ref xc, mask[c], nx, slab)) * invDiag;
                }
            }
        }
    }

    /// <summary>
    /// Post-correction black sweep: like <see cref="SmoothSlice"/> for black cells, but each (red) neighbour is read as
    /// x + the coarse correction of its parent (the fused piecewise-constant prolongation).
    /// </summary>
    private void CorrectedBlackSlice(int k)
    {
        var L = _current;
        var C = _coarse;
        int nx = L.Nx, ny = L.Ny, slab = nx * ny;
        int cnx = C.Nx, cslab = C.Nx * C.Ny;
        var x = L.X;
        var b = L.B;
        var inv = L.InvDiag;
        var mask = L.Mask;
        var e = C.X;
        int ck = k >> 1;
        for (int j = 0; j < ny; j++)
        {
            int row = nx * (j + ny * k);
            int cj = j >> 1;
            int parentRow = cnx * cj + cslab * ck;
            for (int i = (j + k + 1) & 1; i < nx; i += 2)
            {
                int c = row + i;
                float invDiag = inv[c];
                if (invDiag == 0f)
                    continue;
                byte m = mask[c];
                int ci = i >> 1;
                ref float xc = ref At(x, c);
                // Corrections of the parents of the six neighbours (unchecked: a mask bit implies the neighbour, and so
                // its parent, exists).
                float s = NeighbourSum(ref xc, m, nx, slab);
                if ((m & XMinus) != 0) s += At(e, parentRow + ((i - 1) >> 1));
                if ((m & XPlus) != 0) s += At(e, parentRow + ((i + 1) >> 1));
                if ((m & YMinus) != 0) s += At(e, cnx * ((j - 1) >> 1) + cslab * ck + ci);
                if ((m & YPlus) != 0) s += At(e, cnx * ((j + 1) >> 1) + cslab * ck + ci);
                if ((m & ZMinus) != 0) s += At(e, parentRow + cslab * (((k - 1) >> 1) - ck) + ci);
                if ((m & ZPlus) != 0) s += At(e, parentRow + cslab * (((k + 1) >> 1) - ck) + ci);
                xc = (b[c] + s) * invDiag;
            }
        }
    }

    /// <summary>
    /// Coarse b = ½ Σ r over the fine children of coarse slice K, where only red children can have a residual (the
    /// black ones were just relaxed): r = b − n·x + Σ x_nb.
    /// </summary>
    private void RestrictSlice(int kc)
    {
        var F = _current;
        var C = _coarse;
        int nx = F.Nx, ny = F.Ny, nz = F.Nz, slab = nx * ny;
        var x = F.X;
        var b = F.B;
        var diag = F.Diag;
        var mask = F.Mask;
        var coarse = C.B;
        int cslab = C.Nx * C.Ny, cbase = cslab * kc;
        Array.Clear(coarse, cbase, cslab);
        for (int k = 2 * kc; k <= Math.Min(2 * kc + 1, nz - 1); k++)
        {
            for (int j = 0; j < ny; j++)
            {
                int row = nx * (j + ny * k);
                int target = cbase + C.Nx * (j >> 1);
                for (int i = (j + k) & 1; i < nx; i += 2)
                {
                    int c = row + i;
                    float d = diag[c];
                    if (d != 0f)
                    {
                        ref float xc = ref At(x, c);
                        coarse[target + (i >> 1)] += 0.5f * (b[c] - d * xc + NeighbourSum(ref xc, mask[c], nx, slab));
                    }
                }
            }
        }
    }

    /// <summary>Neighbour masks: a neighbour is coupled when it exists, takes part (n &gt; 0) and the face between is open.</summary>
    private static void BuildMasks(Level L)
    {
        int nx = L.Nx, ny = L.Ny, nz = L.Nz;
        for (int k = 0; k < nz; k++)
        {
            for (int j = 0; j < ny; j++)
            {
                for (int i = 0; i < nx; i++)
                {
                    int c = i + nx * (j + ny * k);
                    byte m = 0;
                    if (L.Diag[c] != 0f)
                    {
                        int fx = i + (nx + 1) * (j + ny * k);
                        int fy = i + nx * (j + (ny + 1) * k);
                        int fz = c;
                        if (i > 0 && L.Fx[fx] == Open && L.Diag[c - 1] != 0f) m |= XMinus;
                        if (i < nx - 1 && L.Fx[fx + 1] == Open && L.Diag[c + 1] != 0f) m |= XPlus;
                        if (j > 0 && L.Fy[fy] == Open && L.Diag[c - nx] != 0f) m |= YMinus;
                        if (j < ny - 1 && L.Fy[fy + nx] == Open && L.Diag[c + nx] != 0f) m |= YPlus;
                        if (k > 0 && L.Fz[fz] == Open && L.Diag[c - nx * ny] != 0f) m |= ZMinus;
                        if (k < nz - 1 && L.Fz[fz + nx * ny] == Open && L.Diag[c + nx * ny] != 0f) m |= ZPlus;
                    }

                    L.Mask[c] = m;
                }
            }
        }
    }

    /// <summary>Derives the face flags and stencil diagonal of <paramref name="coarse"/> from <paramref name="fine"/>.</summary>
    private static void Coarsen(Level fine, Level coarse)
    {
        int fnx = fine.Nx, fny = fine.Ny, fnz = fine.Nz;
        int nx = coarse.Nx, ny = coarse.Ny, nz = coarse.Nz;

        // A coarse face is open when any fine face it covers is open and borders a fluid cell. On the max side of an
        // odd-sized fine grid the coarse face lies half a fine cell outside; the fine boundary faces stand in for it.
        for (int k = 0; k < nz; k++)
        {
            for (int j = 0; j < ny; j++)
            {
                for (int i = 0; i <= nx; i++)
                {
                    int fi = Math.Min(2 * i, fnx);
                    bool open = false;
                    for (int dk = 0; dk < 2 && !open; dk++)
                    {
                        for (int dj = 0; dj < 2 && !open; dj++)
                        {
                            int fj = 2 * j + dj, fk = 2 * k + dk;
                            open = fj < fny && fk < fnz && fine.Fx[fi + (fnx + 1) * (fj + fny * fk)] == Open
                                && (Fluid(fine, fi - 1, fj, fk) || Fluid(fine, fi, fj, fk));
                        }
                    }

                    coarse.Fx[i + (nx + 1) * (j + ny * k)] = open ? Open : Closed;
                }
            }
        }

        for (int k = 0; k < nz; k++)
        {
            for (int j = 0; j <= ny; j++)
            {
                for (int i = 0; i < nx; i++)
                {
                    int fj = Math.Min(2 * j, fny);
                    bool open = false;
                    for (int dk = 0; dk < 2 && !open; dk++)
                    {
                        for (int di = 0; di < 2 && !open; di++)
                        {
                            int fi = 2 * i + di, fk = 2 * k + dk;
                            open = fi < fnx && fk < fnz && fine.Fy[fi + fnx * (fj + (fny + 1) * fk)] == Open
                                && (Fluid(fine, fi, fj - 1, fk) || Fluid(fine, fi, fj, fk));
                        }
                    }

                    coarse.Fy[i + nx * (j + (ny + 1) * k)] = open ? Open : Closed;
                }
            }
        }

        for (int k = 0; k <= nz; k++)
        {
            for (int j = 0; j < ny; j++)
            {
                for (int i = 0; i < nx; i++)
                {
                    int fk = Math.Min(2 * k, fnz);
                    bool open = false;
                    for (int dj = 0; dj < 2 && !open; dj++)
                    {
                        for (int di = 0; di < 2 && !open; di++)
                        {
                            int fi = 2 * i + di, fj = 2 * j + dj;
                            open = fi < fnx && fj < fny && fine.Fz[fi + fnx * (fj + fny * fk)] == Open
                                && (Fluid(fine, fi, fj, fk - 1) || Fluid(fine, fi, fj, fk));
                        }
                    }

                    coarse.Fz[i + nx * (j + ny * k)] = open ? Open : Closed;
                }
            }
        }

        // Diagonal: open faces of the cells that have at least one fluid child; other cells take no part.
        for (int k = 0; k < nz; k++)
        {
            for (int j = 0; j < ny; j++)
            {
                for (int i = 0; i < nx; i++)
                {
                    int c = i + nx * (j + ny * k);
                    int open = 0;
                    if (HasFluidChild(fine, i, j, k))
                    {
                        open += coarse.Fx[i + (nx + 1) * (j + ny * k)] == Open ? 1 : 0;
                        open += coarse.Fx[i + 1 + (nx + 1) * (j + ny * k)] == Open ? 1 : 0;
                        open += coarse.Fy[i + nx * (j + (ny + 1) * k)] == Open ? 1 : 0;
                        open += coarse.Fy[i + nx * (j + 1 + (ny + 1) * k)] == Open ? 1 : 0;
                        open += coarse.Fz[i + nx * (j + ny * k)] == Open ? 1 : 0;
                        open += coarse.Fz[i + nx * (j + ny * (k + 1))] == Open ? 1 : 0;
                    }

                    coarse.Diag[c] = open;
                    coarse.InvDiag[c] = open > 0 ? 1f / open : 0f;
                }
            }
        }
    }

    private static bool Fluid(Level L, int i, int j, int k) =>
        i >= 0 && j >= 0 && k >= 0 && i < L.Nx && j < L.Ny && k < L.Nz && L.Diag[i + L.Nx * (j + L.Ny * k)] != 0f;

    private static bool HasFluidChild(Level fine, int ic, int jc, int kc)
    {
        for (int k = 2 * kc; k <= Math.Min(2 * kc + 1, fine.Nz - 1); k++)
        {
            for (int j = 2 * jc; j <= Math.Min(2 * jc + 1, fine.Ny - 1); j++)
            {
                for (int i = 2 * ic; i <= Math.Min(2 * ic + 1, fine.Nx - 1); i++)
                {
                    if (Fluid(fine, i, j, k))
                        return true;
                }
            }
        }

        return false;
    }
}
