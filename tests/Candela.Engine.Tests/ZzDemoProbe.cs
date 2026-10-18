using Candela.Core.SceneGraph.Components;
using Xunit.Abstractions;

namespace Candela.Engine.Tests;

public sealed class ZzDemoProbe(ITestOutputHelper output)
{
    [Fact]
    public void Probe()
    {
        using var engine = TestEnvironment.CreateEngine();
        Assert.True(engine.Open(TestEnvironment.Fokina).Success);
        if (Environment.GetEnvironmentVariable("PROBE_OPEN") == "1") StageLooks.ApplyOpenWhite(engine); else StageLooks.ApplyDemo(engine, new DemoLookOptions { Undoable = false });
        engine.Tick(0);
        for (int i = 0; i < 60; i++) engine.Tick(1.0 / 30);
        foreach (var g in engine.Scene.Query<FixtureComponent>().Select(x => (x.Entity, x.Component, P: engine.Profiles.Resolve(x.Component.GdtfSpec, x.Component.GdtfMode).Profile!))
            .GroupBy(m => (Type: m.P.Key, Band: (int)MathF.Round(m.Entity.WorldPosition.Z / 0.5f))).OrderByDescending(g => g.Key.Band).ThenBy(g => g.Key.Type, StringComparer.Ordinal))
            output.WriteLine($"BAR {g.Key.Band} {g.Key.Type} n={g.Count()} cat={g.First().P.Category} names={string.Join("/", g.Select(x => x.Entity.Name).Distinct())}");
        foreach (var (entity, fixture) in engine.Scene.Query<FixtureComponent>().Where(x => x.Entity.Name.StartsWith("Zonda 3 W")).Take(1))
            output.WriteLine("MANUAL " + string.Join(", ", fixture.ManualAttributes.Select(kv => $"{kv.Key}={kv.Value:F3}")));
        foreach (var (entity, fixture) in engine.Scene.Query<FixtureComponent>().OrderByDescending(x => x.Entity.WorldPosition.Z).ThenBy(x => x.Entity.WorldPosition.X))
        {
            var s = engine.Dmx.GetState(entity.Id)!;
            var p = entity.WorldPosition;
            var b = s.Beams.Count > 0 ? s.Beams[0] : null;
            var lit = s.Beams.Count(x => x.Intensity > 0.01f);
            output.WriteLine($"{entity.Name,-14} {s.Profile.Category,-9} {s.Profile.Mode.Name,-12} pos=({p.X:F1},{p.Y:F1},{p.Z:F1}) beams={s.Beams.Count} lit={lit} " +
                (b is null ? "" : $"I={b.Intensity:F2} D={b.Dimmer:F2} S={b.Shutter:F2} T={b.ColorTransmission:F3} lm={b.Flux:F0} cd={b.PeakCandela:F0} col={b.Color} beam={b.BeamAngle * 57.3f:F1} field={b.FieldAngle * 57.3f:F1} gobo={(b.Gobos.Length > 0 ? b.Gobos[0].Slot : -1)} ") +
                $"axes={string.Join(",", s.Axes.Select(a => $"{a.Pan * 57.3f:F0}/{a.Tilt * 57.3f:F0}"))} dev={s.Outputs.Haze:F2}");
        }
    }
}
