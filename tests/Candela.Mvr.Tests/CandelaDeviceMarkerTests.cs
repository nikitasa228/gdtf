using System.Numerics;
using System.Xml.Linq;
using Candela.Core.Diagnostics;
using Candela.Core.Math;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;
using Candela.Gdtf;

namespace Candela.Mvr.Tests;

/// <summary>
/// Candela's generic devices (splitters, nodes, dimmer packs whose GDTF files Candela generates) travel through an MVR
/// as ordinary fixtures with their GDTF embedded, marked in Candela's UserData block so Candela imports them as those
/// devices again: by the device's own file name and without reporting the channel-less GDTF as a placeholder stub.
/// </summary>
public sealed class CandelaDeviceMarkerTests
{
    private const string SplitterFile = "Candela@Generic_Test_Splitter@v1.gdtf";
    private const string DimmerFile = "Candela@Generic_Test_Dimmer@v1.gdtf";

    private static readonly byte[] SplitterGdtf = ConformanceTests.ModellessGdtf("Candela", "Generic Test Splitter", 0);
    private static readonly byte[] DimmerGdtf = ConformanceTests.ModellessGdtf("Candela", "Generic Test Dimmer", 4);

    private static Scene Rig(out Entity splitter, out Entity dimmer, out Entity other)
    {
        var scene = new Scene("Devices");
        splitter = scene.Add(new Entity("Splitter") { Local = new Transform(new Vector3(1, 2, 0)) });
        splitter.Add(new FixtureComponent { GdtfSpec = SplitterFile, GdtfMode = "Mode A" });
        dimmer = scene.Add(new Entity("Dimmer") { Local = new Transform(new Vector3(3, 2, 0)) });
        dimmer.Add(new FixtureComponent { GdtfSpec = DimmerFile, GdtfMode = "Mode A", FixtureId = 7, Addresses = { new DmxAddress(1, 1) } });
        other = scene.Add(new Entity("Plain") { Local = new Transform(new Vector3(5, 2, 0)) });
        other.Add(new FixtureComponent { GdtfSpec = "Some@Bracket@v1.gdtf", GdtfMode = "Mode A" });
        return scene;
    }

    private static MvrExportOptions Options(bool mark = true) => new()
    {
        GdtfProvider = spec => spec switch
        {
            SplitterFile => SplitterGdtf,
            DimmerFile => DimmerGdtf,
            _ => ConformanceTests.ModellessGdtf("Some", "Bracket", 0),
        },
        IsCandelaDevice = mark ? spec => spec.StartsWith("Candela@Generic_", StringComparison.Ordinal) : null,
    };

    private static MvrDocument Export(Scene scene, MvrExportOptions options)
    {
        using var ms = new MemoryStream();
        MvrWriter.Write(scene, ms, options);
        return MvrReader.Read(ms.ToArray(), "devices.mvr");
    }

    private static readonly string[] StubCodes =
        [MvrIssueCodes.GdtfStub, MvrIssueCodes.GdtfUnresolved, MvrIssueCodes.GdtfSubstitute, MvrIssueCodes.GdtfNoGeometry];

    [Fact]
    public void Generic_devices_are_marked_valid_fixtures_and_come_back_as_the_same_devices()
    {
        var scene = Rig(out var splitter, out var dimmer, out var other);
        var document = Export(scene, Options());

        // other programs: ordinary fixtures with an embedded GDTF and the mode MVR requires
        foreach (var e in new[] { splitter, dimmer })
        {
            var fixture = Assert.IsType<MvrFixture>(document.AllObjects().Single(o => o.Uuid == e.Id));
            Assert.Equal(e.Get<FixtureComponent>()!.GdtfSpec, fixture.GdtfSpec);
            Assert.Equal("Mode A", fixture.GdtfMode);
            using var archive = GdtfArchive.FromBytes(document.FindFile(fixture.GdtfSpec!)!.Bytes!, fixture.GdtfSpec!);
            Assert.Contains(GdtfReader.Read(archive).DmxModes, m => m.Name == "Mode A");
        }
        // Candela: one Device record per generic device, none for the plain fixture
        var block = document.UserData.Single(u => u.Provider == CandelaUserData.Provider).Element!;
        var records = block.Elements("Device").ToList();
        Assert.Equal(2, records.Count);
        Assert.Contains(records, r => r.Attribute("object")!.Value == splitter.Id.ToString("D") && r.Attribute("gdtf")!.Value == SplitterFile
                                      && r.Attribute("mode")!.Value == "Mode A");
        Assert.DoesNotContain(records, r => r.Attribute("object")!.Value == other.Id.ToString("D"));
        Assert.Empty(block.Elements("Wall"));

        var imported = MvrImporter.Import(document);
        var bySpec = imported.AllEntities().Where(e => e.Has<FixtureComponent>()).ToDictionary(e => e.Name, e => e.Get<FixtureComponent>()!);
        Assert.Equal(SplitterFile, bySpec["Splitter"].GdtfSpec);
        Assert.Equal(DimmerFile, bySpec["Dimmer"].GdtfSpec);
        Assert.Equal("Mode A", bySpec["Dimmer"].GdtfMode);
        Assert.Equal(new DmxAddress(1, 1), Assert.Single(bySpec["Dimmer"].Addresses));
        Assert.All(imported.Gdtfs.Where(g => g.SpecName.StartsWith("Candela@", StringComparison.Ordinal)), g =>
        {
            Assert.True(g.IsCandelaDevice, g.SpecName);
            Assert.Equal(MvrGdtfSource.Embedded, g.Source);
            Assert.NotNull(g.EmbeddedBytes);
        });
        Assert.DoesNotContain(imported.Issues, i => StubCodes.Contains(i.Code) && !i.Message.Contains("Bracket", StringComparison.Ordinal));
        Assert.False(imported.Gdtfs.Single(g => g.SpecName.StartsWith("Some@", StringComparison.Ordinal)).IsCandelaDevice);

        // without the marker the channel-less splitter is an unresolved stub (what the marker avoids)
        var unmarked = MvrImporter.Import(Export(scene, Options(mark: false)));
        Assert.Contains(unmarked.Issues, i => StubCodes.Contains(i.Code) && i.Message.Contains(SplitterFile, StringComparison.Ordinal));
        Assert.DoesNotContain(unmarked.Gdtfs, g => g.IsCandelaDevice);
    }

    [Fact]
    public void A_renamed_embedded_copy_is_brought_back_under_the_device_s_own_file_name()
    {
        var document = Export(Rig(out var splitter, out _, out _), Options());
        // another program saved the file again and renamed the GDTF
        var file = document.Files.Single(f => f.Name == SplitterFile);
        document.Files.Remove(file);
        document.Files.Add(new MvrEmbeddedFile("Generic Splitter (2).gdtf", file.Bytes));
        ((MvrFixture)document.AllObjects().Single(o => o.Uuid == splitter.Id)).GdtfSpec = "Generic Splitter (2).gdtf";

        var imported = MvrImporter.Import(document);
        var fixture = imported.AllEntities().Single(e => e.Name == "Splitter").Get<FixtureComponent>()!;
        Assert.Equal(SplitterFile, fixture.GdtfSpec);
        var reference = imported.Gdtfs.Single(g => g.SpecName == "Generic Splitter (2).gdtf");
        Assert.True(reference.IsCandelaDevice);
        Assert.Equal(SplitterFile, reference.FileName);
        Assert.Equal(file.Bytes, reference.EmbeddedBytes);
    }

    [Fact]
    public void A_library_with_the_device_supplies_its_file_and_damaged_records_are_reported()
    {
        string folder = Path.Combine(Path.GetTempPath(), "candela-mvr-devices-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllBytes(Path.Combine(folder, SplitterFile), SplitterGdtf);
            var library = FixtureLibrary.Scan(folder);
            var document = Export(Rig(out _, out _, out _), Options());
            var imported = MvrImporter.Import(document, new MvrImportOptions { Library = library });
            var reference = imported.Gdtfs.Single(g => g.SpecName == SplitterFile);
            Assert.Equal((MvrGdtfSource.Library, FixtureMatchKind.ExactFileName, true), (reference.Source, reference.MatchKind, reference.IsCandelaDevice));
            Assert.DoesNotContain(imported.Issues, i => StubCodes.Contains(i.Code) && i.Message.Contains(SplitterFile, StringComparison.Ordinal));

            // damaged records: no object, a path instead of a file name, a name that is not a GDTF
            var block = document.UserData.Single(u => u.Provider == CandelaUserData.Provider).Element!;
            var records = block.Elements("Device").ToList();
            records[0].SetAttributeValue("object", "nonsense");
            records[1].SetAttributeValue("gdtf", "../../etc/passwd.gdtf");
            block.Add(new XElement("Device", new XAttribute("object", Guid.NewGuid().ToString("D")), new XAttribute("gdtf", "readme.txt")));
            var problems = new List<Issue>();
            Assert.Empty(CandelaUserData.ReadDevices(document, problems));
            Assert.Contains("3 device record(s)", Assert.Single(problems).Message, StringComparison.Ordinal);
            var again = MvrImporter.Import(document);
            Assert.Contains(again.Issues, i => i.Code == MvrIssueCodes.ValueInvalid && i.Message.Contains("device record", StringComparison.Ordinal));
            Assert.Equal(3, again.AllEntities().Count(e => e.Has<FixtureComponent>())); // still imported, as the fixtures they are
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
