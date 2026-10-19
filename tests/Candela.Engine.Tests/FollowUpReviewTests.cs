using System.Numerics;
using Candela.Assets;
using Candela.Core.Diagnostics;
using Candela.Core.SceneGraph.Components;

namespace Candela.Engine.Tests;

/// <summary>Regression tests from the review of the engine follow-ups (background model decoding).</summary>
public class FollowUpReviewTests
{
    [Fact]
    public async Task A_background_decode_that_throws_is_recorded_as_failed_and_not_started_again()
    {
        // a native-format model in a store that has not decoded it (an opened project), and a worker lookup that throws
        string obj = SceneBuilder.WriteCubeObj(TestEnvironment.NewTempDirectory("decode-throws"));
        var store = new AssetStore();
        var entry = store.Add(AssetKind.Model, "cube.obj", File.ReadAllBytes(obj));
        int lookups = 0;
        var models = new EngineModels(() => store, cacheDirectory: null, workerPath: () =>
        {
            Interlocked.Increment(ref lookups);
            throw new InvalidOperationException("worker lookup failed");
        }, isolateNativeImports: true, timeout: TimeSpan.FromSeconds(5));
        Assert.False(models.IsDecoded(entry.Id));

        Assert.False(await models.DecodeAsync(entry.Id));
        Assert.Equal(1, lookups);
        Assert.Contains(models.Issues, i => i.Code == EngineIssueCodes.AssetUnreadable && i.Severity == IssueSeverity.Error);
        // known to fail: the renderer binding's next request returns at once instead of decoding (and failing) again
        Assert.True(models.IsDecoded(entry.Id));
        Assert.True(store.TryGetDecodedModel(entry.Id, out var model, out var error) && model is null && error is not null);
        Assert.False(await models.DecodeAsync(entry.Id));
        Assert.False(models.TryGet(entry.Id, out _, out var reason));
        Assert.Contains("worker lookup failed", reason, StringComparison.Ordinal);
        Assert.Equal(1, lookups);
    }

    [Fact]
    public void Looks_and_the_test_pattern_use_the_stand_in_mode_the_dmx_engine_evaluates()
    {
        // fixtures that name a model the library lacks (an MVR stub name, as a scene written by another tool keeps it),
        // patched 44 channels apart: the DMX engine draws them with the MagicPanel 602 in its 20-channel mode that fits,
        // not in its 160-channel 'Extended' of the requested name
        using var engine = TestEnvironment.CreateEngine("stub-names");
        var blades = new List<Core.SceneGraph.Entity>();
        for (int i = 0; i < 4; i++)
            blades.Add(SceneBuilder.Fixture(engine.Scene, "Ayrton@MagicBlade R.gdtf", "Extended", new Vector3(i - 1.5f, 0f, 2f), 1, 1 + 44 * i));
        engine.Tick(1.0 / 30);
        var evaluated = engine.Dmx.GetState(blades[0].Id)!.Profile;
        Assert.True(evaluated.Footprint <= 44, $"{evaluated} on a 44-channel spacing");

        // the Art-Net test pattern writes the DMX layout of that mode, so the fixtures do not overlap
        var encoders = GdtfTestPatternEncoder.EncodersForScene(engine);
        Assert.Equal(4, encoders.Count);
        Assert.All(encoders, e => Assert.Same(evaluated, e.Profile));

        // the looks write manual values for that profile: every key names one of its attributes
        Assert.Equal(4, StageLooks.ApplyOpenWhite(engine));
        Assert.Equal(4, StageLooks.ApplyDemo(engine));
        foreach (var blade in blades)
        {
            var manual = blade.Get<FixtureComponent>()!.ManualAttributes;
            Assert.NotEmpty(manual);
            var known = new Dictionary<string, float>(StringComparer.Ordinal);
            engine.Tick(2.0 / 30);
            engine.Dmx.GetState(blade.Id)!.CopyAttributes(known);
            Assert.All(manual.Keys, k => Assert.True(known.ContainsKey(k), $"'{k}' is not an attribute of {evaluated}"));
        }
    }

    [Fact]
    public void Wide_washes_are_not_reported_as_clamped_but_a_bulb_wider_than_the_renderer_cone_is()
    {
        // an ARRI SkyPanel's soft light has a field of 179° (the DMX profile's limit); a bulb radiates beyond 170° with its
        // 50 % cone already. Only the bulb is drawn narrower than it is.
        using var engine = TestEnvironment.CreateEngine("field-clamp");
        var panel = SceneBuilder.Fixture(engine.Scene, "ARRI@SkyPanel_S60C@DMX_v4.4_13_Jan_2021.gdtf", null, new Vector3(-2, 0, 4), 1, 1);
        var bulb = SceneBuilder.Fixture(engine.Scene, "Astera_LED_Technology@FP7_LunaBulb@tested_by_Astera__V1.gdtf", null, new Vector3(2, 0, 4), 2, 1);
        Assert.Equal(2, StageLooks.ApplyOpenWhite(engine, new OpenWhiteOptions { Undoable = false, Zoom = 1f }));
        var world = new RecordingRenderWorld();
        engine.AttachRenderWorld(world, loadFixtureModelsInBackground: false);
        engine.Tick(0);
        engine.Tick(1.0 / 30);

        var panelBeams = engine.Dmx.GetState(panel.Id)!.Beams.Where(b => b.PeakCandela > 0f).ToList();
        Assert.NotEmpty(panelBeams);
        Assert.Contains(panelBeams, b => b.FieldAngle > StageLightCollector.MaxFieldAngle);
        Assert.Contains(engine.Dmx.GetState(bulb.Id)!.Beams, b => b.PeakCandela > 0f && b.BeamAngle > StageLightCollector.MaxFieldAngle);
        Assert.All(world.Lights, l =>
        {
            Assert.InRange(l.BeamAngle, 0f, StageLightCollector.MaxFieldAngle + 1e-5f);
            Assert.InRange(l.FieldAngle, l.BeamAngle, StageLightCollector.MaxFieldAngle + 1e-5f);
        });
        Assert.Equal([bulb.Id], engine.LightCollector.ClampedFieldFixtures);
        engine.RefreshIssues();
        var clamped = Assert.Single(engine.Issues.Current, i => i.Code == EngineIssueCodes.FieldClamped);
        Assert.Equal([bulb.Id], clamped.Entities);
    }
}
