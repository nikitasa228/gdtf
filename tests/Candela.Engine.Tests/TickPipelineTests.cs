using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using Candela.Core.Math;
using Candela.Core.SceneGraph.Components;
using Candela.Dmx;
using Candela.Dmx.Input;
using Candela.Protocols;
using Candela.Protocols.ArtNet;
using Candela.Rendering;

namespace Candela.Engine.Tests;

/// <summary>The frame pipeline: protocol input → DMX → poses → simulations → render sync.</summary>
public sealed class TickPipelineTests
{
    private const double Frame = 1.0 / 30.0;

    [Fact]
    public void Art_net_packet_moves_a_moving_head_with_motor_dynamics()
    {
        using var engine = TestEnvironment.CreateEngine("artnet");
        var head = SceneBuilder.Fixture(engine.Scene, SceneBuilder.MacViper, "Basic", new Vector3(0, 0, 6), universe: 1, address: 1);
        var world = new RecordingRenderWorld();
        var sync = engine.AttachRenderWorld(world);
        sync.LoadFixtureModelsInBackground = false;
        engine.Tick(0);
        Assert.True(sync.CompletePendingLoads(TimeSpan.FromMinutes(1)));
        var before = sync.InstancesOf(head.Id).ToDictionary(i => i.Handle, i => i.World);
        Assert.NotEmpty(before);
        Assert.False(engine.Dmx.IsConsoleDriven(head.Id));

        int port = FreeUdpPort();
        var statuses = engine.StartProtocols(new[] { ProtocolIds.ArtNet }, new ProtocolOptions { ArtNetPort = port, ArtNetUniverseOffset = 1 });
        Assert.Contains(statuses, s => s.State is ProtocolState.Running or ProtocolState.Warning);

        // the console sends pan to the far end of its range (Art-Net port-address 0 = internal universe 1)
        var profile = engine.Dmx.GetState(head.Id)!.Profile;
        var buffer = new FixtureDmxBuffer(profile);
        buffer.ResetToDefaults();
        Assert.True(DmxEncoder.Encode(buffer, "Pan", 200f) > 0);
        Assert.True(DmxEncoder.Encode(buffer, "Dimmer", 100f) > 0);
        var universe = new byte[512];
        buffer.GetBreak(0).CopyTo(universe);
        using (var sender = new ArtNetSender())
        {
            var watch = Stopwatch.StartNew();
            var probe = new byte[512];
            while (!engine.Input.TryRead(1, probe, out _) && watch.Elapsed < TimeSpan.FromSeconds(5))
            {
                sender.SendDmx(new IPEndPoint(IPAddress.Loopback, port), 0, universe);
                Thread.Sleep(20);
            }
            Assert.True(engine.Input.TryRead(1, probe, out _), "no Art-Net packet arrived in the hub");
        }

        engine.Tick(Frame);
        Assert.True(engine.Dmx.IsConsoleDriven(head.Id), "a live universe without consoles drives the fixture");
        var first = sync.InstancesOf(head.Id).ToDictionary(i => i.Handle, i => i.World);
        for (int i = 0; i < 6; i++) engine.Tick(Frame);
        var later = sync.InstancesOf(head.Id).ToDictionary(i => i.Handle, i => i.World);

        float Moved(Dictionary<InstanceHandle, Matrix4x4> a, Dictionary<InstanceHandle, Matrix4x4> b) =>
            a.Keys.Max(k => Vector3.Distance(a[k].Translation, b[k].Translation) + (new Vector3(a[k].M11, a[k].M12, a[k].M13) - new Vector3(b[k].M11, b[k].M12, b[k].M13)).Length());

        float afterOne = Moved(before, first), afterSeven = Moved(before, later);
        Assert.True(afterSeven > 0.05f, $"the head did not move ({afterSeven})");
        Assert.True(afterOne < afterSeven, "the motor needs several frames to travel (no jump)");
        // the base does not turn with pan
        var baseInstance = sync.InstancesOf(head.Id).First(i => i.Part.Contains("Base", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(before[baseInstance.Handle], later[baseInstance.Handle]);
        Assert.Empty(world.Violations);
    }

    [Fact]
    public void Console_driven_fixtures_follow_the_data_cables()
    {
        using var engine = TestEnvironment.CreateEngine("console");
        var console = SceneBuilder.Console(engine.Scene, new Vector3(0, -8, 0));
        var cabled = SceneBuilder.Fixture(engine.Scene, SceneBuilder.Naja, null, new Vector3(-2, 0, 5), 1, 1, "Cabled");
        var loose = SceneBuilder.Fixture(engine.Scene, SceneBuilder.Naja, null, new Vector3(2, 0, 5), 1, 101, "Loose");
        var cable = SceneBuilder.Cable(engine.Scene, console, "DMX A", cabled, "XLR IN");
        engine.Tick(0);
        var profile = engine.Dmx.GetState(cabled.Id)!.Profile;

        // universe 1 carries full intensity for both patch ranges
        var buffer = new FixtureDmxBuffer(profile);
        buffer.ResetToDefaults();
        DmxEncoder.Encode(buffer, "Dimmer", 100f);
        DmxEncoder.Encode(buffer, "Shutter1", 1f);
        var universe = new byte[512];
        buffer.GetBreak(0).CopyTo(universe.AsSpan(0));
        buffer.GetBreak(0).CopyTo(universe.AsSpan(100));
        engine.Input.Submit(new DmxSourceId("artnet", "test"), 1, universe);

        engine.Tick(Frame);
        Assert.True(engine.HasConsoles);
        Assert.True(engine.Dmx.IsConsoleDriven(cabled.Id));
        Assert.False(engine.Dmx.IsConsoleDriven(loose.Id), "with a console in the scene only cabled fixtures follow the input");
        Assert.True(engine.Dmx.GetState(cabled.Id)!.Beams.Max(b => b.Intensity) > 0.9f);

        // unplug: the fixture falls back to its manual values
        engine.Scene.Remove(cable);
        engine.Tick(Frame);
        Assert.False(engine.Dmx.IsConsoleDriven(cabled.Id));

        // without any console a live universe drives every fixture patched in it
        engine.Scene.Remove(console);
        engine.Tick(Frame);
        Assert.False(engine.HasConsoles);
        Assert.True(engine.Dmx.IsConsoleDriven(cabled.Id));
        Assert.True(engine.Dmx.IsConsoleDriven(loose.Id));
    }

    [Fact]
    public void Hazer_output_fills_the_air_around_it_with_haze()
    {
        using var engine = TestEnvironment.CreateEngine("hazer");
        SceneBuilder.Floor(engine.Scene, 16f);
        var hazer = SceneBuilder.Fixture(engine.Scene, SceneBuilder.EntourHaze, "4 Channel Mode", new Vector3(0, 0, 0.3f));
        hazer.Get<FixtureComponent>()!.ManualAttributes = new Dictionary<string, float> { ["Haze1"] = 100f, ["Fan1"] = 100f };
        var world = new RecordingRenderWorld();
        engine.AttachRenderWorld(world).LoadFixtureModelsInBackground = false;

        engine.Tick(0);
        Assert.Equal(0.0, engine.Simulation.Time);
        for (int i = 0; i < 6 * 30; i++) engine.Tick(Frame);

        var smoke = engine.Simulation.Smoke;
        Assert.NotNull(smoke);
        Assert.True(engine.Simulation.IsAutomaticDomain);
        Assert.Contains(engine.Simulation.SmokeEmitters, e => e.Output > 0.9f);
        float near = 0f;
        for (float d = 0.5f; d <= 3f; d += 0.5f)
            foreach (var dir in new[] { Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY })
                near = MathF.Max(near, engine.Simulation.DensityAt(new Vector3(0, 0, 0.6f) + dir * d));
        float far = engine.Simulation.DensityAt(smoke!.Current.Bounds.Max - new Vector3(0.5f));
        Assert.True(near > 0.01f, $"no haze near the machine ({near})");
        Assert.True(near > 10f * far, $"haze near {near} vs far corner {far}");

        // the render world receives the density grid
        Assert.NotNull(world.Atmosphere?.SmokeDensity);
        Assert.Contains(world.Atmosphere!.SmokeDensity!.Values, v => v > 0.01f);

        // paused: the haze stays as it is
        engine.Settings.SimulationPaused = true;
        double t = smoke.Current.Time;
        engine.Tick(1);
        Assert.Equal(t, smoke.Current.Time);
    }

    [Fact]
    public void Light_meter_matches_the_inverse_square_law_under_a_fixture()
    {
        using var engine = TestEnvironment.CreateEngine("meter");
        var head = SceneBuilder.Fixture(engine.Scene, SceneBuilder.MacViper, "Basic", new Vector3(0, 0, 6), fixtureId: 101);
        StageLooks.ApplyOpenWhite(engine, new OpenWhiteOptions { Target = new Vector3(0, 0, 0), Spread = 0f });
        engine.Tick(0);
        var light = Assert.Single(engine.Lights.ToArray());

        // the head aims straight down at the target
        float angle = MathF.Acos(Math.Clamp(Vector3.Dot(light.Direction, Vector3.Normalize(-light.Position)), -1f, 1f));
        Assert.True(angle < 1.5f * MathF.PI / 180f, $"aim error {angle * 180 / MathF.PI:0.0}°");

        float d = light.Position.Length();
        float lux = engine.LightMeter.HorizontalIlluminanceAt(Vector3.Zero);
        float expected = light.PeakCandela / (d * d) * MathF.Abs(Vector3.Dot(light.Direction, -Vector3.UnitZ));
        Assert.InRange(lux, expected * 0.97f, expected * 1.03f);
        var breakdown = engine.LightMeter.Breakdown(Vector3.Zero, Vector3.UnitZ);
        Assert.Equal(head.Id, Assert.Single(breakdown).FixtureId);

        var plot = engine.LightMeter.BuildLightPlot();
        var row = Assert.Single(plot.Rows);
        Assert.Equal(101, row.FixtureId);
        Assert.NotNull(row.PowerW);
        Assert.NotNull(row.WeightKg);
        Assert.Contains("MAC Viper", plot.ToText(), StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("Fixture ID,Name,Type", plot.ToCsv(), StringComparison.Ordinal);
        Assert.True(engine.LightMeter.IncidentEv100(new Aabb(new Vector3(-2, -2, 0), new Vector3(2, 2, 0)), 0f) > 5f);
    }

    [Fact]
    public void Moving_an_entity_moves_its_fixture_pose_and_render_instances()
    {
        using var engine = TestEnvironment.CreateEngine("move");
        var head = SceneBuilder.Fixture(engine.Scene, SceneBuilder.MacViper, "Basic", new Vector3(0, 0, 6));
        var world = new RecordingRenderWorld();
        var sync = engine.AttachRenderWorld(world);
        sync.LoadFixtureModelsInBackground = false;
        engine.Tick(0);
        sync.CompletePendingLoads(TimeSpan.FromMinutes(1));
        var before = sync.InstancesOf(head.Id).Select(i => i.World.Translation).ToArray();

        head.Local = new Transform(new Vector3(3, 0, 6));
        engine.Tick(Frame);
        var after = sync.InstancesOf(head.Id).Select(i => i.World.Translation).ToArray();
        Assert.Equal(before.Length, after.Length);
        for (int i = 0; i < before.Length; i++) Assert.Equal(3f, after[i].X - before[i].X, 3);

        // selection and visibility become instance flags
        engine.Selection.Set(head.Id);
        engine.Tick(Frame);
        Assert.All(sync.InstancesOf(head.Id), i => Assert.True(i.Flags.HasFlag(Rendering.InstanceFlags.Selected)));
        head.Visible = false;
        engine.Tick(Frame);
        Assert.All(sync.InstancesOf(head.Id), i => Assert.True(world.Instances[i.Handle.Value].Flags.HasFlag(Rendering.InstanceFlags.Hidden)));

        // removing the entity destroys its instances
        engine.Scene.Remove(head);
        engine.Tick(Frame);
        Assert.Empty(sync.InstancesOf(head.Id));
        Assert.Empty(world.Instances);
        Assert.Empty(world.Violations);
    }

    private static int FreeUdpPort()
    {
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }
}
