using System.Diagnostics;
using System.Numerics;
using Candela.Core.Diagnostics;
using Candela.Core.Math;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;
using Candela.Gdtf;
using Candela.Photometry;
using Candela.Simulation;

namespace Candela.Engine;

/// <summary>
/// Everything in the scene that moves on its own, driven by the fixtures' device outputs each frame: haze and fog
/// machines and fans feed the smoke simulation, GENERATOR FIRE devices the fire simulation (whose soot and heat feed
/// the smoke), and every device with a specified noise level becomes a sound source scaled by how hard it works.
/// </summary>
/// <remarks>
/// <para><b>Smoke domain.</b> The first enabled <see cref="AtmosphereVolumeComponent"/> of the scene is simulated (more
/// are reported). Without one, and with <see cref="EngineSettings.AutomaticAtmosphere"/>, a box around the scene's
/// content is created the first time a device emits smoke. Walls, floors and ceilings are obstacles.</para>
/// <para><b>Devices.</b> A device emits from its root part. Its nozzle points along its first beam when it has one
/// (foggers with LED rings light their own jet), otherwise along the device's local −Y axis (the model's front by the
/// Candela convention). Flames fire along the beam axis when that points upwards, otherwise straight up; their
/// nominal height is the DMX flame height or 5 m. Fog machines use the <c>_specs</c> power draw as heater power; a
/// device whose name says "low fog" or "ground fog" emits chilled fog.</para>
/// <para><b>Noise.</b> Level = <c>noiseLevelDb</c> + 10·log₁₀(activity): a light's activity is
/// <see cref="EngineSettings.IdleNoiseShare"/> + (1 − share) × its brightest beam's intensity (cooling fans follow the
/// lamp load); an atmospheric device's activity is its largest output and it is silent when it does not run.</para>
/// <para><b>Smoke solver thread.</b> The smoke solver runs on its own threads (<see cref="SmokeWorker"/>): each frame
/// hands it the emitters and the frame time and takes the newest finished density grid, so a solver step never runs on
/// the main thread. With <see cref="RealTime"/> off (the default: command-line renders, tests, offline sequences) every
/// frame waits for its smoke, and the results are bit-identical to stepping the solver in the frame and deterministic
/// for a given sequence of frame times. With <see cref="RealTime"/> on (the editor) the frame does not wait: the
/// solver keeps up with wall-clock time on its own, at a cell size chosen from a time budget, and reports time it had to
/// drop (<see cref="IsSmokeBehindRealTime"/>).</para>
/// <para>Fire and sound are stepped in the frame (they are cheap). Main thread only.</para>
/// </remarks>
public sealed class StageSimulation : IDisposable
{
    /// <summary>Margin added around the scene content for the automatic smoke box, m.</summary>
    public const float AutomaticMargin = 2f;

    /// <summary>Largest edge of the automatic smoke box, m (bigger scenes are cropped around their centre).</summary>
    public const float AutomaticMaxEdge = 60f;

    /// <summary>Nominal flame height used when the device does not give one, m.</summary>
    public const float DefaultFlameHeight = FlameEmitter.DefaultNominalHeight;

    private readonly int _parallelism;
    private readonly List<SmokeEmitter> _smokeEmitters = new();
    private readonly List<FlameEmitter> _flameEmitters = new();
    private readonly List<SoundSource> _soundSources = new();
    private readonly List<Rendering.NoiseSource> _noiseSources = new();
    private readonly List<Rendering.SmokeEmitterMarker> _emitterMarkers = new();
    private readonly Dictionary<Candela.Dmx.FixtureProfile, EmitterCapabilities> _capabilities = new(ReferenceEqualityComparer.Instance);
    private readonly List<ObstacleBox> _obstacles = new();
    private readonly List<Issue> _issues = new();
    private SmokeSimulationSettings? _smokeSettings;
    private bool _domainDirty = true;
    private bool _obstaclesDirty = true;
    private bool _automaticDomain;
    private bool _realTime;
    private int _volumeCount;

    /// <summary>
    /// Creates the simulations; <paramref name="parallelism"/> threads for the smoke solver (−1: all cores, in real time
    /// half of them); <paramref name="realTime"/> sets <see cref="RealTime"/>.
    /// </summary>
    public StageSimulation(int parallelism = -1, bool realTime = false)
    {
        _parallelism = parallelism;
        _realTime = realTime;
        Fire = new FireSimulation();
        Acoustics = new AcousticField();
    }

    /// <summary>
    /// The smoke/haze solver (on its own threads), or null while the scene has no smoke domain. Its
    /// <see cref="SmokeWorker.Current"/> frame is the density the frame shows.
    /// </summary>
    public SmokeWorker? Smoke { get; private set; }

    /// <summary>
    /// Whether the smoke solver keeps up with wall-clock time on its own instead of finishing every frame's smoke before
    /// the frame ends (see the remarks). The editor turns it on and off again while it renders a sequence frame by frame;
    /// switching off returns to the requested cell size.
    /// </summary>
    public bool RealTime
    {
        get => _realTime;
        set
        {
            if (_realTime == value)
                return;
            _realTime = value;
            if (Smoke is { } smoke)
            {
                smoke.Timing = value ? SmokeTiming.RealTime : SmokeTiming.Lockstep;
                if (!value)
                    smoke.Synchronize();
            }
        }
    }

    /// <summary>
    /// True while the real-time smoke does not keep up with wall-clock time (time is being dropped, or the solver failed):
    /// the editor shows a warning. Always false with <see cref="RealTime"/> off.
    /// </summary>
    public bool IsSmokeBehindRealTime => _realTime && Smoke is { IsBehindRealTime: true };

    /// <summary>Share of real time the smoke solver could not simulate over the last few seconds (0 … 1; 0 without smoke).</summary>
    public double SmokeDroppedFraction => Smoke?.DroppedFraction ?? 0;

    /// <summary>
    /// Main-thread time of the last <see cref="Step"/> call, ms: building the emitters, fire, sound, handing the frame to
    /// the smoke solver and taking its result (with <see cref="RealTime"/> off, also waiting for the smoke).
    /// </summary>
    public double LastStepMilliseconds { get; private set; }

    /// <summary>True when <see cref="Smoke"/> is the automatic box around the scene rather than an atmosphere volume.</summary>
    public bool IsAutomaticDomain => Smoke is not null && _automaticDomain;

    /// <summary>The scattering coefficient at smoke density 1 of the simulated volume, 1/m.</summary>
    public float SmokeScatteringAtFullDensity => _smokeSettings?.ScatteringAtFullDensity ?? 0.5f;

    /// <summary>Flames of GENERATOR FIRE devices.</summary>
    public FireSimulation Fire { get; }

    /// <summary>Sound-pressure field of the noise sources (for the noise overlay and readouts).</summary>
    public AcousticField Acoustics { get; }

    /// <summary>Simulated time, s (advances only while not paused).</summary>
    public double Time { get; private set; }

    /// <summary>Smoke emitters (hazers, fog machines, fans, flame plumes) of the last step.</summary>
    public IReadOnlyList<SmokeEmitter> SmokeEmitters => _smokeEmitters;

    /// <summary>Flame devices of the last step.</summary>
    public IReadOnlyList<FlameEmitter> FlameEmitters => _flameEmitters;

    /// <summary>Noise sources of the last step (for the renderer's noise overlay).</summary>
    public IReadOnlyList<Rendering.NoiseSource> NoiseSources => _noiseSources;

    /// <summary>The noise sources of the last step as a span (no copy; valid until the next step).</summary>
    public ReadOnlySpan<Rendering.NoiseSource> NoiseSourceSpan => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_noiseSources);

    /// <summary>
    /// Every hazer, fog machine, fan and flame device of the last step with its nozzle, jet direction and current output,
    /// also the ones that are switched off (output 0): what the smoke overlay marks
    /// (<see cref="Rendering.IRenderWorld.SetSmokeEmitters"/>). A device with several outputs (a hazer with a fan) has
    /// one marker per output. Positions are the nozzle exits the smoke simulation uses.
    /// </summary>
    public IReadOnlyList<Rendering.SmokeEmitterMarker> EmitterMarkers => _emitterMarkers;

    /// <summary>The emitter markers of the last step as a span (no copy; valid until the next step).</summary>
    public ReadOnlySpan<Rendering.SmokeEmitterMarker> EmitterMarkerSpan => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_emitterMarkers);

    /// <summary>Problems of the simulations (bad settings, ignored emitters, extra atmosphere volumes, adapted cell size).</summary>
    public IReadOnlyList<Issue> Issues
    {
        get
        {
            var all = new List<Issue>(_issues);
            if (Smoke is not null) all.AddRange(Smoke.Issues);
            all.AddRange(Fire.Issues);
            return all;
        }
    }

    /// <summary>Smoke density (0 … about 1) at a world point in the current smoke frame; 0 outside the domain or without smoke.</summary>
    public float DensityAt(Vector3 point) => Smoke?.Current.DensityAt(point) ?? 0f;

    /// <summary>Sound pressure level at a point, dB(A) (−∞ when silent).</summary>
    public float NoiseLevelAt(Vector3 point) => Acoustics.LevelAt(point);

    /// <summary>Discards all smoke, flames and time (scene replaced or simulation reset by the user).</summary>
    public void Reset()
    {
        Smoke?.Dispose();
        Smoke = null;
        _smokeSettings = null;
        _automaticDomain = false;
        _domainDirty = true;
        _obstaclesDirty = true;
        Fire.Reset();
        Time = 0;
        _smokeEmitters.Clear();
        _flameEmitters.Clear();
        _noiseSources.Clear();
        _emitterMarkers.Clear();
        _capabilities.Clear();
        _soundSources.Clear();
        Acoustics.SetSources(ReadOnlySpan<SoundSource>.Empty);
        _issues.Clear();
    }

    /// <summary>Stops the smoke solver's threads (the engine disposes its simulation with itself).</summary>
    public void Dispose()
    {
        Smoke?.Dispose();
        Smoke = null;
        _smokeSettings = null;
    }

    /// <summary>Called by the engine for scene changes that can affect the smoke domain or its obstacles.</summary>
    internal void OnSceneChanged(SceneChange change)
    {
        switch (change.Kind)
        {
            case SceneChangeKind.Added or SceneChangeKind.Removed or SceneChangeKind.Reparented or SceneChangeKind.TransformChanged:
                if (change.Entity is { } e)
                {
                    foreach (var d in e.SelfAndDescendants())
                    {
                        if (d.Has<AtmosphereVolumeComponent>()) _domainDirty = true;
                        if (d.Has<WallComponent>()) _obstaclesDirty = true;
                    }
                }
                break;
            case SceneChangeKind.ComponentAdded or SceneChangeKind.ComponentRemoved or SceneChangeKind.ComponentChanged:
                if (change.Component is AtmosphereVolumeComponent) _domainDirty = true;
                if (change.Component is WallComponent) _obstaclesDirty = true;
                break;
        }
    }

    /// <summary>
    /// Advances the simulations by <paramref name="deltaTime"/> seconds: builds emitters and noise sources from the
    /// fixtures' current outputs, then steps fire and smoke (unless <paramref name="paused"/>).
    /// </summary>
    internal void Step(double deltaTime, Scene scene, FixturePoses poses, EngineSettings settings, bool paused)
    {
        long start = Stopwatch.GetTimestamp();
        BuildEmitters(poses, settings);
        UpdateDomain(scene, settings);
        if (Smoke is not null && _obstaclesDirty) UpdateObstacles(scene);

        if (!paused && deltaTime > 0 && double.IsFinite(deltaTime))
        {
            Time += deltaTime;

            if (settings.SimulateFire && (_flameEmitters.Count > 0 || Fire.Volumes.Count > 0 || Fire.LightSamples.Length > 0))
                Fire.Step(deltaTime, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_flameEmitters));
            if (settings.SimulateFire)
                foreach (var e in Fire.SmokeEmitters) _smokeEmitters.Add(e);

            if (settings.SimulateSmoke && Smoke is not null)
                Smoke.Submit(deltaTime, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_smokeEmitters));
        }

        // Real time: the newest smoke the solver has finished (it simulates on while the frame goes on); otherwise the
        // smoke of this very frame, as if the solver had run here.
        if (Smoke is { } smoke)
        {
            if (_realTime) smoke.AcquireLatest();
            else smoke.Synchronize();
        }
        LastStepMilliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }

    private void BuildEmitters(FixturePoses poses, EngineSettings settings)
    {
        _smokeEmitters.Clear();
        _flameEmitters.Clear();
        _soundSources.Clear();
        _noiseSources.Clear();
        _emitterMarkers.Clear();
        foreach (var pose in poses.All)
        {
            var profile = pose.Profile;
            var outputs = pose.State.Outputs;
            bool atmospheric = profile.IsAtmospheric || profile.Category is FixtureCategory.Haze or FixtureCategory.Fog
                or FixtureCategory.Fan or FixtureCategory.GeneratorFire;
            var position = pose.Position;
            if (!IsFinite(position)) continue;
            if (atmospheric || outputs.Any) AddMarkers(pose, profile, outputs, position);

            if (outputs.Any)
            {
                var nozzle = NozzleDirection(pose);
                var exit = position + nozzle * 0.25f;
                if (outputs.Haze > 0f) _smokeEmitters.Add(SmokeEmitter.Hazer(exit, nozzle, Math.Clamp(outputs.Haze, 0f, 1f)));
                if (outputs.Fog > 0f)
                {
                    float heater = (float)(profile.Specs?.PowerConsumptionW ?? SmokeEmitter.DefaultFogHeaterPower);
                    _smokeEmitters.Add(SmokeEmitter.FogMachine(exit, nozzle, Math.Clamp(outputs.Fog, 0f, 1f), heater, IsLowFog(profile)));
                }
                if (outputs.FanSpeed > 0f)
                {
                    var dir = outputs.FanDirection < 0f ? -nozzle : nozzle;
                    _smokeEmitters.Add(SmokeEmitter.Fan(exit, dir, Math.Clamp(outputs.FanSpeed, 0f, 1f)));
                }
            }
            if (outputs.Flame > 0f || Fire.TryGetStatus(pose.Entity.Id, out _))
            {
                float height = float.IsFinite(outputs.FlameHeight) && outputs.FlameHeight > 0f ? outputs.FlameHeight : DefaultFlameHeight;
                _flameEmitters.Add(FlameEmitter.Create(pose.Entity.Id, position, FlameDirection(pose), Math.Clamp(outputs.Flame, 0f, 1f), height));
            }

            if (settings.SimulateNoise && profile.Specs?.NoiseLevelDb is { } db && db > 0 && double.IsFinite(db))
            {
                float activity;
                if (atmospheric)
                {
                    activity = Math.Max(Math.Max(outputs.Fog, outputs.Haze), Math.Max(outputs.FanSpeed, outputs.Flame));
                }
                else
                {
                    float brightest = 0f;
                    var beams = pose.State.Beams;
                    for (int b = 0; b < beams.Count; b++)
                        if (beams[b].Intensity > brightest) brightest = beams[b].Intensity;
                    float share = settings.IdleNoiseShare;
                    activity = share + (1f - share) * Math.Clamp(brightest, 0f, 1f);
                }
                if (activity > 1e-4f && float.IsFinite(activity))
                {
                    float level = (float)db + 10f * MathF.Log10(Math.Clamp(activity, 1e-4f, 1f));
                    _soundSources.Add(new SoundSource(position, level));
                    _noiseSources.Add(new Rendering.NoiseSource(position, level));
                }
            }
        }
        Acoustics.SetSources(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_soundSources));
    }

    private void UpdateDomain(Scene scene, EngineSettings settings)
    {
        if (_domainDirty)
        {
            _domainDirty = false;
            AtmosphereVolumeComponent? volume = null;
            Entity? owner = null;
            _volumeCount = 0;
            foreach (var (entity, component) in scene.Query<AtmosphereVolumeComponent>())
            {
                if (!component.Enabled) continue;
                _volumeCount++;
                if (volume is null)
                {
                    volume = component;
                    owner = entity;
                }
            }
            _issues.RemoveAll(i => i.Code == EngineIssueCodes.ExtraAtmosphere);
            if (_volumeCount > 1 && owner is not null)
            {
                _issues.Add(new Issue(IssueSeverity.Warning, IssueCategory.Scene, EngineIssueCodes.ExtraAtmosphere,
                    $"The scene has {_volumeCount} atmosphere volumes; only '{owner.Name}' is simulated.", owner.Id));
            }
            if (volume is not null && owner is not null)
            {
                var wanted = SmokeSimulationSettings.FromComponent(Sanitize(volume), owner.WorldPosition) with
                {
                    MaxDegreeOfParallelism = _parallelism,
                };
                if (Smoke is null || _automaticDomain || _smokeSettings != wanted) CreateSmoke(wanted, automatic: false);
            }
            else if (Smoke is not null && !_automaticDomain)
            {
                Smoke.Dispose();
                Smoke = null;
                _smokeSettings = null;
            }
        }

        if (Smoke is null && settings.AutomaticAtmosphere && settings.SimulateSmoke && AnySmokeOutput())
        {
            var bounds = SceneBounds.Compute(scene);
            if (bounds.IsEmpty) bounds = new Aabb(new Vector3(-5f, -5f, 0f), new Vector3(5f, 5f, 4f));
            var min = bounds.Min - new Vector3(AutomaticMargin);
            var max = bounds.Max + new Vector3(AutomaticMargin);
            min.Z = MathF.Min(bounds.Min.Z, 0f);
            max.Z = MathF.Max(max.Z, min.Z + 3f);
            var centre = (min + max) * 0.5f;
            var half = Vector3.Min((max - min) * 0.5f, new Vector3(AutomaticMaxEdge * 0.5f));
            CreateSmoke(new SmokeSimulationSettings
            {
                Bounds = new Aabb(centre - half, centre + half),
                CellSize = settings.AutomaticAtmosphereCellSize,
                MaxDegreeOfParallelism = _parallelism,
            }, automatic: true);
        }
    }

    private void CreateSmoke(SmokeSimulationSettings settings, bool automatic)
    {
        // One solver thread per stage: a changed atmosphere volume replaces the simulation, not the thread.
        if (Smoke is { } smoke)
            smoke.Reconfigure(settings);
        else
            Smoke = new SmokeWorker(settings, new SmokeWorkerOptions { Timing = _realTime ? SmokeTiming.RealTime : SmokeTiming.Lockstep });
        _smokeSettings = settings;
        _automaticDomain = automatic;
        _obstaclesDirty = true;
    }

    private void UpdateObstacles(Scene scene)
    {
        _obstaclesDirty = false;
        _obstacles.Clear();
        foreach (var (entity, wall) in scene.Query<WallComponent>())
        {
            if (!TryWallBox(wall, out var min, out var max)) continue;
            var box = ObstacleBox.FromLocalBox(min, max, entity.WorldMatrix);
            if (box.IsValid) _obstacles.Add(box);
        }
        Smoke!.SetObstacles(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_obstacles));
    }

    /// <summary>Local box of a wall/floor/ceiling (see <see cref="WallKind"/>); a zero thickness counts as 2 cm.</summary>
    internal static bool TryWallBox(WallComponent wall, out Vector3 min, out Vector3 max)
    {
        float w = Positive(wall.Width), h = Positive(wall.Height), t = MathF.Max(Positive(wall.Thickness), 0.02f);
        switch (wall.Kind)
        {
            case WallKind.Floor:
                min = new Vector3(-w / 2, -h / 2, -t);
                max = new Vector3(w / 2, h / 2, 0f);
                break;
            case WallKind.Ceiling:
                min = new Vector3(-w / 2, -h / 2, 0f);
                max = new Vector3(w / 2, h / 2, t);
                break;
            default:
                min = new Vector3(-w / 2, 0f, 0f);
                max = new Vector3(w / 2, t, h);
                break;
        }
        return w > 0f && h > 0f;
    }

    private bool AnySmokeOutput()
    {
        foreach (var e in _smokeEmitters)
            if (e.Output > 0f && e.Kind != SmokeEmitterKind.Fan) return true;
        return _flameEmitters.Count > 0 && Fire.LightSamples.Length > 0;
    }

    /// <summary>What an atmospheric device can emit, from its DMX functions and its category (cached per profile).</summary>
    [Flags]
    private enum EmitterCapabilities
    {
        None = 0,
        Haze = 1,
        Fog = 2,
        Fan = 4,
        Flame = 8,
    }

    private EmitterCapabilities CapabilitiesOf(Candela.Dmx.FixtureProfile profile)
    {
        if (_capabilities.TryGetValue(profile, out var known)) return known;
        var c = EmitterCapabilities.None;
        foreach (var f in profile.Functions)
        {
            c |= f.Class.Kind switch
            {
                Candela.Dmx.AttributeKind.Haze => EmitterCapabilities.Haze,
                Candela.Dmx.AttributeKind.Fog => EmitterCapabilities.Fog,
                Candela.Dmx.AttributeKind.Fan or Candela.Dmx.AttributeKind.FanDirection when profile.IsAtmospheric => EmitterCapabilities.Fan,
                Candela.Dmx.AttributeKind.Flame or Candela.Dmx.AttributeKind.FlameHeight => EmitterCapabilities.Flame,
                _ => EmitterCapabilities.None,
            };
        }
        // A device whose channels do not say it (or that has none) is what its category says.
        if (c == EmitterCapabilities.None)
        {
            c = profile.Category switch
            {
                FixtureCategory.Haze => EmitterCapabilities.Haze,
                FixtureCategory.Fog => EmitterCapabilities.Fog,
                FixtureCategory.Fan => EmitterCapabilities.Fan,
                FixtureCategory.GeneratorFire => EmitterCapabilities.Flame,
                _ => EmitterCapabilities.None,
            };
        }
        _capabilities[profile] = c;
        return c;
    }

    /// <summary>The smoke overlay's markers of one device: one per kind of output it has (running or not).</summary>
    private void AddMarkers(FixturePose pose, Candela.Dmx.FixtureProfile profile, in Candela.Dmx.DeviceOutputs outputs, Vector3 position)
    {
        var c = CapabilitiesOf(profile);
        if (outputs.Haze > 0f) c |= EmitterCapabilities.Haze;
        if (outputs.Fog > 0f) c |= EmitterCapabilities.Fog;
        if (outputs.FanSpeed > 0f) c |= EmitterCapabilities.Fan;
        if (outputs.Flame > 0f) c |= EmitterCapabilities.Flame;
        if (c == EmitterCapabilities.None) return;
        var id = pose.Entity.Id;
        if ((c & (EmitterCapabilities.Haze | EmitterCapabilities.Fog | EmitterCapabilities.Fan)) != 0)
        {
            var nozzle = NozzleDirection(pose);
            var exit = position + nozzle * 0.25f;
            if ((c & EmitterCapabilities.Haze) != 0)
                _emitterMarkers.Add(new Rendering.SmokeEmitterMarker(exit, nozzle, Level(outputs.Haze), Rendering.SmokeEmitterMarkerKind.Hazer, id));
            if ((c & EmitterCapabilities.Fog) != 0)
            {
                var kind = IsLowFog(profile) ? Rendering.SmokeEmitterMarkerKind.LowFog : Rendering.SmokeEmitterMarkerKind.FogMachine;
                _emitterMarkers.Add(new Rendering.SmokeEmitterMarker(exit, nozzle, Level(outputs.Fog), kind, id));
            }
            if ((c & EmitterCapabilities.Fan) != 0)
            {
                var dir = outputs.FanDirection < 0f ? -nozzle : nozzle;
                _emitterMarkers.Add(new Rendering.SmokeEmitterMarker(exit, dir, Level(outputs.FanSpeed), Rendering.SmokeEmitterMarkerKind.Fan, id));
            }
        }
        if ((c & EmitterCapabilities.Flame) != 0)
            _emitterMarkers.Add(new Rendering.SmokeEmitterMarker(position, FlameDirection(pose), Level(outputs.Flame), Rendering.SmokeEmitterMarkerKind.Flame, id));

        static float Level(float v) => float.IsFinite(v) ? Math.Clamp(v, 0f, 1f) : 0f;
    }

    private static Vector3 NozzleDirection(FixturePose pose)
    {
        var beams = pose.Profile.Beams;
        if (beams.Count > 0 && pose.TryGetBeamFrame(beams[0].PartIndex, out _, out var dir, out _)) return dir;
        var m = pose.Entity.WorldMatrix;
        var front = -new Vector3(m.M21, m.M22, m.M23);
        float len = front.Length();
        return len > 1e-6f && float.IsFinite(len) ? front / len : -Vector3.UnitY;
    }

    private static Vector3 FlameDirection(FixturePose pose)
    {
        var beams = pose.Profile.Beams;
        if (beams.Count > 0 && pose.TryGetBeamFrame(beams[0].PartIndex, out _, out var dir, out _) && dir.Z > 0.3f) return dir;
        return Vector3.UnitZ;
    }

    private static bool IsLowFog(Candela.Dmx.FixtureProfile profile)
    {
        string name = $"{profile.FixtureType.Name} {profile.FixtureType.LongName} {profile.Specs?.SubType}";
        return name.Contains("low fog", StringComparison.OrdinalIgnoreCase) || name.Contains("lowfog", StringComparison.OrdinalIgnoreCase)
            || name.Contains("ground fog", StringComparison.OrdinalIgnoreCase) || name.Contains("low-fog", StringComparison.OrdinalIgnoreCase);
    }

    private static AtmosphereVolumeComponent Sanitize(AtmosphereVolumeComponent c) => new()
    {
        Size = new Vector3(Positive(c.Size.X, 20f), Positive(c.Size.Y, 20f), Positive(c.Size.Z, 10f)),
        CellSize = Positive(c.CellSize, 0.25f),
        Dissipation = float.IsFinite(c.Dissipation) ? Math.Clamp(c.Dissipation, 0f, 1f) : 0.01f,
        ScatteringAtFullDensity = float.IsFinite(c.ScatteringAtFullDensity) && c.ScatteringAtFullDensity >= 0f ? c.ScatteringAtFullDensity : 0.5f,
        Airflow = IsFinite(c.Airflow) ? c.Airflow : Vector3.Zero,
        Enabled = c.Enabled,
    };

    private static float Positive(float v, float fallback = 0f) => float.IsFinite(v) && v > 0f ? v : fallback;

    private static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
