using System.Globalization;
using System.Numerics;
using Candela.Core.Diagnostics;
using Candela.Core.Math;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;
using Candela.Gdtf;

namespace Candela.Mvr;

/// <summary>
/// Turns an MVR file into Core scene content: layers → <see cref="Layer"/>s, every object → an <see cref="Entity"/>
/// (keeping the MVR uuid and hierarchy), fixtures → <see cref="FixtureComponent"/> with the GDTF resolved (embedded,
/// or from the fixture library when the embedded file is a stub or missing), trusses → <see cref="TrussComponent"/>,
/// supports → <see cref="RiggingPointComponent"/>, groups → <see cref="GroupComponent"/>, scene objects, video screens
/// and projectors → <see cref="MeshComponent"/>s referencing imported model assets, symbol instances expanded and
/// 1.6 connections → <see cref="CableComponent"/>s.
/// </summary>
/// <remarks>
/// <para>
/// Content problems never throw: they become <see cref="MvrImportResult.Issues"/>. Only an unreadable archive throws
/// (<see cref="MvrFormatException"/> / <see cref="IOException"/>). See docs/mvr.md for the coordinate handling (MVR
/// millimetres → metres, mirror matrices kept as a negative X scale) and the stub-GDTF resolution.
/// </para>
/// <para>
/// Threading: <see cref="Import(MvrDocument, MvrImportOptions?)"/> builds a new scene that nobody else sees yet, so it
/// may run on a background thread; <see cref="Merge(Scene, MvrDocument, MvrImportOptions?)"/> mutates the target scene
/// and must run on the thread that owns it (read the document in the background first with <see cref="MvrReader"/>).
/// </para>
/// </remarks>
public static class MvrImporter
{
    /// <summary>Reads an <c>.mvr</c> file and imports it as a new scene.</summary>
    /// <exception cref="MvrFormatException">The file is not an MVR archive.</exception>
    public static MvrImportResult ImportFile(string path, MvrImportOptions? options = null) =>
        Import(MvrReader.ReadFile(path), options);

    /// <summary>Reads an MVR archive from a stream and imports it as a new scene.</summary>
    /// <exception cref="MvrFormatException">The data is not an MVR archive.</exception>
    public static MvrImportResult Import(Stream mvr, string? fileName = null, MvrImportOptions? options = null) =>
        Import(MvrReader.Read(mvr, fileName), options);

    /// <summary>Imports a document as a new scene whose layers are the MVR layers.</summary>
    public static MvrImportResult Import(MvrDocument document, MvrImportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        return new ImportSession(document, options ?? new MvrImportOptions(), null).Run();
    }

    /// <summary>Reads an <c>.mvr</c> file and merges it into <paramref name="target"/> under a new group entity.</summary>
    /// <exception cref="MvrFormatException">The file is not an MVR archive.</exception>
    public static MvrImportResult MergeFile(Scene target, string path, MvrImportOptions? options = null) =>
        Merge(target, MvrReader.ReadFile(path), options);

    /// <summary>Reads an MVR archive from a stream and merges it into <paramref name="target"/> under a new group entity.</summary>
    /// <exception cref="MvrFormatException">The data is not an MVR archive.</exception>
    public static MvrImportResult Merge(Scene target, Stream mvr, string? fileName = null, MvrImportOptions? options = null) =>
        Merge(target, MvrReader.Read(mvr, fileName), options);

    /// <summary>
    /// Merges a document into an existing scene: all objects go under one new root entity with a
    /// <see cref="GroupComponent"/>; MVR layers are matched to the scene's layers by uuid, then by name, else added.
    /// Object uuids that already exist in the scene get new ids (reported).
    /// </summary>
    public static MvrImportResult Merge(Scene target, MvrDocument document, MvrImportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(document);
        return new ImportSession(document, options ?? new MvrImportOptions(), target).Run();
    }

    private sealed class ImportSession
    {
        private const int MaxSymbolDepth = 32;

        /// <summary>
        /// Geometry instances that symbol expansion may create in one import. Nested symbols multiply (a symdef with ten
        /// symbols of a symdef with ten symbols …), so a broken or malicious file could otherwise ask for billions of
        /// meshes; expansion stops at this budget and the cut is reported.
        /// </summary>
        internal const int MaxExpandedGeometries = 250_000;

        private readonly MvrDocument _doc;
        private readonly MvrImportOptions _options;
        private readonly Scene _scene;
        private readonly bool _merge;
        private readonly List<Issue> _issues = new();
        private readonly IssueSink _sink;
        private readonly Entity? _group;
        private readonly Guid _idNamespace;

        private readonly Dictionary<MvrObject, Guid> _ids = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<Guid, Entity> _byUuid = new();
        private readonly Dictionary<Guid, MvrSymdef> _symdefs = new();
        private readonly Dictionary<Guid, Vector3> _focusWorld = new();
        private readonly Dictionary<string, List<Guid>> _specUsers = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _patchedSpecs = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<(string Spec, string Mode), List<int>> _modeAddresses = new(SpecModeComparer.Instance);
        private readonly Dictionary<(string Spec, string Mode), int> _modeUsers = new(SpecModeComparer.Instance);
        private readonly Dictionary<(string Spec, string Mode), MvrModeResolution> _modes = new(SpecModeComparer.Instance);
        private readonly Dictionary<string, MvrGdtfReference> _gdtfs = new(StringComparer.OrdinalIgnoreCase);
        private readonly GdtfResolver _resolver;

        private readonly Dictionary<string, ImportedAsset?> _assetByFile = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<ImportedAsset> _assets = new();
        private readonly Dictionary<Guid, Aabb> _assetBounds = new();

        private readonly List<Entity> _roots = new();
        private readonly List<Layer> _layers = new();
        private readonly Dictionary<Guid, Layer> _layerMap = new();
        private readonly List<(MvrDeviceObject Object, Entity Entity)> _connectionOwners = new();
        private int _geometryBudget = MaxExpandedGeometries;
        private bool _geometryBudgetReported;

        public ImportSession(MvrDocument doc, MvrImportOptions options, Scene? target)
        {
            _doc = doc;
            _options = options;
            _sink = new IssueSink(_issues);
            _resolver = new GdtfResolver(doc, options.Library, _sink);
            string baseName = doc.SourceFileName is { Length: > 0 } src ? Path.GetFileNameWithoutExtension(src) : "MVR import";
            if (target is null)
            {
                _scene = new Scene(options.SceneName ?? baseName);
                _idNamespace = DeterministicGuid.MvrNamespace;
            }
            else
            {
                _scene = target;
                _merge = true;
                if (target.Layers.Count == 0) target.Layers.Add(new Layer { Name = "Default" }); // DefaultLayer needs one
                _group = new Entity(options.GroupName ?? baseName) { LayerId = target.DefaultLayer.Id };
                _group.Add(new GroupComponent());
                _idNamespace = _group.Id;
            }
        }

        public MvrImportResult Run()
        {
            foreach (var s in _doc.AuxData.Symdefs) _symdefs.TryAdd(s.Uuid, s);
            Prepass();
            ResolveGdtfs();
            SetUpLayers();
            if (_group is not null)
            {
                _scene.Add(_group);
                _roots.Add(_group);
            }
            foreach (var layer in _doc.Layers)
            {
                var layerId = _layerMap[layer.Uuid].Id;
                bool layerTransform = layer.HasMatrix && !MvrMatrix.IsIdentity(layer.Matrix);
                foreach (var obj in layer.Objects)
                    Build(obj, _group, null, layerId, layerTransform ? obj.Matrix * layer.Matrix : obj.Matrix);
            }
            CreateCables();
            ReportUnrepresentable();
            _sink.Complete();

            var issues = new List<Issue>(_doc.Issues.Count + _issues.Count);
            issues.AddRange(_doc.Issues);
            issues.AddRange(_issues);
            return new MvrImportResult(_scene, _group, _roots, _assets, _resolver.References, issues, _doc, _byUuid, _layers);
        }

        // ------------------------------------------------------------ pass 1: ids, focus points, GDTF users

        private void Prepass()
        {
            var used = new HashSet<Guid>();
            int remapped = 0;
            foreach (var layer in _doc.Layers)
            {
                foreach (var root in layer.Objects)
                    Visit(root, layer.HasMatrix ? layer.Matrix : Matrix4x4.Identity);
            }

            void Visit(MvrObject obj, Matrix4x4 parentWorld)
            {
                var world = obj.Matrix * parentWorld;
                if (obj is MvrFocusPoint)
                {
                    _focusWorld.TryAdd(obj.Uuid, world.Translation);
                }
                else
                {
                    Guid id = obj.Uuid;
                    if (id == Guid.Empty || !used.Add(id) || _scene.Find(id) is not null)
                    {
                        int n = 0;
                        do id = DeterministicGuid.Create(_idNamespace, obj.Uuid.ToString("D"), n++);
                        while (!used.Add(id) || _scene.Find(id) is not null);
                        remapped++;
                    }
                    _ids[obj] = id;
                    if (obj is MvrDeviceObject d && !string.IsNullOrWhiteSpace(d.GdtfSpec))
                    {
                        string spec = d.GdtfSpec.Trim();
                        if (!_specUsers.TryGetValue(spec, out var users)) _specUsers[spec] = users = new List<Guid>();
                        users.Add(id);
                        if (d.Addresses.Count > 0) _patchedSpecs.Add(spec);
                        var key = (spec, (d.GdtfMode ?? "").Trim());
                        _modeUsers[key] = _modeUsers.GetValueOrDefault(key) + 1;
                        if (!_modeAddresses.TryGetValue(key, out var list)) _modeAddresses[key] = list = new List<int>();
                        foreach (var a in d.Addresses)
                            if (a.Break == 0) list.Add(a.Absolute);
                    }
                }
                foreach (var c in obj.ChildObjects) Visit(c, world);
            }

            if (remapped > 0)
            {
                _sink.Add(IssueSeverity.Note, IssueCategory.Scene, MvrIssueCodes.IdRemapped,
                    _merge
                        ? $"{remapped} object uuid(s) already exist in the scene (or repeat in the file); those objects got new ids."
                        : $"{remapped} object uuid(s) repeat in the file; those objects got new ids.");
            }
        }

        private void ResolveGdtfs()
        {
            foreach (var (spec, users) in _specUsers)
            {
                var r = _resolver.Resolve(spec, users, _patchedSpecs.Contains(spec));
                r.ObjectCount = users.Count;
                _gdtfs[spec] = r;
            }

            var modesBySpec = new Dictionary<string, List<MvrModeResolution>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, count) in _modeUsers)
            {
                var r = _gdtfs[key.Spec];
                int? inferred = ModeMatcher.InferFootprint(_modeAddresses.GetValueOrDefault(key) ?? new List<int>());
                string? requested = key.Mode.Length == 0 ? null : key.Mode;
                MvrModeResolution resolution;
                if (r.FixtureType is null)
                {
                    resolution = new MvrModeResolution(requested, requested, null, inferred, false, count);
                }
                else if (r.MatchKind == FixtureMatchKind.Substitute &&
                         DmxPatchSpacing.Infer(_modeAddresses.GetValueOrDefault(key) ?? new List<int>()) is { } substituteSpacing)
                {
                    resolution = ResolveSubstituteMode(key.Spec, r, requested, substituteSpacing, inferred, count);
                }
                else
                {
                    var (mode, exact) = ModeMatcher.Match(r.FixtureType, requested, inferred);
                    resolution = new MvrModeResolution(requested, mode?.Name ?? requested,
                        mode?.FirstBreakFootprint, inferred, exact, count);
                    if (!exact && mode is not null)
                    {
                        var users = _specUsers[key.Spec].Take(64).ToArray();
                        string inferredText = inferred is { } i ? string.Create(CultureInfo.InvariantCulture, $"; the patch suggests {i} channels") : "";
                        _sink.Add(requested is null ? IssueSeverity.Note : IssueSeverity.Warning, IssueCategory.Fixture, MvrIssueCodes.ModeUnknown,
                            requested is null
                                ? $"'{key.Spec}' has fixtures without a DMX mode; '{mode.Name}' ({resolution.Footprint} channels) of '{r.FileName}' is used{inferredText}."
                                : $"'{r.FileName}' has no DMX mode '{requested}' (asked for by '{key.Spec}'); the closest mode '{mode.Name}' ({resolution.Footprint} channels) is used{inferredText}.",
                            users);
                    }
                    if (mode is not null && inferred is { } spacing && resolution.Footprint is { } footprint && footprint > spacing &&
                        (!exact || r.MatchKind == FixtureMatchKind.Substitute))
                    {
                        _sink.Add(IssueSeverity.Warning, IssueCategory.Patch, MvrIssueCodes.ModeFootprint,
                            string.Create(CultureInfo.InvariantCulture,
                                $"'{key.Spec}' fixtures are patched {spacing} channels apart, but mode '{mode.Name}' of the {(exact ? "substitute" : "closest")} '{r.FileName}' uses {footprint} channels; their DMX ranges overlap until the real GDTF is added or the patch is changed."),
                            _specUsers[key.Spec].Take(64).ToArray());
                    }
                }
                _modes[key] = resolution;
                if (!modesBySpec.TryGetValue(key.Spec, out var list)) modesBySpec[key.Spec] = list = new List<MvrModeResolution>();
                list.Add(resolution);
            }
            foreach (var (spec, list) in modesBySpec) _gdtfs[spec].Modes = list;
        }

        /// <summary>
        /// The mode of a substitute GDTF (a similar model standing in for one the library lacks) for fixtures patched
        /// <paramref name="spacing"/> channels apart: the requested mode name belongs to the missing model, so the
        /// stand-in's mode of that name is kept only when it fits the spacing; otherwise its most complete mode that fits
        /// (<see cref="FixtureType.FitModeToSpacing(DmxMode?, int?)"/>, the rule the DMX engine uses too), so that the stand-in does not overlap its neighbours in the patch.
        /// </summary>
        private MvrModeResolution ResolveSubstituteMode(string spec, MvrGdtfReference r, string? requested, int spacing, int? inferred, int count)
        {
            var type = r.FixtureType!;
            var named = ModeMatcher.MatchName(type, requested);
            var mode = type.FitModeToSpacing(named, spacing);
            if (mode is null) return new MvrModeResolution(requested, requested, null, inferred, false, count);
            int footprint = mode.FirstBreakFootprint;
            bool exact = named is not null && ReferenceEquals(mode, named);
            var users = _specUsers[spec].Take(64).ToArray();
            string fit = footprint <= spacing
                ? string.Create(CultureInfo.InvariantCulture, $"the most complete of its modes that fits the {spacing}-channel patch spacing")
                : string.Create(CultureInfo.InvariantCulture, $"its smallest mode, as none fits the {spacing}-channel patch spacing");
            if (named is not null && !exact)
            {
                _sink.Add(IssueSeverity.Note, IssueCategory.Patch, MvrIssueCodes.SubstituteMode,
                    string.Create(CultureInfo.InvariantCulture,
                        $"'{spec}' fixtures are patched {spacing} channels apart; the stand-in '{r.FileName}' uses its mode '{mode.Name}' ({footprint} channels, {fit}) instead of its mode of the requested name, '{named.Name}' ({named.FirstBreakFootprint} channels)."),
                    users);
            }
            else if (named is null)
            {
                string message = requested is null
                    ? string.Create(CultureInfo.InvariantCulture,
                        $"'{spec}' has fixtures without a DMX mode; '{mode.Name}' ({footprint} channels, {fit}) of the stand-in '{r.FileName}' is used.")
                    : string.Create(CultureInfo.InvariantCulture,
                        $"'{r.FileName}' has no DMX mode '{requested}' (asked for by '{spec}'); its mode '{mode.Name}' ({footprint} channels, {fit}) is used.");
                _sink.Add(requested is null ? IssueSeverity.Note : IssueSeverity.Warning, IssueCategory.Fixture, MvrIssueCodes.ModeUnknown, message, users);
            }
            if (footprint > spacing)
            {
                _sink.Add(IssueSeverity.Warning, IssueCategory.Patch, MvrIssueCodes.ModeFootprint,
                    string.Create(CultureInfo.InvariantCulture,
                        $"'{spec}' fixtures are patched {spacing} channels apart, but mode '{mode.Name}' of the substitute '{r.FileName}' uses {footprint} channels; their DMX ranges overlap until the real GDTF is added or the patch is changed."),
                    users);
            }
            return new MvrModeResolution(requested, mode.Name, footprint, inferred, exact, count);
        }

        // ------------------------------------------------------------ layers

        private void SetUpLayers()
        {
            if (!_merge)
            {
                _scene.Layers.Clear();
                foreach (var l in _doc.Layers)
                {
                    if (_layerMap.ContainsKey(l.Uuid)) continue;
                    var layer = new Layer { Id = l.Uuid == Guid.Empty ? Guid.NewGuid() : l.Uuid, Name = l.Name.Length > 0 ? l.Name : "Layer" };
                    _scene.Layers.Add(layer);
                    _layerMap[l.Uuid] = layer;
                    _layers.Add(layer);
                }
                if (_scene.Layers.Count == 0) _scene.Layers.Add(new Layer { Name = "Default" });
                return;
            }

            bool added = false;
            foreach (var l in _doc.Layers)
            {
                if (_layerMap.ContainsKey(l.Uuid)) continue;
                var layer = _scene.FindLayer(l.Uuid)
                    ?? _scene.Layers.FirstOrDefault(x => string.Equals(x.Name.Trim(), l.Name.Trim(), StringComparison.OrdinalIgnoreCase));
                if (layer is null)
                {
                    layer = new Layer { Id = l.Uuid == Guid.Empty ? Guid.NewGuid() : l.Uuid, Name = l.Name.Length > 0 ? l.Name : "Layer" };
                    _scene.Layers.Add(layer);
                    added = true;
                }
                _layerMap[l.Uuid] = layer;
                if (!_layers.Contains(layer)) _layers.Add(layer);
            }
            if (added) _scene.NotifySettingsChanged();
        }

        // ------------------------------------------------------------ pass 2: entities

        private void Build(MvrObject obj, Entity? parentEntity, MvrObject? parentObject, Guid layerId, Matrix4x4 local)
        {
            if (obj is MvrFocusPoint fp)
            {
                if (fp.Geometries.Count > 0)
                {
                    _sink.Aggregate(IssueSeverity.Note, IssueCategory.Scene, MvrIssueCodes.NotRepresentable, "FocusPoint/Geometries",
                        n => $"Geometry of {n} focus point(s) is not imported (focus points become fixture focus positions).");
                }
                return;
            }

            Entity entity;
            List<(Guid Asset, Matrix4x4 Local, string Name)> geometryChildren;
            try
            {
                (entity, geometryChildren) = CreateEntity(obj, parentEntity, parentObject, layerId, local);
                _scene.Add(entity, parentEntity);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // One broken object must not abort the import of the rest of the scene: report it and import its
                // children under its parent, composed with its matrix so they keep their place in the world.
                _sink.Add(IssueSeverity.Error, IssueCategory.Program, MvrIssueCodes.ObjectFailed,
                    $"{obj.Kind} '{obj.Name}' ({obj.Uuid}) could not be imported ({e.Message}); its children are attached to its parent.");
                foreach (var c in obj.ChildObjects) Build(c, parentEntity, parentObject, layerId, c.Matrix * local);
                return;
            }

            _byUuid[obj.Uuid] = entity;
            if (parentEntity is null) _roots.Add(entity);
            if (obj is MvrDeviceObject { Connections.Count: > 0 } dev) _connectionOwners.Add((dev, entity));

            for (int i = 0; i < geometryChildren.Count; i++)
            {
                var (asset, geoLocal, name) = geometryChildren[i];
                var child = new Entity(name, DeterministicGuid.Create(entity.Id, "geometry", i)) { LayerId = layerId };
                ApplyTransform(child, geoLocal);
                child.Add(new MeshComponent { ModelAssetId = asset, CastShadow = (obj as MvrDeviceObject)?.CastShadow ?? true });
                if (_scene.Find(child.Id) is null) _scene.Add(child, entity);
            }

            foreach (var c in obj.ChildObjects) Build(c, entity, obj, layerId, c.Matrix);

            if (obj is MvrSupport && entity.Get<RiggingPointComponent>() is { } rig)
            {
                foreach (var c in entity.Children)
                    if (c.Has<TrussComponent>()) rig.Carries.Add(c.Id);
            }
        }

        private (Entity Entity, List<(Guid Asset, Matrix4x4 Local, string Name)> GeometryChildren) CreateEntity(
            MvrObject obj, Entity? parentEntity, MvrObject? parentObject, Guid layerId, Matrix4x4 local)
        {
            string name = obj.Name.Length > 0 ? obj.Name : obj.Kind.ToString();
            var entity = new Entity(name, _ids[obj]) { LayerId = layerId };
            ApplyTransform(entity, local);
            var children = new List<(Guid, Matrix4x4, string)>();

            switch (obj)
            {
                case MvrGroupObject:
                    entity.Add(new GroupComponent());
                    break;
                case MvrFixture f:
                    entity.Add(BuildFixture(f, parentEntity, parentObject));
                    if (f.Geometries.Count > 0)
                    {
                        _sink.Aggregate(IssueSeverity.Note, IssueCategory.Scene, MvrIssueCodes.NotRepresentable, "Fixture/Geometries",
                            n => $"Geometries of {n} fixture(s) are not imported (a fixture's body comes from its GDTF).", entity.Id);
                    }
                    break;
                case MvrDeviceObject d:
                    var (direct, rest) = PlanGeometry(d, entity.Id, isTruss: d is MvrTruss);
                    children = rest;
                    switch (d)
                    {
                        case MvrTruss:
                            var truss = new TrussComponent { Kind = TrussKind.Truss, ModelAssetId = direct };
                            var bounds = Bounds(direct, rest);
                            if (!bounds.IsEmpty && bounds.Size.X > 0) truss.Length = bounds.Size.X;
                            entity.Add(truss);
                            if (direct is null && rest.Count > 0)
                            {
                                _sink.Aggregate(IssueSeverity.Note, IssueCategory.Scene, MvrIssueCodes.NotRepresentable, "Truss/OffsetGeometry",
                                    n => $"{n} truss(es) have geometry with an offset; it is attached as child meshes and the truss has no model of its own.",
                                    entity.Id);
                            }
                            break;
                        case MvrSupport:
                            entity.Add(new RiggingPointComponent());
                            if (direct is { } sa) entity.Add(new MeshComponent { ModelAssetId = sa, CastShadow = d.CastShadow ?? true });
                            break;
                        default:
                            if (direct is { } ma) entity.Add(new MeshComponent { ModelAssetId = ma, CastShadow = d.CastShadow ?? true });
                            break;
                    }
                    if (!string.IsNullOrWhiteSpace(d.GdtfSpec)) entity.Add(BuildFixture(d, parentEntity, parentObject));
                    break;
            }
            return (entity, children);
        }

        private FixtureComponent BuildFixture(MvrDeviceObject d, Entity? parentEntity, MvrObject? parentObject)
        {
            var fc = new FixtureComponent { CastShadow = d.CastShadow ?? true, CustomId = d.CustomId ?? 0 };
            string spec = (d.GdtfSpec ?? "").Trim();
            if (spec.Length > 0 && _gdtfs.TryGetValue(spec, out var r))
            {
                fc.GdtfSpec = r.FileName;
                fc.GdtfMode = _modes.TryGetValue((spec, (d.GdtfMode ?? "").Trim()), out var m) ? m.Resolved : d.GdtfMode;
            }
            else
            {
                fc.GdtfSpec = spec;
                fc.GdtfMode = d.GdtfMode;
                if (d is MvrFixture)
                {
                    _sink.Aggregate(IssueSeverity.Error, IssueCategory.Fixture, MvrIssueCodes.GdtfUnresolved, "no-spec",
                        n => $"{n} fixture(s) have no GDTFSpec.", _ids[d]);
                }
            }

            foreach (var a in d.Addresses)
            {
                var dmx = a.ToDmxAddress();
                // The break "has to be unique for one fixture"; a second address for it would make the patch ambiguous.
                if (fc.Addresses.Exists(x => x.Break == dmx.Break))
                {
                    _sink.Add(IssueSeverity.Warning, IssueCategory.Patch, MvrIssueCodes.AddressInvalid,
                        string.Create(CultureInfo.InvariantCulture,
                            $"'{d.Name}' has more than one address for DMX break {a.Break}; only the first ({fc.Addresses.First(x => x.Break == dmx.Break)}) is used."),
                        _ids[d]);
                    continue;
                }
                fc.Addresses.Add(dmx);
            }
            fc.FixtureId = FixtureIdOf(d);
            // 0 is the neutral value exporters write because MVR makes UnitNumber mandatory on fixtures: no unit number.
            if (d.UnitNumber is { } unit && unit != 0) fc.UnitNumber = unit.ToString(CultureInfo.InvariantCulture);
            if (parentObject is MvrTruss or MvrSupport && parentEntity is not null) fc.MountedOn = parentEntity.Id;

            if (d is MvrFixture f)
            {
                fc.Color = f.Color;
                if (f.Focus is { } focus)
                {
                    if (_focusWorld.TryGetValue(focus, out var p)) fc.FocusPoint = p;
                    else
                    {
                        _sink.Add(IssueSeverity.Warning, IssueCategory.Fixture, MvrIssueCodes.FocusMissing,
                            $"Fixture '{f.Name}' is focused on {focus}, which is not a focus point of the file; the focus is dropped.", _ids[f]);
                    }
                }
            }
            return fc;
        }

        private int FixtureIdOf(MvrDeviceObject d)
        {
            if (d.FixtureIdNumeric is { } numeric && numeric != 0) return numeric;
            if (string.IsNullOrWhiteSpace(d.FixtureId)) return d.FixtureIdNumeric ?? 0;
            if (int.TryParse(d.FixtureId.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int id)) return id;
            _sink.Aggregate(IssueSeverity.Note, IssueCategory.Patch, MvrIssueCodes.NotRepresentable, "FixtureID/text",
                n => $"{n} object(s) have a non-numeric FixtureID (e.g. '{d.FixtureId}') and no FixtureIDNumeric; their fixture id is 0.", _ids[d]);
            return 0;
        }

        private void ApplyTransform(Entity entity, Matrix4x4 local)
        {
            var d = MvrMatrix.Decompose(local);
            entity.Local = d.Transform;
            if (d.Degenerate)
            {
                _sink.Aggregate(IssueSeverity.Warning, IssueCategory.Scene, MvrIssueCodes.MatrixDegenerate, "",
                    n => $"{n} object(s) have degenerate matrices (zero or coplanar axes); only their position is kept.", entity.Id);
            }
            if (d.Mirrored)
            {
                _sink.Aggregate(IssueSeverity.Note, IssueCategory.Scene, MvrIssueCodes.MatrixMirrored, "",
                    n => $"{n} object(s) have mirror matrices (determinant < 0); the mirror is kept as a negative X scale.", entity.Id);
            }
            if (d.IsSheared)
            {
                _sink.Aggregate(IssueSeverity.Warning, IssueCategory.Scene, MvrIssueCodes.MatrixSheared, "",
                    n => $"{n} object(s) have sheared matrices; the shear is dropped (axes are re-orthogonalised around local Z).", entity.Id);
            }
        }

        // ------------------------------------------------------------ geometry

        /// <summary>
        /// Expands symbols and resolves files. A single geometry at the object's origin goes onto the entity itself
        /// (<c>direct</c>); anything else becomes child entities. For trusses the first geometry at the origin is the
        /// truss model.
        /// </summary>
        private (Guid? Direct, List<(Guid Asset, Matrix4x4 Local, string Name)> Children) PlanGeometry(MvrDeviceObject d, Guid owner, bool isTruss)
        {
            var flat = new List<(MvrGeometry3D Geometry, Matrix4x4 Local, string Name)>();
            Flatten(d.Geometries, Matrix4x4.Identity, flat, new HashSet<Guid>(), 0, owner, null);
            var items = new List<(Guid Asset, Matrix4x4 Local, string Name)>();
            foreach (var (g, m, name) in flat)
                if (GetAsset(g.FileName, owner) is { } asset) items.Add((asset.Id, m, name));

            if (items.Count == 0) return (null, items);
            if (isTruss)
            {
                int idx = items.FindIndex(i => MvrMatrix.IsIdentity(i.Local, 1e-5f));
                if (idx < 0) return (null, items);
                var direct = items[idx].Asset;
                items.RemoveAt(idx);
                return (direct, items);
            }
            if (items.Count == 1 && MvrMatrix.IsIdentity(items[0].Local, 1e-5f)) return (items[0].Asset, new());
            return (null, items);
        }

        private void Flatten(
            List<MvrGeometry> geometries, Matrix4x4 parent, List<(MvrGeometry3D, Matrix4x4, string)> output,
            HashSet<Guid> visiting, int depth, Guid owner, string? symbolName)
        {
            foreach (var g in geometries)
            {
                // Checked for symbols too: a tree of symbols without files must not be walked to the end either.
                if (_geometryBudget <= 0)
                {
                    if (!_geometryBudgetReported)
                    {
                        _geometryBudgetReported = true;
                        _sink.Add(IssueSeverity.Warning, IssueCategory.Scene, MvrIssueCodes.GeometryLimit,
                            string.Create(CultureInfo.InvariantCulture,
                                $"The file expands to more than {MaxExpandedGeometries} geometry instances (nested symbols multiply); the rest of the geometry is not imported."),
                            owner);
                    }
                    return;
                }
                var local = g.Matrix * parent;
                switch (g)
                {
                    case MvrGeometry3D g3:
                        _geometryBudget--;
                        output.Add((g3, local, symbolName ?? Path.GetFileNameWithoutExtension(g3.FileName)));
                        break;
                    case MvrSymbol s:
                        if (!_symdefs.TryGetValue(s.SymdefId, out var def))
                        {
                            _sink.Add(IssueSeverity.Warning, IssueCategory.Scene, MvrIssueCodes.SymdefMissing,
                                $"A symbol references the undefined symdef {s.SymdefId}; it is skipped.", owner);
                            break;
                        }
                        if (depth >= MaxSymbolDepth || !visiting.Add(def.Uuid))
                        {
                            _sink.Add(IssueSeverity.Warning, IssueCategory.Scene, MvrIssueCodes.SymdefCycle,
                                $"Symdef '{def.Name}' ({def.Uuid}) instances itself; the recursion is cut.", owner);
                            break;
                        }
                        _geometryBudget--; // every expansion step counts, so symbol trees without files stay bounded
                        Flatten(def.Geometries, local, output, visiting, depth + 1, owner, def.Name.Length > 0 ? def.Name : symbolName);
                        visiting.Remove(def.Uuid);
                        break;
                }
            }
        }

        private ImportedAsset? GetAsset(string fileName, Guid owner)
        {
            string key = MvrDocument.NormalizePath(fileName);
            if (_assetByFile.TryGetValue(key, out var cached)) return cached;
            ImportedAsset? asset = null;
            var file = _doc.FindFile(fileName);
            // MVR: "if there is no extension, it will assume that the extension is 3ds".
            if (file is null && Path.GetExtension(key).Length == 0) file = _doc.FindFile(key + ".3ds");
            // Different spellings of one archive file ("Box.glb", "./box.glb", "truss" for "truss.3ds") share its asset.
            if (file is not null && _assetByFile.TryGetValue(file.Name, out var sameFile))
            {
                _assetByFile[key] = sameFile;
                return sameFile;
            }
            if (file is null)
            {
                _sink.Add(IssueSeverity.Warning, IssueCategory.Scene, MvrIssueCodes.FileMissing,
                    $"3D file '{fileName}' is not in the MVR archive; the geometry is skipped.", owner);
            }
            else if (file.Bytes is null)
            {
                _sink.Add(IssueSeverity.Warning, IssueCategory.Scene, MvrIssueCodes.GeometryUnreadable,
                    $"3D file '{file.Name}' cannot be read from the archive ({file.Error}); the geometry is skipped.", owner);
            }
            else if (MvrModelFiles.FormatOf(file.Name) is not { } format)
            {
                _sink.Add(IssueSeverity.Warning, IssueCategory.Scene, MvrIssueCodes.GeometryUnsupported,
                    $"3D file '{file.Name}' is not 3DS, glTF or GLB (the formats MVR allows); the geometry is skipped.", owner);
            }
            else
            {
                var extra = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
                if (format == ModelFileFormat.Gltf)
                {
                    string folder = file.Name.Contains('/') ? file.Name[..(file.Name.LastIndexOf('/') + 1)] : "";
                    foreach (var dep in MvrModelFiles.GltfDependencies(file.Name, file.Bytes))
                    {
                        if (_doc.FindFile(dep) is { Bytes: { } depBytes })
                            extra[dep.StartsWith(folder, StringComparison.OrdinalIgnoreCase) ? dep[folder.Length..] : dep] = depBytes;
                    }
                }
                asset = new ImportedAsset(DeterministicGuid.FromContent(file.Name, file.Bytes), file.Name, file.Bytes, ImportedAssetKind.Model, format)
                {
                    AdditionalFiles = extra,
                };
                if (_options.ValidateGeometry)
                {
                    try
                    {
                        var model = asset.Decode();
                        _assetBounds[asset.Id] = model.ComputeBounds();
                    }
                    catch (InvalidDataException e)
                    {
                        _sink.Add(IssueSeverity.Warning, IssueCategory.Scene, MvrIssueCodes.GeometryUnreadable,
                            $"3D file '{file.Name}' cannot be decoded ({e.Message}); the geometry is skipped.", owner);
                        asset = null;
                    }
                }
                if (asset is not null) _assets.Add(asset);
            }
            _assetByFile[key] = asset;
            if (file is not null) _assetByFile[file.Name] = asset;
            return asset;
        }

        private Aabb Bounds(Guid? direct, List<(Guid Asset, Matrix4x4 Local, string Name)> rest)
        {
            var b = Aabb.Empty;
            if (direct is { } d && _assetBounds.TryGetValue(d, out var db)) b.Include(db);
            foreach (var (asset, local, _) in rest)
                if (_assetBounds.TryGetValue(asset, out var ab)) b.Include(ab.Transform(local));
            return b;
        }

        // ------------------------------------------------------------ connections → cables

        private void CreateCables()
        {
            if (!_options.CreateCablesFromConnections) return;
            var seen = new HashSet<(Guid, string, Guid, string)>();
            foreach (var (obj, entity) in _connectionOwners)
            {
                for (int i = 0; i < obj.Connections.Count; i++)
                {
                    var c = obj.Connections[i];
                    if (!_byUuid.TryGetValue(c.ToObject, out var other))
                    {
                        _sink.Add(IssueSeverity.Warning, IssueCategory.Data, MvrIssueCodes.ConnectionTargetMissing,
                            $"'{entity.Name}' has a connection to {c.ToObject}, which is not an object of the file; it is skipped.", entity.Id);
                        continue;
                    }
                    var a = (other.Id, c.Other);
                    var b = (entity.Id, c.Own);
                    var key = Compare(a, b) <= 0 ? (a.Item1, a.Item2, b.Item1, b.Item2) : (b.Item1, b.Item2, a.Item1, a.Item2);
                    if (!seen.Add(key)) continue;

                    var cableEntity = new Entity($"{other.Name} → {entity.Name}", DeterministicGuid.Create(entity.Id, "connection", i))
                    {
                        LayerId = entity.LayerId,
                    };
                    var n0 = new CableNode { Id = DeterministicGuid.Create(cableEntity.Id, "node", 0), Position = other.WorldPosition, Anchor = new CableAnchor(other.Id, Vector3.Zero) };
                    var n1 = new CableNode { Id = DeterministicGuid.Create(cableEntity.Id, "node", 1), Position = entity.WorldPosition, Anchor = new CableAnchor(entity.Id, Vector3.Zero) };
                    var cable = new CableComponent();
                    cable.Nodes.Add(n0);
                    cable.Nodes.Add(n1);
                    cable.Segments.Add(new CableSegment(n0.Id, n1.Id));
                    cable.Endpoints.Add(new CableEndpoint { NodeId = n0.Id, EntityId = other.Id, PortName = c.Other });
                    cable.Endpoints.Add(new CableEndpoint { NodeId = n1.Id, EntityId = entity.Id, PortName = c.Own });
                    cableEntity.Add(cable);
                    if (_scene.Find(cableEntity.Id) is not null) continue;
                    _scene.Add(cableEntity, _group);
                    if (_group is null) _roots.Add(cableEntity);
                }
            }

            static int Compare((Guid Id, string Port) x, (Guid Id, string Port) y)
            {
                int c = x.Id.CompareTo(y.Id);
                return c != 0 ? c : string.CompareOrdinal(x.Port, y.Port);
            }
        }

        // ------------------------------------------------------------ data Core cannot hold

        private void ReportUnrepresentable()
        {
            int classes = 0, positions = 0, alignments = 0, overwrites = 0, commands = 0, mappings = 0, gobos = 0, invert = 0;
            int protocols = 0, networks = 0, sources = 0, projections = 0, multipatch = 0;
            foreach (var o in _doc.AllObjects())
            {
                if (o.Classing is not null) classes++;
                if (o is not MvrDeviceObject d) continue;
                if (d.Position is not null) positions++;
                if (d.Alignments.Count > 0) alignments++;
                if (d.Overwrites.Count > 0) overwrites++;
                if (d.CustomCommands.Count > 0) commands++;
                if (d.Protocols.Count > 0) protocols++;
                if (d.Networks.Count > 0) networks++;
                if (d.Multipatch is not null) multipatch++;
                switch (d)
                {
                    case MvrFixture f:
                        if (f.Mappings.Count > 0) mappings++;
                        if (f.Gobo is not null) gobos++;
                        if (f.DmxInvertPan == true || f.DmxInvertTilt == true) invert++;
                        break;
                    case MvrVideoScreen v when v.Sources.Count > 0:
                        sources++;
                        break;
                    case MvrProjector p when p.Projections.Count > 0:
                        projections++;
                        break;
                }
            }
            void Report(int count, string what)
            {
                if (count == 0) return;
                _sink.Add(IssueSeverity.Note, IssueCategory.Scene, MvrIssueCodes.NotRepresentable,
                    $"{count} object(s) have {what}, which the scene does not hold yet; it stays available in the imported MVR document.");
            }
            Report(classes, "a class (Classing)");
            Report(positions, "a hanging position (Position)");
            Report(alignments, "beam alignments");
            Report(overwrites, "wheel/emitter overwrites");
            Report(commands, "custom commands");
            Report(protocols, "protocol settings");
            Report(networks, "network settings");
            Report(multipatch, "a multipatch parent");
            Report(mappings, "video mappings");
            Report(gobos, "custom gobos");
            Report(invert, "inverted pan/tilt DMX");
            Report(sources, "video sources");
            Report(projections, "projections");
        }
    }

    private sealed class SpecModeComparer : IEqualityComparer<(string Spec, string Mode)>
    {
        public static readonly SpecModeComparer Instance = new();

        public bool Equals((string Spec, string Mode) x, (string Spec, string Mode) y) =>
            string.Equals(x.Spec, y.Spec, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Mode, y.Mode, StringComparison.Ordinal);

        public int GetHashCode((string Spec, string Mode) obj) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Spec), StringComparer.Ordinal.GetHashCode(obj.Mode));
    }
}
