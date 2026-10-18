using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Candela.Core.Diagnostics;
using Candela.Core.Math;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;
using Candela.Core.Serialization;
using Candela.Core.Settings;

namespace Candela.Core.Tests;

/// <summary>
/// Scene JSON: exact nulls, unknown properties kept for newer versions, "$type" at any position, and tolerant reading
/// that skips damaged elements with issues instead of failing the whole scene.
/// </summary>
public class SerializationTests
{
    [Fact]
    public void A_null_whose_default_is_not_null_survives_files_copies_and_snapshots()
    {
        var scene = new Scene();
        var console = scene.Add(new Entity("Console"));
        console.Add(new ConsoleComponent { ProtocolId = null });
        var fixture = scene.Add(new Entity("Spot"));
        fixture.Add(new FixtureComponent { GdtfMode = null });

        var json = SceneSerializer.ToJson(scene);
        Assert.Contains("\"protocolId\": null", json);
        Assert.DoesNotContain("gdtfMode", json); // a null whose default is null is still omitted
        Assert.DoesNotContain("mountedOn", json);

        var back = SceneSerializer.FromJson(json);
        Assert.Null(back.Find(console.Id)!.Get<ConsoleComponent>()!.ProtocolId);
        Assert.Null(CandelaJson.Deserialize<ConsoleComponent>(CandelaJson.Serialize(new ConsoleComponent { ProtocolId = null }))!.ProtocolId);
        Assert.Null(((ConsoleComponent)console.Get<ConsoleComponent>()!.Clone()).ProtocolId);
        Assert.Equal("artnet", CandelaJson.Deserialize<ConsoleComponent>("""{ "model": "x" }""")!.ProtocolId); // omitted = default
    }

    [Fact]
    public void Unknown_properties_of_a_newer_version_are_kept_at_every_level()
    {
        var scene = new Scene("Show");
        var spot = scene.Add(new Entity("Spot"));
        spot.Add(new FixtureComponent { GdtfSpec = "a.gdtf" });
        var root = JsonNode.Parse(SceneSerializer.ToJson(scene))!.AsObject();
        root["futureScene"] = new JsonObject { ["a"] = 1 };
        root["layers"]![0]!["futureLayer"] = "x";
        root["settings"]!["futureSetting"] = 2.5;
        root["entities"]![0]!["futureEntity"] = new JsonArray(1, 2, 3);
        root["entities"]![0]!["components"]![0]!["futureComponent"] = true;

        var issues = new List<Issue>();
        var back = SceneSerializer.FromJson(root.ToJsonString(), issues);
        Assert.Empty(issues);
        Assert.Equal(1, back.UnknownProperties!["futureScene"].GetProperty("a").GetInt32());
        Assert.Equal("x", back.Layers[0].UnknownProperties!["futureLayer"].GetString());
        Assert.Equal(2.5, back.Settings.UnknownProperties!["futureSetting"].GetDouble());
        Assert.Equal(3, back.Find(spot.Id)!.UnknownProperties!["futureEntity"].GetArrayLength());
        Assert.True(back.Find(spot.Id)!.Get<FixtureComponent>()!.UnknownProperties!["futureComponent"].GetBoolean());

        // Saved again by this version: everything is still there.
        var again = JsonNode.Parse(SceneSerializer.ToJson(back))!;
        Assert.Equal(1, (int)again["futureScene"]!["a"]!);
        Assert.Equal("x", (string)again["layers"]![0]!["futureLayer"]!);
        Assert.Equal(2.5, (double)again["settings"]!["futureSetting"]!);
        Assert.Equal(3, again["entities"]![0]!["futureEntity"]!.AsArray().Count);
        Assert.True((bool)again["entities"]![0]!["components"]![0]!["futureComponent"]!);
        Assert.Equal("fixture", (string)again["entities"]![0]!["components"]![0]!["$type"]!);

        // Through SceneSerializer.FromFile of a deserialised record as well.
        var file = JsonSerializer.Deserialize<SceneFile>(root.ToJsonString(), CandelaJson.Options)!;
        Assert.True(SceneSerializer.FromFile(file).Find(spot.Id)!.UnknownProperties!.ContainsKey("futureEntity"));
    }

    [Fact]
    public void App_settings_and_key_bindings_keep_unknown_properties()
    {
        var path = Path.Combine(Path.GetTempPath(), "candela-settings-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, """{ "uiScale": 1.5, "futureOption": { "on": true } }""");
            var settings = AppSettings.Load(path);
            Assert.Equal(1.5f, settings.UiScale);
            settings.Save(path);
            Assert.True(JsonNode.Parse(File.ReadAllText(path))!["futureOption"]!["on"]!.GetValue<bool>());

            var keys = CandelaJson.Deserialize<KeyBindingSet>("""{ "bindings": { "edit.undo": ["Ctrl+Z"] }, "futureKeys": 1 }""")!;
            Assert.Contains("futureKeys", CandelaJson.Serialize(keys));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void The_type_discriminator_may_appear_anywhere_in_a_component()
    {
        var id = Guid.NewGuid();
        var json = $$"""
            {
              "formatVersion": 1,
              "name": "Hand edited",
              "entities": [
                { "id": "{{id}}", "name": "Wall", "components": [ { "width": 7, "kind": "Floor", "$type": "wall" }, { "$type": "group" } ] }
              ]
            }
            """;
        var scene = SceneSerializer.FromJson(json);
        var wall = scene.Find(id)!.Get<WallComponent>()!;
        Assert.Equal(7f, wall.Width);
        Assert.Equal(WallKind.Floor, wall.Kind);
        Assert.NotNull(scene.Find(id)!.Get<GroupComponent>());
    }

    [Fact]
    public void Damaged_elements_are_skipped_or_repaired_with_issues_and_the_rest_loads()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var json = $$"""
            {
              "formatVersion": 1,
              "id": "not-a-guid",
              "name": "Damaged",
              "layers": [ { "id": "{{Guid.NewGuid()}}", "name": "L", "visible": "maybe" }, 17, { "name": "Good" } ],
              "settings": { "gridSpacing": "wide" },
              "entities": [
                { "id": "{{a}}", "name": "A", "visible": "yes", "transform": { "position": [1, 2] } },
                "not an entity",
                { "id": "{{a}}", "name": "Twin" },
                { "name": "No id" },
                { "id": "{{b}}", "parent": "{{Guid.NewGuid()}}", "name": "Orphan", "transform": { "position": ["NaN", 0, 0] },
                  "components": [ { "$type": "nope" }, { "width": 2 }, null, { "$type": "fixture", "addresses": "x" }, { "$type": "truss", "length": 4 } ] }
              ]
            }
            """;
        var issues = new List<Issue>();
        var scene = SceneSerializer.FromJson(json, issues);

        Assert.Equal("Damaged", scene.Name);
        Assert.NotEqual(Guid.Empty, scene.Id);
        Assert.Equal(new[] { "Good" }, scene.Layers.Select(l => l.Name)); // the layer with an unreadable value and the number are skipped
        Assert.Equal(1f, scene.Settings.GridSpacing);
        Assert.Equal(4, scene.Count);

        var entityA = scene.Find(a)!;
        Assert.Equal("A", entityA.Name);
        Assert.True(entityA.Visible);
        Assert.Equal(Transform.Identity, entityA.Local);
        var twin = scene.All().Single(e => e.Name == "Twin");
        Assert.NotEqual(a, twin.Id);
        Assert.NotEqual(Guid.Empty, scene.All().Single(e => e.Name == "No id").Id);
        var orphan = scene.Find(b)!;
        Assert.Null(orphan.Parent);
        Assert.Equal(Transform.Identity, orphan.Local);
        Assert.Equal(4f, Assert.Single(orphan.Components.OfType<TrussComponent>()).Length);
        Assert.Single(orphan.Components);

        var codes = issues.Select(i => i.Code).ToList();
        Assert.Contains(SceneSerializer.ValueInvalidCode, codes);
        Assert.Contains(SceneSerializer.LayerInvalidCode, codes);
        Assert.Contains(SceneSerializer.SettingsInvalidCode, codes);
        Assert.Contains(SceneSerializer.EntityInvalidCode, codes);
        Assert.Contains(SceneSerializer.EntityIdInvalidCode, codes);
        Assert.Contains(SceneSerializer.ParentMissingCode, codes);
        Assert.Contains(SceneSerializer.TransformInvalidCode, codes);
        Assert.Equal(4, codes.Count(c => c == SceneSerializer.ComponentInvalidCode));
        Assert.All(issues, i => Assert.Equal(IssueCategory.Scene, i.Category));
        Assert.Contains(issues, i => i.Code == SceneSerializer.ComponentInvalidCode && i.Message.Contains("'nope'") && i.Entities.Contains(b));

        // Without an issue list the same damage is tolerated silently.
        Assert.Equal(4, SceneSerializer.FromJson(json).Count);
    }

    [Fact]
    public void Only_unreadable_text_wrong_roots_and_newer_formats_throw()
    {
        Assert.ThrowsAny<JsonException>(() => SceneSerializer.FromJson("{ not json"));
        Assert.Throws<InvalidDataException>(() => SceneSerializer.FromJson("[1, 2]"));
        var newer = Assert.Throws<InvalidDataException>(() => SceneSerializer.FromJson("""{ "formatVersion": 99 }"""));
        Assert.Contains("99", newer.Message);
        Assert.Throws<InvalidDataException>(() => SceneSerializer.FromFile(new SceneFile { FormatVersion = 2 }));

        var empty = SceneSerializer.FromJson("{}");
        Assert.Equal(0, empty.Count);
        Assert.Single(empty.Layers);
        var nulls = SceneSerializer.FromJson("""{ "layers": null, "settings": null, "entities": null, "name": null }""");
        Assert.Equal("Untitled", nulls.Name);
    }

    [Fact]
    public void A_scene_file_record_with_nulls_and_repeated_ids_loads()
    {
        var id = Guid.NewGuid();
        var shared = new GroupComponent();
        var file = new SceneFile
        {
            Name = null!,
            Layers = new List<Layer> { null! },
            Entities = new List<EntityRecord>
            {
                new() { Id = id, Name = "First", Components = { shared } },
                null!,
                new() { Id = id, Name = null!, Components = { null!, shared }, Transform = new Transform(new Vector3(float.PositiveInfinity, 0, 0)) },
                new() { Id = Guid.NewGuid(), Parent = id, Name = "Child", Components = null! },
            },
        };
        var issues = new List<Issue>();
        var scene = SceneSerializer.FromFile(file, issues);

        Assert.Equal(3, scene.Count);
        Assert.Single(scene.Layers);
        var first = scene.Find(id)!;
        Assert.Equal("First", first.Name);
        Assert.Same(first, shared.Owner);
        Assert.Equal("Child", Assert.Single(first.Children).Name);
        var second = scene.Roots.Single(e => e != first);
        Assert.Equal("", second.Name);
        Assert.Empty(second.Components);
        Assert.Equal(Transform.Identity, second.Local);
        Assert.Contains(issues, i => i.Code == SceneSerializer.EntityInvalidCode);
        Assert.Contains(issues, i => i.Code == SceneSerializer.EntityIdInvalidCode && i.Entities.Contains(second.Id));
        Assert.Equal(2, issues.Count(i => i.Code == SceneSerializer.ComponentInvalidCode));
        Assert.Contains(issues, i => i.Code == SceneSerializer.TransformInvalidCode);
    }

    [Fact]
    public void Very_deep_hierarchies_load_and_compute_world_matrices_without_recursion()
    {
        const int depth = 100_000;
        var records = new List<EntityRecord>(depth);
        Guid? parent = null;
        for (int i = 0; i < depth; i++)
        {
            var id = Guid.NewGuid();
            records.Add(new EntityRecord { Id = id, Parent = parent, Name = "n", Transform = new Transform(new Vector3(0, 0, 1)) });
            parent = id;
        }
        var scene = SceneSerializer.FromFile(new SceneFile { Entities = records });
        var leaf = scene.Find(parent!.Value)!;
        Assert.Equal(depth, leaf.WorldPosition.Z); // integer steps are exact in single precision

        // Moving the root invalidates the chain without recursion; reading it again is correct.
        var root = scene.Roots[0];
        root.Local = new Transform(new Vector3(5, 0, 1));
        Assert.Equal(new Vector3(5, 0, depth), leaf.WorldPosition);
        root.Local = new Transform(new Vector3(6, 0, 1));
        root.Local = new Transform(new Vector3(7, 0, 1)); // the chain is already dirty: O(1)
        Assert.Equal(new Vector3(7, 0, depth), leaf.WorldPosition);

        var json = SceneSerializer.ToJson(scene, indented: false);
        Assert.Equal(depth, SceneSerializer.FromJson(json).Count);
    }

    [Fact]
    public void World_matrices_stay_consistent_when_parts_of_the_tree_move_in_any_order()
    {
        var scene = new Scene();
        var a = scene.Add(new Entity("a") { Local = new Transform(new Vector3(1, 0, 0)) });
        var b = scene.Add(new Entity("b") { Local = new Transform(new Vector3(0, 1, 0)) }, a);
        var c = scene.Add(new Entity("c") { Local = new Transform(new Vector3(0, 0, 1)) }, b);
        var d = scene.Add(new Entity("d") { Local = new Transform(new Vector3(0, 0, 2)) }, b);
        Assert.Equal(new Vector3(1, 1, 1), c.WorldPosition);

        b.Local = new Transform(new Vector3(0, 2, 0)); // c, d dirty
        a.Local = new Transform(new Vector3(3, 0, 0)); // a dirty again; b already dirty
        Assert.Equal(new Vector3(3, 2, 2), d.WorldPosition);
        Assert.Equal(new Vector3(3, 2, 1), c.WorldPosition);

        a.Local = new Transform(new Vector3(4, 0, 0));
        Assert.Equal(new Vector3(4, 2, 0), b.WorldPosition); // computed via b only; c and d stay dirty
        Assert.Equal(new Vector3(4, 2, 1), c.WorldPosition);
        scene.Reparent(d, null);
        Assert.Equal(new Vector3(4, 2, 2), d.WorldPosition);
        scene.Reparent(c, d, keepWorldTransform: false);
        Assert.Equal(new Vector3(4, 2, 3), c.WorldPosition);

        // Moving and reading world matrices does not allocate (a detached chain: no scene changes are raised).
        var top = new Entity("top");
        var leaf = top;
        for (int i = 0; i < 50; i++) leaf = leaf.AddChild(new Entity("n") { Local = new Transform(Vector3.UnitZ) });
        _ = leaf.WorldMatrix; // warm up the per-thread scratch list
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            top.Local = new Transform(new Vector3(i, 0, 0));
            _ = leaf.WorldMatrix;
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(new Vector3(999, 0, 50), leaf.WorldPosition);
    }
}
