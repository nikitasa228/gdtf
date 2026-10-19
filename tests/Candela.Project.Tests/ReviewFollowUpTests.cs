using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;
using Candela.Core.Serialization;

namespace Candela.Project.Tests;

/// <summary>
/// Review of the Core-alignment follow-ups: data nested as deep as the project formats allow (<see cref="CandelaJson.MaxDepth"/>)
/// survives every path through load and save, and merged layers keep the properties of newer versions.
/// </summary>
public sealed class ReviewFollowUpTests
{
    /// <summary>A JSON value nested <paramref name="depth"/> levels deep (arrays of arrays … of 1).</summary>
    private static string Nested(int depth) => new string('[', depth) + "1" + new string(']', depth);

    private static int DepthOf(JsonElement e)
    {
        int depth = 0;
        while (e.ValueKind == JsonValueKind.Array)
        {
            depth++;
            e = e[0];
        }
        return depth;
    }

    [Fact]
    public void Components_of_newer_versions_nested_deeper_than_64_levels_are_saved_again()
    {
        using var dir = new TempDir();
        var project = new ProjectData(new Scene("Deep"));
        var wall = new Entity("Wall");
        wall.Add(new WallComponent());
        project.Scene.Add(wall);
        string path = dir.File("deep.candela");
        ProjectFile.Save(path, project);
        TestData.RewriteArchive(path, (name, data) =>
        {
            if (name != "scene.json") return data;
            var root = JsonNode.Parse(data)!.AsObject();
            var entity = root["entities"]!.AsArray()[0]!.AsObject();
            // a component type this version does not know, kept verbatim, holding deep data
            entity["components"]!.AsArray().Add(JsonNode.Parse($$"""{"$type":"hologram","shape":{{Nested(120)}}}""",
                null, new JsonDocumentOptions { MaxDepth = 512 }));
            // an entity property of a newer version, as deep
            entity["futureData"] = JsonNode.Parse(Nested(120), null, new JsonDocumentOptions { MaxDepth = 512 });
            return Encoding.UTF8.GetBytes(root.ToJsonString(new JsonSerializerOptions { MaxDepth = 512 }));
        });

        var loaded = ProjectFile.Load(path);
        Assert.Equal(120, DepthOf(loaded.Scene.Find(wall.Id)!.UnknownProperties!["futureData"]));

        string again = dir.File("again.candela");
        ProjectFile.Save(again, loaded);
        var reloaded = ProjectFile.Load(again);
        Assert.Equal(120, DepthOf(reloaded.Scene.Find(wall.Id)!.UnknownProperties!["futureData"]));
        var kept = Assert.Single(reloaded.UnknownComponents[wall.Id]);
        Assert.Equal(120, DepthOf(kept.GetProperty("shape")));

        // the autosave path writes the same scene
        var snapshot = ProjectSnapshot.Capture(reloaded);
        Assert.NotNull(snapshot);
    }

    [Fact]
    public void Hand_edited_components_with_the_type_last_and_deep_data_load()
    {
        using var dir = new TempDir();
        var project = new ProjectData(new Scene("Edited"));
        var wall = new Entity("Wall");
        wall.Add(new WallComponent { Width = 2f });
        project.Scene.Add(wall);
        string path = dir.File("edited.candela");
        ProjectFile.Save(path, project);
        TestData.RewriteArchive(path, (name, data) =>
        {
            if (name != "scene.json") return data;
            var root = JsonNode.Parse(data)!.AsObject();
            var entity = root["entities"]!.AsArray()[0]!.AsObject();
            var components = entity["components"]!.AsArray();
            components.Clear();
            components.Add(JsonNode.Parse($$"""{"width":3.5,"futureShape":{{Nested(100)}},"$type":"wall"}""",
                null, new JsonDocumentOptions { MaxDepth = 512 }));
            return Encoding.UTF8.GetBytes(root.ToJsonString(new JsonSerializerOptions { MaxDepth = 512 }));
        });

        var loaded = ProjectFile.Load(path);
        var loadedWall = loaded.Scene.Find(wall.Id)!.Get<WallComponent>()!;
        Assert.Equal(3.5f, loadedWall.Width);
    }

    [Fact]
    public void Merged_layers_keep_the_properties_of_newer_versions()
    {
        var source = new ProjectData(new Scene("Source"));
        var layer = new Layer
        {
            Name = "Rig",
            UnknownProperties = new Dictionary<string, JsonElement> { ["colour"] = JsonDocument.Parse("\"#ff0000\"").RootElement.Clone() },
        };
        source.Scene.Layers.Add(layer);
        var truss = new Entity("Truss") { LayerId = layer.Id };
        truss.Add(new TrussComponent());
        source.Scene.Add(truss);

        var target = new ProjectData(new Scene("Target"));
        var command = ProjectMerge.CreateCommand(target, source);
        command.Do();
        var added = Assert.Single(command.Result.AddedLayers);
        Assert.Equal("#ff0000", added.UnknownProperties!["colour"].GetString());
        Assert.NotSame(layer.UnknownProperties, added.UnknownProperties);
    }
}
