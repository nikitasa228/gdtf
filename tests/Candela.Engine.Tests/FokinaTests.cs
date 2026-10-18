using Candela.Core.Diagnostics;
using Candela.Core.SceneGraph.Components;
using Candela.Mvr;

namespace Candela.Engine.Tests;

/// <summary>The real Depence export <c>stages/Фокина.mvr</c> (91 fixtures, stub GDTFs) loaded, evaluated and synced to a render world.</summary>
public sealed class FokinaTests
{
    [Fact]
    public void Opening_the_mvr_resolves_every_fixture_with_a_substitute_per_missing_model()
    {
        // four of the five models are not in the repository's library (FokinaFixtureTypes); each gets a substitute
        var missing = FokinaFixtureTypes.MissingFrom(TestEnvironment.Library);
        using var engine = TestEnvironment.CreateEngine();
        var result = engine.Open(TestEnvironment.Fokina);

        Assert.True(result.Success, result.Summary);
        Assert.Equal(91, engine.Scene.Query<FixtureComponent>().Count());
        Assert.Equal(missing.Count, result.Issues.Count(i => i.Code == MvrIssueCodes.GdtfSubstitute));
        Assert.DoesNotContain(result.Issues, i => i.Severity == IssueSeverity.Error);
        Assert.Null(engine.ProjectPath);
        Assert.False(engine.IsModified);

        engine.Tick(1.0 / 30);
        Assert.Equal(91, engine.Dmx.FixtureCount);
        foreach (var (entity, _) in engine.Scene.Query<FixtureComponent>())
            Assert.NotNull(engine.Dmx.GetState(entity.Id));
        Assert.Equal(91, engine.Poses.Count);

        // the stand-ins use the mode that fits their type's patch spacing (the MagicBlade R stand-in, a MagicPanel 602,
        // would overlap its neighbours in its 160-channel "Extended"): the resolved scene has no patch errors, and each
        // stand-in type is reported once with all its fixtures
        engine.RefreshIssues();
        Assert.DoesNotContain(engine.Issues.Current, i => i.Category == IssueCategory.Patch && i.Severity == IssueSeverity.Error);
        Assert.DoesNotContain(engine.Issues.Current, i => i.Code == "patch.overlap" || i.Code == "patch.out-of-range");
        Assert.Equal(missing.Count > 0, engine.Issues.Current.Any(i => i.Code == MvrIssueCodes.GdtfSubstitute));
        var substituteIssues = engine.Issues.Current.Where(i => i.Code == "patch.substitute-gdtf").ToList();
        Assert.Equal(missing.Count, substituteIssues.Count);
        Assert.Equal(missing.Sum(t => t.Objects), substituteIssues.Sum(i => i.Entities.Count));
        Assert.Equal(missing.Sum(t => t.Objects), engine.Settings.Substitutes.Count);
        if (missing.Contains(FokinaFixtureTypes.MagicBladeR))
        {
            var blade = engine.Scene.Query<FixtureComponent>().First(x => x.Entity.Name == "MagicBlade R");
            var state = engine.Dmx.GetState(blade.Entity.Id)!;
            Assert.True(state.Profile.Footprint <= FokinaFixtureTypes.MagicBladeR.Footprint, $"{state.Profile} on a 44-channel spacing");
            Assert.Equal(FokinaFixtureTypes.MagicBladeR.Spec, engine.SubstitutionOf(blade.Entity.Id)!.RequestedSpec);
            Assert.Equal("Extended", engine.SubstitutionOf(blade.Entity.Id)!.RequestedMode);
            Assert.Equal(44, engine.SubstitutionOf(blade.Entity.Id)!.PatchSpacing);
            Assert.Contains(result.Issues, i => i.Code == EngineIssueCodes.SubstituteMode);
            Assert.DoesNotContain(result.Issues, i => i.Code == MvrIssueCodes.ModeFootprint);
        }
        // the real Zonda 3 FX fixtures are not stand-ins, although the Zonda 3 Wash stand-in is the same file
        var fx = engine.Scene.Query<FixtureComponent>().Where(x => x.Entity.Name == "Zonda 3 FX").ToList();
        Assert.NotEmpty(fx);
        Assert.All(fx, x => Assert.Null(engine.SubstitutionOf(x.Entity.Id)));
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
        // every fixture lights at open white — the pixel-mapped MagicBlade stand-in included (its pixels' virtual dimmers
        // and its master dimmer whose channel sets are named the wrong way round, see Candela.Dmx RelationTests) — except
        // between the flashes of a pure strobe (the Atomic stand-in's 3-channel mode has no steady state)
        var dark = engine.Scene.Query<FixtureComponent>().Where(x => !litFixtures.Contains(x.Entity.Id)).ToList();
        Assert.All(dark, x => Assert.True(engine.Dmx.GetState(x.Entity.Id)!.Beams.All(b => b.Strobing && b.Dimmer > 0.99f),
            $"{x.Component.GdtfSpec} [{engine.Dmx.GetState(x.Entity.Id)!.Profile.Mode.Name}] is dark"));
        Assert.True(litFixtures.Count >= 87, $"only {litFixtures.Count} fixtures lit");
        Assert.Equal(litBeams, world.Lights.Length);
        foreach (var id in litFixtures)
        {
            int beams = engine.Dmx.GetState(id)!.Beams.Count(b => b.PeakCandela > 0f);
            Assert.Equal(beams, world.Lights.Count(l => l.FixtureId == id));
        }
        Assert.All(world.Lights, l => Assert.InRange(l.FieldAngle, l.BeamAngle, StageLightCollector.MaxFieldAngle + 1e-4f));
        // real optics: no field wider than 4 × the beam (the Rivale Wash stand-in writes its zoom range as beam/field
        // angle, which used to give a 244° field at mid zoom), and nothing needed the renderer's sanity clamp
        Assert.All(world.Lights, l => Assert.True(l.FieldAngle <= 4.001f * l.BeamAngle, $"field {l.FieldAngle} for beam {l.BeamAngle}"));
        Assert.Empty(engine.LightCollector.ClampedFieldFixtures);
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
