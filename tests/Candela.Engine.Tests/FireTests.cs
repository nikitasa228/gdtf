using System.IO.Compression;
using System.Numerics;
using Candela.Core.SceneGraph.Components;

namespace Candela.Engine.Tests;

/// <summary>A flame projector (synthetic GDTF: the library has no GENERATOR FIRE files) driven through the engine.</summary>
public sealed class FireTests
{
    private const string FlameJet = """
        <?xml version="1.0" encoding="UTF-8"?>
        <GDTF DataVersion="1.2">
          <FixtureType Name="Flame Jet" ShortName="FJ" LongName="Test Flame Jet" Manufacturer="Test" Description="" Thumbnail=""
                       FixtureTypeID="22222222-3333-4444-5555-666666666666">
            <AttributeDefinitions>
              <FeatureGroups><FeatureGroup Name="Control" Pretty="Control"><Feature Name="Control"/></FeatureGroup></FeatureGroups>
              <Attributes>
                <Attribute Name="Flame1" Pretty="Flame" Feature="Control.Control" PhysicalUnit="Percent"/>
                <Attribute Name="Flame1Height" Pretty="Height" Feature="Control.Control" PhysicalUnit="Length"/>
              </Attributes>
            </AttributeDefinitions>
            <Models><Model Name="Body" Length="0.3" Width="0.3" Height="0.4" PrimitiveType="Cube"/></Models>
            <Geometries><Geometry Name="Body" Model="Body" Position="{1,0,0,0}{0,1,0,0}{0,0,1,0}{0,0,0,1}"/></Geometries>
            <DMXModes>
              <DMXMode Name="2ch" Geometry="Body">
                <DMXChannels>
                  <DMXChannel DMXBreak="1" Offset="1" Geometry="Body">
                    <LogicalChannel Attribute="Flame1">
                      <ChannelFunction Name="Flame" Attribute="Flame1" DMXFrom="0/1" PhysicalFrom="0" PhysicalTo="100"/>
                    </LogicalChannel>
                  </DMXChannel>
                  <DMXChannel DMXBreak="1" Offset="2" Geometry="Body">
                    <LogicalChannel Attribute="Flame1Height">
                      <ChannelFunction Name="Height" Attribute="Flame1Height" DMXFrom="0/1" PhysicalFrom="0" PhysicalTo="8"/>
                    </LogicalChannel>
                  </DMXChannel>
                </DMXChannels>
              </DMXMode>
            </DMXModes>
          </FixtureType>
        </GDTF>
        """;

    [Fact]
    public void A_flame_projector_burns_lights_the_stage_and_feeds_the_renderer()
    {
        string folder = TestEnvironment.NewTempDirectory("fire-library");
        string gdtf = Path.Combine(folder, "Test@Flame_Jet@Rev1.gdtf");
        using (var zip = ZipFile.Open(gdtf, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(zip.CreateEntry("description.xml").Open());
            writer.Write(FlameJet);
        }
        using var engine = new StageEngine(new StageEngineOptions
        {
            LibraryFolders = [folder],
            CacheDirectory = TestEnvironment.NewTempDirectory("fire-cache"),
            SimulationParallelism = 2,
        });
        SceneBuilder.Floor(engine.Scene, 12f);
        var jet = SceneBuilder.Fixture(engine.Scene, Path.GetFileName(gdtf), "2ch", new Vector3(0, 0, 0));
        jet.Get<FixtureComponent>()!.ManualAttributes = new Dictionary<string, float> { ["Flame1"] = 100f, ["Flame1Height"] = 3f };
        var world = new RecordingRenderWorld();
        engine.AttachRenderWorld(world).LoadFixtureModelsInBackground = false;

        for (int i = 0; i < 45; i++) engine.Tick(1.0 / 30);

        Assert.Single(engine.Simulation.FlameEmitters);
        Assert.False(engine.Simulation.Fire.LightSamples.IsEmpty);
        Assert.True(engine.LightCollector.FlameLightCount > 0);
        Assert.Equal(engine.Lights.Length, world.Lights.Length);
        Assert.All(world.Lights, l => Assert.Equal(jet.Id, l.FixtureId));
        Assert.True(engine.LightMeter.HorizontalIlluminanceAt(new Vector3(2, 0, 0)) > 1f, "the flame lights the floor next to it");
        var flames = world.Atmosphere?.FlameTemperature;
        Assert.NotNull(flames);
        Assert.Contains(flames!.Values, t => t > 1000f);
        Assert.Contains(engine.Simulation.SmokeEmitters, e => e.Output > 0f); // soot and heat rise into the air
        Assert.NotEmpty(world.Instances); // the device body (primitive cube)

        // valve closed: the flame dies down
        jet.Get<FixtureComponent>()!.ManualAttributes["Flame1"] = 0f;
        jet.Get<FixtureComponent>()!.NotifyChanged();
        for (int i = 0; i < 90; i++) engine.Tick(1.0 / 30);
        Assert.Equal(0, engine.LightCollector.FlameLightCount);
        Assert.Empty(world.Violations);
    }
}
