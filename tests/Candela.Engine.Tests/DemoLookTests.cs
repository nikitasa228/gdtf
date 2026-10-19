using System.Numerics;
using Candela.Core.SceneGraph.Components;
using Candela.Dmx;
using Candela.Gdtf;

namespace Candela.Engine.Tests;

/// <summary>
/// <see cref="StageLooks.ApplyDemo"/>: the show-like state <c>candela render --look demo</c> renders, on the real
/// <c>stages/Фокина.mvr</c> rig (91 fixtures) and small scenes.
/// </summary>
public sealed class DemoLookTests
{
    private static readonly Vector2 D65 = new(0.3127f, 0.3290f);

    private sealed record Lit(string Name, string Type, FixtureCategory Category, bool Moving, Vector3 Position, Vector3 Direction,
        Vector2 Xy, float Intensity, float BeamAngle, bool Gobo);

    /// <summary>
    /// Opens Фокина, applies the demo look and, with <paramref name="simulate"/>, lets the motors arrive and reads every
    /// fixture's first beam.
    /// </summary>
    private static (List<Lit> Fixtures, Vector3 Centre, Dictionary<string, Dictionary<string, float>> Manual) Demo(bool simulate = true)
    {
        using var engine = TestEnvironment.CreateEngine();
        Assert.True(engine.Open(TestEnvironment.Fokina).Success);
        Assert.Equal(91, StageLooks.ApplyDemo(engine, new DemoLookOptions { Undoable = false }));
        var manual = engine.Scene.Query<FixtureComponent>().ToDictionary(
            x => FormattableString.Invariant($"{x.Entity.Name} {x.Entity.WorldPosition.X:F2} {x.Entity.WorldPosition.Y:F2} {x.Entity.WorldPosition.Z:F2}"),
            x => new Dictionary<string, float>(x.Component.ManualAttributes));
        if (!simulate) return ([], StageLooks.StageCentre(engine), manual);
        engine.Tick(0);
        for (int i = 0; i < 180; i++) engine.Tick(1.0 / 30); // 6 s: every head has arrived
        var list = new List<Lit>();
        foreach (var pose in engine.Poses.All)
        {
            var beam = pose.State.Beams[0];
            Assert.True(pose.TryGetBeamFrame(beam.PartIndex, out _, out var direction, out _));
            bool moving = pose.Profile.Functions.Any(f => f.Class.Kind is AttributeKind.Pan or AttributeKind.Tilt);
            list.Add(new Lit(pose.Entity.Name, pose.Profile.Key, pose.Profile.Category, moving, pose.Entity.WorldPosition,
                Vector3.Normalize(direction), beam.Color.ToXy(), beam.Intensity, beam.BeamAngle, beam.Gobos.Length > 0 && !beam.Gobos[0].IsOpen));
        }
        return (list, StageLooks.StageCentre(engine), manual);
    }

    [Fact]
    public void The_demo_look_is_deterministic_symmetric_and_coloured_with_fans_crossings_and_gobos()
    {
        var (fixtures, centre, manual) = Demo();
        Assert.Equal(manual, Demo(simulate: false).Manual); // the same scene always gets the same look
        float floor = centre.Z;

        // strobes stay dark, everything else is at full
        Assert.All(fixtures.Where(f => f.Category == FixtureCategory.Strobe), f => Assert.Equal(0f, f.Intensity));
        Assert.All(fixtures.Where(f => f.Category != FixtureCategory.Strobe), f => Assert.True(f.Intensity > 0.99f, f.Name));
        var lit = fixtures.Where(f => f.Category != FixtureCategory.Strobe).ToList();

        // mirror partners (same type, height and depth, mirrored about the centre line) share colour and mirror their aim
        int pairs = 0;
        foreach (var a in lit.Where(f => f.Position.X < centre.X - 0.2f))
        {
            var b = lit.FirstOrDefault(f => f.Type == a.Type && MathF.Abs(f.Position.Z - a.Position.Z) < 0.3f &&
                MathF.Abs(f.Position.Y - a.Position.Y) < 0.3f && MathF.Abs(f.Position.X - centre.X + a.Position.X - centre.X) < 0.4f);
            if (b is null) continue;
            pairs++;
            Assert.True(Vector2.Distance(a.Xy, b.Xy) < 0.02f, $"{a.Name} at {a.Position}: {a.Xy} vs {b.Xy}");
            if (!a.Moving) continue;
            var mirrored = new Vector3(-b.Direction.X, b.Direction.Y, b.Direction.Z);
            Assert.True(Vector3.Distance(a.Direction, mirrored) < 0.1f, $"{a.Name} at {a.Position}: {a.Direction} vs mirrored {mirrored}");
        }
        Assert.True(pairs >= 30, $"{pairs} mirror pairs");

        // a few saturated colours plus white
        var colours = new List<Vector2>();
        foreach (var f in lit)
            if (!colours.Any(c => Vector2.Distance(c, f.Xy) < 0.03f)) colours.Add(f.Xy);
        Assert.True(colours.Count >= 5, $"{colours.Count} colours");
        Assert.Contains(lit, f => Vector2.Distance(f.Xy, D65) < 0.03f);
        Assert.True(colours.Count(c => Vector2.Distance(c, D65) > 0.15f) >= 3);

        // low heads fan up, over the audience (−Y) and across the stage; overhead heads light the floor
        var low = lit.Where(f => f.Moving && f.Position.Z - floor < 3.5f).ToList();
        var overhead = lit.Where(f => f.Moving && f.Position.Z - floor >= 3.5f).ToList();
        Assert.NotEmpty(low);
        Assert.NotEmpty(overhead);
        Assert.All(low, f => Assert.True(f.Direction.Z > 0.3f, $"{f.Name} at {f.Position}: {f.Direction}"));
        Assert.All(overhead, f => Assert.True(f.Direction.Z < -0.3f, $"{f.Name} at {f.Position}: {f.Direction}"));
        Assert.Contains(low, f => f.Direction.Y < -0.3f); // over the audience
        Assert.Contains(low, f => f.Position.X < centre.X - 3f && f.Direction.X > 0.5f); // from the left side across the stage
        Assert.Contains(low, f => f.Position.X < centre.X - 1f && f.Direction.X < -0.2f); // fanning out
        Assert.Contains(low, f => f.Position.X < centre.X - 1f && f.Position.Y < centre.Y - 3f && f.Direction.X > 0.1f); // crossing over the audience

        // narrow beams low in the rig: the Rivale stand-ins at their narrowest zoom (4°)
        Assert.All(low.Where(f => f.Name.StartsWith("Rivale", StringComparison.Ordinal)), f => Assert.InRange(f.BeamAngle * 180f / MathF.PI, 3.5f, 4.5f));

        // gobos on some overhead heads, not on all
        Assert.Contains(overhead, f => f.Gobo);
        Assert.Contains(overhead, f => !f.Gobo);
    }

    [Fact]
    public void Hazers_run_fog_stays_off_and_the_look_is_one_undo_step()
    {
        using var engine = TestEnvironment.CreateEngine("demo-haze");
        var hazer = SceneBuilder.Fixture(engine.Scene, SceneBuilder.EntourHaze, null, new Vector3(0, 6, 0), name: "Hazer");
        var left = SceneBuilder.Fixture(engine.Scene, SceneBuilder.MacViper, "Extended", new Vector3(-3, 0, 6), name: "Left");
        var right = SceneBuilder.Fixture(engine.Scene, SceneBuilder.MacViper, "Extended", new Vector3(3, 0, 6), name: "Right");
        Assert.Equal(3, StageLooks.ApplyDemo(engine, new DemoLookOptions { HazeOutput = 0.4f }));
        engine.Tick(0);
        for (int i = 0; i < 90; i++) engine.Tick(1.0 / 30);
        var outputs = engine.Dmx.GetState(hazer.Id)!.Outputs;
        // the Entour Haze PRO file names DMX 5 … 255 "100 percent output" (one physical value): any output is full
        Assert.True(outputs.Haze > 0.3f, $"haze {outputs.Haze}");
        Assert.Equal(0f, outputs.Fog);
        foreach (var head in new[] { left, right })
        {
            var beam = engine.Dmx.GetState(head.Id)!.Beams[0];
            Assert.True(beam.Intensity > 0.99f);
        }

        // one undo step takes the whole look back
        engine.Undo.Undo();
        foreach (var e in new[] { hazer, left, right })
            Assert.Empty(e.Get<FixtureComponent>()!.ManualAttributes);
    }
}
