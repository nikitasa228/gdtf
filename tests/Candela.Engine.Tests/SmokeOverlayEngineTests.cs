using System.IO.Compression;
using System.Numerics;
using Candela.Core.SceneGraph.Components;
using Candela.Rendering;

namespace Candela.Engine.Tests;

/// <summary>
/// The engine's side of the smoke overlay: every hazer, fog machine, fan and flame device reaches the render world as
/// a marker with its nozzle, jet direction and output — running or switched off — and the overlay with its gradient
/// is kept in the project.
/// </summary>
public sealed class SmokeOverlayEngineTests
{
    private const string Hazer = "ADJ@Entour_Haze_PRO@2023-09-08_First_Release.gdtf"; // Haze1 + Fan1
    private const string Fogger = "PRstage@FOG_1500@Ver_1.2.gdtf"; // Fog1
    private const string LowFogger = "PRstage@LOWFOG_3000@Ver_1_3.gdtf"; // Fog1 + Blower1, a low-fog machine

    /// <summary>A flame projector (the library has no GENERATOR FIRE files).</summary>
    private const string FlameJet = """
        <?xml version="1.0" encoding="UTF-8"?>
        <GDTF DataVersion="1.2">
          <FixtureType Name="Flame Jet" ShortName="FJ" LongName="Test Flame Jet" Manufacturer="Test" Description="" Thumbnail=""
                       FixtureTypeID="22222222-3333-4444-5555-777777777777">
            <AttributeDefinitions>
              <FeatureGroups><FeatureGroup Name="Control" Pretty="Control"><Feature Name="Control"/></FeatureGroup></FeatureGroups>
              <Attributes>
                <Attribute Name="Flame1" Pretty="Flame" Feature="Control.Control" PhysicalUnit="Percent"/>
              </Attributes>
            </AttributeDefinitions>
            <Models><Model Name="Body" Length="0.3" Width="0.3" Height="0.4" PrimitiveType="Cube"/></Models>
            <Geometries><Geometry Name="Body" Model="Body" Position="{1,0,0,0}{0,1,0,0}{0,0,1,0}{0,0,0,1}"/></Geometries>
            <DMXModes>
              <DMXMode Name="1ch" Geometry="Body">
                <DMXChannels>
                  <DMXChannel DMXBreak="1" Offset="1" Geometry="Body">
                    <LogicalChannel Attribute="Flame1">
                      <ChannelFunction Name="Flame" Attribute="Flame1" DMXFrom="0/1" PhysicalFrom="0" PhysicalTo="100"/>
                    </LogicalChannel>
                  </DMXChannel>
                </DMXChannels>
              </DMXMode>
            </DMXModes>
          </FixtureType>
        </GDTF>
        """;

    private static StageEngine CreateEngine(string name)
    {
        string folder = TestEnvironment.NewTempDirectory(name + "-library");
        foreach (string file in new[] { Hazer, Fogger, LowFogger })
            File.Copy(Path.Combine(TestEnvironment.Root, file), Path.Combine(folder, file));
        using (var zip = ZipFile.Open(Path.Combine(folder, "Test@Flame_Jet@Rev1.gdtf"), ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(zip.CreateEntry("description.xml").Open());
            writer.Write(FlameJet);
        }

        return new StageEngine(new StageEngineOptions
        {
            LibraryFolders = [folder],
            CacheDirectory = TestEnvironment.NewTempDirectory(name + "-cache"),
            SimulationParallelism = 2,
        });
    }

    [Fact]
    public void Every_atmospheric_device_is_marked_with_its_output_and_direction()
    {
        using var engine = CreateEngine("smoke-markers");
        SceneBuilder.Floor(engine.Scene, 16f);
        var hazer = SceneBuilder.Fixture(engine.Scene, Hazer, "2 Channel Mode ", new Vector3(-4f, 0f, 0f));
        var fogger = SceneBuilder.Fixture(engine.Scene, Fogger, "1Ch", new Vector3(0f, -3f, 0f));
        var lowFogger = SceneBuilder.Fixture(engine.Scene, LowFogger, "2CH", new Vector3(4f, 0f, 0f));
        var flame = SceneBuilder.Fixture(engine.Scene, "Test@Flame_Jet@Rev1.gdtf", "1ch", new Vector3(0f, 4f, 0f));
        hazer.Get<FixtureComponent>()!.ManualAttributes = new() { ["Haze1"] = 100f, ["Fan1"] = 52.5f };
        lowFogger.Get<FixtureComponent>()!.ManualAttributes = new() { ["Fog1"] = 110f };
        flame.Get<FixtureComponent>()!.ManualAttributes = new() { ["Flame1"] = 100f };
        var world = new RecordingRenderWorld();
        var sync = engine.AttachRenderWorld(world);
        sync.LoadFixtureModelsInBackground = false;
        for (int i = 0; i < 10; i++) engine.Tick(1.0 / 30);

        var markers = world.SmokeEmitters;
        Assert.Equal(engine.Simulation.EmitterMarkers, markers);
        Assert.All(markers, m => Assert.True(float.IsFinite(m.Position.X + m.Position.Y + m.Position.Z) && float.IsFinite(m.Output)));
        Assert.All(markers, m => Assert.InRange(m.Direction.Length(), 0.999f, 1.001f));

        // the hazer: haze at full output and its fan at about half speed, both from its nozzle
        var haze = Assert.Single(markers, m => m.DeviceId == hazer.Id && m.Kind == SmokeEmitterMarkerKind.Hazer);
        Assert.Equal(1f, haze.Output, 1e-3f);
        var fan = Assert.Single(markers, m => m.DeviceId == hazer.Id && m.Kind == SmokeEmitterMarkerKind.Fan);
        Assert.InRange(fan.Output, 0.3f, 0.7f);
        Assert.Equal(haze.Position, fan.Position);
        Assert.Contains(engine.Simulation.SmokeEmitters, e => e.Kind == Simulation.SmokeEmitterKind.Hazer && Vector3.Distance(e.Position, haze.Position) < 1e-4f);

        // the fog machine that is off is still marked, with output 0
        var off = Assert.Single(markers, m => m.DeviceId == fogger.Id);
        Assert.Equal(SmokeEmitterMarkerKind.FogMachine, off.Kind);
        Assert.Equal(0f, off.Output);
        Assert.DoesNotContain(engine.Simulation.SmokeEmitters, e => e.Kind == Simulation.SmokeEmitterKind.FogMachine && e.Output > 0f
            && Vector3.Distance(e.Position, off.Position) < 1e-3f);

        // the low-fog machine is told apart from a fog machine
        var low = Assert.Single(markers, m => m.DeviceId == lowFogger.Id && m.Kind == SmokeEmitterMarkerKind.LowFog);
        Assert.Equal(1f, low.Output, 1e-3f);

        // the flame fires upwards from the burner
        var fire = Assert.Single(markers, m => m.DeviceId == flame.Id);
        Assert.Equal(SmokeEmitterMarkerKind.Flame, fire.Kind);
        Assert.Equal(1f, fire.Output, 1e-3f);
        Assert.True(fire.Direction.Z > 0.99f, $"flame direction {fire.Direction}");

        // switching the hazer off keeps its marker at output 0
        hazer.Get<FixtureComponent>()!.ManualAttributes["Haze1"] = 0f;
        hazer.Get<FixtureComponent>()!.NotifyChanged();
        for (int i = 0; i < 5; i++) engine.Tick(1.0 / 30);
        Assert.Equal(0f, Assert.Single(world.SmokeEmitters, m => m.DeviceId == hazer.Id && m.Kind == SmokeEmitterMarkerKind.Hazer).Output);

        // detaching clears the markers in the world
        sync.Dispose();
        Assert.Empty(world.SmokeEmitters);
        Assert.Empty(world.Violations);
    }

    [Fact]
    public void The_cli_summary_names_the_haze_the_densest_air_and_every_emitter()
    {
        using var engine = CreateEngine("smoke-summary");
        SceneBuilder.Floor(engine.Scene, 16f);
        var hazer = SceneBuilder.Fixture(engine.Scene, Hazer, "1 Channel Mode", new Vector3(-2f, 0f, 0f), name: "Hazer SL");
        SceneBuilder.Fixture(engine.Scene, Fogger, "1Ch", new Vector3(2f, 0f, 0f), name: "Fogger SR");
        hazer.Get<FixtureComponent>()!.ManualAttributes = new() { ["Haze1"] = 50f };
        engine.Scene.Settings.BaseHazeScattering = 0.03f;
        for (int i = 0; i < 60; i++) engine.Tick(1.0 / 30);

        var output = new StringWriter();
        Cli.RenderCommand.WriteSmokeSummary(engine, ColorGradient.DefaultSmoke(), null, output);
        string text = output.ToString();
        Assert.Contains("Smoke:    overlay 0.005 … 2 /m (log scale), haze 0.03 /m", text, StringComparison.Ordinal);
        Assert.Contains("Hazer SL (haze) 50 %", text, StringComparison.Ordinal);
        Assert.Contains("Fogger SR (fog) off", text, StringComparison.Ordinal);
        // the hazer has filled some air above the haze
        var densest = System.Text.RegularExpressions.Regex.Match(text, @"densest air ([0-9.]+) /m");
        Assert.True(densest.Success, text);
        Assert.True(float.Parse(densest.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) > 0.03f, text);

        // --haze overrides the scene's haze in the summary as in the picture
        var overridden = new StringWriter();
        Cli.RenderCommand.WriteSmokeSummary(engine, ColorGradient.DefaultSmoke(), 0.1f, overridden);
        Assert.Contains("haze 0.1 /m", overridden.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_smoke_overlay_and_its_gradient_are_kept_in_the_project()
    {
        string path = Path.Combine(TestEnvironment.NewTempDirectory("smoke-overlay-project"), "smoke.candela");
        using (var engine = TestEnvironment.CreateEngine("smoke-overlay-save"))
        {
            SceneBuilder.Floor(engine.Scene, 10f);
            engine.View.View.Overlay = OverlayMode.Smoke;
            engine.View.View.SmokeGradient.RangeMin = 0.01f;
            engine.View.View.SmokeGradient.RangeMax = 4f;
            engine.View.View.SmokeGradient.Stops[0] = engine.View.View.SmokeGradient.Stops[0] with { Value = 0.01f };
            Assert.True(engine.SaveAs(path).Success);
        }

        using var reopened = TestEnvironment.CreateEngine("smoke-overlay-open");
        Assert.True(reopened.Open(path).Success);
        Assert.Equal(OverlayMode.Smoke, reopened.View.View.Overlay);
        var gradient = reopened.View.View.SmokeGradient;
        Assert.Equal(0.01f, gradient.RangeMin);
        Assert.Equal(4f, gradient.RangeMax);
        Assert.True(gradient.Logarithmic);
        Assert.Equal(ColorGradient.DefaultSmoke().Stops.Count, gradient.Stops.Count);
        Assert.Equal(0f, gradient.OutOfRangeColor.W);
    }
}
