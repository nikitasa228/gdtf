using System.Numerics;
using System.Text.Json;
using Candela.Core.Diagnostics;
using Candela.Core.Math;
using Candela.Core.SceneGraph;
using Candela.Core.Serialization;

namespace Candela.Project.Internal;

/// <summary>
/// Reads <c>scene.json</c> (written by Core's <see cref="SceneSerializer"/>) element by element, so one damaged entity,
/// component or layer is reported and skipped instead of failing the whole project. The validated records are handed
/// to <see cref="SceneSerializer.FromFile"/>, so the scene is built exactly the way Core builds it.
/// Repairs: missing or repeated ids get new ids, missing parents and parent cycles put entities at the root, records are
/// reordered parents-first, invalid or non-finite transforms become identity, unknown component types (from newer
/// versions) are dropped, explicit nulls in required values are replaced by defaults, entities on unknown layers move
/// to their parent's (or the default) layer.
/// </summary>
internal static class SceneJsonReader
{
    public static Scene Read(byte[] json, IssueCollector issues)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, ProjectSettings.ReadOptions);
        }
        catch (JsonException e)
        {
            throw new ProjectFormatException($"The scene (scene.json) is damaged and cannot be read: {e.Message}", e);
        }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new ProjectFormatException("The scene (scene.json) does not contain a scene.");

            if (root.TryGetProperty("formatVersion", out var fv) && fv.ValueKind == JsonValueKind.Number && fv.TryGetInt32(out int version) &&
                version > SceneSerializer.FormatVersion)
            {
                throw new ProjectVersionException(
                    $"The scene was saved by a newer version of Candela (scene format {version}; this version reads format {SceneSerializer.FormatVersion}). Update Candela to open it.",
                    version, SceneSerializer.FormatVersion);
            }

            var file = new SceneFile
            {
                FormatVersion = SceneSerializer.FormatVersion,
                Id = ReadGuid(root, "id") ?? Guid.Empty,
                Name = ReadString(root, "name") ?? "Untitled",
                Layers = ReadLayers(root, issues),
                Settings = ReadSceneSettings(root, issues),
            };
            var records = ReadEntities(root, issues);
            CheckLayers(records, file.Layers, issues);
            file.Entities = OrderParentsFirst(records, issues);
            return SceneSerializer.FromFile(file);
        }
    }

    private static List<Layer> ReadLayers(JsonElement root, IssueCollector issues)
    {
        var layers = new List<Layer>();
        if (!root.TryGetProperty("layers", out var array) || array.ValueKind != JsonValueKind.Array) return layers;
        var ids = new HashSet<Guid>();
        foreach (var element in array.EnumerateArray())
        {
            Layer? layer = null;
            try
            {
                layer = element.Deserialize<Layer>(CandelaJson.Options);
            }
            catch (Exception e) when (IsDataError(e))
            {
                issues.Add(IssueSeverity.Warning, IssueCategory.Scene, ProjectIssueCodes.SceneLayerInvalid, $"A layer could not be read and was skipped: {e.Message}");
                continue;
            }
            if (layer is null || layer.Id == Guid.Empty || !ids.Add(layer.Id))
            {
                issues.Add(IssueSeverity.Warning, IssueCategory.Scene, ProjectIssueCodes.SceneLayerInvalid, "A layer without a valid, unique id was skipped.");
                continue;
            }
            if (NullRepair.Repair(layer) > 0 && layer.Name.Length == 0) layer.Name = "Layer";
            layers.Add(layer);
        }
        return layers;
    }

    private static SceneSettings ReadSceneSettings(JsonElement root, IssueCollector issues)
    {
        if (!root.TryGetProperty("settings", out var element) || element.ValueKind == JsonValueKind.Null) return new SceneSettings();
        try
        {
            var settings = element.Deserialize<SceneSettings>(CandelaJson.Options);
            if (settings is not null) return settings;
        }
        catch (Exception e) when (IsDataError(e))
        {
            issues.Add(IssueSeverity.Warning, IssueCategory.Scene, ProjectIssueCodes.SceneSettingsInvalid,
                $"The scene settings could not be read; defaults are used: {e.Message}");
            return new SceneSettings();
        }
        return new SceneSettings();
    }

    private static List<EntityRecord> ReadEntities(JsonElement root, IssueCollector issues)
    {
        var records = new List<EntityRecord>();
        if (!root.TryGetProperty("entities", out var array)) return records;
        if (array.ValueKind != JsonValueKind.Array)
        {
            issues.Add(IssueSeverity.Error, IssueCategory.Scene, ProjectIssueCodes.SceneEntityInvalid, "The scene's entity list is damaged; the scene is empty.");
            return records;
        }
        var ids = new HashSet<Guid>();
        int index = 0;
        foreach (var element in array.EnumerateArray())
        {
            index++;
            if (element.ValueKind != JsonValueKind.Object)
            {
                issues.Add(IssueSeverity.Warning, IssueCategory.Scene, ProjectIssueCodes.SceneEntityInvalid, $"Entity #{index} is not an object and was skipped.");
                continue;
            }
            var name = ReadString(element, "name") ?? "";
            var id = ReadGuid(element, "id") ?? Guid.Empty;
            if (id == Guid.Empty || !ids.Add(id))
            {
                var reason = id == Guid.Empty ? "has no valid id" : $"repeats the id {id:D}";
                id = Guid.NewGuid();
                ids.Add(id);
                issues.Add(IssueSeverity.Warning, IssueCategory.Scene, ProjectIssueCodes.SceneIdInvalid, $"Entity '{name}' {reason}; it was given a new id.", id);
            }
            var record = new EntityRecord
            {
                Id = id,
                Parent = ReadGuid(element, "parent"),
                Name = name,
                Layer = ReadGuid(element, "layer") ?? Guid.Empty,
                Visible = ReadBool(element, "visible") ?? true,
                Locked = ReadBool(element, "locked") ?? false,
                Transform = ReadTransform(element, name, id, issues),
                Components = ReadComponents(element, name, id, issues),
            };
            records.Add(record);
        }
        return records;
    }

    private static Transform ReadTransform(JsonElement entity, string name, Guid id, IssueCollector issues)
    {
        if (!entity.TryGetProperty("transform", out var element) || element.ValueKind == JsonValueKind.Null) return Transform.Identity;
        Transform t;
        try
        {
            t = element.Deserialize<Transform>(CandelaJson.Options);
        }
        catch (Exception e) when (IsDataError(e))
        {
            issues.Add(IssueSeverity.Warning, IssueCategory.Scene, ProjectIssueCodes.SceneTransformInvalid,
                $"The placement of '{name}' could not be read; it was reset: {e.Message}", id);
            return Transform.Identity;
        }
        if (!IsFinite(t.Position) || !IsFinite(t.Scale) || !IsFinite(t.Rotation))
        {
            issues.Add(IssueSeverity.Warning, IssueCategory.Scene, ProjectIssueCodes.SceneTransformInvalid,
                $"The placement of '{name}' contains invalid numbers; it was reset.", id);
            return Transform.Identity;
        }
        float length = t.Rotation.Length();
        if (length < 1e-6f)
        {
            issues.Add(IssueSeverity.Warning, IssueCategory.Scene, ProjectIssueCodes.SceneTransformInvalid,
                $"The rotation of '{name}' is degenerate; it was reset.", id);
            t.Rotation = Quaternion.Identity;
        }
        else if (MathF.Abs(length - 1f) > 1e-3f)
        {
            // Keep exact values of valid files (round trip), normalise only what is clearly not a rotation.
            t.Rotation = Quaternion.Normalize(t.Rotation);
        }
        return t;
    }

    private static List<Component> ReadComponents(JsonElement entity, string name, Guid id, IssueCollector issues)
    {
        var list = new List<Component>();
        if (!entity.TryGetProperty("components", out var array) || array.ValueKind != JsonValueKind.Array) return list;
        foreach (var element in array.EnumerateArray())
        {
            string? type = element.ValueKind == JsonValueKind.Object ? ReadString(element, "$type") : null;
            try
            {
                var component = element.Deserialize<Component>(CandelaJson.Options);
                if (component is null) continue;
                if (NullRepair.Repair(component) > 0)
                {
                    issues.Add(IssueSeverity.Note, IssueCategory.Scene, ProjectIssueCodes.SceneValueRepaired,
                        $"Missing values in a {type ?? component.GetType().Name} component of '{name}' were replaced by defaults.", id);
                }
                list.Add(component);
            }
            catch (NotSupportedException) when (type is not null)
            {
                issues.Add(IssueSeverity.Warning, IssueCategory.Scene, ProjectIssueCodes.SceneComponentUnknown,
                    $"'{name}' has a component of type '{type}' that this version of Candela does not know (saved by a newer version?); it was dropped.", id);
            }
            catch (Exception e) when (IsDataError(e))
            {
                issues.Add(IssueSeverity.Warning, IssueCategory.Scene, ProjectIssueCodes.SceneComponentInvalid,
                    $"A {type ?? "component"} of '{name}' could not be read and was dropped: {e.Message}", id);
            }
        }
        return list;
    }

    private static void CheckLayers(List<EntityRecord> records, List<Layer> layers, IssueCollector issues)
    {
        if (records.Count == 0) return;
        var known = new HashSet<Guid>();
        foreach (var l in layers) known.Add(l.Id);
        int moved = 0;
        foreach (var r in records)
        {
            if (r.Layer == Guid.Empty || known.Contains(r.Layer)) continue;
            // An empty layer id makes Scene.Add use the parent's layer (or the default layer at the root).
            r.Layer = Guid.Empty;
            moved++;
        }
        if (moved > 0 && layers.Count > 0)
        {
            issues.Add(IssueSeverity.Note, IssueCategory.Scene, ProjectIssueCodes.SceneLayerMissing,
                $"{moved} object(s) were on layers that do not exist; they were put on their parent's or the default layer.");
        }
    }

    /// <summary>
    /// Returns the records ordered so that every parent precedes its children (depth first, children in file order).
    /// Parents that do not exist become null (root); cycles are broken at the first record of the cycle in file order.
    /// Iterative, so arbitrarily deep trees cannot overflow the stack.
    /// </summary>
    private static List<EntityRecord> OrderParentsFirst(List<EntityRecord> records, IssueCollector issues)
    {
        var byId = new Dictionary<Guid, EntityRecord>(records.Count);
        foreach (var r in records) byId[r.Id] = r;
        var children = new Dictionary<Guid, List<EntityRecord>>();
        var roots = new List<EntityRecord>();
        foreach (var r in records)
        {
            if (r.Parent is { } pid && pid != r.Id && byId.ContainsKey(pid))
            {
                if (!children.TryGetValue(pid, out var list)) children[pid] = list = new List<EntityRecord>();
                list.Add(r);
                continue;
            }
            if (r.Parent is not null)
            {
                issues.Add(IssueSeverity.Warning, IssueCategory.Scene, ProjectIssueCodes.SceneParentMissing,
                    $"The parent of '{r.Name}' does not exist; it was put at the scene root.", r.Id);
                r.Parent = null;
            }
            roots.Add(r);
        }

        var ordered = new List<EntityRecord>(records.Count);
        var visited = new HashSet<Guid>();
        var stack = new Stack<EntityRecord>();

        void Walk(EntityRecord start)
        {
            stack.Push(start);
            while (stack.Count > 0)
            {
                var r = stack.Pop();
                if (!visited.Add(r.Id)) continue;
                ordered.Add(r);
                if (!children.TryGetValue(r.Id, out var kids)) continue;
                for (int i = kids.Count - 1; i >= 0; i--) stack.Push(kids[i]);
            }
        }

        foreach (var r in roots) Walk(r);
        if (ordered.Count < records.Count)
        {
            foreach (var r in records)
            {
                if (visited.Contains(r.Id)) continue;
                issues.Add(IssueSeverity.Warning, IssueCategory.Scene, ProjectIssueCodes.SceneParentCycle,
                    $"'{r.Name}' was part of a parent cycle; it was moved to the scene root.", r.Id);
                r.Parent = null;
                Walk(r);
            }
        }
        return ordered;
    }

    private static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    private static bool IsFinite(Quaternion q) => float.IsFinite(q.X) && float.IsFinite(q.Y) && float.IsFinite(q.Z) && float.IsFinite(q.W);

    internal static bool IsDataError(Exception e) =>
        e is JsonException or NotSupportedException or InvalidOperationException or FormatException or ArgumentException or OverflowException
            or InvalidCastException;

    internal static string? ReadString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    internal static Guid? ReadGuid(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && Guid.TryParse(v.GetString(), out var g) ? g : null;

    internal static bool? ReadBool(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        } : null;
}
