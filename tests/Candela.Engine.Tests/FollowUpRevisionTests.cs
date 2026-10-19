using System.Numerics;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;
using Candela.Rendering;

namespace Candela.Engine.Tests;

/// <summary>
/// Review of the engine follow-ups: the work light is deterministic (the same scene gets the same rig, whatever the
/// heads did before), and the other findings of the review.
/// </summary>
public sealed class FollowUpRevisionTests
{
    private const double Frame = 1.0 / 30.0;

    /// <summary>The work-light rig of a scene with one moving head tilted to <paramref name="tilt"/>, reached by <paramref name="path"/>.</summary>
    private static SpotLight[] RigAfter(float tilt, bool settleFirst, string name)
    {
        using var engine = TestEnvironment.CreateEngine(name);
        var head = SceneBuilder.Fixture(engine.Scene, SceneBuilder.MacViper, "Basic", new Vector3(0, 0, 6));
        var world = new RecordingRenderWorld();
        var sync = engine.AttachRenderWorld(world, loadFixtureModelsInBackground: false);
        sync.WorkLight = WorkLightMode.On;
        engine.Tick(0);
        var fixture = head.Get<FixtureComponent>()!;
        fixture.ManualAttributes["Tilt"] = tilt;
        fixture.NotifyChanged();
        // the motors take their time: the head is still travelling when the edit is drawn
        for (int i = 0; i < (settleFirst ? 150 : 1); i++) engine.Tick(Frame);
        if (!settleFirst)
            for (int i = 0; i < 150; i++) engine.Tick(Frame);
        Assert.True(sync.IsWorkLightOn);
        return world.Lights.ToArray();
    }

    [Fact]
    public void The_work_light_rig_does_not_depend_on_where_the_heads_pointed_when_it_was_placed()
    {
        // the same scene: one engine drew the edit while the head was still moving, the other renamed the head after it
        // head arrived (an unrelated edit that makes the binding place the rig again)
        var travelling = RigAfter(110f, settleFirst: false, "work-light-travelling");
        SpotLight[] arrived;
        {
            using var engine = TestEnvironment.CreateEngine("work-light-arrived");
            var head = SceneBuilder.Fixture(engine.Scene, SceneBuilder.MacViper, "Basic", new Vector3(0, 0, 6));
            var world = new RecordingRenderWorld();
            var sync = engine.AttachRenderWorld(world, loadFixtureModelsInBackground: false);
            sync.WorkLight = WorkLightMode.On;
            engine.Tick(0);
            var fixture = head.Get<FixtureComponent>()!;
            fixture.ManualAttributes["Tilt"] = 110f;
            fixture.NotifyChanged();
            for (int i = 0; i < 150; i++) engine.Tick(Frame);
            head.Name = "Spot 1";
            engine.Tick(Frame);
            arrived = world.Lights.ToArray();
        }
        Assert.Equal(WorkLightRig.LightCount, travelling.Length);
        Assert.Equal(travelling, arrived);

        // and a head pointing elsewhere does not move the rig either
        Assert.Equal(travelling, RigAfter(-60f, settleFirst: true, "work-light-other-tilt"));
    }
}

/// <summary>Event-driven render flags with the outline of selected groups' content, undoable removals and scene changes.</summary>
public sealed class FlagRevisionTests
{
    private const double Frame = 1.0 / 30.0;
    private const InstanceFlags EntityFlags = InstanceFlags.Selected | InstanceFlags.Hidden | InstanceFlags.CableHighlight;

    private static void AssertFlagsMatchScene(StageEngine engine, RecordingRenderWorld world, RenderSync sync, string step)
    {
        var scene = engine.Scene;
        var layers = scene.Layers.ToDictionary(l => l.Id, l => l.Visible);
        var cableEnds = new HashSet<Guid>();
        foreach (var (entity, cable) in scene.Query<CableComponent>())
        {
            cableEnds.Add(entity.Id);
            foreach (var end in cable.Endpoints)
                if (end is not null && end.EntityId != Guid.Empty) cableEnds.Add(end.EntityId);
        }
        foreach (var instance in world.Instances.Values)
        {
            Assert.True(sync.TryGetEntity(instance.PickId, out var id));
            var e = scene.Find(id);
            Assert.True(e is not null, $"{step}: an instance of a removed entity is still drawn");
            bool selected = engine.Selection.Contains(id);
            if (sync.OutlineSelectedDescendants)
                for (var p = e!.Parent; p is not null && !selected; p = p.Parent) selected = engine.Selection.Contains(p.Id);
            var expected = selected ? InstanceFlags.Selected : InstanceFlags.None;
            if (!e!.IsVisibleInHierarchy || (layers.TryGetValue(e.LayerId, out bool visible) && !visible)) expected |= InstanceFlags.Hidden;
            if (cableEnds.Contains(id)) expected |= InstanceFlags.CableHighlight;
            Assert.True(expected == (instance.Flags & EntityFlags), $"{step}: {e.Name}: expected {expected}, drawn {instance.Flags & EntityFlags}");
        }
    }

    [Fact]
    public void Flags_with_outlined_group_content_follow_random_edits_undo_and_outline_toggles()
    {
        using var engine = TestEnvironment.CreateEngine("flags-outline");
        var scene = engine.Scene;
        var groups = new List<Entity>();
        for (int g = 0; g < 3; g++)
        {
            var group = new Entity($"Group {g}");
            scene.Add(group, g == 2 ? groups[0] : null); // group 2 nests in group 0
            groups.Add(group);
        }
        var items = new List<Entity>();
        for (int i = 0; i < 8; i++)
        {
            var wall = new Entity($"Wall {i}") { Local = new Core.Math.Transform(new Vector3(i * 2.5f, 0, 0)) };
            wall.Add(new WallComponent { Width = 2f, Height = 3f, Thickness = 0.2f });
            scene.Add(wall, groups[i % 3]);
            items.Add(wall);
        }
        var head = SceneBuilder.Fixture(scene, SceneBuilder.MacViper, "Basic", new Vector3(0, 0, 6));
        scene.Reparent(head, groups[2]);
        items.Add(head);
        var layer = new Layer { Name = "L" };
        scene.Layers.Add(layer);
        var world = new RecordingRenderWorld();
        var sync = engine.AttachRenderWorld(world, loadFixtureModelsInBackground: false);
        sync.OutlineSelectedDescendants = true;
        engine.Tick(0);
        AssertFlagsMatchScene(engine, world, sync, "start");

        var all = new List<Entity>(items);
        all.AddRange(groups);
        var random = new Random(4321);
        for (int step = 0; step < 200; step++)
        {
            var target = all[random.Next(all.Count)];
            switch (random.Next(10))
            {
                case 0:
                    if (target.Scene is not null) target.Visible = !target.Visible;
                    break;
                case 1:
                    engine.Selection.Set(all.Where(e => e.Scene is not null && random.Next(5) == 0).Select(e => e.Id));
                    break;
                case 2:
                    // remove a selected subtree undoably (the selection keeps or drops it), then maybe bring it back
                    if (target.Scene is not null && target != groups[0])
                    {
                        engine.Undo.Execute(new Core.Editing.RemoveEntityCommand(scene, target));
                        if (random.Next(2) == 0) engine.Undo.Undo();
                    }
                    break;
                case 3:
                    if (engine.Undo.CanUndo) engine.Undo.Undo();
                    break;
                case 4:
                    if (engine.Undo.CanRedo) engine.Undo.Redo();
                    break;
                case 5:
                    sync.OutlineSelectedDescendants = !sync.OutlineSelectedDescendants;
                    break;
                case 6 when target.Scene is not null && !groups.Contains(target):
                    var parent = groups[random.Next(groups.Count)];
                    if (parent.Scene is not null) scene.Reparent(target, parent);
                    break;
                case 7:
                    if (target.Scene is not null) target.LayerId = random.Next(2) == 0 ? layer.Id : scene.DefaultLayer.Id;
                    break;
                case 8:
                    layer.Visible = !layer.Visible;
                    break;
                default:
                    engine.Selection.Add(target.Id);
                    break;
            }
            engine.Tick(Frame);
            AssertFlagsMatchScene(engine, world, sync, $"step {step}");
        }
        Assert.Empty(world.Violations);
    }
}

/// <summary>The packet-rate recorder under concurrent receive threads.</summary>
public sealed class RecorderConcurrencyTests
{
    [Fact]
    public void Packets_from_several_receive_threads_are_all_recorded_in_time_order()
    {
        var hub = new Dmx.Input.DmxInputHub();
        var recorder = new DmxRecorder(hub);
        recorder.Start(0, "Stress");
        const int Threads = 4, Packets = 1500;
        using var go = new ManualResetEventSlim(false);
        var workers = Enumerable.Range(0, Threads).Select(t => Task.Run(() =>
        {
            var source = new Dmx.Input.DmxSourceId("artnet", $"10.0.0.{t + 1}:6454");
            var slots = new byte[512];
            go.Wait();
            for (int i = 1; i <= Packets; i++)
            {
                slots[0] = (byte)(i & 0xFF);
                slots[1] = (byte)(i >> 8);
                slots[2 + t] = (byte)t;
                hub.Submit(source, t + 1, slots);
                if (i % 100 == 0) hub.SubmitTimecode(new Dmx.Input.Timecode(0, 0, i / 100, t, Dmx.Input.TimecodeType.Ebu25), source);
            }
        })).ToArray();
        go.Set();
        double engineTime = 0;
        while (!Task.WhenAll(workers).IsCompleted)
        {
            engineTime += 0.001;
            recorder.Capture(engineTime);
            _ = recorder.FrameCount;
            _ = recorder.Duration;
        }
        Task.WaitAll(workers);
        var recording = recorder.Stop()!;

        Assert.True(recording.Frames.Zip(recording.Frames.Skip(1)).All(p => p.First.Time <= p.Second.Time));
        // every packet changed its universe, so every one of them is in the recording
        for (int t = 0; t < Threads; t++)
            Assert.Equal(Packets, recording.Frames.SelectMany(f => f.Changes).Count(c => c.Universe == t + 1));
        var state = new Dictionary<int, byte[]>();
        recording.StateAt(recording.Duration, state);
        for (int t = 0; t < Threads; t++)
        {
            Assert.Equal(Packets & 0xFF, state[t + 1][0]);
            Assert.Equal(Packets >> 8, state[t + 1][1]);
        }
    }
}

/// <summary>The library event covers every way GDTF files enter the library.</summary>
public sealed class LibraryEventRevisionTests
{
    private static StageEngine EngineWithEmptyLibrary(string name) => new(new StageEngineOptions
    {
        Library = new Gdtf.FixtureLibrary(TestEnvironment.Library.Specs),
        CacheDirectory = TestEnvironment.NewTempDirectory(name + "-cache"),
        DmxParallelism = 2,
        SimulationParallelism = 2,
    });

    [Fact]
    public void Gdtfs_that_arrive_with_an_mvr_or_a_project_raise_the_library_event_with_their_source()
    {
        string project = Path.Combine(TestEnvironment.NewTempDirectory("library-event"), "fokina.candela");
        using (var engine = EngineWithEmptyLibrary("library-event-mvr"))
        {
            var changes = new List<FixtureLibraryChange>();
            engine.FixtureLibraryChanged += changes.Add;
            Assert.True(engine.Open(TestEnvironment.Fokina).Success);
            var change = Assert.Single(changes);
            Assert.Equal(FixtureLibraryChangeSource.Mvr, change.Source);
            Assert.NotEmpty(change.Added);
            Assert.Equal(engine.Library.Count, change.Added.Count);
            Assert.All(change.Added, e => Assert.Same(e, engine.Library.FindByFileName(e.FileName)));
            Assert.True(engine.SaveAs(project).Success);

            // nothing new the second time
            Assert.True(engine.Open(TestEnvironment.Fokina).Success);
            Assert.Single(changes);
        }
        using (var engine = EngineWithEmptyLibrary("library-event-project"))
        {
            var changes = new List<FixtureLibraryChange>();
            engine.FixtureLibraryChanged += changes.Add;
            Assert.True(engine.Open(project).Success);
            var change = Assert.Single(changes);
            Assert.Equal(FixtureLibraryChangeSource.Project, change.Source);
            Assert.Equal(engine.Library.Count, change.Added.Count);
        }
    }
}

/// <summary>The CLI's work light follows the overlay of the picture it renders, not the one saved with the project.</summary>
[Collection(VulkanCollection.Name)]
public sealed class CliWorkLightTests
{
    [SkippableFact]
    public void The_cli_work_light_follows_the_rendered_overlay_not_the_projects_saved_one()
    {
        try
        {
            using var probe = Rendering.Vulkan.VulkanRenderer.Create(new Rendering.Vulkan.VulkanContextOptions
            {
                CacheDirectory = Path.Combine(AppContext.BaseDirectory, "vk-cache"),
                MinimumLogLevel = Rendering.Vulkan.VulkanLogLevel.Error,
            });
        }
        catch (Rendering.Vulkan.VulkanUnavailableException e)
        {
            Skip.If(true, $"No Vulkan device available: {e.Message}");
        }

        string dir = TestEnvironment.NewTempDirectory("cli-work-light");
        string project = Path.Combine(dir, "dark.candela");
        using (var engine = TestEnvironment.CreateEngine("cli-work-light-project"))
        {
            SceneBuilder.Floor(engine.Scene, 6f);
            // the project was saved while its view showed the lux overlay
            engine.View.View.Overlay = OverlayMode.Lux;
            Assert.True(engine.SaveAs(project).Success);
        }

        string plain = Path.Combine(dir, "plain.candela");
        using (var engine = TestEnvironment.CreateEngine("cli-work-light-plain"))
        {
            SceneBuilder.Floor(engine.Scene, 6f);
            Assert.True(engine.SaveAs(plain).Success);
        }

        string Render(params string[] extra) => RenderFile(project, extra);

        string RenderFile(string file, params string[] extra)
        {
            var output = new StringWriter();
            var error = new StringWriter();
            string[] args = ["render", file, "--out", Path.Combine(dir, $"r{extra.Length}.png"), "--width", "64", "--height", "36",
                "--look", "show", "--ev", "0", "--cache", Path.Combine(dir, "cache"), "--library", TestEnvironment.Root, .. extra];
            int code = Cli.Program.Run(args, output, error);
            Assert.True(code == Cli.Program.ExitOk, $"exit {code}\n{output}\n{error}");
            return output.ToString();
        }

        // a plain picture of the project: asked for, the work light is there although the saved view showed lux
        Assert.Contains("work light", Render("--work-light", "on"), StringComparison.Ordinal);
        // a lux picture measures stage light only
        Assert.DoesNotContain("work light", Render("--work-light", "on", "--overlay", "lux"), StringComparison.Ordinal);
        Assert.DoesNotContain("work light", Render(), StringComparison.Ordinal);
        Assert.DoesNotContain("work light", RenderFile(plain, "--work-light", "on", "--overlay", "lux"), StringComparison.Ordinal);
        Assert.Contains("work light", RenderFile(plain, "--work-light", "auto"), StringComparison.Ordinal);
    }
}
