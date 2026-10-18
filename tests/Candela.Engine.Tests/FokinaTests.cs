using Candela.Core.Diagnostics;
using Candela.Core.SceneGraph.Components;
using Candela.Mvr;

namespace Candela.Engine.Tests;

/// <summary>The real Depence export <c>stages/Фокина.mvr</c> (91 fixtures, stub GDTFs) loaded, evaluated and synced to a render world.</summary>
public sealed class FokinaTests
{
    [Fact]
    public void Opening_the_mvr_resolves_every_fixture_with_four_substitutes()
    {
        using var engine = TestEnvironment.CreateEngine();
        var result = engine.Open(TestEnvironment.Fokina);

        Assert.True(result.Success, result.Summary);
        Assert.Equal(91, engine.Scene.Query<FixtureComponent>().Count());
        Assert.Equal(4, result.Issues.Count(i => i.Code == MvrIssueCodes.GdtfSubstitute));
        Assert.DoesNotContain(result.Issues, i => i.Severity == IssueSeverity.Error);
        Assert.Null(engine.ProjectPath);
        Assert.False(engine.IsModified);

        engine.Tick(1.0 / 30);
        Assert.Equal(91, engine.Dmx.FixtureCount);
        foreach (var (entity, _) in engine.Scene.Query<FixtureComponent>())
            Assert.NotNull(engine.Dmx.GetState(entity.Id));
        Assert.Equal(91, engine.Poses.Count);

        // the substitute patch overlaps are real problems of the file and stay visible in the issue list
        engine.RefreshIssues();
        Assert.Contains(engine.Issues.Current, i => i.Code == "patch.overlap");
        Assert.Contains(engine.Issues.Current, i => i.Code == MvrIssueCodes.GdtfSubstitute);
    }

    [Fact]
    public void Render_sync_draws_every_fixture_part_and_one_light_per_lit_beam()
    {
        using var engine = TestEnvironment.CreateEngine();
        Assert.True(engine.Open(TestEnvironment.Fokina).Success);
        Assert.Equal(91, StageLooks.ApplyOpenWhite(engine));
        var world = new RecordingRenderWorld();
        var sync = engine.AttachRenderWorld(world);
        sync.LoadFixtureModelsInBackground = false;
        engine.Tick(0);
        engine.Tick(1.0 / 30);
        Assert.True(sync.CompletePendingLoads(TimeSpan.FromMinutes(2)));
        Assert.Empty(world.Violations);

        // every geometry part with a model is drawn for every fixture, at the pose's part matrix
        var expected = new Dictionary<string, int>();
        foreach (var (entity, fixture) in engine.Scene.Query<FixtureComponent>())
        {
            var state = engine.Dmx.GetState(entity.Id)!;
            var path = engine.Dmx.GetResolution(entity.Id)!.Entry!.FilePath;
            if (!expected.TryGetValue(state.Profile.Key, out int count))
            {
                var models = FixtureModelLoader.Load(path, state.Profile);
                Assert.Null(models.Error);
                count = models.Parts.Sum(p => p.Model.Parts.Count(m => m.Mesh.Positions.Length > 0 && m.Mesh.Indices.Length > 0));
                expected[state.Profile.Key] = count;
            }
            var instances = sync.InstancesOf(entity.Id);
            Assert.True(count > 0, $"{fixture.GdtfSpec} has no model parts");
            Assert.Equal(count, instances.Count);
            uint pick = sync.PickIdOf(entity.Id);
            Assert.NotEqual(0u, pick);
            Assert.True(sync.TryGetEntity(pick, out var picked) && picked == entity.Id);
            foreach (var i in instances) Assert.Equal(pick, world.Instances[i.Handle.Value].PickId);
        }
        Assert.Equal(sync.InstanceCount, world.Instances.Count);

        // one light per lit beam, each attributed to its fixture
        int litBeams = 0;
        var litFixtures = new HashSet<Guid>();
        foreach (var (entity, _) in engine.Scene.Query<FixtureComponent>())
        {
            foreach (var beam in engine.Dmx.GetState(entity.Id)!.Beams)
            {
                if (!(beam.PeakCandela > 0f)) continue;
                litBeams++;
                litFixtures.Add(entity.Id);
            }
        }
        Assert.True(litFixtures.Count >= 59, $"only {litFixtures.Count} fixtures lit"); // Zonda (32) and Rivale (27) at least
        Assert.Equal(litBeams, world.Lights.Length);
        foreach (var id in litFixtures)
        {
            int beams = engine.Dmx.GetState(id)!.Beams.Count(b => b.PeakCandela > 0f);
            Assert.Equal(beams, world.Lights.Count(l => l.FixtureId == id));
        }
        Assert.All(world.Lights, l => Assert.InRange(l.FieldAngle, l.BeamAngle, StageLightCollector.MaxFieldAngle + 1e-4f));
    }

    [Fact]
    public void Evaluation_is_deterministic_for_the_same_input_sequence()
    {
        float[] Run()
        {
            using var engine = TestEnvironment.CreateEngine(dmxThreads: 4);
            Assert.True(engine.Open(TestEnvironment.Fokina).Success);
            StageLooks.ApplyOpenWhite(engine);
            foreach (double dt in new[] { 0, 0.033, 0.016, 0.05, 0.033 }) engine.Tick(dt);
            return engine.Lights.ToArray().SelectMany(l => new[] { l.Position.X, l.Position.Y, l.Position.Z, l.Direction.X, l.Direction.Y, l.PeakCandela, l.BeamAngle }).ToArray();
        }

        Assert.Equal(Run(), Run());
    }

    [Fact]
    public void Exported_mvr_opens_again_with_every_fixture()
    {
        using var engine = TestEnvironment.CreateEngine();
        Assert.True(engine.Open(TestEnvironment.Fokina).Success);
        string path = Path.Combine(TestEnvironment.NewTempDirectory("export"), "fokina-export.mvr");
        var exported = engine.ExportMvr(path);
        Assert.True(exported.Success, exported.Summary);

        using var reopened = TestEnvironment.CreateEngine();
        var result = reopened.Open(path);
        Assert.True(result.Success, result.Summary);
        var before = engine.Scene.Query<FixtureComponent>().Select(f => (f.Entity.Id, f.Component.GdtfSpec, f.Component.Addresses.FirstOrDefault())).OrderBy(x => x.Id).ToList();
        var after = reopened.Scene.Query<FixtureComponent>().Select(f => (f.Entity.Id, f.Component.GdtfSpec, f.Component.Addresses.FirstOrDefault())).OrderBy(x => x.Id).ToList();
        Assert.Equal(before, after);
    }
}
