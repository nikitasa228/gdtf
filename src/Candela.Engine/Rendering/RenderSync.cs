using System.Numerics;
using System.Runtime.InteropServices;
using Candela.Assets;
using Candela.Core.Diagnostics;
using Candela.Core.Geometry;
using Candela.Core.Math;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;
using Candela.Dmx;
using Candela.Electrical;
using Candela.Rendering;
using FixtureProfile = Candela.Dmx.FixtureProfile;

namespace Candela.Engine;

/// <summary>What the render binding drew for one instance (for tools and tests).</summary>
/// <param name="Handle">The renderer instance.</param>
/// <param name="Part">Name of the part: the fixture geometry part (e.g. <c>Head</c>), the model part, or the component.</param>
/// <param name="World">Current world matrix.</param>
/// <param name="Flags">Current flags.</param>
public readonly record struct RenderInstanceInfo(InstanceHandle Handle, string Part, Matrix4x4 World, InstanceFlags Flags)
{
    /// <summary>
    /// World-space bounds of the instance, m: the bounds of its mesh placed with <see cref="World"/> (empty when the mesh
    /// has no vertices).
    /// </summary>
    public Aabb WorldBounds { get; init; } = Aabb.Empty;
}

/// <summary>
/// Keeps an <see cref="IRenderWorld"/> in sync with the engine's scene: retained meshes, materials and instances are
/// created when entities appear and updated incrementally from <see cref="Scene.Changed"/>; lights, cable lines,
/// atmosphere and noise sources are sent every frame. Created by <see cref="StageEngine.AttachRenderWorld"/> and
/// updated by <see cref="StageEngine.Tick"/>.
/// </summary>
/// <remarks>
/// <para><b>Fixtures.</b> The 3D models of a fixture type + mode are loaded once (in the background by default; see
/// <see cref="LoadFixtureModelsInBackground"/>) with Candela.Gdtf's geometry builder and model loader and uploaded once;
/// each fixture gets one instance per model part of every geometry part, whose world matrix follows the part's pose
/// (pan, tilt and other axes from the DMX state, after motor dynamics). Textures (<c>gdtf:</c> keys) and gobo images come
/// from the GDTF archives through <see cref="StageEngine.Images"/>; gobo textures are created once per wheel slot.</para>
/// <para><b>Other content.</b> Walls/floors (<see cref="WallMesh"/> + built-in materials), images (plane + texture;
/// reference drawings unlit), imported models (decoded through <see cref="StageEngine.Models"/>), trusses (model or
/// <see cref="TrussMesh"/>), cables (tubes plus coloured lines for the cable overlay), consoles, power sources and
/// rigging points (simple primitives).</para>
/// <para><b>Flags.</b> Selected (selection), Hidden (entity or layer hidden), CableHighlight (cables and the devices
/// they plug into), CastShadow (component setting), Reference (reference drawings). Every entity drawn gets a pick id;
/// <see cref="TryGetEntity"/> maps it back.</para>
/// <para><b>Lens glow.</b> The instances of a fixture's beam geometry parts (the lens) emit the luminance of the lit
/// beam through <see cref="IRenderWorld.SetInstanceEmission(InstanceHandle, Vector3, Vector3, float)"/>: peak candela
/// / lens area (π·r², r = the beam geometry's BeamRadius as the light collector uses it) in the beam's colour, summed
/// when several beams share a part, at most <see cref="MaxLensLuminance"/>, seen along the part's −Z axis (where the
/// light leaves) and falling off like the beam (half at half the widest beam angle), so a lens glares only for eyes in
/// its beam; the renderer's bloom adds the glare. Updated every frame, sent only when it changes.</para>
/// <para><b>Flames.</b> Every flame of the fire simulation keeps its own temperature grid (its box and resolution):
/// the first is <see cref="AtmosphereState.FlameTemperature"/>, the others <see cref="AtmosphereState.FlameTemperatures"/>.
/// The grids are reused between steps (no allocation while the flames keep their sizes).</para>
/// <para>Main thread only. Steady-state frames (nothing edited, fixtures moving) do not allocate.</para>
/// </remarks>
public sealed partial class RenderSync : IDisposable
{
    /// <summary>
    /// Largest lens luminance sent to the renderer, cd/m² (a 1 mm lens of a 10⁶ cd beam stays finite in single precision;
    /// the renderer clamps the pre-exposed result to its half-float range).
    /// </summary>
    public const float MaxLensLuminance = 1e9f;

    private readonly StageEngine _engine;
    private readonly Dictionary<Guid, EntityVisual> _entities = new();
    private readonly Dictionary<uint, Guid> _byPickId = new();
    private readonly Dictionary<string, FixtureTypeVisual> _fixtureTypes = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, ModelVisual> _models = new();
    private readonly Dictionary<string, MaterialHandle> _builtinMaterials = new(StringComparer.Ordinal);
    private readonly Dictionary<(FixtureProfile Profile, int Wheel, int Slot), TextureHandle> _gobos = new();
    private readonly HashSet<Guid> _rebuild = new();
    private readonly HashSet<Guid> _moved = new();
    private readonly HashSet<Guid> _selected = new();
    private readonly HashSet<Guid> _cableEnds = new();
    private readonly CableAnchorIndex _cableAnchors = new();
    private readonly List<LineVertex> _lines = new();
    private readonly List<Guid> _scratch = new();
    private readonly List<Issue> _issues = new();
    private readonly Dictionary<Guid, bool> _layerVisible = new();
    private readonly AtmosphereState _atmosphere = new();
    private readonly List<VolumeGrid> _flameGrids = new();
    private readonly List<VolumeGrid> _moreFlames = new();
    private PartGlow[] _partGlow = new PartGlow[16];
    private readonly List<ModelVisual> _staleModels = new();
    private readonly Dictionary<Guid, (Task<bool> Task, HashSet<Guid> Entities)> _decoding = new();
    private readonly List<FixtureTypeVisual> _retiredTypes = new();
    private readonly List<TextureHandle> _retiredTextures = new();
    private readonly List<(FixtureProfile Profile, int Wheel, int Slot)> _scratchGobos = new();
    private Vector3[] _nodePositions = new Vector3[16];
    private readonly Dictionary<Guid, int> _nodeIndex = new();
    private MeshHandle _consoleMesh, _powerMesh, _hoistMesh;
    private Aabb _consoleBounds = Aabb.Empty, _powerBounds = Aabb.Empty, _hoistBounds = Aabb.Empty;
    private MaterialHandle _defaultMaterial, _steelMaterial, _hoistMaterial, _greyMaterial;
    private uint _nextPickId = 1;
    private bool _all = true;
    private bool _linesDirty = true;
    private bool _cablesDirty = true;
    private bool _atmosphereDirty = true;
    private long _smokeStep = -1;
    private long _fireStep = -1;
    private float _lastHaze = float.NaN;
    private float _lastAnisotropy = float.NaN;
    private float? _hazeOverride;
    private VolumeGrid? _smokeGrid;
    private Scene _scene;
    private AssetStore _assets;
    private bool _disposed;

    internal RenderSync(StageEngine engine, IRenderWorld world)
    {
        _engine = engine;
        World = world;
        GoboResolver = ResolveGobo;
        _scene = engine.Scene;
        _scene.Changed += OnSceneChanged;
        engine.SceneReplaced += OnSceneReplaced;
        engine.Selection.Changed += OnSelectionChanged;
        _assets = engine.Assets;
        _assets.AssetChanged += OnAssetChanged;
        OnSelectionChanged();
    }

    /// <summary>The render world kept in sync.</summary>
    public IRenderWorld World { get; }

    /// <summary>Resolves gobo wheel slots to textures (cached per slot), for the light collector.</summary>
    public GoboResolver GoboResolver { get; }

    /// <summary>
    /// Loads fixture models on background threads (default) so opening a large rig does not stall the frame; fixtures
    /// appear when their type has loaded. False loads them during <see cref="Update"/> (CLI, tests, thumbnails).
    /// </summary>
    public bool LoadFixtureModelsInBackground { get; set; } = true;

    /// <summary>
    /// Decodes the project's imported models (model and truss assets) on background threads (default): a model in a
    /// native format goes through the import worker, which may take up to <see cref="EngineModels.Timeout"/>, and must
    /// not stall the frame; the objects appear when their model has decoded. False decodes during <see cref="Update"/>.
    /// </summary>
    public bool LoadModelsInBackground { get; set; } = true;

    /// <summary>Imported models still decoding in the background.</summary>
    public int PendingModels => _decoding.Count;

    /// <summary>Uniform haze (1/m) to use instead of the scene's <see cref="SceneSettings.BaseHazeScattering"/>, or null.</summary>
    public float? HazeOverride
    {
        get => _hazeOverride;
        set
        {
            _hazeOverride = value is { } v && float.IsFinite(v) ? Math.Max(0f, v) : null;
            _atmosphereDirty = true;
        }
    }

    /// <summary>Number of renderer instances created for the scene.</summary>
    public int InstanceCount
    {
        get
        {
            int n = 0;
            foreach (var v in _entities.Values) n += v.Instances.Count;
            return n;
        }
    }

    /// <summary>Fixture types whose models are still loading.</summary>
    public int PendingFixtureTypes => _fixtureTypes.Values.Count(t => !t.Uploaded);

    /// <summary>Problems of the render binding (fixture models that could not be loaded).</summary>
    public IReadOnlyList<Issue> Issues => _issues;

    /// <summary>The entity drawn with <paramref name="pickId"/> (from a pick query).</summary>
    public bool TryGetEntity(uint pickId, out Guid entityId) => _byPickId.TryGetValue(pickId, out entityId);

    /// <summary>The pick id of an entity, or 0 when it is not drawn.</summary>
    public uint PickIdOf(Guid entityId) => _entities.TryGetValue(entityId, out var v) ? v.PickId : 0;

    /// <summary>The instances drawn for an entity (empty when none).</summary>
    public IReadOnlyList<RenderInstanceInfo> InstancesOf(Guid entityId)
    {
        if (!_entities.TryGetValue(entityId, out var v)) return Array.Empty<RenderInstanceInfo>();
        return v.Instances.Select(i => new RenderInstanceInfo(i.Handle, i.Name, i.World, v.Flags) { WorldBounds = WorldBoundsOf(i) }).ToList();
    }

    /// <summary>
    /// Brings the render world up to date: scene edits, fixture types that finished loading, fixture poses, flags, lights,
    /// cable lines, atmosphere and noise sources. Called by <see cref="StageEngine.Tick"/>.
    /// </summary>
    public void Update()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_all) RebuildAll();
        RefreshLayerVisibility();
        if (_decoding.Count > 0) CollectDecodedModels(wait: TimeSpan.Zero);
        ProcessChanges();
        UploadLoadedFixtureTypes();
        UpdateFixtures();
        RefreshFlags();
        UpdateLensGlow();
        SendLights();
        DestroyRetired();
        if (_linesDirty) UpdateLines();
        UpdateAtmosphere();
        World.SetNoiseSources(_engine.Simulation.NoiseSourceSpan);
    }

    /// <summary>
    /// Waits until every fixture type that is loading in the background has loaded (or <paramref name="timeout"/> passed)
    /// and uploads them. Returns true when nothing is pending any more.
    /// </summary>
    public bool CompletePendingLoads(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        foreach (var t in _fixtureTypes.Values)
        {
            if (t.Loading is not { } task) continue;
            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero || !task.Wait(left)) break;
        }
        var remaining = deadline - DateTime.UtcNow;
        CollectDecodedModels(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
        RefreshLayerVisibility();
        ProcessChanges();
        UploadLoadedFixtureTypes();
        UpdateFixtures(force: true);
        RefreshFlags();
        return _fixtureTypes.Values.All(t => t.Uploaded) && _decoding.Count == 0;
    }

    /// <summary>
    /// Takes the imported models whose background decode finished (waiting up to <paramref name="wait"/> for the others)
    /// and marks the objects that draw them for a rebuild.
    /// </summary>
    private void CollectDecodedModels(TimeSpan wait)
    {
        var deadline = DateTime.UtcNow + wait;
        _scratch.Clear();
        foreach (var (assetId, pending) in _decoding)
        {
            if (!pending.Task.IsCompleted)
            {
                var left = deadline - DateTime.UtcNow;
                if (left <= TimeSpan.Zero || !pending.Task.Wait(left)) continue;
            }
            _scratch.Add(assetId);
        }
        foreach (var assetId in _scratch)
        {
            // the objects that wait for it are not drawn yet (nothing to keep), so they are listed with the decode
            if (_decoding.Remove(assetId, out var done))
                foreach (var id in done.Entities) _rebuild.Add(id);
        }
        _scratch.Clear();
    }

    /// <summary>Destroys everything this binding created in the render world and stops following the engine.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _scene.Changed -= OnSceneChanged;
        _engine.SceneReplaced -= OnSceneReplaced;
        _engine.Selection.Changed -= OnSelectionChanged;
        _assets.AssetChanged -= OnAssetChanged;
        try
        {
            // the light list refers to gobo textures: clear it before they are destroyed
            World.SetLights(ReadOnlySpan<SpotLight>.Empty);
            DestroyAll(destroySharedResources: true);
            World.SetLines(ReadOnlySpan<LineVertex>.Empty);
            World.SetNoiseSources(ReadOnlySpan<NoiseSource>.Empty);
            World.SetAtmosphere(new AtmosphereState { BaseScattering = 0f });
        }
        catch (ObjectDisposedException)
        {
            // The renderer was disposed first: everything this binding created is gone with it.
        }
    }

    // ------------------------------------------------------------------ scene changes

    private void OnSceneReplaced(Scene scene)
    {
        _scene.Changed -= OnSceneChanged;
        _scene = scene;
        _scene.Changed += OnSceneChanged;
        _assets.AssetChanged -= OnAssetChanged;
        _assets = _engine.Assets;
        _assets.AssetChanged += OnAssetChanged;
        _all = true;
    }

    private void OnSceneChanged(SceneChange change)
    {
        switch (change.Kind)
        {
            case SceneChangeKind.Added or SceneChangeKind.Removed:
                if (change.Entity is { } e)
                    foreach (var d in e.SelfAndDescendants()) _rebuild.Add(d.Id);
                _cablesDirty = true;
                break;
            case SceneChangeKind.TransformChanged:
                if (change.Entity is { } m)
                    foreach (var d in m.SelfAndDescendants()) _moved.Add(d.Id);
                break;
            case SceneChangeKind.Reparented:
                // the subtree moved and may now be below a hidden (or selected) ancestor
                if (change.Entity is { } r)
                    foreach (var d in r.SelfAndDescendants())
                    {
                        _moved.Add(d.Id);
                        _flagsDirty.Add(d.Id);
                    }
                break;
            case SceneChangeKind.VisibilityChanged:
                if (change.Entity is { } shown)
                    foreach (var d in shown.SelfAndDescendants()) _flagsDirty.Add(d.Id);
                break;
            case SceneChangeKind.LayerChanged:
                // layer visibility applies to the entity itself (not inherited by its children)
                if (change.Entity is { } layered) _flagsDirty.Add(layered.Id);
                break;
            case SceneChangeKind.LayersChanged:
                _layersDirty = true;
                break;
            case SceneChangeKind.ComponentChanged when change.Component is FixtureComponent f && change.Entity is { } fe
                && _entities.TryGetValue(fe.Id, out var drawn) && !drawn.Instances.Exists(r => r.Part >= 0 && r.CastShadow != f.CastShadow):
                // manual values, patch, mode: the DMX engine re-evaluates and a new profile is caught when the pose arrives
                break;
            case SceneChangeKind.ComponentAdded or SceneChangeKind.ComponentRemoved or SceneChangeKind.ComponentChanged:
                if (change.Entity is { } c) _rebuild.Add(c.Id);
                if (change.Component is CableComponent) _cablesDirty = true;
                break;
            case SceneChangeKind.SettingsChanged:
                _atmosphereDirty = true;
                break;
        }
    }

    private void OnSelectionChanged()
    {
        // only the entities whose selection changed get new flags (with their subtrees when descendants are outlined)
        _scratchSelection.Clear();
        foreach (var id in _engine.Selection.Ids) _scratchSelection.Add(id);
        foreach (var id in _selected)
            if (!_scratchSelection.Contains(id)) MarkSelectionChanged(id);
        foreach (var id in _scratchSelection)
            if (!_selected.Contains(id)) MarkSelectionChanged(id);
        _selected.Clear();
        foreach (var id in _scratchSelection) _selected.Add(id);
        _scratchSelection.Clear();
    }

    private void OnAssetChanged(Guid assetId)
    {
        // the old model is destroyed once the entities drawing it have been rebuilt (next update)
        if (_models.Remove(assetId, out var model)) _staleModels.Add(model);
        foreach (var v in _entities.Values)
            if (v.AssetIds.Contains(assetId)) _rebuild.Add(v.Entity.Id);
    }

    private void RebuildAll()
    {
        _all = false;
        DestroyAll(destroySharedResources: false);
        // models belong to the project's assets; fixture types and built-in materials stay valid across projects
        foreach (var m in _models.Values) m.Destroy(World);
        _models.Clear();
        foreach (var m in _staleModels) m.Destroy(World);
        _staleModels.Clear();
        _decoding.Clear(); // decodes of the previous project's assets finish into its asset store
        _rebuild.Clear();
        _moved.Clear();
        foreach (var e in _scene.All()) _rebuild.Add(e.Id);
        _layersDirty = true;
        _flagsDirty.Clear();
        _cablesDirty = true;
        _atmosphereDirty = true;
        _smokeStep = _fireStep = -1;
    }

    private void ProcessChanges()
    {
        if (_cablesDirty) RebuildCableIndex();
        if (_moved.Count > 0 || _rebuild.Count > 0)
        {
            // cables follow the entities their nodes are anchored to (and lose them when they are removed)
            _scratch.Clear();
            _scratch.AddRange(_moved);
            _scratch.AddRange(_rebuild);
            foreach (var id in _scratch)
            {
                if (!_cableAnchors.TryGetCables(id, out var cables)) continue;
                foreach (var cable in cables) _rebuild.Add(cable);
                _linesDirty = true;
            }
        }
        if (_rebuild.Count > 0)
        {
            _scratch.Clear();
            _scratch.AddRange(_rebuild);
            _rebuild.Clear();
            foreach (var id in _scratch)
            {
                _moved.Remove(id);
                Rebuild(id);
            }
        }
        if (_moved.Count > 0)
        {
            foreach (var id in _moved)
                if (_entities.TryGetValue(id, out var v) && v.Kind != VisualKind.Cable) PlaceStatic(v);
            _moved.Clear();
        }
    }

    /// <summary>A fixture type replaced by a new profile: destroyed by <see cref="DestroyRetired"/>.</summary>
    private void Retire(FixtureTypeVisual type)
    {
        _retiredTypes.Add(type);
        _scratchGobos.Clear();
        foreach (var key in _gobos.Keys)
            if (ReferenceEquals(key.Profile, type.Profile)) _scratchGobos.Add(key);
        foreach (var key in _scratchGobos)
        {
            if (_gobos.Remove(key, out var texture) && texture.IsValid) _retiredTextures.Add(texture);
        }
    }

    /// <summary>
    /// Destroys models, fixture types and gobo textures that were replaced during this update, after every entity
    /// drawing them was rebuilt and the new light list was sent.
    /// </summary>
    private void DestroyRetired()
    {
        if (_retiredTypes.Count > 0)
        {
            foreach (var type in _retiredTypes)
            {
                _scratch.Clear();
                foreach (var v in _entities.Values)
                    if (ReferenceEquals(v.FixtureType, type)) _scratch.Add(v.Entity.Id);
                foreach (var id in _scratch) Rebuild(id);
            }
            foreach (var type in _retiredTypes)
                if (type.Uploaded) type.Destroy(World);
            _retiredTypes.Clear();
        }
        if (_staleModels.Count > 0)
        {
            foreach (var m in _staleModels) m.Destroy(World);
            _staleModels.Clear();
        }
        if (_retiredTextures.Count > 0)
        {
            foreach (var t in _retiredTextures) World.DestroyTexture(t);
            _retiredTextures.Clear();
        }
    }

    private void Rebuild(Guid id)
    {
        _structureVersion++;
        if (_entities.Remove(id, out var old)) Destroy(old);
        var entity = _scene.Find(id);
        if (entity is null || entity.Components.Count == 0) return;
        var v = new EntityVisual(entity, _nextPickId++);
        BuildStatic(v);
        if (entity.Has<FixtureComponent>())
        {
            v.Kind = VisualKind.Fixture;
            if (_engine.Poses.TryGet(id, out var pose)) BuildFixture(v, pose);
        }
        if (v.Instances.Count == 0 && v.Kind == VisualKind.Static && !entity.Has<FixtureComponent>())
        {
            // nothing drawable (groups, cameras, atmosphere volumes …)
            return;
        }
        _entities[id] = v;
        _byPickId[v.PickId] = id;
    }

    // ------------------------------------------------------------------ static content

    private void BuildStatic(EntityVisual v)
    {
        var entity = v.Entity;
        foreach (var component in entity.Components)
        {
            switch (component)
            {
                case WallComponent wall:
                    {
                        var mesh = OwnMesh(v, WallMesh.Create(wall), out var bounds);
                        AddStatic(v, mesh, bounds, Builtin(wall.MaterialId, wall.UvScale), Matrix4x4.Identity, "Wall", castShadow: true);
                        break;
                    }
                case ImageComponent image when image.ImageAssetId != Guid.Empty && _engine.Assets.Contains(image.ImageAssetId):
                    {
                        v.AssetIds.Add(image.ImageAssetId);
                        var mesh = OwnMesh(v, ImagePlaneMesh.Create(image), out var bounds);
                        var material = World.CreateMaterial(ImagePlaneMesh.CreateMaterial(image), _engine.Images);
                        v.OwnedMaterials.Add(material);
                        bool reference = image.Mode == ImageMode.Reference;
                        AddStatic(v, mesh, bounds, material, Matrix4x4.Identity, "Image", castShadow: !reference, reference);
                        break;
                    }
                case MeshComponent mesh:
                    v.AssetIds.Add(mesh.ModelAssetId);
                    AddModel(v, mesh.ModelAssetId, mesh.MaterialOverrides, mesh.CastShadow);
                    break;
                case TrussComponent truss:
                    if (truss.ModelAssetId is { } trussModel && trussModel != Guid.Empty)
                    {
                        v.AssetIds.Add(trussModel);
                        if (AddModel(v, trussModel, null, castShadow: true)) break;
                    }
                    {
                        var mesh = OwnMesh(v, TrussMesh.Create(truss), out var bounds);
                        AddStatic(v, mesh, bounds, Builtin(MaterialIds.AluminiumTruss, 1f), Matrix4x4.Identity, "Truss", castShadow: true);
                    }
                    break;
                case RiggingPointComponent:
                    EnsurePrimitives();
                    AddStatic(v, _hoistMesh, _hoistBounds, _hoistMaterial, Matrix4x4.CreateTranslation(0f, 0f, 0.3f), "Hoist", castShadow: true);
                    break;
                case ConsoleComponent:
                    EnsurePrimitives();
                    AddStatic(v, _consoleMesh, _consoleBounds, _steelMaterial, Matrix4x4.CreateTranslation(0f, 0f, DeviceCatalog.ConsoleSize.Z / 2f), "Console", castShadow: true);
                    break;
                case PowerSourceComponent:
                    EnsurePrimitives();
                    AddStatic(v, _powerMesh, _powerBounds, _greyMaterial, Matrix4x4.CreateTranslation(0f, 0f, DeviceCatalog.PowerSourceSize.Z / 2f), "Power source", castShadow: true);
                    break;
                case CableComponent cable:
                    BuildCable(v, cable);
                    break;
            }
        }
    }

    private void AddStatic(EntityVisual v, MeshHandle mesh, in Aabb bounds, MaterialHandle material, Matrix4x4 local, string name, bool castShadow,
        bool reference = false)
    {
        Span<MaterialHandle> one = stackalloc MaterialHandle[1];
        one[0] = material;
        AddInstance(v, mesh, bounds, one, local, -1, name, castShadow, reference);
    }

    private void AddInstance(EntityVisual v, MeshHandle mesh, in Aabb bounds, ReadOnlySpan<MaterialHandle> materials, Matrix4x4 local, int part,
        string name, bool castShadow, bool reference = false, Matrix4x4? world = null)
    {
        var m = world ?? (v.Kind == VisualKind.Cable ? local : local * v.Entity.WorldMatrix);
        if (!IsFinite(m)) m = Matrix4x4.Identity;
        v.Flags = ComputeFlags(v);
        var record = new InstanceRecord(default, local, part, m, name, castShadow, reference) { Bounds = bounds };
        var handle = World.CreateInstance(mesh, materials, m, InstanceFlagsOf(v, record), v.PickId);
        v.Instances.Add(record with { Handle = handle });
    }

    private void PlaceStatic(EntityVisual v)
    {
        var entityWorld = v.Entity.WorldMatrix;
        var span = CollectionsMarshal.AsSpan(v.Instances);
        for (int i = 0; i < span.Length; i++)
        {
            ref var r = ref span[i];
            if (r.Part >= 0) continue;
            var world = r.Local * entityWorld;
            if (!IsFinite(world)) continue;
            r.World = world;
            World.UpdateInstance(r.Handle, world, InstanceFlagsOf(v, r));
        }
    }

    private bool AddModel(EntityVisual v, Guid assetId, Dictionary<int, string>? overrides, bool castShadow)
    {
        if (assetId == Guid.Empty || !_engine.Assets.Contains(assetId)) return false;
        if (!_models.TryGetValue(assetId, out var model))
        {
            if (LoadModelsInBackground && !_engine.Models.IsDecoded(assetId))
            {
                // decoded on a worker thread (the import worker may run for seconds); drawn once it is done
                if (!_decoding.TryGetValue(assetId, out var pending))
                    _decoding[assetId] = pending = (_engine.Models.DecodeAsync(assetId), new HashSet<Guid>());
                pending.Entities.Add(v.Entity.Id);
                return false;
            }
            var data = _engine.Models.GetModelData(assetId);
            if (data is null) return false;
            model = ModelVisual.Create(World, data, _engine.Images, DefaultMaterial());
            _models[assetId] = model;
        }
        var materials = model.Materials;
        if (overrides is { Count: > 0 })
        {
            materials = (MaterialHandle[])materials.Clone();
            foreach (var (slot, id) in overrides)
                if ((uint)slot < (uint)materials.Length && !string.IsNullOrEmpty(id)) materials[slot] = Builtin(id, 1f);
        }
        for (int i = 0; i < model.Meshes.Length; i++)
            AddInstance(v, model.Meshes[i], model.Bounds[i], materials, model.Transforms[i], -1, model.Names[i], castShadow);
        return model.Meshes.Length > 0;
    }

    private void BuildCable(EntityVisual v, CableComponent cable)
    {
        v.Kind = VisualKind.Cable;
        if (cable.Nodes.Count < 2 || cable.Segments.Count == 0) return;
        var entity = v.Entity;
        var cableWorld = entity.WorldMatrix;
        var type = _engine.Network.Catalog.Find(cable.CableTypeId);
        MeshData mesh;
        try
        {
            mesh = CableTubeMesh.Create(cable, node => CableGeometry.NodeWorldPosition(_scene, cableWorld, node, out _),
                new CableTubeOptions
                {
                    SlackFactor = float.IsFinite(cable.SlackFactor) ? Math.Max(1f, cable.SlackFactor) : 1f,
                    Radius = type?.IsPower == true ? 0.006f : 0.004f,
                });
        }
        catch (Exception e) when (e is ArgumentException or InvalidDataException)
        {
            return;
        }
        if (mesh.Positions.Length == 0 || mesh.Indices.Length == 0) return;
        var handle = OwnMesh(v, mesh, out var bounds);
        var (r, g, b) = type?.ColorSrgb ?? (0.5f, 0.5f, 0.5f);
        var tint = new Vector3(SrgbToLinear(r), SrgbToLinear(g), SrgbToLinear(b));
        var rubber = World.CreateMaterial(new MaterialData
        {
            Name = "Cable",
            BaseColorFactor = new Vector4(Vector3.Lerp(new Vector3(0.03f), tint, 0.25f), 1f),
            Roughness = 0.55f,
        }, null);
        v.OwnedMaterials.Add(rubber);
        AddStatic(v, handle, bounds, rubber, Matrix4x4.Identity, "Cable", castShadow: false);
    }

    private void RebuildCableIndex()
    {
        _cablesDirty = false;
        _linesDirty = true;
        _cableAnchors.Invalidate();
        _cableAnchors.EnsureBuilt(_scene);
        // the devices that lose or gain the cable highlight get new flags
        foreach (var id in _cableEnds) _flagsDirty.Add(id);
        _cableEnds.Clear();
        foreach (var (entity, cable) in _scene.Query<CableComponent>())
        {
            _cableEnds.Add(entity.Id);
            foreach (var end in cable.Endpoints)
                if (end is not null && end.EntityId != Guid.Empty) _cableEnds.Add(end.EntityId);
        }
        foreach (var id in _cableEnds) _flagsDirty.Add(id);
    }

    private void UpdateLines()
    {
        _linesDirty = false;
        _lines.Clear();
        foreach (var (entity, cable) in _scene.Query<CableComponent>())
        {
            if (!entity.IsVisibleInHierarchy || cable.Nodes.Count < 2) continue;
            if (_nodePositions.Length < cable.Nodes.Count) _nodePositions = new Vector3[Math.Max(cable.Nodes.Count, _nodePositions.Length * 2)];
            int n = CableGeometry.WriteNodeWorldPositions(_scene, entity, cable, _nodePositions);
            var (r, g, b) = _engine.Network.Catalog.Find(cable.CableTypeId)?.ColorSrgb ?? (0.8f, 0.8f, 0.8f);
            var color = new Vector4(r, g, b, 1f);
            // node id → position index (the first node of a repeated id, as CableGeometry does), so a long cable costs
            // nodes + segments, not nodes × segments
            _nodeIndex.Clear();
            for (int i = 0; i < n; i++)
                if (cable.Nodes[i] is { } node) _nodeIndex.TryAdd(node.Id, i);
            foreach (var segment in cable.Segments)
            {
                if (!_nodeIndex.TryGetValue(segment.FromNode, out int a) || !_nodeIndex.TryGetValue(segment.ToNode, out int c)) continue;
                var p0 = _nodePositions[a];
                var p1 = _nodePositions[c];
                if (!IsFinite(p0) || !IsFinite(p1)) continue;
                _lines.Add(new LineVertex { Position = p0, Color = color, Width = 3f });
                _lines.Add(new LineVertex { Position = p1, Color = color, Width = 3f });
            }
        }
        World.SetLines(CollectionsMarshal.AsSpan(_lines));
    }

    // ------------------------------------------------------------------ fixtures

    private void UpdateFixtures(bool force = false)
    {
        var poses = _engine.Poses;
        var removed = poses.Removed;
        for (int i = 0; i < removed.Count; i++)
            if (_entities.TryGetValue(removed[i], out var gone) && gone.FixtureType is not null) _rebuild.Add(removed[i]);
        if (_rebuild.Count > 0) ProcessChanges();

        if (force)
        {
            foreach (var pose in poses.All) PlaceFixture(pose);
            return;
        }
        var changed = poses.Changed;
        for (int i = 0; i < changed.Count; i++)
            if (poses.TryGet(changed[i], out var pose)) PlaceFixture(pose);
    }

    private void PlaceFixture(FixturePose pose)
    {
        var id = pose.Entity.Id;
        if (!_entities.TryGetValue(id, out var v))
        {
            Rebuild(id);
            if (!_entities.TryGetValue(id, out v)) return;
        }
        if (v.FixtureType is null || !ReferenceEquals(v.FixtureType.Profile, pose.Profile) || (v.FixtureType.Uploaded && !v.HasFixtureInstances))
        {
            Rebuild(id);
            return;
        }
        var world = pose.PartWorld;
        var span = CollectionsMarshal.AsSpan(v.Instances);
        for (int i = 0; i < span.Length; i++)
        {
            ref var r = ref span[i];
            if (r.Part < 0 || r.Part >= world.Length) continue;
            var m = r.Local * world[r.Part];
            if (!IsFinite(m)) continue;
            r.World = m;
            World.UpdateInstance(r.Handle, m, InstanceFlagsOf(v, r));
        }
    }

    private void BuildFixture(EntityVisual v, FixturePose pose)
    {
        var profile = pose.Profile;
        if (!_fixtureTypes.TryGetValue(profile.Key, out var type))
        {
            var entry = _engine.Dmx.GetResolution(v.Entity.Id)?.Entry;
            type = new FixtureTypeVisual(profile, entry?.FileName);
            _fixtureTypes[profile.Key] = type;
            if (entry is null)
            {
                type.Error = "the GDTF file is not in the library";
                type.Uploaded = true;
            }
            else if (LoadFixtureModelsInBackground)
            {
                string path = entry.FilePath;
                type.Loading = Task.Run(() => FixtureModelLoader.Load(path, profile));
            }
            else
            {
                Upload(type, FixtureModelLoader.Load(entry.FilePath, profile));
            }
        }
        if (!ReferenceEquals(type.Profile, profile))
        {
            // the profile cache was rebuilt (library changed): load the new profile's type; the old one (and its gobo
            // textures) is destroyed at the end of the update, when no fixture draws it any more
            _fixtureTypes.Remove(profile.Key);
            Retire(type);
            BuildFixture(v, pose);
            return;
        }
        v.FixtureType = type;
        if (!type.Uploaded) return;
        bool cast = pose.Fixture.CastShadow;
        var world = pose.PartWorld;
        foreach (var draw in type.Draws)
        {
            var m = draw.Part < world.Length ? draw.Local * world[draw.Part] : draw.Local * v.Entity.WorldMatrix;
            AddInstance(v, draw.Mesh, draw.Bounds, draw.Materials, draw.Local, draw.Part, draw.Name, cast, world: m);
        }
        v.HasFixtureInstances = true;
    }

    private void UploadLoadedFixtureTypes()
    {
        foreach (var type in _fixtureTypes.Values)
        {
            if (type.Uploaded || type.Loading is not { IsCompleted: true } task) continue;
            var loaded = task.IsCompletedSuccessfully ? task.Result : new LoadedFixtureModels(new(), task.Exception?.GetBaseException().Message ?? "loading failed");
            type.Loading = null;
            Upload(type, loaded);
            foreach (var v in _entities.Values)
                if (ReferenceEquals(v.FixtureType, type)) _rebuild.Add(v.Entity.Id);
        }
        if (_rebuild.Count > 0) ProcessChanges();
    }

    private void Upload(FixtureTypeVisual type, LoadedFixtureModels loaded)
    {
        type.Uploaded = true;
        type.Error = loaded.Error;
        if (loaded.Error is not null)
        {
            _issues.Add(new Issue(IssueSeverity.Warning, IssueCategory.Fixture, EngineIssueCodes.FixtureModelFailed,
                $"The 3D model of {type.Profile.FixtureType.Manufacturer} {type.Profile.FixtureType.Name} could not be loaded ({loaded.Error}); its light is shown without a body."));
        }
        var perModel = new Dictionary<ModelData, (MeshHandle[] Meshes, MaterialHandle[] Materials)>(ReferenceEqualityComparer.Instance);
        var draws = new List<FixtureDraw>();
        foreach (var (part, model) in loaded.Parts)
        {
            if (!perModel.TryGetValue(model, out var res))
            {
                var materials = model.Materials.Count == 0
                    ? new[] { DefaultMaterial() }
                    : model.Materials.Select(m => World.CreateMaterial(m, _engine.Images)).ToArray();
                if (model.Materials.Count > 0) type.Materials.AddRange(materials);
                var meshes = new MeshHandle[model.Parts.Count];
                for (int i = 0; i < model.Parts.Count; i++)
                {
                    var mesh = model.Parts[i].Mesh;
                    if (mesh.Positions.Length == 0 || mesh.Indices.Length == 0) continue;
                    meshes[i] = World.CreateMesh(mesh);
                    type.Meshes.Add(meshes[i]);
                }
                perModel[model] = res = (meshes, materials);
            }
            string name = type.Profile.Geometry.Parts[part].Name;
            for (int i = 0; i < model.Parts.Count; i++)
                if (res.Meshes[i].IsValid)
                    draws.Add(new FixtureDraw(part, res.Meshes[i], model.Parts[i].Transform, res.Materials, name, model.Parts[i].Mesh.ComputeBounds()));
        }
        type.Draws = draws.ToArray();
    }

    /// <summary>
    /// Lens glow: every instance of a fixture's beam geometry part emits the luminance of the beams of that part (see
    /// the class remarks); only changes are sent to the render world.
    /// </summary>
    private void UpdateLensGlow()
    {
        foreach (var v in _entities.Values)
        {
            if (v.Kind != VisualKind.Fixture || !v.HasFixtureInstances || !_engine.Poses.TryGet(v.Entity.Id, out var pose)) continue;
            // The glow of every part first (one pass over the beams), then the instances: linear in parts + beams +
            // instances, also for pixel bars with hundreds of beams.
            var world = pose.PartWorld;
            int parts = world.Length;
            if (_partGlow.Length < parts) Array.Resize(ref _partGlow, Math.Max(parts, 2 * _partGlow.Length));
            var glowOf = _partGlow.AsSpan(0, parts);
            glowOf.Clear();
            var beams = pose.State.Beams;
            for (int b = 0; b < beams.Count; b++)
            {
                var beam = beams[b];
                if ((uint)beam.PartIndex >= (uint)parts) continue;
                var lens = LensLuminance(beam);
                if (lens == Vector3.Zero) continue;
                ref var g = ref glowOf[beam.PartIndex];
                g.Luminance += lens;
                if (float.IsFinite(beam.BeamAngle)) g.HalfAngle = MathF.Max(g.HalfAngle, 0.5f * beam.BeamAngle);
            }

            var span = CollectionsMarshal.AsSpan(v.Instances);
            for (int i = 0; i < span.Length; i++)
            {
                ref var r = ref span[i];
                if (r.Part < 0) continue;
                var part = r.Part < parts ? glowOf[r.Part] : default;
                var glow = Vector3.Min(part.Luminance, new Vector3(MaxLensLuminance));
                float halfAngle = part.HalfAngle;
                var axis = r.Part < parts ? Vector3.TransformNormal(-Vector3.UnitZ, world[r.Part]) : Vector3.Zero;
                if (glow == r.Emission && axis == r.EmissionAxis && halfAngle == r.EmissionHalfAngle) continue;
                r.Emission = glow;
                r.EmissionAxis = axis;
                r.EmissionHalfAngle = halfAngle;
                World.SetInstanceEmission(r.Handle, glow, axis, halfAngle);
            }
        }
    }

    /// <summary>Lens glow of one geometry part (sum of its beams), see <see cref="UpdateLensGlow"/>.</summary>
    private struct PartGlow
    {
        public Vector3 Luminance;
        public float HalfAngle;
    }

    /// <summary>Luminance of a beam's lens, cd/m² per Rec.2020 channel: peak candela / (π r²) in the beam's colour.</summary>
    private static Vector3 LensLuminance(BeamState beam)
    {
        float candela = beam.PeakCandela;
        if (!(candela > 0f) || !float.IsFinite(candela)) return Vector3.Zero;
        float radius = beam.Beam.Geometry.BeamRadius;
        radius = float.IsFinite(radius) && radius > 0f ? Math.Clamp(radius, 0.001f, 2f) : 0.05f;
        var color = beam.Color.Rgb;
        if (!IsFinite(color)) return Vector3.Zero;
        return Vector3.Max(color, Vector3.Zero) * MathF.Min(candela / (MathF.PI * radius * radius), MaxLensLuminance);
    }

    // ------------------------------------------------------------------ gobos

    private TextureHandle ResolveGobo(FixtureProfile profile, int wheelIndex, int slot)
    {
        var key = (profile, wheelIndex, slot);
        if (_gobos.TryGetValue(key, out var handle)) return handle;
        handle = default;
        if ((uint)wheelIndex >= (uint)profile.Wheels.Count || profile.Wheels[wheelIndex].Source.GetSlot(slot) is not { MediaPath: { Length: > 0 } media })
        {
            _gobos[key] = handle;
            return handle;
        }
        // The light list is collected before this binding's update of the same frame, so a fixture type seen for the
        // first time (just opened, attached or added) is not registered yet: try again next frame instead of caching
        // "no gobo" for good.
        if (!_fixtureTypes.TryGetValue(profile.Key, out var type)) return handle;
        if (type.FileName is { } file
            && _engine.Images.Load(Candela.Gdtf.GdtfImageSource.MakeKey(file, media)) is { } image && GoboImage.ToTransmission(image) is { } gobo)
        {
            handle = World.CreateTexture(gobo, srgb: true);
        }
        _gobos[key] = handle;
        return handle;
    }

    // ------------------------------------------------------------------ flags

    /// <summary>
    /// Sends new flags for the entities whose selection, visibility, layer or cable highlight changed since the last
    /// update (see <see cref="OnSceneChanged"/>, <see cref="OnSelectionChanged"/>); a change of the layer list re-derives
    /// every entity. Nothing is scanned while nothing changed.
    /// </summary>
    private void RefreshFlags()
    {
        RefreshLayerVisibility();
        if (_allFlagsDirty)
        {
            _allFlagsDirty = false;
            _flagsDirty.Clear();
            foreach (var v in _entities.Values) ApplyFlags(v);
            return;
        }
        if (_flagsDirty.Count == 0) return;
        foreach (var id in _flagsDirty)
            if (_entities.TryGetValue(id, out var v)) ApplyFlags(v);
        _flagsDirty.Clear();
    }

    private void ApplyFlags(EntityVisual v)
    {
        var flags = ComputeFlags(v);
        if (flags == v.Flags) return;
        if (v.Kind == VisualKind.Cable && (flags & InstanceFlags.Hidden) != (v.Flags & InstanceFlags.Hidden)) _linesDirty = true;
        v.Flags = flags;
        foreach (var r in v.Instances) World.UpdateInstance(r.Handle, r.World, InstanceFlagsOf(v, r));
    }

    /// <summary>Re-reads the layers' visibility after the layer list changed; every entity's flags are then re-derived.</summary>
    private void RefreshLayerVisibility()
    {
        if (!_layersDirty) return;
        _layersDirty = false;
        _layerVisible.Clear();
        foreach (var layer in _scene.Layers) _layerVisible[layer.Id] = layer.Visible;
        _allFlagsDirty = true;
    }

    /// <summary>The entity-level flags (selection, visibility, cable highlight); per-instance flags are added by <see cref="InstanceFlagsOf"/>.</summary>
    private InstanceFlags ComputeFlags(EntityVisual v)
    {
        var e = v.Entity;
        var flags = InstanceFlags.None;
        if (_selected.Contains(e.Id) || (_outlineDescendants && HasSelectedAncestor(e))) flags |= InstanceFlags.Selected;
        bool layerVisible = !_layerVisible.TryGetValue(e.LayerId, out bool lv) || lv;
        if (!e.IsVisibleInHierarchy || !layerVisible) flags |= InstanceFlags.Hidden;
        if (_cableEnds.Contains(e.Id)) flags |= InstanceFlags.CableHighlight;
        return flags;
    }

    private static InstanceFlags InstanceFlagsOf(EntityVisual v, in InstanceRecord r) =>
        v.Flags | (r.CastShadow ? InstanceFlags.CastShadow : 0) | (r.Reference ? InstanceFlags.Reference : 0);

    // ------------------------------------------------------------------ atmosphere

    private void UpdateAtmosphere()
    {
        var settings = _scene.Settings;
        float haze = _hazeOverride ?? (float.IsFinite(settings.BaseHazeScattering) ? Math.Max(0f, settings.BaseHazeScattering) : 0f);
        float g = float.IsFinite(settings.HazeAnisotropy) ? Math.Clamp(settings.HazeAnisotropy, -0.95f, 0.95f) : 0.7f;
        bool changed = _atmosphereDirty || haze != _lastHaze || g != _lastAnisotropy;
        _atmosphereDirty = false;
        _lastHaze = haze;
        _lastAnisotropy = g;
        _atmosphere.BaseScattering = haze;
        _atmosphere.Anisotropy = g;

        var simulation = _engine.Simulation;
        var smoke = simulation.Smoke;
        if (smoke is null || !_engine.Settings.SimulateSmoke)
        {
            if (_atmosphere.SmokeDensity is not null)
            {
                _atmosphere.SmokeDensity = null;
                _smokeGrid = null;
                changed = true;
            }
            _smokeStep = -1;
        }
        else if (smoke.StepCount != _smokeStep || _smokeGrid is null)
        {
            _smokeStep = smoke.StepCount;
            if (_smokeGrid is null || _smokeGrid.SizeX != smoke.SizeX || _smokeGrid.SizeY != smoke.SizeY || _smokeGrid.SizeZ != smoke.SizeZ
                || _smokeGrid.Min != smoke.Bounds.Min || _smokeGrid.Max != smoke.Bounds.Max)
            {
                _smokeGrid = new VolumeGrid
                {
                    Min = smoke.Bounds.Min,
                    Max = smoke.Bounds.Max,
                    SizeX = smoke.SizeX,
                    SizeY = smoke.SizeY,
                    SizeZ = smoke.SizeZ,
                    Values = new float[smoke.CellCount],
                };
            }
            smoke.CopyDensityTo(_smokeGrid.Values);
            _atmosphere.SmokeDensity = _smokeGrid;
            _atmosphere.SmokeScatteringAtFullDensity = simulation.SmokeScatteringAtFullDensity;
            changed = true;
        }

        var fire = simulation.Fire;
        if (fire.Volumes.Count == 0 || !_engine.Settings.SimulateFire)
        {
            if (_atmosphere.FlameTemperature is not null || _atmosphere.FlameTemperatures is not null)
            {
                _atmosphere.FlameTemperature = null;
                _atmosphere.FlameTemperatures = null;
                changed = true;
            }
            _fireStep = -1;
        }
        else if (fire.StepCount != _fireStep)
        {
            _fireStep = fire.StepCount;
            UpdateFlameGrids(fire.Volumes);
            changed = true;
        }

        if (changed) World.SetAtmosphere(_atmosphere);
    }

    /// <summary>
    /// One temperature grid per flame (its own box and resolution, reusing the grid objects and their value arrays):
    /// the first goes to <see cref="AtmosphereState.FlameTemperature"/>, the others to
    /// <see cref="AtmosphereState.FlameTemperatures"/>.
    /// </summary>
    private void UpdateFlameGrids(IReadOnlyList<Candela.Simulation.FlameVolume> volumes)
    {
        int n = 0;
        _moreFlames.Clear();
        for (int i = 0; i < volumes.Count; i++)
        {
            var v = volumes[i];
            int count = v.SizeX * v.SizeY * v.SizeZ;
            if (v.Bounds.IsEmpty || v.SizeX <= 0 || v.SizeY <= 0 || v.SizeZ <= 0 || count <= 0) continue;
            if (n == _flameGrids.Count) _flameGrids.Add(new VolumeGrid());
            var grid = _flameGrids[n++];
            if (grid.Values.Length < count) grid.Values = new float[Math.Max(count, 2 * grid.Values.Length)];
            grid.Min = v.Bounds.Min;
            grid.Max = v.Bounds.Max;
            grid.SizeX = v.SizeX;
            grid.SizeY = v.SizeY;
            grid.SizeZ = v.SizeZ;
            v.Temperature.CopyTo(grid.Values);
            if (n > 1) _moreFlames.Add(grid);
        }
        _atmosphere.FlameTemperature = n > 0 ? _flameGrids[0] : null;
        _atmosphere.FlameTemperatures = _moreFlames.Count > 0 ? _moreFlames : null;
    }

    // ------------------------------------------------------------------ resources

    private MeshHandle OwnMesh(EntityVisual v, MeshData mesh, out Aabb bounds)
    {
        var handle = World.CreateMesh(mesh);
        v.OwnedMeshes.Add(handle);
        bounds = mesh.ComputeBounds();
        return handle;
    }

    private MaterialHandle Builtin(string? id, float uvScale)
    {
        string materialId = string.IsNullOrEmpty(id) ? MaterialIds.Default : id;
        float scale = float.IsFinite(uvScale) && uvScale > 0f ? uvScale : 1f;
        string key = materialId + "|" + scale.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        if (_builtinMaterials.TryGetValue(key, out var handle)) return handle;
        var data = _engine.Assets.Materials.GetMaterial(materialId);
        data.UvScale *= scale;
        handle = World.CreateMaterial(data, _engine.Images);
        _builtinMaterials[key] = handle;
        return handle;
    }

    private MaterialHandle DefaultMaterial()
    {
        if (!_defaultMaterial.IsValid) _defaultMaterial = World.CreateMaterial(ModelImporter.CreateDefaultMaterial(), null);
        return _defaultMaterial;
    }

    private void EnsurePrimitives()
    {
        if (_consoleMesh.IsValid) return;
        // the boxes the editor places and the cable tool puts the ports on (Candela.Electrical's device catalogue)
        var console = Primitives.Box(DeviceCatalog.ConsoleSize);
        var power = Primitives.Box(DeviceCatalog.PowerSourceSize);
        var hoist = Primitives.Cylinder(0.12f, 0.45f);
        _consoleMesh = World.CreateMesh(console);
        _powerMesh = World.CreateMesh(power);
        _hoistMesh = World.CreateMesh(hoist);
        _consoleBounds = console.ComputeBounds();
        _powerBounds = power.ComputeBounds();
        _hoistBounds = hoist.ComputeBounds();
        _steelMaterial = Builtin(MaterialIds.SteelBlack, 1f);
        _greyMaterial = World.CreateMaterial(new MaterialData { Name = "Distro", BaseColorFactor = new Vector4(0.25f, 0.25f, 0.27f, 1f), Roughness = 0.5f }, null);
        _hoistMaterial = World.CreateMaterial(new MaterialData { Name = "Hoist", BaseColorFactor = new Vector4(0.45f, 0.04f, 0.03f, 1f), Roughness = 0.45f }, null);
    }

    private void Destroy(EntityVisual v)
    {
        foreach (var r in v.Instances) World.DestroyInstance(r.Handle);
        foreach (var m in v.OwnedMeshes) World.DestroyMesh(m);
        foreach (var m in v.OwnedMaterials) World.DestroyMaterial(m);
        v.Instances.Clear();
        v.OwnedMeshes.Clear();
        v.OwnedMaterials.Clear();
        _byPickId.Remove(v.PickId);
    }

    private void DestroyAll(bool destroySharedResources)
    {
        foreach (var v in _entities.Values) Destroy(v);
        _entities.Clear();
        _byPickId.Clear();
        _lines.Clear();
        _linesDirty = true;
        if (!destroySharedResources) return;
        foreach (var t in _fixtureTypes.Values)
            if (t.Uploaded) t.Destroy(World);
        _fixtureTypes.Clear();
        foreach (var m in _models.Values) m.Destroy(World);
        _models.Clear();
        foreach (var m in _staleModels) m.Destroy(World);
        _staleModels.Clear();
        foreach (var t in _retiredTypes)
            if (t.Uploaded) t.Destroy(World);
        _retiredTypes.Clear();
        foreach (var t in _retiredTextures) World.DestroyTexture(t);
        _retiredTextures.Clear();
        foreach (var h in _builtinMaterials.Values) World.DestroyMaterial(h);
        _builtinMaterials.Clear();
        foreach (var t in _gobos.Values)
            if (t.IsValid) World.DestroyTexture(t);
        _gobos.Clear();
        if (_consoleMesh.IsValid)
        {
            World.DestroyMesh(_consoleMesh);
            World.DestroyMesh(_powerMesh);
            World.DestroyMesh(_hoistMesh);
            World.DestroyMaterial(_greyMaterial);
            World.DestroyMaterial(_hoistMaterial);
            _consoleMesh = _powerMesh = _hoistMesh = default;
        }
        if (_defaultMaterial.IsValid) World.DestroyMaterial(_defaultMaterial);
        _defaultMaterial = default;
    }

    private static float SrgbToLinear(float c) => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);

    private static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    private static bool IsFinite(in Matrix4x4 m) =>
        float.IsFinite(m.M11) && float.IsFinite(m.M12) && float.IsFinite(m.M13) && float.IsFinite(m.M21) && float.IsFinite(m.M22)
        && float.IsFinite(m.M23) && float.IsFinite(m.M31) && float.IsFinite(m.M32) && float.IsFinite(m.M33) && float.IsFinite(m.M41)
        && float.IsFinite(m.M42) && float.IsFinite(m.M43);

    // ------------------------------------------------------------------ records

    private enum VisualKind
    {
        Static,
        Fixture,
        Cable,
    }

    private record struct InstanceRecord(InstanceHandle Handle, Matrix4x4 Local, int Part, Matrix4x4 World, string Name, bool CastShadow, bool Reference)
    {
        /// <summary>Lens glow last sent to the render world (cd/m², Rec.2020).</summary>
        public Vector3 Emission;

        /// <summary>World axis of the lens glow last sent (the part's −Z).</summary>
        public Vector3 EmissionAxis;

        /// <summary>Half-angle of the lens glow last sent, rad.</summary>
        public float EmissionHalfAngle;

        /// <summary>Bounds of the instance's mesh in its own space, m (empty for a mesh without vertices).</summary>
        public Aabb Bounds;
    }

    private sealed class EntityVisual(Entity entity, uint pickId)
    {
        public Entity Entity { get; } = entity;
        public uint PickId { get; } = pickId;
        public VisualKind Kind { get; set; }
        public List<InstanceRecord> Instances { get; } = new();
        public List<MeshHandle> OwnedMeshes { get; } = new();
        public List<MaterialHandle> OwnedMaterials { get; } = new();
        public HashSet<Guid> AssetIds { get; } = new();
        public InstanceFlags Flags { get; set; }
        public FixtureTypeVisual? FixtureType { get; set; }
        public bool HasFixtureInstances { get; set; }
    }

    private readonly record struct FixtureDraw(int Part, MeshHandle Mesh, Matrix4x4 Local, MaterialHandle[] Materials, string Name, Aabb Bounds);

    private sealed class FixtureTypeVisual(FixtureProfile profile, string? fileName)
    {
        public FixtureProfile Profile { get; } = profile;
        public string? FileName { get; } = fileName;
        public Task<LoadedFixtureModels>? Loading { get; set; }
        public bool Uploaded { get; set; }
        public string? Error { get; set; }
        public FixtureDraw[] Draws { get; set; } = Array.Empty<FixtureDraw>();
        public List<MeshHandle> Meshes { get; } = new();
        public List<MaterialHandle> Materials { get; } = new();

        public void Destroy(IRenderWorld world)
        {
            foreach (var m in Meshes) world.DestroyMesh(m);
            foreach (var m in Materials) world.DestroyMaterial(m);
            Meshes.Clear();
            Materials.Clear();
            Draws = Array.Empty<FixtureDraw>();
        }
    }

    private sealed class ModelVisual
    {
        private ModelVisual(MeshHandle[] meshes, Matrix4x4[] transforms, string[] names, Aabb[] bounds, MaterialHandle[] materials, bool ownsMaterials)
        {
            Bounds = bounds;
            Meshes = meshes;
            Transforms = transforms;
            Names = names;
            Materials = materials;
            OwnsMaterials = ownsMaterials;
        }

        public MeshHandle[] Meshes { get; }
        public Matrix4x4[] Transforms { get; }
        public string[] Names { get; }

        /// <summary>Bounds of each mesh in its own space, m.</summary>
        public Aabb[] Bounds { get; }
        public MaterialHandle[] Materials { get; }
        public bool OwnsMaterials { get; }

        public static ModelVisual Create(IRenderWorld world, ModelData model, IImageSource images, MaterialHandle fallback)
        {
            var meshes = new List<MeshHandle>();
            var transforms = new List<Matrix4x4>();
            var names = new List<string>();
            var bounds = new List<Aabb>();
            foreach (var part in model.Parts)
            {
                if (part.Mesh.Positions.Length == 0 || part.Mesh.Indices.Length == 0) continue;
                meshes.Add(world.CreateMesh(part.Mesh));
                bounds.Add(part.Mesh.ComputeBounds());
                transforms.Add(part.Transform);
                names.Add(string.IsNullOrEmpty(part.Name) ? model.Name : part.Name);
            }
            bool own = model.Materials.Count > 0;
            var materials = own ? model.Materials.Select(m => world.CreateMaterial(m, images)).ToArray() : new[] { fallback };
            return new ModelVisual(meshes.ToArray(), transforms.ToArray(), names.ToArray(), bounds.ToArray(), materials, own);
        }

        public void Destroy(IRenderWorld world)
        {
            foreach (var m in Meshes) world.DestroyMesh(m);
            if (OwnsMaterials)
                foreach (var m in Materials) world.DestroyMaterial(m);
        }
    }
}
