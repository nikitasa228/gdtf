using System.Numerics;
using Candela.App.Editing;
using Candela.Core.Editing;
using Candela.Core.Geometry;
using Candela.Core.Math;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;
using Candela.Photometry;
using SpotLight = Candela.Rendering.SpotLight;
using Candela.Rendering;
using Candela.Rendering.Vulkan;
using SceneModel = Candela.Core.SceneGraph.Scene;

namespace Candela.App.Session;

/// <summary>
/// What the viewports show until the stage engine is part of the build: the renderer's procedural test scene of
/// <c>candela render-test</c> (stage floor, back wall, boxes, spheres, an LED panel, three coloured spot lights and a cable)
/// rebuilt as ordinary scene entities, so selection, gizmos, undo, duplicate, copy/paste and delete work on real
/// <see cref="Entity"/> objects. Each entity carries a <see cref="MeshComponent"/> whose model id names one of three
/// procedural meshes (unit box, unit sphere, the 14 m × 10 m floor) and whose material slot 0 names a preview material;
/// the stage keeps one render instance per such entity, updates it when the entity moves, hides it when the entity is
/// hidden and flags it for the selection outline. The engine's render synchronisation replaces this class.
/// </summary>
internal sealed class PreviewStage : IViewportContent
{
    /// <summary>Model id of the unit box (−0.5 … 0.5 on each axis; size comes from the entity's scale).</summary>
    public static readonly Guid BoxModel = new("5c1a0b0e-0001-4c0d-9a1e-0000000000b0");

    /// <summary>Model id of the unit sphere (radius 1).</summary>
    public static readonly Guid SphereModel = new("5c1a0b0e-0002-4c0d-9a1e-0000000000b1");

    /// <summary>Model id of the 14 m × 10 m stage floor plane (top at the entity origin).</summary>
    public static readonly Guid FloorModel = new("5c1a0b0e-0003-4c0d-9a1e-0000000000b2");

    private readonly IRenderWorld _world;
    private readonly SceneModel _scene;
    private readonly Selection _selection;
    private readonly Dictionary<Guid, Bound> _bound = new();
    private readonly Dictionary<uint, Guid> _byPick = new();
    private readonly Dictionary<Guid, MeshHandle> _meshes = new();
    private readonly Dictionary<Guid, Aabb> _meshBounds = new();
    private readonly Dictionary<string, MaterialHandle> _materials = new(StringComparer.Ordinal);
    private readonly HashSet<Guid> _dirty = new();
    private readonly List<Guid> _scratch = new();
    private uint _nextPickId = 1;
    private bool _allDirty = true;
    private bool _disposed;

    private PreviewStage(IRenderWorld world, SceneModel scene, Selection selection)
    {
        _world = world;
        _scene = scene;
        _selection = selection;
        _scene.Changed += OnSceneChanged;
    }

    /// <inheritdoc/>
    public string Caption => Ui.Loc.Get(Ui.Str.ViewportPreviewNote);

    /// <summary>Lights of the stage (the three spot lights of the render test).</summary>
    public SpotLight[] Lights { get; private set; } = Array.Empty<SpotLight>();

    /// <summary>Number of render instances currently bound to entities.</summary>
    public int InstanceCount => _bound.Count;

    /// <summary>
    /// Creates the stage in <paramref name="world"/> and adds its entities to <paramref name="scene"/> (without undo
    /// history; the scene is new).
    /// </summary>
    public static PreviewStage Create(IRenderWorld world, SceneModel scene, Selection selection)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(selection);
        var stage = new PreviewStage(world, scene, selection);
        stage.CreateResources();
        foreach (var entity in CreateEntities())
            scene.Add(entity);
        stage.Sync();
        return stage;
    }

    /// <summary>The entities of the test scene (also used by tests without a GPU).</summary>
    public static IReadOnlyList<Entity> CreateEntities() => new[]
    {
        Shape("Stage floor", FloorModel, "floor", new Vector3(0f, 1f, 0f), 0f, Vector3.One),
        Shape("Back wall", BoxModel, "wall", new Vector3(0f, 5.1f, 3f), 0f, new Vector3(14f, 0.2f, 6f)),
        Shape("Painted box", BoxModel, "wood", new Vector3(-3.2f, 1.6f, 0.5f), 20f, Vector3.One),
        Shape("Gold sphere", SphereModel, "gold", new Vector3(0.3f, 1.6f, 0.6f), 0f, new Vector3(0.6f)),
        Shape("Plastic sphere", SphereModel, "plastic", new Vector3(3.0f, 1.2f, 0.5f), 0f, new Vector3(0.5f)),
        Shape("Steel column", BoxModel, "steel", new Vector3(1.8f, 3.0f, 1.0f), 0f, new Vector3(0.6f, 0.6f, 2.0f)),
        Shape("Stage box", BoxModel, "black", new Vector3(-1.2f, 3.2f, 0.4f), 0f, new Vector3(0.8f)),
        Shape("LED panel", BoxModel, "led", new Vector3(-0.5f, 4.98f, 3.2f), 0f, new Vector3(2.0f, 0.04f, 1.1f)),
    };

    /// <inheritdoc/>
    public void Sync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_allDirty)
        {
            _allDirty = false;
            _dirty.Clear();
            foreach (var id in _bound.Keys)
                _dirty.Add(id);
            foreach (var e in _scene.All())
                _dirty.Add(e.Id);
        }

        if (_dirty.Count > 0)
        {
            _scratch.Clear();
            _scratch.AddRange(_dirty);
            _dirty.Clear();
            foreach (var id in _scratch)
                SyncEntity(id);
        }

        // Visibility and lock are plain properties without change events, and the selection changes often, so the
        // (cheap) instance flags are re-derived every frame; only instances whose flags changed are updated.
        _scratch.Clear();
        foreach (var (id, bound) in _bound)
            if (FlagsFor(bound.Entity) != bound.Flags)
                _scratch.Add(id);
        foreach (var id in _scratch)
        {
            var bound = _bound[id];
            var flags = FlagsFor(bound.Entity);
            _world.UpdateInstance(bound.Instance, bound.Entity.WorldMatrix, flags);
            _bound[id] = bound with { Flags = flags };
        }
    }

    /// <inheritdoc/>
    public Guid? EntityForPick(uint pickId)
    {
        if (!_byPick.TryGetValue(pickId, out var id))
            return null;
        var entity = _scene.Find(id);
        return entity is null || entity.Locked ? null : id;
    }

    /// <inheritdoc/>
    public bool TryGetBounds(Guid entityId, out Aabb bounds)
    {
        bounds = Aabb.Empty;
        var entity = _scene.Find(entityId);
        if (entity is null)
            return false;
        foreach (var e in entity.SelfAndDescendants())
        {
            if (e.Get<MeshComponent>() is { } mesh && _meshBounds.TryGetValue(mesh.ModelAssetId, out var local))
                bounds.Include(local.Transform(e.WorldMatrix));
        }

        return !bounds.IsEmpty;
    }

    /// <inheritdoc/>
    public Aabb SceneBounds
    {
        get
        {
            var b = Aabb.Empty;
            foreach (var (_, bound) in _bound)
                if (bound.Entity.IsVisibleInHierarchy && _meshBounds.TryGetValue(bound.Model, out var local))
                    b.Include(local.Transform(bound.Entity.WorldMatrix));
            return b;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _scene.Changed -= OnSceneChanged;
        foreach (var bound in _bound.Values)
            _world.DestroyInstance(bound.Instance);
        _bound.Clear();
        _byPick.Clear();
        foreach (var m in _materials.Values)
            _world.DestroyMaterial(m);
        foreach (var m in _meshes.Values)
            _world.DestroyMesh(m);
        _materials.Clear();
        _meshes.Clear();
        _world.SetLights(ReadOnlySpan<SpotLight>.Empty);
        _world.SetLines(ReadOnlySpan<LineVertex>.Empty);
    }

    private static Entity Shape(string name, Guid model, string material, Vector3 position, float yawDegrees, Vector3 scale)
    {
        var entity = new Entity(name)
        {
            Local = new Transform(position, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, yawDegrees * MathF.PI / 180f), scale),
        };
        var mesh = new MeshComponent { ModelAssetId = model, CastShadow = true };
        mesh.MaterialOverrides[0] = "preview:" + material;
        entity.Add(mesh);
        return entity;
    }

    private void CreateResources()
    {
        AddMesh(BoxModel, ProceduralMeshes.Box(Vector3.One));
        AddMesh(SphereModel, ProceduralMeshes.Sphere(1f, 48, 24));
        AddMesh(FloorModel, ProceduralMeshes.Plane(14f, 10f));

        AddMaterial("floor", new Vector3(0.45f, 0.45f, 0.46f), 0f, 0.7f);
        AddMaterial("wall", new Vector3(0.55f, 0.55f, 0.58f), 0f, 0.9f);
        AddMaterial("wood", new Vector3(0.72f, 0.70f, 0.66f), 0f, 0.55f);
        AddMaterial("gold", new Vector3(1.0f, 0.77f, 0.34f), 1f, 0.3f);
        AddMaterial("plastic", new Vector3(0.8f, 0.8f, 0.8f), 0f, 0.25f);
        AddMaterial("steel", new Vector3(0.56f, 0.57f, 0.58f), 1f, 0.45f);
        AddMaterial("black", new Vector3(0.04f, 0.04f, 0.04f), 0f, 0.6f);
        _materials["preview:led"] = _world.CreateMaterial(new MaterialData
        {
            Name = "LED panel",
            BaseColorFactor = new Vector4(0.02f, 0.02f, 0.02f, 1f),
            Roughness = 0.35f,
            EmissiveColor = new Vector3(0.8f, 0.9f, 1.0f),
            EmissiveLuminance = 150f,
        }, null);
        _materials[""] = _world.CreateMaterial(new MaterialData { Name = "preview default", BaseColorFactor = new Vector4(0.6f, 0.6f, 0.6f, 1f) }, null);

        // The same lights and cable as ProceduralTestScene (candela render-test).
        Lights = new[]
        {
            ProceduralTestScene.Spot(new Vector3(-3f, 0.5f, 6.5f), new Vector3(-3f, 1.2f, 0f), LightColor.FromRec709(new Vector3(1f, 0.22f, 0.06f)), 60000f, 18f, 26f),
            ProceduralTestScene.Spot(new Vector3(0.4f, 1.0f, 6.5f), new Vector3(0.3f, 1.8f, 0f), LightColor.FromRec709(new Vector3(0.15f, 1f, 0.2f)), 45000f, 28f, 40f),
            ProceduralTestScene.Spot(new Vector3(3.2f, 0f, 6.5f), new Vector3(3.1f, 1.0f, 0f), LightColor.FromRec709(new Vector3(0.15f, 0.4f, 1f)), 25000f, 40f, 60f),
        };
        _world.SetLights(Lights);
        var cable = new[]
        {
            new Vector3(-6f, -2f, 0.02f), new Vector3(-3f, -2.6f, 0.02f), new Vector3(0f, -2.2f, 0.02f), new Vector3(3f, -2.7f, 0.02f),
            new Vector3(5.5f, -1.5f, 0.02f), new Vector3(6f, 4.9f, 0.02f), new Vector3(6f, 4.9f, 4.5f),
        };
        var lines = new LineVertex[(cable.Length - 1) * 2];
        var color = new Vector4(1f, 0.82f, 0.1f, 1f);
        for (int i = 0; i + 1 < cable.Length; i++)
        {
            lines[2 * i] = new LineVertex { Position = cable[i], Color = color, Width = 3f };
            lines[2 * i + 1] = new LineVertex { Position = cable[i + 1], Color = color, Width = 3f };
        }

        _world.SetLines(lines);
    }

    private void AddMesh(Guid id, MeshData mesh)
    {
        _meshes[id] = _world.CreateMesh(mesh);
        _meshBounds[id] = mesh.ComputeBounds();
    }

    private void AddMaterial(string key, Vector3 baseColor, float metallic, float roughness) =>
        _materials["preview:" + key] = _world.CreateMaterial(new MaterialData
        {
            Name = key,
            BaseColorFactor = new Vector4(baseColor, 1f),
            Metallic = metallic,
            Roughness = roughness,
        }, null);

    private void SyncEntity(Guid id)
    {
        var entity = _scene.Find(id);
        var mesh = entity?.Get<MeshComponent>();
        bool wanted = entity is not null && mesh is not null && _meshes.ContainsKey(mesh.ModelAssetId);
        if (_bound.TryGetValue(id, out var bound))
        {
            if (!wanted || bound.Model != mesh!.ModelAssetId || bound.MaterialKey != MaterialKey(mesh))
            {
                _world.DestroyInstance(bound.Instance);
                _byPick.Remove(bound.PickId);
                _bound.Remove(id);
            }
            else
            {
                var flags = FlagsFor(entity!);
                _world.UpdateInstance(bound.Instance, entity!.WorldMatrix, flags);
                _bound[id] = bound with { Flags = flags };
                foreach (var child in entity.Children)
                    _dirty.Add(child.Id);
                return;
            }
        }

        if (!wanted)
        {
            // Children of a removed or moved entity may still be bound.
            if (entity is not null)
                foreach (var child in entity.Children)
                    if (_bound.ContainsKey(child.Id))
                        SyncEntity(child.Id);
            return;
        }

        string key = MaterialKey(mesh!);
        if (!_materials.TryGetValue(key, out var material))
            material = _materials[""];
        Span<MaterialHandle> slots = stackalloc MaterialHandle[1];
        slots[0] = material;
        uint pickId = _nextPickId++;
        var flagsNew = FlagsFor(entity!);
        var instance = _world.CreateInstance(_meshes[mesh!.ModelAssetId], slots, entity!.WorldMatrix, flagsNew, pickId);
        _bound[id] = new Bound(entity, instance, mesh.ModelAssetId, key, pickId, flagsNew);
        _byPick[pickId] = id;
        foreach (var child in entity.Children)
            _dirty.Add(child.Id);
    }

    private static string MaterialKey(MeshComponent mesh) => mesh.MaterialOverrides.TryGetValue(0, out var m) ? m : "";

    private InstanceFlags FlagsFor(Entity entity)
    {
        var flags = InstanceFlags.None;
        if (entity.Get<MeshComponent>()?.CastShadow == true)
            flags |= InstanceFlags.CastShadow;
        if (!entity.IsVisibleInHierarchy || SceneLookup.LayerOf(_scene, entity.LayerId) is { Visible: false })
            flags |= InstanceFlags.Hidden;
        if (IsSelected(entity))
            flags |= InstanceFlags.Selected;
        return flags;
    }

    private bool IsSelected(Entity entity)
    {
        for (var e = entity; e is not null; e = e.Parent)
            if (_selection.Contains(e.Id))
                return true;
        return false;
    }

    private void OnSceneChanged(SceneChange change)
    {
        if (change.Entity is null)
        {
            _allDirty = true;
            return;
        }

        if (change.Kind == SceneChangeKind.Removed)
        {
            foreach (var e in change.Entity.SelfAndDescendants())
                _dirty.Add(e.Id);
            return;
        }

        if (change.Kind is SceneChangeKind.Added or SceneChangeKind.Reparented or SceneChangeKind.TransformChanged)
        {
            foreach (var e in change.Entity.SelfAndDescendants())
                _dirty.Add(e.Id);
            return;
        }

        _dirty.Add(change.Entity.Id);
    }

    private readonly record struct Bound(Entity Entity, InstanceHandle Instance, Guid Model, string MaterialKey, uint PickId, InstanceFlags Flags);
}
