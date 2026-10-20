using System.Diagnostics;
using System.Numerics;
using Candela.Core.Math;

namespace Candela.Simulation;

/// <summary>
/// A published state of a <see cref="SmokeWorker"/>'s simulation: the smoke density grid with its box and resolution,
/// and the counters at the moment the worker finished it. The main thread reads a frame while the worker already
/// simulates the next one; the worker never writes a frame the main thread holds (three frames rotate: one being
/// written, one ready, one held). A frame stays unchanged until the next <see cref="SmokeWorker.AcquireLatest"/> or
/// <see cref="SmokeWorker.Synchronize()"/> on the same worker, after which it may be reused for a later state.
/// </summary>
public sealed class SmokeFrame
{
    private float[] _density;
    private byte[] _solid = Array.Empty<byte>();
    private bool _hasSolids;
    private Vector3 _origin;
    private float _invH;

    internal SmokeFrame(Aabb bounds, int sizeX, int sizeY, int sizeZ, float cellSize, float massPerDensity)
    {
        _density = Array.Empty<float>();
        SetLayout(bounds, sizeX, sizeY, sizeZ, cellSize, massPerDensity);
    }

    /// <summary>
    /// Publication number: 0 for the empty frame of the requested layout before the worker published anything, then
    /// increasing by one per publication (a new frame always has a larger number than the one it replaces).
    /// </summary>
    public long Version { get; internal set; }

    /// <summary>World-space box covered by the grid, m.</summary>
    public Aabb Bounds { get; private set; }

    /// <summary>Cells along X.</summary>
    public int SizeX { get; private set; }

    /// <summary>Cells along Y.</summary>
    public int SizeY { get; private set; }

    /// <summary>Cells along Z.</summary>
    public int SizeZ { get; private set; }

    /// <summary>Total number of cells.</summary>
    public int CellCount => SizeX * SizeY * SizeZ;

    /// <summary>Cell edge length of this grid, m (in real time it may be coarser than requested, see <see cref="SmokeWorker"/>).</summary>
    public float CellSize { get; private set; }

    /// <summary>
    /// Smoke density per cell, x fastest, then y, then z (relative aerosol concentration; 1 scatters
    /// <see cref="SmokeSimulationSettings.ScatteringAtFullDensity"/> per metre).
    /// </summary>
    public ReadOnlySpan<float> Density => _density.AsSpan(0, CellCount);

    /// <summary>Aerosol mass concentration at density 1, kg/m³.</summary>
    public float MassConcentrationAtFullDensity { get; private set; }

    /// <summary>Total aerosol mass in the box, kg (Σ density × cell volume × <see cref="MassConcentrationAtFullDensity"/>).</summary>
    public double TotalSmokeMass { get; private set; }

    /// <summary>Simulated time of the state, s (<see cref="SmokeSimulation.Time"/>).</summary>
    public double Time { get; private set; }

    /// <summary>Fixed sub-steps simulated up to this state (<see cref="SmokeSimulation.StepCount"/>).</summary>
    public long StepCount { get; private set; }

    /// <summary>Diagnostics of the last step call before publication.</summary>
    public SmokeStepStatistics Statistics { get; private set; }

    /// <summary>
    /// Frame time the worker has taken in so far, s: every submitted frame's time it processed — simulated, carried to
    /// the next step, or dropped. Submitted time minus this is the work still queued (see <see cref="SmokeWorker.Lag"/>).
    /// </summary>
    public double ConsumedTime { get; internal set; }

    /// <summary>Simulation time dropped since the worker started because the solver could not keep up, s.</summary>
    public double DroppedTime { get; internal set; }

    /// <summary>
    /// Share of real time the solver could not simulate, averaged over the last few seconds (0 … 1) when the frame was
    /// published; only measured in <see cref="SmokeTiming.RealTime"/>.
    /// </summary>
    public float DroppedFraction { get; internal set; }

    /// <summary>Measured wall-clock time per fixed sub-step, ms (moving average; 0 before the first measurement).</summary>
    public float MillisecondsPerStep { get; internal set; }

    /// <summary><see cref="Stopwatch"/> timestamp of the publication.</summary>
    public long PublishedTimestamp { get; internal set; }

    /// <summary>Smoke density at a world point (trilinear, solid cells skipped like <see cref="SmokeSimulation.DensityAt"/>); 0 outside.</summary>
    public float DensityAt(Vector3 point)
    {
        if (!float.IsFinite(point.X) || !float.IsFinite(point.Y) || !float.IsFinite(point.Z) || !Bounds.Contains(point))
            return 0f;
        var g = (point - _origin) * _invH;
        return _hasSolids ? SampleSkippingSolids(g) : Trilinear(g.X - 0.5f, g.Y - 0.5f, g.Z - 0.5f);
    }

    /// <summary>Copies <see cref="Density"/> into <paramref name="destination"/> (length ≥ <see cref="CellCount"/>).</summary>
    public void CopyDensityTo(Span<float> destination)
    {
        if (destination.Length < CellCount)
            throw new ArgumentException($"Destination holds {destination.Length} values, {CellCount} needed.", nameof(destination));
        Density.CopyTo(destination);
    }

    /// <summary>Whether cell (<paramref name="i"/>, <paramref name="j"/>, <paramref name="k"/>) is solid.</summary>
    public bool IsSolid(int i, int j, int k) =>
        _hasSolids && (uint)i < (uint)SizeX && (uint)j < (uint)SizeY && (uint)k < (uint)SizeZ && _solid[i + SizeX * (j + SizeY * k)] != 0;

    /// <summary>Sets the grid (keeping the arrays when they are large enough) and clears the content.</summary>
    internal void SetLayout(Aabb bounds, int sizeX, int sizeY, int sizeZ, float cellSize, float massPerDensity)
    {
        Bounds = bounds;
        SizeX = sizeX;
        SizeY = sizeY;
        SizeZ = sizeZ;
        CellSize = cellSize;
        MassConcentrationAtFullDensity = massPerDensity;
        _origin = bounds.Min;
        _invH = 1f / cellSize;
        int cells = CellCount;
        if (_density.Length < cells)
            _density = new float[cells];
        else
            Array.Clear(_density, 0, cells);
        _hasSolids = false;
        SolidKey = default;
        TotalSmokeMass = 0;
        Time = 0;
        StepCount = 0;
        Statistics = default;
    }

    /// <summary>Which simulation and topology version the solid mask was copied from.</summary>
    internal (int Generation, int Topology) SolidKey { get; private set; }

    /// <summary>
    /// Copies the state of <paramref name="simulation"/> (generation <paramref name="generation"/> of the worker's
    /// simulations): layout, density, solid mask when it changed, counters, and the total mass summed per z slice in
    /// slice order exactly like <see cref="SmokeSimulation.TotalSmokeMass"/>.
    /// </summary>
    internal void CopyFrom(SmokeSimulation simulation, int generation)
    {
        var bounds = simulation.Bounds;
        if (simulation.SizeX != SizeX || simulation.SizeY != SizeY || simulation.SizeZ != SizeZ || bounds.Min != Bounds.Min
            || bounds.Max != Bounds.Max || simulation.CellSize != CellSize || simulation.MassConcentrationAtFullDensity != MassConcentrationAtFullDensity)
        {
            SetLayout(simulation.Bounds, simulation.SizeX, simulation.SizeY, simulation.SizeZ, simulation.CellSize,
                simulation.MassConcentrationAtFullDensity);
        }

        int cells = CellCount;
        simulation.CopyDensityTo(_density);
        var key = (generation, simulation.TopologyVersion);
        if (SolidKey != key)
        {
            SolidKey = key;
            _hasSolids = simulation.HasObstacles;
            if (_hasSolids)
            {
                if (_solid.Length < cells)
                    _solid = new byte[cells];
                simulation.CopySolidTo(_solid);
            }
        }

        double sum = 0;
        int slab = SizeX * SizeY;
        for (int k = 0; k < SizeZ; k++)
        {
            double s = 0;
            for (int c = k * slab, end = c + slab; c < end; c++)
                s += _density[c];
            sum += s;
        }

        double h = CellSize;
        TotalSmokeMass = sum * h * h * h * MassConcentrationAtFullDensity;
        Time = simulation.Time;
        StepCount = simulation.StepCount;
        Statistics = simulation.LastStatistics;
    }

    /// <summary>Trilinear interpolation at sample coordinates (cell centres at integers), clamped to the grid.</summary>
    private float Trilinear(float x, float y, float z)
    {
        int sx = SizeX, sy = SizeY, sz = SizeZ;
        x = ClampCoord(x, sx - 1);
        y = ClampCoord(y, sy - 1);
        z = ClampCoord(z, sz - 1);
        int i0 = (int)x, j0 = (int)y, k0 = (int)z;
        int di = i0 < sx - 1 ? 1 : 0;
        int dj = j0 < sy - 1 ? sx : 0;
        int dk = k0 < sz - 1 ? sx * sy : 0;
        float fx = x - i0, fy = y - j0, fz = z - k0;
        int p = i0 + sx * (j0 + sy * k0);
        var f = _density;
        float a = f[p], b = f[p + di];
        float c = f[p + dj], d = f[p + dj + di];
        float e = f[p + dk], g = f[p + dk + di];
        float h = f[p + dk + dj], q = f[p + dk + dj + di];
        float c00 = a + (b - a) * fx;
        float c10 = c + (d - c) * fx;
        float c01 = e + (g - e) * fx;
        float c11 = h + (q - h) * fx;
        float c0 = c00 + (c10 - c00) * fy;
        float c1 = c01 + (c11 - c01) * fy;
        return c0 + (c1 - c0) * fz;
    }

    /// <summary>
    /// Trilinear interpolation at grid position <paramref name="g"/> that skips solid corners and renormalises the
    /// remaining weights (0 inside a solid cell or when every corner is solid) — the arithmetic of the simulation's own
    /// sampling, so both give the same value.
    /// </summary>
    private float SampleSkippingSolids(Vector3 g)
    {
        int nx = SizeX, ny = SizeY, nz = SizeZ;
        int ci = (int)ClampCoord(g.X, nx - 1), cj = (int)ClampCoord(g.Y, ny - 1), ck = (int)ClampCoord(g.Z, nz - 1);
        if (_solid[ci + nx * (cj + ny * ck)] != 0)
            return 0f;
        float x = ClampCoord(g.X - 0.5f, nx - 1), y = ClampCoord(g.Y - 0.5f, ny - 1), z = ClampCoord(g.Z - 0.5f, nz - 1);
        int i0 = (int)x, j0 = (int)y, k0 = (int)z;
        int di = i0 < nx - 1 ? 1 : 0, dj = j0 < ny - 1 ? nx : 0, dk = k0 < nz - 1 ? nx * ny : 0;
        float fx = x - i0, fy = y - j0, fz = z - k0;
        int c000 = i0 + nx * (j0 + ny * k0);
        int o100 = di, o010 = dj, o110 = dj + di, o001 = dk, o101 = dk + di, o011 = dk + dj, o111 = dk + dj + di;
        float gx = 1f - fx, gy = 1f - fy, gz = 1f - fz;
        float w000 = gx * gy * gz, w100 = fx * gy * gz, w010 = gx * fy * gz, w110 = fx * fy * gz;
        float w001 = gx * gy * fz, w101 = fx * gy * fz, w011 = gx * fy * fz, w111 = fx * fy * fz;
        var s = _solid;
        if (s[c000] != 0) w000 = 0f;
        if (s[c000 + o100] != 0) w100 = 0f;
        if (s[c000 + o010] != 0) w010 = 0f;
        if (s[c000 + o110] != 0) w110 = 0f;
        if (s[c000 + o001] != 0) w001 = 0f;
        if (s[c000 + o101] != 0) w101 = 0f;
        if (s[c000 + o011] != 0) w011 = 0f;
        if (s[c000 + o111] != 0) w111 = 0f;
        float sum = w000 + w100 + w010 + w110 + w001 + w101 + w011 + w111;
        if (!(sum > 1e-6f))
            return 0f;
        float inv = 1f / sum;
        w000 *= inv; w100 *= inv; w010 *= inv; w110 *= inv;
        w001 *= inv; w101 *= inv; w011 *= inv; w111 *= inv;
        var f = _density;
        return w000 * f[c000] + w100 * f[c000 + o100] + w010 * f[c000 + o010] + w110 * f[c000 + o110]
            + w001 * f[c000 + o001] + w101 * f[c000 + o101] + w011 * f[c000 + o011] + w111 * f[c000 + o111];
    }

    private static float ClampCoord(float v, int max) => v >= 0f ? (v <= max ? v : max) : 0f;
}
