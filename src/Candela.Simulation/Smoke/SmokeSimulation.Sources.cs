using System.Numerics;

namespace Candela.Simulation;

public sealed partial class SmokeSimulation
{
    /// <summary>Centre-line decay constant B of a turbulent round jet, u_c/U₀ = B·D/x (Hussein, Capp &amp; George 1994).</summary>
    public const float JetDecayConstant = 5.8f;

    /// <summary>Spreading rate S of a turbulent round jet: velocity half-width grows as S·x (Hussein, Capp &amp; George 1994).</summary>
    public const float JetSpreadingRate = 0.094f;

    /// <summary>Time constant with which a fan's or nozzle's jet field is imposed on the air, s.</summary>
    public const float FanResponseTime = 0.25f;

    /// <summary>A jet is applied until its centre-line speed falls below this, m/s (and at most over the box diagonal).</summary>
    private const float FanCutoffSpeed = 0.2f;

    private ResolvedSource[] _sources = new ResolvedSource[8];
    private int _sourceCount;
    private float _dtForKernels;

    private struct ResolvedSource
    {
        public SmokeEmitterKind Kind;

        /// <summary>Centre of the injection sphere (sources) or the outlet centre (fans), world m.</summary>
        public Vector3 Center;

        public Vector3 Direction;

        /// <summary>Injection sphere radius, m (sources).</summary>
        public float Radius;

        /// <summary>Speed held in the source sphere (plumes, grid scale) or outlet speed (jets), m/s.</summary>
        public float Speed;

        /// <summary>Heat added per second (negative = cooling), W.</summary>
        public float HeatRate;

        /// <summary>Density added per second, density·m³/s.</summary>
        public float DensityRate;

        /// <summary>Outlet diameter (fans), m.</summary>
        public float Diameter;

        /// <summary>Length of the applied fan jet, m.</summary>
        public float Reach;
    }

    /// <summary>
    /// Converts the emitters to grid-scale sources. Nozzles are much smaller than a cell, so the aerosol mass rate ṁ and
    /// the heat flux H = ρ·c_p·U₀·πr₀²·ΔT₀ (both × output) are added at exactly their rates to the cells of a sphere of
    /// radius R = max(r₀, h) in front of the nozzle; the nozzle's momentum is imposed as a grid-filtered round jet
    /// (<see cref="ApplyJets"/>). A plume holds its exit velocity U₀·r₀/R in the sphere (momentum flux ρU²πR² of the
    /// nozzle) and is otherwise driven by its own buoyancy. Returns the number of ignored emitters.
    /// </summary>
    private int ApplySources(ReadOnlySpan<SmokeEmitter> emitters, float dt)
    {
        int ignored = 0;
        _sourceCount = 0;
        float tMax = _settings.MaxTemperatureExcess;
        foreach (var e in emitters)
        {
            if (!IsFinite(e.Position) || !Enum.IsDefined(e.Kind))
            {
                ignored++;
                continue;
            }

            float output = float.IsFinite(e.Output) ? Math.Clamp(e.Output, 0f, 1f) : 0f;
            if (output <= 0f)
                continue;
            var dir = e.Direction;
            float len = dir.Length();
            dir = float.IsFinite(len) && len > 1e-6f ? dir / len : Vector3.UnitZ;
            float diameter = float.IsFinite(e.NozzleDiameter) && e.NozzleDiameter > 0f ? e.NozzleDiameter : 0.05f;
            float speed = float.IsFinite(e.ExitSpeed) ? Math.Clamp(e.ExitSpeed, 0f, _settings.MaxSpeed) : 0f;
            float massRate = float.IsFinite(e.MassRate) ? MathF.Max(e.MassRate, 0f) : 0f;
            float temperature = float.IsFinite(e.TemperatureExcess) ? Math.Clamp(e.TemperatureExcess, -tMax, tMax) : 0f;
            if (e.SpeedFollowsOutput)
                speed *= output;

            if (e.Kind == SmokeEmitterKind.Fan)
            {
                if (speed > 0f && !AddJet(e.Position, dir, diameter, speed))
                    ignored++;
                continue;
            }

            float r0 = 0.5f * diameter;
            float radius = MathF.Max(r0, _h);
            float scale = MathF.Min(1f, r0 / radius);
            float area = MathF.PI * r0 * r0;
            float heatRate = SimulationConstants.AirDensity * SimulationConstants.AirSpecificHeat * speed * area * temperature
                * (e.SpeedFollowsOutput ? 1f : output);
            // Smoke machines blow through a nozzle: their momentum is imposed as a resolved round jet (like a fan), and the
            // aerosol and heat go into the cells at the nozzle. A plume is driven by its own buoyancy; its exit velocity
            // is held only in the source sphere.
            bool jet = e.Kind != SmokeEmitterKind.Plume && speed > 0f;
            var source = new ResolvedSource
            {
                Kind = e.Kind,
                Center = e.Position + dir * radius,
                Direction = dir,
                Radius = radius,
                Speed = jet ? 0f : speed * scale,
                HeatRate = float.IsFinite(heatRate) ? heatRate : 0f,
                DensityRate = massRate * output / _massPerDensity,
            };
            if (!InjectSource(source, holdVelocity: !jet, dt))
            {
                ignored++;
                continue;
            }

            if (jet)
                AddJet(e.Position, dir, diameter, speed);
        }

        return ignored;
    }

    private bool InjectSource(in ResolvedSource s, bool holdVelocity, float dt)
    {
        var extent = new Vector3(s.Radius);
        if (!Overlaps(s.Center - extent, s.Center + extent))
            return false;
        GetCellRange(s.Center - extent, s.Center + extent, out var lo, out var hi);
        float invR2 = 1f / (s.Radius * s.Radius);

        // Normalise the kernel over the fluid cells it touches so the injected mass is exact.
        double total = 0;
        for (int k = lo.Z; k <= hi.Z; k++)
        {
            for (int j = lo.Y; j <= hi.Y; j++)
            {
                for (int i = lo.X; i <= hi.X; i++)
                {
                    if (_solid[CellIndex(i, j, k)] == 0)
                        total += Kernel(Vector3.DistanceSquared(CellCenter(i, j, k), s.Center) * invR2);
                }
            }
        }

        if (!(total > 1e-9))
            return false;

        double weightVolume = total * _h * _h * _h;
        float densityPerWeight = (float)(s.DensityRate * dt / weightVolume);
        float kelvinPerWeight = (float)(s.HeatRate * dt
            / (SimulationConstants.AirDensity * SimulationConstants.AirSpecificHeat * weightVolume));
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
                    float w = Kernel(Vector3.DistanceSquared(CellCenter(i, j, k), s.Center) * invR2);
                    if (w <= 0f)
                        continue;
                    _density[c] += densityPerWeight * w;
                    _temperature[c] = Math.Clamp(_temperature[c] + kelvinPerWeight * w, -tMax, tMax);
                }
            }
        }

        if (!holdVelocity)
            return true;
        var target = s.Direction * s.Speed;
        BlendFaces(_u, _uFlags, _nx + 1, _ny, _nz, new Vector3(0f, 0.5f, 0.5f), s.Center, s.Radius, target.X);
        BlendFaces(_v, _vFlags, _nx, _ny + 1, _nz, new Vector3(0.5f, 0f, 0.5f), s.Center, s.Radius, target.Y);
        BlendFaces(_w, _wFlags, _nx, _ny, _nz + 1, new Vector3(0.5f, 0.5f, 0f), s.Center, s.Radius, target.Z);
        return true;
    }

    /// <summary>Smooth injection kernel (1 − q²)² of the squared normalised distance q².</summary>
    private static float Kernel(float q2) => q2 < 1f ? (1f - q2) * (1f - q2) : 0f;

    private bool Overlaps(Vector3 min, Vector3 max) =>
        max.X >= _bounds.Min.X && max.Y >= _bounds.Min.Y && max.Z >= _bounds.Min.Z
        && min.X <= _bounds.Max.X && min.Y <= _bounds.Max.Y && min.Z <= _bounds.Max.Z;

    /// <summary>
    /// Pushes the free faces of one velocity component inside a sphere toward <paramref name="target"/> (the exit
    /// velocity's component). Air that already moves faster in that direction — e.g. accelerated by the plume's own
    /// buoyancy — is left alone, and components perpendicular to the exit direction (target 0) are not touched.
    /// </summary>
    private void BlendFaces(float[] f, byte[] flags, int sx, int sy, int sz, Vector3 offset, Vector3 center, float radius,
        float target)
    {
        if (target == 0f)
            return;
        var g = (center - _origin) * _invH - offset;
        float r = radius * _invH;
        int i0 = Math.Max(0, (int)MathF.Floor(g.X - r)), i1 = Math.Min(sx - 1, (int)MathF.Ceiling(g.X + r));
        int j0 = Math.Max(0, (int)MathF.Floor(g.Y - r)), j1 = Math.Min(sy - 1, (int)MathF.Ceiling(g.Y + r));
        int k0 = Math.Max(0, (int)MathF.Floor(g.Z - r)), k1 = Math.Min(sz - 1, (int)MathF.Ceiling(g.Z + r));
        float invR2 = 1f / (r * r);
        for (int k = k0; k <= k1; k++)
        {
            for (int j = j0; j <= j1; j++)
            {
                for (int i = i0; i <= i1; i++)
                {
                    int n = i + sx * (j + sy * k);
                    if (flags[n] != FaceFree)
                        continue;
                    float dx = i - g.X, dy = j - g.Y, dz = k - g.Z;
                    float w = Kernel((dx * dx + dy * dy + dz * dz) * invR2);
                    float deficit = target > 0f ? MathF.Max(0f, target - f[n]) : MathF.Min(0f, target - f[n]);
                    if (w > 0f)
                        f[n] += deficit * w;
                }
            }
        }
    }

    /// <summary>
    /// Registers a round jet (fan outlet or smoke-machine nozzle) of outlet diameter <paramref name="diameter"/> and
    /// outlet speed <paramref name="speed"/>; it is imposed in <see cref="ApplyJets"/>. The jet is followed until its
    /// centre-line speed falls below <see cref="FanCutoffSpeed"/> (at most the diagonal of the box). Returns false when it
    /// misses the box.
    /// </summary>
    private bool AddJet(Vector3 position, Vector3 direction, float diameter, float speed)
    {
        if (speed <= 0f)
            return true;
        float diagonal = _bounds.Size.Length();
        float reach = MathF.Min(diagonal, JetDecayConstant * diameter * speed / FanCutoffSpeed);
        reach = MathF.Max(reach, diameter);
        var end = position + direction * reach;
        float endWidth = 2f * HalfWidth(reach, diameter) + _h;
        var min = Vector3.Min(position - new Vector3(diameter + _h), end - new Vector3(endWidth));
        var max = Vector3.Max(position + new Vector3(diameter + _h), end + new Vector3(endWidth));
        if (!Overlaps(min, max))
            return false;
        if (_sourceCount == _sources.Length)
            Array.Resize(ref _sources, _sources.Length * 2);
        _sources[_sourceCount++] = new ResolvedSource
        {
            Kind = SmokeEmitterKind.Fan,
            Center = position,
            Direction = direction,
            Speed = speed,
            Diameter = diameter,
            Reach = reach,
        };
        return true;
    }

    /// <summary>Velocity half-width of a round jet of outlet diameter D at distance x: D/2 + S·x.</summary>
    private static float HalfWidth(float x, float diameter) => 0.5f * diameter + JetSpreadingRate * x;

    /// <summary>
    /// Imposes the mean velocity field of a turbulent round jet for every active fan and nozzle: centre-line speed U₀ in
    /// the potential core (x &lt; B·D), U₀·B·D/x beyond, Gaussian radial profile exp(−ln2·r²/b²) with half-width
    /// b = D/2 + S·x. Where the jet is narrower than a cell it is widened to b' = h and slowed to u·b/b', which keeps its
    /// momentum flux (∝ u²b²) — the grid-filtered jet. Only the axial component is imposed: each velocity component is
    /// relaxed toward its share of the jet with time constant <see cref="FanResponseTime"/>, weighted by the profile and
    /// by the squared direction cosine, so cross flow and buoyancy can still bend the jet and the surrounding air is
    /// entrained rather than held.
    /// </summary>
    private void ApplyJets(float dt)
    {
        float alpha = 1f - MathF.Exp(-dt / FanResponseTime);
        for (int n = 0; n < _sourceCount; n++)
        {
            ref readonly var jet = ref _sources[n];
            ApplyJetComponent(jet, _u, _uFlags, _nx + 1, _ny, _nz, new Vector3(0f, 0.5f, 0.5f), jet.Direction.X, alpha);
            ApplyJetComponent(jet, _v, _vFlags, _nx, _ny + 1, _nz, new Vector3(0.5f, 0f, 0.5f), jet.Direction.Y, alpha);
            ApplyJetComponent(jet, _w, _wFlags, _nx, _ny, _nz + 1, new Vector3(0.5f, 0.5f, 0f), jet.Direction.Z, alpha);
        }
    }

    private void ApplyJetComponent(in ResolvedSource jet, float[] f, byte[] flags, int sx, int sy, int sz, Vector3 offset,
        float axisComponent, float alpha)
    {
        float weight = axisComponent * axisComponent;
        if (weight < 1e-6f)
            return;
        var end = jet.Center + jet.Direction * jet.Reach;
        float endWidth = 2f * HalfWidth(jet.Reach, jet.Diameter) + _h;
        var pad = new Vector3(jet.Diameter + _h);
        var min = (Vector3.Min(jet.Center - pad, end - new Vector3(endWidth)) - _origin) * _invH - offset;
        var max = (Vector3.Max(jet.Center + pad, end + new Vector3(endWidth)) - _origin) * _invH - offset;
        int i0 = Math.Max(0, (int)MathF.Floor(min.X)), i1 = Math.Min(sx - 1, (int)MathF.Ceiling(max.X));
        int j0 = Math.Max(0, (int)MathF.Floor(min.Y)), j1 = Math.Min(sy - 1, (int)MathF.Ceiling(max.Y));
        int k0 = Math.Max(0, (int)MathF.Floor(min.Z)), k1 = Math.Min(sz - 1, (int)MathF.Ceiling(max.Z));
        float core = JetDecayConstant * jet.Diameter;
        float minWidth = _h;
        float rate = alpha * weight;
        const float ln2 = 0.693147182f;
        for (int k = k0; k <= k1; k++)
        {
            for (int j = j0; j <= j1; j++)
            {
                for (int i = i0; i <= i1; i++)
                {
                    int idx = i + sx * (j + sy * k);
                    if (flags[idx] != FaceFree)
                        continue;
                    var p = _origin + (new Vector3(i, j, k) + offset) * _h;
                    var rel = p - jet.Center;
                    float x = Vector3.Dot(rel, jet.Direction);
                    if (x < -0.5f * _h || x > jet.Reach)
                        continue;
                    float xa = MathF.Max(x, 0f);
                    float r2 = MathF.Max(0f, rel.LengthSquared() - x * x);
                    float b = HalfWidth(xa, jet.Diameter);
                    float centreline = xa < core ? jet.Speed : jet.Speed * core / xa;
                    if (b < minWidth)
                    {
                        centreline *= b / minWidth;
                        b = minWidth;
                    }

                    float profile = MathF.Exp(-ln2 * r2 / (b * b));
                    if (profile < 0.02f)
                        continue;
                    float target = centreline * profile * axisComponent;
                    f[idx] += (target - f[idx]) * rate * profile;
                }
            }
        }
    }

    /// <summary>
    /// Constant ventilation airflow. The draft is ramped up with time constant
    /// <see cref="SmokeSimulationSettings.AirflowResponseTime"/>: each sub-step the inflow faces move toward the target
    /// airflow and the whole air mass receives the same uniform velocity increment (the body force of the pressure
    /// difference that drives the draft), so the uniform part of the flow stays consistent with the inflow and the
    /// projection only has to deal with obstacles and outlets.
    /// </summary>
    private void ApplyAirflow(float dt)
    {
        if (_airflowTarget == Vector3.Zero && _inflowVelocity == Vector3.Zero)
            return;
        float alpha = 1f - MathF.Exp(-dt / _settings.AirflowResponseTime);
        var increment = (_airflowTarget - _inflowVelocity) * alpha;
        if (increment == Vector3.Zero)
            return;
        _inflowVelocity += increment;
        AddUniform(_u, _uFlags, increment.X, _inflowVelocity.X);
        AddUniform(_v, _vFlags, increment.Y, _inflowVelocity.Y);
        AddUniform(_w, _wFlags, increment.Z, _inflowVelocity.Z);

        static void AddUniform(float[] f, byte[] flags, float increment, float inflow)
        {
            if (increment == 0f)
                return;
            for (int n = 0; n < f.Length; n++)
            {
                byte flag = flags[n];
                if (flag == FaceFree)
                    f[n] += increment;
                else if (flag == FaceInflow)
                    f[n] = inflow;
            }
        }
    }

    /// <summary>
    /// Buoyancy on the vertical faces: a parcel at temperature T in air at T_a accelerates upward with
    /// g·(T − T_a)/T_a (exact for an ideal gas at constant pressure), and the suspended aerosol mass pulls it down with
    /// g·c/ρ_air (c = aerosol mass concentration).
    /// </summary>
    private void ApplyBuoyancy(float dt)
    {
        _dtForKernels = dt;
        RunSlices(_nz + 1, _buoyancySlice);
    }

    private void BuoyancySlice(int k)
    {
        float dt = _dtForKernels;
        float thermal = SimulationConstants.Gravity / _settings.AmbientTemperature;
        float loading = SimulationConstants.Gravity * _massPerDensity / SimulationConstants.AirDensity;
        float vMax = _settings.MaxSpeed;
        int slab = _nx * _ny;
        for (int j = 0; j < _ny; j++)
        {
            for (int i = 0; i < _nx; i++)
            {
                int n = WIndex(i, j, k);
                if (_wFlags[n] != FaceFree)
                    continue;
                int below = n - slab, above = n;
                float t, d;
                if (k == 0)
                {
                    t = _temperature[above];
                    d = _density[above];
                }
                else if (k == _nz)
                {
                    t = _temperature[below];
                    d = _density[below];
                }
                else
                {
                    t = 0.5f * (_temperature[below] + _temperature[above]);
                    d = 0.5f * (_density[below] + _density[above]);
                }

                float w = _w[n] + (thermal * t - loading * d) * dt;
                _w[n] = Math.Clamp(w, -vMax, vMax);
            }
        }
    }

    /// <summary>Smoke dissipation (density × (1 − f)^dt) and relaxation of the temperature toward ambient.</summary>
    private void Dissipate(float dt)
    {
        float f = _settings.DissipationPerSecond;
        _densityFactor = f >= 1f ? 0f : MathF.Exp(MathF.Log(1f - f) * dt);
        _temperatureFactor = MathF.Exp(-_settings.TemperatureRelaxationRate * dt);
        if (_densityFactor == 1f && _temperatureFactor == 1f)
            return;
        RunSlices(_nz, _dissipateSlice);
    }

    private float _densityFactor = 1f, _temperatureFactor = 1f;

    private void DissipateSlice(int k)
    {
        int start = k * _nx * _ny, end = start + _nx * _ny;
        float fd = _densityFactor, ft = _temperatureFactor;
        for (int c = start; c < end; c++)
        {
            _density[c] *= fd;
            _temperature[c] *= ft;
        }
    }
}
