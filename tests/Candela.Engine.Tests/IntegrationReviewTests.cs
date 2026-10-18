using System.Numerics;
using Candela.Core.Editing;
using Candela.Core.Math;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;
using Candela.Project;
using Candela.Rendering;

namespace Candela.Engine.Tests;

/// <summary>
/// End-to-end checks of the integration: lights against independent computations, incremental render sync against a
/// sync built from scratch, gobos, autosave recovery through the final Candela.Project API.
/// </summary>
public sealed class IntegrationReviewTests
{
    private const double Frame = 1.0 / 30.0;

    // Rec.2020 luminance coefficients (the working colour space of the engine).
    private static float Luminance2020(Vector3 rgb) => 0.2627f * rgb.X + 0.6780f * rgb.Y + 0.0593f * rgb.Z;

    [Fact]
    public void Gobo_textures_appear_when_the_render_world_is_attached_before_the_first_frame()
    {
        using var engine = TestEnvironment.CreateEngine("gobo-first-frame");
        var head = SceneBuilder.Fixture(engine.Scene, SceneBuilder.MacViper, "Basic", new Vector3(0, 0, 6));
        // attach first (the editor attaches its viewport before the first frame of an opened project)
        var world = new RecordingRenderWorld();
        engine.AttachRenderWorld(world, loadFixtureModelsInBackground: false);
        StageLooks.ApplyOpenWhite(engine, new OpenWhiteOptions { Undoable = false, Target = Vector3.Zero, Spread = 0f });
        var fixture = head.Get<FixtureComponent>()!;
        fixture.ManualAttributes["Gobo1"] = 3f;
        fixture.NotifyChanged();

        for (int i = 0; i < 90; i++) engine.Tick(Frame); // the gobo wheel travels to slot 3
        var light = Assert.Single(world.Lights);
        Assert.True(light.Gobo.IsValid, "the selected gobo is not projected");
        Assert.True(world.Textures.ContainsKey(light.Gobo.Value));
        Assert.Empty(world.Violations);
    }

    [Fact]
    public void Lights_follow_the_beam_geometry_colour_and_entity_rotation()
    {
        Vector3 Direction(Quaternion rotation, out SpotLight light, out Vector3 beamColour, out float beamCandela)
        {
            using var engine = TestEnvironment.CreateEngine("light-rotation");
            var head = SceneBuilder.Fixture(engine.Scene, SceneBuilder.MacViper, "Basic", new Vector3(1, 2, 6), rotation: rotation);
            StageLooks.ApplyOpenWhite(engine, new OpenWhiteOptions { Undoable = false });
            var fixture = head.Get<FixtureComponent>()!;
            fixture.ManualAttributes["Pan"] = 30f;
            fixture.ManualAttributes["Tilt"] = 40f;
            fixture.ManualAttributes["CTO"] = 3200f;
            fixture.NotifyChanged();
            engine.Settings.MotorDynamics = false;
            engine.Tick(Frame);
            light = Assert.Single(engine.Lights.ToArray());
            var beam = Assert.Single(engine.Dmx.GetState(head.Id)!.Beams, b => b.PeakCandela > 0f);
            beamColour = beam.Color.Rgb;
            beamCandela = beam.PeakCandela;
            Assert.True(engine.Poses.TryGet(head.Id, out var pose));
            var part = pose.PartWorld[beam.PartIndex];
            Assert.Equal(part.Translation, light.Position);
            return light.Direction;
        }

        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f);
        var d0 = Direction(Quaternion.Identity, out var l0, out var c0, out var cd0);
        var d1 = Direction(turn, out var l1, out _, out _);

        // the same pan/tilt on a fixture turned 90° about Z gives the beam turned 90° about Z
        var expected = Vector3.Transform(d0, turn);
        Assert.True(Vector3.Distance(expected, d1) < 1e-3f, $"expected {expected}, got {d1}");
        Assert.True(MathF.Abs(l0.Direction.Length() - 1f) < 1e-4f && MathF.Abs(l0.Up.Length() - 1f) < 1e-4f);
        Assert.True(MathF.Abs(Vector3.Dot(l0.Up, l0.Direction)) < 1e-4f);
        // light colour = chromaticity at luminance 1, magnitude in PeakCandela
        Assert.Equal(c0, l0.Color);
        Assert.Equal(cd0, l0.PeakCandela);
        Assert.InRange(Luminance2020(l0.Color), 0.98f, 1.02f);
        Assert.True(l0.Color.Z < l0.Color.X, "a 3200 K correction makes the light warmer than it is blue");
        Assert.Equal(l0.PeakCandela, l1.PeakCandela);
    }

    [Fact]
    public void Every_light_of_fokina_is_a_normalised_colour_at_its_beam_part()
    {
        using var engine = TestEnvironment.CreateEngine("fokina-lights");
        Assert.True(engine.Open(TestEnvironment.Fokina).Success);
        StageLooks.ApplyOpenWhite(engine, new OpenWhiteOptions { Undoable = false });
        engine.Tick(0);
        engine.Tick(Frame);
        var lights = engine.Lights.ToArray();
        Assert.NotEmpty(lights);
        int checkedLights = 0;
        foreach (var pose in engine.Poses.All)
        {
            int n = 0;
            foreach (var beam in pose.State.Beams)
            {
                if (!(beam.PeakCandela > 0f)) continue;
                var light = lights.Where(l => l.FixtureId == pose.Entity.Id).ElementAt(n++);
                Assert.Equal(pose.PartWorld[beam.PartIndex].Translation, light.Position);
                Assert.Equal(beam.PeakCandela, light.PeakCandela);
                Assert.InRange(Luminance2020(light.Color), 0.97f, 1.03f);
                Assert.True(light.FieldAngle >= light.BeamAngle);
                checkedLights++;
            }
        }
        Assert.Equal(lights.Length, checkedLights);
    }

    [Fact]
    public void Incremental_render_sync_matches_a_sync_built_from_scratch_after_edits_and_undo()
    {
        using var engine = TestEnvironment.CreateEngine("incremental");
        var scene = engine.Scene;
        SceneBuilder.Floor(scene, 16f);
        var truss = new Entity("Truss") { Local = new Transform(new Vector3(-3, 0, 6)) };
        truss.Add(new TrussComponent { Length = 6f });
        scene.Add(truss);
        var a = SceneBuilder.Fixture(scene, SceneBuilder.MacViper, "Basic", Vector3.Zero, 1, 1, "A");
        var b = SceneBuilder.Fixture(scene, SceneBuilder.Naja, null, Vector3.Zero, 1, 101, "B");
        scene.Reparent(a, truss, keepWorldTransform: false);
        scene.Reparent(b, truss, keepWorldTransform: false);
        a.Local = new Transform(new Vector3(1, 0, -0.3f));
        b.Local = new Transform(new Vector3(4, 0, -0.3f));
        var console = SceneBuilder.Console(scene, new Vector3(0, -8, 0));
        var cable = SceneBuilder.Cable(scene, console, "DMX A", a, "XLR IN");
        var point = new Entity("Hoist") { Local = new Transform(new Vector3(0, 0, 8)) };
        point.Add(new RiggingPointComponent());
        scene.Add(point);
        var layer = new Layer { Name = "Side" };
        scene.Layers.Add(layer);
        StageLooks.ApplyOpenWhite(engine, new OpenWhiteOptions { Undoable = false });

        var world = new RecordingRenderWorld();
        var sync = engine.AttachRenderWorld(world, loadFixtureModelsInBackground: false);
        engine.Tick(0);

        // a sequence of edits, each followed by a frame
        void Step() => engine.Tick(Frame);
        truss.Local = new Transform(new Vector3(-2, 1, 6.5f), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.3f), Vector3.One);
        Step();
        engine.Undo.Execute(new ReparentCommand(scene, b, null));
        Step();
        engine.Undo.Execute(new RemoveEntityCommand(scene, a)); // the cable loses its anchor
        Step();
        engine.Undo.Undo(); // a comes back under the truss, cable re-anchored
        Step();
        var c = new Entity("C") { Local = new Transform(new Vector3(0, 3, 5)) };
        c.Add(new FixtureComponent { GdtfSpec = SceneBuilder.MacViper, GdtfMode = "Basic" });
        engine.Undo.Execute(new AddEntityCommand(scene, c, truss));
        Step();
        engine.Undo.Undo();
        Step();
        engine.Undo.Redo();
        Step();
        b.LayerId = layer.Id;
        layer.Visible = false;
        engine.Selection.Set(a.Id);
        Step();
        engine.Undo.Undo(); // remove c again
        Step();
        truss.Local = new Transform(new Vector3(-1, 0, 7));
        Step();
        Assert.Empty(world.Violations);

        var incremental = Snapshot(world, sync);
        var lines = world.Lines.Select(l => Round(l.Position)).OrderBy(p => p.ToString()).ToList();
        var lights = world.Lights.Select(l => (l.FixtureId, Round(l.Position), Round(l.Direction))).OrderBy(x => x.ToString()).ToList();

        // a fresh binding of the same engine draws exactly the same
        engine.DetachRenderWorld();
        Assert.Empty(world.Instances);
        Assert.Empty(world.Meshes);
        Assert.Empty(world.Materials);
        Assert.Empty(world.Textures);
        Assert.Empty(world.Violations);
        var fresh = new RecordingRenderWorld();
        var sync2 = engine.AttachRenderWorld(fresh, loadFixtureModelsInBackground: false);
        engine.Tick(0);
        Assert.Equal(Snapshot(fresh, sync2), incremental);
        Assert.Equal(fresh.Lines.Select(l => Round(l.Position)).OrderBy(p => p.ToString()).ToList(), lines);
        Assert.Equal(fresh.Lights.Select(l => (l.FixtureId, Round(l.Position), Round(l.Direction))).OrderBy(x => x.ToString()).ToList(), lights);
        Assert.Empty(fresh.Violations);
    }

    private static Vector3 Round(Vector3 v) => new(MathF.Round(v.X, 3), MathF.Round(v.Y, 3), MathF.Round(v.Z, 3));

    private static List<string> Snapshot(RecordingRenderWorld world, RenderSync sync) =>
        world.Instances.Values.Select(i =>
        {
            Assert.True(sync.TryGetEntity(i.PickId, out var id));
            var t = Round(i.World.Translation);
            var x = Round(new Vector3(i.World.M11, i.World.M12, i.World.M13));
            return $"{id} {world.Meshes[i.Mesh.Value].Positions.Length} {t} {x} {i.Flags} {i.Materials.Length}";
        }).OrderBy(s => s, StringComparer.Ordinal).ToList();

    [Fact]
    public async Task A_recovered_session_is_continued_and_gone_after_saving()
    {
        string dir = TestEnvironment.NewTempDirectory("autosave-resume");
        var options = new AutosaveOptions { Directory = dir, Interval = TimeSpan.FromMilliseconds(10) };
        await CrashWithAutosave(options);

        var session = Assert.Single(AutosaveService.FindRecoverable(dir));
        using var recovered = TestEnvironment.CreateEngine("autosave-resume-2");
        recovered.EnableAutosave(options);
        Assert.True(recovered.Recover(session).Success);
        Assert.Single(recovered.Scene.Query<FixtureComponent>());
        Assert.Equal(session.SessionKey, recovered.Autosave!.SessionKey);

        string path = Path.Combine(TestEnvironment.NewTempDirectory("autosave-resume-file"), "show.candela");
        Assert.True(recovered.SaveAs(path).Success);
        await recovered.Autosave.FlushAsync();
        Assert.Empty(AutosaveService.FindRecoverable(dir)); // the saved file supersedes the crash data
        recovered.DisableAutosave();
        Assert.Empty(Directory.GetFiles(dir));
    }

    [Fact]
    public async Task A_recovered_session_saved_without_autosave_is_not_offered_again()
    {
        string dir = TestEnvironment.NewTempDirectory("autosave-recover-off");
        await CrashWithAutosave(new AutosaveOptions { Directory = dir, Interval = TimeSpan.FromMilliseconds(10) });
        var session = Assert.Single(AutosaveService.FindRecoverable(dir));

        using var recovered = TestEnvironment.CreateEngine("autosave-recover-off-2");
        Assert.True(recovered.Recover(session).Success);
        // not saved yet: the crash data must survive (the user may still close without saving)
        recovered.New();
        Assert.Single(AutosaveService.FindRecoverable(dir));

        Assert.True(recovered.Recover(session).Success);
        string path = Path.Combine(TestEnvironment.NewTempDirectory("autosave-recover-off-file"), "show.candela");
        Assert.True(recovered.SaveAs(path).Success);
        Assert.Empty(AutosaveService.FindRecoverable(dir));
    }

    private static async Task CrashWithAutosave(AutosaveOptions options)
    {
        using var engine = TestEnvironment.CreateEngine("autosave-crash");
        engine.EnableAutosave(options);
        SceneBuilder.Fixture(engine.Scene, SceneBuilder.MacViper, "Basic", new Vector3(0, 0, 6), 1, 1);
        for (int i = 0; i < 100 && engine.Autosave!.LastSavedUtc is null; i++)
        {
            Thread.Sleep(15);
            engine.Tick(Frame);
            await engine.Autosave.FlushAsync();
        }
        engine.Tick(0);
        Assert.NotNull(engine.Autosave!.LastSavedUtc);
        engine.DisableAutosave(completeSession: false); // the process "crashes": recovery files stay
    }
}

public sealed class FokinaRoundTripReviewTests
{
    [Fact]
    public void Fokina_saved_as_a_project_reopens_with_the_same_lights_and_instances()
    {
        static (List<string> Lights, int Instances, int Fixtures, List<string> Specs) Evaluate(StageEngine engine)
        {
            StageLooks.ApplyOpenWhite(engine, new OpenWhiteOptions { Undoable = false });
            var world = new RecordingRenderWorld();
            var sync = engine.AttachRenderWorld(world, loadFixtureModelsInBackground: false);
            engine.Settings.MotorDynamics = false;
            engine.Tick(0);
            engine.Tick(1.0 / 30);
            Assert.Empty(world.Violations);
            var lights = world.Lights.Select(l => $"{l.FixtureId} {l.Position.X:0.000} {l.Position.Y:0.000} {l.Position.Z:0.000} {l.Direction.X:0.000} {l.Direction.Y:0.000} {l.PeakCandela:0}")
                .OrderBy(s => s, StringComparer.Ordinal).ToList();
            var specs = engine.Scene.Query<FixtureComponent>()
                .Select(f => $"{f.Entity.Id} {engine.Dmx.GetResolution(f.Entity.Id)?.Entry?.FileName} {engine.Dmx.GetState(f.Entity.Id)?.Profile.Mode.Name}")
                .OrderBy(s => s, StringComparer.Ordinal).ToList();
            return (lights, sync.InstanceCount, engine.Dmx.FixtureCount, specs);
        }

        string path = Path.Combine(TestEnvironment.NewTempDirectory("fokina-project"), "fokina.candela");
        (List<string> Lights, int Instances, int Fixtures, List<string> Specs) before;
        using (var engine = TestEnvironment.CreateEngine("fokina-save"))
        {
            Assert.True(engine.Open(TestEnvironment.Fokina).Success);
            Assert.True(engine.SaveAs(path).Success);
            before = Evaluate(engine);
        }
        using var reopened = TestEnvironment.CreateEngine("fokina-reopen");
        var opened = reopened.Open(path);
        Assert.True(opened.Success, opened.Summary);
        var after = Evaluate(reopened);
        Assert.Equal(before.Fixtures, after.Fixtures);
        Assert.Equal(before.Specs, after.Specs);
        Assert.Equal(before.Instances, after.Instances);
        Assert.Equal(before.Lights, after.Lights);
    }
}
