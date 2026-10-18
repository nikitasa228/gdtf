using System.Runtime.CompilerServices;

namespace Candela.Simulation;

public sealed partial class SmokeSimulation
{
    private Action<int> _sumDensitySlice = null!;
    private Action<int> _sumFieldSlice = null!;
    private Action<int> _fixSlice = null!;
    private Action<int> _cellVelocitySlice = null!;
    private Action<int> _buoyancySlice = null!;
    private Action<int> _dissipateSlice = null!;
    private Action<int> _advectVelocitySlice = null!;
    private Action<int> _advectScalarsForwardSlice = null!;
    private Action<int> _advectScalarsCorrectSlice = null!;
    private Action<int> _diffuseSlice = null!;
    private Action<int> _rhsSlice = null!;
    private Action<int> _initialResidualSlice = null!;
    private Action<int> _matVecSlice = null!;
    private Action<int> _updateSlice = null!;
    private Action<int> _searchSlice = null!;
    private Action<int> _searchMultigridSlice = null!;
    private Action<int> _applyPressureSlice = null!;
    private Action<int> _jacobiStartSlice = null!;
    private float _cgAlpha, _cgBeta;

    /// <summary>Creates the per-slice delegates once, so stepping does not allocate closures.</summary>
    private void InitializeKernels()
    {
        _sumDensitySlice = SumDensitySlice;
        _sumFieldSlice = SumFieldSlice;
        _fixSlice = FixSlice;
        _cellVelocitySlice = CellVelocitySlice;
        _buoyancySlice = BuoyancySlice;
        _dissipateSlice = DissipateSlice;
        _advectVelocitySlice = AdvectVelocitySlice;
        _advectScalarsForwardSlice = AdvectScalarsForwardSlice;
        _advectScalarsCorrectSlice = AdvectScalarsCorrectSlice;
        _diffuseSlice = DiffuseSlice;
        _rhsSlice = RhsSlice;
        _initialResidualSlice = InitialResidualSlice;
        _matVecSlice = MatVecSlice;
        _updateSlice = UpdateSlice;
        _searchSlice = SearchSlice;
        _searchMultigridSlice = SearchMultigridSlice;
        _applyPressureSlice = ApplyPressureSlice;
        _jacobiStartSlice = JacobiStartSlice;
    }

    /// <summary>
    /// Pressure projection. With the scaled pressure φ = p·dt/ρ, a free face between cells a and b is corrected by
    /// u −= (φ_b − φ_a)/h; requiring zero divergence gives, for every fluid cell, n·φ_c − Σ φ_nb = −h²·(∇·u)_c, where n
    /// counts the cell's free faces, solid and wall faces drop out (free slip: their normal velocity stays zero) and
    /// open faces see the ambient pressure φ = 0 outside. The symmetric positive (semi-)definite system is solved with
    /// the preconditioned conjugate gradient (multigrid V-cycle or Jacobi preconditioner), warm-started from the previous
    /// step's pressure. Returns the iteration count and the largest cell divergence before and after (1/s).
    /// </summary>
    private (int Iterations, float Before, float After) Project()
    {
        RunSlices(_nz, _rhsSlice);
        if (_anyClosedComponent)
            RemoveClosedComponentMeans();
        float maxB = MaxOfSlices();
        float h2 = _h * _h;
        if (!(maxB > 1e-12f))
            return (0, maxB / h2, maxB / h2);

        // r = b − A·x₀, z = M⁻¹·r, p = z.
        bool multigrid = _multigrid is not null;
        RunSlices(_nz, _initialResidualSlice);
        float maxR = MaxOfSlices();
        if (_anyClosedComponent)
            maxR = ProjectClosedResidual(maxR, recomputeJacobi: !multigrid);
        double rz = multigrid ? Precondition() : SumOfSlices();
        if (multigrid)
            _z.AsSpan(0, _cellCount).CopyTo(_search);
        float tolerance = _settings.PressureTolerance * maxB;
        int iterations = 0;
        while (iterations < _settings.MaxPressureIterations && maxR > tolerance)
        {
            RunSlices(_nz, _matVecSlice);
            double pq = SumOfSlices();
            if (!(pq > 0) || !double.IsFinite(pq) || !(rz > 0) || !double.IsFinite(rz))
                break;
            _cgAlpha = (float)(rz / pq);
            RunSlices(_nz, _updateSlice);
            double rzNext = SumOfSlices();
            maxR = MaxOfSlices();
            iterations++;
            if (maxR <= tolerance)
                break;
            if (_anyClosedComponent)
                maxR = ProjectClosedResidual(maxR, recomputeJacobi: false);
            if (multigrid)
                rzNext = Precondition();
            // r·M⁻¹r of a symmetric positive definite preconditioner is positive for r ≠ 0: anything else is a
            // breakdown (rounding), and continuing would only make the pressure worse.
            if (!(rzNext > 0) || !double.IsFinite(rzNext))
                break;
            _cgBeta = (float)(rzNext / rz);
            rz = rzNext;
            RunSlices(_nz, multigrid ? _searchMultigridSlice : _searchSlice);
        }

        if (!float.IsFinite(maxR))
        {
            // A broken solve (only possible with corrupted input) must not poison the flow: drop this pressure.
            Array.Clear(_pressure);
            return (iterations, maxB / h2, maxB / h2);
        }

        RunSlices(_nz + 1, _applyPressureSlice);
        return (iterations, maxB / h2, maxR / h2);
    }

    /// <summary>
    /// Removes the mean of the residual over every closed fluid region (sequential over <c>_closedCells</c>, so
    /// deterministic). In exact arithmetic it is zero already — the right-hand side was made consistent and A·x has no
    /// component in the null space — but float rounding in r −= α·q lets a constant creep in, which the multigrid
    /// preconditioner (an approximate inverse of the singular operator) amplifies until the conjugate gradient breaks
    /// down. Returns the updated max |r|: exact when the whole box is closed; otherwise the larger of the previous
    /// maximum and the maximum over the closed cells (a bound that differs from the exact value only by rounding).
    /// With <paramref name="recomputeJacobi"/> the Jacobi start values p = r/n and r·p are recomputed into
    /// <c>_search</c> and the slice partials.
    /// </summary>
    private float ProjectClosedResidual(float maxR, bool recomputeJacobi)
    {
        var cells = _closedCells;
        var r = _residual;
        Array.Clear(_componentSums);
        for (int n = 0; n < cells.Length; n++)
        {
            int c = cells[n];
            _componentSums[_component[c]] += r[c];
        }

        for (int id = 0; id < _componentSums.Length; id++)
            _componentSums[id] = _componentClosed[id] ? _componentSums[id] / _componentCells[id] : 0;

        float closedMax = 0f;
        for (int n = 0; n < cells.Length; n++)
        {
            int c = cells[n];
            if (_diag[c] == 0f)
                continue; // a single sealed cell: its row of A is empty and r stays 0
            float value = r[c] - (float)_componentSums[_component[c]];
            r[c] = value;
            closedMax = MathF.Max(closedMax, MathF.Abs(value));
        }

        if (recomputeJacobi)
            RunSlices(_nz, _jacobiStartSlice);
        return _anyOpenComponent ? MathF.Max(maxR, closedMax) : closedMax;
    }

    /// <summary>p = r/n and the partial Σ r²/n of slice k (Jacobi start after the residual was projected).</summary>
    private void JacobiStartSlice(int k)
    {
        int start = k * _nx * _ny, end = start + _nx * _ny;
        double rz = 0;
        for (int c = start; c < end; c++)
        {
            float z = _residual[c] * _invDiag[c];
            _search[c] = z;
            rz += (double)_residual[c] * z;
        }

        _slicePartials[k] = rz;
    }

    /// <summary>z = V-cycle(r); returns r·z.</summary>
    private double Precondition() => _multigrid!.Apply(_residual, _z);

    /// <summary>p = z + β·p with z from the multigrid preconditioner.</summary>
    private void SearchMultigridSlice(int k)
    {
        float beta = _cgBeta;
        int start = k * _nx * _ny, end = start + _nx * _ny;
        for (int c = start; c < end; c++)
            _search[c] = _z[c] + beta * _search[c];
    }

    private double SumOfSlices()
    {
        double sum = 0;
        for (int k = 0; k < _nz; k++)
            sum += _slicePartials[k];
        return sum;
    }

    private float MaxOfSlices()
    {
        float max = 0f;
        for (int k = 0; k < _nz; k++)
            max = MathF.Max(max, _sliceMax[k]);
        return max;
    }

    /// <summary>b = −h·(Σ outflow − Σ inflow) = −h²·∇·u for fluid cells, 0 for solids.</summary>
    private void RhsSlice(int k)
    {
        float max = 0f;
        for (int j = 0; j < _ny; j++)
        {
            for (int i = 0; i < _nx; i++)
            {
                int c = CellIndex(i, j, k);
                if (_solid[c] != 0)
                {
                    _rhs[c] = 0f;
                    continue;
                }

                float flux = _u[UIndex(i + 1, j, k)] - _u[UIndex(i, j, k)]
                    + _v[VIndex(i, j + 1, k)] - _v[VIndex(i, j, k)]
                    + _w[WIndex(i, j, k + 1)] - _w[WIndex(i, j, k)];
                float b = -_h * flux;
                _rhs[c] = b;
                max = MathF.Max(max, MathF.Abs(b));
            }
        }

        _sliceMax[k] = max;
    }

    /// <summary>
    /// A fluid region without an open face can only be solved if its net divergence is zero (what flows in must flow
    /// out); remove each such region's mean so the singular system stays consistent. Sequential, so deterministic.
    /// </summary>
    private void RemoveClosedComponentMeans()
    {
        Array.Clear(_componentSums);
        for (int c = 0; c < _cellCount; c++)
        {
            int id = _component[c];
            if (id >= 0 && _componentClosed[id])
                _componentSums[id] += _rhs[c];
        }

        for (int id = 0; id < _componentSums.Length; id++)
            _componentSums[id] = _componentClosed[id] ? _componentSums[id] / _componentCells[id] : 0;
        for (int k = 0; k < _nz; k++)
        {
            float max = 0f;
            int start = k * _nx * _ny, end = start + _nx * _ny;
            for (int c = start; c < end; c++)
            {
                int id = _component[c];
                if (id >= 0 && _componentClosed[id])
                    _rhs[c] -= (float)_componentSums[id];
                max = MathF.Max(max, MathF.Abs(_rhs[c]));
            }

            _sliceMax[k] = max;
        }
    }

    /// <summary>
    /// q = A·x for every cell of slice <paramref name="k"/>: (A·x)_c = n_c·x_c − Σ x_nb for fluid cells and 0 for solid
    /// ones. x is zero in solid cells, so they drop out of the neighbour sum by themselves. Rows are processed with the
    /// four neighbouring rows (j ± 1, k ± 1) as flat spans — a zero row stands in beyond the box — so the inner loop has
    /// no index arithmetic or boundary tests. Returns Σ x·q over the slice.
    /// </summary>
    private double MultiplySlice(float[] x, float[] q, int k)
    {
        int nx = _nx, ny = _ny, slab = nx * ny;
        double dot = 0;
        for (int j = 0; j < ny; j++)
        {
            int c0 = CellIndex(0, j, k);
            dot += MultiplyRow(
                ref x[c0],
                ref j > 0 ? ref x[c0 - nx] : ref _zeroRow[0],
                ref j < ny - 1 ? ref x[c0 + nx] : ref _zeroRow[0],
                ref k > 0 ? ref x[c0 - slab] : ref _zeroRow[0],
                ref k < _nz - 1 ? ref x[c0 + slab] : ref _zeroRow[0],
                ref _diag[c0],
                ref q[c0],
                nx);
        }

        return dot;
    }

    private static double MultiplyRow(ref float row, ref float jm, ref float jp, ref float km, ref float kp, ref float diag,
        ref float q, int n)
    {
        double dot = 0;
        float left = 0f;
        float centre = row;
        for (int i = 0; i < n; i++)
        {
            float right = i < n - 1 ? Unsafe.Add(ref row, i + 1) : 0f;
            float d = Unsafe.Add(ref diag, i);
            float sum = left + right + Unsafe.Add(ref jm, i) + Unsafe.Add(ref jp, i) + Unsafe.Add(ref km, i) + Unsafe.Add(ref kp, i);
            float value = d > 0f ? d * centre - sum : 0f;
            Unsafe.Add(ref q, i) = value;
            dot += centre * value;
            left = centre;
            centre = right;
        }

        return dot;
    }

    /// <summary>r = b − A·x₀ (warm start from the previous pressure), p = M⁻¹·r; partial r·M⁻¹r and max |r|.</summary>
    private void InitialResidualSlice(int k)
    {
        int start = k * _nx * _ny, end = start + _nx * _ny;
        for (int c = start; c < end; c++)
        {
            if (_diag[c] == 0f)
                _pressure[c] = 0f;
        }

        MultiplySlice(_pressure, _product, k);
        double rz = 0;
        float max = 0f;
        for (int c = start; c < end; c++)
        {
            float r = _diag[c] == 0f ? 0f : _rhs[c] - _product[c];
            float z = r * _invDiag[c];
            _residual[c] = r;
            _search[c] = z;
            rz += (double)r * z;
            max = MathF.Max(max, MathF.Abs(r));
        }

        _slicePartials[k] = rz;
        _sliceMax[k] = max;
    }

    private void MatVecSlice(int k) => _slicePartials[k] = MultiplySlice(_search, _product, k);

    /// <summary>x += α·p, r −= α·q; partial r·M⁻¹r (Jacobi preconditioner M = diag A) and max |r|.</summary>
    private void UpdateSlice(int k)
    {
        float alpha = _cgAlpha;
        double rz = 0;
        float max = 0f;
        int start = k * _nx * _ny, count = _nx * _ny;
        ref float p = ref _search[start];
        ref float q = ref _product[start];
        ref float x = ref _pressure[start];
        ref float res = ref _residual[start];
        ref float inv = ref _invDiag[start];
        for (int i = 0; i < count; i++)
        {
            Unsafe.Add(ref x, i) += alpha * Unsafe.Add(ref p, i);
            float r = Unsafe.Add(ref res, i) - alpha * Unsafe.Add(ref q, i);
            Unsafe.Add(ref res, i) = r;
            float rr = r * r;
            rz += rr * Unsafe.Add(ref inv, i);
            max = MathF.Max(max, rr);
        }

        _slicePartials[k] = rz;
        _sliceMax[k] = MathF.Sqrt(max);
    }

    /// <summary>p = M⁻¹·r + β·p.</summary>
    private void SearchSlice(int k)
    {
        float beta = _cgBeta;
        int start = k * _nx * _ny, count = _nx * _ny;
        ref float p = ref _search[start];
        ref float res = ref _residual[start];
        ref float inv = ref _invDiag[start];
        for (int i = 0; i < count; i++)
            Unsafe.Add(ref p, i) = Unsafe.Add(ref res, i) * Unsafe.Add(ref inv, i) + beta * Unsafe.Add(ref p, i);
    }

    /// <summary>Subtracts the pressure gradient from every free face (ambient φ = 0 beyond open faces).</summary>
    private void ApplyPressureSlice(int k)
    {
        int nx = _nx, ny = _ny, nz = _nz;
        float invH = _invH;
        var p = _pressure;
        if (k < nz)
        {
            for (int j = 0; j < ny; j++)
            {
                for (int i = 0; i <= nx; i++)
                {
                    int n = UIndex(i, j, k);
                    if (_uFlags[n] != FaceFree)
                        continue;
                    float left = i > 0 ? p[CellIndex(i - 1, j, k)] : 0f;
                    float right = i < nx ? p[CellIndex(i, j, k)] : 0f;
                    _u[n] -= (right - left) * invH;
                }
            }

            for (int j = 0; j <= ny; j++)
            {
                for (int i = 0; i < nx; i++)
                {
                    int n = VIndex(i, j, k);
                    if (_vFlags[n] != FaceFree)
                        continue;
                    float front = j > 0 ? p[CellIndex(i, j - 1, k)] : 0f;
                    float back = j < ny ? p[CellIndex(i, j, k)] : 0f;
                    _v[n] -= (back - front) * invH;
                }
            }
        }

        for (int j = 0; j < ny; j++)
        {
            for (int i = 0; i < nx; i++)
            {
                int n = WIndex(i, j, k);
                if (_wFlags[n] != FaceFree)
                    continue;
                float below = k > 0 ? p[CellIndex(i, j, k - 1)] : 0f;
                float above = k < nz ? p[CellIndex(i, j, k)] : 0f;
                _w[n] -= (above - below) * invH;
            }
        }
    }
}
