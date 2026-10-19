using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;

namespace Candela.Project.Tests;

/// <summary>
/// Candela.Project on Core's shared machinery: properties of newer versions survive load and save (Core
/// <c>UnknownProperties</c>), merge imports copy with Core's <see cref="EntityCloner"/>, scene files use Core's JSON
/// options, and layer changes rely on Core's own change events.
/// </summary>
public sealed class CoreAlignmentTests
{
    private static byte[] AddFutureProperties(byte[] sceneJson, Guid entityId)
    {
        var root = JsonNode.Parse(sceneJson)!.AsObject();
        root["futureScene"] = new JsonObject { ["mode"] = "x", ["level"] = 3 };
        var layer = root["layers"]!.AsArray()[0]!.AsObject();
        layer["futureLayer"] = "tagged";
        root["settings"] ??= new JsonObject();
        root["settings"]!.AsObject()["futureSetting"] = true;
        foreach (var node in root["entities"]!.AsArray())
        {
            var entity = node!.AsObject();
            if (entity["id"]!.GetValue<string>() != entityId.ToString("D")) continue;
            entity["futureEntity"] = new JsonArray(1, 2, 3);
            entity["components"]!.AsArray()[0]!.AsObject()["futureField"] = "kept";
        }
        return Encoding.UTF8.GetBytes(root.ToJsonString());
    }

    [Fact]
    public void Properties_of_newer_versions_survive_loading_and_saving()
    {
        using var dir = new TempDir();
        var project = new ProjectData(new Scene("Future"));
        var wall = new Entity("Back wall");
        wall.Add(new WallComponent { Width = 6f, Height = 4f });
        project.Scene.Add(wall);
        string path = dir.File("future.candela");
        ProjectFile.Save(path, project);
        TestData.RewriteArchive(path, (name, data) => name == "scene.json" ? AddFutureProperties(data, wall.Id) : data);

        var loaded = ProjectFile.Load(path);
        Assert.Equal(3, loaded.Scene.UnknownProperties!["futureScene"].GetProperty("level").GetInt32());
        Assert.Equal("tagged", loaded.Scene.Layers[0].UnknownProperties!["futureLayer"].GetString());
        Assert.True(loaded.Scene.Settings.UnknownProperties!["futureSetting"].GetBoolean());
        var loadedWall = loaded.Scene.Find(wall.Id)!;
        Assert.Equal(3, loadedWall.UnknownProperties!["futureEntity"].GetArrayLength());
        Assert.Equal("kept", loadedWall.Get<WallComponent>()!.UnknownProperties!["futureField"].GetString());

        // saved again by this version: everything is still there
        string again = dir.File("again.candela");
        ProjectFile.Save(again, loaded);
        var reloaded = ProjectFile.Load(again);
        Assert.Equal("x", reloaded.Scene.UnknownProperties!["futureScene"].GetProperty("mode").GetString());
        Assert.Equal("tagged", reloaded.Scene.Layers[0].UnknownProperties!["futureLayer"].GetString());
        Assert.True(reloaded.Scene.Settings.UnknownProperties!["futureSetting"].GetBoolean());
        Assert.Equal(3, reloaded.Scene.Find(wall.Id)!.UnknownProperties!["futureEntity"].GetArrayLength());
        Assert.Equal("kept", reloaded.Scene.Find(wall.Id)!.Get<WallComponent>()!.UnknownProperties!["futureField"].GetString());
        Assert.DoesNotContain(reloaded.LoadIssues, i => i.Severity >= Core.Diagnostics.IssueSeverity.Warning);
    }

    [Fact]
    public void A_merge_copies_with_core_including_properties_of_newer_versions()
    {
        var source = new ProjectData(new Scene("Source"));
        var truss = new Entity("Truss");
        truss.Add(new TrussComponent());
        truss.UnknownProperties = new Dictionary<string, JsonElement> { ["futureEntity"] = JsonDocument.Parse("42").RootElement.Clone() };
        source.Scene.Add(truss);
        var head = new Entity("Head");
        head.Add(new FixtureComponent { MountedOn = truss.Id, GdtfSpec = "A@B@C.gdtf" });
        head.Get<FixtureComponent>()!.UnknownProperties = new Dictionary<string, JsonElement> { ["futureField"] = JsonDocument.Parse("\"x\"").RootElement.Clone() };
        source.Scene.Add(head, truss);
        source.Scene.Layers.Add(new Layer { Name = "Rig" });
        head.LayerId = source.Scene.Layers[^1].Id;

        var target = new ProjectData(new Scene("Target"));
        var changes = new List<SceneChangeKind>();
        target.Scene.Changed += c => changes.Add(c.Kind);
        var command = ProjectMerge.CreateCommand(target, source, new ProjectMergeOptions { GroupName = "Imported" });
        Assert.Equal("Import Imported", command.Description);
        command.Do();
        var result = command.Result;

        var trussCopy = target.Scene.Find(result.EntityIds[truss.Id])!;
        var headCopy = target.Scene.Find(result.EntityIds[head.Id])!;
        Assert.Equal(42, trussCopy.UnknownProperties!["futureEntity"].GetInt32());
        Assert.Equal("x", headCopy.Get<FixtureComponent>()!.UnknownProperties!["futureField"].GetString());
        Assert.Equal(trussCopy.Id, headCopy.Get<FixtureComponent>()!.MountedOn);
        Assert.Equal(result.AddedLayers.Single().Id, headCopy.LayerId);
        Assert.Equal(target.Scene.DefaultLayer.Id, trussCopy.LayerId); // no layer: the group's
        Assert.Contains(SceneChangeKind.LayersChanged, changes); // the layer collection reports itself
        Assert.DoesNotContain(SceneChangeKind.SettingsChanged, changes);

        // the undo text was fixed when the command was made
        result.Group.Name = "Renamed";
        Assert.Equal("Import Imported", command.Description);
        changes.Clear();
        command.Undo();
        Assert.DoesNotContain(target.Scene.Layers, l => l.Name == "Rig");
        Assert.Contains(SceneChangeKind.LayersChanged, changes);
        Assert.DoesNotContain(SceneChangeKind.SettingsChanged, changes);
    }
}
