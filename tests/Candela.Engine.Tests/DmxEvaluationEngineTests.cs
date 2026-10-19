using System.Numerics;
using Candela.Core.SceneGraph.Components;
using Candela.Dmx;
using Candela.Photometry;

namespace Candela.Engine.Tests;

/// <summary>
/// DMX evaluation as the render world sees it: the Фокина stage at open white (no animation disc in the Rivale beams,
/// Zonda white inside its datasheet's CCT range) and the Rivale beam shaper drawn as an elongated beam.
/// </summary>
public sealed class DmxEvaluationEngineTests
{
    private const string Rivale = "Ayrton@Rivale_Wash@V1.8_Correct_Gobo_Value.gdtf";

    private static bool Inserted(in WheelState w) => (w.Insertion > 0f && !w.IsOpen) || w.Facets > 0;

    [Fact]
    public void Fokina_open_white_shows_no_animation_pattern_and_white_zondas()
    {
        using var engine = TestEnvironment.CreateEngine("fokina-open-white");
        Assert.True(engine.Open(TestEnvironment.Fokina).Success);
        Assert.Equal(91, StageLooks.ApplyOpenWhite(engine));
        var world = new RecordingRenderWorld();
        var sync = engine.AttachRenderWorld(world);
        sync.LoadFixtureModelsInBackground = false;
        engine.Tick(0);
        engine.Tick(1.0 / 30);
        Assert.True(sync.CompletePendingLoads(TimeSpan.FromMinutes(2)));
        engine.Tick(2.0 / 30);

        var rivales = new HashSet<Guid>();
        var zondas = new HashSet<Guid>();
        foreach (var (entity, _) in engine.Scene.Query<FixtureComponent>())
        {
            string file = engine.Dmx.GetResolution(entity.Id)!.Entry!.FileName;
            if (file.StartsWith("Ayrton@Rivale_Wash@", StringComparison.Ordinal)) rivales.Add(entity.Id);
            else if (file.StartsWith("Ayrton@Zonda_3_FX@", StringComparison.Ordinal)) zondas.Add(entity.Id);
        }
        Assert.NotEmpty(rivales);
        Assert.NotEmpty(zondas);

        // the Rivale's animation wheel (GD053) and gobos stay out of the beam at open white
        foreach (var id in rivales)
            foreach (var b in engine.Dmx.GetState(id)!.Beams)
                foreach (var w in b.Gobos.Concat(b.AnimationWheels).Concat(b.Prisms))
                    Assert.False(Inserted(w), $"{b.Name}: wheel {w.Wheel} slot {w.Slot} insertion {w.Insertion}");
        var rivaleLights = world.Lights.Where(l => rivales.Contains(l.FixtureId)).ToList();
        Assert.NotEmpty(rivaleLights);
        Assert.All(rivaleLights, l => Assert.False(l.Gobo.IsValid));
        Assert.All(rivaleLights, l => Assert.True(l.PrismFacets <= 1));

        // the Zonda full white: a re-balanced estimate inside the datasheet's 2700–8000 K, on the locus within 0.02
        var zondaLights = world.Lights.Where(l => zondas.Contains(l.FixtureId)).ToList();
        Assert.NotEmpty(zondaLights);
        foreach (var l in zondaLights)
        {
            var xyz = ColorSpaces.Rec2020ToXyz(l.Color);
            var xy = new Vector2(xyz.X, xyz.Y) / (xyz.X + xyz.Y + xyz.Z);
            var cct = Cct.FromXy(xy);
            Assert.True(cct.IsMeaningful, $"{xy}");
            Assert.InRange(cct.Kelvin, 2700f / 1.02f, 8000f * 1.02f);
            Assert.InRange(MathF.Abs(cct.Duv), 0f, 0.02f);
        }
        foreach (var id in zondas)
            Assert.Contains(engine.Dmx.GetState(id)!.Beams, b => b.Beam.WhitePointEstimated);
    }

    [Fact]
    public void An_mvr_fixture_with_a_gel_colour_and_no_colour_system_renders_in_that_colour()
    {
        // a dimmer-only profile with an MVR <Color> (Lee 106 Primary Red), written and read back as MVR
        const string ellipsoidal = "ACME@ACME_ELLIPSOIDAL_300_D2550(E-300_D2550)@ACME_ELLIPSOIDAL_300_D2550(E-300_D2550).gdtf";
        var red = new CieColor(0.69f, 0.30f, 9f);
        string path = Path.Combine(TestEnvironment.NewTempDirectory("gel"), "gel.mvr");
        using (var writer = TestEnvironment.CreateEngine("gel-export"))
        {
            var e = SceneBuilder.Fixture(writer.Scene, ellipsoidal, null, new Vector3(0f, 0f, 5f), 1, 1);
            e.Get<FixtureComponent>()!.Color = red;
            var exported = writer.ExportMvr(path);
            Assert.True(exported.Success, exported.Summary);
        }

        using var engine = TestEnvironment.CreateEngine("gel-open");
        var opened = engine.Open(path);
        Assert.True(opened.Success, opened.Summary);
        var (entity, fixture) = engine.Scene.Query<FixtureComponent>().Single();
        Assert.NotNull(fixture.Color);
        Assert.Equal(red.X, fixture.Color!.Value.X, 1e-4f);
        Assert.Equal(1, StageLooks.ApplyOpenWhite(engine, new OpenWhiteOptions { Undoable = false }));
        var world = new RecordingRenderWorld();
        engine.AttachRenderWorld(world, loadFixtureModelsInBackground: false);
        engine.Tick(0);
        engine.Tick(1.0 / 30);

        Assert.False(engine.Dmx.GetState(entity.Id)!.Beams[0].Beam.HasColorSystem);
        var light = world.Lights.Single(l => l.FixtureId == entity.Id);
        var xyz = ColorSpaces.Rec2020ToXyz(light.Color);
        var xy = new Vector2(xyz.X, xyz.Y) / (xyz.X + xyz.Y + xyz.Z);
        Assert.True(xy.X > 0.6f && xy.Y < 0.36f, $"light colour {xy}");
    }

    [Fact]
    public void Rivale_beam_shaper_is_drawn_as_an_elongated_beam()
    {
        using var engine = TestEnvironment.CreateEngine("rivale-shaper");
        var rivale = SceneBuilder.Fixture(engine.Scene, Rivale, "Standard_540", new Vector3(0f, 0f, 5f), 1, 1);
        Assert.Equal(1, StageLooks.ApplyOpenWhite(engine, new OpenWhiteOptions { Undoable = false, Zoom = 0.5f }));
        var world = new RecordingRenderWorld();
        engine.AttachRenderWorld(world, loadFixtureModelsInBackground: false);
        engine.Tick(0);
        engine.Tick(1.0 / 30);
        var round = world.Lights.Single(l => l.FixtureId == rivale.Id);
        Assert.Equal(0, round.PrismFacets);

        // BeamShaper on its second slot (the anamorphic lens), turned to 30°
        var fixture = rivale.Get<FixtureComponent>()!;
        fixture.ManualAttributes["BeamShaper"] = 2f;
        fixture.ManualAttributes["BeamShaperPos"] = 30f;
        fixture.NotifyChanged();
        for (int frame = 0; frame < 90; frame++) engine.Tick(1.0 / 30); // the wheel motor turns the lens in
        var beam = engine.Dmx.GetState(rivale.Id)!.Beams[0];
        Assert.Equal(2, beam.BeamShapers[0].Slot);
        Assert.Equal(NameParsing.DefaultShaperStretch, beam.ShaperStretch, 1e-4f);

        var shaped = world.Lights.Single(l => l.FixtureId == rivale.Id);
        Assert.Equal(2, shaped.PrismFacets);
        float drawn = MathF.Min(beam.ShaperStretch, StageLightCollector.MaxDrawnShaperStretch);
        Assert.Equal((drawn - 1f) * 0.5f * shaped.BeamAngle, shaped.PrismSpread, 1e-5f);
        Assert.Equal(beam.ShaperStretchRotation, shaped.PrismRotation, 1e-5f);
        // the two half-intensity copies together carry the round beam's peak
        Assert.Equal(beam.PeakCandela * beam.ShaperStretch, shaped.PeakCandela, shaped.PeakCandela * 1e-4f);
        Assert.Equal(round.PeakCandela, shaped.PeakCandela, round.PeakCandela * 0.01f);
    }
}
