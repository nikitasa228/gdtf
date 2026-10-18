using System.Numerics;
using Candela.Assets;
using Candela.Core.Diagnostics;
using Candela.Core.Editing;
using Candela.Core.Math;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;
using Candela.Project.Internal;

namespace Candela.Project;

/// <summary>Options of a merge import (File → Import → project).</summary>
public sealed class ProjectMergeOptions
{
    /// <summary>Shared default options.</summary>
    public static ProjectMergeOptions Default { get; } = new();

    /// <summary>Name of the new group entity (default: the imported scene's name, or "Imported project").</summary>
    public string? GroupName { get; init; }

    /// <summary>Entity of the current scene to insert the group under; null inserts it at the scene root.</summary>
    public Guid? ParentId { get; init; }

    /// <summary>
    /// Local transform of the group (metres, relative to the parent). Imported objects keep their positions relative to
    /// the group; world-space points stored in components (fixture focus points, camera targets) are moved with it.
    /// </summary>
    public Transform Placement { get; init; } = Transform.Identity;

    /// <summary>
    /// GDTF files of the current project's fixture library, used to detect name collisions: an imported GDTF whose name
    /// the library already uses for a different file is renamed. Without it only the project's own copies are compared.
    /// </summary>
    public GdtfProvider? TargetGdtfProvider { get; init; }

    /// <summary>
    /// Map imported assets to identical assets already in the project (same content hash, kind and import options)
    /// instead of storing them twice (default true).
    /// </summary>
    public bool ReuseIdenticalAssets { get; init; } = true;

    /// <summary>Put imported entities on layers of the same name when the project has them (default true); otherwise layers are added.</summary>
    public bool MergeLayersByName { get; init; } = true;

    /// <summary>
    /// Give imported fixtures whose console fixture id (<see cref="FixtureComponent.FixtureId"/>) is already used a new
    /// id above the highest one in use (default false: ids are kept as authored and overlaps are left to the patch checks).
    /// </summary>
    public bool RenumberFixtureIds { get; init; }
}

/// <summary>What a merge import added and how identities were translated.</summary>
public sealed class ProjectMergeResult
{
    private readonly List<Issue> _issues;

    internal ProjectMergeResult(Entity group, Dictionary<Guid, Guid> entityIds, Dictionary<Guid, Guid> assetIds,
        Dictionary<string, string> gdtfNames, List<Guid> addedAssets, List<string> addedGdtfs, List<Layer> addedLayers, List<Issue> issues)
    {
        Group = group;
        EntityIds = entityIds;
        AssetIds = assetIds;
        GdtfNames = gdtfNames;
        AddedAssets = addedAssets;
        AddedGdtfs = addedGdtfs;
        AddedLayers = addedLayers;
        _issues = issues;
    }

    /// <summary>The new group entity that holds every imported root entity.</summary>
    public Entity Group { get; }

    /// <summary>Entity id in the imported project → new id in this scene (every imported entity gets a new id).</summary>
    public IReadOnlyDictionary<Guid, Guid> EntityIds { get; }

    /// <summary>Asset id in the imported project → id in this project (a new asset, or an identical existing one).</summary>
    public IReadOnlyDictionary<Guid, Guid> AssetIds { get; }

    /// <summary>GDTF name stored in the imported project → name in this project (differs when renamed to avoid a collision).</summary>
    public IReadOnlyDictionary<string, string> GdtfNames { get; }

    /// <summary>Ids of the assets added to this project's store (identical existing assets are reused, not listed).</summary>
    public IReadOnlyList<Guid> AddedAssets { get; }

    /// <summary>GDTF names added to <see cref="ProjectData.GdtfFiles"/>.</summary>
    public IReadOnlyList<string> AddedGdtfs { get; }

    /// <summary>Layers added to the scene.</summary>
    public IReadOnlyList<Layer> AddedLayers { get; }

    /// <summary>Renamed GDTF files, cleared dangling references, assets that could not be added, and the imported project's load issues.</summary>
    public IReadOnlyList<Issue> Issues => _issues;

    internal void AddIssue(Issue issue) => _issues.Add(issue);
}

/// <summary>
/// Merge import: inserts another project into the open one under a new group entity.
/// <list type="bullet">
/// <item>Every imported entity gets a new id, so a project can be merged into itself (or the same file twice) without
/// collisions. All references are translated: <see cref="FixtureComponent.MountedOn"/>, cable node anchors and endpoints,
/// <see cref="RiggingPointComponent.Carries"/>, asset ids of meshes, images and trusses, asset texture keys used as
/// material ids. References to objects that do not exist in the imported project are cleared and reported.</item>
/// <item>Assets are added with their bytes; identical assets already in the project are reused, colliding ids get new ids.</item>
/// <item>GDTF copies are added; a name that this project (or its fixture library) uses for a different file is renamed
/// ("Name (2).gdtf") and the imported fixtures are pointed to the new name.</item>
/// <item>Layers are matched by id, then by name; missing ones are added. The imported project's settings sections,
/// scene settings, thumbnail and components of unknown types (<see cref="ProjectData.UnknownComponents"/>) are not
/// merged.</item>
/// </list>
/// Merging runs on the main thread (it reads and changes the scene); load the other project first, possibly in the
/// background, with <see cref="ProjectFile.Load(string, ProjectLoadOptions?)"/>.
/// </summary>
public static class ProjectMerge
{
    /// <summary>Merges <paramref name="source"/> into <paramref name="target"/> now (not undoable; see <see cref="CreateCommand"/>).</summary>
    /// <exception cref="ArgumentException"><see cref="ProjectMergeOptions.ParentId"/> is not an entity of the target scene.</exception>
    public static ProjectMergeResult Import(ProjectData target, ProjectData source, ProjectMergeOptions? options = null)
    {
        var command = CreateCommand(target, source, options);
        command.Do();
        return command.Result;
    }

    /// <summary>
    /// Loads <paramref name="path"/> and merges it into <paramref name="target"/>. The loaded project's issues are part of
    /// the result (with entity ids translated).
    /// </summary>
    public static ProjectMergeResult Import(ProjectData target, string path, ProjectMergeOptions? options = null,
        ProjectLoadOptions? loadOptions = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        var source = ProjectFile.Load(path, loadOptions);
        if (options?.GroupName is null)
        {
            var name = string.IsNullOrWhiteSpace(source.Scene.Name) || source.Scene.Name == "Untitled"
                ? Path.GetFileNameWithoutExtension(path)
                : source.Scene.Name;
            options = CopyWithName(options ?? ProjectMergeOptions.Default, name);
        }
        return Import(target, source, options);
    }

    /// <summary>
    /// Prepares a merge as an undoable command (for <see cref="UndoStack.Execute"/>): <c>Do</c> adds the group, assets,
    /// GDTF copies and layers, <c>Undo</c> removes exactly what was added. The imported entities are built now; the
    /// source project is only read and may be discarded afterwards.
    /// </summary>
    public static ProjectImportCommand CreateCommand(ProjectData target, ProjectData source, ProjectMergeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(source);
        return new ProjectImportCommand(target, MergePlanner.Plan(target, source, options ?? ProjectMergeOptions.Default));
    }

    private static ProjectMergeOptions CopyWithName(ProjectMergeOptions o, string name) => new()
    {
        GroupName = name,
        ParentId = o.ParentId,
        Placement = o.Placement,
        TargetGdtfProvider = o.TargetGdtfProvider,
        ReuseIdenticalAssets = o.ReuseIdenticalAssets,
        MergeLayersByName = o.MergeLayersByName,
        RenumberFixtureIds = o.RenumberFixtureIds,
    };

    /// <summary>Everything a merge adds, computed before anything is changed.</summary>
    internal sealed class MergePlan
    {
        public required Entity Group { get; init; }
        public required Guid? ParentId { get; init; }
        public required List<(AssetEntry Entry, Guid NewId)> Assets { get; init; }
        public required List<(string Name, byte[] Data)> Gdtfs { get; init; }
        public required ProjectMergeResult Result { get; init; }
    }

    /// <summary>Builds the detached entity tree with translated references.</summary>
    private static class MergePlanner
    {
        public static MergePlan Plan(ProjectData target, ProjectData source, ProjectMergeOptions options)
        {
            var issues = new List<Issue>();
            var targetScene = target.Scene;
            Entity? parent = null;
            if (options.ParentId is { } pid)
            {
                parent = targetScene.Find(pid) ?? throw new ArgumentException($"Entity {pid} is not in the scene.", nameof(options));
            }

            // Every source entity gets a new id (tree order: parents first).
            var sourceEntities = source.Scene.All().ToList();
            var entityIds = new Dictionary<Guid, Guid>(sourceEntities.Count);
            foreach (var e in sourceEntities) entityIds[e.Id] = Guid.NewGuid();

            var layers = MapLayers(targetScene, source.Scene, options, out var addedLayers);
            var assetIds = MapAssets(target, source, options, out var plannedAssets);
            var gdtfNames = MapGdtfs(target, source, options, entityIds, issues, out var plannedGdtfs);
            var fixtureIds = options.RenumberFixtureIds ? PlanFixtureIds(targetScene, source.Scene) : null;

            // World transform the imported scene's root space ends up in (for world-space points in components).
            var parentWorld = parent?.WorldMatrix ?? Matrix4x4.Identity;
            var groupWorld = options.Placement.ToMatrix() * parentWorld;

            var groupName = string.IsNullOrWhiteSpace(options.GroupName)
                ? (string.IsNullOrWhiteSpace(source.Scene.Name) ? "Imported project" : source.Scene.Name)
                : options.GroupName;
            var groupLayer = parent?.LayerId ?? targetScene.DefaultLayer.Id;
            var group = new Entity(groupName) { LayerId = groupLayer };
            group.Local = options.Placement;
            group.Add(new GroupComponent());

            // Build the tree in a staging scene (Scene.Add is the only way to attach children), then detach it whole.
            var staging = new Scene();
            staging.Add(group);
            var clones = new Dictionary<Guid, Entity>(sourceEntities.Count);
            var remap = new ReferenceRemapper(entityIds, assetIds, target, source.Assets, gdtfNames, fixtureIds, groupWorld, issues);
            foreach (var e in sourceEntities)
            {
                var newId = entityIds[e.Id];
                var newParent = e.Parent is { } p && clones.TryGetValue(p.Id, out var pc) ? pc : group;
                var layer = e.LayerId != Guid.Empty && layers.TryGetValue(e.LayerId, out var l) ? l : newParent.LayerId;
                var clone = new Entity(e.Name, newId) { LayerId = layer, Visible = e.Visible, Locked = e.Locked };
                clone.Local = e.Local;
                foreach (var c in e.Components)
                {
                    var copy = ProjectJson.Clone(c); // Component.Clone would turn a null that differs from the default into the default
                    remap.Apply(c, copy, clone);
                    clone.Add(copy);
                }
                staging.Add(clone, newParent);
                clones[e.Id] = clone;
            }
            staging.Remove(group);

            // The imported project's own load issues, pointing at the new entities. Components of unknown types stay in the
            // imported file only: this version cannot translate the references they may hold, so they are not copied.
            foreach (var issue in source.LoadIssues)
            {
                if (issue.Code == ProjectIssueCodes.SceneComponentUnknown && source.UnknownComponents.Count > 0) continue;
                if (issue.Code == ProjectIssueCodes.AssetKept) continue;
                var related = issue.Entities.Where(entityIds.ContainsKey).Select(id => entityIds[id]).ToArray();
                issues.Add(issue with { Entities = related });
            }
            foreach (var (sourceId, kept) in source.UnknownComponents)
            {
                if (!entityIds.TryGetValue(sourceId, out var newId) || kept.Count == 0) continue;
                var name = source.Scene.Find(sourceId)?.Name ?? sourceId.ToString("D");
                issues.Add(new Issue(IssueSeverity.Warning, IssueCategory.Scene, ProjectIssueCodes.SceneComponentUnknown,
                    $"'{name}' has {kept.Count} component(s) of a type this version of Candela does not know; they were not imported.", newId));
            }
            var notImported = source.KeptAssetIds.Where(id => !target.KeptAssetIds.Contains(id) && !target.Assets.Contains(id)).ToList();
            if (notImported.Count > 0)
            {
                issues.Add(new Issue(IssueSeverity.Warning, IssueCategory.Scene, ProjectIssueCodes.AssetKept,
                    $"{notImported.Count} asset(s) of the imported project that this version of Candela cannot load were not imported; objects using them have no asset.",
                    Array.Empty<Guid>()));
            }

            var result = new ProjectMergeResult(group, entityIds, assetIds, gdtfNames,
                plannedAssets.Select(a => a.NewId).ToList(), plannedGdtfs.Select(g => g.Name).ToList(), addedLayers, issues);
            return new MergePlan
            {
                Group = group,
                ParentId = parent?.Id,
                Assets = plannedAssets,
                Gdtfs = plannedGdtfs,
                Result = result,
            };
        }

        private static Dictionary<Guid, Guid> MapLayers(Scene target, Scene source, ProjectMergeOptions options, out List<Layer> added)
        {
            added = new List<Layer>();
            var map = new Dictionary<Guid, Guid>();
            var byId = new HashSet<Guid>(target.Layers.Select(l => l.Id));
            var byName = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
            foreach (var l in target.Layers) byName.TryAdd(l.Name, l.Id);
            foreach (var layer in source.Layers)
            {
                if (byId.Contains(layer.Id))
                {
                    map[layer.Id] = layer.Id;
                    continue;
                }
                if (options.MergeLayersByName && byName.TryGetValue(layer.Name, out var existing))
                {
                    map[layer.Id] = existing;
                    continue;
                }
                var copy = new Layer { Id = Guid.NewGuid(), Name = layer.Name, Visible = layer.Visible, Locked = layer.Locked };
                added.Add(copy);
                byName.TryAdd(copy.Name, copy.Id);
                map[layer.Id] = copy.Id;
            }
            return map;
        }

        private static Dictionary<Guid, Guid> MapAssets(ProjectData target, ProjectData source, ProjectMergeOptions options,
            out List<(AssetEntry Entry, Guid NewId)> planned)
        {
            planned = new List<(AssetEntry, Guid)>();
            var map = new Dictionary<Guid, Guid>();
            var existing = new Dictionary<(string Hash, AssetKind Kind), List<AssetEntry>>();
            var usedIds = new HashSet<Guid>();
            foreach (var a in target.Assets.Entries)
            {
                usedIds.Add(a.Id);
                if (!existing.TryGetValue((a.ContentHash, a.Kind), out var list)) existing[(a.ContentHash, a.Kind)] = list = new List<AssetEntry>();
                list.Add(a);
            }
            foreach (var asset in source.Assets.Entries)
            {
                if (options.ReuseIdenticalAssets && existing.TryGetValue((asset.ContentHash, asset.Kind), out var candidates) &&
                    candidates.Find(c => Equals(c.ImportOptions, asset.ImportOptions)) is { } same)
                {
                    map[asset.Id] = same.Id;
                    continue;
                }
                var id = usedIds.Contains(asset.Id) ? Guid.NewGuid() : asset.Id;
                usedIds.Add(id);
                map[asset.Id] = id;
                planned.Add((asset, id));
            }
            return map;
        }

        private static Dictionary<string, string> MapGdtfs(ProjectData target, ProjectData source, ProjectMergeOptions options,
            Dictionary<Guid, Guid> entityIds, List<Issue> issues, out List<(string Name, byte[] Data)> planned)
        {
            planned = new List<(string, byte[])>();
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            var taken = new HashSet<string>(target.GdtfFiles.Keys, StringComparer.OrdinalIgnoreCase);
            var users = UsersOfStoredGdtfs(source);
            foreach (var (name, data) in source.GdtfFiles)
            {
                if (target.FindGdtf(name) is { } own)
                {
                    if (SameBytes(own, data))
                    {
                        map[name] = ExactKey(target, name);
                        continue;
                    }
                }
                else if (!taken.Contains(name) && LibraryAgrees(options.TargetGdtfProvider, name, data))
                {
                    map[name] = name;
                    taken.Add(name);
                    planned.Add((name, data));
                    continue;
                }

                var renamed = UniqueName(name, taken, options.TargetGdtfProvider);
                taken.Add(renamed);
                map[name] = renamed;
                planned.Add((renamed, data));
                issues.Add(new Issue(IssueSeverity.Note, IssueCategory.Fixture, ProjectIssueCodes.MergeGdtfRenamed,
                    $"The imported fixture file '{name}' differs from the file of the same name in this project; it was added as '{renamed}'.",
                    users.TryGetValue(name, out var related) ? related.Select(id => entityIds[id]).ToArray() : Array.Empty<Guid>()));
            }
            return map;
        }

        /// <summary>
        /// Stored GDTF name → the source entities whose fixtures use that copy: the exact name, or a name that differs only
        /// in case and that <see cref="ProjectData.FindGdtf"/> binds to the copy (the first such copy in storage order).
        /// </summary>
        private static Dictionary<string, List<Guid>> UsersOfStoredGdtfs(ProjectData source)
        {
            var usage = ProjectData.CollectGdtfSpecs(source.Scene);
            var result = new Dictionary<string, List<Guid>>(StringComparer.Ordinal);
            Dictionary<string, string>? ignoringCase = null;
            foreach (var spec in usage.Keys)
            {
                string? stored = source.GdtfFiles.ContainsKey(spec) ? spec : null;
                if (stored is null)
                {
                    if (ignoringCase is null)
                    {
                        ignoringCase = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var key in source.GdtfFiles.Keys) ignoringCase.TryAdd(key, key);
                    }
                    if (!ignoringCase.TryGetValue(spec, out stored)) continue;
                }
                if (!result.TryGetValue(stored, out var list)) result[stored] = list = new List<Guid>();
                list.AddRange(usage.EntitiesOf(spec));
            }
            return result;
        }

        /// <summary>True when the library has no file of that name, or exactly the same file.</summary>
        private static bool LibraryAgrees(GdtfProvider? library, string name, byte[] data)
        {
            if (library is null) return true;
            var bytes = library(name);
            return bytes is null || SameBytes(bytes, data);
        }

        private static string UniqueName(string name, HashSet<string> taken, GdtfProvider? library)
        {
            var ext = Path.GetExtension(name);
            if (!string.Equals(ext, ".gdtf", StringComparison.OrdinalIgnoreCase)) ext = "";
            var stem = name[..^ext.Length];
            for (int n = 2; ; n++)
            {
                var candidate = $"{stem} ({n}){ext}";
                if (taken.Contains(candidate)) continue;
                if (library?.Invoke(candidate) is not null) continue;
                return candidate;
            }
        }

        private static string ExactKey(ProjectData target, string name)
        {
            if (target.GdtfFiles.ContainsKey(name)) return name;
            foreach (var key in target.GdtfFiles.Keys)
                if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase)) return key;
            return name;
        }

        private static bool SameBytes(byte[] a, byte[] b) => ReferenceEquals(a, b) || a.AsSpan().SequenceEqual(b);

        /// <summary>Source fixture component → new console fixture id, for ids already used in the target.</summary>
        private static Dictionary<FixtureComponent, int> PlanFixtureIds(Scene target, Scene source)
        {
            var used = new HashSet<int>();
            int max = 0;
            foreach (var (_, f) in target.Query<FixtureComponent>())
            {
                if (f.FixtureId <= 0) continue;
                used.Add(f.FixtureId);
                max = System.Math.Max(max, f.FixtureId);
            }
            var map = new Dictionary<FixtureComponent, int>(ReferenceEqualityComparer.Instance);
            foreach (var (_, f) in source.Query<FixtureComponent>())
            {
                if (f.FixtureId <= 0) continue;
                if (used.Add(f.FixtureId))
                {
                    max = System.Math.Max(max, f.FixtureId);
                    continue;
                }
                map[f] = ++max;
                used.Add(max);
            }
            return map;
        }
    }

    /// <summary>Translates the references of one cloned component.</summary>
    private sealed class ReferenceRemapper(
        Dictionary<Guid, Guid> entityIds,
        Dictionary<Guid, Guid> assetIds,
        ProjectData target,
        AssetStore sourceAssets,
        Dictionary<string, string> gdtfNames,
        Dictionary<FixtureComponent, int>? fixtureIds,
        Matrix4x4 groupWorld,
        List<Issue> issues)
    {
        private readonly bool _moveWorldPoints = !groupWorld.IsIdentity;

        /// <summary>Translates the references of <paramref name="copy"/>, a clone of <paramref name="original"/> owned by <paramref name="owner"/>.</summary>
        public void Apply(Component original, Component copy, Entity owner)
        {
            switch (copy)
            {
                case FixtureComponent f:
                    f.MountedOn = Entity(f.MountedOn, owner, "the object it is mounted on");
                    if (GdtfName(f.GdtfSpec) is { } gdtf) f.GdtfSpec = gdtf;
                    if (f.FocusPoint is { } focus && _moveWorldPoints) f.FocusPoint = Vector3.Transform(focus, groupWorld);
                    if (fixtureIds is not null && original is FixtureComponent source && fixtureIds.TryGetValue(source, out var fixtureId))
                        f.FixtureId = fixtureId;
                    break;
                case CableComponent cable:
                    RemapCable(cable, owner);
                    break;
                case RiggingPointComponent rig:
                    var carries = new List<Guid>(rig.Carries.Count);
                    foreach (var id in rig.Carries)
                        if (Entity(id, owner, "a structure it carries") is { } mapped) carries.Add(mapped);
                    rig.Carries = carries;
                    break;
                case MeshComponent mesh:
                    mesh.ModelAssetId = Asset(mesh.ModelAssetId, owner, "model") ?? Guid.Empty;
                    foreach (var slot in mesh.MaterialOverrides.Keys.ToArray())
                        mesh.MaterialOverrides[slot] = AssetKey(mesh.MaterialOverrides[slot], owner);
                    break;
                case ImageComponent image:
                    image.ImageAssetId = Asset(image.ImageAssetId, owner, "image") ?? Guid.Empty;
                    break;
                case TrussComponent truss:
                    truss.ModelAssetId = truss.ModelAssetId is { } tm ? Asset(tm, owner, "model") : null;
                    break;
                case WallComponent wall:
                    wall.MaterialId = AssetKey(wall.MaterialId, owner);
                    break;
                case CameraComponent camera:
                    if (camera.Target is { } target && _moveWorldPoints) camera.Target = Vector3.Transform(target, groupWorld);
                    break;
            }
        }

        /// <summary>
        /// The new name of the stored GDTF a fixture of the imported project used, or null to keep its name. A fixture
        /// whose name differs from the stored copy's only in case (bound to it by <see cref="ProjectData.FindGdtf"/>) is
        /// re-pointed too when that copy was renamed; otherwise it would bind to this project's different file of that name.
        /// </summary>
        private string? GdtfName(string spec)
        {
            if (string.IsNullOrEmpty(spec)) return null;
            if (gdtfNames.TryGetValue(spec, out var exact)) return exact;
            _gdtfNamesIgnoringCase ??= BuildCaseInsensitive(gdtfNames);
            return _gdtfNamesIgnoringCase.TryGetValue(spec, out var stored) && !string.Equals(stored.Stored, stored.Mapped, StringComparison.Ordinal)
                ? stored.Mapped
                : null;
        }

        private Dictionary<string, (string Stored, string Mapped)>? _gdtfNamesIgnoringCase;

        private static Dictionary<string, (string Stored, string Mapped)> BuildCaseInsensitive(Dictionary<string, string> names)
        {
            // First wins, in the stored copies' order: the copy ProjectData.FindGdtf binds a case-variant name to.
            var map = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
            foreach (var (stored, mapped) in names) map.TryAdd(stored, (stored, mapped));
            return map;
        }

        private void RemapCable(CableComponent cable, Entity owner)
        {
            // Node ids get new ids too, so cables of a project merged into itself never share node ids.
            var nodeIds = new Dictionary<Guid, Guid>(cable.Nodes.Count);
            foreach (var node in cable.Nodes)
            {
                var newId = Guid.NewGuid();
                nodeIds.TryAdd(node.Id, newId);
                node.Id = newId;
                if (node.Anchor is { } anchor)
                {
                    if (Entity(anchor.EntityId, owner, "the object a cable node is anchored to") is { } mapped)
                        node.Anchor = anchor with { EntityId = mapped };
                    else
                        node.Anchor = null;
                }
            }
            var segments = new List<CableSegment>(cable.Segments.Count);
            foreach (var s in cable.Segments)
            {
                if (nodeIds.TryGetValue(s.FromNode, out var from) && nodeIds.TryGetValue(s.ToNode, out var to))
                    segments.Add(new CableSegment(from, to));
            }
            cable.Segments = segments;
            var endpoints = new List<CableEndpoint>(cable.Endpoints.Count);
            foreach (var ep in cable.Endpoints)
            {
                if (!nodeIds.TryGetValue(ep.NodeId, out var node)) continue;
                if (Entity(ep.EntityId, owner, $"the device of cable end '{ep.PortName}'") is not { } device) continue;
                endpoints.Add(new CableEndpoint { NodeId = node, EntityId = device, PortName = ep.PortName });
            }
            cable.Endpoints = endpoints;
        }

        private Guid? Entity(Guid? id, Entity owner, string what)
        {
            if (id is not { } value || value == Guid.Empty) return id;
            if (entityIds.TryGetValue(value, out var mapped)) return mapped;
            Dangling(owner, $"'{owner.Name}' referred to {what} ({value:D}), which does not exist in the imported project; the reference was removed.");
            return null;
        }

        private Guid? Asset(Guid id, Entity owner, string what)
        {
            if (id == Guid.Empty) return id;
            if (assetIds.TryGetValue(id, out var mapped)) return mapped;
            // Not in the imported project. Keep it when this project has that asset (the file was a copy of this project),
            // loaded or kept verbatim.
            if (!sourceAssets.Contains(id) && (target.Assets.Contains(id) || target.KeptAssetIds.Contains(id))) return id;
            Dangling(owner, $"'{owner.Name}' referred to a {what} asset ({id:D}) that is not in the imported project; the reference was removed.");
            return null;
        }

        private string AssetKey(string key, Entity owner)
        {
            if (!AssetKeys.TryParseAssetKey(key, out var id, out var relative)) return key;
            if (Asset(id, owner, "texture") is not { } mapped || mapped == Guid.Empty) return MaterialIds.Default;
            return relative is null ? AssetKeys.ForAsset(mapped) : AssetKeys.ForFile(mapped, relative);
        }

        private void Dangling(Entity owner, string message) =>
            issues.Add(new Issue(IssueSeverity.Warning, IssueCategory.Scene, ProjectIssueCodes.MergeReferenceDangling, message, owner.Id));
    }
}

/// <summary>
/// An undoable merge import (<see cref="ProjectMerge.CreateCommand"/>). <see cref="Do"/> adds the prepared group, assets,
/// GDTF copies and layers; <see cref="Undo"/> removes them again. Runs on the main thread.
/// </summary>
public sealed class ProjectImportCommand : IUndoableCommand
{
    private readonly ProjectData _target;
    private readonly ProjectMerge.MergePlan _plan;
    private readonly List<Guid> _assetsAdded = new();
    private readonly List<(string Name, byte[] Data)> _gdtfsAdded = new();
    private bool _done;
    private bool _firstRun = true;

    internal ProjectImportCommand(ProjectData target, ProjectMerge.MergePlan plan)
    {
        _target = target;
        _plan = plan;
    }

    /// <inheritdoc />
    public string Description => $"Import {_plan.Group.Name}";

    /// <summary>What the import adds (valid before the first <see cref="Do"/>; issues of adding assets appear after it).</summary>
    public ProjectMergeResult Result => _plan.Result;

    /// <inheritdoc />
    public void Do()
    {
        if (_done) return;
        var scene = _target.Scene;
        var parent = _plan.ParentId is { } pid ? scene.Find(pid) : null;

        if (_plan.Result.AddedLayers.Count > 0)
        {
            foreach (var layer in _plan.Result.AddedLayers) scene.Layers.Add(layer);
            scene.NotifySettingsChanged();
        }
        _assetsAdded.Clear();
        foreach (var (entry, id) in _plan.Assets)
        {
            if (_target.Assets.Contains(id)) continue;
            try
            {
                _target.Assets.Add(entry.Kind, entry.OriginalFileName, entry.Data, entry.CompanionFiles, entry.SourcePath, id, entry.ImportOptions);
                _assetsAdded.Add(id);
            }
            catch (Exception e) when (e is AssetImportException or InvalidOperationException)
            {
                if (_firstRun)
                {
                    _plan.Result.AddIssue(new Issue(IssueSeverity.Error, IssueCategory.Scene, ProjectIssueCodes.MergeAssetFailed,
                        $"The asset '{entry.OriginalFileName}' of the imported project could not be added: {e.Message}"));
                }
            }
        }
        _gdtfsAdded.Clear();
        foreach (var (name, data) in _plan.Gdtfs)
        {
            if (_target.GdtfFiles.ContainsKey(name)) continue;
            _target.GdtfFiles[name] = data;
            _gdtfsAdded.Add((name, data));
        }
        scene.Add(_plan.Group, parent);
        _done = true;
        _firstRun = false;
    }

    /// <inheritdoc />
    public void Undo()
    {
        if (!_done) return;
        var scene = _target.Scene;
        if (_plan.Group.Scene == scene) scene.Remove(_plan.Group);
        foreach (var (name, added) in _gdtfsAdded)
        {
            // Only remove the copy this import added (not one put there under the same name since).
            if (_target.GdtfFiles.TryGetValue(name, out var data) && ReferenceEquals(data, added)) _target.GdtfFiles.Remove(name);
        }
        foreach (var id in _assetsAdded) _target.Assets.Remove(id);
        if (_plan.Result.AddedLayers.Count > 0)
        {
            foreach (var layer in _plan.Result.AddedLayers) scene.Layers.Remove(layer);
            scene.NotifySettingsChanged();
        }
        _done = false;
    }
}
