using System.Text.Json;
using Candela.Core.Math;
using Candela.Core.SceneGraph;

namespace Candela.Core.Serialization;

/// <summary>
/// Scene ↔ JSON (the <c>scene.json</c> of a project archive). Entities are written flat, parents before children,
/// so the format is easy to diff and robust to deep trees.
/// </summary>
public static class SceneSerializer
{
    public const int FormatVersion = 1;

    public static string ToJson(Scene scene, bool indented = true) =>
        JsonSerializer.Serialize(ToFile(scene), indented ? CandelaJson.Options : CandelaJson.CompactOptions);

    public static Scene FromJson(string json)
    {
        var file = JsonSerializer.Deserialize<SceneFile>(json, CandelaJson.Options)
                   ?? throw new InvalidDataException("scene.json is empty.");
        return FromFile(file);
    }

    public static SceneFile ToFile(Scene scene) => new()
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
        }).ToList(),
    };

    public static Scene FromFile(SceneFile file)
    {
        if (file.FormatVersion > FormatVersion)
            throw new InvalidDataException($"The scene was saved by a newer version (format {file.FormatVersion}, this program reads {FormatVersion}).");
        var scene = new Scene(file.Name) { Id = file.Id == Guid.Empty ? Guid.NewGuid() : file.Id };
        if (file.Layers.Count > 0)
        {
            scene.Layers.Clear();
            scene.Layers.AddRange(file.Layers);
        }
        scene.Settings = file.Settings ?? new SceneSettings();

        var created = new Dictionary<Guid, Entity>();
        foreach (var r in file.Entities)
        {
            var e = new Entity(r.Name, r.Id)
            {
                LayerId = r.Layer,
                Visible = r.Visible,
                Locked = r.Locked,
            };
            e.Local = r.Transform;
            foreach (var c in r.Components) e.Add(c);
            created[r.Id] = e;
            Entity? parent = r.Parent is { } pid && created.TryGetValue(pid, out var p) ? p : null;
            scene.Add(e, parent);
        }
        return scene;
    }
}

public sealed class SceneFile
{
    public int FormatVersion { get; set; } = SceneSerializer.FormatVersion;
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public List<Layer> Layers { get; set; } = new();
    public SceneSettings? Settings { get; set; }
    public List<EntityRecord> Entities { get; set; } = new();
}

public sealed class EntityRecord
{
    public Guid Id { get; set; }
    public Guid? Parent { get; set; }
    public string Name { get; set; } = "";
    public Guid Layer { get; set; }
    public bool Visible { get; set; } = true;
    public bool Locked { get; set; }
    public Transform Transform { get; set; } = Transform.Identity;
    public List<Component> Components { get; set; } = new();
}
