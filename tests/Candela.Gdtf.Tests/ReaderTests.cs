using System.Text;

namespace Candela.Gdtf.Tests;

/// <summary>Reader behaviour on synthetic descriptions: complete coverage of the format and tolerance of bad input.</summary>
public sealed class ReaderTests
{
    internal const string LedBar = """
        <?xml version="1.0" encoding="UTF-8"?>
        <GDTF DataVersion="1.2">
          <FixtureType Name="Bar 4" ShortName="B4" LongName="Test Bar 4" Manufacturer="Test" Description="d" Thumbnail="thumbnail"
                       FixtureTypeID="11111111-2222-3333-4444-555555555555" RefFT="" CanHaveChildren="No">
            <AttributeDefinitions>
              <ActivationGroups><ActivationGroup Name="ColorRGB"/></ActivationGroups>
              <FeatureGroups>
                <FeatureGroup Name="Dimmer" Pretty="Dimmer"><Feature Name="Dimmer"/></FeatureGroup>
                <FeatureGroup Name="Color" Pretty="Color"><Feature Name="RGB"/></FeatureGroup>
              </FeatureGroups>
              <Attributes>
                <Attribute Name="Dimmer" Pretty="Dim" Feature="Dimmer.Dimmer" PhysicalUnit="LuminousIntensity"/>
                <Attribute Name="ColorAdd_R" Pretty="R" ActivationGroup="ColorRGB" Feature="Color.RGB" PhysicalUnit="ColorComponent" Color="0.64,0.33,21.3"/>
                <Attribute Name="ColorAdd_G" Pretty="G" ActivationGroup="ColorRGB" Feature="Color.RGB" PhysicalUnit="ColorComponent"/>
                <Attribute Name="Shutter1" Pretty="Sh1" Feature="Dimmer.Dimmer" PhysicalUnit="Frequency"/>
                <Attribute Name="Shutter1Strobe" Pretty="Strobe" MainAttribute="Shutter1" Feature="Dimmer.Dimmer" PhysicalUnit="Frequency">
                  <SubPhysicalUnit Type="Duration" PhysicalUnit="Time" PhysicalFrom="0" PhysicalTo="2"/>
                </Attribute>
              </Attributes>
            </AttributeDefinitions>
            <Wheels>
              <Wheel Name="Gobo1">
                <Slot Name="Open" Color="0.3127,0.3290,100.0"/>
                <Slot Name="Dots" MediaFileName="gobo_dots"/>
                <Slot Name="Prism"><Facet Color="0.3,0.3,100" Rotation="{1,0,0}{0,1,0}{0.5,0,1}"/></Slot>
              </Wheel>
            </Wheels>
            <PhysicalDescriptions>
              <Emitters>
                <Emitter Name="Red" Color="0.7,0.29,20" DominantWaveLength="625" DiodePart="XP-E">
                  <Measurement Physical="100" LuminousIntensity="12.5" InterpolationTo="Linear">
                    <MeasurementPoint WaveLength="620" Energy="0.5"/><MeasurementPoint WaveLength="630" Energy="1"/>
                  </Measurement>
                </Emitter>
              </Emitters>
              <Filters><Filter Name="CTO" Color="0.45,0.41,60"><Measurement Physical="100" Transmission="0.6"/></Filter></Filters>
              <ColorSpace Mode="Custom" Red="0.68,0.31,20" Green="0.2,0.7,70" Blue="0.14,0.05,8" WhitePoint="0.3127,0.329,100"/>
              <AdditionalColorSpaces><ColorSpace Name="Wide" Mode="ProPhoto"/></AdditionalColorSpaces>
              <Gamuts><Gamut Name="LEDs" Points="{0.7,0.29,1}{0.17,0.7,1}{0.14,0.04,1}"/></Gamuts>
              <DMXProfiles><DMXProfile Name="Square"><Point DMXPercentage="0" CFC0="0" CFC1="0" CFC2="1" CFC3="0"/></DMXProfile></DMXProfiles>
              <CRIs><CRIGroup ColorTemperature="3200"><CRI CES="CES01" ColorRenderingIndex="91"/></CRIGroup></CRIs>
              <Properties>
                <OperatingTemperature Low="-20" High="45"/>
                <Weight Value="4.2"/>
                <LegHeight Value="0.05"/>
                <PowerConsumption Value="180" PowerFactor="0.95" Connector="Power In" VoltageLow="100" VoltageHigh="240" FrequencyLow="50" FrequencyHigh="60"/>
              </Properties>
            </PhysicalDescriptions>
            <Models>
              <Model Name="Body" Length="1" Width="0.1" Height="0.08" PrimitiveType="Cube"/>
              <Model Name="PixelModel" Length="0.24" Width="0.1" Height="0.01" PrimitiveType="Cylinder" File=""/>
            </Models>
            <Geometries>
              <Geometry Name="Body" Model="Body" Position="{1,0,0,0}{0,1,0,0}{0,0,1,0}{0,0,0,1}">
                <GeometryReference Name="Pixel 1" Geometry="Pixel" Position="{1,0,0,-0.375}{0,1,0,0}{0,0,1,-0.04}{0,0,0,1}"><Break DMXBreak="1" DMXOffset="2"/></GeometryReference>
                <GeometryReference Name="Pixel 2" Geometry="Pixel" Position="{1,0,0,-0.125}{0,1,0,0}{0,0,1,-0.04}{0,0,0,1}"><Break DMXBreak="1" DMXOffset="5"/></GeometryReference>
                <GeometryReference Name="Pixel 3" Geometry="Pixel" Position="{1,0,0,0.125}{0,1,0,0}{0,0,1,-0.04}{0,0,0,1}"><Break DMXBreak="1" DMXOffset="8"/></GeometryReference>
                <GeometryReference Name="Pixel 4" Geometry="Pixel" Model="Body" Position="{1,0,0,0.375}{0,1,0,0}{0,0,1,-0.04}{0,0,0,1}"><Break DMXBreak="1" DMXOffset="1.11"/></GeometryReference>
                <WiringObject Name="Power In" ConnectorType="PowerconTRUE1" ComponentType="Consumer" SignalType="Power" PinCount="3"
                              ElectricalPayLoad="180" VoltageRangeMin="100" VoltageRangeMax="240" FrequencyRangeMin="50" FrequencyRangeMax="60"
                              CosPhi="0.95" FuseCurrent="10" FuseRating="C" Orientation="Bottom" WireGroup="Mains">
                  <PinPatch ToWiringObject="Power Out" FromPin="1" ToPin="1"/>
                </WiringObject>
                <WiringObject Name="Power Out" ConnectorType="PowerconTRUE1" ComponentType="Output" SignalType="Power" PinCount="3" MaxPayLoad="3000"/>
                <Laser Name="Laser" ColorType="SingleWaveLength" Color="532" OutputStrength="0.5" Emitter="Red" BeamDiameter="0.002"
                       BeamDivergenceMin="0.5" BeamDivergenceMax="1" ScanAnglePan="40" ScanAngleTilt="30" ScanSpeed="30000">
                  <Protocol Name="ILDA"/>
                </Laser>
                <Support Name="Rope" SupportType="Rope" CapacityX="10" CapacityY="20" CapacityZ="300" ResistanceZ="5" RopeOffset="0,0,0.1"/>
                <Inventory Name="Clamp" Count="2"/>
                <Magnet Name="Magnet"/>
                <Display Name="Screen" Texture="screen"/>
                <Structure Name="Tube" LinkedGeometry="Body" StructureType="Detail" CrossSectionType="Tube" CrossSectionHeight="0.05"/>
              </Geometry>
              <Geometry Name="Pixel" Model="PixelModel">
                <Beam Name="Pixel Beam" LampType="LED" PowerConsumption="15" LuminousFlux="600" ColorTemperature="5600" BeamAngle="20"
                      FieldAngle="35" BeamRadius="0.02" BeamType="Wash" ColorRenderingIndex="90" EmitterSpectrum="Red"/>
              </Geometry>
            </Geometries>
            <DMXModes>
              <DMXMode Name="13ch" Geometry="Body" Description="Dimmer + 4 RGB pixels">
                <DMXChannels>
                  <DMXChannel DMXBreak="1" Offset="1" Geometry="Body" InitialFunction="Body_Dimmer.Dimmer.Dimmer 1" Highlight="255/1">
                    <LogicalChannel Attribute="Dimmer" Snap="No" Master="Grand" MibFade="0.5" DMXChangeTimeLimit="0.1">
                      <ChannelFunction Attribute="Dimmer" DMXFrom="0/1" Default="0/1" PhysicalFrom="0" PhysicalTo="1000" RealFade="0.2" DMXProfile="Square">
                        <ChannelSet Name="Closed" DMXFrom="0/1" PhysicalFrom="0" PhysicalTo="0"/>
                        <ChannelSet Name="" DMXFrom="1/1"/>
                        <ChannelSet Name="Open" DMXFrom="255/1" PhysicalFrom="1000" PhysicalTo="1000"/>
                      </ChannelFunction>
                    </LogicalChannel>
                  </DMXChannel>
                  <DMXChannel DMXBreak="1" Offset="1" Geometry="Pixel" InitialFunction="Pixel_ColorAdd_R.ColorAdd_R.Red">
                    <LogicalChannel Attribute="ColorAdd_R">
                      <ChannelFunction Name="Red" Attribute="ColorAdd_R" DMXFrom="0/1" Default="255/1" Emitter="Red" ColorSpace="Wide" Gamut="LEDs"/>
                    </LogicalChannel>
                  </DMXChannel>
                  <DMXChannel DMXBreak="1" Offset="2" Geometry="Pixel">
                    <LogicalChannel Attribute="ColorAdd_G">
                      <ChannelFunction Name="Green" Attribute="ColorAdd_G" DMXFrom="0/1" Filter="CTO"/>
                    </LogicalChannel>
                  </DMXChannel>
                  <DMXChannel DMXBreak="1" Offset="3" Geometry="Pixel">
                    <LogicalChannel Attribute="Shutter1">
                      <ChannelFunction Name="Gobo" Attribute="Shutter1" DMXFrom="0/1" Wheel="Gobo1" ModeMaster="Body_Dimmer" ModeFrom="0/1" ModeTo="127/1">
                        <ChannelSet Name="Open" DMXFrom="0/1" WheelSlotIndex="1"/>
                        <ChannelSet Name="Dots" DMXFrom="100/1" WheelSlotIndex="2"/>
                        <ChannelSet Name="Missing" DMXFrom="200/1" WheelSlotIndex="9"/>
                      </ChannelFunction>
                      <ChannelFunction Name="Strobe" Attribute="Shutter1Strobe" DMXFrom="0/1" ModeMaster="Body_Dimmer.Dimmer.Dimmer 1" ModeFrom="128/1" ModeTo="255/1">
                        <SubChannelSet Name="Slow" PhysicalFrom="0" PhysicalTo="1" SubPhysicalUnit="Shutter1Strobe.Duration"/>
                      </ChannelFunction>
                    </LogicalChannel>
                  </DMXChannel>
                  <DMXChannel DMXBreak="1" Offset="None" Geometry="Pixel">
                    <LogicalChannel Attribute="Dimmer"><ChannelFunction Name="Virtual" Attribute="Dimmer" DMXFrom="0/2" Default="65535/2"/></LogicalChannel>
                  </DMXChannel>
                </DMXChannels>
                <Relations>
                  <Relation Name="VirtualDimmer" Master="Pixel_Dimmer" Follower="Pixel_ColorAdd_R.ColorAdd_R.Red" Type="Multiply"/>
                </Relations>
                <FTMacros>
                  <FTMacro Name="Reset" ChannelFunction="Body_Dimmer.Dimmer.Dimmer 1">
                    <MacroDMX><MacroDMXStep Duration="2"><MacroDMXValue Value="255/1" DMXChannel="Body_Dimmer"/></MacroDMXStep></MacroDMX>
                  </FTMacro>
                </FTMacros>
              </DMXMode>
            </DMXModes>
            <Revisions><Revision Text="First" Date="2026-01-02T03:04:05" UserID="17" ModifiedBy="Tester"/></Revisions>
            <FTPresets/>
            <Protocols>
              <FTRDM ManufacturerID="0x1234" DeviceModelID="0x0042">
                <SoftwareVersionID Value="5"><DMXPersonality Value="0x0001" DMXMode="13ch"/></SoftwareVersionID>
              </FTRDM>
              <Art-Net><Map Key="1" Value="2"/></Art-Net>
            </Protocols>
          </FixtureType>
        </GDTF>
        """;

    [Fact]
    public void Reads_identity_attribute_definitions_wheels_and_physical_descriptions()
    {
        var ft = TestData.ReadXml(LedBar);
        Assert.Equal("1.2", ft.DataVersion);
        Assert.Equal(("Bar 4", "B4", "Test Bar 4", "Test"), (ft.Name, ft.ShortName, ft.LongName, ft.Manufacturer));
        Assert.Equal(Guid.Parse("11111111-2222-3333-4444-555555555555"), ft.FixtureTypeId);
        Assert.Null(ft.RefFixtureTypeId);
        Assert.False(ft.CanHaveChildren);
        Assert.Equal("thumbnail", ft.Thumbnail);

        var defs = ft.AttributeDefinitions;
        var red = ft.FindAttribute("ColorAdd_R")!;
        Assert.Same(defs.ActivationGroups[0], red.ActivationGroup);
        Assert.Contains(red, defs.ActivationGroups[0].Attributes);
        Assert.Equal("Color.RGB", red.Feature!.FullName);
        Assert.Equal(PhysicalUnit.ColorComponent, red.PhysicalUnit);
        Assert.Equal(new Candela.Core.SceneGraph.Components.CieColor(0.64f, 0.33f, 21.3f), red.Color);
        var strobe = ft.FindAttribute("Shutter1Strobe")!;
        Assert.Same(ft.FindAttribute("Shutter1"), strobe.MainAttribute);
        Assert.Equal(SubPhysicalUnitType.Duration, strobe.SubPhysicalUnits.Single().Type);

        var wheel = ft.FindWheel("Gobo1")!;
        Assert.Equal(3, wheel.Slots.Count);
        Assert.Equal("gobo_dots", wheel.GetSlot(2)!.MediaFileName);
        Assert.Null(wheel.GetSlot(2)!.MediaPath); // no archive: resources cannot be checked
        var facet = wheel.GetSlot(3)!.Facets.Single();
        Assert.True(wheel.GetSlot(3)!.IsPrism);
        Assert.Equal(0.5f, facet.Transform.M31);

        var pd = ft.PhysicalDescriptions;
        var emitter = ft.FindEmitter("Red")!;
        Assert.Equal(625f, emitter.DominantWaveLength);
        Assert.Equal("XP-E", emitter.DiodePart);
        Assert.Equal(2, emitter.Measurements.Single().Points.Count);
        Assert.Equal(12.5f, emitter.Measurements[0].LuminousIntensity);
        Assert.Equal(0.6f, ft.FindFilter("CTO")!.Measurements.Single().Transmission);
        Assert.Equal(ColorSpaceMode.Custom, pd.ColorSpace.Mode);
        Assert.Equal(ColorSpace.DefaultName, pd.ColorSpace.Name);
        Assert.Equal(1f, pd.ColorSpace.Red.BigY + pd.ColorSpace.Green.BigY + pd.ColorSpace.Blue.BigY, 3);
        Assert.Equal(ColorSpaceMode.ProPhoto, pd.FindColorSpace("Wide")!.Mode);
        Assert.Equal(3, pd.Gamuts.Single().Points.Count);
        Assert.Equal(0.25f, pd.DmxProfiles.Single().Evaluate(50f), 4);
        Assert.Equal(91, pd.CriGroups.Single().Cris.Single().ColorRenderingIndex);
        Assert.Equal((-20f, 45f, 4.2f, 0.05f), (pd.Properties.OperatingTemperatureLow, pd.Properties.OperatingTemperatureHigh,
            pd.Properties.Weight!.Value, pd.Properties.LegHeight));
        var power = pd.Properties.PowerConsumptions.Single();
        Assert.Equal((180f, 0.95f, 100f, 240f), (power.Value, power.PowerFactor, power.VoltageLow, power.VoltageHigh));
        Assert.Same(ft.FindGeometry("Power In"), power.Connector);
        Assert.DoesNotContain(ft.Warnings, w => !w.Message.Contains("slot 9", StringComparison.Ordinal));
    }

    [Fact]
    public void Reads_every_geometry_type_with_its_attributes()
    {
        var ft = TestData.ReadXml(LedBar);
        var body = ft.Geometries[0];
        Assert.Same(ft.FindModel("Body"), body.Model);
        Assert.Equal(GdtfPrimitiveType.Cube, body.Model!.PrimitiveType);

        var refs = body.Children.OfType<GeometryReference>().ToList();
        Assert.Equal(4, refs.Count);
        Assert.All(refs, r => Assert.Same(ft.Geometries[1], r.ReferencedGeometry));
        Assert.Equal(new GeometryBreak(1, 2), refs[0].Breaks.Single());
        Assert.Equal(new GeometryBreak(1, 11), refs[3].Breaks.Single()); // "1.11" = universe 1, address 11
        Assert.Equal(-0.375f, refs[0].Position.Translation.X, 5);

        var power = (WiringObjectGeometry)ft.FindGeometry("Power In")!;
        Assert.Equal(("PowerconTRUE1", WiringComponentType.Consumer, "Power", 3), (power.ConnectorType, power.ComponentType, power.SignalType, power.PinCount));
        Assert.Equal((180f, 100f, 240f, 50f, 60f), (power.ElectricalPayLoad, power.VoltageRangeMin, power.VoltageRangeMax, power.FrequencyRangeMin, power.FrequencyRangeMax));
        Assert.Equal((0.95f, 10f, FuseRating.C, WiringOrientation.Bottom, "Mains"), (power.CosPhi, power.FuseCurrent, power.FuseRating, power.Orientation, power.WireGroup));
        Assert.True(power.IsPower);
        Assert.Same(ft.FindGeometry("Power Out"), power.PinPatches.Single().ToWiringObject);
        Assert.Equal(3000f, ((WiringObjectGeometry)ft.FindGeometry("Power Out")!).MaxPayLoad);

        var laser = (LaserGeometry)ft.FindGeometry("Laser")!;
        Assert.Equal((LaserColorType.SingleWaveLength, 532f, 0.5f), (laser.ColorType, laser.ColorWaveLength!.Value, laser.OutputStrength));
        Assert.Same(ft.FindEmitter("Red"), laser.Emitter);
        Assert.Equal("ILDA", laser.Protocols.Single());

        var support = (SupportGeometry)ft.FindGeometry("Rope")!;
        Assert.Equal(new System.Numerics.Vector3(10, 20, 300), support.Capacity);
        Assert.Equal(5f, support.Resistance.Z);
        Assert.Equal(0.1f, support.RopeOffset.Z, 5);
        Assert.Equal(2, ((InventoryGeometry)ft.FindGeometry("Clamp")!).Count);
        Assert.IsType<MagnetGeometry>(ft.FindGeometry("Magnet"));
        Assert.Equal("screen", ((DisplayGeometry)ft.FindGeometry("Screen")!).Texture);
        var tube = (StructureGeometry)ft.FindGeometry("Tube")!;
        Assert.Same(body, tube.LinkedGeometry);
        Assert.Equal(CrossSectionType.Tube, tube.CrossSectionType);

        var beam = (BeamGeometry)ft.FindGeometry("Pixel Beam")!;
        Assert.Equal((LampType.Led, 15f, 600f, 5600f, 20f, 35f, 0.02f, 90f), (beam.LampType, beam.PowerConsumption, beam.LuminousFlux,
            beam.ColorTemperature, beam.BeamAngle, beam.FieldAngle, beam.BeamRadius, beam.ColorRenderingIndex));
        Assert.Same(ft.FindEmitter("Red"), beam.EmitterSpectrum);
    }

    [Fact]
    public void Resolves_dmx_references_and_expands_geometry_references_into_channel_instances()
    {
        var ft = TestData.ReadXml(LedBar);
        var mode = ft.FindMode("13ch")!;
        Assert.Same(ft.Geometries[0], mode.Geometry);
        Assert.Equal(5, mode.Channels.Count);

        var dimmer = mode.FindChannel("Body_Dimmer")!;
        Assert.Same(dimmer.Functions.Single(), dimmer.InitialFunction);
        Assert.Equal(255u, dimmer.Highlight!.Value.Value);
        var lc = dimmer.LogicalChannels.Single();
        Assert.Equal((ChannelMaster.Grand, 0.5f, 0.1f), (lc.Master, lc.MibFade, lc.DmxChangeTimeLimit));
        var dimFn = dimmer.Functions.Single();
        Assert.Equal("Dimmer 1", dimFn.Name); // GDTF default name "{Attribute} {n}"
        Assert.Same(ft.PhysicalDescriptions.DmxProfiles[0], dimFn.DmxProfile);
        Assert.Equal((0u, 255u), (dimFn.DmxFromValue, dimFn.DmxToValue));
        Assert.Equal(1000f * 127 / 255, dimFn.PhysicalAt(127), 0.01f);
        var sets = dimFn.ChannelSets;
        Assert.Equal((0u, 0u), (sets[0].DmxFromValue, sets[0].DmxToValue));
        Assert.Equal((1u, 254u), (sets[1].DmxFromValue, sets[1].DmxToValue));
        Assert.Equal((255u, 255u), (sets[2].DmxFromValue, sets[2].DmxToValue));

        var red = mode.FindChannel("Pixel_ColorAdd_R")!.Functions.Single();
        Assert.Same(ft.FindEmitter("Red"), red.Emitter);
        Assert.Same(ft.PhysicalDescriptions.FindColorSpace("Wide"), red.ColorSpace);
        Assert.Same(ft.PhysicalDescriptions.Gamuts[0], red.Gamut);
        Assert.Same(ft.FindFilter("CTO"), mode.FindChannel("Pixel_ColorAdd_G")!.Functions.Single().Filter);

        // Two functions with the same mode master and disjoint mode ranges may both start at 0.
        var shutter = mode.FindChannel("Pixel_Shutter1")!;
        var gobo = shutter.Functions.First();
        var strobe = shutter.Functions.Last();
        Assert.Same(dimmer, gobo.ModeMasterChannel);
        Assert.Null(gobo.ModeMasterFunction);
        Assert.Same(dimFn, strobe.ModeMasterFunction);
        Assert.Equal(255u, gobo.DmxToValue);
        Assert.Equal(255u, strobe.DmxToValue);
        Assert.Same(ft.FindWheel("Gobo1")!.GetSlot(2), gobo.ChannelSets[1].WheelSlot);
        Assert.Null(gobo.ChannelSets[2].WheelSlot);
        Assert.Contains(ft.Warnings, w => w.Message.Contains("slot 9 does not exist", StringComparison.Ordinal));
        Assert.Same(ft.FindAttribute("Shutter1Strobe")!.SubPhysicalUnits[0], strobe.SubChannelSets.Single().SubPhysicalUnit);

        var virtualDimmer = mode.FindChannel("Pixel_Dimmer")!;
        Assert.True(virtualDimmer.IsVirtual);
        Assert.Equal(2, virtualDimmer.Resolution);
        Assert.Same(virtualDimmer, mode.Relations.Single().Master);
        Assert.Same(red, mode.Relations.Single().Follower);
        Assert.Equal(RelationType.Multiply, mode.Relations[0].Type);
        Assert.Same(dimFn, mode.Macros.Single().ChannelFunction);
        Assert.Same(dimmer, mode.Macros[0].Steps.Single().Values.Single().DmxChannel);

        // 1 dimmer + 4 pixels × 3 addressed channels; pixel 1 at 2-4, pixel 2 at 5-7, pixel 3 at 8-10, pixel 4 at 11-13.
        Assert.Equal(3, mode.DeclaredChannelCount);
        Assert.Equal(13, mode.Footprint);
        var redInstances = mode.ChannelInstances.Where(i => i.Channel.Name == "Pixel_ColorAdd_R").ToList();
        Assert.Equal(new[] { 2, 5, 8, 11 }, redInstances.Select(i => i.FirstOffset));
        Assert.Equal(new[] { "Pixel 1", "Pixel 2", "Pixel 3", "Pixel 4" }, redInstances.Select(i => i.InstanceName));
        Assert.Equal(4, mode.ChannelInstances.Count(i => i.Channel.IsVirtual));
        Assert.Equal(1 + 4 * 4, mode.ChannelInstances.Count);

        var rdm = ft.Protocols.Rdm.Single();
        Assert.Equal((0x1234u, 0x42u), (rdm.ManufacturerId, rdm.DeviceModelId));
        Assert.Same(mode, rdm.SoftwareVersions.Single().Personalities.Single().DmxMode);
        Assert.Equal("Art-Net", ft.Protocols.Others.Single().Name);
        var revision = ft.Revisions.Single();
        Assert.Equal((17u, "Tester", new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc)), (revision.UserId, revision.ModifiedBy, revision.Date!.Value));
    }

    [Fact]
    public void Geometry_builder_flattens_the_tree_and_expands_references()
    {
        var ft = TestData.ReadXml(LedBar);
        var geometry = new FixtureGeometryBuilder(ft, m => new LoadedModel(PrimitiveMeshes.Create(m.PrimitiveType, m.Size, m.Name)!,
            new ModelLoadInfo(ModelSourceKind.Primitive, null, false, 1, System.Numerics.Vector3.One, m.Size, m.Size))).Build();
        Assert.Equal("Body", geometry.Parts[0].Path);
        var pixels = geometry.Parts.Where(p => p.InstancedBy is not null).ToList();
        Assert.Equal(4, pixels.Count);
        Assert.Equal("Body/Pixel 3", pixels[2].Path);
        Assert.Equal("Body/Pixel 3/Pixel Beam", geometry.Parts[pixels[2].Index + 1].Path);
        Assert.Same(ft.FindModel("PixelModel"), pixels[0].ModelDefinition);
        Assert.Same(ft.FindModel("Body"), pixels[3].ModelDefinition); // the reference's Model replaces the top node's model
        Assert.All(geometry.Parts, p => Assert.True(p.ParentIndex < p.Index));

        var world = new System.Numerics.Matrix4x4[geometry.Parts.Count];
        geometry.ComputeWorldTransforms(world, System.Numerics.Matrix4x4.CreateTranslation(0, 0, 5));
        var pixel3 = world[pixels[2].Index].Translation;
        Assert.Equal(0.125f, pixel3.X, 1e-5f);
        Assert.Equal(0f, pixel3.Y, 1e-5f);
        Assert.Equal(4.96f, pixel3.Z, 1e-5f);

        // Each pixel channel instance maps to the part of its own pixel.
        var mode = ft.DmxModes[0];
        var instance = mode.ChannelInstances.First(i => i.Channel.Name == "Pixel_ColorAdd_R" && i.InstanceName == "Pixel 3");
        int part = geometry.FindPart(instance);
        Assert.Equal("Body/Pixel 3", geometry.Parts[part].Path);
    }

    [Fact]
    public void Dangling_references_and_bad_values_become_warnings()
    {
        var ft = TestData.ReadXml("""
            <GDTF DataVersion="1.1">
              <FixtureType Name="Broken" Manufacturer="M" FixtureTypeID="not-a-guid">
                <AttributeDefinitions><Attributes><Attribute Name="Pan" Feature="Position.Nope" PhysicalUnit="Furlongs"/></Attributes></AttributeDefinitions>
                <Wheels><Wheel Name="W"><Slot Name="S" Color="red" Filter="Nope"/></Wheel></Wheels>
                <Models><Model Name="M" Length="-1" Width="abc" Height="0.2" PrimitiveType="Blob"/></Models>
                <Geometries>
                  <Geometry Name="Body" Model="Missing">
                    <GeometryReference Name="R" Geometry="Nowhere"/>
                    <Teleporter Name="X"/>
                  </Geometry>
                </Geometries>
                <DMXModes>
                  <DMXMode Name="Mode" Geometry="NoSuchGeometry">
                    <DMXChannels>
                      <DMXChannel Offset="1,x" Geometry="Ghost" InitialFunction="Nope">
                        <LogicalChannel Attribute="Tilt">
                          <ChannelFunction Attribute="Tilt" DMXFrom="300/1" Wheel="NoWheel" Emitter="NoEmitter" ModeMaster="NoChannel"/>
                        </LogicalChannel>
                      </DMXChannel>
                      <DMXChannel Offset="2" Geometry="Body"/>
                    </DMXChannels>
                    <Relations><Relation Name="R" Master="Nope" Follower="Nope" Type="Divide"/></Relations>
                  </DMXMode>
                </DMXModes>
                <Mystery/>
              </FixtureType>
            </GDTF>
            """);
        Assert.Equal("Broken", ft.Name);
        Assert.Equal(Guid.Empty, ft.FixtureTypeId);
        string all = string.Join("\n", ft.Warnings);
        foreach (var expected in new[]
        {
            "FixtureTypeID", "Feature 'Position.Nope'", "Furlongs", "\"red\"", "Filter 'Nope'", "Negative model dimension", "Width=\"abc\"",
            "Blob", "Model 'Missing'", "'Nowhere' is not defined", "<Teleporter>", "'NoSuchGeometry' is not defined", "\"x\"", "'Ghost'",
            "attribute 'Tilt'", "DMXFrom=\"300/1\"", "wheel 'NoWheel'", "emitter 'NoEmitter'", "Mode master 'NoChannel'", "Initial function 'Nope'",
            "no logical channel", "Master 'Nope'", "Follower 'Nope'", "Divide", "<Mystery>",
        })
            Assert.Contains(expected, all);
        var mode = ft.DmxModes.Single();
        Assert.Same(ft.Geometries[0], mode.Geometry); // falls back to the first top-level geometry
        Assert.Equal(new[] { 1 }, mode.Channels[0].Offsets);
        Assert.Equal(2, mode.Footprint);
    }

    [Theory]
    [InlineData("not a zip at all")]
    [InlineData("")]
    public void Unreadable_archives_throw_a_format_exception(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        Assert.Throws<GdtfFormatException>(() => GdtfReader.Read(new MemoryStream(bytes), "bad.gdtf"));
    }

    [Fact]
    public void Archive_without_description_or_with_malformed_xml_throws_a_format_exception()
    {
        var noDescription = TestData.Zip(("thumbnail.png", new byte[] { 1, 2, 3 }));
        Assert.Throws<GdtfFormatException>(() => GdtfReader.Read(new MemoryStream(noDescription)));
        var malformed = TestData.GdtfWithDescription("<GDTF><FixtureType Name=\"x\"></GDTF>");
        Assert.Throws<GdtfFormatException>(() => GdtfReader.Read(new MemoryStream(malformed)));
        var noFixtureType = TestData.GdtfWithDescription("<GDTF DataVersion=\"1.2\"><Nothing/></GDTF>");
        Assert.Throws<GdtfFormatException>(() => GdtfReader.Read(new MemoryStream(noFixtureType)));
        Assert.False(GdtfReader.TryReadFile(Path.Combine(TestData.RepoRoot, "no-such-file.gdtf"), out var ft, out var error));
        Assert.Null(ft);
        Assert.NotNull(error);
    }

    [Fact]
    public void Empty_fixture_type_reads_with_defaults()
    {
        var ft = GdtfReader.Read(new MemoryStream(TestData.GdtfWithDescription("<GDTF DataVersion=\"1.0\"><FixtureType Name=\"Empty\"/></GDTF>")), "e.gdtf");
        Assert.Equal("Empty", ft.Name);
        Assert.Equal(new Version(1, 0), ft.Version);
        Assert.Empty(ft.DmxModes);
        Assert.Null(ft.FindMode(null));
        Assert.Equal(ColorSpaceMode.Srgb, ft.PhysicalDescriptions.ColorSpace.Mode);
        var geometry = new FixtureGeometryBuilder(ft).Build();
        Assert.Single(geometry.Parts);
        Assert.Equal("e.gdtf", ft.SourceFileName);
    }
}
