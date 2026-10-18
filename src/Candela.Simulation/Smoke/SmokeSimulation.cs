using System.Diagnostics;
using System.Numerics;
using Candela.Core.Diagnostics;
using Candela.Core.Math;

namespace Candela.Simulation;

/// <summary>
/// CPU reference smoke and haze simulation: an Eulerian "stable fluids" solver (Stam 1999; Fedkiw, Stam &amp; Jensen
/// 2001) on a staggered MAC grid over an axis-aligned box. It advances air velocity, smoke density and air
/// temperature with fixed sub-steps:
/// <list type="number">
/// <item>sources — hazers and fog machines add aerosol, heat and a jet;</item>
/// <item>forces — buoyancy (warm air rises, chilled air and heavy fog sink), effect fans;</item>
/// <item>semi-Lagrangian self-advection of the velocity (RK2 back-trace, trilinear interpolation);</item>
/// <item>optional viscosity;</item>
/// <item>pressure projection (conjugate gradient preconditioned with a geometric multigrid V-cycle) with free-slip
/// solids and open faces;</item>
/// <item>advection of density and temperature by the divergence-free velocity (MacCormack with limiter by default) with
/// a conservation fixer for smoke mass and heat, optional diffusion, then dissipation and cooling.</item>
/// </list>
/// The GPU implementation in the renderer is tested against this class, so it favours clarity and physical units:
/// density is the relative aerosol concentration whose scattering coefficient is density × <see
/// cref="SmokeSimulationSettings.ScatteringAtFullDensity"/>, temperatures are kelvin above ambient.
/// </summary>
/// <remarks>
/// <para><b>Determinism.</b> The same settings, obstacles, emitters and sequence of <c>Step</c> time deltas give
/// bit-identical results on the same machine, whatever the thread count: work is split over Z slices that never write
/// to shared data, and reductions sum per-slice partial results in slice order.</para>
/// <para><b>Threading.</b> Not thread-safe; call from one thread (the engine's simulation thread). Each step uses
/// <see cref="Parallel.For(int, int, ParallelOptions, Action{int})"/> internally.</para>
/// </remarks>
public sealed partial class SmokeSimulation
{
    /// <summary>Grids with fewer cells than this run single-threaded (thread hand-off would cost more than it saves).</summary>
    private const int ParallelThreshold = 8192;

    private readonly SmokeSimulationSettings _settings;
    private readonly List<Issue> _issues = new();
    private readonly int _nx, _ny, _nz, _cellCount;
    private readonly int _uCount, _vCount, _wCount;
    private readonly float _h, _invH;
    private readonly Vector3 _origin;
    private readonly Aabb _bounds;
    private readonly double _dt;
    private readonly float _massPerDensity;
    private readonly ParallelOptions _parallelOptions;
    private readonly bool _parallel;

    // State.
    private float[] _u, _v, _w;
    private float[] _density, _temperature;
    private readonly float[] _pressure;
    private readonly byte[] _solid;

    // Scratch buffers (double buffers and solver vectors).
    private float[] _u2, _v2, _w2;
    private float[] _density2, _temperature2;
    private readonly float[] _densityMin, _densityMax, _temperatureMin, _temperatureMax;
    private readonly float[] _rhs, _residual, _search, _product, _zeroRow;
    private readonly Vector3[] _cellVelocity;
    private readonly PressureMultigrid? _multigrid;
    private readonly float[] _z;
    private readonly double[] _slicePartials;
    private readonly double[] _slicePartials2;
    private readonly float[] _sliceMax;

    private double _time;
    private double _accumulator;
    private long _stepCount;
    private SmokeStepStatistics _statistics;
    private SmokeStageTimings _timings;
    private int _obstacleCount;

    /// <summary>Creates a simulation for <paramref name="settings"/> (invalid values are replaced and reported in <see cref="Issues"/>).</summary>
    public SmokeSimulation(SmokeSimulationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = Sanitize(settings, _issues);
        (_nx, _ny, _nz, _h, _bounds) = LayoutGrid(_settings, _issues);
        _invH = 1f / _h;
        _origin = _bounds.Min;
        _cellCount = _nx * _ny * _nz;
        _uCount = (_nx + 1) * _ny * _nz;
        _vCount = _nx * (_ny + 1) * _nz;
        _wCount = _nx * _ny * (_nz + 1);
        _dt = _settings.TimeStep;
        _massPerDensity = _settings.ScatteringAtFullDensity / _settings.MassScatteringCoefficient;

        _u = new float[_uCount];
        _v = new float[_vCount];
        _w = new float[_wCount];
        _u2 = new float[_uCount];
        _v2 = new float[_vCount];
        _w2 = new float[_wCount];
        _density = new float[_cellCount];
        _temperature = new float[_cellCount];
        _density2 = new float[_cellCount];
        _temperature2 = new float[_cellCount];
        _densityMin = new float[_cellCount];
        _densityMax = new float[_cellCount];
        _temperatureMin = new float[_cellCount];
        _temperatureMax = new float[_cellCount];
        _pressure = new float[_cellCount];
        _rhs = new float[_cellCount];
        _residual = new float[_cellCount];
        _search = new float[_cellCount];
        _zeroRow = new float[_nx];
        _cellVelocity = new Vector3[_cellCount];
        _product = new float[_cellCount];
        _solid = new byte[_cellCount];
        _slicePartials = new double[_nz + 1];
        _slicePartials2 = new double[_nz + 1];
        _sliceMax = new float[_nz + 1];

        _parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = _settings.MaxDegreeOfParallelism > 0 ? _settings.MaxDegreeOfParallelism : -1,
        };
        _parallel = _cellCount >= ParallelThreshold && _settings.MaxDegreeOfParallelism != 1;
        if (_settings.PressurePreconditioner == PressurePreconditioner.Multigrid)
        {
            _multigrid = new PressureMultigrid(_nx, _ny, _nz, _parallelOptions, _parallel);
            _z = new float[_cellCount];
        }
        else
        {
            _z = Array.Empty<float>();
        }

        InitializeBoundaries();
        InitializeKernels();
        RebuildSolverTopology();
    }

    /// <summary>The settings in use, after sanitising.</summary>
    public SmokeSimulationSettings Settings => _settings;

    /// <summary>Problems found in the settings or obstacles (replaced values, enlarged cells, skipped boxes).</summary>
    public IReadOnlyList<Issue> Issues => _issues;

    /// <summary>Cells along X.</summary>
    public int SizeX => _nx;

    /// <summary>Cells along Y.</summary>
    public int SizeY => _ny;

    /// <summary>Cells along Z.</summary>
    public int SizeZ => _nz;

    /// <summary>Total number of cells.</summary>
    public int CellCount => _cellCount;

    /// <summary>Cell edge length actually used, m (may exceed the requested size because of the budget).</summary>
    public float CellSize => _h;

    /// <summary>World-space box covered by the grid, m (the requested box rounded up to whole cells, same centre).</summary>
    public Aabb Bounds => _bounds;

    /// <summary>Fixed sub-step length, s.</summary>
    public double TimeStep => _dt;

    /// <summary>Simulated time since creation or <see cref="Reset"/>, s.</summary>
    public double Time => _time;

    /// <summary>Fixed sub-steps run since creation or <see cref="Reset"/>.</summary>
    public long StepCount => _stepCount;

    /// <summary>Diagnostics of the last step.</summary>
    public SmokeStepStatistics LastStatistics => _statistics;

    /// <summary>Wall-clock time of each stage of the last fixed sub-step (for profiling overlays; not part of the state).</summary>
    public SmokeStageTimings LastTimings => _timings;

    /// <summary>Number of obstacle boxes applied by the last <see cref="SetObstacles"/> call.</summary>
    public int ObstacleCount => _obstacleCount;

    /// <summary>
    /// Smoke density per cell, x fastest, then y, then z (the layout of the renderer's <c>VolumeGrid</c>). Relative
    /// aerosol concentration: 1 scatters <see cref="SmokeSimulationSettings.ScatteringAtFullDensity"/> per metre; values
    /// above 1 occur close to fog nozzles. The span stays valid until the next step.
    /// </summary>
    public ReadOnlySpan<float> Density => _density;

    /// <summary>Air temperature above ambient per cell, K (same layout as <see cref="Density"/>).</summary>
    public ReadOnlySpan<float> TemperatureExcess => _temperature;

    /// <summary>Aerosol mass concentration at density 1, kg/m³ (= σₛ at full density / mass scattering coefficient).</summary>
    public float MassConcentrationAtFullDensity => _massPerDensity;

    /// <summary>Whether cell (<paramref name="i"/>, <paramref name="j"/>, <paramref name="k"/>) is solid.</summary>
    public bool IsSolid(int i, int j, int k) =>
        (uint)i < (uint)_nx && (uint)j < (uint)_ny && (uint)k < (uint)_nz && _solid[CellIndex(i, j, k)] != 0;

    /// <summary>
    /// Advances the simulation by <paramref name="deltaTime"/> seconds using fixed sub-steps of <see cref="TimeStep"/>;
    /// time that does not fill a whole sub-step is carried over to the next call. The emitters apply to every sub-step
    /// of this call. Returns the number of sub-steps run. Non-finite or negative time is ignored.
    /// </summary>
    /// <param name="deltaTime">Frame time, s.</param>
    /// <param name="emitters">Hazers, fog machines, fans and plumes active during this frame.</param>
    public int Step(double deltaTime, ReadOnlySpan<SmokeEmitter> emitters)
    {
        if (!double.IsFinite(deltaTime) || deltaTime <= 0)
        {
            _statistics = _statistics with { SubSteps = 0, DroppedTime = 0 };
            return 0;
        }

        _accumulator += deltaTime;
        int steps = 0;
        double dropped = 0;
        // The small tolerance keeps e.g. 30 calls of 1/30 s from losing a step to rounding.
        while (_accumulator + 1e-9 * _dt >= _dt)
        {
            if (steps >= _settings.MaxSubStepsPerCall)
            {
                double whole = Math.Floor((_accumulator + 1e-9 * _dt) / _dt) * _dt;
                dropped = whole;
                _accumulator = Math.Max(0, _accumulator - whole);
                break;
            }

            RunSubStep(emitters);
            _accumulator = Math.Max(0, _accumulator - _dt);
            steps++;
        }

        _statistics = _statistics with { SubSteps = steps, DroppedTime = dropped };
        return steps;
    }

    /// <summary>Runs exactly one fixed sub-step of <see cref="TimeStep"/> seconds (for offline rendering and tests).</summary>
    /// <param name="emitters">Emitters active during the sub-step.</param>
    public void StepOnce(ReadOnlySpan<SmokeEmitter> emitters)
    {
        RunSubStep(emitters);
        _statistics = _statistics with { SubSteps = 1, DroppedTime = 0 };
    }

    /// <summary>Clears velocity, smoke, temperature, pressure and time. Obstacles are kept.</summary>
    public void Reset()
    {
        Array.Clear(_u);
        Array.Clear(_v);
        Array.Clear(_w);
        Array.Clear(_density);
        Array.Clear(_temperature);
        Array.Clear(_pressure);
        _time = 0;
        _accumulator = 0;
        _stepCount = 0;
        _statistics = default;
        _inflowVelocity = Vector3.Zero;
        EnforceBoundaryVelocities(_u, _v, _w);
    }

    /// <summary>
    /// Adds smoke in a sphere: density rises by <paramref name="densityIncrement"/> at the centre, falling smoothly to 0
    /// at <paramref name="radius"/>, and the air there is mixed toward <paramref name="temperatureExcess"/> K above
    /// ambient (0 leaves the temperature alone). For scripted puffs, initial conditions and tests.
    /// </summary>
    public void AddSmoke(Vector3 center, float radius, float densityIncrement, float temperatureExcess = 0f)
    {
        if (!IsFinite(center) || !(radius > 0f) || !float.IsFinite(radius) || !float.IsFinite(densityIncrement)
            || !float.IsFinite(temperatureExcess))
            return;
        GetCellRange(center - new Vector3(radius), center + new Vector3(radius), out var lo, out var hi);
        float invR2 = 1f / (radius * radius);
        float tMax = _settings.MaxTemperatureExcess;
        for (int k = lo.Z; k <= hi.Z; k++)
        {
            for (int j = lo.Y; j <= hi.Y; j++)
            {
                for (int i = lo.X; i <= hi.X; i++)
                {
                    int c = CellIndex(i, j, k);
                    if (_solid[c] != 0)
                        continue;
                    float q2 = Vector3.DistanceSquared(CellCenter(i, j, k), center) * invR2;
                    if (q2 >= 1f)
                        continue;
                    float w = (1f - q2) * (1f - q2);
                    _density[c] = MathF.Max(0f, _density[c] + densityIncrement * w);
                    if (temperatureExcess != 0f)
                        _temperature[c] = Math.Clamp(_temperature[c] + (temperatureExcess - _temperature[c]) * w, -tMax, tMax);
                }
            }
        }
    }

    /// <summary>Sets the smoke density of every fluid cell to <paramref name="density"/> (e.g. a pre-hazed room).</summary>
    public void Fill(float density)
    {
        float d = float.IsFinite(density) ? MathF.Max(density, 0f) : 0f;
        for (int c = 0; c < _cellCount; c++)
            _density[c] = _solid[c] != 0 ? 0f : d;
    }

    /// <summary>Copies <see cref="Density"/> into <paramref name="destination"/> (length ≥ <see cref="CellCount"/>).</summary>
    public void CopyDensityTo(Span<float> destination)
    {
        if (destination.Length < _cellCount)
            throw new ArgumentException($"Destination holds {destination.Length} values, {_cellCount} needed.", nameof(destination));
        _density.AsSpan().CopyTo(destination);
    }

    /// <summary>Smoke density at a world point by trilinear interpolation; 0 outside the box and inside solids.</summary>
    public float DensityAt(Vector3 point)
    {
        if (!IsFinite(point) || !_bounds.Contains(point))
            return 0f;
        var g = (point - _origin) * _invH;
        return SampleScalarPlain(_density, g.X - 0.5f, g.Y - 0.5f, g.Z - 0.5f);
    }

    /// <summary>Absolute air temperature at a world point, K (ambient outside the box).</summary>
    public float TemperatureAt(Vector3 point)
    {
        if (!IsFinite(point) || !_bounds.Contains(point))
            return _settings.AmbientTemperature;
        var g = (point - _origin) * _invH;
        return _settings.AmbientTemperature + SampleScalarPlain(_temperature, g.X - 0.5f, g.Y - 0.5f, g.Z - 0.5f);
    }

    /// <summary>Air velocity at a world point, m/s (zero outside the box).</summary>
    public Vector3 VelocityAt(Vector3 point)
    {
        if (!IsFinite(point) || !_bounds.Contains(point))
            return Vector3.Zero;
        return SampleVelocity((point - _origin) * _invH);
    }

    /// <summary>Total aerosol mass in the box, kg: Σ density × cell volume × <see cref="MassConcentrationAtFullDensity"/>.</summary>
    public double TotalSmokeMass() => SumDensity() * _h * _h * _h * _massPerDensity;

    /// <summary>Density-weighted centre of the smoke, m; the box centre when there is no smoke.</summary>
    public Vector3 SmokeCentreOfMass()
    {
        double sum = 0, sx = 0, sy = 0, sz = 0;
        for (int k = 0; k < _nz; k++)
        {
            for (int j = 0; j < _ny; j++)
            {
                int row = CellIndex(0, j, k);
                for (int i = 0; i < _nx; i++)
                {
                    double d = _density[row + i];
                    sum += d;
                    sx += d * i;
                    sy += d * j;
                    sz += d * k;
                }
            }
        }

        if (!(sum > 0))
            return _bounds.Center;
        return _origin + new Vector3((float)(sx / sum + 0.5), (float)(sy / sum + 0.5), (float)(sz / sum + 0.5)) * _h;
    }

    /// <summary>Mean density over all fluid cells.</summary>
    public double AverageDensity()
    {
        int fluid = 0;
        for (int c = 0; c < _cellCount; c++)
            fluid += _solid[c] == 0 ? 1 : 0;
        return fluid == 0 ? 0 : SumDensity() / fluid;
    }

    /// <summary>Largest |∇·u| over fluid cells, 1/s (diagnostic; one pass over the grid).</summary>
    public float MaxDivergence()
    {
        float max = 0f;
        for (int k = 0; k < _nz; k++)
        {
            for (int j = 0; j < _ny; j++)
            {
                for (int i = 0; i < _nx; i++)
                {
                    if (_solid[CellIndex(i, j, k)] == 0)
                        max = MathF.Max(max, MathF.Abs(Divergence(i, j, k)));
                }
            }
        }

        return max;
    }

    /// <summary>Copies the full state into a new snapshot.</summary>
    public SmokeSimulationSnapshot CaptureSnapshot()
    {
        var snapshot = new SmokeSimulationSnapshot(_cellCount, _uCount, _vCount, _wCount);
        CaptureSnapshot(snapshot);
        return snapshot;
    }

    /// <summary>Copies the full state into <paramref name="snapshot"/>, which must come from a simulation of the same size.</summary>
    public void CaptureSnapshot(SmokeSimulationSnapshot snapshot)
    {
        CheckSnapshot(snapshot);
        snapshot.Dimensions = (_nx, _ny, _nz);
        _u.AsSpan().CopyTo(snapshot.U);
        _v.AsSpan().CopyTo(snapshot.V);
        _w.AsSpan().CopyTo(snapshot.W);
        _density.AsSpan().CopyTo(snapshot.Density);
        _temperature.AsSpan().CopyTo(snapshot.Temperature);
        _pressure.AsSpan().CopyTo(snapshot.Pressure);
        _solid.AsSpan().CopyTo(snapshot.Solid);
        snapshot.Time = _time;
        snapshot.StepCount = _stepCount;
        snapshot.Accumulator = _accumulator;
        snapshot.Airflow = _inflowVelocity;
        snapshot.ObstacleCount = _obstacleCount;
    }

    /// <summary>
    /// Restores a state captured from this simulation (or one with identical settings). Continuing from a restored
    /// snapshot with the same inputs reproduces the original run exactly.
    /// </summary>
    public void RestoreSnapshot(SmokeSimulationSnapshot snapshot)
    {
        CheckSnapshot(snapshot);
        if (snapshot.Dimensions != (_nx, _ny, _nz))
            throw new ArgumentException("The snapshot was captured from a simulation with a different grid.", nameof(snapshot));
        bool solidsChanged = !snapshot.Solid.AsSpan().SequenceEqual(_solid);
        snapshot.U.AsSpan().CopyTo(_u);
        snapshot.V.AsSpan().CopyTo(_v);
        snapshot.W.AsSpan().CopyTo(_w);
        snapshot.Density.AsSpan().CopyTo(_density);
        snapshot.Temperature.AsSpan().CopyTo(_temperature);
        snapshot.Pressure.AsSpan().CopyTo(_pressure);
        _inflowVelocity = snapshot.Airflow;
        if (solidsChanged)
        {
            snapshot.Solid.AsSpan().CopyTo(_solid);
            RebuildSolverTopology();
        }

        _obstacleCount = snapshot.ObstacleCount;

        _time = snapshot.Time;
        _stepCount = snapshot.StepCount;
        _accumulator = snapshot.Accumulator;
        _statistics = default;
    }

    private void CheckSnapshot(SmokeSimulationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Density.Length != _cellCount || snapshot.U.Length != _uCount || snapshot.V.Length != _vCount
            || snapshot.W.Length != _wCount)
            throw new ArgumentException("The snapshot belongs to a simulation with a different grid size.", nameof(snapshot));
    }

    private void RunSubStep(ReadOnlySpan<SmokeEmitter> emitters)
    {
        float dt = (float)_dt;
        long t0 = Stopwatch.GetTimestamp();
        int ignored = ApplySources(emitters, dt);
        long t1 = Stopwatch.GetTimestamp();
        ApplyBuoyancy(dt);
        ApplyJets(dt);
        ApplyAirflow(dt);
        long t2 = Stopwatch.GetTimestamp();
        AdvectVelocity(dt);
        if (_settings.Viscosity > 0f)
            DiffuseVelocity(dt);
        long t3 = Stopwatch.GetTimestamp();
        var (iterations, before, after) = Project();
        long t4 = Stopwatch.GetTimestamp();
        AdvectScalars(dt);
        if (_settings.Diffusivity > 0f)
            DiffuseScalars(dt);
        Dissipate(dt);
        long t5 = Stopwatch.GetTimestamp();
        _time += _dt;
        _stepCount++;
        _statistics = new SmokeStepStatistics(1, iterations, before, after, ignored, 0, _massCorrection, _heatCorrection);
        _timings = new SmokeStageTimings(Ms(t1 - t0), Ms(t2 - t1), Ms(t3 - t2), Ms(t4 - t3), Ms(t5 - t4));

        static float Ms(long ticks) => (float)(ticks * 1000.0 / Stopwatch.Frequency);
    }

    private double SumDensity()
    {
        RunSlices(_nz, _sumDensitySlice);
        double sum = 0;
        for (int k = 0; k < _nz; k++)
            sum += _slicePartials[k];
        return sum;
    }

    private void SumDensitySlice(int k)
    {
        double s = 0;
        int start = k * _nx * _ny, end = start + _nx * _ny;
        for (int c = start; c < end; c++)
            s += _density[c];
        _slicePartials[k] = s;
    }

    private void RunSlices(int count, Action<int> body)
    {
        if (_parallel)
        {
            Parallel.For(0, count, _parallelOptions, body);
        }
        else
        {
            for (int k = 0; k < count; k++)
                body(k);
        }
    }

    private int CellIndex(int i, int j, int k) => i + _nx * (j + _ny * k);

    private int UIndex(int i, int j, int k) => i + (_nx + 1) * (j + _ny * k);

    private int VIndex(int i, int j, int k) => i + _nx * (j + (_ny + 1) * k);

    private int WIndex(int i, int j, int k) => i + _nx * (j + _ny * k);

    private Vector3 CellCenter(int i, int j, int k) => _origin + new Vector3(i + 0.5f, j + 0.5f, k + 0.5f) * _h;

    /// <summary>Inclusive cell index range overlapping the world box [min, max], clamped to the grid.</summary>
    private void GetCellRange(Vector3 min, Vector3 max, out (int X, int Y, int Z) lo, out (int X, int Y, int Z) hi)
    {
        var a = (min - _origin) * _invH;
        var b = (max - _origin) * _invH;
        lo = (ClampIndex(a.X, _nx), ClampIndex(a.Y, _ny), ClampIndex(a.Z, _nz));
        hi = (ClampIndex(b.X, _nx), ClampIndex(b.Y, _ny), ClampIndex(b.Z, _nz));
    }

    private static int ClampIndex(float g, int n) => (int)Math.Clamp(MathF.Floor(Math.Clamp(g, -1f, n + 1f)), 0f, n - 1f);

    private float Divergence(int i, int j, int k) =>
        (_u[UIndex(i + 1, j, k)] - _u[UIndex(i, j, k)]
         + _v[VIndex(i, j + 1, k)] - _v[VIndex(i, j, k)]
         + _w[WIndex(i, j, k + 1)] - _w[WIndex(i, j, k)]) * _invH;

    private static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    private static SmokeSimulationSettings Sanitize(SmokeSimulationSettings s, List<Issue> issues)
    {
        void Report(string name, object value, object replacement) => issues.Add(new Issue(IssueSeverity.Warning, IssueCategory.Scene,
            "simulation.invalid-setting", $"Smoke simulation setting {name} = {value} is invalid; using {replacement}."));

        float Positive(string name, float value, float fallback, float min = 0f)
        {
            if (float.IsFinite(value) && value > min)
                return value;
            Report(name, value, fallback);
            return fallback;
        }

        float NonNegative(string name, float value, float fallback)
        {
            if (float.IsFinite(value) && value >= 0f)
                return value;
            Report(name, value, fallback);
            return fallback;
        }

        int AtLeast(string name, int value, int min, int fallback)
        {
            if (value >= min)
                return value;
            Report(name, value, fallback);
            return fallback;
        }

        var defaults = new SmokeSimulationSettings();
        var bounds = s.Bounds;
        if (!IsFinite(bounds.Min) || !IsFinite(bounds.Max) || bounds.IsEmpty)
        {
            var c = IsFinite(bounds.Min) && IsFinite(bounds.Max) ? bounds.Center : Vector3.Zero;
            var half = defaults.Bounds.Size * 0.5f;
            Report(nameof(s.Bounds), $"{bounds.Min}–{bounds.Max}", "a 20 × 20 × 10 m box");
            bounds = new Aabb(c - half, c + half);
        }

        var airflow = IsFinite(s.Airflow) ? s.Airflow : Vector3.Zero;
        if (!IsFinite(s.Airflow))
            Report(nameof(s.Airflow), s.Airflow, Vector3.Zero);
        float dissipation = float.IsFinite(s.DissipationPerSecond) && s.DissipationPerSecond >= 0f
            ? MathF.Min(s.DissipationPerSecond, 1f)
            : NonNegative(nameof(s.DissipationPerSecond), s.DissipationPerSecond, defaults.DissipationPerSecond);
        var advection = Enum.IsDefined(s.ScalarAdvection) ? s.ScalarAdvection : defaults.ScalarAdvection;
        var preconditioner = Enum.IsDefined(s.PressurePreconditioner) ? s.PressurePreconditioner : defaults.PressurePreconditioner;

        return s with
        {
            Bounds = bounds,
            CellSize = Positive(nameof(s.CellSize), s.CellSize, defaults.CellSize, 1e-4f),
            MaxCellsPerAxis = AtLeast(nameof(s.MaxCellsPerAxis), s.MaxCellsPerAxis, 2, defaults.MaxCellsPerAxis),
            MaxCellCount = AtLeast(nameof(s.MaxCellCount), s.MaxCellCount, 8, defaults.MaxCellCount),
            TimeStep = Positive(nameof(s.TimeStep), s.TimeStep, defaults.TimeStep, 1e-5f),
            MaxSubStepsPerCall = AtLeast(nameof(s.MaxSubStepsPerCall), s.MaxSubStepsPerCall, 1, defaults.MaxSubStepsPerCall),
            OpenFaces = s.OpenFaces & DomainFaces.All,
            AmbientTemperature = Positive(nameof(s.AmbientTemperature), s.AmbientTemperature, defaults.AmbientTemperature, 1f),
            DissipationPerSecond = dissipation,
            TemperatureRelaxationRate = NonNegative(nameof(s.TemperatureRelaxationRate), s.TemperatureRelaxationRate,
                defaults.TemperatureRelaxationRate),
            ScatteringAtFullDensity = Positive(nameof(s.ScatteringAtFullDensity), s.ScatteringAtFullDensity,
                defaults.ScatteringAtFullDensity),
            MassScatteringCoefficient = Positive(nameof(s.MassScatteringCoefficient), s.MassScatteringCoefficient,
                defaults.MassScatteringCoefficient),
            Airflow = airflow,
            AirflowResponseTime = Positive(nameof(s.AirflowResponseTime), s.AirflowResponseTime, defaults.AirflowResponseTime),
            ScalarAdvection = advection,
            PressurePreconditioner = preconditioner,
            Diffusivity = NonNegative(nameof(s.Diffusivity), s.Diffusivity, 0f),
            Viscosity = NonNegative(nameof(s.Viscosity), s.Viscosity, 0f),
            DiffusionIterations = AtLeast(nameof(s.DiffusionIterations), s.DiffusionIterations, 1, defaults.DiffusionIterations),
            PressureTolerance = Positive(nameof(s.PressureTolerance), s.PressureTolerance, defaults.PressureTolerance),
            MaxPressureIterations = AtLeast(nameof(s.MaxPressureIterations), s.MaxPressureIterations, 1,
                defaults.MaxPressureIterations),
            MaxSpeed = Positive(nameof(s.MaxSpeed), s.MaxSpeed, defaults.MaxSpeed),
            MaxTemperatureExcess = Positive(nameof(s.MaxTemperatureExcess), s.MaxTemperatureExcess, defaults.MaxTemperatureExcess),
        };
    }

    private static (int Nx, int Ny, int Nz, float H, Aabb Bounds) LayoutGrid(SmokeSimulationSettings s, List<Issue> issues)
    {
        var size = Vector3.Max(s.Bounds.Size, new Vector3(1e-3f));
        float h = s.CellSize;
        // Smallest cell that keeps every axis and the total within the budget.
        float maxSide = MathF.Max(size.X, MathF.Max(size.Y, size.Z));
        h = MathF.Max(h, maxSide / s.MaxCellsPerAxis);
        h = MathF.Max(h, MathF.Cbrt(size.X * size.Y * size.Z / s.MaxCellCount));
        int nx, ny, nz;
        while (true)
        {
            nx = Cells(size.X, h);
            ny = Cells(size.Y, h);
            nz = Cells(size.Z, h);
            if (Math.Max(nx, Math.Max(ny, nz)) <= s.MaxCellsPerAxis && (long)nx * ny * nz <= s.MaxCellCount)
                break;
            h *= 1.01f;
        }

        if (h > s.CellSize * 1.0001f)
        {
            issues.Add(new Issue(IssueSeverity.Note, IssueCategory.Scene, "simulation.cell-size-increased",
                $"Smoke simulation cell size increased from {s.CellSize:0.###} m to {h:0.###} m to stay within the budget of " +
                $"{s.MaxCellCount} cells ({nx} × {ny} × {nz})."));
        }

        var center = s.Bounds.Center;
        var half = new Vector3(nx, ny, nz) * (h * 0.5f);
        return (nx, ny, nz, h, new Aabb(center - half, center + half));

        static int Cells(float extent, float cell) => Math.Max(2, (int)MathF.Ceiling(extent / cell - 1e-3f));
    }
}
