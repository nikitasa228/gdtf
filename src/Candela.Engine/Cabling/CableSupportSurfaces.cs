using System.Numerics;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;

namespace Candela.Engine;

/// <summary>
/// The surfaces cables lie on, for drawing them: the ground plane (<see cref="GroundHeight"/>, Z = 0 m by default, where
/// the editor's cable tool puts free nodes) and the upper faces of floors and ceilings (<see cref="WallComponent"/>s of
/// kind <see cref="WallKind.Floor"/> or <see cref="WallKind.Ceiling"/>, also when tilted up to 60°, e.g. ramps).
/// <see cref="HeightAt"/> answers the <c>Candela.Assets.CableSupportHeight</c> query of the cable tube builder, and
/// <see cref="RestingHeight"/> tells whether a cable node lies on a surface (so a span between two nodes resting on the
/// same surface runs flat instead of sagging through it).
/// <para>Built lazily from the scene after <see cref="Invalidate"/> (a floor was added, removed, edited or moved); the
/// queries do not allocate and cost one rectangle test per floor whose horizontal extent contains the point.
/// <see cref="TakeChanges"/> tells which horizontal area a rebuild changed, so only the cables over it are drawn again
/// (a floor dragged or resized does not redraw every cable on every frame). Main thread only, like the scene. Heights
/// are world Z in metres.</para>
/// </summary>
public sealed class CableSupportSurfaces
{
    /// <summary>
    /// A point this close above a surface (metres) still counts as on it, and a surface this little above the queried
    /// height still supports the point: absorbs rounding and the 2 cm the cable tool lifts nodes off the surface they
    /// are placed on.
    /// </summary>
    public const float Tolerance = 0.02f;

    /// <summary>A node at most this high above the surface under it (metres) rests on it.</summary>
    public const float RestDistance = 0.05f;

    /// <summary>Surfaces steeper than this (the up component of their normal below 0.5, i.e. over 60°) do not carry cables.</summary>
    private const float MinUp = 0.5f;

    private readonly List<Slab> _slabs = new();
    private readonly List<Slab> _previous = new();
    private readonly HashSet<Slab> _oldSet = new();
    private readonly HashSet<Slab> _newSet = new();
    private float? _groundHeight = 0f;
    private bool _valid;
    // what changed since the last TakeChanges: everything (the ground, the start), or the X/Y rectangle of changed floors
    private bool _changedEverywhere = true;
    private Vector2 _changedMin = new(float.PositiveInfinity), _changedMax = new(float.NegativeInfinity);

    /// <summary>
    /// Height of the ground plane in metres (world Z), or null for no ground (a scene floating in space, or one whose
    /// stage floor is modelled with floors). Default 0 m: MVR and the editor put the stage floor at Z = 0.
    /// </summary>
    public float? GroundHeight
    {
        get => _groundHeight;
        set
        {
            var v = value is { } h && float.IsFinite(h) ? h : (float?)null;
            if (v == _groundHeight) return;
            _groundHeight = v;
            _changedEverywhere = true;
            Version++;
        }
    }

    /// <summary>Number of floor and ceiling surfaces found by the last build.</summary>
    public int Count => _slabs.Count;

    /// <summary>True when the surfaces describe the scene they were last built from.</summary>
    public bool IsValid => _valid;

    /// <summary>Incremented whenever a build found different surfaces or <see cref="GroundHeight"/> changed.</summary>
    public long Version { get; private set; }

    /// <summary>Floors were added, removed, edited or moved: rebuild on the next <see cref="EnsureBuilt"/>.</summary>
    public void Invalidate() => _valid = false;

    /// <summary>True when <paramref name="entity"/> carries a surface cables can lie on (a floor or a ceiling).</summary>
    public static bool IsSurface(Entity entity) =>
        entity is not null && entity.Get<WallComponent>() is { Kind: WallKind.Floor or WallKind.Ceiling };

    /// <summary>
    /// Builds the surfaces from <paramref name="scene"/> when they were invalidated (linear in the scene's walls).
    /// Returns true when the surfaces differ from those of the previous build (<see cref="Version"/> was incremented):
    /// cables drawn on the old ones must be drawn again.
    /// </summary>
    public bool EnsureBuilt(Scene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (_valid) return false;
        _valid = true;
        _previous.Clear();
        _previous.AddRange(_slabs);
        _slabs.Clear();
        // hidden floors still carry the cables lying on them (hiding is a view setting, not a change of the stage)
        foreach (var (entity, wall) in scene.Query<WallComponent>())
            if (Slab.TryCreate(entity, wall, out var slab)) _slabs.Add(slab);
        bool changed = _slabs.Count != _previous.Count;
        for (int i = 0; !changed && i < _slabs.Count; i++) changed = !_slabs[i].Equals(_previous[i]);
        if (changed)
        {
            Version++;
            // the floors that are new, gone or different: only the cables over them can lie differently
            _oldSet.Clear();
            _newSet.Clear();
            foreach (var slab in _previous) _oldSet.Add(slab);
            foreach (var slab in _slabs) _newSet.Add(slab);
            foreach (var slab in _previous)
                if (!_newSet.Contains(slab)) Grow(slab);
            foreach (var slab in _slabs)
                if (!_oldSet.Contains(slab)) Grow(slab);
            _oldSet.Clear();
            _newSet.Clear();
        }
        _previous.Clear();
        return changed;
    }

    /// <summary>
    /// Takes what changed since the last call (builds by <see cref="EnsureBuilt"/> and <see cref="GroundHeight"/>):
    /// false when nothing did; otherwise <paramref name="everywhere"/> (the ground changed, or nothing was taken yet), or
    /// the horizontal rectangle (world X/Y, metres, from <paramref name="min"/> to <paramref name="max"/>) covering every
    /// floor that was added, removed, moved or resized. A cable is drawn along the vertical through its nodes' horizontal
    /// extent, so only cables whose nodes' extent meets the rectangle lie differently.
    /// </summary>
    public bool TakeChanges(out bool everywhere, out Vector2 min, out Vector2 max)
    {
        everywhere = _changedEverywhere;
        min = _changedMin;
        max = _changedMax;
        bool any = everywhere || (min.X <= max.X && min.Y <= max.Y);
        _changedEverywhere = false;
        _changedMin = new Vector2(float.PositiveInfinity);
        _changedMax = new Vector2(float.NegativeInfinity);
        return any;
    }

    private void Grow(in Slab slab)
    {
        _changedMin = Vector2.Min(_changedMin, new Vector2(slab.MinX, slab.MinY));
        _changedMax = Vector2.Max(_changedMax, new Vector2(slab.MaxX, slab.MaxY));
    }

    /// <summary>
    /// Height in metres of the highest surface under (<paramref name="x"/>, <paramref name="y"/>) that is not above
    /// <paramref name="maxHeight"/> + <see cref="Tolerance"/>, or <see cref="float.NegativeInfinity"/> when none is.
    /// Matches <c>Candela.Assets.CableSupportHeight</c>.
    /// </summary>
    public float HeightAt(float x, float y, float maxHeight)
    {
        if (!float.IsFinite(x) || !float.IsFinite(y) || float.IsNaN(maxHeight)) return float.NegativeInfinity;
        float limit = maxHeight + Tolerance;
        float best = float.NegativeInfinity;
        if (_groundHeight is { } ground && ground <= limit) best = ground;
        var slabs = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_slabs);
        for (int i = 0; i < slabs.Length; i++)
        {
            ref readonly var s = ref slabs[i];
            if (x < s.MinX || x > s.MaxX || y < s.MinY || y > s.MaxY) continue;
            if (s.TryHeight(x, y, out float z) && z <= limit && z > best) best = z;
        }
        return best;
    }

    /// <summary>
    /// Height of the surface <paramref name="point"/> rests on (it is at most <see cref="RestDistance"/> above it and not
    /// more than <see cref="Tolerance"/> below it), or null when it hangs free.
    /// </summary>
    public float? RestingHeight(Vector3 point)
    {
        float h = HeightAt(point.X, point.Y, point.Z);
        return float.IsFinite(h) && point.Z - h <= RestDistance ? h : null;
    }

    /// <summary>The upper face of a floor or ceiling, as a rectangle on a plane in world space.</summary>
    private readonly record struct Slab(Vector3 Origin, Vector3 Normal, Matrix4x4 Inverse, float HalfWidth, float HalfLength, float Face,
        float MinX, float MaxX, float MinY, float MaxY)
    {
        public static bool TryCreate(Entity entity, WallComponent wall, out Slab slab)
        {
            slab = default;
            if (wall.Kind is not (WallKind.Floor or WallKind.Ceiling)) return false;
            var world = entity.WorldMatrix;
            if (!Matrix4x4.Invert(world, out var inverse) || !IsFinite(inverse)) return false;
            float halfW = Size(wall.Width) / 2, halfL = Size(wall.Height) / 2, t = Thickness(wall.Thickness);
            // the box spans local z = −t … 0 (floor) or 0 … t (ceiling); its upper face is the one facing world up
            float bottom = wall.Kind == WallKind.Floor ? -t : 0f, top = wall.Kind == WallKind.Floor ? 0f : t;
            var up = Vector3.TransformNormal(Vector3.UnitZ, world);
            var ex = Vector3.TransformNormal(Vector3.UnitX, world);
            var ey = Vector3.TransformNormal(Vector3.UnitY, world);
            var normal = Vector3.Cross(ex, ey);
            if (!(normal.LengthSquared() > 1e-12f)) return false;
            normal = Vector3.Normalize(normal);
            if (Vector3.Dot(normal, up) < 0) normal = -normal; // mirrored transforms
            float face = top;
            if (normal.Z < 0)
            {
                normal = -normal;
                face = bottom; // upside down: the other face is on top
            }
            if (normal.Z < MinUp) return false;
            var origin = Vector3.Transform(new Vector3(0, 0, face), world);
            float minX = float.PositiveInfinity, maxX = float.NegativeInfinity, minY = float.PositiveInfinity, maxY = float.NegativeInfinity;
            for (int c = 0; c < 4; c++)
            {
                var corner = Vector3.Transform(new Vector3((c & 1) == 0 ? -halfW : halfW, (c & 2) == 0 ? -halfL : halfL, face), world);
                minX = MathF.Min(minX, corner.X);
                maxX = MathF.Max(maxX, corner.X);
                minY = MathF.Min(minY, corner.Y);
                maxY = MathF.Max(maxY, corner.Y);
            }
            if (!float.IsFinite(minX + maxX + minY + maxY) || !float.IsFinite(origin.X + origin.Y + origin.Z)) return false;
            const float Edge = 1e-3f;
            slab = new Slab(origin, normal, inverse, halfW + Edge, halfL + Edge, face, minX - Edge, maxX + Edge, minY - Edge, maxY + Edge);
            return true;
        }

        /// <summary>Height of the face's plane above (x, y), when that point of the plane is inside the rectangle.</summary>
        public bool TryHeight(float x, float y, out float z)
        {
            z = Origin.Z - (Normal.X * (x - Origin.X) + Normal.Y * (y - Origin.Y)) / Normal.Z;
            var local = Vector3.Transform(new Vector3(x, y, z), Inverse);
            return MathF.Abs(local.X) <= HalfWidth && MathF.Abs(local.Y) <= HalfLength;
        }

        private static float Size(float v) => float.IsFinite(v) ? System.Math.Clamp(v, 0.001f, 10_000f) : 0.001f;

        private static float Thickness(float v) => float.IsFinite(v) && v > 0 ? System.Math.Min(v, 10_000f) : 0f;

        private static bool IsFinite(in Matrix4x4 m) =>
            float.IsFinite(m.M11 + m.M12 + m.M13 + m.M14 + m.M21 + m.M22 + m.M23 + m.M24 + m.M31 + m.M32 + m.M33 + m.M34 + m.M41 + m.M42 + m.M43 + m.M44);
    }
}
