namespace Candela.Gdtf.Tests;

/// <summary>Content checks on specific real files of the library (GDTF 1.0 and 1.2, physical data, wiring, RDM).</summary>
public sealed class RealFixtureTests
{
    [Fact]
    public void Robe_MegaPointe_has_spectral_emitter_wiring_rdm_and_resolved_dmx_modes()
    {
        var ft = GdtfReader.ReadFile(TestData.Fixture("Robe_Lighting@Robin_MegaPointe@"));
        Assert.Equal("1.2", ft.DataVersion);
        Assert.Equal("Robin MegaPointe", ft.Name);
        Assert.Equal("Robe Lighting", ft.Manufacturer);
        Assert.NotEqual(Guid.Empty, ft.FixtureTypeId);
        Assert.Empty(ft.Warnings);

        var white = ft.FindEmitter("White")!;
        Assert.Equal(0.312235f, white.Color!.Value.X, 5);
        var measurement = white.Measurements[0];
        Assert.Equal(22999f, measurement.LuminousIntensity);
        Assert.True(measurement.Points.Count > 300);
        Assert.Equal(360f, measurement.Points[0].WaveLength);

        var power = ft.AllGeometries().OfType<WiringObjectGeometry>().Single(w => w.Name == "Power IN");
        Assert.Equal(("PowerconTRUE1", WiringComponentType.Consumer, 670f, 0.97f), (power.ConnectorType, power.ComponentType, power.ElectricalPayLoad, power.CosPhi));
        Assert.Equal((100f, 240f, 50f, 60f), (power.VoltageRangeMin, power.VoltageRangeMax, power.FrequencyRangeMin, power.FrequencyRangeMax));
        Assert.True(power.IsPower);
        Assert.Contains(ft.AllGeometries().OfType<WiringObjectGeometry>(), w => w.ComponentType == WiringComponentType.NetworkInput);

        var rdm = ft.Protocols.Rdm.Single();
        Assert.Equal((0x5253u, 0xE2u), (rdm.ManufacturerId, rdm.DeviceModelId));
        Assert.All(rdm.SoftwareVersions.SelectMany(v => v.Personalities), p => Assert.NotNull(p.DmxMode));

        Assert.NotEmpty(ft.DmxModes);
        foreach (var mode in ft.DmxModes)
        {
            Assert.Same(ft.FindGeometry("Base"), mode.Geometry);
            Assert.Equal(mode.DeclaredChannelCount, mode.Footprint);
            foreach (var ch in mode.Channels)
            {
                Assert.NotNull(ch.Geometry);
                Assert.NotNull(ch.InitialFunction);
                Assert.InRange(ch.DefaultValue, 0u, ch.MaxValue);
                foreach (var f in ch.Functions)
                {
                    Assert.True(f.AttributeName == "NoFeature" || f.Attribute is not null, f.FullName);
                    Assert.True(f.DmxFromValue <= f.DmxToValue, f.FullName);
                    Assert.True(f.ModeMasterName is null || f.ModeMasterChannel is not null, f.FullName);
                    Assert.True(f.WheelName is null || f.Wheel is not null, f.FullName);
                }
            }
        }
        var pan = ft.DmxModes[0].Channels.First(c => c.LogicalChannels[0].AttributeName == "Pan");
        Assert.Equal(2, pan.Resolution);
        Assert.Same(ft.FindGeometry("Yoke"), pan.Geometry);
        Assert.Equal(PhysicalUnit.Angle, pan.LogicalChannels[0].Attribute!.PhysicalUnit);
        Assert.True(System.Math.Abs(pan.Functions.First().PhysicalTo - pan.Functions.First().PhysicalFrom) >= 360);
    }

    [Fact]
    public void Gdtf_1_0_file_keeps_its_channel_default_and_old_connectors()
    {
        var ft = GdtfReader.ReadFile(TestData.Fixture("ACME@ACME_AM-WASH300@"));
        Assert.Equal("1.0", ft.DataVersion);
        Assert.Equal(new Version(1, 0), ft.Version);
        var pan = ft.DmxModes[0].Channels.First(c => c.Offsets.SequenceEqual(new[] { 1, 2 }));
        Assert.Equal(new DmxValue(32767, 2), pan.LegacyDefault);
        Assert.Equal(32767u, pan.DefaultValue);
        Assert.Equal(SnapMode.Yes, pan.LogicalChannels[0].Snap);
        Assert.Equal(4.5f, pan.LogicalChannels[0].DmxChangeTimeLimit);
        Assert.Equal((270f, -270f), (pan.Functions.First().PhysicalFrom, pan.Functions.First().PhysicalTo));

        var adj = GdtfReader.ReadFile(TestData.Fixture("ADJ@COB_Cannon_LP200X@"));
        var connectors = adj.PhysicalDescriptions.Connectors;
        Assert.Contains(connectors, c => c.Name == "DMX-In 5pin" && c.Type == "XLR5" && c.Gender == -1);
        Assert.Contains(connectors, c => c.Type == "PowerconTRUE1" && c.Gender == 1);
    }

    [Fact]
    public void Gamuts_and_colour_spaces_are_read_from_real_files()
    {
        var ft = GdtfReader.ReadFile(TestData.Fixture("BeamZ_Pro@BS_1100_IP65@"));
        var gamut = ft.PhysicalDescriptions.Gamuts.Single();
        Assert.Equal("Gamut Collect 1", gamut.Name);
        Assert.Equal(3, gamut.Points.Count);
        Assert.Equal(0.64f, gamut.Points[0].X, 5);
        Assert.Equal((-5f, 45f, 7.05f, 0.02f), (ft.PhysicalDescriptions.Properties.OperatingTemperatureLow,
            ft.PhysicalDescriptions.Properties.OperatingTemperatureHigh, ft.PhysicalDescriptions.Properties.Weight!.Value,
            ft.PhysicalDescriptions.Properties.LegHeight));
        Assert.Equal(ColorSpaceMode.Srgb, ft.PhysicalDescriptions.ColorSpace.Mode);
        Assert.Equal(0.2126f, ft.PhysicalDescriptions.ColorSpace.Red.BigY, 3);
        Assert.Equal(0.7152f, ft.PhysicalDescriptions.ColorSpace.Green.BigY, 3);
    }

    [Fact]
    public void Stub_gdtf_from_the_mvr_scene_reads_as_a_minimal_fixture()
    {
        using var mvr = System.IO.Compression.ZipFile.OpenRead(TestData.FokinaMvr);
        var entry = mvr.GetEntry("Martin@Atomic 3000 LED.gdtf")!;
        using var stream = entry.Open();
        var ft = GdtfReader.Read(stream, entry.Name);
        Assert.Equal(("Atomic 3000 LED", "Martin", "1.0"), (ft.Name, ft.Manufacturer, ft.DataVersion));
        Assert.Equal(new[] { "Mode 1", "Mode 2" }, ft.DmxModes.Select(m => m.Name).Take(2));
        Assert.All(ft.DmxModes, m => Assert.Equal(1, m.Footprint));
        Assert.Contains(ft.Warnings, w => w.Message.Contains("Model 'Base2' is not defined", StringComparison.Ordinal));
    }
}
