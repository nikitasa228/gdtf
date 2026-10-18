using System.Globalization;
using System.Numerics;
using System.Text;
using Candela.Core.Diagnostics;
using Candela.Core.Geometry;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;

namespace Candela.Mvr;

/// <summary>
/// Converts a Core scene into an <see cref="MvrDocument"/> (MVR 1.6) for <see cref="MvrWriter"/>. See the writer's
/// remarks for the mapping; every conversion here is deterministic for equal input.
/// </summary>
internal sealed class SceneExporter
{
    private readonly Scene _scene;
    private readonly MvrExportOptions _options;
    private readonly MvrDocument _doc = new();
    private readonly List<Issue> _issues = new();
    private readonly IssueSink _sink;

    private readonly Dictionary<Guid, MvrLayer> _layers = new();
    private readonly Dictionary<Guid, MvrDeviceObject> _devices = new();
    private readonly List<(MvrLayer Layer, MvrFocusPoint Point)> _focusPoints = new();
    private readonly Dictionary<string, string> _gdtfFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, string?> _modelFiles = new();
    private readonly Dictionary<string, string?> _wallFiles = new(StringComparer.Ordinal);
    private readonly HashSet<string> _fileNames = new(StringComparer.OrdinalIgnoreCase);
    private int _objectCount;
    private int _connectionCount;

    public SceneExporter(Scene scene, MvrExportOptions options)
    {
        _scene = scene;
        _options = options;
        _sink = new IssueSink(_issues);
    }

    public MvrExportResult Run()
    {
        _doc.VerMajor = MvrWriter.VerMajor;
        _doc.VerMinor = MvrWriter.VerMinor;
        _doc.Provider = string.IsNullOrWhiteSpace(_options.Provider) ? MvrExportOptions.DefaultProvider : _options.Provider;
        _doc.ProviderVersion = string.IsNullOrWhiteSpace(_options.ProviderVersion) ? null : _options.ProviderVersion;
        _doc.SourceFileName = _scene.Name;

        SetUpLayers();
        foreach (var root in _scene.Roots)
        {
            var layer = LayerOf(root);
            if (Convert(root, layer) is { } obj) layer.Objects.Add(obj);
        }
        foreach (var (layer, point) in _focusPoints) layer.Objects.Add(point);
        WriteConnections();
        _sink.Complete();
        return new MvrExportResult(_doc, _issues, _objectCount, _focusPoints.Count, _connectionCount);
    }

    // ------------------------------------------------------------------ layers

    private void SetUpLayers()
    {
        int index = 0;
        foreach (var l in _scene.Layers)
        {
            var id = l.Id == Guid.Empty ? DeterministicGuid.Create(_scene.Id, "layer", index) : l.Id;
            index++;
            if (_layers.ContainsKey(id)) continue;
            var layer = new MvrLayer { Uuid = id, Name = l.Name };
            _layers[l.Id] = layer;
            _layers[id] = layer;
            _doc.Layers.Add(layer);
        }
        if (_doc.Layers.Count == 0)
            _doc.Layers.Add(new MvrLayer { Uuid = DeterministicGuid.Create(_scene.Id, "layer", 0), Name = "Default" });
    }

    private MvrLayer LayerOf(Entity root)
    {
        if (_layers.TryGetValue(root.LayerId, out var layer)) return layer;
        _sink.Aggregate(IssueSeverity.Note, IssueCategory.Scene, MvrIssueCodes.NotRepresentable, "layer-missing",
            n => $"{n} object(s) are on a layer that is not in the scene's layer list; they are written to the layer '{_doc.Layers[0].Name}'.",
            root.Id);
        return _doc.Layers[0];
    }

    // ------------------------------------------------------------------ objects

    private MvrObject? Convert(Entity e, MvrLayer layer)
    {
        var fixture = e.Get<FixtureComponent>();
        var mesh = e.Get<MeshComponent>();
        var truss = e.Get<TrussComponent>();
        var rigging = e.Get<RiggingPointComponent>();
        var wall = _options.ExportWalls ? e.Get<WallComponent>() : null;
        ReportUnexported(e);

        MvrObject obj;
        if (truss is not null) obj = new MvrTruss();
        else if (rigging is not null) obj = new MvrSupport();
        else if (mesh is not null || wall is not null) obj = new MvrSceneObject();
        else if (fixture is not null) obj = new MvrFixture();
        else if (e.Has<GroupComponent>()) obj = new MvrGroupObject();
        else if (e.Components.Count == 0)
        {
            // A plain transform node: what the importer makes of a SceneObject whose geometry is all in child meshes
            // (or that has none), so it goes back out as a SceneObject.
            obj = new MvrSceneObject();
        }
        else if (e.Children.Count > 0)
        {
            obj = new MvrGroupObject();
            _sink.Aggregate(IssueSeverity.Note, IssueCategory.Scene, MvrIssueCodes.NotRepresentable, "as-group",
                n => $"{n} object(s) of a kind MVR cannot hold have child objects; they are written as GroupObjects so the children keep their place.",
                e.Id);
        }
        else
        {
            // A cable (written as Connections of the devices it links, see WriteConnections) or content MVR cannot
            // hold (reported by ReportUnexported).
            return null;
        }

        obj.Uuid = e.Id == Guid.Empty ? DeterministicGuid.Create(_scene.Id, "entity", _objectCount) : e.Id;
        obj.Name = e.Name;
        obj.Matrix = LocalMatrix(e);
        obj.HasMatrix = !MvrMatrix.IsIdentity(obj.Matrix, 0f);
        _objectCount++;

        if (e.Children.Count > 0)
        {
            foreach (var c in e.Children)
            {
                if (c.LayerId != e.LayerId && c.LayerId != Guid.Empty)
                {
                    _sink.Aggregate(IssueSeverity.Note, IssueCategory.Scene, MvrIssueCodes.NotRepresentable, "child-layer",
                        n => $"{n} child object(s) are on another layer than their parent; MVR puts children on their parent's layer.",
                        c.Id);
                }
            }
        }

        switch (obj)
        {
            case MvrGroupObject group:
                foreach (var c in e.Children)
                    if (Convert(c, layer) is { } child) group.Children.Add(child);
                break;

            case MvrDeviceObject device:
                _devices[e.Id] = device;
                if (truss?.ModelAssetId is { } trussModel) AddGeometry(device, trussModel, Matrix4x4.Identity, e.Id);
                if (mesh is not null) AddGeometry(device, mesh.ModelAssetId, Matrix4x4.Identity, e.Id);
                if (wall is not null) AddWallGeometry(device, wall, e.Id);
                if (truss is not null && truss.ModelAssetId is null && mesh is null)
                {
                    _sink.Aggregate(IssueSeverity.Note, IssueCategory.Rigging, MvrIssueCodes.NotRepresentable, "truss-procedural",
                        n => $"{n} truss(es) have no model (drawn procedurally from length and profile); MVR has no truss length, so they are written without geometry.",
                        e.Id);
                }

                if (fixture is not null) ApplyFixture(device, fixture, e, layer);
                else if (mesh is { CastShadow: false }) device.CastShadow = false;

                var geometryChildren = device is MvrFixture ? null : GeometryChildren(e);
                foreach (var c in e.Children)
                {
                    if (geometryChildren is not null && geometryChildren.Contains(c.Id))
                    {
                        AddGeometry(device, c.Get<MeshComponent>()!.ModelAssetId, LocalMatrix(c), c.Id);
                        continue;
                    }
                    if (Convert(c, layer) is { } child) device.Children.Add(child);
                }
                break;
        }
        return obj;
    }

    private Matrix4x4 LocalMatrix(Entity e)
    {
        var m = e.Local.ToMatrix();
        if (IsFinite(m)) return m;
        _sink.Aggregate(IssueSeverity.Warning, IssueCategory.Scene, MvrIssueCodes.MatrixInvalid, "export",
            n => $"{n} object(s) have a transform that is not finite; they are written at their parent's origin.", e.Id);
        return Matrix4x4.Identity;
    }

    /// <summary>
    /// Children the importer created for the extra geometry of an MVR object (ids derived from the parent's id): they
    /// are written back as <c>Geometry3D</c> entries of the parent, so an import–export round trip keeps the structure.
    /// </summary>
    private static HashSet<Guid>? GeometryChildren(Entity e)
    {
        if (e.Children.Count == 0) return null;
        HashSet<Guid>? expected = null;
        HashSet<Guid>? result = null;
        foreach (var c in e.Children)
        {
            if (c.Children.Count != 0 || c.Components.Count != 1 || c.Components[0] is not MeshComponent) continue;
            if (expected is null)
            {
                expected = new HashSet<Guid>();
                for (int k = 0; k < e.Children.Count; k++) expected.Add(DeterministicGuid.Create(e.Id, "geometry", k));
            }
            if (expected.Contains(c.Id)) (result ??= new HashSet<Guid>()).Add(c.Id);
        }
        return result;
    }

    private void ApplyFixture(MvrDeviceObject d, FixtureComponent fc, Entity e, MvrLayer layer)
    {
        string spec = (fc.GdtfSpec ?? "").Trim();
        if (spec.Length == 0)
        {
            _sink.Aggregate(IssueSeverity.Warning, IssueCategory.Fixture, MvrIssueCodes.ExportResourceMissing, "fixture-no-spec",
                n => $"{n} fixture(s) have no GDTF file; they are written without GDTFSpec.", e.Id);
        }
        else
        {
            d.GdtfSpec = EmbedGdtf(spec, e.Id);
        }
        d.GdtfMode = string.IsNullOrWhiteSpace(fc.GdtfMode) ? null : fc.GdtfMode.Trim();
        d.CastShadow = fc.CastShadow;

        foreach (var a in fc.Addresses)
        {
            if (a.Universe < 1 || a.Address is < 1 or > 512 || a.Break < 1 || a.Universe > (int.MaxValue - 512) / 512)
            {
                _sink.Add(IssueSeverity.Warning, IssueCategory.Patch, MvrIssueCodes.AddressInvalid,
                    string.Create(CultureInfo.InvariantCulture,
                        $"'{e.Name}': DMX address universe {a.Universe}, address {a.Address}, break {a.Break} is out of range and is not exported."),
                    e.Id);
                continue;
            }
            d.Addresses.Add(MvrAddress.FromDmxAddress(a));
        }

        bool isFixture = d is MvrFixture;
        if (isFixture || fc.FixtureId != 0)
        {
            d.FixtureId = fc.FixtureId.ToString(CultureInfo.InvariantCulture);
            d.FixtureIdNumeric = fc.FixtureId;
        }
        if (!string.IsNullOrWhiteSpace(fc.UnitNumber))
        {
            if (int.TryParse(fc.UnitNumber.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int unit)) d.UnitNumber = unit;
            else
            {
                _sink.Aggregate(IssueSeverity.Note, IssueCategory.Patch, MvrIssueCodes.NotRepresentable, "unit-number",
                    n => $"{n} fixture(s) have a non-numeric unit number; MVR UnitNumber is an integer, so it is not exported.", e.Id);
            }
        }
        if (fc.CustomId != 0) d.CustomId = fc.CustomId;

        if (d is MvrFixture f)
        {
            if (fc.Color is { } c)
            {
                if (float.IsFinite(c.X) && float.IsFinite(c.Y) && float.IsFinite(c.BigY) && c.X >= 0 && c.X <= 1 && c.Y > 0 && c.Y <= 1 && c.BigY >= 0)
                    f.Color = c;
                else
                    _sink.Add(IssueSeverity.Warning, IssueCategory.Fixture, MvrIssueCodes.ValueInvalid,
                        $"'{e.Name}': colour {c} is not a valid CIE xyY chromaticity and is not exported.", e.Id);
            }
            if (fc.FocusPoint is { } p)
            {
                if (float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z))
                {
                    var point = new MvrFocusPoint
                    {
                        Uuid = DeterministicGuid.Create(e.Id, "focus"),
                        Name = $"{e.Name} focus",
                        Matrix = Matrix4x4.CreateTranslation(p),
                        HasMatrix = true,
                    };
                    f.Focus = point.Uuid;
                    _focusPoints.Add((layer, point));
                }
                else
                {
                    _sink.Add(IssueSeverity.Warning, IssueCategory.Fixture, MvrIssueCodes.ValueInvalid,
                        $"'{e.Name}': focus point {p} is not finite and is not exported.", e.Id);
                }
            }
        }
        else if (fc.FocusPoint is not null || fc.Color is not null)
        {
            _sink.Aggregate(IssueSeverity.Note, IssueCategory.Fixture, MvrIssueCodes.NotRepresentable, "device-focus",
                n => $"{n} GDTF device(s) written as {d.Kind} have a focus point or colour; MVR only stores those on Fixture elements.", e.Id);
        }
        if (fc.ManualAttributes.Count > 0)
        {
            _sink.Aggregate(IssueSeverity.Note, IssueCategory.Fixture, MvrIssueCodes.NotRepresentable, "manual-attributes",
                n => $"{n} fixture(s) have manual attribute values; MVR has no programming data, so they are not exported.", e.Id);
        }
    }

    private void ReportUnexported(Entity e)
    {
        foreach (var c in e.Components)
        {
            string? what = c switch
            {
                ImageComponent => "images",
                CameraComponent => "cameras",
                ConsoleComponent => "consoles",
                PowerSourceComponent => "power sources",
                AtmosphereVolumeComponent => "atmosphere volumes",
                WallComponent when !_options.ExportWalls => "walls",
                MeshComponent { MaterialOverrides.Count: > 0 } => "material overrides",
                _ => null,
            };
            if (what is null) continue;
            _sink.Aggregate(IssueSeverity.Note, IssueCategory.Scene, MvrIssueCodes.NotRepresentable, "component/" + what,
                n => $"{n} object(s) have {what}, which MVR cannot hold; they are not exported.", e.Id);
        }
    }

    // ------------------------------------------------------------------ connections

    private void WriteConnections()
    {
        int routed = 0;
        foreach (var (entity, cable) in _scene.Query<CableComponent>())
        {
            var ends = new List<(MvrDeviceObject Device, string Port)>(cable.Endpoints.Count);
            foreach (var ep in cable.Endpoints)
                if (_devices.TryGetValue(ep.EntityId, out var device)) ends.Add((device, ep.PortName ?? ""));
            if (ends.Count < 2)
            {
                if (cable.Endpoints.Count > 0)
                {
                    _sink.Aggregate(IssueSeverity.Note, IssueCategory.Data, MvrIssueCodes.NotRepresentable, "cable-not-device",
                        n => $"{n} cable(s) do not connect two exported devices; MVR connections link devices only, so they are not exported.",
                        entity.Id);
                }
                continue;
            }
            routed++;
            var (source, sourcePort) = ends[0];
            for (int k = 1; k < ends.Count; k++)
            {
                var (target, port) = ends[k];
                if (ReferenceEquals(target, source)) continue;
                bool duplicate = false;
                foreach (var existing in target.Connections)
                {
                    if (existing.ToObject == source.Uuid && existing.Own == port && existing.Other == sourcePort)
                    {
                        duplicate = true;
                        break;
                    }
                }
                if (duplicate) continue;
                target.Connections.Add(new MvrConnection { Own = port, Other = sourcePort, ToObject = source.Uuid });
                _connectionCount++;
            }
        }
        if (routed > 0)
        {
            _sink.Add(IssueSeverity.Note, IssueCategory.Data, MvrIssueCodes.NotRepresentable,
                $"{routed} cable(s) were written as MVR connections; their routes, types and labels are not part of MVR.");
        }
    }

    // ------------------------------------------------------------------ embedded files

    private string EmbedGdtf(string spec, Guid user)
    {
        if (_gdtfFiles.TryGetValue(spec, out var existing)) return existing;
        string name = UniqueFileName(GdtfResolver.EnsureGdtfExtension(MvrDocument.FileNameOf(MvrDocument.NormalizePath(spec))));
        _gdtfFiles[spec] = name;

        if (_options.GdtfProvider is null)
        {
            _sink.Aggregate(IssueSeverity.Warning, IssueCategory.Fixture, MvrIssueCodes.ExportResourceMissing, "gdtf-no-provider",
                n => $"No GDTF source was given; {n} GDTF file(s) are referenced but not embedded, so other programs must find them in their own library.",
                user);
            return name;
        }

        byte[]? bytes = null;
        string? error = null;
        try
        {
            bytes = _options.GdtfProvider(spec);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            error = e.Message;
        }
        if (bytes is null || bytes.Length == 0)
        {
            _sink.Add(IssueSeverity.Warning, IssueCategory.Fixture, MvrIssueCodes.ExportResourceMissing,
                error is null
                    ? $"GDTF '{spec}' was not found; it is referenced by its file name but not embedded."
                    : $"GDTF '{spec}' could not be read ({error}); it is referenced by its file name but not embedded.",
                user);
            return name;
        }
        if (bytes.Length < 4 || bytes[0] != (byte)'P' || bytes[1] != (byte)'K')
        {
            _sink.Add(IssueSeverity.Warning, IssueCategory.Fixture, MvrIssueCodes.ExportResourceMissing,
                $"The data supplied for GDTF '{spec}' is not a GDTF (zip) archive; it is referenced but not embedded.", user);
            return name;
        }
        _doc.Files.Add(new MvrEmbeddedFile(name, bytes));
        return name;
    }

    private void AddGeometry(MvrDeviceObject device, Guid assetId, Matrix4x4 local, Guid user)
    {
        if (assetId == Guid.Empty) return;
        if (EmbedModel(assetId, user) is not { } file) return;
        device.Geometries.Add(new MvrGeometry3D { FileName = file, Matrix = local, HasMatrix = !MvrMatrix.IsIdentity(local, 0f) });
    }

    private string? EmbedModel(Guid assetId, Guid user)
    {
        if (_modelFiles.TryGetValue(assetId, out var existing)) return existing;
        _modelFiles[assetId] = null;
        if (_options.ModelProvider is null)
        {
            _sink.Aggregate(IssueSeverity.Warning, IssueCategory.Scene, MvrIssueCodes.ExportResourceMissing, "model-no-provider",
                n => $"No model source was given; {n} model(s) are not exported and their objects have no geometry.", user);
            return null;
        }

        ModelData? model = null;
        string? error = null;
        try
        {
            model = _options.ModelProvider(assetId);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            error = e.Message;
        }
        if (model is null)
        {
            _sink.Add(IssueSeverity.Warning, IssueCategory.Scene, MvrIssueCodes.ExportResourceMissing,
                error is null
                    ? $"Model {assetId} was not found; its objects are written without geometry."
                    : $"Model {assetId} could not be loaded ({error}); its objects are written without geometry.",
                user);
            return null;
        }

        string? name = WriteGlb(model, SafeName(model.Name, "Model") + "-" + assetId.ToString("N")[..8] + ".glb", user);
        _modelFiles[assetId] = name;
        return name;
    }

    private void AddWallGeometry(MvrDeviceObject device, WallComponent wall, Guid user)
    {
        float w = wall.Width, h = wall.Height, t = wall.Thickness;
        if (!(float.IsFinite(w) && float.IsFinite(h) && float.IsFinite(t) && w > 0 && h > 0 && t > 0))
        {
            _sink.Add(IssueSeverity.Warning, IssueCategory.Scene, MvrIssueCodes.ValueInvalid,
                string.Create(CultureInfo.InvariantCulture, $"Wall {user} has an invalid size ({w} × {h} × {t} m) and is written without geometry."), user);
            return;
        }
        string key = string.Create(CultureInfo.InvariantCulture, $"{wall.Kind}-{Mm(w)}x{Mm(h)}x{Mm(t)}");
        if (!_wallFiles.TryGetValue(key, out var file))
        {
            file = WriteGlb(WallModel(wall.Kind, w, h, t, key), key + ".glb", user);
            _wallFiles[key] = file;
        }
        if (file is not null) device.Geometries.Add(new MvrGeometry3D { FileName = file });

        static long Mm(float metres) => (long)System.Math.Round(metres * 1000.0);
    }

    private string? WriteGlb(ModelData model, string preferredName, Guid user)
    {
        byte[] glb;
        try
        {
            glb = GlbWriter.Write(model, warning => _sink.Add(IssueSeverity.Warning, IssueCategory.Scene, MvrIssueCodes.GeometryUnreadable,
                $"Model '{model.Name}': {warning}", user));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _sink.Add(IssueSeverity.Warning, IssueCategory.Scene, MvrIssueCodes.GeometryUnreadable,
                $"Model '{model.Name}' cannot be written as glTF ({e.Message}); its objects are written without geometry.", user);
            return null;
        }
        string name = UniqueFileName(preferredName);
        _doc.Files.Add(new MvrEmbeddedFile(name, glb));
        return name;
    }

    private string UniqueFileName(string name)
    {
        if (_fileNames.Add(name) && !string.Equals(name, MvrReader.SceneDescriptionEntry, StringComparison.OrdinalIgnoreCase)) return name;
        string stem = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
        for (int i = 2; ; i++)
        {
            string candidate = string.Create(CultureInfo.InvariantCulture, $"{stem} ({i}){ext}");
            if (_fileNames.Add(candidate)) return candidate;
        }
    }

    /// <summary>ASCII letters, digits, '-' and '_' (others become '_'), at most 48 characters.</summary>
    private static string SafeName(string? name, string fallback)
    {
        if (string.IsNullOrWhiteSpace(name)) return fallback;
        var sb = new StringBuilder(System.Math.Min(name.Length, 48));
        foreach (char ch in name.Trim())
        {
            if (sb.Length >= 48) break;
            bool ok = ch is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_';
            char c = ok ? ch : '_';
            if (c == '_' && sb.Length > 0 && sb[^1] == '_') continue;
            sb.Append(c);
        }
        string s = sb.ToString().Trim('_');
        return s.Length == 0 ? fallback : s;
    }

    /// <summary>
    /// The box of a wall, floor or ceiling (see <see cref="WallKind"/>): centred on the entity's X axis; walls stand on
    /// the origin (Z 0 … height) with the thickness centred on Y; floors have their top face and ceilings their bottom
    /// face at the origin, centred on X and Y.
    /// </summary>
    internal static ModelData WallModel(WallKind kind, float width, float height, float thickness, string name)
    {
        Vector3 min, max;
        switch (kind)
        {
            case WallKind.Floor:
                min = new Vector3(-width / 2, -height / 2, -thickness);
                max = new Vector3(width / 2, height / 2, 0);
                break;
            case WallKind.Ceiling:
                min = new Vector3(-width / 2, -height / 2, 0);
                max = new Vector3(width / 2, height / 2, thickness);
                break;
            default:
                min = new Vector3(-width / 2, -thickness / 2, 0);
                max = new Vector3(width / 2, thickness / 2, height);
                break;
        }
        var mesh = Box(min, max);
        mesh.Name = name;
        var model = new ModelData { Name = name };
        model.Parts.Add(new ModelPart { Name = name, Mesh = mesh });
        model.Materials.Add(new MaterialData { Name = "Wall", BaseColorFactor = new Vector4(0.8f, 0.8f, 0.8f, 1f), Roughness = 0.9f });
        return model;
    }

    /// <summary>Face normal and two in-face axes with <c>A × B = N</c>, so the quads wind counter-clockwise seen from outside.</summary>
    private static readonly (Vector3 N, Vector3 A, Vector3 B)[] BoxFaces =
    {
        (Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ),
        (-Vector3.UnitX, Vector3.UnitZ, Vector3.UnitY),
        (Vector3.UnitY, Vector3.UnitZ, Vector3.UnitX),
        (-Vector3.UnitY, Vector3.UnitX, Vector3.UnitZ),
        (Vector3.UnitZ, Vector3.UnitX, Vector3.UnitY),
        (-Vector3.UnitZ, Vector3.UnitY, Vector3.UnitX),
    };

    /// <summary>An axis-aligned box with flat normals and counter-clockwise outward faces.</summary>
    internal static MeshData Box(Vector3 min, Vector3 max)
    {
        var centre = (min + max) / 2;
        var half = (max - min) / 2;
        var positions = new Vector3[24];
        var normals = new Vector3[24];
        var indices = new uint[36];
        for (int f = 0; f < 6; f++)
        {
            var (n, a, b) = BoxFaces[f];
            var c = centre + n * half;
            var ea = a * half;
            var eb = b * half;
            int v = f * 4;
            positions[v] = c - ea - eb;
            positions[v + 1] = c + ea - eb;
            positions[v + 2] = c + ea + eb;
            positions[v + 3] = c - ea + eb;
            for (int k = 0; k < 4; k++) normals[v + k] = n;
            int i = f * 6;
            indices[i] = (uint)v;
            indices[i + 1] = (uint)(v + 1);
            indices[i + 2] = (uint)(v + 2);
            indices[i + 3] = (uint)v;
            indices[i + 4] = (uint)(v + 2);
            indices[i + 5] = (uint)(v + 3);
        }
        return new MeshData { Positions = positions, Normals = normals, Indices = indices };
    }

    private static bool IsFinite(Matrix4x4 m) =>
        float.IsFinite(m.M11) && float.IsFinite(m.M12) && float.IsFinite(m.M13) &&
        float.IsFinite(m.M21) && float.IsFinite(m.M22) && float.IsFinite(m.M23) &&
        float.IsFinite(m.M31) && float.IsFinite(m.M32) && float.IsFinite(m.M33) &&
        float.IsFinite(m.M41) && float.IsFinite(m.M42) && float.IsFinite(m.M43);
}
