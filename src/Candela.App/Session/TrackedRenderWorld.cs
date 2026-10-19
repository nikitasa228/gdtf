using System.Numerics;
using Candela.Core.Geometry;
using Candela.Core.Math;
using Candela.Rendering;

namespace Candela.App.Session;

/// <summary>
/// The renderer's world as the engine's render binding sees it: every call is forwarded unchanged to the real
/// <see cref="IRenderWorld"/>, and the editor remembers, per instance, the local bounds of its mesh (metres), its world
/// matrix, pick id and flags. That gives the real world-space bounds of everything drawn for an entity — a fixture in
/// its current pan/tilt pose, a decoded model, a wall — for box selection, framing and drop targets, without asking the
/// renderer. It also lets the editor outline the children of a selected group: instances whose entity has a selected
/// ancestor get the <see cref="InstanceFlags.Selected"/> flag added to the flags the binding sends.
/// </summary>
/// <remarks>
/// Main thread only, like the world it wraps. Updating an instance (fixtures moving every frame) is a dictionary write
/// of a struct and does not allocate.
/// </remarks>
internal sealed class TrackedRenderWorld : IRenderWorld
{
    private readonly IRenderWorld _inner;
    private readonly Dictionary<MeshHandle, Aabb> _meshBounds = new();
    private readonly Dictionary<InstanceHandle, InstanceState> _instances = new();
    private readonly Dictionary<uint, List<InstanceHandle>> _byPick = new();
    private readonly HashSet<uint> _inheritedSelection = new();
    private readonly List<InstanceHandle> _scratch = new();

    /// <summary>Wraps <paramref name="inner"/>.</summary>
    public TrackedRenderWorld(IRenderWorld inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    /// <summary>The wrapped world.</summary>
    public IRenderWorld Inner => _inner;

    /// <summary>Number of instances currently placed.</summary>
    public int InstanceCount => _instances.Count;

    /// <summary>
    /// World bounds of the instances drawn with <paramref name="pickId"/> (all parts of one entity), added to
    /// <paramref name="bounds"/>; hidden instances count only with <paramref name="includeHidden"/>. Returns true when
    /// at least one instance contributed.
    /// </summary>
    public bool IncludeBounds(uint pickId, ref Aabb bounds, bool includeHidden = true)
    {
        if (pickId == 0 || !_byPick.TryGetValue(pickId, out var list))
            return false;
        bool any = false;
        foreach (var handle in list)
        {
            if (!_instances.TryGetValue(handle, out var state))
                continue;
            if (!includeHidden && (state.Flags & InstanceFlags.Hidden) != 0)
                continue;
            if (!_meshBounds.TryGetValue(state.Mesh, out var local) || local.IsEmpty)
                continue;
            var world = local.Transform(state.World);
            if (!IsFinite(world))
                continue;
            bounds.Include(world);
            any = true;
        }

        return any;
    }

    /// <summary>Bounds of every visible instance (empty when nothing is drawn).</summary>
    public Aabb VisibleBounds()
    {
        var bounds = Aabb.Empty;
        foreach (var (_, state) in _instances)
        {
            if ((state.Flags & InstanceFlags.Hidden) != 0 || !_meshBounds.TryGetValue(state.Mesh, out var local) || local.IsEmpty)
                continue;
            var world = local.Transform(state.World);
            if (IsFinite(world))
                bounds.Include(world);
        }

        return bounds;
    }

    /// <summary>Appends the eight world corners of every visible instance's box (framing the view on what is drawn).</summary>
    public void CollectVisibleCorners(List<Vector3> corners)
    {
        ArgumentNullException.ThrowIfNull(corners);
        foreach (var (_, state) in _instances)
        {
            if ((state.Flags & InstanceFlags.Hidden) != 0 || !_meshBounds.TryGetValue(state.Mesh, out var local) || local.IsEmpty)
                continue;
            var world = local.Transform(state.World);
            if (!IsFinite(world) || world.Size.Length() > 10_000f)
                continue;
            for (int i = 0; i < 8; i++)
                corners.Add(new Vector3((i & 1) == 0 ? world.Min.X : world.Max.X, (i & 2) == 0 ? world.Min.Y : world.Max.Y, (i & 4) == 0 ? world.Min.Z : world.Max.Z));
        }
    }

    /// <summary>
    /// Sets the pick ids whose instances are outlined because an ancestor of their entity is selected, and re-sends the
    /// flags of the instances whose highlight changed.
    /// </summary>
    public void SetInheritedSelection(IReadOnlySet<uint> pickIds)
    {
        ArgumentNullException.ThrowIfNull(pickIds);
        _scratch.Clear();
        foreach (var id in _inheritedSelection)
        {
            if (!pickIds.Contains(id) && _byPick.TryGetValue(id, out var list))
                _scratch.AddRange(list);
        }

        foreach (var id in pickIds)
            if (!_inheritedSelection.Contains(id) && _byPick.TryGetValue(id, out var list))
                _scratch.AddRange(list);
        _inheritedSelection.Clear();
        foreach (var id in pickIds)
            if (id != 0)
                _inheritedSelection.Add(id);
        foreach (var handle in _scratch)
        {
            if (_instances.TryGetValue(handle, out var state))
                _inner.UpdateInstance(handle, state.World, Effective(state));
        }
    }

    /// <summary>True when an instance drawn with <paramref name="pickId"/> exists.</summary>
    public bool HasInstances(uint pickId) => _byPick.TryGetValue(pickId, out var list) && list.Count > 0;

    /// <inheritdoc/>
    public MeshHandle CreateMesh(MeshData mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var handle = _inner.CreateMesh(mesh);
        if (handle.IsValid)
            _meshBounds[handle] = mesh.ComputeBounds();
        return handle;
    }

    /// <inheritdoc/>
    public void DestroyMesh(MeshHandle mesh)
    {
        _meshBounds.Remove(mesh);
        _inner.DestroyMesh(mesh);
    }

    /// <inheritdoc/>
    public TextureHandle CreateTexture(ImageData image, bool srgb) => _inner.CreateTexture(image, srgb);

    /// <inheritdoc/>
    public void DestroyTexture(TextureHandle texture) => _inner.DestroyTexture(texture);

    /// <inheritdoc/>
    public MaterialHandle CreateMaterial(MaterialData material, IImageSource? images) => _inner.CreateMaterial(material, images);

    /// <inheritdoc/>
    public void UpdateMaterial(MaterialHandle handle, MaterialData material, IImageSource? images) => _inner.UpdateMaterial(handle, material, images);

    /// <inheritdoc/>
    public void DestroyMaterial(MaterialHandle material) => _inner.DestroyMaterial(material);

    /// <inheritdoc/>
    public InstanceHandle CreateInstance(MeshHandle mesh, ReadOnlySpan<MaterialHandle> materials, Matrix4x4 world, InstanceFlags flags, uint pickId)
    {
        var state = new InstanceState(mesh, world, flags, pickId);
        var handle = _inner.CreateInstance(mesh, materials, world, Effective(state), pickId);
        if (!handle.IsValid)
            return handle;
        _instances[handle] = state;
        if (pickId != 0)
        {
            if (!_byPick.TryGetValue(pickId, out var list))
                _byPick[pickId] = list = new List<InstanceHandle>(4);
            list.Add(handle);
        }

        return handle;
    }

    /// <inheritdoc/>
    public void UpdateInstance(InstanceHandle instance, Matrix4x4 world, InstanceFlags flags)
    {
        if (_instances.TryGetValue(instance, out var state))
        {
            state = state with { World = world, Flags = flags };
            _instances[instance] = state;
            _inner.UpdateInstance(instance, world, Effective(state));
            return;
        }

        _inner.UpdateInstance(instance, world, flags);
    }

    /// <inheritdoc/>
    public void SetInstanceMaterials(InstanceHandle instance, ReadOnlySpan<MaterialHandle> materials) => _inner.SetInstanceMaterials(instance, materials);

    /// <inheritdoc/>
    public void DestroyInstance(InstanceHandle instance)
    {
        if (_instances.Remove(instance, out var state) && state.PickId != 0 && _byPick.TryGetValue(state.PickId, out var list))
        {
            list.Remove(instance);
            if (list.Count == 0)
                _byPick.Remove(state.PickId);
        }

        _inner.DestroyInstance(instance);
    }

    /// <inheritdoc/>
    public void SetLights(ReadOnlySpan<SpotLight> lights) => _inner.SetLights(lights);

    /// <inheritdoc/>
    public void SetLines(ReadOnlySpan<LineVertex> segments) => _inner.SetLines(segments);

    /// <inheritdoc/>
    public void SetAtmosphere(AtmosphereState atmosphere) => _inner.SetAtmosphere(atmosphere);

    /// <inheritdoc/>
    public void SetNoiseSources(ReadOnlySpan<NoiseSource> sources) => _inner.SetNoiseSources(sources);

    private InstanceFlags Effective(in InstanceState state) =>
        state.PickId != 0 && _inheritedSelection.Contains(state.PickId) ? state.Flags | InstanceFlags.Selected : state.Flags;

    private static bool IsFinite(Aabb b) =>
        float.IsFinite(b.Min.X) && float.IsFinite(b.Min.Y) && float.IsFinite(b.Min.Z) &&
        float.IsFinite(b.Max.X) && float.IsFinite(b.Max.Y) && float.IsFinite(b.Max.Z);

    private readonly record struct InstanceState(MeshHandle Mesh, Matrix4x4 World, InstanceFlags Flags, uint PickId);
}
