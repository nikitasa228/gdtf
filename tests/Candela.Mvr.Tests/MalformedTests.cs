using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using Candela.Core.Diagnostics;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;
using Xunit.Abstractions;
using static Candela.Mvr.Tests.FokinaTests;

namespace Candela.Mvr.Tests;

/// <summary>
/// Broken content never throws: bad matrices, missing or repeated uuids, unknown tags, unparsable values, dangling
/// references and broken embedded files become issues and the rest of the file is still read and imported. Only data
/// that is not an MVR archive at all throws <see cref="MvrFormatException"/>.
/// </summary>
public sealed class MalformedTests
{
    private const string GoodFixture = "99999999-0000-4000-8000-000000000001";
    private const string BadMatrixObject = "99999999-0000-4000-8000-000000000002";
    private const string Degenerate = "99999999-0000-4000-8000-000000000003";
    private const string Sheared = "99999999-0000-4000-8000-000000000004";
    private const string BrokenModel = "99999999-0000-4000-8000-000000000005";
    private const string MissingModel = "99999999-0000-4000-8000-000000000006";
    private const string BrokenGdtfFixture = "99999999-0000-4000-8000-000000000007";
    private const string Duplicate = "99999999-0000-4000-8000-000000000008";
    private const string Cyclic = "99999999-0000-4000-8000-000000000009";
    private const string BadValues = "99999999-0000-4000-8000-00000000000A";
    private const string SymdefLoop = "88888888-0000-4000-8000-000000000001";
    private const string SymdefChair = "88888888-0000-4000-8000-000000000002";
    /// <summary>Embedded as garbage bytes: the library file must be used instead.</summary>
    private const string BrokenGdtfSpec = SyntheticMvr.LibrarySpec;

    private readonly ITestOutputHelper _out;

    public MalformedTests(ITestOutputHelper output) => _out = output;

    /// <summary>A scene description with one or more problems in nearly every element.</summary>
    private static string Xml(string libraryMode) => $$"""
        <?xml version="1.0" encoding="UTF-8"?>
        <GeneralSceneDescription verMajor="1" verMinor="6" provider="Fuzz">
          <Bogus/>
          <Scene>
            <AUXData>
              <Class name="No uuid class"/>
              <Symdef uuid="{{SymdefLoop}}" name="Loop">
                <ChildList>
                  <Symbol uuid="77777777-0000-4000-8000-0000000000F1" symdef="{{SymdefLoop}}"/>
                  <Symbol symdef="not-a-guid"/>
                  <Geometry3D/>
                </ChildList>
              </Symdef>
              <Symdef uuid="{{SymdefChair}}" name="Chair">
                <ChildList><Geometry3D fileName="box.glb"><Matrix>{1,0,0}{0,1,0}{0,0,1}</Matrix></Geometry3D></ChildList>
              </Symdef>
              <Wibble name="unknown aux"/>
            </AUXData>
            <Layers>
              <Layer name="No uuid layer">
                <ChildList>
                  <Fixture uuid="{{GoodFixture}}" name="Good fixture">
                    <Matrix>{1,0,0}{0,1,0}{0,0,1}{1000,2000,3000}</Matrix>
                    <GDTFSpec>{{SyntheticMvr.LibrarySpec}}</GDTFSpec>
                    <GDTFMode>{{libraryMode}}</GDTFMode>
                    <Focus>12345678-0000-4000-8000-000000000000</Focus>
                    <Addresses>
                      <Address break="0">1</Address>
                      <Address break="0">abc</Address>
                      <Address break="-1">20</Address>
                      <Address>0</Address>
                      <Bogus/>
                    </Addresses>
                    <Connections>
                      <Connection own="In" other="Out" toObject="12345678-0000-4000-8000-0000000000FF"/>
                      <Connection own="In" other="Out" toObject="garbage"/>
                    </Connections>
                    <FixtureID>1</FixtureID>
                    <Geometries><Geometry3D fileName="box.glb"/></Geometries>
                    <Sparkle level="11"/>
                  </Fixture>
                  <SceneObject uuid="{{BadMatrixObject}}" name="Bad matrix">
                    <Matrix>{1,0,0}{0,1}{0,0,1}{0,0,0}</Matrix>
                  </SceneObject>
                  <SceneObject uuid="{{Degenerate}}" name="Degenerate">
                    <Matrix>{0,0,0}{0,0,0}{0,0,0}{500,0,0}</Matrix>
                  </SceneObject>
                  <SceneObject uuid="{{Sheared}}" name="Sheared">
                    <Matrix>{1,0,0}{0.5,1,0}{0,0,1}{0,500,0}</Matrix>
                  </SceneObject>
                  <SceneObject uuid="{{BrokenModel}}" name="Broken model">
                    <Matrix>{1,0,0}{0,1,0}{0,0,1}{NaN,0,0}</Matrix>
                    <Geometries><Geometry3D fileName="broken.glb"/></Geometries>
                  </SceneObject>
                  <SceneObject uuid="{{MissingModel}}" name="Missing model">
                    <Geometries>
                      <Geometry3D fileName="nowhere.3ds"/>
                      <Geometry3D fileName="notes.txt"/>
                      <Symbol symdef="12345678-0000-4000-8000-0000000000EE"/>
                      <Symbol symdef="{{SymdefChair}}"/>
                    </Geometries>
                  </SceneObject>
                  <Fixture name="No uuid fixture">
                    <GDTFSpec>{{SyntheticMvr.LibrarySpec}}</GDTFSpec>
                    <GDTFMode>Mode that does not exist</GDTFMode>
                    <Addresses><Address>2.1</Address></Addresses>
                  </Fixture>
                  <Fixture uuid="{{BrokenGdtfFixture}}" name="Broken GDTF">
                    <GDTFSpec>{{BrokenGdtfSpec}}</GDTFSpec>
                    <Addresses><Address>600</Address></Addresses>
                  </Fixture>
                  <Fixture uuid="{{BadValues}}" name="Bad values">
                    <GDTFSpec>Nobody@Nothing Here.gdtf</GDTFSpec>
                    <CastShadow>maybe</CastShadow>
                    <FixtureIDNumeric>twelve</FixtureIDNumeric>
                    <UnitNumber>1.5</UnitNumber>
                    <Color>0.3,1.7,100</Color>
                    <Classing>not-a-guid</Classing>
                    <Gobo rotation="fast">gobo.png</Gobo>
                  </Fixture>
                  <GroupObject uuid="{{Duplicate}}" name="First">
                    <ChildList>
                      <SceneObject uuid="{{Duplicate}}" name="Second (same uuid)"/>
                      <Spaceship uuid="12345678-0000-4000-8000-0000000000AB" name="unknown object type"/>
                    </ChildList>
                  </GroupObject>
                  <SceneObject uuid="{{Cyclic}}" name="Cyclic symbol">
                    <Geometries><Symbol symdef="{{SymdefLoop}}"/></Geometries>
                  </SceneObject>
                </ChildList>
              </Layer>
              <Layer uuid="also-not-a-guid" name="Second">
                <Matrix>garbage</Matrix>
              </Layer>
              <NotALayer/>
            </Layers>
          </Scene>
        </GeneralSceneDescription>
        """;

    private static byte[] Archive()
    {
        var bytes = TestData.Zip(
            ("GeneralSceneDescription.xml", TestData.Utf8(Xml(SyntheticMvr.LibraryMode()))),
            ("box.glb", SyntheticMvr.BoxGlb()),
            ("broken.glb", Encoding.ASCII.GetBytes("glTF this is not a binary glTF at all")),
            ("notes.txt", Encoding.ASCII.GetBytes("not a model")),
            (BrokenGdtfSpec, Encoding.ASCII.GetBytes("PK but not really a zip")),
            ("damaged.bin", new byte[256]));
        return WithUnsupportedCompression(bytes, "damaged.bin");
    }

    [Fact]
    public void Reader_reports_every_problem_and_keeps_reading()
    {
        var doc = MvrReader.Read(Archive(), "malformed.mvr");
        foreach (var i in doc.Issues) _out.WriteLine($"{i.Severity} {i.Code}: {i.Message}");

        Assert.Equal(2, doc.Layers.Count);
        Assert.NotEqual(Guid.Empty, doc.Layers[0].Uuid);
        Assert.NotEqual(Guid.Empty, doc.Layers[1].Uuid);
        Assert.False(doc.Layers[1].HasMatrix);
        Assert.Equal(12, doc.AllObjects().Count());

        // Missing and invalid uuids are replaced by generated ones (reported); repeated ones too.
        var noUuid = doc.AllObjects().Single(o => o.Name == "No uuid fixture");
        Assert.NotEqual(Guid.Empty, noUuid.Uuid);
        Assert.Equal(doc.AllObjects().Count(), doc.AllObjects().Select(o => o.Uuid).Distinct().Count());
        var second = doc.AllObjects().Single(o => o.Name == "Second (same uuid)");
        Assert.NotEqual(Guid.Parse(Duplicate), second.Uuid);
        Assert.NotEqual(Guid.Empty, Assert.Single(doc.AuxData.Classes).Uuid);
        Assert.Contains(doc.Issues, i => i.Code == MvrIssueCodes.UuidMissing && i.Message.Contains("No uuid fixture", StringComparison.Ordinal));
        Assert.Contains(doc.Issues, i => i.Code == MvrIssueCodes.UuidMissing && i.Message.Contains("No uuid layer", StringComparison.Ordinal));
        Assert.Contains(doc.Issues, i => i.Code == MvrIssueCodes.UuidMissing && i.Message.Contains("Class", StringComparison.Ordinal));
        Assert.Contains(doc.Issues, i => i.Code == MvrIssueCodes.UuidDuplicate && i.Entities.Contains(second.Uuid));

        // Matrices: unparsable (wrong group size, NaN, garbage) → identity and reported.
        var badMatrix = doc.AllObjects().Single(o => o.Uuid == Guid.Parse(BadMatrixObject));
        Assert.False(badMatrix.HasMatrix);
        Assert.Equal(Matrix4x4.Identity, badMatrix.Matrix);
        Assert.Contains(doc.Issues, i => i.Code == MvrIssueCodes.MatrixInvalid && i.Entities.Contains(badMatrix.Uuid));
        Assert.Contains(doc.Issues, i => i.Code == MvrIssueCodes.MatrixInvalid && i.Entities.Contains(Guid.Parse(BrokenModel)));
        Assert.Contains(doc.Issues, i => i.Code == MvrIssueCodes.MatrixInvalid && i.Message.Contains("Layer 'Second'", StringComparison.Ordinal));
        Assert.Contains(doc.Issues, i => i.Code == MvrIssueCodes.MatrixInvalid && i.Message.Contains("Symdef 'Chair'", StringComparison.Ordinal));

        // Unknown elements are reported and kept verbatim where they occur.
        var good = Assert.IsType<MvrFixture>(doc.AllObjects().Single(o => o.Uuid == Guid.Parse(GoodFixture)));
        Assert.Equal("11", Assert.Single(good.UnknownElements).Attribute("level")!.Value);
        var group = Assert.IsType<MvrGroupObject>(doc.AllObjects().Single(o => o.Uuid == Guid.Parse(Duplicate)));
        Assert.Equal("Spaceship", Assert.Single(group.UnknownElements).Name.LocalName);
        Assert.Equal("Wibble", Assert.Single(doc.AuxData.UnknownElements).Name.LocalName);
        foreach (var tag in new[] { "<Bogus>", "<Sparkle>", "<Spaceship>", "<Wibble>", "<NotALayer>" })
            Assert.Contains(doc.Issues, i => i.Code == MvrIssueCodes.UnknownElement && i.Message.Contains(tag, StringComparison.Ordinal));

        // Addresses: only the valid one survives; the others are reported.
        Assert.Equal(1, Assert.Single(good.Addresses).Absolute);
        Assert.Equal(3, doc.Issues.Count(i => i.Code == MvrIssueCodes.AddressInvalid && i.Entities.Contains(good.Uuid)));
        Assert.Equal(new DmxAddress(2, 1, 1), doc.AllObjects().OfType<MvrFixture>().Single(o => o.Name == "No uuid fixture").Addresses.Single().ToDmxAddress());

        // Unparsable values fall back to the default.
        var bad = Assert.IsType<MvrFixture>(doc.AllObjects().Single(o => o.Uuid == Guid.Parse(BadValues)));
        Assert.Equal((null, null, null, null, null), (bad.CastShadow, bad.FixtureIdNumeric, bad.UnitNumber, bad.Color, bad.Classing));
        Assert.Equal(("gobo.png", 0f), (bad.Gobo!.FileName, bad.Gobo.Rotation));
        foreach (var element in new[] { "<CastShadow>", "<FixtureIDNumeric>", "<UnitNumber>", "<Color>", "<Classing>", "<Gobo rotation>", "<Connection toObject>" })
            Assert.Contains(doc.Issues, i => i.Code == MvrIssueCodes.ValueInvalid && i.Message.Contains(element, StringComparison.Ordinal));
        Assert.Single(good.Connections); // the garbage toObject is dropped, the dangling one is kept for the importer

        // Geometry: a Geometry3D without file and a Symbol without symdef are dropped.
        var loop = doc.AuxData.Symdefs[0];
        Assert.IsType<MvrSymbol>(Assert.Single(loop.Geometries));
        Assert.Contains(doc.Issues, i => i.Code == MvrIssueCodes.SymdefMissing && i.Message.Contains("not-a-guid", StringComparison.Ordinal));
        Assert.Contains(doc.Issues, i => i.Code == MvrIssueCodes.ValueInvalid && i.Message.Contains("Geometry3D without fileName", StringComparison.Ordinal));

        // The entry with an unsupported compression method is listed without bytes.
        var damaged = doc.Files.Single(f => f.Name == "damaged.bin");
        Assert.Null(damaged.Bytes);
        Assert.NotNull(damaged.Error);
        Assert.Contains(doc.Issues, i => i.Code == MvrIssueCodes.FileUnreadable && i.Message.Contains("damaged.bin", StringComparison.Ordinal));

        // Generated ids are reproducible: the same bytes give the same document.
        var again = MvrReader.Read(Archive(), "malformed.mvr");
        Assert.Equal(doc.AllObjects().Select(o => o.Uuid), again.AllObjects().Select(o => o.Uuid));
        Assert.Equal(doc.Layers.Select(l => l.Uuid), again.Layers.Select(l => l.Uuid));
        Assert.Equal(doc.Issues.Select(i => (i.Code, i.Message)), again.Issues.Select(i => (i.Code, i.Message)));
    }

    [Fact]
    public void Import_turns_content_problems_into_issues_and_imports_the_rest()
    {
        var result = MvrImporter.Import(MvrReader.Read(Archive(), "malformed.mvr"), new MvrImportOptions { Library = TestData.Library });
        foreach (var i in result.Issues) _out.WriteLine($"{i.Severity} {i.Code}: {i.Message}");
        var scene = result.Scene;

        // Every object becomes an entity (the unknown <Spaceship> is not an object).
        Assert.Equal(12, scene.Count);
        Assert.Equal(new[] { "No uuid layer", "Second" }, scene.Layers.Select(l => l.Name));
        Assert.DoesNotContain(result.Issues, i => i.Code == "mvr.object-failed");

        // The good fixture: library GDTF, the valid address, its unknown focus dropped with a warning.
        var good = scene.Find(Guid.Parse(GoodFixture))!;
        var fc = good.Get<FixtureComponent>()!;
        Assert.Equal(new[] { new DmxAddress(1, 1, 1) }, fc.Addresses);
        Assert.Null(fc.FocusPoint);
        AssertNear(new Vector3(1, 2, 3), good.WorldPosition, 1e-6f);
        Assert.Contains(result.Issues, i => i.Code == MvrIssueCodes.FocusMissing && i.Entities.Contains(good.Id));
        Assert.Contains(result.Issues, i => i.Code == MvrIssueCodes.ConnectionTargetMissing && i.Entities.Contains(good.Id));
        Assert.Contains(result.Issues, i => i.Code == MvrIssueCodes.NotRepresentable && i.Message.StartsWith("Geometries of 1 fixture", StringComparison.Ordinal));
        Assert.False(good.Has<MeshComponent>());
        Assert.Empty(scene.Query<CableComponent>());

        // Matrices: invalid → identity; degenerate → position only; sheared → orthonormal rotation; NaN → origin.
        Assert.Equal(Vector3.Zero, scene.Find(Guid.Parse(BadMatrixObject))!.WorldPosition);
        var degenerate = scene.Find(Guid.Parse(Degenerate))!;
        AssertNear(new Vector3(0.5f, 0, 0), degenerate.WorldPosition, 1e-6f);
        Assert.Equal(Quaternion.Identity, degenerate.Local.Rotation);
        Assert.Equal(Vector3.One, degenerate.Local.Scale);
        Assert.Contains(result.Issues, i => i.Code == MvrIssueCodes.MatrixDegenerate && i.Entities.Contains(degenerate.Id));
        var sheared = scene.Find(Guid.Parse(Sheared))!;
        AssertProperRotation(new Core.Math.Transform(sheared.Local.Position, sheared.Local.Rotation, Vector3.One));
        AssertNear(new Vector3(0, 0.5f, 0), sheared.WorldPosition, 1e-6f);
        Assert.Contains(result.Issues, i => i.Code == MvrIssueCodes.MatrixSheared && i.Entities.Contains(sheared.Id));

        // Broken, missing and unsupported model files: the objects exist without meshes; the one usable geometry (the
        // Chair symbol at the origin) becomes the object's own mesh.
        var broken = scene.Find(Guid.Parse(BrokenModel))!;
        Assert.False(broken.Has<MeshComponent>());
        Assert.Empty(broken.Children);
        Assert.Contains(result.Issues, i => i.Code == MvrIssueCodes.GeometryUnreadable && i.Message.Contains("broken.glb", StringComparison.Ordinal));
        var missing = scene.Find(Guid.Parse(MissingModel))!;
        Assert.Empty(missing.Children);
        Assert.Equal(result.Assets.Single().Id, missing.Get<MeshComponent>()!.ModelAssetId);
        Assert.Contains(result.Issues, i => i.Code == MvrIssueCodes.FileMissing && i.Message.Contains("nowhere.3ds", StringComparison.Ordinal));
        Assert.Contains(result.Issues, i => i.Code == MvrIssueCodes.GeometryUnsupported && i.Message.Contains("notes.txt", StringComparison.Ordinal));
        Assert.Contains(result.Issues, i => i.Code == MvrIssueCodes.SymdefMissing && i.Entities.Contains(missing.Id));
        Assert.Equal("box.glb", Assert.Single(result.Assets).FileName);

        // A self-instancing symdef is cut, not followed forever.
        Assert.Contains(result.Issues, i => i.Code == MvrIssueCodes.SymdefCycle && i.Entities.Contains(Guid.Parse(Cyclic)));

        // The unreadable embedded GDTF is reported and the fixture is resolved from the library instead.
        var brokenGdtf = result.Gdtfs.Single(g => g.SpecName == BrokenGdtfSpec);
        Assert.Equal(MvrGdtfSource.Library, brokenGdtf.Source);
        Assert.Null(brokenGdtf.EmbeddedBytes);
        Assert.Contains(result.Issues, i => i.Code == MvrIssueCodes.GdtfUnreadable && i.Entities.Contains(Guid.Parse(BrokenGdtfFixture)));
        Assert.StartsWith("Robe_Lighting@Robin_MegaPointe@", scene.Find(Guid.Parse(BrokenGdtfFixture))!.Get<FixtureComponent>()!.GdtfSpec, StringComparison.Ordinal);

        // An unknown mode is matched to an existing one and reported.
        var noUuid = result.Document.AllObjects().Single(o => o.Name == "No uuid fixture");
        var noUuidFixture = result.FindEntity(noUuid.Uuid)!.Get<FixtureComponent>()!;
        Assert.NotNull(brokenGdtf.FixtureType!.FindMode(noUuidFixture.GdtfMode!));
        Assert.Contains(result.Issues, i => i.Code == MvrIssueCodes.ModeUnknown && i.Message.Contains("Mode that does not exist", StringComparison.Ordinal));

        // A GDTF nobody has stays unresolved (an error issue) but its fixture is still placed and patched as written.
        var nothing = result.Gdtfs.Single(g => g.SpecName == "Nobody@Nothing Here.gdtf");
        Assert.Equal((MvrGdtfSource.Unresolved, false), (nothing.Source, nothing.IsResolved));
        Assert.Contains(result.Issues, i => i.Code == MvrIssueCodes.GdtfUnresolved && i.Severity == IssueSeverity.Error && i.Entities.Contains(Guid.Parse(BadValues)));
        var badValues = scene.Find(Guid.Parse(BadValues))!.Get<FixtureComponent>()!;
        Assert.Equal(("Nobody@Nothing Here.gdtf", true, null), (badValues.GdtfSpec, badValues.CastShadow, badValues.Color));

        // Repeated uuid: both objects are there, the second under a new id.
        var first = scene.Find(Guid.Parse(Duplicate))!;
        Assert.Equal("Second (same uuid)", Assert.Single(first.Children).Name);
    }

    [Fact]
    public void Import_without_a_library_reports_every_gdtf_as_unresolved()
    {
        var result = MvrImporter.Import(MvrReader.Read(Archive(), "malformed.mvr"));
        Assert.Equal(12, result.Scene.Count);
        Assert.Equal(2, result.Gdtfs.Count);
        Assert.All(result.Gdtfs, g => Assert.Equal(MvrGdtfSource.Unresolved, g.Source));
        Assert.Equal(2, result.Issues.Count(i => i.Code == MvrIssueCodes.GdtfUnresolved && i.Severity == IssueSeverity.Error));
        var fc = result.Scene.Find(Guid.Parse(GoodFixture))!.Get<FixtureComponent>()!;
        // The fixture keeps what the file says, so a library added later can still resolve it.
        Assert.Equal(SyntheticMvr.LibraryMode(), fc.GdtfMode);
        Assert.Equal(SyntheticMvr.LibrarySpec, fc.GdtfSpec);
        Assert.Equal(new[] { new DmxAddress(1, 1, 1) }, fc.Addresses);
    }

    [Fact]
    public void Minimal_and_empty_descriptions_import_as_empty_scenes()
    {
        foreach (var xml in new[]
        {
            "<GeneralSceneDescription/>",
            """<GeneralSceneDescription verMajor="1" verMinor="6"><Scene/></GeneralSceneDescription>""",
            """<GeneralSceneDescription verMajor="2" verMinor="0"><Scene><Layers/></Scene></GeneralSceneDescription>""",
            """<x:GeneralSceneDescription xmlns:x="urn:any" verMajor="1" verMinor="5"><x:Scene><x:Layers><x:Layer uuid="44444444-0000-4000-8000-0000000000AA"/></x:Layers></x:Scene></x:GeneralSceneDescription>""",
        })
        {
            var result = MvrImporter.Import(MvrReader.Read(TestData.Zip(("GeneralSceneDescription.xml", TestData.Utf8(xml))), "minimal.mvr"));
            Assert.Equal(0, result.Scene.Count);
            Assert.NotEmpty(result.Scene.Layers);
            Assert.DoesNotContain(result.Issues, i => i.Severity == IssueSeverity.Error);
        }
        var versionless = MvrReader.Read(TestData.Zip(("GeneralSceneDescription.xml", TestData.Utf8("<GeneralSceneDescription/>"))));
        Assert.Contains(versionless.Issues, i => i.Code == MvrIssueCodes.VersionUnsupported);
    }

    [Fact]
    public void Description_in_a_subfolder_is_found_and_files_resolve_from_there()
    {
        var bytes = TestData.Zip(
            ("export/GeneralSceneDescription.xml", TestData.Utf8($$"""
                <GeneralSceneDescription verMajor="1" verMinor="6"><Scene><Layers><Layer uuid="{{SyntheticMvr.LayerRig}}" name="L"><ChildList>
                  <SceneObject uuid="{{SyntheticMvr.SetPiece}}" name="Box"><Geometries><Geometry3D fileName="box.glb"/></Geometries></SceneObject>
                </ChildList></Layer></Layers></Scene></GeneralSceneDescription>
                """)),
            ("export/box.glb", SyntheticMvr.BoxGlb()));
        var result = MvrImporter.Import(MvrReader.Read(bytes, "nested.mvr"));
        Assert.Contains(result.Issues, i => i.Code == MvrIssueCodes.ArchiveLayout);
        Assert.NotNull(result.Scene.Find(Guid.Parse(SyntheticMvr.SetPiece))!.Get<MeshComponent>());
    }

    [Fact]
    public void Data_that_is_not_an_mvr_archive_throws_a_format_exception()
    {
        Assert.Throws<MvrFormatException>(() => MvrReader.Read(Encoding.ASCII.GetBytes("definitely not a zip"), "text.mvr"));
        Assert.Throws<MvrFormatException>(() => MvrReader.Read(Array.Empty<byte>(), "empty.mvr"));
        Assert.Throws<MvrFormatException>(() => MvrReader.Read(TestData.Zip(("box.glb", SyntheticMvr.BoxGlb())), "nodescription.mvr"));
        Assert.Throws<MvrFormatException>(() => MvrReader.Read(TestData.Zip(("GeneralSceneDescription.xml", TestData.Utf8("<GeneralSceneDescription><Scene>"))), "truncated.mvr"));
        Assert.Throws<MvrFormatException>(() => MvrReader.Read(TestData.Zip(("GeneralSceneDescription.xml", TestData.Utf8("<GDTF/>"))), "wrongroot.mvr"));
        var unreadableDescription = WithUnsupportedCompression(
            TestData.Zip(("GeneralSceneDescription.xml", TestData.Utf8("<GeneralSceneDescription/>"))), "GeneralSceneDescription.xml");
        Assert.Throws<MvrFormatException>(() => MvrReader.Read(unreadableDescription, "unsupported.mvr"));
        // A DTD is ignored, never resolved.
        var dtd = TestData.Utf8("""<?xml version="1.0"?><!DOCTYPE x [<!ENTITY e SYSTEM "file:///etc/passwd">]><GeneralSceneDescription verMajor="1" verMinor="6"/>""");
        Assert.Empty(MvrReader.Read(TestData.Zip(("GeneralSceneDescription.xml", dtd))).Layers);
    }

    [Fact]
    public void Export_reports_unusable_scene_content_instead_of_throwing()
    {
        var scene = new Scene("Broken");
        var fixture = new Entity("Fixture");
        fixture.Add(new FixtureComponent
        {
            GdtfSpec = "Robe@Robin MegaPointe.gdtf",
            GdtfMode = "Standard",
            Addresses = { new DmxAddress(1, 1), new DmxAddress(0, 1), new DmxAddress(1, 600) },
            Color = new CieColor(float.NaN, 0.3f, 100f),
            FocusPoint = new Vector3(float.PositiveInfinity, 0, 0),
            UnitNumber = "A",
        });
        scene.Add(fixture);
        var other = new Entity("Other\u0001 fixture\uD800"); // characters XML cannot carry are dropped
        other.Add(new FixtureComponent { GdtfSpec = "Other@Thing.gdtf" });
        scene.Add(other);
        var noSpec = new Entity("No spec");
        noSpec.Add(new FixtureComponent());
        scene.Add(noSpec);
        var mesh = new Entity("Mesh") { Local = new Core.Math.Transform(new Vector3(float.NaN, 0, 0)) };
        mesh.Add(new MeshComponent { ModelAssetId = Guid.Parse("12345678-0000-4000-8000-000000000001") });
        scene.Add(mesh);

        var options = new MvrExportOptions
        {
            GdtfProvider = name => name.StartsWith("Robe", StringComparison.Ordinal) ? throw new IOException("disk on fire") : Encoding.ASCII.GetBytes("not a zip"),
            ModelProvider = _ => throw new InvalidOperationException("model store offline"),
        };
        using var ms = new MemoryStream();
        var export = MvrWriter.Write(scene, ms, options);
        foreach (var i in export.Issues) _out.WriteLine($"{i.Severity} {i.Code}: {i.Message}");

        Assert.Equal(4, export.ObjectCount);
        Assert.Empty(export.Document.Files);
        Assert.Contains(export.Issues, i => i.Code == MvrIssueCodes.ExportResourceMissing && i.Message.Contains("disk on fire", StringComparison.Ordinal));
        Assert.Contains(export.Issues, i => i.Code == MvrIssueCodes.ExportResourceMissing && i.Message.Contains("not a GDTF", StringComparison.Ordinal));
        Assert.Contains(export.Issues, i => i.Code == MvrIssueCodes.ExportResourceMissing && i.Message.Contains("model store offline", StringComparison.Ordinal));
        Assert.Contains(export.Issues, i => i.Code == MvrIssueCodes.ExportResourceMissing && i.Entities.Contains(noSpec.Id));
        Assert.Equal(2, export.Issues.Count(i => i.Code == MvrIssueCodes.AddressInvalid));
        Assert.Equal(2, export.Issues.Count(i => i.Code == MvrIssueCodes.ValueInvalid));
        Assert.Contains(export.Issues, i => i.Code == MvrIssueCodes.MatrixInvalid && i.Entities.Contains(mesh.Id));

        // What was written is a valid MVR that reads back without issues.
        var doc = MvrReader.Read(ms.ToArray(), "broken.mvr");
        Assert.Empty(doc.Issues);
        var f = doc.AllObjects().OfType<MvrFixture>().Single(o => o.Uuid == fixture.Id);
        Assert.Equal(1, Assert.Single(f.Addresses).Absolute);
        Assert.Null(f.Color);
        Assert.Null(f.Focus);
        Assert.Null(f.UnitNumber);
        Assert.Equal("Robe@Robin MegaPointe.gdtf", f.GdtfSpec);
        Assert.Equal(Matrix4x4.Identity, doc.AllObjects().Single(o => o.Uuid == mesh.Id).Matrix);
        Assert.Equal("Other fixture", doc.AllObjects().Single(o => o.Uuid == other.Id).Name);
    }

    /// <summary>
    /// Sets the compression method of one entry (local header and central directory) to 99 (AES, which .NET cannot
    /// read), so the entry is listed but cannot be opened.
    /// </summary>
    private static byte[] WithUnsupportedCompression(byte[] zip, string entryName)
    {
        var data = (byte[])zip.Clone();
        byte[] name = Encoding.UTF8.GetBytes(entryName);
        int patched = 0;
        for (int i = 0; i + 46 <= data.Length; i++)
        {
            uint sig = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i));
            int nameOffset, methodOffset, nameLength;
            if (sig == 0x04034b50)
            {
                methodOffset = i + 8;
                nameLength = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(i + 26));
                nameOffset = i + 30;
            }
            else if (sig == 0x02014b50)
            {
                methodOffset = i + 10;
                nameLength = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(i + 28));
                nameOffset = i + 46;
            }
            else
            {
                continue;
            }
            if (nameLength != name.Length || nameOffset + nameLength > data.Length || !data.AsSpan(nameOffset, nameLength).SequenceEqual(name)) continue;
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(methodOffset), 99);
            patched++;
        }
        Assert.Equal(2, patched);
        using (var check = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read))
            Assert.NotNull(check.GetEntry(entryName));
        return data;
    }
}
