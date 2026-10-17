using System.Text;

namespace Candela.Gdtf.Tests;

public sealed class SpecsAndArchiveTests
{
    [Fact]
    public void Specs_index_loads_every_record_with_typed_fields()
    {
        var specs = TestData.Specs;
        Assert.Equal(TestData.LibraryFileCount, specs.Count);
        Assert.Empty(specs.Warnings);
        Assert.All(TestData.GdtfFiles, f => Assert.NotNull(specs.Find(Path.GetFileName(f))));

        var face = specs.Find("ACME@ACME_(FACE_600)@ACME__(FACE_600)")!;
        Assert.Equal("1.0", face.SchemaVersion);
        Assert.Equal(FixtureCategory.Profile, face.Category);
        Assert.Equal("PROFILE", face.CategoryText);
        Assert.Equal(("FACE 600", "ACME FACE 600", "ACME"), (face.LongName, face.FullName, face.Manufacturer));
        Assert.Equal(763, face.PowerConsumptionW);
        Assert.Equal(25.0, face.WeightKg);
        Assert.Equal(43, face.NoiseLevelDb);
        Assert.Equal(150000.0, face.MaxLuminousIntensityCd);
        Assert.Equal(5000.0, face.LuminousFluxLm);
        Assert.Equal(new SpecRange(10, 30), face.BeamAngleDeg);
        Assert.Equal(15.0, face.FieldAngleDeg);
        Assert.True(face.Zoom);
        Assert.Equal(new SpecRange(2700, 8000), face.ColorTemperatureK);
        Assert.Equal("CMY", face.ColorMixing);
        Assert.Equal(7, face.SupportedPowerNetworks.Count);
        Assert.Equal(new SpecRange(100, 240), face.VoltageRangeV);
        Assert.Equal(("powerCON TRUE1", "powerCON TRUE1", "powerCON TRUE1 LINK", true), (face.PowerConnectorIn, face.PowerConnectorOut, face.PowerSupplyType, face.PowerLinkable));
        Assert.Equal("IP20", face.IpRating);
        Assert.Equal((540.0, 270.0, false), (face.PanRangeDeg!.Value, face.TiltRangeDeg!.Value, face.InfinitePan!.Value));
        Assert.Equal(new[] { "DMX512", "RDM" }, face.ControlSignal);
        Assert.Empty(face.DataConnectors);
        Assert.Equal("LED", face.LightSourceType);
        Assert.Equal("Wash", face.LensType);
        Assert.Equal((true, 4, true, "Framing shutters"), (face.FramingShutters, face.FramingShutterBlades, face.FramingShutterRotation, face.FramingShutterType));
        Assert.Equal((true, 1, true, true), (face.Frost, face.FrostFilters, face.Iris, face.Focus));
        Assert.Equal(1, face.PixelCount);
        Assert.False(face.BatteryPowered);
        Assert.Equal(new[] { new SpecDmxMode("Mode 1 28 DMX", 28), new SpecDmxMode("Mode 2 23 DMX", 23) }, face.DmxModes);
        Assert.Null(face.MaxLoadW);
        Assert.Equal(SpecSource.Gdtf, face.SourceOf("luminousFluxLm"));
        Assert.Equal(SpecSource.Estimated, face.SourceOf("maxLuminousIntensityCd"));
        Assert.Equal(SpecSource.GdtfDerived, face.SourceOf("panRangeDeg"));
        Assert.Equal(SpecSource.Unknown, face.SourceOf("noSuchField"));

        Assert.Contains(specs.All, r => r.Refs.Count > 0);
        Assert.Contains(specs.All, r => r.Issues.Count > 0);
        Assert.Contains(specs.All, r => r.MaxLoadW is > 0 && r.OutputChannels is > 0);
        Assert.Contains(specs.All, r => r.GoboWheels.Count > 0 && r.GoboWheels[0].Slots > 0);
        Assert.Contains(specs.All, r => r.Prisms.Count > 0);
    }

    [Fact]
    public void Specs_index_tolerates_values_of_unexpected_type()
    {
        const string json = """
            [
              {"file": "A@B@1.gdtf", "category": "WASH BEAM", "powerConsumptionW": "120", "weightKg": "heavy", "zoom": "yes",
               "beamAngleDeg": 25, "controlSignal": "DMX512", "dmxModes": [{"name": "M", "channels": 7}], "_source": {"weightKg": "guess"}},
              {"file": "C@D@1.gdtf", "category": "Discotheque"},
              {"category": "WASH"},
              42
            ]
            """;
        var index = FixtureSpecsIndex.Load(new MemoryStream(Encoding.UTF8.GetBytes(json)));
        Assert.Equal(2, index.Count);
        var a = index.Find("/library/A@B@1.gdtf")!;
        Assert.Equal(FixtureCategory.WashBeam, a.Category);
        Assert.Equal(120, a.PowerConsumptionW);
        Assert.Null(a.WeightKg);
        Assert.False(a.Zoom);
        Assert.Equal(new SpecRange(25, 25), a.BeamAngleDeg);
        Assert.Equal(new[] { "DMX512" }, a.ControlSignal);
        Assert.Equal(7, a.DmxModes.Single().Channels);
        Assert.Equal(SpecSource.Unknown, a.SourceOf("weightKg"));
        Assert.Equal(FixtureCategory.Other, index.Find("C@D@1")!.Category);
        Assert.True(index.Warnings.Count >= 5);
        Assert.Throws<InvalidDataException>(() => FixtureSpecsIndex.Load(new MemoryStream(Encoding.UTF8.GetBytes("{\"not\": \"an array\"}"))));
        Assert.Throws<InvalidDataException>(() => FixtureSpecsIndex.Load(new MemoryStream(Encoding.UTF8.GetBytes("[{"))));
        Assert.NotNull(FixtureSpecsIndex.TryLoadNear(Path.Combine(TestData.RepoRoot, "stages")));
    }

    [Fact]
    public void Archive_gives_access_to_description_thumbnail_wheels_and_models()
    {
        string path = TestData.Fixture("Robe_Lighting@Robin_MegaPointe@");
        foreach (var load in new[] { GdtfArchiveLoad.Lazy, GdtfArchiveLoad.InMemory })
        {
            using var archive = GdtfArchive.Open(path, load);
            Assert.Equal(Path.GetFileName(path), archive.FileName);
            Assert.Equal(Path.GetFullPath(path), archive.FilePath);
            Assert.True(archive.Contains("DESCRIPTION.XML"));
            Assert.True(archive.Contains("models\\gltf\\head.glb"));
            Assert.StartsWith("<?xml", Encoding.UTF8.GetString(archive.ReadDescription()).TrimStart('﻿'));
            Assert.Contains(archive.EntryNames, n => n.StartsWith("wheels/", StringComparison.Ordinal));
            Assert.NotNull(archive.FindThumbnail("thumbnail"));
            Assert.Equal("models/gltf/head.glb", archive.FindModelFile("head", new[] { "models/gltf" }, new[] { ".glb" }));
            Assert.Null(archive.FindModelFile("nothing", new[] { "models/gltf" }, new[] { ".glb" }));
            Assert.True(archive.GetSize("models/gltf/head.glb") > 1000);
            Assert.Equal(-1, archive.GetSize("nope"));
            Assert.Null(archive.TryReadBytes("nope"));
            Assert.Throws<FileNotFoundException>(() => archive.ReadBytes("nope"));
            using var stream = archive.TryOpen("models/gltf/head.glb")!;
            Assert.Equal(archive.GetSize("models/gltf/head.glb"), stream.Length);
        }
    }

    [Fact]
    public void Archive_opens_from_streams_and_bytes_and_reads_concurrently()
    {
        var bytes = File.ReadAllBytes(TestData.Fixture("Clay_Paky@Sharpy@"));
        using var fromStream = GdtfArchive.Open(new MemoryStream(bytes), "sharpy.gdtf");
        using var fromBytes = GdtfArchive.FromBytes(bytes, "sharpy.gdtf");
        Assert.Equal(fromStream.EntryNames, fromBytes.EntryNames);
        Assert.Null(fromBytes.FilePath);
        var lengths = new long[16];
        Parallel.For(0, lengths.Length, i => lengths[i] = fromBytes.ReadBytes(fromBytes.EntryNames[i % fromBytes.EntryNames.Count]).Length);
        for (int i = 0; i < lengths.Length; i++)
            Assert.Equal(fromBytes.GetSize(fromBytes.EntryNames[i % fromBytes.EntryNames.Count]), lengths[i]);
        Assert.Equal("Sharpy", GdtfReader.Read(fromStream).Name);

        fromBytes.Dispose();
        Assert.Throws<ObjectDisposedException>(() => fromBytes.ReadDescription());
    }

    [Fact]
    public void Wheel_media_paths_are_resolved_against_the_archive()
    {
        var ft = GdtfReader.ReadFile(TestData.Fixture("Robe_Lighting@Robin_MegaPointe@"));
        var slots = ft.Wheels.SelectMany(w => w.Slots).Where(s => s.MediaFileName is not null).ToList();
        Assert.NotEmpty(slots);
        Assert.All(slots, s => Assert.NotNull(s.MediaPath));
        Assert.Contains(ft.Wheels, w => w.Slots.Any(s => s.IsPrism && s.Facets.Count >= 3));
    }
}
