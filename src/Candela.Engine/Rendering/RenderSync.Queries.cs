using System.Numerics;
using Candela.Core.Math;
using Candela.Core.SceneGraph;
using Candela.Rendering;

namespace Candela.Engine;

public sealed partial class RenderSync
{
    /// <summary>Instance boxes larger than this along their diagonal are left out of <see cref="VisibleWorldBounds"/>, m.</summary>
    public const float MaxContentExtent = 10_000f;

    private readonly HashSet<Guid> _flagsDirty = new();
    private readonly HashSet<Guid> _scratchSelection = new();
    private SpotLight[] _lightBuffer = new SpotLight[64];
    private readonly SpotLight[] _workLights = new SpotLight[WorkLightRig.LightCount];
    private bool _allFlagsDirty;
    private bool _layersDirty = true;
    private bool _outlineDescendants;
    private long _structureVersion;
    private long _rigSceneVersion = -1;
    private long _rigStructureVersion = -1;

    /// <summary>
    /// Draws every descendant of a selected entity selected as well (the outline of a selected group shows its content).
    /// Default false: only the selected entities themselves.
    /// </summary>
    public bool OutlineSelectedDescendants
    {
        get => _outlineDescendants;
        set
        {
            if (_outlineDescendants == value) return;
            _outlineDescendants = value;
            _allFlagsDirty = true;
        }
    }

    /// <summary>
    /// The work light of this binding (see <see cref="WorkLightRig"/>): a dim neutral key and ambient light added to the
    /// light list sent to the renderer — in <see cref="WorkLightMode.Auto"/> only while no stage light emits. Default
    /// <see cref="WorkLightMode.Off"/>, so renders never get it unless asked; the editor's preview passes
    /// <see cref="ViewSettings.WorkLight"/> (default <see cref="WorkLightMode.Auto"/>). It never enters
    /// <see cref="StageEngine.Lights"/> (light meter, light plot), and it is left out while the project's view shows the
    /// lux overlay (<see cref="ViewSettings.View"/>), which measures stage light only.
    /// </summary>
    public WorkLightMode WorkLight { get; set; } = WorkLightMode.Off;

    /// <summary>True when the last <see cref="Update"/> sent the work light.</summary>
    public bool IsWorkLightOn { get; private set; }

    /// <summary>The work-light rig of the last update that sent it (empty while it is off).</summary>
    public ReadOnlySpan<SpotLight> WorkLights => IsWorkLightOn ? _workLights : ReadOnlySpan<SpotLight>.Empty;

    /// <summary>
    /// World-space bounds (m) of everything drawn for an entity: the union of its instances' mesh bounds placed with their
    /// current world matrices — a fixture in its current pan/tilt pose, a decoded model, a wall, a cable. False when
    /// nothing of it is drawn (yet), or only hidden instances and <paramref name="includeHidden"/> is false.
    /// </summary>
    public bool TryGetWorldBounds(Guid entityId, out Aabb bounds, bool includeHidden = true)
    {
        bounds = Aabb.Empty;
        return _entities.TryGetValue(entityId, out var v) && Include(v, ref bounds, includeHidden);
    }

    /// <summary>World-space bounds (m) of the entity drawn with <paramref name="pickId"/>; see <see cref="TryGetWorldBounds(Guid, out Aabb, bool)"/>.</summary>
    public bool TryGetWorldBounds(uint pickId, out Aabb bounds, bool includeHidden = true)
    {
        bounds = Aabb.Empty;
        return _byPickId.TryGetValue(pickId, out var id) && _entities.TryGetValue(id, out var v) && Include(v, ref bounds, includeHidden);
    }

    /// <summary>
    /// Adds the world bounds of the entity drawn with <paramref name="pickId"/> to <paramref name="bounds"/> (box selection,
    /// framing a selection). Returns true when at least one instance contributed.
    /// </summary>
    public bool IncludeWorldBounds(uint pickId, ref Aabb bounds, bool includeHidden = true) =>
        _byPickId.TryGetValue(pickId, out var id) && _entities.TryGetValue(id, out var v) && Include(v, ref bounds, includeHidden);

    /// <summary>
    /// Bounds of every visible instance, m (empty when nothing is drawn). Boxes with a diagonal above
    /// <see cref="MaxContentExtent"/> (a stray far-away model) and non-finite placements are left out.
    /// </summary>
    public Aabb VisibleWorldBounds()
    {
        var bounds = Aabb.Empty;
        foreach (var v in _entities.Values)
        {
            if ((v.Flags & InstanceFlags.Hidden) != 0) continue;
            foreach (var r in v.Instances)
            {
                var box = WorldBoundsOf(r);
                if (box.IsEmpty || !IsFinite(box) || box.Size.Length() > MaxContentExtent) continue;
                bounds.Include(box);
            }
        }
        return bounds;
    }

    /// <summary>
    /// Appends the eight world corners (m) of every visible instance's box to <paramref name="corners"/> (framing a view on
    /// what is drawn); the same instances as <see cref="VisibleWorldBounds"/>.
    /// </summary>
    public void CollectVisibleCorners(List<Vector3> corners)
    {
        ArgumentNullException.ThrowIfNull(corners);
        foreach (var v in _entities.Values)
        {
            if ((v.Flags & InstanceFlags.Hidden) != 0) continue;
            foreach (var r in v.Instances)
            {
                var b = WorldBoundsOf(r);
                if (b.IsEmpty || !IsFinite(b) || b.Size.Length() > MaxContentExtent) continue;
                for (int i = 0; i < 8; i++)
                    corners.Add(new Vector3((i & 1) == 0 ? b.Min.X : b.Max.X, (i & 2) == 0 ? b.Min.Y : b.Max.Y, (i & 4) == 0 ? b.Min.Z : b.Max.Z));
            }
        }
    }

    private static bool Include(EntityVisual v, ref Aabb bounds, bool includeHidden)
    {
        if (!includeHidden && (v.Flags & InstanceFlags.Hidden) != 0) return false;
        bool any = false;
        foreach (var r in v.Instances)
        {
            var box = WorldBoundsOf(r);
            if (box.IsEmpty || !IsFinite(box)) continue;
            bounds.Include(box);
            any = true;
        }
        return any;
    }

    private static Aabb WorldBoundsOf(in InstanceRecord r) => r.Bounds.IsEmpty ? Aabb.Empty : r.Bounds.Transform(r.World);

    private static bool IsFinite(in Aabb b) => IsFinite(b.Min) && IsFinite(b.Max);

    // ------------------------------------------------------------------ flags

    /// <summary>The selection of <paramref name="id"/> changed: its flags (and its subtree's, when descendants are outlined) are re-derived.</summary>
    private void MarkSelectionChanged(Guid id)
    {
        if (_outlineDescendants && _scene.Find(id) is { } entity)
        {
            foreach (var d in entity.SelfAndDescendants()) _flagsDirty.Add(d.Id);
            return;
        }
        _flagsDirty.Add(id);
    }

    private bool HasSelectedAncestor(Entity entity)
    {
        for (var p = entity.Parent; p is not null; p = p.Parent)
            if (_selected.Contains(p.Id)) return true;
        return false;
    }

    // ------------------------------------------------------------------ lights

    /// <summary>
    /// Sends the frame's stage lights, plus the work light when <see cref="WorkLight"/> asks for it. The rig is placed
    /// around <see cref="VisibleWorldBounds"/> and recomputed only when the scene or the set of drawn entities changed.
    /// </summary>
    private void SendLights()
    {
        var stage = _engine.Lights;
        bool on = WorkLight != WorkLightMode.Off && _engine.View.View?.Overlay != OverlayMode.Lux && WorkLightRig.IsNeeded(WorkLight, stage);
        IsWorkLightOn = on;
        if (!on)
        {
            World.SetLights(stage);
            return;
        }
        if (_rigSceneVersion != _scene.Version || _rigStructureVersion != _structureVersion)
        {
            _rigSceneVersion = _scene.Version;
            _rigStructureVersion = _structureVersion;
            WorkLightRig.Write(VisibleWorldBounds(), _workLights);
        }
        int count = stage.Length + _workLights.Length;
        if (_lightBuffer.Length < count) _lightBuffer = new SpotLight[Math.Max(count, 2 * _lightBuffer.Length)];
        stage.CopyTo(_lightBuffer);
        _workLights.CopyTo(_lightBuffer.AsSpan(stage.Length));
        World.SetLights(_lightBuffer.AsSpan(0, count));
    }
}
