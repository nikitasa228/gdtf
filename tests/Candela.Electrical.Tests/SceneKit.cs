using System.Numerics;
using Candela.Core.Diagnostics;
using Candela.Core.Math;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;

namespace Candela.Electrical.Tests;

/// <summary>Builds small test scenes: fixtures, consoles, power sources, trusses and cables anchored to devices.</summary>
internal sealed class SceneKit
{
    public SceneKit(IFixtureProfileProvider? profiles = null)
    {
        Profiles = profiles ?? TestData.Profiles;
        Solver = new NetworkSolver(Profiles);
    }

    public Scene Scene { get; } = new("Electrical test");

    public IFixtureProfileProvider Profiles { get; }

    public NetworkSolver Solver { get; }

    public NetworkSolution Solve() => Solver.Solve(Scene);

    public Entity Fixture(string spec, string name, int fixtureId, Vector3 position, int universe = 0, int address = 1,
        Guid? mountedOn = null, Entity? parent = null)
    {
        var e = new Entity(name) { Local = new Transform(position) };
        var f = new FixtureComponent { GdtfSpec = spec, FixtureId = fixtureId, MountedOn = mountedOn };
        if (universe > 0) f.Addresses.Add(new DmxAddress(universe, address));
        e.Add(f);
        Scene.Add(e, parent);
        return e;
    }

    public Entity Console(string name, Vector3 position, params ConsoleOutput[] outputs)
    {
        var e = new Entity(name) { Local = new Transform(position) };
        var c = new ConsoleComponent();
        if (outputs.Length > 0)
        {
            c.Outputs.Clear();
            c.Outputs.AddRange(outputs);
        }
        e.Add(c);
        Scene.Add(e);
        return e;
    }

    public static ConsoleOutput DmxOutput(string port, int universe) => new() { PortName = port, Signal = "DMX512", FirstUniverse = universe };

    public static ConsoleOutput EthernetOutput(string port, int firstUniverse, int count) =>
        new() { PortName = port, Signal = "Ethernet", FirstUniverse = firstUniverse, UniverseCount = count };

    public Entity Source(string name, Vector3 position, float voltage = 230f, float frequency = 50f, int phases = 1, params PowerOutlet[] outlets)
    {
        var e = new Entity(name) { Local = new Transform(position) };
        var s = new PowerSourceComponent { VoltageV = voltage, FrequencyHz = frequency, Phases = phases };
        if (outlets.Length > 0)
        {
            s.Outlets.Clear();
            s.Outlets.AddRange(outlets);
        }
        e.Add(s);
        Scene.Add(e);
        return e;
    }

    public static PowerOutlet Outlet(string name, string connector = "powerCON TRUE1", float breakerA = 16f, int phase = 1) =>
        new() { PortName = name, Connector = connector, BreakerA = breakerA, Phase = phase };

    public Entity Truss(string name, Vector3 position, float length, float maxUniformKg = 0, float maxPointKg = 0, float weightKg = 0)
    {
        var e = new Entity(name) { Local = new Transform(position) };
        e.Add(new TrussComponent { Length = length, MaxUniformLoadKg = maxUniformKg, MaxPointLoadKg = maxPointKg, WeightKg = weightKg });
        Scene.Add(e);
        return e;
    }

    /// <summary>
    /// A cable from one port to one or more ports. Every plugged node is anchored to its device (zero offset); with
    /// several targets the cable branches at a free node placed at the centroid of all ends.
    /// </summary>
    public Entity Cable(string typeId, string label, (Entity Entity, string Port) from, params (Entity Entity, string Port)[] to)
    {
        var e = new Entity(label);
        var c = new CableComponent { CableTypeId = typeId, Label = label };
        var first = Plug(c, from);
        Guid hub = first;
        if (to.Length > 1)
        {
            var centroid = to.Aggregate(from.Entity.WorldPosition, (acc, t) => acc + t.Entity.WorldPosition) / (to.Length + 1);
            var branch = new CableNode { Position = centroid };
            c.Nodes.Add(branch);
            c.Segments.Add(new CableSegment(first, branch.Id));
            hub = branch.Id;
        }
        foreach (var t in to)
        {
            var n = Plug(c, t);
            c.Segments.Add(new CableSegment(hub, n));
        }
        e.Add(c);
        Scene.Add(e);
        return e;
    }

    /// <summary>Daisy-chains devices: from → d1.inPort, d1.outPort → d2.inPort, … with one cable per hop.</summary>
    public List<Entity> Chain(string typeId, string labelPrefix, (Entity Entity, string Port) from, IReadOnlyList<Entity> devices, string inPort, string outPort)
    {
        var cables = new List<Entity>(devices.Count);
        var current = from;
        for (int i = 0; i < devices.Count; i++)
        {
            cables.Add(Cable(typeId, $"{labelPrefix}{i + 1}", current, (devices[i], inPort)));
            current = (devices[i], outPort);
        }
        return cables;
    }

    private static Guid Plug(CableComponent c, (Entity Entity, string Port) end)
    {
        var node = new CableNode { Anchor = new CableAnchor(end.Entity.Id, Vector3.Zero) };
        c.Nodes.Add(node);
        c.Endpoints.Add(new CableEndpoint { NodeId = node.Id, EntityId = end.Entity.Id, PortName = end.Port });
        return node.Id;
    }
}

internal static class IssueAssertions
{
    public static IReadOnlyList<Issue> WithCode(this IEnumerable<Issue> issues, string code) => issues.Where(i => i.Code == code).ToList();

    public static Issue Single(this IEnumerable<Issue> issues, string code)
    {
        var hits = issues.WithCode(code);
        Assert.True(hits.Count == 1, $"Expected one '{code}' issue, found {hits.Count}: {string.Join(" | ", issues.Select(i => i.Code + ": " + i.Message))}");
        return hits[0];
    }

    public static void None(this IEnumerable<Issue> issues, string code)
    {
        var hits = issues.WithCode(code);
        Assert.True(hits.Count == 0, $"Expected no '{code}' issue, found: {string.Join(" | ", hits.Select(i => i.Message))}");
    }
}
