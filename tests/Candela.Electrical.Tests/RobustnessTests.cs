using System.Diagnostics;
using System.Numerics;
using Candela.Core.Diagnostics;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;
using Xunit.Abstractions;

namespace Candela.Electrical.Tests;

public class RobustnessTests(ITestOutputHelper output)
{
    [Fact]
    public void Malformed_scene_data_becomes_issues_instead_of_exceptions()
    {
        var kit = new SceneKit();
        var desk = kit.Console("Desk", new Vector3(0, -10, 0), SceneKit.DmxOutput("DMX A", 1));
        desk.Get<ConsoleComponent>()!.Outputs.Add(null!);
        var dead = kit.Source("Dead source", new Vector3(5, -10, 0), voltage: 0f, outlets: SceneKit.Outlet("X"));
        var head = kit.Fixture(TestData.Naja, "Head", 1, new Vector3(0, 0, 6), 1, 1);
        var ghost = kit.Fixture("Nobody@Nothing@v1.gdtf", "Ghost", 2, new Vector3(2, 0, 6), 1, 101);
        var gone = kit.Fixture(TestData.Naja, "Gone", 3, new Vector3(4, 0, 6));

        kit.Cable("no-such-type", "Mystery", (desk, "DMX A"), (head, "XLR IN"));
        kit.Cable("dmx-xlr5", "Bad port", (desk, "DMX A"), (head, "XLR NOWHERE"));
        var toGone = kit.Cable("dmx-xlr5", "To gone", (head, "XLR OUT"), (gone, "XLR IN"));
        kit.Scene.Remove(gone);
        var broken = kit.Cable("powercon-true1", "Broken", (dead, "X"), (head, "Power IN"));
        var bc = broken.Get<CableComponent>()!;
        bc.Endpoints.Add(null!);
        bc.Endpoints.Add(new CableEndpoint { NodeId = Guid.NewGuid(), EntityId = head.Id, PortName = "ETH IN" });
        bc.Segments.Add(new CableSegment(Guid.NewGuid(), Guid.NewGuid()));
        bc.Nodes.Add(null!);

        var s = kit.Solve();
        var codes = s.Issues.Select(i => i.Code).ToHashSet();
        Assert.Contains(IssueCodes.CableUnknownType, codes);
        Assert.Contains(IssueCodes.CableUnknownPort, codes);
        Assert.Contains(IssueCodes.CableEndpointMissing, codes);
        Assert.Contains(IssueCodes.CableEndpointNodeMissing, codes);
        Assert.Contains(IssueCodes.CableInvalidGeometry, codes);
        Assert.Contains(IssueCodes.CableAnchorMissing, codes);
        Assert.Contains(IssueCodes.PowerSourceInvalid, codes);
        Assert.Contains(IssueCodes.FixtureUnknownType, codes);
        Assert.Contains(s.Issues, i => i.Code == IssueCodes.CableUnknownPort && i.Message.Contains("'XLR IN'"));
        Assert.Null(s.FindCable(toGone.Id)!.Endpoints.Single(e => e.EntityName is null).Port);
        Assert.False(s.IsConsoleDriven(head.Id));

        // Reports and views cope with the same mess.
        Assert.NotNull(PowerReport.Build(s));
        Assert.NotNull(DataReport.Build(s));
        Assert.Equal(4, CableSchedule.Build(s).Rows.Count);
        Assert.All(s.Cables, c => Assert.NotNull(CableLinkView.Build(s, c.EntityId)));
        Assert.Null(CableLinkView.Build(s, Guid.NewGuid()));
        Assert.True(ghost.Has<FixtureComponent>());
    }

    [Fact]
    public void Issue_log_collects_electrical_and_rigging_issues()
    {
        var kit = new SceneKit();
        var distro = kit.Source("Distro", new Vector3(0, -5, 0), outlets: SceneKit.Outlet("A", "powerCON TRUE1", 5f));
        var aeco = kit.Fixture(TestData.Aeco20, "AECO", 1, new Vector3(0, 0, 6));
        kit.Cable("powercon-true1", "P", (distro, "A"), (aeco, "Power In"));

        var log = new IssueLog();
        log.Register(new ElectricalIssueProvider(kit.Solver));
        log.Register(new RiggingIssueProvider(new RiggingAnalyzer(TestData.Profiles)));
        log.Refresh(kit.Scene);
        Assert.Equal(IssueSeverity.Error, log.Current[0].Severity);
        Assert.Contains(log.Current, i => i.Code == IssueCodes.PowerBreakerOverload && i.Category == IssueCategory.Power);
        Assert.Contains(log.Current, i => i.Code == IssueCodes.RiggingNotMounted && i.Category == IssueCategory.Rigging);
        Assert.DoesNotContain(log.Current, i => i.Category == IssueCategory.Program);
        Assert.Same(kit.Solver.GetOrSolve(kit.Scene), kit.Solver.GetOrSolve(kit.Scene));
    }

    [Fact]
    public void Large_scene_solves_in_linear_time()
    {
        var kit = new SceneKit();
        const int lines = 40, perLine = 25;
        var outputs = Enumerable.Range(1, lines).Select(i => SceneKit.DmxOutput($"DMX {i}", i)).ToArray();
        var desk = kit.Console("Desk", new Vector3(0, -20, 0), outputs);
        var outlets = Enumerable.Range(1, 2 * lines).Select(i => SceneKit.Outlet($"O{i}", "powerCON TRUE1", 16f, (i % 3) + 1)).ToArray();
        var distro = kit.Source("Distro", new Vector3(0, -15, 0), phases: 3, outlets: outlets);
        for (int l = 0; l < lines; l++)
        {
            var row = Enumerable.Range(0, perLine)
                .Select(i => kit.Fixture(TestData.Cobra100, $"Spot {l}-{i}", l * perLine + i + 1, new Vector3(i * 0.8f, l * 0.5f, 6), l + 1, 1 + i * 20))
                .ToList();
            kit.Chain("dmx-xlr5", $"D{l}-", (desk, $"DMX {l + 1}"), row, "DMX 5Pin IN", "DMX 5Pin OUT");
            // Two power runs per line keep every outlet within 16 A (12/13 × 216 W).
            kit.Chain("powercon-true1", $"P{l}-", (distro, $"O{2 * l + 1}"), row.Take(12).ToList(), "Power IN", "Power OUT");
            kit.Chain("powercon-true1", $"Q{l}-", (distro, $"O{2 * l + 2}"), row.Skip(12).ToList(), "Power IN", "Power OUT");
        }
        kit.Solve(); // warm-up (profile cache, JIT)
        var sw = Stopwatch.StartNew();
        var s = kit.Solve();
        sw.Stop();
        output.WriteLine($"{lines * perLine} fixtures, {s.Cables.Count} cables solved in {sw.Elapsed.TotalMilliseconds:0} ms; {s.Issues.Count} issues");
        Assert.Equal(lines * perLine, s.ConsoleDrivenFixtures.Count);
        Assert.All(s.DataLines, l => Assert.Equal(perLine, l.Feeds.Count));
        Assert.Empty(s.UnpoweredDevices);
        Assert.DoesNotContain(s.Issues, i => i.Severity == IssueSeverity.Error);
        Assert.True(sw.Elapsed.TotalSeconds < 3, $"Solve took {sw.Elapsed.TotalMilliseconds:0} ms");
    }
}
