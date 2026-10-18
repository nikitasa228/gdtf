using System.Numerics;

namespace Candela.Simulation;

public sealed partial class SmokeSimulation
{
    private float[] _faceScratch = Array.Empty<float>();
    private float _diffusionLambda;
    private float[] _diffuseField = Array.Empty<float>(), _diffuseSource = Array.Empty<float>(), _diffuseTarget = Array.Empty<float>();
    private byte[] _diffuseMask = Array.Empty<byte>();
    private int _diffuseSx, _diffuseSy, _diffuseSz;

    /// <summary>
    /// Semi-Lagrangian self-advection of the velocity (Stam 1999): each free face takes the velocity found by tracing
    /// back along the flow for one time step with a midpoint (RK2) step, interpolated trilinearly.
    /// </summary>
    private void AdvectVelocity(float dt)
    {
        _dtForKernels = dt;
        RunSlices(_nz, _cellVelocitySlice);
        RunSlices(_nz + 1, _advectVelocitySlice);
        (_u, _u2) = (_u2, _u);
        (_v, _v2) = (_v2, _v);
        (_w, _w2) = (_w2, _w);
    }

    private void AdvectVelocitySlice(int k)
    {
        float dt = _dtForKernels;
        float s = dt * _invH;
        float vMax = _settings.MaxSpeed;
        var inflow = _inflowVelocity;
        int nx = _nx, ny = _ny, nz = _nz;

        if (k < nz)
        {
            for (int j = 0; j < ny; j++)
            {
                for (int i = 0; i <= nx; i++)
                {
                    int n = UIndex(i, j, k);
                    byte flag = _uFlags[n];
                    if (flag != FaceFree)
                    {
                        _u2[n] = FixedFaceValue(flag, inflow.X);
                        continue;
                    }

                    int il = Math.Max(i - 1, 0), ir = Math.Min(i, nx - 1);
                    float vv = 0.25f * (_v[VIndex(il, j, k)] + _v[VIndex(ir, j, k)] + _v[VIndex(il, j + 1, k)] + _v[VIndex(ir, j + 1, k)]);
                    float ww = 0.25f * (_w[WIndex(il, j, k)] + _w[WIndex(ir, j, k)] + _w[WIndex(il, j, k + 1)] + _w[WIndex(ir, j, k + 1)]);
                    var g = new Vector3(i, j + 0.5f, k + 0.5f);
                    var back = Backtrace(g, new Vector3(_u[n], vv, ww), s);
                    _u2[n] = ClampSpeed(Trilinear(_u, nx + 1, ny, nz, back.X, back.Y - 0.5f, back.Z - 0.5f), vMax);
                }
            }

            for (int j = 0; j <= ny; j++)
            {
                for (int i = 0; i < nx; i++)
                {
                    int n = VIndex(i, j, k);
                    byte flag = _vFlags[n];
                    if (flag != FaceFree)
                    {
                        _v2[n] = FixedFaceValue(flag, inflow.Y);
                        continue;
                    }

                    int jl = Math.Max(j - 1, 0), jr = Math.Min(j, ny - 1);
                    float uu = 0.25f * (_u[UIndex(i, jl, k)] + _u[UIndex(i + 1, jl, k)] + _u[UIndex(i, jr, k)] + _u[UIndex(i + 1, jr, k)]);
                    float ww = 0.25f * (_w[WIndex(i, jl, k)] + _w[WIndex(i, jr, k)] + _w[WIndex(i, jl, k + 1)] + _w[WIndex(i, jr, k + 1)]);
                    var g = new Vector3(i + 0.5f, j, k + 0.5f);
                    var back = Backtrace(g, new Vector3(uu, _v[n], ww), s);
                    _v2[n] = ClampSpeed(Trilinear(_v, nx, ny + 1, nz, back.X - 0.5f, back.Y, back.Z - 0.5f), vMax);
                }
            }
        }

        int kl = Math.Max(k - 1, 0), kr = Math.Min(k, nz - 1);
        for (int j = 0; j < ny; j++)
        {
            for (int i = 0; i < nx; i++)
            {
                int n = WIndex(i, j, k);
                byte flag = _wFlags[n];
                if (flag != FaceFree)
                {
                    _w2[n] = FixedFaceValue(flag, inflow.Z);
                    continue;
                }

                float uu = 0.25f * (_u[UIndex(i, j, kl)] + _u[UIndex(i + 1, j, kl)] + _u[UIndex(i, j, kr)] + _u[UIndex(i + 1, j, kr)]);
                float vv = 0.25f * (_v[VIndex(i, j, kl)] + _v[VIndex(i, j + 1, kl)] + _v[VIndex(i, j, kr)] + _v[VIndex(i, j + 1, kr)]);
                var g = new Vector3(i + 0.5f, j + 0.5f, k);
                var back = Backtrace(g, new Vector3(uu, vv, _w[n]), s);
                _w2[n] = ClampSpeed(Trilinear(_w, nx, ny, nz + 1, back.X - 0.5f, back.Y - 0.5f, back.Z), vMax);
            }
        }
    }

    /// <summary>
    /// Midpoint (RK2) trace from grid position <paramref name="g"/>, where the velocity is <paramref name="velocity"/>,
    /// over <paramref name="s"/> = dt/h (negative traces forward). The midpoint velocity comes from the cell-centred copy
    /// of the field (<see cref="CellVelocitySlice"/>): one interpolation for all three components.
    /// </summary>
    private Vector3 Backtrace(Vector3 g, Vector3 velocity, float s)
    {
        var mid = g - velocity * (0.5f * s);
        return g - SampleCellVelocity(mid) * s;
    }

    /// <summary>Cell-centred velocity (average of the two faces on each axis), m/s, into <c>_cellVelocity</c>.</summary>
    private void CellVelocitySlice(int k)
    {
        for (int j = 0; j < _ny; j++)
        {
            int c = CellIndex(0, j, k);
            for (int i = 0; i < _nx; i++, c++)
            {
                _cellVelocity[c] = new Vector3(
                    0.5f * (_u[UIndex(i, j, k)] + _u[UIndex(i + 1, j, k)]),
                    0.5f * (_v[VIndex(i, j, k)] + _v[VIndex(i, j + 1, k)]),
                    0.5f * (_w[WIndex(i, j, k)] + _w[WIndex(i, j, k + 1)]));
            }
        }
    }

    private static float ClampSpeed(float v, float max) => v >= -max ? (v <= max ? v : max) : (float.IsNaN(v) ? 0f : -max);

    /// <summary>
    /// Advects density and temperature with the projected (divergence-free) velocity. Semi-Lagrangian pass first; with
    /// MacCormack a second pass traces the result forward again and corrects by half the round-trip error, reverting to
    /// the semi-Lagrangian value wherever that would leave the range of the interpolated corners (Selle et al. 2008).
    /// </summary>
    private void AdvectScalars(float dt)
    {
        _dtForKernels = dt;
        RunSlices(_nz, _cellVelocitySlice);
        bool conserve = _settings.ConserveMass;
        double target = 0;
        if (conserve)
            target = Math.Max(0.0, SumDensity() - BoundaryOutflow(dt));

        RunSlices(_nz, _advectScalarsForwardSlice);
        if (_settings.ScalarAdvection == ScalarAdvectionScheme.MacCormack)
        {
            // Writes the corrected values back into _density/_temperature in place (each cell only reads its own old value).
            RunSlices(_nz, _advectScalarsCorrectSlice);
        }
        else
        {
            (_density, _density2) = (_density2, _density);
            (_temperature, _temperature2) = (_temperature2, _temperature);
        }

        _massCorrection = 0f;
        if (conserve)
            FixMass(target);
    }

    /// <summary>
    /// Smoke that leaves through open faces during one step, in density·cells: Σ u_n·d·dt/h over the free boundary
    /// faces with outward velocity, using the density of the cell inside (first-order upwind). Air entering through an
    /// open face is ambient and carries no smoke. Sequential, so the sum is deterministic.
    /// </summary>
    private double BoundaryOutflow(float dt)
    {
        var open = _settings.OpenFaces;
        if (open == DomainFaces.None)
            return 0.0;
        double sum = 0;
        int nx = _nx, ny = _ny, nz = _nz;
        for (int k = 0; k < nz; k++)
        {
            for (int j = 0; j < ny; j++)
            {
                sum += Outflow(_u, _uFlags, UIndex(0, j, k), -1f, CellIndex(0, j, k));
                sum += Outflow(_u, _uFlags, UIndex(nx, j, k), 1f, CellIndex(nx - 1, j, k));
            }

            for (int i = 0; i < nx; i++)
            {
                sum += Outflow(_v, _vFlags, VIndex(i, 0, k), -1f, CellIndex(i, 0, k));
                sum += Outflow(_v, _vFlags, VIndex(i, ny, k), 1f, CellIndex(i, ny - 1, k));
            }
        }

        for (int j = 0; j < ny; j++)
        {
            for (int i = 0; i < nx; i++)
            {
                sum += Outflow(_w, _wFlags, WIndex(i, j, 0), -1f, CellIndex(i, j, 0));
                sum += Outflow(_w, _wFlags, WIndex(i, j, nz), 1f, CellIndex(i, j, nz - 1));
            }
        }

        return sum * dt * _invH;

        float Outflow(float[] f, byte[] flags, int face, float outward, int cell)
        {
            if (flags[face] != FaceFree)
                return 0f;
            float un = f[face] * outward;
            return un > 0f ? un * _density[cell] : 0f;
        }
    }

    /// <summary>
    /// Global mass fixer: semi-Lagrangian and MacCormack advection do not conserve mass (errors of 10–60 % over a few
    /// hundred steps in recirculating flows), so the advected density is scaled so that the total equals the total
    /// before advection minus what left through open faces. Scaling keeps the shape and the sign of the field; the
    /// factor is limited to [0.5, 2] so a broken step cannot blow the field up. This is the "mass fixer" used with
    /// semi-Lagrangian tracer transport in atmospheric models.
    /// </summary>
    private void FixMass(double target)
    {
        double after = SumDensity();
        if (!(after > 1e-12) || !double.IsFinite(after) || !double.IsFinite(target))
            return;
        float factor = (float)Math.Clamp(target / after, 0.5, 2.0);
        _massCorrection = factor - 1f;
        if (factor == 1f)
            return;
        _massScale = factor;
        RunSlices(_nz, _scaleDensitySlice);
    }

    private float _massScale = 1f, _massCorrection;

    private void ScaleDensitySlice(int k)
    {
        int start = k * _nx * _ny, end = start + _nx * _ny;
        float f = _massScale;
        for (int c = start; c < end; c++)
            _density[c] *= f;
    }

    private void AdvectScalarsForwardSlice(int k)
    {
        float s = _dtForKernels * _invH;
        for (int j = 0; j < _ny; j++)
        {
            for (int i = 0; i < _nx; i++)
            {
                int c = CellIndex(i, j, k);
                if (_solid[c] != 0)
                {
                    _density2[c] = _temperature2[c] = 0f;
                    _densityMin[c] = _densityMax[c] = _temperatureMin[c] = _temperatureMax[c] = 0f;
                    continue;
                }

                var g = new Vector3(i + 0.5f, j + 0.5f, k + 0.5f);
                var back = Backtrace(g, _cellVelocity[c], s);
                SampleTwo(_density, _temperature, back, out float d, out float dMin, out float dMax,
                    out float t, out float tMin, out float tMax);
                _density2[c] = d;
                _temperature2[c] = t;
                _densityMin[c] = dMin;
                _densityMax[c] = dMax;
                _temperatureMin[c] = tMin;
                _temperatureMax[c] = tMax;
            }
        }
    }

    private void AdvectScalarsCorrectSlice(int k)
    {
        float s = _dtForKernels * _invH;
        for (int j = 0; j < _ny; j++)
        {
            for (int i = 0; i < _nx; i++)
            {
                int c = CellIndex(i, j, k);
                if (_solid[c] != 0)
                {
                    _density[c] = _temperature[c] = 0f;
                    continue;
                }

                var g = new Vector3(i + 0.5f, j + 0.5f, k + 0.5f);
                var forward = Backtrace(g, _cellVelocity[c], -s);
                SampleTwo(_density2, _temperature2, forward, out float dRound, out _, out _, out float tRound, out _, out _);
                float dHat = _density2[c], tHat = _temperature2[c];
                float d = dHat + 0.5f * (_density[c] - dRound);
                float t = tHat + 0.5f * (_temperature[c] - tRound);
                _density[c] = d >= _densityMin[c] && d <= _densityMax[c] ? d : dHat;
                _temperature[c] = t >= _temperatureMin[c] && t <= _temperatureMax[c] ? t : tHat;
            }
        }
    }

    /// <summary>Implicit diffusion of density and temperature (Neumann at solids and walls).</summary>
    private void DiffuseScalars(float dt)
    {
        float lambda = _settings.Diffusivity * dt * _invH * _invH;
        Diffuse(_density, _density2, _densityMin, _solid, _nx, _ny, _nz, lambda);
        Diffuse(_temperature, _temperature2, _densityMin, _solid, _nx, _ny, _nz, lambda);
    }

    /// <summary>Implicit viscosity on each velocity component (free-slip: walls are excluded from the stencil).</summary>
    private void DiffuseVelocity(float dt)
    {
        float lambda = _settings.Viscosity * dt * _invH * _invH;
        int max = Math.Max(_uCount, Math.Max(_vCount, _wCount));
        if (_faceScratch.Length < max)
            _faceScratch = new float[max];
        Diffuse(_u, _u2, _faceScratch, _uFlags, _nx + 1, _ny, _nz, lambda);
        Diffuse(_v, _v2, _faceScratch, _vFlags, _nx, _ny + 1, _nz, lambda);
        Diffuse(_w, _w2, _faceScratch, _wFlags, _nx, _ny, _nz + 1, lambda);
    }

    /// <summary>
    /// Solves (1 + n·λ)·x − λ·Σ x_nb = x₀ (backward Euler diffusion, λ = D·dt/h²) with Jacobi iterations; entries whose
    /// mask is non-zero neither change nor act as neighbours. Unconditionally stable.
    /// </summary>
    private void Diffuse(float[] field, float[] source, float[] scratch, byte[] mask, int sx, int sy, int sz, float lambda)
    {
        if (!(lambda > 0f))
            return;
        int count = sx * sy * sz;
        field.AsSpan(0, count).CopyTo(source);
        _diffusionLambda = lambda;
        _diffuseMask = mask;
        _diffuseSource = source;
        (_diffuseSx, _diffuseSy, _diffuseSz) = (sx, sy, sz);
        var current = field;
        var next = scratch;
        for (int it = 0; it < _settings.DiffusionIterations; it++)
        {
            _diffuseField = current;
            _diffuseTarget = next;
            RunSlices(sz, _diffuseSlice);
            (current, next) = (next, current);
        }

        if (!ReferenceEquals(current, field))
            current.AsSpan(0, count).CopyTo(field);
    }

    private void DiffuseSlice(int k)
    {
        int sx = _diffuseSx, sy = _diffuseSy, sz = _diffuseSz;
        float lambda = _diffusionLambda;
        var x = _diffuseField;
        var x0 = _diffuseSource;
        var y = _diffuseTarget;
        var mask = _diffuseMask;
        int slab = sx * sy;
        for (int j = 0; j < sy; j++)
        {
            for (int i = 0; i < sx; i++)
            {
                int n = i + sx * (j + sy * k);
                if (mask[n] != 0)
                {
                    y[n] = x[n];
                    continue;
                }

                float sum = 0f;
                int neighbours = 0;
                if (i > 0 && mask[n - 1] == 0) { sum += x[n - 1]; neighbours++; }
                if (i < sx - 1 && mask[n + 1] == 0) { sum += x[n + 1]; neighbours++; }
                if (j > 0 && mask[n - sx] == 0) { sum += x[n - sx]; neighbours++; }
                if (j < sy - 1 && mask[n + sx] == 0) { sum += x[n + sx]; neighbours++; }
                if (k > 0 && mask[n - slab] == 0) { sum += x[n - slab]; neighbours++; }
                if (k < sz - 1 && mask[n + slab] == 0) { sum += x[n + slab]; neighbours++; }
                y[n] = (x0[n] + lambda * sum) / (1f + lambda * neighbours);
            }
        }
    }
}
