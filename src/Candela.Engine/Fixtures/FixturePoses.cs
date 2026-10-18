using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;
using Candela.Dmx;

namespace Candela.Engine;

/// <summary>
/// The pose of one fixture: world matrices of every part of its geometry tree (base, yoke, head, beams, pixels …) for
/// the current DMX state and entity transform. Owned by <see cref="FixturePoses"/>; overwritten in place.
/// </summary>
public sealed class FixturePose
{
    private Matrix4x4[] _animation;
    private Matrix4x4[] _world;

    internal FixturePose(Entity entity, FixtureComponent fixture, FixtureState state)
    {
        Entity = entity;
        Fixture = fixture;
        State = state;
        int n = state.Profile.Geometry.Parts.Count;
        _animation = new Matrix4x4[n];
        _world = new Matrix4x4[n];
    }

    /// <summary>The fixture entity.</summary>
    public Entity Entity { get; private set; }

    /// <summary>Its fixture component.</summary>
    public FixtureComponent Fixture { get; private set; }

    /// <summary>The DMX-evaluated state (owned by the <see cref="DmxEngine"/>).</summary>
    public FixtureState State { get; private set; }

    /// <summary>The compiled profile (GDTF type + mode).</summary>
    public FixtureProfile Profile => State.Profile;

    /// <summary>World matrix of each geometry part (index = <c>Profile.Geometry.Parts</c> index), metres, row-vector convention.</summary>
    public ReadOnlySpan<Matrix4x4> PartWorld => _world;

    /// <summary>Incremented whenever <see cref="PartWorld"/> changes.</summary>
    public long Version { get; private set; }

    /// <summary>World position of the fixture's root part, m.</summary>
    public Vector3 Position => _world.Length > 0 ? _world[0].Translation : Entity.WorldPosition;

    /// <summary>
    /// World-space beam axis of a beam part: GDTF beams leave their geometry along local −Z. Returns false for an index
    /// outside the geometry or a degenerate matrix.
    /// </summary>
    public bool TryGetBeamFrame(int partIndex, out Vector3 position, out Vector3 direction, out Vector3 up)
    {
        if ((uint)partIndex >= (uint)_world.Length)
        {
            position = direction = up = default;
            return false;
        }
        ref readonly var m = ref _world[partIndex];
        position = m.Translation;
        direction = -new Vector3(m.M31, m.M32, m.M33);
        up = new Vector3(m.M21, m.M22, m.M23);
        float len = direction.Length();
        if (!(len > 1e-9f) || !float.IsFinite(len))
        {
            direction = -Vector3.UnitZ;
            up = Vector3.UnitY;
            return float.IsFinite(position.X) && float.IsFinite(position.Y) && float.IsFinite(position.Z);
        }
        direction /= len;
        up -= Vector3.Dot(up, direction) * direction;
        float upLen = up.Length();
        up = upLen > 1e-9f && float.IsFinite(upLen) ? up / upLen : AnyPerpendicular(direction);
        return float.IsFinite(position.X) && float.IsFinite(position.Y) && float.IsFinite(position.Z);
    }

    internal void Rebind(Entity entity, FixtureComponent fixture, FixtureState state)
    {
        Entity = entity;
        Fixture = fixture;
        if (ReferenceEquals(State, state)) return;
        State = state;
        int n = state.Profile.Geometry.Parts.Count;
        if (_world.Length != n)
        {
            _animation = new Matrix4x4[n];
            _world = new Matrix4x4[n];
        }
    }

    /// <summary>Recomputes the part matrices from the state and the entity transform (allocation-free).</summary>
    internal void Recompute()
    {
        var geometry = State.Profile.Geometry;
        State.WriteAnimation(_animation);
        geometry.ComputeWorldTransforms(_world, Entity.WorldMatrix, _animation);
        Version++;
    }

    internal static Vector3 AnyPerpendicular(Vector3 axis)
    {
        var helper = MathF.Abs(axis.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitX;
        var p = Vector3.Cross(axis, helper);
        float len = p.Length();
        return len > 1e-9f ? p / len : Vector3.UnitY;
    }
}

/// <summary>
/// Part world transforms of every fixture that has a DMX state, kept up to date once per frame from the
/// <see cref="DmxEngine"/>'s changed states and from entity moves. Rendering, the light collector, the light meter and
/// the simulations read it instead of each computing the geometry tree. Main thread only; steady-state frames do not
/// allocate.
/// </summary>
public sealed class FixturePoses
{
    private readonly Dictionary<Guid, FixturePose> _poses = new();
    private readonly HashSet<Guid> _moved = new();
    private readonly List<Guid> _changed = new();
    private readonly HashSet<Guid> _changedSet = new();
    private readonly List<Guid> _removed = new();
    private readonly List<Guid> _scratch = new();

    /// <summary>Number of fixtures with a pose.</summary>
    public int Count => _poses.Count;

    /// <summary>Fixtures whose pose changed in the last update (new, re-evaluated or moved).</summary>
    public IReadOnlyList<Guid> Changed => _changed;

    /// <summary>Fixtures that lost their pose in the last update (removed, or their GDTF can no longer be resolved).</summary>
    public IReadOnlyList<Guid> Removed => _removed;

    /// <summary>Every pose (struct enumerator; no allocation).</summary>
    public Dictionary<Guid, FixturePose>.ValueCollection All => _poses.Values;

    /// <summary>The pose of a fixture entity.</summary>
    public bool TryGet(Guid entityId, [NotNullWhen(true)] out FixturePose? pose) => _poses.TryGetValue(entityId, out pose);

    /// <summary>Marks the fixtures in <paramref name="entity"/>'s subtree as moved (their world transform changed).</summary>
    internal void MarkMoved(Entity entity)
    {
        foreach (var e in entity.SelfAndDescendants())
            if (_poses.ContainsKey(e.Id)) _moved.Add(e.Id);
    }

    /// <summary>Forgets every pose (scene replaced).</summary>
    internal void Clear()
    {
        _poses.Clear();
        _moved.Clear();
        _changed.Clear();
        _changedSet.Clear();
        _removed.Clear();
    }

    /// <summary>Applies the DMX engine's last update: new and changed states, removed fixtures, moved entities.</summary>
    internal void Update(Scene scene, DmxEngine dmx)
    {
        _changed.Clear();
        _changedSet.Clear();
        _removed.Clear();
        foreach (var id in dmx.Removed)
        {
            if (_poses.Remove(id)) _removed.Add(id);
            _moved.Remove(id);
        }

        foreach (var id in dmx.Changed) Refresh(scene, dmx, id);
        if (_moved.Count > 0)
        {
            _scratch.Clear();
            foreach (var id in _moved) _scratch.Add(id);
            _moved.Clear();
            foreach (var id in _scratch)
                if (!_changedSet.Contains(id)) Refresh(scene, dmx, id);
        }
    }

    private void Refresh(Scene scene, DmxEngine dmx, Guid id)
    {
        var entity = scene.Find(id);
        var fixture = entity?.Get<FixtureComponent>();
        if (entity is null || fixture is null || !dmx.TryGetState(id, out var state))
        {
            if (_poses.Remove(id)) _removed.Add(id);
            return;
        }
        if (_poses.TryGetValue(id, out var pose)) pose.Rebind(entity, fixture, state);
        else _poses[id] = pose = new FixturePose(entity, fixture, state);
        pose.Recompute();
        if (_changedSet.Add(id)) _changed.Add(id);
    }
}
