using System.Text.Json.Nodes;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;
using Candela.Core.Serialization;

namespace Candela.Core.Tests;

/// <summary>
/// The lists of the cabling components are never null, also when a damaged project writes <c>null</c> for them, so the
/// cable, power and data code that walks them cannot fail on one bad element.
/// </summary>
public class CablingComponentRobustnessTests
{
    [Theory]
    [InlineData("nodes")]
    [InlineData("segments")]
    [InlineData("endpoints")]
    [InlineData("outputs")]
    [InlineData("outlets")]
    public void A_null_list_in_a_damaged_project_loads_as_an_empty_list(string property)
    {
        var scene = new Scene("Damaged");
        var cable = new Entity("Cable");
        cable.Add(new CableComponent { CableTypeId = "dmx-xlr5", Nodes = { new CableNode() }, Endpoints = { new CableEndpoint() } });
        cable.Get<CableComponent>()!.Segments.Add(new CableSegment(Guid.NewGuid(), Guid.NewGuid()));
        scene.Add(cable);
        var desk = new Entity("Desk");
        desk.Add(new ConsoleComponent());
        scene.Add(desk);
        var distro = new Entity("Distro");
        distro.Add(new PowerSourceComponent());
        scene.Add(distro);

        var json = JsonNode.Parse(SceneSerializer.ToJson(scene))!;
        Assert.Equal(1, SetNull(json, property));
        var loaded = SceneSerializer.FromJson(json.ToJsonString());

        var c = loaded.All().Single(e => e.Name == "Cable").Get<CableComponent>()!;
        var k = loaded.All().Single(e => e.Name == "Desk").Get<ConsoleComponent>()!;
        var p = loaded.All().Single(e => e.Name == "Distro").Get<PowerSourceComponent>()!;
        Assert.NotNull(c.Nodes);
        Assert.NotNull(c.Segments);
        Assert.NotNull(c.Endpoints);
        Assert.NotNull(k.Outputs);
        Assert.NotNull(p.Outlets);
        int count = property switch
        {
            "nodes" => c.Nodes.Count,
            "segments" => c.Segments.Count,
            "endpoints" => c.Endpoints.Count,
            "outputs" => k.Outputs.Count,
            _ => p.Outlets.Count,
        };
        Assert.Equal(0, count);

        // assigning null in code is the same as assigning an empty list; the others keep their values
        c.Nodes = null!;
        p.Outlets = null!;
        Assert.Empty(c.Nodes);
        Assert.Empty(p.Outlets);
        Assert.Equal(4, new ConsoleComponent().Outputs.Count);
        Assert.Single(new PowerSourceComponent().Outlets);
    }

    private static int SetNull(JsonNode node, string name)
    {
        int count = 0;
        if (node is JsonObject o)
        {
            if (o.ContainsKey(name))
            {
                o[name] = null;
                count++;
            }
            foreach (var (_, child) in o.ToList())
                if (child is not null) count += SetNull(child, name);
        }
        else if (node is JsonArray a)
        {
            foreach (var child in a)
                if (child is not null) count += SetNull(child, name);
        }
        return count;
    }
}
