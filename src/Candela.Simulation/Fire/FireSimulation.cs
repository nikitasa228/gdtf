using System.Numerics;
using Candela.Core.Diagnostics;
using Candela.Core.Math;
using Candela.Photometry;

namespace Candela.Simulation;

/// <summary>Live state of one flame device, for UI readouts and tests.</summary>
/// <param name="Id">Device identity.</param>
/// <param name="Valve">Fuel valve opening 0..1 (follows the output with the valve's time constant).</param>
/// <param name="Burning">Whether the valve is commanded open (output &gt; 0 and the burn time not yet used up).</param>
/// <param name="HeatRelease">Heat released by the fuel burning right now, W.</param>
/// <param name="LuminousFlux">Luminous flux, lm.</param>
/// <param name="Height">Current visible flame height along its axis, m (grows after ignition, 0 once burnt out).</param>
/// <param name="Particles">Burning fuel parcels.</param>
public readonly record struct FlameStatus(Guid Id, float Valve, bool Burning, float HeatRelease, float LuminousFlux,
    float Height, int Particles);

/// <summary>
/// CPU reference simulation of flame effects (GENERATOR FIRE devices such as stage flame projectors). Each device's
/// fuel valve follows its DMX output; while it is open, parcels of burning fuel ("particles") leave the nozzle and
/// travel to the flame tip in <see cref="FireSimulationSettings.IgnitionTime"/>, so a flame grows over ≈ 0.2 s after
/// ignition and is gone ≈ 0.2 s after the valve closes. The flame height follows the fuel flow by Heskestad's
/// correlation; the column sways at the flame's pulsation frequency and the parcels scatter across its width with
/// deterministic, hash-based turbulence.
/// <para>
/// Outputs, rebuilt after every step: point-light samples along each flame (black-body colour of the soot radiation
/// temperature, intensity from Φ = χ_r·Q·K(T_r), see <see cref="FlameModel"/>), a temperature volume per flame for the
/// volumetric renderer, and plume emitters along each flame that inject its soot and convective heat into a
/// <see cref="SmokeSimulation"/>.
/// </para>
/// </summary>
/// <remarks>
/// Deterministic: identical settings, emitter sequences and <see cref="Step"/> time deltas give identical results;
/// <see cref="CaptureSnapshot"/>/<see cref="RestoreSnapshot"/> cover the complete state. Not thread-safe. Steady-state
/// stepping does not allocate.
/// </remarks>
public sealed class FireSimulation
{
    private const float MinValve = 0.005f;

    private readonly FireSimulationSettings _settings;
    private readonly List<Issue> _issues = new();
    private readonly List<FlameState> _flames = new();
    private readonly Dictionary<Guid, FlameState> _byId = new();
    private readonly List<FlameVolume> _volumes = new();
    private readonly HashSet<Guid> _seen = new();
    private readonly double _dt;
    private readonly float _meanLuminanceWeight;
    private readonly float _referenceLuminance;
    private readonly float _efficacy;
    private FlameLightSample[] _lights = new FlameLightSample[16];
    private int _lightCount;
    private SmokeEmitter[] _smoke = new SmokeEmitter[4];
    private int _smokeCount;
    private float[] _weights = Array.Empty<float>();
    private Segment[] _segments;
    private double _time;
    private double _accumulator;
    private long _stepCount;
    private FireStepStatistics _statistics;

    /// <summary>A parcel of burning fuel.</summary>
    internal struct Particle
    {
        public Vector3 Origin;
        public Vector3 Direction;
        public float Age;
        public float Lifetime;

        /// <summary>Heat release while it burns, W (its fuel energy / lifetime).</summary>
        public float Power;

        /// <summary>Flame height for the fuel flow at its release, m.</summary>
        public float Height;

        public uint Seed;
    }

    /// <summary>Per flame section: light (flux-weighted sums) and heat (power-weighted sums along the axis).</summary>
    private struct Segment
    {
        public double Flux, Px, Py, Pz, X, Z, T, Radius;
        public double Heat, Ax, Ay, Az, Dx, Dy, Dz, Column;
    }

    /// <summary>Persistent state of one device (internal so snapshots can hold copies).</summary>
    internal sealed class FlameState
    {
        public Guid Id;
        public uint IdHash;
        public Vector3 Position;
        public Vector3 Direction = Vector3.UnitZ;
        public float Output;
        public float BurnTime;
        public float NozzleDiameter = FlameEmitter.DefaultNozzleDiameter;
        public float FullHeatRelease;
        public bool Present;
        public bool Armed = true;
        public bool Burning;
        public double BurnElapsed;
        public float Valve;
        public Particle[] Particles = new Particle[64];
        public int Count;
        public uint NextSerial;
        public FlameVolume Volume = null!;
        public float HeatRelease;
        public float Flux;
        public float Height;

        public FlameState Clone()
        {
            var copy = (FlameState)MemberwiseClone();
            copy.Particles = (Particle[])Particles.Clone();
            copy.Volume = Volume.Clone();
            return copy;
        }
    }

    /// <summary>Creates a fire simulation (invalid settings are replaced and reported in <see cref="Issues"/>).</summary>
    public FireSimulation(FireSimulationSettings? settings = null)
    {
        _settings = Sanitize(settings ?? new FireSimulationSettings(), _issues);
        _dt = _settings.TimeStep;
        _segments = new Segment[_settings.LightSamplesPerFlame];
        _referenceLuminance = MathF.Max(BlackbodyTable.Luminance(_settings.RadiationTemperature), 1e-30f);
        _efficacy = BlackbodyTable.Efficacy(_settings.RadiationTemperature);
        // Mean relative luminance along a steady flame: the luminous flux of a steady flame is exactly χ_r·Q·K(T_r).
        double sum = 0;
        const int samples = 1000;
        for (int n = 0; n < samples; n++)
            sum += BlackbodyTable.Luminance(ParcelTemperature((n + 0.5f) / samples)) / _referenceLuminance;
        _meanLuminanceWeight = (float)Math.Max(sum / samples, 1e-6);
    }

    /// <summary>The settings in use, after sanitising.</summary>
    public FireSimulationSettings Settings => _settings;

    /// <summary>Problems found in the settings.</summary>
    public IReadOnlyList<Issue> Issues => _issues;

    /// <summary>Simulated time since creation or <see cref="Reset"/>, s.</summary>
    public double Time => _time;

    /// <summary>Fixed sub-steps run since creation or <see cref="Reset"/>.</summary>
    public long StepCount => _stepCount;

    /// <summary>Fixed sub-step length, s.</summary>
    public double TimeStep => _dt;

    /// <summary>Diagnostics of the last step.</summary>
    public FireStepStatistics LastStatistics => _statistics;

    /// <summary>Point-light samples of all flames (valid until the next step).</summary>
    public ReadOnlySpan<FlameLightSample> LightSamples => _lights.AsSpan(0, _lightCount);

    /// <summary>Temperature volumes of the burning flames (valid until the next step).</summary>
    public IReadOnlyList<FlameVolume> Volumes => _volumes;

    /// <summary>
    /// Hot-gas sources along the flames — soot (yield × fuel burnt) and convective heat (1 − χ_r)·Q, one per flame
    /// section — to pass to <see cref="SmokeSimulation.Step"/> together with the other emitters.
    /// </summary>
    public ReadOnlySpan<SmokeEmitter> SmokeEmitters => _smoke.AsSpan(0, _smokeCount);

    /// <summary>Luminous flux of all flames, lm.</summary>
    public float TotalLuminousFlux
    {
        get
        {
            float sum = 0f;
            foreach (var f in _flames)
                sum += f.Flux;
            return sum;
        }
    }

    /// <summary>Heat release of all flames, W.</summary>
    public float TotalHeatRelease
    {
        get
        {
            float sum = 0f;
            foreach (var f in _flames)
                sum += f.HeatRelease;
            return sum;
        }
    }

    /// <summary>Box around all flame volumes (<see cref="Aabb.Empty"/> when nothing burns).</summary>
    public Aabb CombinedBounds
    {
        get
        {
            var box = Aabb.Empty;
            foreach (var v in _volumes)
                box.Include(v.Bounds);
            return box;
        }
    }

    /// <summary>State of device <paramref name="id"/>; false when it is not burning and has nothing left to burn.</summary>
    public bool TryGetStatus(Guid id, out FlameStatus status)
    {
        if (_byId.TryGetValue(id, out var f))
        {
            status = new FlameStatus(f.Id, f.Valve, f.Burning, f.HeatRelease, f.Flux, f.Height, f.Count);
            return true;
        }

        status = default;
        return false;
    }

    /// <summary>
    /// Advances by <paramref name="deltaTime"/> seconds in fixed sub-steps (the remainder carries over to the next call)
    /// and rebuilds the outputs. Devices missing from <paramref name="emitters"/> are treated as output 0: their valve
    /// closes and their flame burns out. Returns the number of sub-steps run.
    /// </summary>
    /// <param name="deltaTime">Frame time, s (non-finite or negative values are ignored).</param>
    /// <param name="emitters">Flame devices this frame.</param>
    public int Step(double deltaTime, ReadOnlySpan<FlameEmitter> emitters)
    {
        int ignored = ApplyEmitters(emitters);
        int steps = 0;
        double dropped = 0;
        if (double.IsFinite(deltaTime) && deltaTime > 0)
        {
            _accumulator += deltaTime;
            while (_accumulator + 1e-9 * _dt >= _dt)
            {
                if (steps >= _settings.MaxSubStepsPerCall)
                {
                    dropped = Math.Floor((_accumulator + 1e-9 * _dt) / _dt) * _dt;
                    _accumulator = Math.Max(0, _accumulator - dropped);
                    break;
                }

                SubStep((float)_dt);
                _accumulator = Math.Max(0, _accumulator - _dt);
                steps++;
            }
        }

        RemoveExtinguished();
        BuildOutputs();
        int particles = 0, active = 0;
        foreach (var f in _flames)
        {
            particles += f.Count;
            active += f.Count > 0 || f.Valve > 0f ? 1 : 0;
        }

        _statistics = new FireStepStatistics(steps, active, particles, ignored, dropped);
        return steps;
    }

    /// <summary>Removes every flame and clears time; settings are kept.</summary>
    public void Reset()
    {
        _flames.Clear();
        _byId.Clear();
        _time = 0;
        _accumulator = 0;
        _stepCount = 0;
        _statistics = default;
        BuildOutputs();
    }

    /// <summary>Copies the complete state (devices, valves, burn timers, fuel parcels, time).</summary>
    public FireSimulationSnapshot CaptureSnapshot()
    {
        var states = new List<FlameState>(_flames.Count);
        foreach (var f in _flames)
            states.Add(f.Clone());
        return new FireSimulationSnapshot(states, _time, _accumulator, _stepCount, _settings);
    }

    /// <summary>
    /// Restores a state captured from this simulation (or one with equal settings); continuing with the same inputs
    /// reproduces the original run exactly.
    /// </summary>
    public void RestoreSnapshot(FireSimulationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Settings != _settings)
            throw new ArgumentException("The snapshot was captured from a fire simulation with different settings.", nameof(snapshot));
        _flames.Clear();
        _byId.Clear();
        foreach (var o in snapshot.States)
        {
            var f = o.Clone();
            _flames.Add(f);
            _byId.Add(f.Id, f);
        }

        _time = snapshot.Time;
        _accumulator = snapshot.Accumulator;
        _stepCount = snapshot.StepCount;
        _statistics = default;
        BuildOutputs();
    }

    /// <summary>Highest flame temperature at a world point over all flames, K (0 = no flame).</summary>
    public float TemperatureAt(Vector3 point)
    {
        float t = 0f;
        foreach (var v in _volumes)
            t = MathF.Max(t, v.TemperatureAt(point));
        return t;
    }

    /// <summary>
    /// Samples all flame volumes into one grid of <paramref name="sizeX"/> × <paramref name="sizeY"/> ×
    /// <paramref name="sizeZ"/> cells over <paramref name="bounds"/> (cell centres, x fastest; the highest temperature
    /// where flames overlap) — e.g. the renderer's single flame <c>VolumeGrid</c>. Only cells near a flame are visited.
    /// </summary>
    public void RasterizeTemperature(Aabb bounds, int sizeX, int sizeY, int sizeZ, Span<float> destination)
    {
        if (sizeX <= 0 || sizeY <= 0 || sizeZ <= 0)
            throw new ArgumentOutOfRangeException(nameof(sizeX), "Grid sizes must be positive.");
        long count = (long)sizeX * sizeY * sizeZ;
        if (destination.Length < count)
            throw new ArgumentException($"Destination holds {destination.Length} values, {count} needed.", nameof(destination));
        destination[..(int)count].Clear();
        if (bounds.IsEmpty)
            return;
        var size = bounds.Size;
        var cell = new Vector3(size.X / sizeX, size.Y / sizeY, size.Z / sizeZ);
        foreach (var v in _volumes)
        {
            var lo = (v.Bounds.Min - bounds.Min) / cell - new Vector3(0.5f);
            var hi = (v.Bounds.Max - bounds.Min) / cell - new Vector3(0.5f);
            int i0 = Math.Max(0, (int)MathF.Ceiling(lo.X)), i1 = Math.Min(sizeX - 1, (int)MathF.Floor(hi.X));
            int j0 = Math.Max(0, (int)MathF.Ceiling(lo.Y)), j1 = Math.Min(sizeY - 1, (int)MathF.Floor(hi.Y));
            int k0 = Math.Max(0, (int)MathF.Ceiling(lo.Z)), k1 = Math.Min(sizeZ - 1, (int)MathF.Floor(hi.Z));
            for (int k = k0; k <= k1; k++)
            {
                for (int j = j0; j <= j1; j++)
                {
                    for (int i = i0; i <= i1; i++)
                    {
                        var p = bounds.Min + new Vector3(i + 0.5f, j + 0.5f, k + 0.5f) * cell;
                        int index = i + sizeX * (j + sizeY * k);
                        destination[index] = MathF.Max(destination[index], v.TemperatureAt(p));
                    }
                }
            }
        }
    }

    private int ApplyEmitters(ReadOnlySpan<FlameEmitter> emitters)
    {
        foreach (var f in _flames)
            f.Present = false;
        _seen.Clear();
        int ignored = 0;
        foreach (var e in emitters)
        {
            if (!float.IsFinite(e.Position.X) || !float.IsFinite(e.Position.Y) || !float.IsFinite(e.Position.Z)
                || !_seen.Add(e.Id))
            {
                ignored++; // unusable position, or the same device twice in one step (the first one counts)
                continue;
            }

            float output = float.IsNaN(e.Output) ? 0f : Math.Clamp(e.Output, 0f, 1f);
            if (!_byId.TryGetValue(e.Id, out var f))
            {
                if (output <= 0f)
                    continue; // an idle device needs no state
                f = new FlameState { Id = e.Id, IdHash = HashGuid(e.Id) };
                f.Volume = new FlameVolume(e.Id);
                _flames.Add(f);
                _byId.Add(e.Id, f);
            }

            f.Present = true;
            f.Position = e.Position;
            var dir = e.Direction;
            float len = dir.Length();
            f.Direction = float.IsFinite(len) && len > 1e-6f ? dir / len : Vector3.UnitZ;
            f.Output = output;
            f.BurnTime = float.IsFinite(e.BurnTime) && e.BurnTime > 0f ? e.BurnTime : 0f;
            f.NozzleDiameter = float.IsFinite(e.NozzleDiameter) && e.NozzleDiameter > 0f
                ? MathF.Min(e.NozzleDiameter, 1f)
                : FlameEmitter.DefaultNozzleDiameter;
            float height = float.IsFinite(e.NominalHeight) && e.NominalHeight > 0f
                ? MathF.Min(e.NominalHeight, 50f)
                : FlameEmitter.DefaultNominalHeight;
            f.FullHeatRelease = float.IsFinite(e.HeatReleaseRate) && e.HeatReleaseRate > 0f
                ? e.HeatReleaseRate
                : FlameModel.HeatReleaseRateForHeight(height, f.NozzleDiameter);
        }

        return ignored;
    }

    private void SubStep(float dt)
    {
        float valveAlpha = 1f - MathF.Exp(-dt / _settings.ValveResponseTime);
        foreach (var f in _flames)
        {
            float output = f.Present ? f.Output : 0f;
            if (output <= 0f)
            {
                f.Armed = true;
                f.Burning = false;
            }
            else if (f.Armed)
            {
                f.Armed = false;
                f.Burning = true;
                f.BurnElapsed = 0;
            }

            if (f.Burning)
            {
                f.BurnElapsed += dt;
                if (f.BurnTime > 0f && f.BurnElapsed >= f.BurnTime)
                    f.Burning = false;
            }

            float target = f.Burning ? output : 0f;
            f.Valve += (target - f.Valve) * valveAlpha;
            if (target == 0f && f.Valve < 1e-4f)
                f.Valve = 0f;

            AgeParticles(f, dt);
            if (f.Valve > MinValve)
                EmitParticles(f, dt);
        }

        _time += dt;
        _stepCount++;
    }

    private static void AgeParticles(FlameState f, float dt)
    {
        int kept = 0;
        for (int n = 0; n < f.Count; n++)
        {
            var p = f.Particles[n];
            p.Age += dt;
            if (p.Age < p.Lifetime)
                f.Particles[kept++] = p;
        }

        f.Count = kept;
    }

    private void EmitParticles(FlameState f, float dt)
    {
        int n = _settings.ParticlesPerSubStep;
        float heat = f.FullHeatRelease * f.Valve;
        float height = FlameModel.FlameHeight(heat, f.NozzleDiameter);
        if (!(height > 0.01f))
            return;
        if (f.Count + n > f.Particles.Length)
            Array.Resize(ref f.Particles, Math.Max(f.Particles.Length * 2, f.Count + n));
        float energy = heat * dt / n;
        for (int m = 0; m < n; m++)
        {
            uint seed = Hash(f.IdHash ^ Hash(f.NextSerial++ ^ _settings.Seed));
            float lifetime = _settings.IgnitionTime * (0.85f + 0.3f * Random(seed, 1));
            f.Particles[f.Count++] = new Particle
            {
                Origin = f.Position,
                Direction = f.Direction,
                // Spread the releases over the sub-step so the parcels do not travel in clumps.
                Age = dt * (n - m - 0.5f) / n,
                Lifetime = lifetime,
                Power = energy / lifetime,
                Height = height,
                Seed = seed,
            };
        }
    }

    private void RemoveExtinguished()
    {
        for (int n = _flames.Count - 1; n >= 0; n--)
        {
            var f = _flames[n];
            if (f.Count == 0 && f.Valve == 0f && (!f.Present || f.Output <= 0f))
            {
                _byId.Remove(f.Id);
                _flames.RemoveAt(n);
            }
        }
    }

    /// <summary>
    /// Soot radiation temperature of a parcel at progress s (0 = nozzle, 1 = tip), K: the blue, barely luminous base
    /// (0.72·T_r) heats up to T_r within the first 12 % of the flame, stays there to 60 %, and cools to 0.6·T_r at the
    /// tip where the soot burns out.
    /// </summary>
    private float ParcelTemperature(float s)
    {
        float tr = _settings.RadiationTemperature;
        if (s < 0.12f)
            return tr * (0.72f + 0.28f * (s / 0.12f));
        if (s < 0.6f)
            return tr;
        return tr * (1f - 0.4f * MathF.Min(1f, (s - 0.6f) / 0.4f));
    }

    /// <summary>World position of a parcel and its kernel radius.</summary>
    private Vector3 ParcelPosition(in Particle p, float s, uint flameHash, out float radius, out Vector3 axisPoint) =>
        ParcelPosition(p, s, flameHash, out radius, out axisPoint, out _);

    /// <summary>World position of a parcel, its kernel radius, its point on the flame axis and the column radius there.</summary>
    private Vector3 ParcelPosition(in Particle p, float s, uint flameHash, out float radius, out Vector3 axisPoint,
        out float columnRadius)
    {
        float length = p.Height;
        var d = p.Direction;
        // Buoyancy bends a tilted jet toward the vertical: a quadratic drift along the part of +Z normal to the nozzle.
        var bend = Vector3.UnitZ - Vector3.Dot(Vector3.UnitZ, d) * d;
        axisPoint = p.Origin + d * (s * length) + bend * (0.5f * s * s * length);
        Basis(d, out var e1, out var e2);
        float width = _settings.WidthToHeightRatio * length;
        // Column radius: widens from the nozzle, narrows toward the tip.
        float profile = (0.35f + 0.65f * MathF.Sqrt(s)) * (1f - 0.55f * SmoothStep(0.65f, 1f, s));
        float r = 0.5f * width * profile;
        columnRadius = r;
        float angle = 2f * MathF.PI * Random(p.Seed, 2);
        float rr = r * MathF.Sqrt(Random(p.Seed, 3));
        // Sway of the whole column at the pulsation frequency, as a wave travelling up the flame.
        float frequency = FlameModel.PulsationFrequency(width);
        float phase = 2f * MathF.PI * (float)(frequency * _time % 1.0) - 2f * MathF.PI * s;
        float phaseA = 2f * MathF.PI * Random(flameHash, 4), phaseB = 2f * MathF.PI * Random(flameHash, 5);
        float amplitude = 0.06f * length * s * s;
        var sway = e1 * (amplitude * MathF.Sin(phase + phaseA)) + e2 * (amplitude * MathF.Sin(1.37f * phase + phaseB));
        radius = 0.35f * width * (0.6f + 0.6f * s);
        return axisPoint + e1 * (rr * MathF.Cos(angle)) + e2 * (rr * MathF.Sin(angle)) + sway;
    }

    private void BuildOutputs()
    {
        _lightCount = 0;
        _smokeCount = 0;
        _volumes.Clear();
        float chi = _settings.RadiantFraction;
        int sampleCount = _segments.Length;
        foreach (var f in _flames)
        {
            f.HeatRelease = 0f;
            f.Flux = 0f;
            f.Height = 0f;
            if (f.Count == 0)
            {
                f.Volume.Clear();
                continue;
            }

            Array.Clear(_segments);
            var box = Aabb.Empty;
            float tipReach = -1f;
            double heat = 0, flux = 0;
            for (int n = 0; n < f.Count; n++)
            {
                ref readonly var p = ref f.Particles[n];
                float s = Math.Clamp(p.Age / p.Lifetime, 0f, 1f);
                var position = ParcelPosition(p, s, f.IdHash, out float radius, out var axisPoint, out float column);
                float temperature = ParcelTemperature(s);
                float weight = BlackbodyTable.Luminance(temperature) / _referenceLuminance / _meanLuminanceWeight;
                double phi = chi * p.Power * _efficacy * weight;
                heat += p.Power;
                flux += phi;
                tipReach = MathF.Max(tipReach, s * p.Height);
                box.Include(position - new Vector3(radius));
                box.Include(position + new Vector3(radius));

                ref var seg = ref _segments[Math.Min(sampleCount - 1, (int)(s * sampleCount))];
                var (x, y) = BlackbodyTable.Chromaticity(temperature);
                seg.Flux += phi;
                seg.Px += phi * position.X;
                seg.Py += phi * position.Y;
                seg.Pz += phi * position.Z;
                seg.X += phi * x / y;
                seg.Z += phi * (1f - x - y) / y;
                seg.T += phi * temperature;
                seg.Radius += phi * radius;

                // Gas flow along the (bent) axis: d + s·(ẑ − (ẑ·d)·d) is the tangent of the parcel's path.
                var tangent = p.Direction + (Vector3.UnitZ - Vector3.Dot(Vector3.UnitZ, p.Direction) * p.Direction) * s;
                double w = p.Power;
                seg.Heat += w;
                seg.Ax += w * axisPoint.X;
                seg.Ay += w * axisPoint.Y;
                seg.Az += w * axisPoint.Z;
                seg.Dx += w * tangent.X;
                seg.Dy += w * tangent.Y;
                seg.Dz += w * tangent.Z;
                seg.Column += w * column;
            }

            f.HeatRelease = (float)heat;
            f.Flux = (float)flux;
            f.Height = MathF.Max(tipReach, 0f);

            for (int n = 0; n < sampleCount; n++)
            {
                ref readonly var seg = ref _segments[n];
                if (!(seg.Flux > 0))
                    continue;
                double sum = seg.X + seg.Flux + seg.Z;
                var color = LightColor.FromXy((float)(seg.X / sum), (float)(seg.Flux / sum));
                var position = new Vector3((float)(seg.Px / seg.Flux), (float)(seg.Py / seg.Flux), (float)(seg.Pz / seg.Flux));
                if (_lightCount == _lights.Length)
                    Array.Resize(ref _lights, _lights.Length * 2);
                _lights[_lightCount++] = new FlameLightSample(f.Id, position, color, FlameModel.MeanIntensity((float)seg.Flux),
                    (float)seg.Flux, (float)(seg.T / seg.Flux), (float)(seg.Radius / seg.Flux));
            }

            RasterizeVolume(f, box);
            _volumes.Add(f.Volume);
            AddPlumes(f);
        }
    }

    /// <summary>
    /// Hands the flame's convective heat (1 − χ_r)·Q and its soot (yield × fuel burnt) to the smoke simulation, released
    /// along the flame — one plume source per section, each with the heat and soot of the fuel burning in it — the way
    /// fire models spread the combustion heat over the flame volume. Each source holds the flame-tip gas velocity
    /// (McCaffrey) over the column's cross-section, with the temperature excess that carries its share of the heat.
    /// Releasing it all at the tip instead would put megawatts into a few grid cells.
    /// </summary>
    private void AddPlumes(FlameState f)
    {
        float chi = _settings.RadiantFraction;
        float velocity = FlameModel.FlameTipVelocity((1f - chi) * f.HeatRelease);
        if (!(velocity > 0f))
            return;
        for (int n = 0; n < _segments.Length; n++)
        {
            ref readonly var seg = ref _segments[n];
            if (!(seg.Heat > 0))
                continue;
            float convective = (float)((1f - chi) * seg.Heat);
            float radius = MathF.Max((float)(seg.Column / seg.Heat), 0.05f);
            float excess = convective
                / (SimulationConstants.AirDensity * SimulationConstants.AirSpecificHeat * velocity * MathF.PI * radius * radius);
            var centre = new Vector3((float)(seg.Ax / seg.Heat), (float)(seg.Ay / seg.Heat), (float)(seg.Az / seg.Heat));
            var direction = new Vector3((float)seg.Dx, (float)seg.Dy, (float)seg.Dz);
            if (_smokeCount == _smoke.Length)
                Array.Resize(ref _smoke, _smoke.Length * 2);
            _smoke[_smokeCount++] = SmokeEmitter.Plume(centre, direction, radius, velocity,
                _settings.SootYield * FlameModel.FuelMassRate((float)seg.Heat), excess);
        }
    }

    /// <summary>
    /// Splats the parcels into the flame's volume: each adds its temperature with the smooth kernel (1 − q²)² of its
    /// radius; a cell gets Σk·T / max(Σk, 1) — the parcels' mean temperature where they overlap, fading to 0 at the
    /// edge of the flame.
    /// </summary>
    private void RasterizeVolume(FlameState f, Aabb box)
    {
        var size = box.Size;
        float h = MathF.Max(_settings.CellSize, MathF.Max(size.X, MathF.Max(size.Y, size.Z)) / _settings.MaxCellsPerAxis);
        int sx = Math.Max(1, (int)MathF.Ceiling(size.X / h));
        int sy = Math.Max(1, (int)MathF.Ceiling(size.Y / h));
        int sz = Math.Max(1, (int)MathF.Ceiling(size.Z / h));
        var centre = box.Center;
        var half = new Vector3(sx, sy, sz) * (0.5f * h);
        var bounds = new Aabb(centre - half, centre + half);
        int cells = sx * sy * sz;
        if (_weights.Length < cells)
            _weights = new float[Math.Max(cells, _weights.Length * 2)];
        var weights = _weights.AsSpan(0, cells);
        var temperature = f.Volume.Prepare(bounds, sx, sy, sz, h, weights);
        float invH = 1f / h;
        for (int n = 0; n < f.Count; n++)
        {
            ref readonly var p = ref f.Particles[n];
            float s = Math.Clamp(p.Age / p.Lifetime, 0f, 1f);
            var position = ParcelPosition(p, s, f.IdHash, out float radius, out _);
            float t = ParcelTemperature(s);
            var g = (position - bounds.Min) * invH - new Vector3(0.5f);
            float r = radius * invH;
            int i0 = Math.Max(0, (int)MathF.Ceiling(g.X - r)), i1 = Math.Min(sx - 1, (int)MathF.Floor(g.X + r));
            int j0 = Math.Max(0, (int)MathF.Ceiling(g.Y - r)), j1 = Math.Min(sy - 1, (int)MathF.Floor(g.Y + r));
            int k0 = Math.Max(0, (int)MathF.Ceiling(g.Z - r)), k1 = Math.Min(sz - 1, (int)MathF.Floor(g.Z + r));
            float invR2 = 1f / (r * r);
            for (int k = k0; k <= k1; k++)
            {
                float dz = k - g.Z;
                for (int j = j0; j <= j1; j++)
                {
                    float dy = j - g.Y;
                    int row = sx * (j + sy * k);
                    for (int i = i0; i <= i1; i++)
                    {
                        float dx = i - g.X;
                        float q2 = (dx * dx + dy * dy + dz * dz) * invR2;
                        if (q2 >= 1f)
                            continue;
                        float w = (1f - q2) * (1f - q2);
                        weights[row + i] += w;
                        temperature[row + i] += w * t;
                    }
                }
            }
        }

        float peak = 0f;
        double emission = 0;
        for (int c = 0; c < cells; c++)
        {
            float t = temperature[c] / MathF.Max(weights[c], 1f);
            temperature[c] = t;
            peak = MathF.Max(peak, t);
            emission += BlackbodyTable.Luminance(t);
        }

        emission *= (double)h * h * h * 4.0 * Math.PI;
        f.Volume.Finish(peak, emission > 0 ? (float)(f.Flux / emission) : 0f, f.Flux);
    }

    private static void Basis(Vector3 d, out Vector3 e1, out Vector3 e2)
    {
        var helper = MathF.Abs(d.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitX;
        e1 = Vector3.Normalize(Vector3.Cross(d, helper));
        e2 = Vector3.Cross(d, e1);
    }

    private static float SmoothStep(float a, float b, float x)
    {
        float t = Math.Clamp((x - a) / (b - a), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>Integer hash (lowbias32 by C. Wellons): deterministic, well mixed, platform independent.</summary>
    private static uint Hash(uint x)
    {
        x ^= x >> 16;
        x *= 0x7feb352du;
        x ^= x >> 15;
        x *= 0x846ca68bu;
        x ^= x >> 16;
        return x;
    }

    /// <summary>Uniform pseudo-random number in [0, 1) for a seed and a channel.</summary>
    private static float Random(uint seed, uint channel) => (Hash(seed ^ (channel * 0x9E3779B9u)) >> 8) * (1f / 16777216f);

    private static uint HashGuid(Guid id)
    {
        Span<byte> bytes = stackalloc byte[16];
        id.TryWriteBytes(bytes);
        uint h = 0x811C9DC5u;
        for (int n = 0; n < 16; n += 4)
            h = Hash(h ^ BitConverter.ToUInt32(bytes[n..]));
        return h;
    }

    private static FireSimulationSettings Sanitize(FireSimulationSettings s, List<Issue> issues)
    {
        var defaults = new FireSimulationSettings();

        void Report(string name, object value, object replacement) => issues.Add(new Issue(IssueSeverity.Warning, IssueCategory.Scene,
            "simulation.invalid-setting", $"Fire simulation setting {name} = {value} is invalid; using {replacement}."));

        float Positive(string name, float value, float fallback, float max = float.MaxValue)
        {
            if (float.IsFinite(value) && value > 0f && value <= max)
                return value;
            Report(name, value, fallback);
            return fallback;
        }

        int Count(string name, int value, int min, int max, int fallback)
        {
            if (value >= min && value <= max)
                return value;
            Report(name, value, fallback);
            return fallback;
        }

        return s with
        {
            TimeStep = Positive(nameof(s.TimeStep), s.TimeStep, defaults.TimeStep, 0.1f),
            MaxSubStepsPerCall = Count(nameof(s.MaxSubStepsPerCall), s.MaxSubStepsPerCall, 1, 1000, defaults.MaxSubStepsPerCall),
            IgnitionTime = Positive(nameof(s.IgnitionTime), s.IgnitionTime, defaults.IgnitionTime, 10f),
            ValveResponseTime = Positive(nameof(s.ValveResponseTime), s.ValveResponseTime, defaults.ValveResponseTime, 10f),
            RadiantFraction = Positive(nameof(s.RadiantFraction), s.RadiantFraction, defaults.RadiantFraction, 1f),
            RadiationTemperature = s.RadiationTemperature is >= 1000f and <= BlackbodyTable.MaxKelvin
                ? s.RadiationTemperature
                : ReportFloat(nameof(s.RadiationTemperature), s.RadiationTemperature, defaults.RadiationTemperature),
            SootYield = float.IsFinite(s.SootYield) && s.SootYield >= 0f && s.SootYield <= 1f
                ? s.SootYield
                : ReportFloat(nameof(s.SootYield), s.SootYield, defaults.SootYield),
            WidthToHeightRatio = Positive(nameof(s.WidthToHeightRatio), s.WidthToHeightRatio, defaults.WidthToHeightRatio, 2f),
            ParticlesPerSubStep = Count(nameof(s.ParticlesPerSubStep), s.ParticlesPerSubStep, 1, 256, defaults.ParticlesPerSubStep),
            LightSamplesPerFlame = Count(nameof(s.LightSamplesPerFlame), s.LightSamplesPerFlame, 1, 64, defaults.LightSamplesPerFlame),
            CellSize = Positive(nameof(s.CellSize), s.CellSize, defaults.CellSize),
            MaxCellsPerAxis = Count(nameof(s.MaxCellsPerAxis), s.MaxCellsPerAxis, 2, 256, defaults.MaxCellsPerAxis),
        };

        float ReportFloat(string name, float value, float fallback)
        {
            Report(name, value, fallback);
            return fallback;
        }
    }
}
