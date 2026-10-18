using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;
using Candela.Dmx.Input;

namespace Candela.Dmx.Tests;

/// <summary>The per-frame DMX engine: live vs manual control, change detection, incremental scene changes, parallelism.</summary>
public class DmxEngineTests
{
    private const string Viper = "Martin_Professional@MAC_Viper_Profile";
    private const string ArcPod = "Prolights@ArcPod27Q";
    private static readonly DmxSourceId Console1 = new("test", "console 1");

    private static FixtureProfile ViperBasic => TestData.Profile(Viper, "Basic");

    private static (Entity Entity, FixtureComponent Fixture) AddFixture(Scene scene, string prefix, string? mode, int universe, int address,
        int fixtureId = 0)
    {
        var entity = new Entity($"{prefix.Split('@')[1]} {fixtureId}");
        var fixture = entity.Add(new FixtureComponent
        {
            GdtfSpec = TestData.Entry(prefix).FileName,
            GdtfMode = mode,
            FixtureId = fixtureId,
            Addresses = { new DmxAddress(universe, address) },
        });
        scene.Add(entity);
        return (entity, fixture);
    }

    /// <summary>A universe holding the Viper's DMX for the given attribute values at <paramref name="address"/>.</summary>
    private static byte[] Universe(FixtureProfile p, int address, params (string Attribute, float Value)[] values)
    {
        var buffer = new FixtureDmxBuffer(p);
        foreach (var (a, v) in values) Assert.True(DmxEncoder.Encode(buffer, a, v) > 0, a);
        var universe = new byte[512];
        buffer.GetBreak(0).CopyTo(universe.AsSpan(address - 1));
        return universe;
    }

    [Fact]
    public void A_live_universe_drives_the_fixture_and_unchanged_input_is_skipped()
    {
        var scene = new Scene();
        var (e, _) = AddFixture(scene, Viper, "Basic", 1, 101, 7);
        var hub = new DmxInputHub();
        using var engine = new DmxEngine(scene, TestData.Profiles, hub) { ApplyDynamics = false };

        engine.Update(0);
        var state = engine.GetState(e.Id);
        Assert.NotNull(state);
        Assert.False(engine.IsConsoleDriven(e.Id)); // no source yet: manual (defaults)
        Assert.Contains(e.Id, engine.Changed);

        hub.Submit(Console1, 1, Universe(ViperBasic, 101, ("Shutter1", 1f), ("Dimmer", 0.5f), ("Pan", 90f)));
        engine.Update(0.02);
        Assert.True(engine.IsConsoleDriven(e.Id));
        Assert.Equal([e.Id], engine.Changed);
        Assert.Equal(0.5f, state!.Beams[0].Intensity, 0.005f);
        Assert.Equal(90f * MathF.PI / 180f, state.Axes[ViperBasic.Channels[ViperBasic.FindChannels("Pan")[0]].Axis].Pan, 1e-3f);
        long version = state.Version;

        engine.Update(0.04); // same DMX, nothing animating
        Assert.Empty(engine.Changed);
        Assert.Equal(version, state.Version);

        hub.Submit(Console1, 1, Universe(ViperBasic, 101, ("Shutter1", 1f), ("Dimmer", 1f), ("Pan", 90f)));
        engine.Update(0.06);
        Assert.Equal([e.Id], engine.Changed);
        Assert.Equal(1f, state.Beams[0].Intensity, 0.005f);
    }

    [Fact]
    public void Fixtures_without_a_live_source_follow_their_manual_attributes()
    {
        var scene = new Scene();
        var (e, fixture) = AddFixture(scene, Viper, "Basic", 3, 1);
        fixture.ManualAttributes["Shutter1"] = 1f;
        fixture.ManualAttributes["Dimmer"] = 0.25f;
        fixture.ManualAttributes["Tilt"] = -45f;
        var hub = new DmxInputHub();
        using var engine = new DmxEngine(scene, TestData.Profiles, hub) { ApplyDynamics = false };
        engine.Update(0);
        var state = engine.GetState(e.Id)!;
        Assert.Equal(0.25f, state.Beams[0].Intensity, 0.005f);
        Assert.True(state.TryGetAttribute("Tilt", out float tilt));
        Assert.Equal(-45f, tilt, 0.01f);

        fixture.ManualAttributes["Dimmer"] = 0.75f;
        fixture.NotifyChanged();
        engine.Update(0.1);
        Assert.Contains(e.Id, engine.Changed);
        Assert.Equal(0.75f, state.Beams[0].Intensity, 0.005f);

        // a console takes over; when its source stops (terminated) the manual values come back
        hub.Submit(Console1, 3, Universe(ViperBasic, 1, ("Shutter1", 1f), ("Dimmer", 1f)));
        engine.Update(0.2);
        Assert.True(engine.IsConsoleDriven(e.Id));
        Assert.Equal(1f, state.Beams[0].Intensity, 0.005f);
        hub.Terminate(Console1, 3);
        engine.Update(0.3);
        Assert.False(engine.IsConsoleDriven(e.Id));
        Assert.Equal(0.75f, state.Beams[0].Intensity, 0.005f);
    }

    [Fact]
    public void The_console_driven_predicate_decides_who_controls_the_fixture()
    {
        var scene = new Scene();
        var (cabled, cabledFixture) = AddFixture(scene, Viper, "Basic", 1, 1, 1);
        var (loose, looseFixture) = AddFixture(scene, Viper, "Basic", 1, 27, 2);
        cabledFixture.ManualAttributes["Dimmer"] = 0.1f;
        looseFixture.ManualAttributes["Shutter1"] = 1f;
        looseFixture.ManualAttributes["Dimmer"] = 0.3f;
        var universe = Universe(ViperBasic, 1, ("Shutter1", 1f), ("Dimmer", 0.9f));
        Universe(ViperBasic, 27, ("Shutter1", 1f), ("Dimmer", 0.9f)).AsSpan(26, 26).CopyTo(universe.AsSpan(26));
        var hub = new DmxInputHub();
        hub.Submit(Console1, 1, universe);
        using var engine = new DmxEngine(scene, TestData.Profiles, hub) { ApplyDynamics = false };
        var seen = new List<bool>();
        engine.ConsoleDriven = (entity, fixture, live) =>
        {
            seen.Add(live);
            return live && entity.Id == cabled.Id; // only the cabled one listens to the console
        };
        engine.Update(0);
        Assert.All(seen, Assert.True);
        Assert.True(engine.IsConsoleDriven(cabled.Id));
        Assert.False(engine.IsConsoleDriven(loose.Id));
        Assert.Equal(0.9f, engine.GetState(cabled.Id)!.Beams[0].Intensity, 0.005f);
        Assert.Equal(0.3f, engine.GetState(loose.Id)!.Beams[0].Intensity, 0.005f);
    }

    [Fact]
    public void Scene_changes_are_applied_incrementally()
    {
        var scene = new Scene();
        var (a, _) = AddFixture(scene, Viper, "Basic", 1, 1, 1);
        var hub = new DmxInputHub();
        hub.Submit(Console1, 1, Universe(ViperBasic, 1, ("Shutter1", 1f), ("Dimmer", 1f)));
        hub.Submit(Console1, 2, Universe(ViperBasic, 1, ("Shutter1", 1f), ("Dimmer", 0.4f)));
        using var engine = new DmxEngine(scene, TestData.Profiles, hub) { ApplyDynamics = false };
        engine.Update(0);
        Assert.Equal(1, engine.FixtureCount);
        var stateA = engine.GetState(a.Id)!;
        Assert.Equal(1f, stateA.Beams[0].Intensity, 0.005f);

        // added
        var (b, fixtureB) = AddFixture(scene, ArcPod, null, 2, 100, 2);
        engine.Update(0.02);
        Assert.Equal(2, engine.FixtureCount);
        Assert.NotNull(engine.GetState(b.Id));
        Assert.Contains(b.Id, engine.Changed);
        Assert.DoesNotContain(a.Id, engine.Changed);

        // repatched: fixture A moves to universe 2, where the dimmer is at 40 %
        var fixtureA = a.Get<FixtureComponent>()!;
        fixtureA.Addresses[0] = new DmxAddress(2, 1);
        fixtureA.NotifyChanged();
        engine.Update(0.04);
        Assert.Same(stateA, engine.GetState(a.Id)); // same profile: the state (and its motors) is kept
        Assert.Equal(0.4f, stateA.Beams[0].Intensity, 0.005f);

        // re-moded: a new profile and a new state
        fixtureB.GdtfMode = TestData.Library.GetFixtureType(TestData.Entry(ArcPod)).DmxModes[^1].Name;
        fixtureB.NotifyChanged();
        engine.Update(0.06);
        Assert.Equal(fixtureB.GdtfMode, engine.GetState(b.Id)!.Profile.Mode.Name);

        // removed
        scene.Remove(a);
        engine.Update(0.08);
        Assert.Equal(1, engine.FixtureCount);
        Assert.Null(engine.GetState(a.Id));
        Assert.Contains(a.Id, engine.Removed);
        engine.Update(0.1);
        Assert.Empty(engine.Removed);

        // fixture component removed from an entity that stays
        b.Remove(fixtureB);
        engine.Update(0.12);
        Assert.Equal(0, engine.FixtureCount);
        Assert.Contains(b.Id, engine.Removed);
    }

    [Fact]
    public void A_fixture_with_an_unknown_gdtf_has_no_state_but_a_reason()
    {
        var scene = new Scene();
        var entity = new Entity("Mystery");
        entity.Add(new FixtureComponent { GdtfSpec = "Nobody@Nothing_9000.gdtf", Addresses = { new DmxAddress(1, 1) } });
        scene.Add(entity);
        using var engine = new DmxEngine(scene, TestData.Profiles, new DmxInputHub());
        engine.Update(0);
        Assert.Null(engine.GetState(entity.Id));
        Assert.False(engine.TryGetState(entity.Id, out _));
        var resolution = engine.GetResolution(entity.Id);
        Assert.NotNull(resolution);
        Assert.Equal(ProfileErrorKind.UnknownFile, resolution!.ErrorKind);
    }

    [Fact]
    public void Motor_dynamics_move_pan_over_several_frames()
    {
        var scene = new Scene();
        var (e, _) = AddFixture(scene, Viper, "Basic", 1, 1);
        var hub = new DmxInputHub();
        hub.Submit(Console1, 1, Universe(ViperBasic, 1, ("Pan", 0f)));
        using var engine = new DmxEngine(scene, TestData.Profiles, hub);
        engine.Update(0);
        var state = engine.GetState(e.Id)!;
        int axis = ViperBasic.Channels[ViperBasic.FindChannels("Pan")[0]].Axis;
        Assert.Equal(0f, state.Axes[axis].Pan, 1e-4f);
        hub.Submit(Console1, 1, Universe(ViperBasic, 1, ("Pan", 180f)));
        engine.Update(1.0 / 60);
        float early = state.Axes[axis].Pan;
        Assert.InRange(early, 1e-5f, 0.1f);
        Assert.True(state.IsAnimating);
        double t = 1.0 / 60;
        while (t < 4)
        {
            t += 1.0 / 60;
            engine.Update(t); // no new DMX: the moving motor keeps the fixture in Changed
        }
        Assert.Equal(MathF.PI, state.Axes[axis].Pan, 1e-4f);
        Assert.False(state.IsAnimating);
        engine.Update(t + 1.0 / 60);
        Assert.Empty(engine.Changed);
    }

    [Fact]
    public void A_fixture_that_was_idle_starts_moving_from_rest_on_its_next_dmx_change()
    {
        // idle fixtures are skipped; when DMX changes after a pause, the motors start from rest at that frame, they do not
        // catch up the pause (which would make every move after an idle period instantaneous)
        var scene = new Scene();
        var (e, _) = AddFixture(scene, Viper, "Basic", 1, 1);
        var hub = new DmxInputHub();
        hub.Submit(Console1, 1, Universe(ViperBasic, 1, ("Pan", 0f)));
        using var engine = new DmxEngine(scene, TestData.Profiles, hub);
        double t = 0;
        engine.Update(t);
        var state = engine.GetState(e.Id)!;
        int axis = ViperBasic.Channels[ViperBasic.FindChannels("Pan")[0]].Axis;
        for (int frame = 0; frame < 300; frame++)
        {
            t += 1.0 / 60;
            hub.Submit(Console1, 1, Universe(ViperBasic, 1, ("Pan", 0f))); // the console keeps sending the same values
            engine.Update(t);
        }
        Assert.Empty(engine.Changed);
        hub.Submit(Console1, 1, Universe(ViperBasic, 1, ("Pan", 180f)));
        t += 1.0 / 60;
        engine.Update(t);
        Assert.Contains(e.Id, engine.Changed);
        float pan = state.Axes[axis].Pan;
        Assert.InRange(pan, 1e-5f, 0.1f); // one frame of acceleration, as for a fixture that was never idle
        Assert.True(state.IsAnimating);
    }

    [Fact]
    public void Parallel_and_sequential_evaluation_give_identical_states()
    {
        var scene = new Scene();
        string[] prefixes = [Viper, ArcPod, "Robe_Lighting@Robin_Tetra1"];
        var ids = new List<Guid>();
        int universe = 1, address = 1;
        for (int i = 0; i < 400; i++)
        {
            string prefix = prefixes[i % prefixes.Length];
            var p = TestData.Profile(prefix, prefix == Viper ? "Basic" : null);
            if (address + p.Footprint - 1 > 512)
            {
                universe++;
                address = 1;
            }
            ids.Add(AddFixture(scene, prefix, p.Mode.Name, universe, address, i + 1).Entity.Id);
            address += p.Footprint;
        }
        var hub = new DmxInputHub();
        var rng = new Random(3);
        using var sequential = new DmxEngine(scene, TestData.Profiles, hub) { MaxDegreeOfParallelism = 1 };
        using var parallel = new DmxEngine(scene, TestData.Profiles, hub) { MaxDegreeOfParallelism = 4 };
        var data = new byte[512];
        for (int frame = 0; frame < 6; frame++)
        {
            for (int u = 1; u <= universe; u++)
            {
                rng.NextBytes(data);
                hub.Submit(Console1, u, data);
            }
            sequential.Update(frame / 30.0);
            parallel.Update(frame / 30.0);
            Assert.Equal(ids.Count, parallel.Changed.Count);
        }
        foreach (var id in ids)
        {
            var s = sequential.GetState(id)!;
            var q = parallel.GetState(id)!;
            for (int c = 0; c < s.ChannelCount; c++) Assert.Equal(s.GetChannel(c), q.GetChannel(c));
            for (int b = 0; b < s.Beams.Count; b++)
            {
                Assert.Equal(s.Beams[b].PeakCandela, q.Beams[b].PeakCandela);
                Assert.Equal(s.Beams[b].Color, q.Beams[b].Color);
            }
            Assert.Equal(s.Axes, q.Axes);
        }
    }

    [Fact]
    public void Steady_state_frames_do_not_allocate()
    {
        var scene = new Scene();
        for (int i = 0; i < 200; i++) AddFixture(scene, ArcPod, null, 1 + i / 20, 1 + i % 20 * 25, i + 1);
        var hub = new DmxInputHub();
        using var engine = new DmxEngine(scene, TestData.Profiles, hub);
        var data = new byte[512];
        var rng = new Random(5);
        void Frame(double t)
        {
            for (int u = 1; u <= 10; u++)
            {
                rng.NextBytes(data);
                hub.Submit(Console1, u, data);
            }
            engine.Update(t);
        }
        for (int i = 0; i < 5; i++) Frame(i / 60.0); // warm up: states, universes, worker threads
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 5; i < 10; i++) Frame(i / 60.0);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(200, engine.Changed.Count);
    }
}
