using System.Text.Json.Nodes;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;
using Candela.Core.Serialization;

namespace Candela.Core.Tests;

/// <summary><see cref="PowerSourceComponent.MainsInConnector"/>: optional, saved when set, absent in older projects.</summary>
public class PowerSourceInletTests
{
    private static Scene SceneWith(PowerSourceComponent source)
    {
        var scene = new Scene("Inlet");
        var entity = new Entity("Distro");
        entity.Add(source);
        scene.Add(entity);
        return scene;
    }

    [Fact]
    public void The_inlet_connector_round_trips_and_a_project_saved_before_it_existed_loads_with_null()
    {
        var saved = SceneSerializer.ToJson(SceneWith(new PowerSourceComponent { Phases = 3, MainsInConnector = "CEE 32A 3P" }));
        var loaded = SceneSerializer.FromJson(saved).All().Single().Get<PowerSourceComponent>()!;
        Assert.Equal("CEE 32A 3P", loaded.MainsInConnector);
        Assert.Equal(3, loaded.Phases);

        // the same project as an older version wrote it: no such property anywhere
        var old = JsonNode.Parse(saved)!;
        int removed = Strip(old, "mainsInConnector");
        Assert.Equal(1, removed);
        var legacy = SceneSerializer.FromJson(old.ToJsonString()).All().Single().Get<PowerSourceComponent>()!;
        Assert.Null(legacy.MainsInConnector);
        Assert.Single(legacy.Outlets);

        // unset, nothing is written (older readers see the file they know)
        Assert.DoesNotContain("mainsInConnector", SceneSerializer.ToJson(SceneWith(new PowerSourceComponent())), StringComparison.Ordinal);
    }

    private static int Strip(JsonNode node, string name)
    {
        int count = 0;
        if (node is JsonObject o)
        {
            if (o.Remove(name)) count++;
            foreach (var (_, child) in o.ToList())
                if (child is not null) count += Strip(child, name);
        }
        else if (node is JsonArray a)
        {
            foreach (var child in a)
                if (child is not null) count += Strip(child, name);
        }
        return count;
    }
}
