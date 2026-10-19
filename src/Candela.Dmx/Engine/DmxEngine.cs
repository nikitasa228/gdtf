using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;
using Candela.Dmx.Input;
using Candela.Gdtf;

namespace Candela.Dmx;

/// <summary>
/// Decides whether a fixture follows the console: <paramref name="universesLive"/> is true when every universe it is
/// patched in has a live source. The engine's default returns <paramref name="universesLive"/>; the editor supplies one
/// from the cable topology (fixtures cabled to a console).
/// </summary>
public delegate bool ConsoleDrivenPredicate(Entity entity, FixtureComponent fixture, bool universesLive);

/// <summary>
/// Runs the DMX side of the world every frame: keeps one <see cref="FixtureState"/> per fixture of the scene, reads
/// every needed universe from the <see cref="DmxInputHub"/> once, evaluates console-driven fixtures from DMX and the
/// others from their manual attributes, applies motor dynamics, and reports which states changed. Scene edits (fixtures
/// added, removed, repatched, re-moded, manual values changed) are picked up incrementally from scene change events.
/// </summary>
/// <remarks>
/// Main-thread only, like the scene: <see cref="Update"/> reads the scene, the input hub and the console-driven delegate
/// on the calling thread and then evaluates the fixtures that need it on <see cref="MaxDegreeOfParallelism"/> threads
/// (each fixture's state is independent; profiles are immutable), returning when all are done. It does not allocate in
/// steady state: fixtures whose DMX did not change and that are not animating (no strobe, rotation or moving motor) are
/// skipped entirely.
/// </remarks>
public sealed class DmxEngine : IDisposable
{
    /// <summary>Fewest fixtures to evaluate in one frame before the work is spread over several threads.</summary>
    public const int ParallelThreshold = 96;

    private const int ParallelChunk = 16;

    private readonly Scene _scene;
    private readonly DmxInputHub _hub;
    private readonly Dictionary<Guid, Runtime> _byEntity = new();
    private readonly List<Runtime> _runtimes = new();
    private readonly HashSet<Guid> _dirty = new();
    private readonly List<Guid> _removed = new();
    private readonly Dictionary<int, UniverseBuffer> _universes = new();
    private readonly List<Guid> _changed = new();
    private readonly Action<int> _evaluateOne;
    private Runtime[] _work = Array.Empty<Runtime>();
    private int _workCount;
    private double _frameTime;
    private double _previousFrameTime = double.NaN;
    private WorkerPool? _pool;
    private int _maxParallelism = Math.Max(1, Environment.ProcessorCount);
    private bool _universeSetDirty = true;
    private Dictionary<(string Spec, string Mode), int?>? _spacing;
    private bool _disposed;

    /// <summary>Creates the engine for <paramref name="scene"/> and loads every fixture already in it.</summary>
    public DmxEngine(Scene scene, FixtureProfileCache profiles, DmxInputHub hub)
    {
        _scene = scene ?? throw new ArgumentNullException(nameof(scene));
        Profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _hub = hub ?? throw new ArgumentNullException(nameof(hub));
        _evaluateOne = EvaluateOne;
        foreach (var (entity, _) in scene.Query<FixtureComponent>()) _dirty.Add(entity.Id);
        scene.Changed += OnSceneChanged;
    }

    /// <summary>
    /// Most threads used to evaluate fixtures in one <see cref="Update"/> (default: the processor count; 1 evaluates on
    /// the calling thread only). Results do not depend on it.
    /// </summary>
    public int MaxDegreeOfParallelism
    {
        get => _maxParallelism;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            if (value == _maxParallelism) return;
            _maxParallelism = value;
            _pool?.Dispose();
            _pool = null;
        }
    }

    /// <summary>Profiles of the fixture types in the scene.</summary>
    public FixtureProfileCache Profiles { get; }

    /// <summary>Decides console-driven status (default: driven when all its universes are live).</summary>
    public ConsoleDrivenPredicate? ConsoleDriven { get; set; }

    /// <summary>Moves motorised attributes at their physical speed (default on); off makes them jump to the DMX value.</summary>
    public bool ApplyDynamics { get; set; } = true;

    /// <summary>Number of fixtures with a state.</summary>
    public int FixtureCount => _runtimes.Count;

    /// <summary>Time passed to the last <see cref="Update"/>, s.</summary>
    public double Time { get; private set; } = double.NaN;

    /// <summary>Number of <see cref="Update"/> calls.</summary>
    public long Frame { get; private set; }

    /// <summary>Entities whose state changed in the last <see cref="Update"/> (including new fixtures).</summary>
    public IReadOnlyList<Guid> Changed => _changed;

    /// <summary>Entities whose fixture was removed (or lost its state) since the previous <see cref="Update"/>.</summary>
    public IReadOnlyList<Guid> Removed => _removed;

    /// <summary>State of a fixture entity, or null (not a fixture, or its GDTF/mode could not be resolved).</summary>
    public FixtureState? GetState(Guid entityId) => _byEntity.TryGetValue(entityId, out var r) ? r.State : null;

    /// <summary>State of a fixture entity.</summary>
    public bool TryGetState(Guid entityId, out FixtureState state)
    {
        if (_byEntity.TryGetValue(entityId, out var r) && r.State is { } s)
        {
            state = s;
            return true;
        }
        state = null!;
        return false;
    }

    /// <summary>Profile resolution of a fixture entity (why it has no state), or null when the entity is not a fixture.</summary>
    public ProfileResolution? GetResolution(Guid entityId) => _byEntity.TryGetValue(entityId, out var r) ? r.Resolution : null;

    /// <summary>True when the fixture currently follows DMX from a console (its manual controls are locked).</summary>
    public bool IsConsoleDriven(Guid entityId) => _byEntity.TryGetValue(entityId, out var r) && r.Driven;

    /// <summary>Every fixture state with its entity id.</summary>
    public IEnumerable<KeyValuePair<Guid, FixtureState>> States
    {
        get
        {
            foreach (var r in _runtimes)
                if (r.State is { } s) yield return new KeyValuePair<Guid, FixtureState>(r.Entity.Id, s);
        }
    }

    /// <summary>Forces a fixture to be re-read from its component (e.g. after changing ManualAttributes without NotifyChanged).</summary>
    public void Invalidate(Guid entityId) => _dirty.Add(entityId);

    /// <summary>Forces every fixture to be re-read and re-evaluated.</summary>
    public void InvalidateAll()
    {
        foreach (var r in _runtimes) _dirty.Add(r.Entity.Id);
    }

    /// <summary>
    /// Re-resolves every fixture's GDTF profile through <see cref="Profiles"/> at the next <see cref="Update"/> — after
    /// the fixture library or the profile cache changed (a GDTF added, a substitute replaced by the real file) — without
    /// resetting the fixtures: a fixture whose profile changed gets a new state that continues the old one's motor
    /// positions, rotations and clock (<see cref="FixtureState.ContinueFrom"/>), so moving heads do not jump or travel
    /// again; a fixture that now resolves for the first time starts where its DMX puts it, one that no longer resolves
    /// is listed in <see cref="Removed"/>.
    /// </summary>
    public void ReloadProfiles()
    {
        foreach (var r in _runtimes)
        {
            r.Resolution = null;
            _dirty.Add(r.Entity.Id);
        }
    }

    /// <summary>
    /// Advances to <paramref name="timeSeconds"/>: applies pending scene changes, reads the needed universes once, and
    /// evaluates every fixture whose input changed or that is animating. <see cref="Changed"/> lists the results.
    /// </summary>
    public void Update(double timeSeconds)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _changed.Clear();
        if (_dirty.Count > 0) ApplySceneChanges();
        else _removed.Clear();
        if (_universeSetDirty) RebuildUniverses();

        foreach (var (number, u) in _universes)
        {
            u.Live = _hub.TryRead(number, u.Data, out long version);
            u.Changed = version != u.Version || u.Live != u.WasLive;
            u.Version = version;
            u.WasLive = u.Live;
        }

        // decide on this thread (scene, delegate, universes); evaluate afterwards, possibly in parallel
        if (_work.Length < _runtimes.Count) _work = new Runtime[Math.Max(_runtimes.Count, _work.Length * 2)];
        _workCount = 0;
        foreach (var r in _runtimes)
        {
            if (r.State is not { } state || r.Buffer is not { } buffer) continue;
            bool live = r.Addresses.Length > 0;
            foreach (var a in r.Addresses)
                if (a.Universe is not { } u || !u.Live) live = false;
            bool driven = ConsoleDriven?.Invoke(r.Entity, r.Fixture, live) ?? live;
            bool inputChanged = r.NeedsEvaluation || driven != r.Driven;
            if (driven)
            {
                foreach (var a in r.Addresses)
                    if (a.Universe is { } u && (u.Changed || inputChanged))
                        inputChanged |= buffer.CopyFromUniverse(a.BreakIndex, u.Data, a.Address);
            }
            else if (r.Driven || r.NeedsEvaluation)
            {
                buffer.ResetToDefaults();
                inputChanged = true;
            }
            r.Driven = driven;
            r.NeedsEvaluation = false;
            if (!inputChanged && !state.IsAnimating) continue;
            _work[_workCount++] = r;
            _changed.Add(r.Entity.Id);
        }

        _previousFrameTime = Time;
        _frameTime = timeSeconds;
        if (_workCount >= ParallelThreshold && _maxParallelism > 1)
        {
            _pool ??= new WorkerPool(_maxParallelism - 1, "Candela DMX");
            _pool.For(_workCount, ParallelChunk, _evaluateOne);
        }
        else
        {
            for (int i = 0; i < _workCount; i++) EvaluateOne(i);
        }
        Array.Clear(_work, 0, _workCount);
        Time = timeSeconds;
        Frame++;
    }

    private void EvaluateOne(int index)
    {
        var r = _work[index];
        // a fixture skipped while it rested starts moving from rest at the previous frame, not at its last evaluation
        if (_previousFrameTime <= _frameTime) r.State!.AdvanceIdleClock(_previousFrameTime);
        FixtureEvaluator.Evaluate(r.State!, r.Buffer!, _frameTime, r.Driven ? null : r.Manual, ApplyDynamics);
    }

    /// <summary>Builds the patch of the scene (for the patch view and issue checks).</summary>
    public Patch BuildPatch() => Patch.Build(_scene, Profiles);

    /// <summary>Stops listening to scene changes.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _scene.Changed -= OnSceneChanged;
        _pool?.Dispose();
        _pool = null;
    }

    private void OnSceneChanged(SceneChange change)
    {
        switch (change.Kind)
        {
            case SceneChangeKind.Added or SceneChangeKind.Removed:
                if (change.Entity is { } e)
                    foreach (var d in e.SelfAndDescendants())
                        if (d.Has<FixtureComponent>() || _byEntity.ContainsKey(d.Id)) _dirty.Add(d.Id);
                break;
            case SceneChangeKind.ComponentAdded or SceneChangeKind.ComponentRemoved or SceneChangeKind.ComponentChanged:
                if (change.Component is FixtureComponent && change.Entity is { } owner) _dirty.Add(owner.Id);
                break;
            case SceneChangeKind.Renamed:
                break;
        }
    }

    private void ApplySceneChanges()
    {
        _removed.Clear();
        _spacing = null;
        foreach (var id in _dirty)
        {
            var entity = _scene.Find(id);
            var fixture = entity?.Get<FixtureComponent>();
            _byEntity.TryGetValue(id, out var existing);
            if (entity is null || fixture is null)
            {
                if (existing is not null)
                {
                    _byEntity.Remove(id);
                    RemoveRuntime(existing);
                    _removed.Add(id);
                    _universeSetDirty = true;
                }
                continue;
            }
            if (existing is null)
            {
                existing = new Runtime(entity, fixture) { Slot = _runtimes.Count };
                _byEntity[id] = existing;
                _runtimes.Add(existing);
            }
            Refresh(existing, entity, fixture);
        }
        _dirty.Clear();

        // a substitute's mode follows the patch spacing of its type, which other fixtures' edits can change
        foreach (var r in _runtimes)
        {
            if (r.Resolution?.Substitution is not { } sub) continue;
            if (sub.PatchSpacing == SpacingOf(r.Fixture)) continue;
            r.Resolution = null;
            Refresh(r, r.Entity, r.Fixture);
        }
    }

    /// <summary>Patch spacing of a fixture's type (spec + mode) over the whole scene, computed once per batch of scene changes.</summary>
    private int? SpacingOf(FixtureComponent fixture)
    {
        _spacing ??= PatchSpacing.ByType(_scene.Query<FixtureComponent>().Select(x => x.Component));
        return _spacing.GetValueOrDefault(PatchSpacing.Key(fixture.GdtfSpec, fixture.GdtfMode));
    }

    /// <summary>Removes a runtime in O(1) by moving the last one into its slot (evaluation order is irrelevant).</summary>
    private void RemoveRuntime(Runtime r)
    {
        int last = _runtimes.Count - 1;
        var moved = _runtimes[last];
        _runtimes[r.Slot] = moved;
        moved.Slot = r.Slot;
        _runtimes.RemoveAt(last);
    }

    /// <summary>Re-reads a fixture component: profile (when GDTF or mode changed), addresses, manual values and gel colour.</summary>
    private void Refresh(Runtime r, Entity entity, FixtureComponent fixture)
    {
        r.Entity = entity;
        r.Fixture = fixture;
        if (r.Resolution is null || r.Spec != fixture.GdtfSpec || r.Mode != fixture.GdtfMode)
        {
            r.Spec = fixture.GdtfSpec;
            r.Mode = fixture.GdtfMode;
            var resolution = Profiles.Resolve(fixture.GdtfSpec, fixture.GdtfMode);
            // a stand-in for a missing model takes the mode that fits its type's patch spacing (as Patch.Build does)
            if (resolution.MatchKind == FixtureMatchKind.Substitute)
                resolution = Profiles.Resolve(fixture.GdtfSpec, fixture.GdtfMode, SpacingOf(fixture));
            r.Resolution = resolution;
            if (r.Resolution.Profile is { } profile)
            {
                if (r.State is null || !ReferenceEquals(r.State.Profile, profile))
                {
                    var fresh = new FixtureState(profile, SeedOf(entity.Id));
                    if (r.State is { } previous) fresh.ContinueFrom(previous);
                    r.State = fresh;
                    r.Buffer = new FixtureDmxBuffer(profile);
                }
            }
            else
            {
                if (r.State is not null) _removed.Add(entity.Id);
                r.State = null;
                r.Buffer = null;
            }
        }
        if (r.State is { } state)
        {
            var profile = state.Profile;
            var addresses = new List<AddressSlot>();
            for (int b = 0; b < profile.Breaks.Count; b++)
            {
                if (Patch.AddressFor(fixture, profile.Breaks[b].Break, profile.Breaks.Count) is not { } a) continue;
                if (a.Universe < 1 || a.Address is < 1 or > 512) continue;
                addresses.Add(new AddressSlot(b, a.Universe, a.Address));
            }
            r.Addresses = addresses.ToArray();
            r.Manual = fixture.ManualAttributes.Count > 0 ? AttributeOverrides.Compile(profile, fixture.ManualAttributes) : null;
            state.SetGel(fixture.Color); // the MVR gel colour of a fixture without its own colour system
        }
        else
        {
            r.Addresses = Array.Empty<AddressSlot>();
            r.Manual = null;
        }
        r.NeedsEvaluation = true;
        _universeSetDirty = true;
    }

    private void RebuildUniverses()
    {
        var needed = new HashSet<int>();
        foreach (var r in _runtimes)
            foreach (var a in r.Addresses)
                needed.Add(a.UniverseNumber);
        foreach (int u in _universes.Keys.Where(k => !needed.Contains(k)).ToList()) _universes.Remove(u);
        foreach (int u in needed)
            if (!_universes.ContainsKey(u)) _universes[u] = new UniverseBuffer();
        foreach (var r in _runtimes)
            for (int i = 0; i < r.Addresses.Length; i++)
                r.Addresses[i].Universe = _universes[r.Addresses[i].UniverseNumber];
        _universeSetDirty = false;
    }

    private static uint SeedOf(Guid id)
    {
        Span<byte> bytes = stackalloc byte[16];
        id.TryWriteBytes(bytes);
        uint h = 2166136261;
        foreach (byte b in bytes) h = (h ^ b) * 16777619;
        return h;
    }

    private sealed class Runtime
    {
        public Runtime(Entity entity, FixtureComponent fixture)
        {
            Entity = entity;
            Fixture = fixture;
        }

        public Entity Entity;
        public FixtureComponent Fixture;
        public int Slot;
        public string? Spec;
        public string? Mode;
        public ProfileResolution? Resolution;
        public FixtureState? State;
        public FixtureDmxBuffer? Buffer;
        public AttributeOverrides? Manual;
        public AddressSlot[] Addresses = Array.Empty<AddressSlot>();
        public bool Driven;
        public bool NeedsEvaluation = true;
    }

    private struct AddressSlot
    {
        public AddressSlot(int breakIndex, int universe, int address)
        {
            BreakIndex = breakIndex;
            UniverseNumber = universe;
            Address = address;
            Universe = null;
        }

        public int BreakIndex;
        public int UniverseNumber;
        public int Address;
        public UniverseBuffer? Universe;
    }

    private sealed class UniverseBuffer
    {
        public readonly byte[] Data = new byte[DmxInputHub.SlotCount];
        public bool Live;
        public bool WasLive;
        public bool Changed;
        public long Version = -1;
    }
}
