using System.Numerics;
using Candela.Core.SceneGraph.Components;
using Candela.Dmx;
using Candela.Dmx.Input;

namespace Candela.Engine.Tests;

/// <summary>The frame pipeline beyond the basics: noise sources, daisy chains, determinism of the simulations.</summary>
public sealed class TickReviewTests
{
    private const double Frame = 1.0 / 30.0;

    [Fact]
    public void Noise_sources_follow_the_specs_level_scaled_by_device_activity()
    {
        using var engine = TestEnvironment.CreateEngine("noise");
        var head = SceneBuilder.Fixture(engine.Scene, SceneBuilder.MacViper, "Basic", new Vector3(0, 0, 6));
        var hazer = SceneBuilder.Fixture(engine.Scene, SceneBuilder.EntourHaze, "4 Channel Mode", new Vector3(4, 0, 0.3f));
        engine.Settings.IdleNoiseShare = 0.25f;
        var world = new RecordingRenderWorld();
        engine.AttachRenderWorld(world, loadFixtureModelsInBackground: false);

        // dark head: its fans idle (the idle share of its 47 dB(A)); the hazer at DMX 0 only runs its fan at the
        // minimum speed its GDTF gives (no haze), so it is as loud as that
        engine.Tick(Frame);
        Assert.Equal(2, world.NoiseSources.Length);
        Assert.True(engine.Poses.TryGet(head.Id, out var pose));
        var source = Assert.Single(world.NoiseSources, s => s.Position == pose.Position);
        Assert.Equal(47f + 10f * MathF.Log10(0.25f), source.DecibelsAt1m, 2);
        var idle = engine.Dmx.GetState(hazer.Id)!.Outputs;
        float hazerActivity = Math.Max(Math.Max(idle.Haze, idle.Fog), idle.FanSpeed);
        Assert.True(hazerActivity < 0.1f);
        Assert.Equal(50f + 10f * MathF.Log10(hazerActivity), Assert.Single(world.NoiseSources, s => s.Position != pose.Position).DecibelsAt1m, 2);

        // head at full, hazer running: both at their specified level
        StageLooks.ApplyOpenWhite(engine, new OpenWhiteOptions { Undoable = false });
        hazer.Get<FixtureComponent>()!.ManualAttributes = new Dictionary<string, float> { ["Haze1"] = 100f, ["Fan1"] = 100f };
        hazer.Get<FixtureComponent>()!.NotifyChanged();
        engine.Settings.MotorDynamics = false;
        engine.Tick(Frame);
        Assert.Equal(2, world.NoiseSources.Length);
        Assert.Contains(world.NoiseSources, s => MathF.Abs(s.DecibelsAt1m - 47f) < 0.05f);
        Assert.Contains(world.NoiseSources, s => MathF.Abs(s.DecibelsAt1m - 50f) < 0.05f);
        Assert.True(engine.Simulation.NoiseLevelAt(new Vector3(4, 1, 1.5f)) > 40f);

        engine.Settings.SimulateNoise = false;
        engine.Tick(Frame);
        Assert.Empty(world.NoiseSources);
    }

    [Fact]
    public void A_fixture_daisy_chained_behind_a_cabled_one_is_console_driven()
    {
        using var engine = TestEnvironment.CreateEngine("daisy");
        var console = SceneBuilder.Console(engine.Scene, new Vector3(0, -8, 0));
        var first = SceneBuilder.Fixture(engine.Scene, SceneBuilder.Naja, null, new Vector3(-2, 0, 5), 1, 1, "First");
        var second = SceneBuilder.Fixture(engine.Scene, SceneBuilder.MacViper, "Basic", new Vector3(2, 0, 5), 1, 101, "Second");
        var third = SceneBuilder.Fixture(engine.Scene, SceneBuilder.MacViper, "Basic", new Vector3(6, 0, 5), 1, 201, "Not cabled");
        SceneBuilder.Cable(engine.Scene, console, "DMX A", first, "XLR IN");
        var link = SceneBuilder.Cable(engine.Scene, first, "XLR OUT", second, "DMX IN");
        engine.Input.Submit(new DmxSourceId("artnet", "desk"), 1, new byte[512]);
        engine.Tick(Frame);
        Assert.True(engine.Dmx.IsConsoleDriven(first.Id));
        Assert.True(engine.Dmx.IsConsoleDriven(second.Id));
        Assert.False(engine.Dmx.IsConsoleDriven(third.Id));

        // undoing the link (scene edit) unlocks the second fixture on the next frame
        engine.Undo.Execute(new Core.Editing.RemoveEntityCommand(engine.Scene, link));
        engine.Tick(Frame);
        Assert.False(engine.Dmx.IsConsoleDriven(second.Id));
        engine.Undo.Undo();
        engine.Tick(Frame);
        Assert.True(engine.Dmx.IsConsoleDriven(second.Id));
    }

    private static void Fixture(StageEngine engine, Guid id, string gdtf, string mode, Vector3 position, int address)
    {
        var entity = new Core.SceneGraph.Entity(gdtf[..8], id) { Local = new Core.Math.Transform(position) };
        var fixture = new FixtureComponent { GdtfSpec = gdtf, GdtfMode = mode };
        fixture.Addresses.Add(new DmxAddress(1, address));
        entity.Add(fixture);
        engine.Scene.Add(entity);
    }

    [Fact]
    public void Smoke_and_lights_are_identical_for_the_same_input_whatever_the_thread_counts()
    {
        (float[] Density, float[] Lights) Run(int threads)
        {
            using var engine = new StageEngine(new StageEngineOptions
            {
                Library = TestEnvironment.Library,
                CacheDirectory = TestEnvironment.NewTempDirectory("determinism-cache"),
                DmxParallelism = threads,
                SimulationParallelism = threads,
            });
            SceneBuilder.Floor(engine.Scene, 12f);
            // the same scene: entity ids seed the fixtures' random effects (random strobe …)
            Fixture(engine, new Guid(1, 0, 0, new byte[8]), SceneBuilder.EntourHaze, "4 Channel Mode", new Vector3(0, 0, 0.3f), 1);
            for (int i = 0; i < 6; i++)
                Fixture(engine, new Guid(2 + i, 0, 0, new byte[8]), SceneBuilder.MacViper, "Basic", new Vector3(-3 + i, 0, 6), 10 + 30 * i);
            var levels = new byte[512];
            for (int frame = 0; frame < 45; frame++)
            {
                for (int s = 0; s < 512; s++) levels[s] = (byte)((s * 7 + frame * 13) % 256);
                levels[0] = 255; // haze
                levels[1] = 255; // fan
                engine.Input.Submit(new DmxSourceId("artnet", "desk"), 1, levels);
                engine.Tick(frame % 3 == 0 ? Frame : 0.5 * Frame);
            }
            var smoke = engine.Simulation.Smoke!;
            var density = new float[smoke.Current.CellCount];
            smoke.Current.CopyDensityTo(density);
            var lights = engine.Lights.ToArray().SelectMany(l => new[] { l.Position.X, l.Direction.X, l.Direction.Y, l.PeakCandela, l.Color.X, l.BeamAngle }).ToArray();
            return (density, lights);
        }

        var one = Run(1);
        var four = Run(4);
        Assert.Equal(one.Lights, four.Lights);
        Assert.Contains(one.Density, d => d > 0f);
        Assert.Equal(one.Density, four.Density);
    }
}
