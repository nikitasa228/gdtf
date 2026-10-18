namespace Candela.Simulation;

public sealed partial class SmokeSimulation
{
    private Action<int> _sumDensitySlice = null!;
    private Action<int> _scaleDensitySlice = null!;
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
    private Action<int> _applyPressureSlice = null!;
    private float _cgAlpha, _cgBeta;

    /// <summary>Creates the per-slice delegates once, so stepping does not allocate closures.</summary>
    private void InitializeKernels()
    {
        _sumDensitySlice = SumDensitySlice;
        _scaleDensitySlice = ScaleDensitySlice;
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
        _applyPressureSlice = ApplyPressureSlice;
    }

    /// <summary>
    /// Pressure projection. With the scaled pressure φ = p·dt/ρ, a free face between cells a and b is corrected by
    /// u −= (φ_b − φ_a)/h; requiring zero divergence gives, for every fluid cell, n·φ_c − Σ φ_nb = −h²·(∇·u)_c, where n
    /// counts the cell's free faces, solid and wall faces drop out (free slip: their normal velocity stays zero) and
    /// open faces see the ambient pressure φ = 0 outside. The symmetric positive (semi-)definite system is solved with
    /// the Jacobi-preconditioned conjugate gradient, warm-started from the previous step's pressure. Returns the
    /// iteration count and the largest cell divergence before and after (1/s).
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
        RunSlices(_nz, _initialResidualSlice);
        double rz = SumOfSlices();
        float maxR = MaxOfSlices();
        float tolerance = _settings.PressureTolerance * maxB;
        int iterations = 0;
        while (iterations < _settings.MaxPressureIterations && maxR > tolerance)
        {
            RunSlices(_nz, _matVecSlice);
            double pq = SumOfSlices();
            if (!(pq > 0) || !double.IsFinite(pq) || !double.IsFinite(rz))
                break;
            _cgAlpha = (float)(rz / pq);
            RunSlices(_nz, _updateSlice);
            double rzNext = SumOfSlices();
            maxR = MaxOfSlices();
            iterations++;
            if (!double.IsFinite(rzNext))
                break;
            _cgBeta = rz > 0 ? (float)(rzNext / rz) : 0f;
            rz = rzNext;
            if (maxR > tolerance)
                RunSlices(_nz, _searchSlice);
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

    /// <summary>(A·x)_c = n_c·x_c − Σ x_nb; x is zero in solid cells, so they drop out of the sum automatically.</summary>
    private float Apply(float[] x, int c, int i, int j, int k)
    {
        int slab = _nx * _ny;
        float s = 0f;
        if (i > 0) s += x[c - 1];
        if (i < _nx - 1) s += x[c + 1];
        if (j > 0) s += x[c - _nx];
        if (j < _ny - 1) s += x[c + _nx];
        if (k > 0) s += x[c - slab];
        if (k < _nz - 1) s += x[c + slab];
        return _diag[c] * x[c] - s;
    }

    private void InitialResidualSlice(int k)
    {
        double rz = 0;
        float max = 0f;
        for (int j = 0; j < _ny; j++)
        {
            for (int i = 0; i < _nx; i++)
            {
                int c = CellIndex(i, j, k);
                if (_diag[c] == 0f)
                {
                    _pressure[c] = _residual[c] = _precond[c] = _search[c] = 0f;
                    continue;
                }

                float r = _rhs[c] - Apply(_pressure, c, i, j, k);
                float z = r * _invDiag[c];
                _residual[c] = r;
                _precond[c] = z;
                _search[c] = z;
                rz += (double)r * z;
                max = MathF.Max(max, MathF.Abs(r));
            }
        }

        _slicePartials[k] = rz;
        _sliceMax[k] = max;
    }

    private void MatVecSlice(int k)
    {
        double pq = 0;
        for (int j = 0; j < _ny; j++)
        {
            for (int i = 0; i < _nx; i++)
            {
                int c = CellIndex(i, j, k);
                float q = _diag[c] == 0f ? 0f : Apply(_search, c, i, j, k);
                _product[c] = q;
                pq += (double)_search[c] * q;
            }
        }

        _slicePartials[k] = pq;
    }

    private void UpdateSlice(int k)
    {
        float alpha = _cgAlpha;
        double rz = 0;
        float max = 0f;
        int start = k * _nx * _ny, end = start + _nx * _ny;
        for (int c = start; c < end; c++)
        {
            _pressure[c] += alpha * _search[c];
            float r = _residual[c] - alpha * _product[c];
            float z = r * _invDiag[c];
            _residual[c] = r;
            _precond[c] = z;
            rz += (double)r * z;
            max = MathF.Max(max, MathF.Abs(r));
        }

        _slicePartials[k] = rz;
        _sliceMax[k] = max;
    }

    private void SearchSlice(int k)
    {
        float beta = _cgBeta;
        int start = k * _nx * _ny, end = start + _nx * _ny;
        for (int c = start; c < end; c++)
            _search[c] = _precond[c] + beta * _search[c];
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
