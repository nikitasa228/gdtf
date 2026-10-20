using System.Numerics;

namespace Candela.Simulation;

public sealed partial class SmokeSimulation
{
    /// <summary>Counts rebuilds of the solid cells (obstacles set, snapshot restored); copies of the mask compare it.</summary>
    internal int TopologyVersion => _topologyVersion;

    /// <summary>True when any cell is solid.</summary>
    internal bool HasObstacles => _hasObstacles;

    /// <summary>Copies the solid flags (1 = solid, x fastest) into <paramref name="destination"/> (length ≥ <see cref="CellCount"/>).</summary>
    internal void CopySolidTo(Span<byte> destination) => _solid.AsSpan().CopyTo(destination);

    /// <summary>
    /// Takes over the state of <paramref name="source"/>, a simulation of the same box with another cell size (the
    /// <see cref="SmokeWorker"/> coarsens or refines its grid to keep up with real time): velocity, density, temperature
    /// and pressure are resampled trilinearly at this grid's faces and cells (solid corners skipped, as in
    /// <see cref="DensityAt"/>), the smoke mass and heat are corrected to the source's totals (the boxes differ by less
    /// than a cell at each side, and resampling is not conservative), the ventilation ramp, time and step counters
    /// continue, and the velocity is projected so the next step starts divergence-free. Obstacles must have been set on
    /// this simulation first.
    /// </summary>
    internal void TransferStateFrom(SmokeSimulation source)
    {
        ArgumentNullException.ThrowIfNull(source);
        // Position in the source's grid units of a position g in this grid: offset + g·scale.
        float scale = _h * source._invH;
        var offset = (_origin - source._origin) * source._invH;

        RunSlices(_nz, k =>
        {
            for (int j = 0; j < _ny; j++)
            {
                for (int i = 0; i < _nx; i++)
                {
                    int c = CellIndex(i, j, k);
                    if (_solid[c] != 0)
                    {
                        _density[c] = 0f;
                        _temperature[c] = 0f;
                        _pressure[c] = 0f;
                        continue;
                    }

                    var g = offset + new Vector3(i + 0.5f, j + 0.5f, k + 0.5f) * scale;
                    _density[c] = MathF.Max(0f, source.SampleScalarPlain(source._density, g.X - 0.5f, g.Y - 0.5f, g.Z - 0.5f));
                    _temperature[c] = Math.Clamp(source.SampleScalarPlain(source._temperature, g.X - 0.5f, g.Y - 0.5f, g.Z - 0.5f),
                        -_coldLimit, _settings.MaxTemperatureExcess);
                    _pressure[c] = source.SampleScalarPlain(source._pressure, g.X - 0.5f, g.Y - 0.5f, g.Z - 0.5f);
                }
            }

            for (int j = 0; j < _ny; j++)
            {
                for (int i = 0; i <= _nx; i++)
                {
                    var g = offset + new Vector3(i, j + 0.5f, k + 0.5f) * scale;
                    _u[UIndex(i, j, k)] = Trilinear(source._u, source._nx + 1, source._ny, source._nz, g.X, g.Y - 0.5f, g.Z - 0.5f);
                }
            }

            for (int j = 0; j <= _ny; j++)
            {
                for (int i = 0; i < _nx; i++)
                {
                    var g = offset + new Vector3(i + 0.5f, j, k + 0.5f) * scale;
                    _v[VIndex(i, j, k)] = Trilinear(source._v, source._nx, source._ny + 1, source._nz, g.X - 0.5f, g.Y, g.Z - 0.5f);
                }
            }
        });

        RunSlices(_nz + 1, k =>
        {
            for (int j = 0; j < _ny; j++)
            {
                for (int i = 0; i < _nx; i++)
                {
                    var g = offset + new Vector3(i + 0.5f, j + 0.5f, k) * scale;
                    _w[WIndex(i, j, k)] = Trilinear(source._w, source._nx, source._ny, source._nz + 1, g.X - 0.5f, g.Y - 0.5f, g.Z);
                }
            }
        });

        // Totals per unit volume: Σ over cells × cell volume, so the target sums scale with (h_source / h)³.
        double volumeRatio = Math.Pow((double)source._h / _h, 3);
        double massTarget = source.SumDensity() * volumeRatio;
        double mass = SumDensity();
        if (mass > 0 && massTarget >= 0 && double.IsFinite(massTarget))
        {
            float factor = (float)(massTarget / mass);
            for (int c = 0; c < _cellCount; c++)
                _density[c] *= factor;
        }

        double heatTarget = source.SumField(source._temperature, out _, out _, out _) * volumeRatio;
        FixSum(_temperature, heatTarget, keepExtremes: true);

        _inflowVelocity = source._inflowVelocity;
        _time = source._time;
        _stepCount = source._stepCount;
        _accumulator = source._accumulator;
        EnforceBoundaryVelocities(_u, _v, _w);
        Project();
        _statistics = default;
    }
}
