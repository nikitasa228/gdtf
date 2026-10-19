using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;
using Candela.Core.Serialization;
using Candela.Gdtf;
using Candela.Mvr;

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

    /// <summary>
    /// Checks that the merge copied every component exactly, references translated: the original's JSON with every id
    /// replaced by its translation (entities, assets, cable nodes by position, renamed GDTFs) equals the copy's JSON. A
    /// reference the copy forgot to translate (or changed without reason) makes them differ.
    /// </summary>
    private static int AssertCopiedExactly(Scene sourceScene, ProjectMergeResult result, Scene targetScene)
    {
        int compared = 0;
        foreach (var original in sourceScene.All().ToList())
        {
            if (!result.EntityIds.TryGetValue(original.Id, out var copyId)) continue;
            var copy = targetScene.Find(copyId)!;
            Assert.Equal(original.Components.Count, copy.Components.Count);
            for (int i = 0; i < original.Components.Count; i++)
            {
                var a = original.Components[i];
                var b = copy.Components[i];
                var text = CandelaJson.SerializeComponent(a);
                foreach (var (from, to) in result.EntityIds) text = text.Replace(from.ToString("D"), to.ToString("D"), StringComparison.OrdinalIgnoreCase);
                foreach (var (from, to) in result.AssetIds) text = text.Replace(from.ToString("D"), to.ToString("D"), StringComparison.OrdinalIgnoreCase);
                if (a is CableComponent ca && b is CableComponent cb)
                {
                    Assert.Equal(ca.Nodes.Count, cb.Nodes.Count);
                    for (int n = 0; n < ca.Nodes.Count; n++) text = text.Replace(ca.Nodes[n].Id.ToString("D"), cb.Nodes[n].Id.ToString("D"), StringComparison.OrdinalIgnoreCase);
                }
                if (a is FixtureComponent fa && result.GdtfNames.TryGetValue(fa.GdtfSpec, out var renamed) && renamed != fa.GdtfSpec)
                    text = text.Replace(JsonSerializer.Serialize(fa.GdtfSpec), JsonSerializer.Serialize(renamed), StringComparison.Ordinal);
                Assert.Equal(text, CandelaJson.SerializeComponent(b));
                compared++;
            }
        }
        return compared;
    }

    [Fact]
    public void A_merge_copies_every_component_of_a_rich_project_exactly_with_translated_ids()
    {
        var target = TestData.RichProject();
        // the same project again: every entity id, asset id and node id collides and must be translated
        using var dir = new TempDir();
        string path = dir.File("rich.candela");
        ProjectFile.Save(path, target, new ProjectSaveOptions { GdtfProvider = TestData.LibraryGdtf });
        var source = ProjectFile.Load(path);
        var result = ProjectMerge.Import(target, source, new ProjectMergeOptions { ReuseIdenticalAssets = false });
        Assert.All(result.AssetIds, kv => Assert.NotEqual(kv.Key, kv.Value));
        Assert.True(AssertCopiedExactly(source.Scene, result, target.Scene) >= source.Scene.All().Sum(e => e.Components.Count));
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void Merging_the_real_fokina_scene_into_itself_copies_every_component_exactly()
    {
        var library = FixtureLibrary.Scan(TestData.RepoRoot);
        var import = MvrImporter.ImportFile(TestData.FokinaMvr, new MvrImportOptions { Library = library });
        var project = new ProjectData(import.Scene);
        var snapshot = SceneSerializer.ToJson(project.Scene);
        var source = new ProjectData(SceneSerializer.FromJson(snapshot, null));
        var result = ProjectMerge.Import(project, source);
        Assert.Empty(result.Issues);
        Assert.True(AssertCopiedExactly(source.Scene, result, project.Scene) >= 91);
        // the originals are untouched
        Assert.Equal(snapshot, SceneSerializer.ToJson(SceneWithout(project.Scene, result.Group)));
    }

    private static Scene SceneWithout(Scene scene, Entity group)
    {
        var copy = SceneSerializer.FromJson(SceneSerializer.ToJson(scene), null);
        copy.Remove(copy.Find(group.Id)!);
        return copy;
    }

    /// <summary>The options the removed <c>ProjectJson</c> wrote <c>scene.json</c> with (kept here to prove byte compatibility).</summary>
    private static JsonSerializerOptions FormerProjectJsonOptions()
    {
        var options = new JsonSerializerOptions(CandelaJson.Options)
        {
            TypeInfoResolver = new DefaultJsonTypeInfoResolver
            {
                Modifiers =
                {
                    static info =>
                    {
                        if (info.Kind != JsonTypeInfoKind.Object || info.CreateObject is null || info.Type.IsAbstract) return;
                        object sample;
                        try
                        {
                            sample = info.CreateObject();
                        }
                        catch (Exception e) when (e is InvalidOperationException or NotSupportedException or MissingMethodException)
                        {
                            return;
                        }
                        foreach (var property in info.Properties)
                        {
                            if (property.Get is null) continue;
                            var type = property.PropertyType;
                            if (type.IsValueType && Nullable.GetUnderlyingType(type) is null) continue;
                            if (property.Get(sample) is not null) property.ShouldSerialize = static (_, _) => true;
                        }
                    },
                },
            },
        };
        options.MakeReadOnly();
        return options;
    }

    [Fact]
    public void Scene_files_are_byte_identical_to_the_ones_the_former_project_options_wrote()
    {
        var project = TestData.RichProject();
        foreach (var e in project.Scene.All())
            foreach (var c in e.Components)
                if (c is ConsoleComponent console) console.ProtocolId = null; // a null that differs from the default
        var file = SceneSerializer.ToFile(project.Scene);
        var former = JsonSerializer.SerializeToUtf8Bytes(file, FormerProjectJsonOptions());
        var now = JsonSerializer.SerializeToUtf8Bytes(file, CandelaJson.Options);
        Assert.Equal(Encoding.UTF8.GetString(former), Encoding.UTF8.GetString(now));
    }
}
