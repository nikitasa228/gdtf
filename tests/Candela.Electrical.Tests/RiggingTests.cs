using System.Numerics;
using Candela.Core.Diagnostics;
using Candela.Core.Math;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;

namespace Candela.Electrical.Tests;

public class RiggingTests
{
    private static RiggingSolution Analyze(SceneKit kit, IFixtureProfileProvider? profiles = null) =>
        new RiggingAnalyzer(profiles ?? TestData.Profiles).Analyze(kit.Scene);

    private static List<Entity> Faces(SceneKit kit, Entity truss, int count, int firstId, float x0 = 0.5f) =>
        Enumerable.Range(0, count)
            .Select(i => kit.Fixture(TestData.Face600, $"Face {firstId + i}", firstId + i, truss.WorldPosition + new Vector3(x0 + i, 0, -0.4f), mountedOn: truss.Id))
            .ToList();

    [Fact]
    public void Overloaded_truss_is_an_error_with_the_weights()
    {
        var kit = new SceneKit();
        var weak = kit.Truss("Weak truss", new Vector3(0, 0, 6), 6f, maxUniformKg: 100f);
        var strong = kit.Truss("Strong truss", new Vector3(0, 5, 6), 6f, maxUniformKg: 200f);
        var onWeak = Faces(kit, weak, 5, 1);
        Faces(kit, strong, 5, 11);

        var r = Analyze(kit);
        var issue = r.Issues.Single(IssueCodes.RiggingTrussOverload);
        Assert.Equal(IssueSeverity.Error, issue.Severity);
        Assert.Contains("'Weak truss' (F34, 6.0 m)", issue.Message);
        Assert.Contains("127.5 kg", issue.Message); // 5 × 25 kg + 5 clamps × 0.5 kg
        Assert.Contains("5 fixtures 125.0 kg", issue.Message);
        Assert.Contains("100.0 kg", issue.Message);
        Assert.Equal(new[] { weak.Id }.Concat(onWeak.Select(f => f.Id)), issue.Entities);

        var load = r.Trusses.Single(t => t.TrussEntityId == weak.Id);
        Assert.Equal(125, load.FixtureWeightKg, 6);
        Assert.Equal(2.5, load.ClampWeightKg, 6);
        Assert.Equal(127.5, load.UniformUtilisationPercent!.Value, 6);
        Assert.True(load.OwnWeightEstimated);
        Assert.Equal(6 * 4.5, load.OwnWeightKg, 4);
        Assert.Equal(25.5, load.HeaviestPointLoadKg, 6);
        r.Issues.None(IssueCodes.RiggingNotMounted);
    }

    [Fact]
    public void Single_fixture_heavier_than_the_point_load_is_an_error()
    {
        var kit = new SceneKit();
        var pipe = kit.Truss("Pipe", new Vector3(0, 0, 6), 3f, maxUniformKg: 500f, maxPointKg: 20f);
        Faces(kit, pipe, 1, 1);
        var issue = Analyze(kit).Issues.Single(IssueCodes.RiggingTrussPointOverload);
        Assert.Contains("#1 'Face 1'", issue.Message);
        Assert.Contains("25.5 kg", issue.Message);
        Assert.Contains("20.0 kg", issue.Message);
    }

    [Fact]
    public void Fixtures_above_the_floor_must_hang_on_a_structure()
    {
        var kit = new SceneKit();
        var hanging = kit.Fixture(TestData.Face600, "Floating", 7, new Vector3(0, 0, 6));
        kit.Fixture(TestData.Face600, "On the deck", 8, new Vector3(2, 0, 0.3f));
        var riser = new Entity("Riser") { Local = new Transform(new Vector3(10, 0, 1.2f)) };
        riser.Add(new WallComponent { Kind = WallKind.Floor, Width = 4, Height = 2, Thickness = 1.2f });
        kit.Scene.Add(riser);
        kit.Fixture(TestData.Face600, "On the riser", 9, new Vector3(10.5f, 0.5f, 1.2f));
        kit.Fixture(TestData.Face600, "Beside the riser", 10, new Vector3(20f, 0, 1.2f));
        var truss = kit.Truss("Truss", new Vector3(0, 5, 6), 4f);
        kit.Fixture(TestData.Face600, "Child of truss", 11, new Vector3(1, 0, -0.4f), parent: truss); // no MountedOn, but hangs under it
        kit.Fixture(TestData.Face600, "Lost mount", 12, new Vector3(3, 3, 6), mountedOn: Guid.NewGuid());

        var r = Analyze(kit);
        var notMounted = r.Issues.WithCode(IssueCodes.RiggingNotMounted);
        Assert.Equal(2, notMounted.Count);
        Assert.Contains(notMounted, i => i.Message.Contains("#7 'Floating'") && i.Message.Contains("6.0 m above the floor") && i.Message.Contains("truss/pipe"));
        Assert.Contains(notMounted, i => i.Message.Contains("#10 'Beside the riser'") && i.Message.Contains("1.2 m"));
        Assert.Contains(hanging.Id, notMounted.SelectMany(i => i.Entities));
        var lost = r.Issues.Single(IssueCodes.RiggingMountMissing);
        Assert.Equal(IssueSeverity.Error, lost.Severity);
        Assert.Contains("#12 'Lost mount'", lost.Message);
        Assert.Single(r.Trusses.Single().Fixtures);
    }

    [Fact]
    public void Cable_weight_along_a_truss_counts()
    {
        var kit = new SceneKit();
        var truss = kit.Truss("Truss", new Vector3(0, 0, 6), 6f);
        var faces = Faces(kit, truss, 2, 1, x0: 1f);
        kit.Cable("dmx-xlr5", "D", (faces[0], "DMX Out"), (faces[1], "DMX In"));
        var load = Analyze(kit).Trusses.Single();
        Assert.Equal(1.0 * 1.05 * 0.055, load.CableWeightKg, 5);
        Assert.Equal(51 + load.CableWeightKg, load.LoadKg, 5);
        Assert.Contains(load.Items, i => i.Description == "cable 'D'");
    }

    [Fact]
    public void Rigging_points_carry_their_share_by_the_lever_rule()
    {
        var kit = new SceneKit();
        var truss = kit.Truss("Truss", new Vector3(0, 0, 6), 10f, weightKg: 50f);
        var p1 = new Entity("Motor 1") { Local = new Transform(new Vector3(0, 0, 7)) };
        p1.Add(new RiggingPointComponent { CapacityKg = 40f, Carries = { truss.Id } });
        var p2 = new Entity("Motor 2") { Local = new Transform(new Vector3(10, 0, 7)) };
        p2.Add(new RiggingPointComponent { CapacityKg = 500f, Carries = { truss.Id, Guid.NewGuid() } });
        kit.Scene.Add(p1);
        kit.Scene.Add(p2);
        kit.Fixture(TestData.Face600, "Near motor 1", 1, new Vector3(2, 0, 5.6f), mountedOn: truss.Id);
        kit.Fixture(TestData.Face600, "On motor 2", 2, new Vector3(10, 0, 6.5f), mountedOn: p2.Id);

        var r = Analyze(kit);
        var m1 = r.Points.Single(p => p.EntityId == p1.Id);
        var m2 = r.Points.Single(p => p.EntityId == p2.Id);
        Assert.Equal(25.5 * 0.8 + 25, m1.LoadKg, 3);
        Assert.Equal(25.5 * 0.2 + 25 + 25.5, m2.LoadKg, 3);
        Assert.False(r.Trusses.Single().OwnWeightEstimated);

        var issue = r.Issues.Single(IssueCodes.RiggingPointOverload);
        Assert.Contains("'Motor 1'", issue.Message);
        Assert.Contains("45.4 kg", issue.Message);
        Assert.Contains("40.0 kg", issue.Message);
        Assert.Contains("truss 'Truss'", issue.Message);
        Assert.Contains("no longer exists", r.Issues.Single(IssueCodes.RiggingPointMissingTarget).Message);
        r.Issues.None(IssueCodes.RiggingNotMounted);
    }

    [Fact]
    public void Fixtures_of_unknown_weight_get_a_note()
    {
        var profiles = new InMemoryFixtureProfileProvider()
            .Add("Custom@Unweighed", FixtureProfile.Build("Custom@Unweighed", null, null, null));
        var kit = new SceneKit(profiles);
        var truss = kit.Truss("Truss", new Vector3(0, 0, 6), 3f, maxUniformKg: 100f);
        kit.Fixture("Custom@Unweighed", "Prop", 3, new Vector3(1, 0, 5.6f), mountedOn: truss.Id);
        var r = Analyze(kit, profiles);
        var note = r.Issues.Single(IssueCodes.RiggingUnknownWeight);
        Assert.Equal(IssueSeverity.Note, note.Severity);
        Assert.Contains("#3 'Prop'", note.Message);
        Assert.Contains("0 kg", note.Message);
        Assert.Single(r.Trusses.Single().UnknownWeightFixtures);
        Assert.Equal(0.5, r.Trusses.Single().LoadKg, 6);
    }
}
