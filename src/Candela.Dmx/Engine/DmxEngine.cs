using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;
using Candela.Dmx.Input;

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
/// Main-thread only, like the scene. <see cref="Update"/> does not allocate in steady state: fixtures whose DMX did not
/// change and that are not animating (no strobe, rotation or moving motor) are skipped entirely.
/// </remarks>
public sealed class DmxEngine : IDisposable
{
    private readonly Scene _scene;
    private readonly DmxInputHub _hub;
    private readonly Dictionary<Guid, Runtime> _byEntity = new();
    private readonly List<Runtime> _runtimes = new();
    private readonly HashSet<Guid> _dirty = new();
    private readonly List<Guid> _removed = new();
    private readonly Dictionary<int, UniverseBuffer> _universes = new();
    private readonly List<Guid> _changed = new();
    private bool _universeSetDirty = true;
    private bool _disposed;

    /// <summary>Creates the engine for <paramref name="scene"/> and loads every fixture already in it.</summary>
    public DmxEngine(Scene scene, FixtureProfileCache profiles, DmxInputHub hub)
    {
        _scene = scene ?? throw new ArgumentNullException(nameof(scene));
        Profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _hub = hub ?? throw new ArgumentNullException(nameof(hub));
        foreach (var (entity, _) in scene.Query<FixtureComponent>()) _dirty.Add(entity.Id);
        scene.Changed += OnSceneChanged;
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
            FixtureEvaluator.Evaluate(state, buffer, timeSeconds, driven ? null : r.Manual, ApplyDynamics);
            _changed.Add(r.Entity.Id);
        }
        Time = timeSeconds;
        Frame++;
    }

    /// <summary>Builds the patch of the scene (for the patch view and issue checks).</summary>
    public Patch BuildPatch() => Patch.Build(_scene, Profiles);

    /// <summary>Stops listening to scene changes.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _scene.Changed -= OnSceneChanged;
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
                    _runtimes.Remove(existing);
                    _removed.Add(id);
                    _universeSetDirty = true;
                }
                continue;
            }
            if (existing is null)
            {
                existing = new Runtime(entity, fixture);
                _byEntity[id] = existing;
                _runtimes.Add(existing);
            }
            Refresh(existing, entity, fixture);
        }
        _dirty.Clear();
    }

    /// <summary>Re-reads a fixture component: profile (when GDTF or mode changed), addresses and manual values.</summary>
    private void Refresh(Runtime r, Entity entity, FixtureComponent fixture)
    {
        r.Entity = entity;
        r.Fixture = fixture;
        if (r.Resolution is null || r.Spec != fixture.GdtfSpec || r.Mode != fixture.GdtfMode)
        {
            r.Spec = fixture.GdtfSpec;
            r.Mode = fixture.GdtfMode;
            r.Resolution = Profiles.Resolve(fixture.GdtfSpec, fixture.GdtfMode);
            if (r.Resolution.Profile is { } profile)
            {
                if (r.State is null || !ReferenceEquals(r.State.Profile, profile))
                {
                    r.State = new FixtureState(profile, SeedOf(entity.Id));
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
