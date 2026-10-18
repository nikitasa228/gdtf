using System.Diagnostics;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using System.Xml.Linq;
using Candela.Core.Diagnostics;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;
using Candela.Gdtf;
using Xunit.Abstractions;
using static Candela.Mvr.Tests.FokinaTests;

namespace Candela.Mvr.Tests;

/// <summary>
/// DIN SPEC 15801 details and hostile input: 3D files without extension, uuids written as hex byte pairs, mandatory
/// elements on export, GDTFs without drawable geometry that must not be swapped for another model, and files built to
/// exhaust the stack or the geometry expansion.
/// </summary>
public sealed class ConformanceTests
{
    private const string LayerId = "44444444-0000-4000-8000-0000000000AA";
    private readonly ITestOutputHelper _out;

    public ConformanceTests(ITestOutputHelper output) => _out = output;

    private static string Scene(string childList, string aux = "", string userData = "") => $$"""
        <?xml version="1.0" encoding="UTF-8"?>
        <GeneralSceneDescription verMajor="1" verMinor="6" provider="Test" providerVersion="1">
          {{userData}}
          <Scene>
            <AUXData>{{aux}}</AUXData>
            <Layers><Layer uuid="{{LayerId}}" name="L"><ChildList>{{childList}}</ChildList></Layer></Layers>
          </Scene>
        </GeneralSceneDescription>
        """;

    private static byte[] Archive(string xml, params (string Name, byte[] Data)[] files) =>
        TestData.Zip(new[] { ("GeneralSceneDescription.xml", TestData.Utf8(xml)) }.Concat(files).ToArray());

    private void Dump(IEnumerable<Issue> issues)
    {
        foreach (var i in issues) _out.WriteLine($"{i.Severity} {i.Code}: {i.Message}");
    }

    [Fact]
    public void Geometry3D_without_extension_is_a_3ds_file()
    {
        // "If there is no extension, it will assume that the extension is 3ds" (Geometry3D fileName).
        string xml = Scene("""
            <SceneObject uuid="11111111-0000-4000-8000-0000000000A1" name="Truss by stem">
              <Geometries><Geometry3D fileName="truss"/></Geometries>
            </SceneObject>
            <SceneObject uuid="11111111-0000-4000-8000-0000000000A2" name="Bare file">
              <Geometries><Geometry3D fileName="block"/></Geometries>
            </SceneObject>
            <SceneObject uuid="11111111-0000-4000-8000-0000000000A3" name="Same truss, other spellings">
              <Geometries><Geometry3D fileName="truss.3ds"/><Geometry3D fileName="./TRUSS.3DS"><Matrix>{1,0,0}{0,1,0}{0,0,1}{0,0,1000}</Matrix></Geometry3D></Geometries>
            </SceneObject>
            """);
        var result = MvrImporter.Import(MvrReader.Read(Archive(xml, ("truss.3ds", SyntheticMvr.TrussThreeDs()), ("block", SyntheticMvr.TrussThreeDs())), "ext.mvr"));
        Dump(result.Issues);
        Assert.DoesNotContain(result.Issues, i => i.Severity != IssueSeverity.Note);
        Assert.Equal(2, result.Assets.Count); // one asset per archive file, however the references spell it
        Assert.All(result.Assets, a => Assert.Equal(ModelFileFormat.ThreeDs, a.Format));
        var spellings = result.FindEntity(Guid.Parse("11111111-0000-4000-8000-0000000000A3"))!;
        Assert.Equal(2, spellings.Children.Count);
        Assert.Single(spellings.Children.Select(c => c.Get<MeshComponent>()!.ModelAssetId).Distinct());
        foreach (var id in new[] { "11111111-0000-4000-8000-0000000000A1", "11111111-0000-4000-8000-0000000000A2" })
        {
            var mesh = result.FindEntity(Guid.Parse(id))!.Get<MeshComponent>();
            Assert.NotNull(mesh);
            var bounds = result.Assets.Single(a => a.Id == mesh!.ModelAssetId).Decode().ComputeBounds();
            AssertNear(SyntheticMvr.TrussSizeMm / 1000f, bounds.Max, 1e-5f); // millimetres → metres as for .3ds
        }
    }

    [Fact]
    public void Uuids_written_as_hex_byte_pairs_are_resolved()
    {
        // The DIN SPEC 15801 Fixture example writes Focus and Position as "4A B1 94 62 …".
        string xml = Scene(
            """
            <FocusPoint uuid="4AB19462-A6E3-4C3B-B25A-D8099F78170C" name="Target"><Matrix>{1,0,0}{0,1,0}{0,0,1}{1000,2000,0}</Matrix><Geometries/></FocusPoint>
            <Fixture uuid="11111111-0000-4000-8000-0000000000B1" name="Spot">
              <Focus>4A B1 94 62 A6 E3 4C 3B B2 5A D8 09 9F 78 17 0C</Focus>
              <Position>77 BC DE 16 95 A6 47 25 9D 04 16 A0 BD 67 CD 1A</Position>
              <FixtureID>1</FixtureID>
            </Fixture>
            """,
            aux: """<Position uuid="77BCDE16-95A6-4725-9D04-16A0BD67CD1A" name="Electric"/>""");
        var doc = MvrReader.Read(Archive(xml), "hex.mvr");
        Dump(doc.Issues);
        Assert.DoesNotContain(doc.Issues, i => i.Code == MvrIssueCodes.ValueInvalid);
        var spot = doc.AllObjects().OfType<MvrFixture>().Single();
        Assert.Equal(Guid.Parse("4AB19462-A6E3-4C3B-B25A-D8099F78170C"), spot.Focus);
        Assert.Equal(doc.AuxData.Positions.Single().Uuid, spot.Position);
        var result = MvrImporter.Import(doc);
        Assert.Equal(new Vector3(1, 2, 0), result.FindEntity(spot.Uuid)!.Get<FixtureComponent>()!.FocusPoint);
        Assert.DoesNotContain(result.Issues, i => i.Code == MvrIssueCodes.FocusMissing);
    }

    [Fact]
    public void Matrix_values_that_overflow_single_precision_are_reported()
    {
        string xml = Scene("""
            <SceneObject uuid="11111111-0000-4000-8000-0000000000C1" name="Far away">
              <Matrix>{1,0,0}{0,1,0}{0,0,1}{1e300,0,0}</Matrix>
              <Geometries/>
            </SceneObject>
            """);
        var doc = MvrReader.Read(Archive(xml));
        var issue = Assert.Single(doc.Issues, i => i.Code == MvrIssueCodes.MatrixInvalid);
        Assert.Contains(Guid.Parse("11111111-0000-4000-8000-0000000000C1"), issue.Entities);
        Assert.Equal(Matrix4x4.Identity, doc.AllObjects().Single().Matrix);
    }

    [Fact]
    public void Unknown_elements_are_reported_as_kept_only_where_a_round_trip_keeps_them()
    {
        string xml = Scene("""
            <Fixture uuid="11111111-0000-4000-8000-0000000000C2" name="F">
              <Addresses><Address>1</Address><Vendor/></Addresses>
              <VendorExtension/>
              <FixtureID>1</FixtureID>
            </Fixture>
            """);
        var doc = MvrReader.Read(Archive(xml));
        Assert.Contains(doc.Issues, i => i.Message.Contains("<Vendor> in <Addresses>", StringComparison.Ordinal) && i.Message.Contains("not kept", StringComparison.Ordinal));
        Assert.Contains(doc.Issues, i => i.Message.Contains("<VendorExtension> in <Fixture>", StringComparison.Ordinal) && i.Message.Contains("kept in the document", StringComparison.Ordinal));
        var again = MvrReader.Read(MvrWriter.WriteDocument(doc)).AllObjects().Single();
        Assert.Equal("VendorExtension", Assert.Single(again.UnknownElements).Name.LocalName);
    }

    [Fact]
    public void Merge_into_a_scene_without_layers_adds_one()
    {
        var target = new Scene("Empty");
        target.Layers.Clear();
        var result = MvrImporter.Merge(target, MvrReader.Read(Archive(Scene("""<SceneObject uuid="11111111-0000-4000-8000-0000000000C4" name="Box"><Geometries/></SceneObject>"""))));
        Assert.Equal(new[] { "Default", "L" }, target.Layers.Select(l => l.Name));
        Assert.Equal(target.Layers[0].Id, result.Group!.LayerId);
        Assert.Equal(target.Layers[1].Id, result.FindEntity(Guid.Parse("11111111-0000-4000-8000-0000000000C4"))!.LayerId);
    }

    [Fact]
    public void A_repeated_dmx_break_keeps_the_first_address_and_is_reported()
    {
        string xml = Scene("""
            <Fixture uuid="11111111-0000-4000-8000-0000000000C3" name="Twice">
              <Addresses><Address break="0">10</Address><Address break="1">600</Address><Address break="0">20</Address></Addresses>
              <FixtureID>1</FixtureID>
            </Fixture>
            """);
        var doc = MvrReader.Read(Archive(xml));
        Assert.Equal(3, doc.AllObjects().OfType<MvrFixture>().Single().Addresses.Count); // the document stays faithful
        var result = MvrImporter.Import(doc);
        var fc = result.FindEntity(Guid.Parse("11111111-0000-4000-8000-0000000000C3"))!.Get<FixtureComponent>()!;
        Assert.Equal(new[] { new DmxAddress(1, 10, 1), new DmxAddress(2, 88, 2) }, fc.Addresses);
        var issue = Assert.Single(result.Issues, i => i.Code == MvrIssueCodes.AddressInvalid);
        Assert.Contains("break 0", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Address_zero_means_unpatched_and_is_one_note()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 50; i++)
            sb.Append($"""<Fixture uuid="{Guid.NewGuid()}" name="F{i}"><Addresses><Address break="0">0</Address></Addresses><FixtureID>{i}</FixtureID></Fixture>""");
        var doc = MvrReader.Read(Archive(Scene(sb.ToString())));
        var issue = Assert.Single(doc.Issues, i => i.Code == MvrIssueCodes.AddressInvalid);
        Assert.Equal(IssueSeverity.Note, issue.Severity);
        Assert.Contains("not patched", issue.Message, StringComparison.Ordinal);
        Assert.All(doc.AllObjects().OfType<MvrFixture>(), f => Assert.Empty(f.Addresses));
    }

    /// <summary>
    /// A minimal GDTF without a drawable model: <paramref name="channels"/> DMX channels in one mode "Mode A" (zero
    /// channels: a passive device, like a bracket).
    /// </summary>
    private static byte[] ModellessGdtf(string manufacturer, string name, int channels)
    {
        string[] attributes = { "Dimmer", "Pan", "Tilt", "Zoom" };
        var dmx = new StringBuilder();
        for (int i = 0; i < channels; i++)
        {
            dmx.Append($"""
                <DMXChannel DMXBreak="1" Geometry="Base" Offset="{i + 1}"><LogicalChannel Attribute="{attributes[i]}"><ChannelFunction Attribute="{attributes[i]}" DMXFrom="0/1" Name="{attributes[i]}"/></LogicalChannel></DMXChannel>
                """);
        }
        string xml = $$"""
            <?xml version="1.0" encoding="UTF-8"?>
            <GDTF DataVersion="1.2">
              <FixtureType Name="{{name}}" ShortName="X" LongName="{{name}}" Manufacturer="{{manufacturer}}" Description="" FixtureTypeID="{{Guid.NewGuid()}}" CanHaveChildren="No">
                <AttributeDefinitions>
                  <FeatureGroups>
                    <FeatureGroup Name="Dimmer" Pretty="Dimmer"><Feature Name="Dimmer"/></FeatureGroup>
                    <FeatureGroup Name="Position" Pretty="Position"><Feature Name="PanTilt"/></FeatureGroup>
                    <FeatureGroup Name="Focus" Pretty="Focus"><Feature Name="Focus"/></FeatureGroup>
                  </FeatureGroups>
                  <Attributes>
                    <Attribute Name="Dimmer" Pretty="Dim" Feature="Dimmer.Dimmer" PhysicalUnit="None"/>
                    <Attribute Name="Pan" Pretty="P" Feature="Position.PanTilt" PhysicalUnit="Angle"/>
                    <Attribute Name="Tilt" Pretty="T" Feature="Position.PanTilt" PhysicalUnit="Angle"/>
                    <Attribute Name="Zoom" Pretty="Zoom" Feature="Focus.Focus" PhysicalUnit="Angle"/>
                  </Attributes>
                </AttributeDefinitions>
                <Models><Model Name="Base" File=""/></Models>
                <Geometries><Geometry Name="Base" Model="Base" Position="{1,0,0,0}{0,1,0,0}{0,0,1,0}{0,0,0,1}"/></Geometries>
                <DMXModes>
                  <DMXMode Name="Mode A" Geometry="Base"><DMXChannels>{{dmx}}</DMXChannels></DMXMode>
                </DMXModes>
              </FixtureType>
            </GDTF>
            """;
        return TestData.Zip(("description.xml", TestData.Utf8(xml)));
    }

    [Fact]
    public void Gdtf_without_drawable_model_keeps_its_dmx_layout_instead_of_a_substitute()
    {
        // The library has Ayrton fixtures (Zonda 3 FX, …) but not these two models. A same-brand substitute would
        // replace a real 3-channel layout by a 26-channel one, and turn an unpatched passive accessory into a light.
        const string Wash = "11111111-0000-4000-8000-0000000000D1";
        const string Bracket = "11111111-0000-4000-8000-0000000000D2";
        const string Placeholder = "11111111-0000-4000-8000-0000000000D3";
        string xml = Scene($$"""
            <Fixture uuid="{{Wash}}" name="Wash">
              <GDTFSpec>Ayrton@Zonda 9 Wash.gdtf</GDTFSpec><GDTFMode>Mode A</GDTFMode>
              <Addresses><Address break="0">1</Address></Addresses><FixtureID>1</FixtureID>
            </Fixture>
            <SceneObject uuid="{{Bracket}}" name="Bracket">
              <Geometries/><GDTFSpec>Ayrton@Zonda 9 Bracket.gdtf</GDTFSpec><GDTFMode>Mode A</GDTFMode>
            </SceneObject>
            <Fixture uuid="{{Placeholder}}" name="Placeholder">
              <GDTFSpec>Ayrton@Zonda 9 Spot.gdtf</GDTFSpec><GDTFMode>Mode A</GDTFMode>
              <Addresses><Address break="0">101</Address></Addresses><FixtureID>2</FixtureID>
            </Fixture>
            """);
        var archive = Archive(xml,
            ("Ayrton@Zonda 9 Wash.gdtf", ModellessGdtf("Ayrton", "Zonda 9 Wash", 3)),
            ("Ayrton@Zonda 9 Bracket.gdtf", ModellessGdtf("Ayrton", "Zonda 9 Bracket", 0)),
            ("Ayrton@Zonda 9 Spot.gdtf", ModellessGdtf("Ayrton", "Zonda 9 Spot", 1)));
        var result = MvrImporter.Import(MvrReader.Read(archive, "modelless.mvr"), new MvrImportOptions { Library = TestData.Library });
        Dump(result.Issues);
        foreach (var g in result.Gdtfs) _out.WriteLine(g.Describe());

        // Real DMX layout: kept, with its mode, and reported.
        var wash = result.Gdtfs.Single(g => g.SpecName.Contains("Wash", StringComparison.Ordinal));
        Assert.Equal(MvrGdtfSource.EmbeddedStub, wash.Source);
        Assert.Equal("Ayrton@Zonda 9 Wash.gdtf", wash.FileName);
        Assert.Equal(3, wash.FixtureType!.DmxModes.Single().Channels.Count);
        var washFixture = result.FindEntity(Guid.Parse(Wash))!.Get<FixtureComponent>()!;
        Assert.Equal(("Ayrton@Zonda 9 Wash.gdtf", "Mode A"), (washFixture.GdtfSpec, washFixture.GdtfMode));
        Assert.Contains(result.Issues, i => i.Code == MvrIssueCodes.GdtfNoGeometry && i.Entities.Contains(Guid.Parse(Wash)));

        // Passive and unpatched: kept.
        var bracket = result.Gdtfs.Single(g => g.SpecName.Contains("Bracket", StringComparison.Ordinal));
        Assert.Equal(MvrGdtfSource.EmbeddedStub, bracket.Source);
        Assert.Contains(result.Issues, i => i.Code == MvrIssueCodes.GdtfNoGeometry && i.Entities.Contains(Guid.Parse(Bracket)));

        // A placeholder stub (one channel, patched, as Depence writes them) still gets the same-brand substitute.
        var placeholder = result.Gdtfs.Single(g => g.SpecName.Contains("Spot", StringComparison.Ordinal));
        Assert.Equal((MvrGdtfSource.Library, FixtureMatchKind.Substitute), (placeholder.Source, placeholder.MatchKind));
        Assert.Single(result.Issues, i => i.Code == MvrIssueCodes.GdtfSubstitute);
    }

    [Fact]
    public void Deeply_nested_objects_are_cut_instead_of_overflowing_the_stack()
    {
        const int depth = 20_000; // object levels; each is a GroupObject and a ChildList element
        var sb = new StringBuilder();
        sb.Append($"""<?xml version="1.0"?><GeneralSceneDescription verMajor="1" verMinor="6"><Scene><Layers><Layer uuid="{LayerId}" name="L"><ChildList>""");
        for (int i = 0; i < depth; i++) sb.Append("<GroupObject name=\"g\"><ChildList>");
        sb.Append("<Unknown><Deeper/></Unknown>");
        for (int i = 0; i < depth; i++) sb.Append("</ChildList></GroupObject>");
        sb.Append("</ChildList></Layer></Layers></Scene></GeneralSceneDescription>");

        var watch = Stopwatch.StartNew();
        var doc = MvrReader.Read(Archive(sb.ToString()), "deep.mvr");
        var issue = Assert.Single(doc.Issues, i => i.Code == MvrIssueCodes.NestingTooDeep);
        Assert.Equal(IssueSeverity.Warning, issue.Severity);
        int kept = doc.AllObjects().Count();
        Assert.InRange(kept, 200, MvrReader.MaxXmlDepth / 2);

        var result = MvrImporter.Import(doc);
        Assert.Equal(kept, result.Scene.Count);
        var export = MvrWriter.Write(result.Scene, new MemoryStream());
        Assert.Equal(kept, export.ObjectCount);
        Assert.Equal(kept, MvrReader.Read(MvrWriter.WriteDocument(doc)).AllObjects().Count());
        _out.WriteLine($"{kept} levels kept in {watch.ElapsedMilliseconds} ms");
        // Loading the unpruned tree alone takes XDocument about half a minute at this depth.
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");
    }

    [Fact]
    public void Exponential_symbol_nesting_is_bounded()
    {
        // Symdef k holds ten symbols of symdef k + 1; the last one holds a file: 10⁹ geometry instances if expanded.
        const int levels = 10;
        var ids = Enumerable.Range(0, levels).Select(i => $"22222222-0000-4000-8000-{i + 1:D12}").ToArray();
        var aux = new StringBuilder();
        for (int k = 0; k < levels; k++)
        {
            aux.Append($"""<Symdef uuid="{ids[k]}" name="S{k}"><ChildList>""");
            if (k == levels - 1) aux.Append("""<Geometry3D fileName="box.glb"/>""");
            else
                for (int j = 0; j < 10; j++)
                    aux.Append($$"""<Symbol uuid="{{Guid.NewGuid()}}" symdef="{{ids[k + 1]}}"><Matrix>{1,0,0}{0,1,0}{0,0,1}{{{j * 100}},0,0}</Matrix></Symbol>""");
            aux.Append("</ChildList></Symdef>");
        }
        string xml = Scene($$"""
            <SceneObject uuid="11111111-0000-4000-8000-0000000000E1" name="Bomb"><Geometries><Symbol uuid="{{Guid.NewGuid()}}" symdef="{{ids[0]}}"/></Geometries></SceneObject>
            <SceneObject uuid="11111111-0000-4000-8000-0000000000E2" name="After"><Geometries><Geometry3D fileName="box.glb"/></Geometries></SceneObject>
            """, aux: aux.ToString());
        var watch = Stopwatch.StartNew();
        var result = MvrImporter.Import(MvrReader.Read(Archive(xml, ("box.glb", SyntheticMvr.BoxGlb())), "bomb.mvr"));
        _out.WriteLine($"{result.Scene.Count} entities in {watch.ElapsedMilliseconds} ms");
        var issue = Assert.Single(result.Issues, i => i.Code == MvrIssueCodes.GeometryLimit);
        Assert.Contains(Guid.Parse("11111111-0000-4000-8000-0000000000E1"), issue.Entities);
        Assert.InRange(result.Scene.Count, 2, 300_000);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(60), $"took {watch.Elapsed}");
    }

    [Fact]
    public void Scene_export_writes_the_elements_mvr_1_6_makes_mandatory()
    {
        var gdtfMatch = TestData.Library.FindBySpec(SyntheticMvr.LibrarySpec)!;
        string firstMode = TestData.Library.GetFixtureType(gdtfMatch.Entry).DmxModes[0].Name;

        var scene = new Scene("Mandatory");
        var truss = scene.Add(new Entity("Truss"));
        truss.Add(new TrussComponent());
        var hoist = scene.Add(new Entity("Hoist"));
        hoist.Add(new RiggingPointComponent());
        var set = scene.Add(new Entity("Set"));
        set.Add(new GroupComponent());
        var spot = scene.Add(new Entity("Spot"), truss);
        spot.Add(new FixtureComponent { GdtfSpec = gdtfMatch.Entry.FileName, Addresses = { new DmxAddress(1, 1) }, FixtureId = 5 });
        var odd = scene.Add(new Entity("Odd name"));
        odd.Add(new FixtureComponent { GdtfSpec = "Acme@Spot: 2000?.gdtf", GdtfMode = "Basic" });

        var options = new MvrExportOptions
        {
            GdtfProvider = name => name == gdtfMatch.Entry.FileName ? File.ReadAllBytes(gdtfMatch.Entry.FilePath) : File.ReadAllBytes(gdtfMatch.Entry.FilePath),
            ProviderVersion = "test",
        };
        using var ms = new MemoryStream();
        var export = MvrWriter.Write(scene, ms, options);
        Dump(export.Issues);

        XElement root;
        using (var zip = new ZipArchive(new MemoryStream(ms.ToArray()), ZipArchiveMode.Read))
        {
            using var s = zip.GetEntry(MvrReader.SceneDescriptionEntry)!.Open();
            root = XDocument.Load(s).Root!;
            // File names MVR must be able to store on FAT32/NTFS.
            Assert.Contains(zip.Entries, e => e.FullName == "Acme@Spot_ 2000_.gdtf");
            Assert.All(zip.Entries, e => Assert.True(e.FullName.IndexOfAny(new[] { ':', '?', '*', '<', '>', '|', '"', '\\', '/' }) < 0, e.FullName));
        }
        Assert.Equal(("1", "6", "Candela", "test"),
            (root.Attribute("verMajor")?.Value, root.Attribute("verMinor")?.Value, root.Attribute("provider")?.Value, root.Attribute("providerVersion")?.Value));
        XElement Obj(Entity e) => root.Descendants().Single(x => x.Attribute("uuid")?.Value == e.Id.ToString("D").ToUpperInvariant());
        foreach (var device in new[] { truss, hoist, spot, odd })
        {
            var x = Obj(device);
            Assert.NotNull(x.Element("FixtureID"));
            Assert.NotNull(x.Element("FixtureIDNumeric"));
            if (x.Name.LocalName != "Fixture") Assert.NotNull(x.Element("Geometries"));
        }
        Assert.Equal(("Truss", "Support", "GroupObject", "Fixture"), (Obj(truss).Name.LocalName, Obj(hoist).Name.LocalName, Obj(set).Name.LocalName, Obj(spot).Name.LocalName));
        Assert.Equal("0", Obj(hoist).Element("ChainLength")?.Value);
        Assert.Equal("0", Obj(spot).Element("UnitNumber")?.Value);
        Assert.Equal(("5", "5"), (Obj(spot).Element("FixtureID")?.Value, Obj(spot).Element("FixtureIDNumeric")?.Value));
        // GDTFMode is mandatory with GDTFSpec: Core's "first mode" is written by name.
        Assert.Equal(firstMode, Obj(spot).Element("GDTFMode")?.Value);
        Assert.Equal("Acme@Spot_ 2000_.gdtf", Obj(odd).Element("GDTFSpec")?.Value);

        // Re-import: the neutral values do not come back as data.
        var again = MvrImporter.Import(MvrReader.Read(ms.ToArray(), "again.mvr"));
        var fc = again.Scene.Find(spot.Id)!.Get<FixtureComponent>()!;
        Assert.Null(fc.UnitNumber);
        Assert.Equal((5, firstMode), (fc.FixtureId, fc.GdtfMode));
    }

    [Fact]
    public void Document_round_trip_does_not_invent_attributes_or_drop_mandatory_containers()
    {
        string xml = Scene(
            """
            <Projector uuid="11111111-0000-4000-8000-0000000000F1" name="Beamer"><Geometries/></Projector>
            """,
            userData: """<UserData><Data provider="Other"><Thing/></Data></UserData>""");
        var doc = MvrReader.Read(Archive(xml));
        var written = MvrReader.Read(MvrWriter.WriteDocument(doc));
        var data = Assert.Single(written.UserData).Element!;
        Assert.Null(data.Attribute("ver"));
        Assert.Equal("Other", data.Attribute("provider")?.Value);

        using var ms = new MemoryStream();
        MvrWriter.WriteSceneDescription(doc, ms);
        var projector = XDocument.Parse(Encoding.UTF8.GetString(ms.ToArray())).Descendants("Projector").Single();
        Assert.NotNull(projector.Element("Projections")); // mandatory on a Projector, even when empty
    }

    [Fact]
    public void Gltf_with_an_invalid_uri_is_reported_and_its_object_kept()
    {
        // A lone surrogate escape in a URI cannot be turned into a string; the object must still be imported.
        byte[] gltf = TestData.Utf8("""{"asset":{"version":"2.0"},"buffers":[{"uri":"\uD800.bin","byteLength":4}]}""");
        string xml = Scene("""
            <SceneObject uuid="11111111-0000-4000-8000-0000000000F2" name="Broken glTF">
              <Geometries><Geometry3D fileName="model.gltf"/></Geometries>
              <ChildList><SceneObject uuid="11111111-0000-4000-8000-0000000000F3" name="Child"><Geometries/></SceneObject></ChildList>
            </SceneObject>
            """);
        var result = MvrImporter.Import(MvrReader.Read(Archive(xml, ("model.gltf", gltf))));
        Dump(result.Issues);
        Assert.DoesNotContain(result.Issues, i => i.Code == MvrIssueCodes.ObjectFailed);
        Assert.Contains(result.Issues, i => i.Code == MvrIssueCodes.GeometryUnreadable);
        var parent = result.FindEntity(Guid.Parse("11111111-0000-4000-8000-0000000000F2"));
        Assert.NotNull(parent);
        Assert.Same(parent, result.FindEntity(Guid.Parse("11111111-0000-4000-8000-0000000000F3"))!.Parent);
    }
}
