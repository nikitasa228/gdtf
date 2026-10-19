using System.Numerics;
using System.Text.Json.Nodes;
using Candela.Core.Math;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;
using Candela.Core.Serialization;

namespace Candela.Electrical.Tests;

/// <summary>
/// Inputs the solver must survive: a project whose cabling lists a damaged file left as <c>null</c>, and an anchor
/// moved to a non-finite place between a solve and a geometry update.
/// </summary>
public sealed class NetworkReviewTests
{
    [Fact]
    public void A_project_with_null_cabling_lists_solves_and_reports_instead_of_throwing()
    {
        var kit = new SceneKit();
        var desk = kit.Console("Desk", new Vector3(0, -5, 0), SceneKit.DmxOutput("DMX A", 1));
        var head = kit.Fixture(TestData.Naja, "Head", 1, new Vector3(0, 0, 6), universe: 1);
        kit.Cable("dmx-xlr5", "D1", (desk, "DMX A"), (head, "XLR IN"));
        kit.Source("Distro", new Vector3(4, -5, 0), outlets: SceneKit.Outlet("Outlet 1"));
        var json = JsonNode.Parse(SceneSerializer.ToJson(kit.Scene))!;
        foreach (var name in new[] { "nodes", "segments", "endpoints", "outputs", "outlets" }) Null(json, name);
        var damaged = SceneSerializer.FromJson(json.ToJsonString());

        var solution = kit.Solver.Solve(damaged);
        Assert.Single(solution.Cables);
        Assert.Empty(solution.Cables[0].Endpoints);
        Assert.Equal([PortResolver.MainsInPortName], kit.Solver.Ports.GetPorts(damaged.All().Single(e => e.Name == "Distro")).Select(p => p.Name));
        Assert.Empty(kit.Solver.Ports.GetPorts(damaged.All().Single(e => e.Name == "Desk")));
        Assert.False(solution.IsConsoleDriven(head.Id));
    }

    [Fact]
    public void An_anchor_moved_to_a_non_finite_place_is_reported_by_the_update_like_a_fresh_solve()
    {
        var kit = new SceneKit();
        var desk = kit.Console("Desk", new Vector3(0, -5, 0), SceneKit.DmxOutput("DMX A", 1));
        var head = kit.Fixture(TestData.Naja, "Head", 1, new Vector3(0, 0, 6), universe: 1);
        var cable = kit.Cable("dmx-xlr5", "D1", (desk, "DMX A"), (head, "XLR IN"));
        var solution = kit.Solve();
        Assert.DoesNotContain(solution.Issues, i => i.Code == IssueCodes.CableInvalidGeometry);

        head.Local = new Transform(new Vector3(float.NaN, 0, 6));
        var broken = kit.Solver.UpdateGeometry(solution, kit.Scene, [cable.Id]);
        var fresh = kit.Solver.Solve(kit.Scene);
        Assert.Contains(broken.Issues, i => i.Code == IssueCodes.CableInvalidGeometry);
        Assert.Equal(fresh.Issues.Select(i => (i.Code, i.Message)), broken.Issues.Select(i => (i.Code, i.Message)));
        Assert.Equal(fresh.FindCable(cable.Id)!.LengthM, broken.FindCable(cable.Id)!.LengthM);

        head.Local = new Transform(new Vector3(0, 0, 6));
        var healed = kit.Solver.UpdateGeometry(broken, kit.Scene, [cable.Id]);
        Assert.DoesNotContain(healed.Issues, i => i.Code == IssueCodes.CableInvalidGeometry);
        Assert.Equal(solution.FindCable(cable.Id)!.LengthM, healed.FindCable(cable.Id)!.LengthM, 4);
        Assert.True(healed.IsConsoleDriven(head.Id));
    }

    private static void Null(JsonNode node, string name)
    {
        if (node is JsonObject o)
        {
            if (o.ContainsKey(name)) o[name] = null;
            foreach (var (_, child) in o.ToList())
                if (child is not null) Null(child, name);
        }
        else if (node is JsonArray a)
        {
            foreach (var child in a)
                if (child is not null) Null(child, name);
        }
    }
}
