using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Candela.Core.Diagnostics;
using Candela.Core.Math;
using Candela.Core.SceneGraph;

namespace Candela.Core.Serialization;

/// <summary>
/// Scene ↔ JSON (the <c>scene.json</c> of a project archive). Entities are written flat, parents before children,
/// so the format is easy to diff and robust to deep trees. Written with <see cref="CandelaJson.Options"/>: a null whose
/// default is not null (a console without protocol input) is written explicitly, so writing and reading back is exact;
/// properties this version does not know are kept (<c>UnknownProperties</c> of the scene, entities, layers, settings and
/// components) and written back.
/// </summary>
/// <remarks>
/// Reading is tolerant: one damaged element never fails the whole scene. <see cref="FromJson(string, ICollection{Issue}?)"/>
/// reads every layer, entity and component separately; an element that cannot be read is skipped (or its value reset)
/// and reported as an <see cref="Issue"/> with one of the codes below. Only text that is not JSON at all, a root that is
/// not an object and a scene written by a newer format throw. The component discriminator <c>"$type"</c> may appear at
/// any position of a component object (hand-edited files), not only first.
/// </remarks>
public static class SceneSerializer
{
    /// <summary>Version of the scene format this program writes and the newest it reads.</summary>
    public const int FormatVersion = 1;

    /// <summary>Issue code: an entity record could not be read (not an object) and was skipped.</summary>
    public const string EntityInvalidCode = "scene.load-entity-invalid";

    /// <summary>Issue code: an entity id was missing, empty or repeated; the entity got a new id.</summary>
    public const string EntityIdInvalidCode = "scene.load-id-invalid";

    /// <summary>Issue code: an entity's parent does not precede it in the file (missing); the entity became a root.</summary>
    public const string ParentMissingCode = "scene.load-parent-missing";

    /// <summary>Issue code: an entity's transform was unreadable or not finite; the identity is used.</summary>
    public const string TransformInvalidCode = "scene.load-transform-invalid";

    /// <summary>Issue code: a value of an entity or of the scene could not be read and took its default.</summary>
    public const string ValueInvalidCode = "scene.load-value-invalid";

    /// <summary>Issue code: a component could not be read (no or unknown <c>"$type"</c>, damaged data) and was dropped.</summary>
    public const string ComponentInvalidCode = "scene.load-component-invalid";

    /// <summary>Issue code: a layer could not be read and was skipped.</summary>
    public const string LayerInvalidCode = "scene.load-layer-invalid";

    /// <summary>Issue code: the scene settings could not be read; defaults are used.</summary>
    public const string SettingsInvalidCode = "scene.load-settings-invalid";

    private const string TypeDiscriminator = "$type";

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>The scene as <c>scene.json</c> text (indented by default).</summary>
    public static string ToJson(Scene scene, bool indented = true) =>
        JsonSerializer.Serialize(ToFile(scene), indented ? CandelaJson.Options : CandelaJson.CompactOptions);

    /// <summary>Reads <c>scene.json</c> text, tolerating damaged elements without reporting them.</summary>
    /// <inheritdoc cref="FromJson(string, ICollection{Issue}?)"/>
    public static Scene FromJson(string json) => FromJson(json, null);

    /// <summary>
    /// Reads <c>scene.json</c> text. Layers, entities and components are read one by one: a damaged one is skipped (or its
    /// value reset) and reported to <paramref name="issues"/> (see the codes of this class), the rest loads.
    /// </summary>
    /// <param name="json">The JSON text.</param>
    /// <param name="issues">Receives what was repaired or skipped; null to ignore.</param>
    /// <exception cref="JsonException">The text is not JSON.</exception>
    /// <exception cref="InvalidDataException">The root is not an object, or the scene was written by a newer format.</exception>
    public static Scene FromJson(string json, ICollection<Issue>? issues)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var document = JsonDocument.Parse(json, DocumentOptions);
        return FromFile(ReadFile(document.RootElement, issues), issues);
    }

    /// <summary>The scene as the serialisable record (entities flat, parents before children).</summary>
    public static SceneFile ToFile(Scene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        return new SceneFile
        {
            FormatVersion = FormatVersion,
            Id = scene.Id,
            Name = scene.Name,
            Layers = scene.Layers.ToList(),
            Settings = scene.Settings,
            Entities = scene.All().Select(e => new EntityRecord
            {
                Id = e.Id,
                Parent = e.Parent?.Id,
                Name = e.Name,
                Layer = e.LayerId,
                Visible = e.Visible,
                Locked = e.Locked,
                Transform = e.Local,
                Components = e.Components.ToList(),
                UnknownProperties = e.UnknownProperties,
            }).ToList(),
            UnknownProperties = scene.UnknownProperties,
        };
    }

    /// <summary>Builds a scene from its record, tolerating damage without reporting it.</summary>
    /// <inheritdoc cref="FromFile(SceneFile, ICollection{Issue}?)"/>
    public static Scene FromFile(SceneFile file) => FromFile(file, null);

    /// <summary>
    /// Builds a scene from its record. Records must list parents before their children (as <see cref="ToFile"/> writes
    /// them). Damage is repaired and reported to <paramref name="issues"/>: null records and components are skipped,
    /// a missing, empty or repeated entity id is replaced by a new one, an entity whose parent was not listed before it
    /// becomes a root, a non-finite transform becomes the identity. The components, layers and settings of the record are
    /// used as they are, except those that still belong to another scene or entity (a record made by <see cref="ToFile"/>
    /// from a live scene, or one object listed twice): those are copied, so building a scene never changes another one
    /// and <c>FromFile(ToFile(scene))</c> is an independent copy with the same ids.
    /// </summary>
    /// <param name="file">The record (e.g. deserialised <c>scene.json</c>).</param>
    /// <param name="issues">Receives what was repaired or skipped; null to ignore.</param>
    /// <exception cref="InvalidDataException">The scene was written by a newer format.</exception>
    public static Scene FromFile(SceneFile file, ICollection<Issue>? issues)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (file.FormatVersion > FormatVersion)
            throw new InvalidDataException($"The scene was saved by a newer version (format {file.FormatVersion}, this program reads {FormatVersion}).");
        var scene = new Scene(file.Name ?? "") { Id = file.Id == Guid.Empty ? Guid.NewGuid() : file.Id };
        scene.UnknownProperties = file.UnknownProperties;

        var layers = file.Layers?.Where(l => l is not null).Select(l => l.Owner is null ? l : CandelaJson.DeepClone(l)).ToList();
        if (layers is { Count: > 0 })
        {
            scene.Layers.Clear();
            scene.Layers.AddRange(layers);
        }
        scene.Settings = file.Settings is { } settings ? settings.Owner is null ? settings : CandelaJson.DeepClone(settings) : new SceneSettings();

        if (file.Entities is null) return scene;
        var created = new Dictionary<Guid, Entity>(file.Entities.Count);
        int index = 0;
        foreach (var r in file.Entities)
        {
            index++;
            if (r is null)
            {
                Report(issues, IssueSeverity.Warning, EntityInvalidCode, $"Entity #{index} is empty and was skipped.");
                continue;
            }
            var name = r.Name ?? "";
            var id = r.Id;
            if (id == Guid.Empty || created.ContainsKey(id))
            {
                var fresh = Guid.NewGuid();
                Report(issues, IssueSeverity.Warning, EntityIdInvalidCode,
                    id == Guid.Empty
                        ? $"'{name}' had no id and got a new one."
                        : $"'{name}' repeats the id {id} of another entity and got a new one; references to that id keep pointing at the first.",
                    fresh);
                id = fresh;
            }

            var e = new Entity(name, id)
            {
                LayerId = r.Layer,
                Visible = r.Visible,
                Locked = r.Locked,
                UnknownProperties = r.UnknownProperties,
            };
            if (IsFinite(r.Transform))
            {
                e.Local = r.Transform;
            }
            else
            {
                Report(issues, IssueSeverity.Warning, TransformInvalidCode, $"The placement of '{name}' was not a finite transform; it was reset.", id);
            }
            if (r.Components is not null)
            {
                foreach (var c in r.Components)
                {
                    if (c is null)
                    {
                        Report(issues, IssueSeverity.Warning, ComponentInvalidCode, $"An empty component of '{name}' was dropped.", id);
                        continue;
                    }
                    var component = c;
                    if (component.Owner is { } holder)
                    {
                        // Still attached (a record of a live scene, or listed twice): the scene gets its own copy.
                        try
                        {
                            component = component.Clone();
                        }
                        catch (Exception x) when (IsReadFailure(x))
                        {
                            Report(issues, IssueSeverity.Warning, ComponentInvalidCode,
                                $"A {c.GetType().Name} of '{name}' is attached to '{holder.Name}' and could not be copied; it was dropped: {x.Message}", id);
                            continue;
                        }
                    }
                    e.Add(component);
                }
            }

            Entity? parent = null;
            if (r.Parent is { } pid && pid != Guid.Empty && !created.TryGetValue(pid, out parent))
            {
                Report(issues, IssueSeverity.Warning, ParentMissingCode,
                    $"The parent {pid} of '{name}' is not listed before it; '{name}' was moved to the scene root.", id);
            }
            created.Add(id, e);
            scene.Add(e, parent);
        }
        return scene;
    }

    /// <summary>Reads the record element by element (see <see cref="FromJson(string, ICollection{Issue}?)"/>).</summary>
    private static SceneFile ReadFile(JsonElement root, ICollection<Issue>? issues)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("scene.json does not contain a scene object.");
        var file = new SceneFile { Name = "Untitled" };
        foreach (var p in root.EnumerateObject())
        {
            switch (p.Name)
            {
                case "formatVersion":
                    if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out int version))
                    {
                        file.FormatVersion = version;
                    }
                    else
                    {
                        Report(issues, IssueSeverity.Warning, ValueInvalidCode, $"The scene format version {Text(p.Value)} is not a number; it was read as {FormatVersion}.");
                    }
                    break;
                case "id":
                    file.Id = ReadValue(p.Value, Guid.Empty, "scene id", issues);
                    break;
                case "name":
                    file.Name = ReadValue(p.Value, "Untitled", "scene name", issues) ?? "Untitled";
                    break;
                case "layers":
                    file.Layers = ReadLayers(p.Value, issues);
                    break;
                case "settings":
                    file.Settings = ReadSettings(p.Value, issues);
                    break;
                case "entities":
                    file.Entities = ReadEntities(p.Value, issues);
                    break;
                default:
                    (file.UnknownProperties ??= new Dictionary<string, JsonElement>()).TryAdd(p.Name, p.Value.Clone());
                    break;
            }
        }
        if (file.FormatVersion > FormatVersion)
            throw new InvalidDataException($"The scene was saved by a newer version (format {file.FormatVersion}, this program reads {FormatVersion}).");
        return file;
    }

    private static List<Layer> ReadLayers(JsonElement array, ICollection<Issue>? issues)
    {
        var layers = new List<Layer>();
        if (array.ValueKind == JsonValueKind.Null) return layers;
        if (array.ValueKind != JsonValueKind.Array)
        {
            Report(issues, IssueSeverity.Warning, LayerInvalidCode, "The layer list is not a list; the scene gets a default layer.");
            return layers;
        }
        foreach (var element in array.EnumerateArray())
        {
            try
            {
                if (element.Deserialize<Layer>(CandelaJson.Options) is { } layer) layers.Add(layer);
                else Report(issues, IssueSeverity.Warning, LayerInvalidCode, "An empty layer was skipped.");
            }
            catch (Exception e) when (IsReadFailure(e))
            {
                Report(issues, IssueSeverity.Warning, LayerInvalidCode, $"A layer could not be read and was skipped: {e.Message}");
            }
        }
        return layers;
    }

    private static SceneSettings? ReadSettings(JsonElement element, ICollection<Issue>? issues)
    {
        if (element.ValueKind == JsonValueKind.Null) return null;
        try
        {
            return element.Deserialize<SceneSettings>(CandelaJson.Options);
        }
        catch (Exception e) when (IsReadFailure(e))
        {
            Report(issues, IssueSeverity.Warning, SettingsInvalidCode, $"The scene settings could not be read; defaults are used: {e.Message}");
            return null;
        }
    }

    private static List<EntityRecord> ReadEntities(JsonElement array, ICollection<Issue>? issues)
    {
        var records = new List<EntityRecord>();
        if (array.ValueKind == JsonValueKind.Null) return records;
        if (array.ValueKind != JsonValueKind.Array)
        {
            Report(issues, IssueSeverity.Error, EntityInvalidCode, "The entity list is not a list; the scene is empty.");
            return records;
        }
        int index = 0;
        foreach (var element in array.EnumerateArray())
        {
            index++;
            if (element.ValueKind != JsonValueKind.Object)
            {
                Report(issues, IssueSeverity.Warning, EntityInvalidCode, $"Entity #{index} is not an object and was skipped.");
                continue;
            }
            records.Add(ReadEntity(element, index, issues));
        }
        return records;
    }

    private static EntityRecord ReadEntity(JsonElement element, int index, ICollection<Issue>? issues)
    {
        var r = new EntityRecord();
        string label = element.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? $"'{n.GetString()}'" : $"Entity #{index}";
        JsonElement? components = null;
        foreach (var p in element.EnumerateObject())
        {
            switch (p.Name)
            {
                case "id":
                    r.Id = ReadValue(p.Value, Guid.Empty, $"id of {label}", issues);
                    break;
                case "parent":
                    r.Parent = ReadValue<Guid?>(p.Value, null, $"parent of {label}", issues);
                    break;
                case "name":
                    r.Name = ReadValue(p.Value, "", $"name of {label}", issues) ?? "";
                    break;
                case "layer":
                    r.Layer = ReadValue(p.Value, Guid.Empty, $"layer of {label}", issues);
                    break;
                case "visible":
                    r.Visible = ReadValue(p.Value, true, $"visibility of {label}", issues);
                    break;
                case "locked":
                    r.Locked = ReadValue(p.Value, false, $"lock of {label}", issues);
                    break;
                case "transform":
                    try
                    {
                        r.Transform = p.Value.ValueKind == JsonValueKind.Null ? Transform.Identity : p.Value.Deserialize<Transform>(CandelaJson.Options);
                    }
                    catch (Exception e) when (IsReadFailure(e))
                    {
                        Report(issues, IssueSeverity.Warning, TransformInvalidCode, $"The placement of {label} could not be read; it was reset: {e.Message}");
                    }
                    break;
                case "components":
                    components = p.Value;
                    break;
                default:
                    (r.UnknownProperties ??= new Dictionary<string, JsonElement>()).TryAdd(p.Name, p.Value.Clone());
                    break;
            }
        }
        if (components is { } list) r.Components = ReadComponents(list, label, r.Id, issues);
        return r;
    }

    private static List<Component> ReadComponents(JsonElement array, string owner, Guid ownerId, ICollection<Issue>? issues)
    {
        var components = new List<Component>();
        if (array.ValueKind == JsonValueKind.Null) return components;
        if (array.ValueKind != JsonValueKind.Array)
        {
            Report(issues, IssueSeverity.Warning, ComponentInvalidCode, $"The component list of {owner} is not a list; it was dropped.", ownerId);
            return components;
        }
        foreach (var element in array.EnumerateArray())
        {
            try
            {
                if (ReadComponent(element) is { } component) components.Add(component);
                else Report(issues, IssueSeverity.Warning, ComponentInvalidCode, $"An empty component of {owner} was dropped.", ownerId);
            }
            catch (Exception e) when (IsReadFailure(e))
            {
                var type = element.ValueKind == JsonValueKind.Object && element.TryGetProperty(TypeDiscriminator, out var t) && t.ValueKind == JsonValueKind.String
                    ? $"'{t.GetString()}' "
                    : "";
                Report(issues, IssueSeverity.Warning, ComponentInvalidCode, $"A {type}component of {owner} could not be read and was dropped: {e.Message}", ownerId);
            }
        }
        return components;
    }

    /// <summary>
    /// One component object, with its <c>"$type"</c> discriminator anywhere among its properties (System.Text.Json reads
    /// the discriminator only as the first property; an object that has it elsewhere is rewritten with it first).
    /// </summary>
    internal static Component? ReadComponent(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return element.Deserialize<Component>(CandelaJson.Options);
        bool first = true;
        bool needsReorder = false;
        foreach (var p in element.EnumerateObject())
        {
            if (p.NameEquals(TypeDiscriminator))
            {
                needsReorder = !first;
                break;
            }
            first = false;
        }
        if (!needsReorder) return element.Deserialize<Component>(CandelaJson.Options);

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WritePropertyName(TypeDiscriminator);
            element.GetProperty(TypeDiscriminator).WriteTo(writer);
            foreach (var p in element.EnumerateObject())
                if (!p.NameEquals(TypeDiscriminator)) p.WriteTo(writer);
            writer.WriteEndObject();
        }
        return JsonSerializer.Deserialize<Component>(buffer.WrittenSpan, CandelaJson.Options);
    }

    private static T ReadValue<T>(JsonElement value, T fallback, string what, ICollection<Issue>? issues)
    {
        try
        {
            return value.Deserialize<T>(CandelaJson.Options) ?? fallback;
        }
        catch (Exception e) when (IsReadFailure(e))
        {
            Report(issues, IssueSeverity.Warning, ValueInvalidCode, $"The {what} ({Text(value)}) could not be read; the default is used.");
            return fallback;
        }
    }

    private static string Text(JsonElement value)
    {
        var raw = value.GetRawText();
        return raw.Length <= 40 ? raw : raw[..40] + "…";
    }

    private static bool IsReadFailure(Exception e) =>
        e is JsonException or NotSupportedException or InvalidOperationException or FormatException or OverflowException or ArgumentException;

    private static bool IsFinite(Transform t) =>
        float.IsFinite(t.Position.X) && float.IsFinite(t.Position.Y) && float.IsFinite(t.Position.Z) &&
        float.IsFinite(t.Rotation.X) && float.IsFinite(t.Rotation.Y) && float.IsFinite(t.Rotation.Z) && float.IsFinite(t.Rotation.W) &&
        float.IsFinite(t.Scale.X) && float.IsFinite(t.Scale.Y) && float.IsFinite(t.Scale.Z);

    private static void Report(ICollection<Issue>? issues, IssueSeverity severity, string code, string message, params Guid[] entities) =>
        issues?.Add(new Issue(severity, IssueCategory.Scene, code, message, entities));
}

/// <summary>The serialisable form of a scene (<c>scene.json</c>), see <see cref="SceneSerializer"/>.</summary>
public sealed class SceneFile
{
    /// <summary>Scene format version (<see cref="SceneSerializer.FormatVersion"/> when written by this program).</summary>
    public int FormatVersion { get; set; } = SceneSerializer.FormatVersion;

    /// <summary>Stable identity of the scene document.</summary>
    public Guid Id { get; set; }

    /// <summary>Display name of the scene.</summary>
    public string Name { get; set; } = "";

    /// <summary>Layers in display order (the first one is the default layer).</summary>
    public List<Layer> Layers { get; set; } = new();

    /// <summary>Scene-wide settings; null = defaults.</summary>
    public SceneSettings? Settings { get; set; }

    /// <summary>Every entity, flat, parents before their children.</summary>
    public List<EntityRecord> Entities { get; set; } = new();

    /// <summary>Top-level properties this version does not know, kept and written back (see <see cref="Scene.UnknownProperties"/>).</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownProperties { get; set; }
}

/// <summary>One entity of a <see cref="SceneFile"/>.</summary>
public sealed class EntityRecord
{
    /// <summary>The entity id (<see cref="Entity.Id"/>).</summary>
    public Guid Id { get; set; }

    /// <summary>Id of the parent entity (listed earlier in the file), or null for a root.</summary>
    public Guid? Parent { get; set; }

    /// <summary>Display name.</summary>
    public string Name { get; set; } = "";

    /// <summary>Id of the entity's layer (<see cref="Entity.LayerId"/>).</summary>
    public Guid Layer { get; set; }

    /// <summary>See <see cref="Entity.Visible"/>.</summary>
    public bool Visible { get; set; } = true;

    /// <summary>See <see cref="Entity.Locked"/>.</summary>
    public bool Locked { get; set; }

    /// <summary>Local transform relative to the parent (metres, quaternion, scale; Z up).</summary>
    public Transform Transform { get; set; } = Transform.Identity;

    /// <summary>Attached components (polymorphic, <c>"$type"</c> discriminator).</summary>
    public List<Component> Components { get; set; } = new();

    /// <summary>Properties of the record this version does not know, kept and written back (see <see cref="Entity.UnknownProperties"/>).</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownProperties { get; set; }
}
