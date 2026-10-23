using System.Numerics;
using Candela.App.Session;
using Candela.Core.SceneGraph.Components;
using Candela.Dmx.Input;
using Candela.Engine;
using Candela.Rendering;

namespace Candela.App.Tests;

/// <summary>
/// A sequence render ticks the engine frame by frame from its preroll on, so the smoke must be simulated with exactly
/// those frames (lockstep) from the first preroll tick — not in the editor's real time, which merges frames that arrive
/// faster than wall-clock time and drops what does not fit, leaving the haze of the first frames unsettled.
/// </summary>
[Collection(UiStateCollection.Name)]
public sealed class SmokeRenderJobTests
{
    private const string EntourHaze = "ADJ@Entour_Haze_PRO@2023-09-08_First_Release.gdtf";

    [Fact]
    public void A_sequence_render_simulates_the_preroll_smoke_frame_by_frame_and_hands_real_time_back()
    {
        using var dir = new TempDir();
        using var engine = new StageEngine(new StageEngineOptions
        {
            Library = StageTestSupport.Library,
            CacheDirectory = dir.File("cache"),
            DmxParallelism = 2,
            SimulationParallelism = 2,
            RealTimeSimulation = true,
        });
        StageTestSupport.AddWall(engine, "Floor", 12, 10, WallKind.Floor);
        // Not patched: the manual values drive it whatever the recording plays.
        var hazer = StageTestSupport.AddFixture(engine, "Hazer", 1, new Vector3(0, 0, 0.3f), universe: 0, spec: EntourHaze);
        var fixture = hazer.Get<FixtureComponent>()!;
        fixture.ManualAttributes["Haze1"] = 100f;
        fixture.ManualAttributes["Fan1"] = 100f;
        fixture.NotifyChanged();

        // Two seconds of timecode to render from.
        var source = new DmxSourceId("test", "console");
        engine.Recorder.Start(engine.Time, "timecode");
        for (int i = 0; i < 50; i++)
        {
            engine.Input.Submit(source, 1, new byte[512]);
            engine.Input.SubmitTimecode(new Timecode(0, 0, 10 + i / 25, i % 25, TimecodeType.Ebu25));
            engine.Tick(1.0 / 25);
        }

        var recording = engine.Recorder.Stop()!;
        engine.Input.Terminate(source, 1);
        var smoke = engine.Simulation.Smoke;
        Assert.NotNull(smoke);
        Assert.True(engine.Simulation.RealTime);
        smoke!.Synchronize();
        double before = smoke.Current.Time, dropped = smoke.DroppedTime;

        using var job = new RenderJobRunner();
        var range = new RenderRange(recording, 1.5, 1.6, 25);
        var camera = RenderCamera.LookAt(new Vector3(0, -9, 5), Vector3.Zero);
        Assert.True(job.Start(new RenderRequest(camera, new ViewOptions(), new PreviewSettings(), 64, 36, 1, 1, dir.File("seq.png"), range), engine),
            job.Message);
        Assert.True(job.HoldsEngineClock);

        // The preroll (1.5 s in 38 frames of 1/25 s) was simulated completely, frame by frame.
        Assert.False(engine.Simulation.RealTime, "the job ticks the engine itself: lockstep from the preroll on");
        double preroll = Math.Round(1.5 * 25) / 25;
        Assert.Equal(dropped, smoke.DroppedTime);
        Assert.InRange(smoke.Current.Time - before, preroll - 1.0 / 30 - 1e-9, preroll + 1.0 / 30 + 1e-9);

        // Cancelling hands the clock back: real time again.
        job.Cancel();
        Assert.False(job.HoldsEngineClock);
        Assert.True(engine.Simulation.RealTime);
    }
}
