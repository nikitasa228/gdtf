using System.Numerics;
using Candela.Core.Diagnostics;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;
using Candela.Gdtf;
using Xunit.Abstractions;
using static Candela.Mvr.Tests.FokinaTests;

namespace Candela.Mvr.Tests;

/// <summary>The hand-written archive of <see cref="SyntheticMvr"/>: reader, importer, document and scene round trips.</summary>
public sealed class SyntheticTests
{
    private static readonly Lazy<byte[]> Archive = new(() => SyntheticMvr.Build());
    private readonly ITestOutputHelper _out;

    public SyntheticTests(ITestOutputHelper output) => _out = output;

    private static Guid G(string id) => Guid.Parse(id);

    [Fact]
    public void Reader_parses_every_object_type_and_attribute()
    {
        var doc = MvrReader.Read(Archive.Value, "synthetic.mvr");
        Assert.Empty(doc.Issues);
        Assert.Equal(new Version(1, 6), doc.Version);
        Assert.Equal("TestSuite", doc.Provider);
        Assert.Equal("2.1", doc.ProviderVersion);
        var data = Assert.Single(doc.UserData);
        Assert.Equal("TestSuite", data.Provider);
        Assert.Equal("1.0", data.Version);
        Assert.Equal("grid", data.Element!.Elements().Single().Attribute("name")!.Value);

        // AUXData
        var aux = doc.AuxData;
        Assert.Equal(("Lighting", G(SyntheticMvr.ClassLighting)), (Assert.Single(aux.Classes).Name, aux.Classes[0].Uuid));
        Assert.Equal("1st Electric", Assert.Single(aux.Positions).Name);
        Assert.Equal(2, aux.Symdefs.Count);
        var chair = aux.Symdefs[0];
        Assert.Equal("Chair", chair.Name);
        Assert.Equal(2, chair.Geometries.Count);
        Assert.All(chair.Geometries, g => Assert.Equal("box.glb", Assert.IsType<MvrGeometry3D>(g).FileName));
        Assert.False(chair.Geometries[0].HasMatrix);
        AssertNear(new Vector3(0, 0, 1), chair.Geometries[1].Matrix.Translation, 1e-7f);
        var nested = Assert.IsType<MvrSymbol>(Assert.Single(aux.Symdefs[1].Geometries));
        Assert.Equal(G(SyntheticMvr.SymdefChair), nested.SymdefId);
        Assert.Equal(G(SyntheticMvr.NestedSymbol), nested.Uuid);
        var mapping = Assert.Single(aux.MappingDefinitions);
        Assert.Equal((1920, 1080, MvrScaleHandling.ScaleKeepRatio), (mapping.SizeX, mapping.SizeY, mapping.ScaleHandling));
        Assert.Equal(("Display", "File", "movie.mp4"), (mapping.Source!.LinkedGeometry, mapping.Source.Type, mapping.Source.Value));

        // Layers
        Assert.Equal(new[] { "Rig", "Set" }, doc.Layers.Select(l => l.Name));
        Assert.False(doc.Layers[0].HasMatrix);
        Assert.True(doc.Layers[1].HasMatrix);
        AssertNear(new Vector3(0, 0, 0.5f), doc.Layers[1].Matrix.Translation, 1e-7f);
        var kinds = doc.AllObjects().Select(o => o.Kind).Distinct().ToHashSet();
        Assert.Equal(Enum.GetValues<MvrObjectKind>().ToHashSet(), kinds);
        Assert.Equal(14, doc.AllObjects().Count());

        // FocusPoint
        var focus = Assert.IsType<MvrFocusPoint>(doc.Layers[0].Objects[0]);
        Assert.Equal(("Centre", G(SyntheticMvr.ClassLighting)), (focus.Name, focus.Classing!.Value));
        AssertNear(new Vector3(1, 2, 0), focus.Matrix.Translation, 1e-7f);
        Assert.Single(focus.Geometries);

        // Truss with a fixture with every fixture element
        var truss = Assert.IsType<MvrTruss>(doc.Layers[0].Objects[1]);
        Assert.Equal((G(SyntheticMvr.PositionElectric), "Front truss"), (truss.Position!.Value, truss.Function));
        Assert.Equal("truss.3ds", Assert.IsType<MvrGeometry3D>(Assert.Single(truss.Geometries)).FileName);
        var spot = Assert.IsType<MvrFixture>(Assert.Single(truss.Children));
        Assert.Equal(SyntheticMvr.LibrarySpec, spot.GdtfSpec);
        Assert.Equal(SyntheticMvr.LibraryMode(), spot.GdtfMode);
        Assert.Equal(G(SyntheticMvr.Focus), spot.Focus);
        Assert.Equal((true, true, false), (spot.CastShadow, spot.DmxInvertPan, spot.DmxInvertTilt));
        Assert.Equal((G(SyntheticMvr.PositionElectric), "Key light"), (spot.Position!.Value, spot.Function));
        Assert.Equal(("101", 101, 7), (spot.FixtureId, spot.FixtureIdNumeric, spot.UnitNumber));
        Assert.Equal("Base.Yoke", spot.ChildPosition);
        Assert.Equal(2, spot.Addresses.Count);
        Assert.Equal((1, 0, 1, 1, "1.001"), (spot.Addresses[0].Absolute, spot.Addresses[0].Break, spot.Addresses[0].Universe, spot.Addresses[0].Address, spot.Addresses[0].RawValue));
        Assert.Equal(new DmxAddress(3, 1, 2), spot.Addresses[1].ToDmxAddress());
        var network = Assert.Single(spot.Networks);
        Assert.Equal(("NetPort", "10.0.0.5", "255.0.0.0", false, "spot1", "none"), (network.Geometry, network.IPv4, network.SubnetMask, network.Dhcp!.Value, network.Hostname, network.IPv6 ?? "none"));
        var protocol = Assert.Single(spot.Protocols);
        Assert.Equal(("NetPort", "Main", "sACN", "2016", "Multicast"), (protocol.Geometry, protocol.Name, protocol.Type, protocol.Version, protocol.Transmission));
        var alignment = Assert.Single(spot.Alignments);
        Assert.Equal(("Beam", Vector3.UnitY, -Vector3.UnitZ), (alignment.Geometry, alignment.Up, alignment.Direction));
        Assert.Equal("Body_Pan,f 50", Assert.Single(spot.CustomCommands));
        Assert.Equal(("Universal/Gobo1", "Gobo1"), (Assert.Single(spot.Overwrites).Universal, spot.Overwrites[0].Target));
        var connection = Assert.Single(spot.Connections);
        Assert.Equal(("Input", "Output", G(SyntheticMvr.Wash)), (connection.Own, connection.Other, connection.ToObject));
        Assert.Equal(new CieColor(0.3127f, 0.329f, 100f), spot.Color);
        Assert.Equal((1, 42), (spot.CustomIdType, spot.CustomId));
        var map = Assert.Single(spot.Mappings);
        Assert.Equal((G(SyntheticMvr.MappingDef), 10, 20, 1, 2, 45f), (map.LinkedDefinition, map.Ux, map.Uy, map.Ox, map.Oy, map.Rz));
        Assert.Equal(("gobo.png", 32.5f), (spot.Gobo!.FileName, spot.Gobo.Rotation));
        Assert.IsType<MvrSceneObject>(Assert.Single(spot.Children));

        // Support, groups, screen, projector
        var hoist = Assert.IsType<MvrSupport>(doc.Layers[0].Objects[2]);
        Assert.Equal((2f, "Chain hoist"), (hoist.ChainLength!.Value, hoist.Function));
        var outer = Assert.IsType<MvrGroupObject>(doc.Layers[0].Objects[3]);
        var inner = Assert.IsType<MvrGroupObject>(Assert.Single(outer.Children));
        Assert.Equal(2, inner.Children.Count);
        var wash = Assert.IsType<MvrFixture>(inner.Children[0]);
        Assert.Equal((G(SyntheticMvr.Spot), "A-7", 7), (wash.Multipatch!.Value, wash.FixtureId, wash.FixtureIdNumeric));
        var chairs = Assert.IsType<MvrSceneObject>(inner.Children[1]);
        Assert.Equal(2, chairs.Geometries.OfType<MvrSymbol>().Count());
        var screen = Assert.IsType<MvrVideoScreen>(doc.Layers[0].Objects[4]);
        var source = Assert.Single(screen.Sources);
        Assert.Equal(("Display", "NDI", "Stream 1", "IMAG"), (source.LinkedGeometry, source.Type, source.Value, screen.Function));
        var projector = Assert.IsType<MvrProjector>(doc.Layers[0].Objects[5]);
        var projection = Assert.Single(projector.Projections);
        Assert.Equal(("Lens", "slide.png", MvrScaleHandling.ScaleIgnoreRatio), (projection.Source!.LinkedGeometry, projection.Source.Value, projection.ScaleHandling));
        var mirrored = doc.Layers[0].Objects[6];
        Assert.True(MvrMatrix.Determinant3(mirrored.Matrix) < 0);

        // Embedded files
        Assert.Equal(
            new[] { MvrFileKind.Glb, MvrFileKind.ThreeDs, MvrFileKind.Image, MvrFileKind.Gdtf, MvrFileKind.Gdtf },
            doc.Files.Select(f => f.Kind));
        Assert.NotNull(doc.FindFile("BOX.GLB"));
        Assert.Null(doc.FindFile("missing.glb"));
    }

    [Fact]
    public void Document_round_trip_is_lossless_and_idempotent()
    {
        var doc = MvrReader.Read(Archive.Value, "synthetic.mvr");
        byte[] first = MvrWriter.WriteDocument(doc);
        var again = MvrReader.Read(first, "again.mvr");
        Assert.Empty(again.Issues);
        byte[] second = MvrWriter.WriteDocument(again);
        Assert.Equal(first, second);

        Assert.Equal(doc.Provider, again.Provider);
        Assert.Equal(doc.UserData.Single().Element!.ToString(), again.UserData.Single().Element!.ToString());
        Assert.Equal(doc.Files.Select(f => (f.Name, f.Bytes!.Length)), again.Files.Select(f => (f.Name, f.Bytes!.Length)));
        var a = doc.AllObjects().ToList();
        var b = again.AllObjects().ToList();
        Assert.Equal(a.Select(o => (o.Kind, o.Uuid, o.Name, o.Classing)), b.Select(o => (o.Kind, o.Uuid, o.Name, o.Classing)));
        for (int i = 0; i < a.Count; i++) AssertMatrixNear(a[i].Matrix, b[i].Matrix, 0f);
        var spotA = a.OfType<MvrFixture>().First();
        var spotB = b.OfType<MvrFixture>().First();
        Assert.Equal(spotA.Addresses.Select(x => (x.Absolute, x.Break, x.RawValue)), spotB.Addresses.Select(x => (x.Absolute, x.Break, x.RawValue)));
        Assert.Equal(
            (spotA.GdtfSpec, spotA.GdtfMode, spotA.Focus, spotA.DmxInvertPan, spotA.FixtureId, spotA.UnitNumber, spotA.Color, spotA.CustomId, spotA.Gobo!.Rotation),
            (spotB.GdtfSpec, spotB.GdtfMode, spotB.Focus, spotB.DmxInvertPan, spotB.FixtureId, spotB.UnitNumber, spotB.Color, spotB.CustomId, spotB.Gobo!.Rotation));
        Assert.Equal(spotA.Networks.Single().Hostname, spotB.Networks.Single().Hostname);
        Assert.Equal(spotA.Connections.Single().ToObject, spotB.Connections.Single().ToObject);
        Assert.Equal(spotA.Mappings.Single().Rz, spotB.Mappings.Single().Rz);
        Assert.Equal(again.AuxData.Symdefs.Select(s => (s.Uuid, s.Geometries.Count)), doc.AuxData.Symdefs.Select(s => (s.Uuid, s.Geometries.Count)));
        Assert.Equal(2f, again.AllObjects().OfType<MvrSupport>().Single().ChainLength);
        Assert.Equal(doc.Layers[1].Matrix, again.Layers[1].Matrix);
    }

    [Fact]
    public void Import_builds_the_hierarchy_components_assets_and_cables()
    {
        var result = MvrImporter.Import(MvrReader.Read(Archive.Value, "synthetic.mvr"), new MvrImportOptions { Library = TestData.Library });
        foreach (var i in result.Issues) _out.WriteLine($"{i.Severity} {i.Code}: {i.Message}");
        var scene = result.Scene;
        Assert.Equal("synthetic", scene.Name);
        Assert.Equal(new[] { (G(SyntheticMvr.LayerRig), "Rig"), (G(SyntheticMvr.LayerSet), "Set") }, scene.Layers.Select(l => (l.Id, l.Name)));
        Assert.Equal(18, scene.Count);
        Assert.DoesNotContain(result.Issues, i => i.Severity == IssueSeverity.Error);

        // Root entities: the layer objects (focus points are not entities) and the cable.
        Assert.Equal(
            new[] { "Truss 1", "Hoist A", "Outer group", "LED wall", "Projector", "Mirrored chair", "Stub fixture", "Set piece", "Wash 1 → Spot 1" },
            result.RootEntities.Select(e => e.Name));

        // Assets: the GLB and the 3DS, each once; the focus point's geometry is reported, not imported.
        Assert.Equal(new[] { "truss.3ds", "box.glb" }, result.Assets.Select(a => a.FileName));
        Assert.All(result.Assets, a => Assert.Equal(ImportedAssetKind.Model, a.Kind));
        var box = result.Assets[1];
        var boxBounds = box.Decode().ComputeBounds();
        AssertNear(Vector3.Zero, boxBounds.Min, 1e-5f);
        AssertNear(SyntheticMvr.BoxSize, boxBounds.Max, 1e-5f);
        var trussBounds = result.Assets[0].Decode().ComputeBounds();
        AssertNear(SyntheticMvr.TrussSizeMm / 1000f, trussBounds.Max, 1e-5f);
        Assert.Contains(result.Issues, i => i.Code == MvrIssueCodes.NotRepresentable && i.Message.Contains("focus point", StringComparison.Ordinal));

        // Truss → TrussComponent with its model; length from the 3DS bounds (millimetres → metres).
        var truss = scene.Find(G(SyntheticMvr.Truss))!;
        var tc = truss.Get<TrussComponent>()!;
        Assert.Equal(result.Assets[0].Id, tc.ModelAssetId);
        Assert.Equal(3f, tc.Length, 4);
        AssertNear(new Vector3(0, 5, 6), truss.WorldPosition, 1e-6f);

        // Fixture on the truss: library GDTF, patch per break, focus point in world space, mount.
        var spot = scene.Find(G(SyntheticMvr.Spot))!;
        Assert.Same(truss, spot.Parent);
        AssertNear(new Vector3(1, 5, 5.5f), spot.WorldPosition, 1e-6f);
        var fc = spot.Get<FixtureComponent>()!;
        Assert.StartsWith("Robe_Lighting@Robin_MegaPointe@", fc.GdtfSpec, StringComparison.Ordinal);
        Assert.Equal(SyntheticMvr.LibraryMode(), fc.GdtfMode);
        Assert.Equal(new[] { new DmxAddress(1, 1, 1), new DmxAddress(3, 1, 2) }, fc.Addresses);
        Assert.Equal((101, "7", 42, true), (fc.FixtureId, fc.UnitNumber, fc.CustomId, fc.CastShadow));
        Assert.Equal(new CieColor(0.3127f, 0.329f, 100f), fc.Color);
        Assert.Equal(new Vector3(1, 2, 0), fc.FocusPoint);
        Assert.Equal(truss.Id, fc.MountedOn);
        var safety = Assert.Single(spot.Children);
        Assert.Equal(box.Id, safety.Get<MeshComponent>()!.ModelAssetId);

        // Support → rigging point with its mesh.
        var hoist = scene.Find(G(SyntheticMvr.Hoist))!;
        Assert.NotNull(hoist.Get<RiggingPointComponent>());
        Assert.Equal(box.Id, hoist.Get<MeshComponent>()!.ModelAssetId);

        // Nested groups and a fixture inside them.
        var outer = scene.Find(G(SyntheticMvr.OuterGroup))!;
        var inner = scene.Find(G(SyntheticMvr.InnerGroup))!;
        Assert.NotNull(outer.Get<GroupComponent>());
        Assert.NotNull(inner.Get<GroupComponent>());
        Assert.Same(outer, inner.Parent);
        var wash = scene.Find(G(SyntheticMvr.Wash))!;
        Assert.Same(inner, wash.Parent);
        AssertNear(new Vector3(2, 1, 1), wash.WorldPosition, 1e-6f);
        var wfc = wash.Get<FixtureComponent>()!;
        Assert.Equal(SyntheticMvr.EmbeddedSpec, wfc.GdtfSpec);
        Assert.Equal("16 CH", wfc.GdtfMode);
        Assert.Equal(new DmxAddress(2, 1, 1), Assert.Single(wfc.Addresses));
        Assert.Equal(7, wfc.FixtureId);
        Assert.Null(wfc.MountedOn);

        // Symbol instances expanded into mesh children (Chair twice through two symbols, one nested in a symdef).
        var chairs = scene.Find(G(SyntheticMvr.Chairs))!;
        Assert.Null(chairs.Get<MeshComponent>());
        Assert.Equal(4, chairs.Children.Count);
        Assert.All(chairs.Children, c =>
        {
            Assert.Equal("Chair", c.Name);
            Assert.Equal(box.Id, c.Get<MeshComponent>()!.ModelAssetId);
        });
        Assert.Equal(
            new[] { new Vector3(0, 3, 0), new Vector3(0, 3, 1), new Vector3(5, 0, 0), new Vector3(5, 0, 1) },
            chairs.Children.Select(c => c.Local.Position));
        // Inner group turns +90° about Z: local (0, 3, 0) of the chairs lands at (2 − 3, 0, 1).
        AssertNear(new Vector3(-1, 0, 1), chairs.Children[0].WorldPosition, 1e-5f);

        // Screen, projector: meshes. Mirror kept as negative X scale.
        Assert.Equal(box.Id, scene.Find(G(SyntheticMvr.Screen))!.Get<MeshComponent>()!.ModelAssetId);
        Assert.Equal(box.Id, scene.Find(G(SyntheticMvr.Projector))!.Get<MeshComponent>()!.ModelAssetId);
        var mirrored = scene.Find(G(SyntheticMvr.Mirrored))!;
        Assert.Equal(-1f, mirrored.Local.Scale.X);
        AssertProperRotation(mirrored.Local, allowMirror: true);
        AssertMatrixNear(result.Document.AllObjects().Single(o => o.Uuid == mirrored.Id).Matrix, mirrored.Local.ToMatrix(), 1e-6f);
        Assert.Contains(result.Issues, i => i.Code == MvrIssueCodes.MatrixMirrored && i.Entities.Contains(mirrored.Id));

        // Layer matrix applied to the layer's objects.
        var setPiece = scene.Find(G(SyntheticMvr.SetPiece))!;
        Assert.Equal(G(SyntheticMvr.LayerSet), setPiece.LayerId);
        AssertNear(new Vector3(1, 0, 0.5f), setPiece.WorldPosition, 1e-6f);

        // Connection → cable between the two fixtures' ports.
        var (cableEntity, cable) = Assert.Single(scene.Query<CableComponent>());
        Assert.Equal(2, cable.Endpoints.Count);
        Assert.Equal((wash.Id, "Output"), (cable.Endpoints[0].EntityId, cable.Endpoints[0].PortName));
        Assert.Equal((spot.Id, "Input"), (cable.Endpoints[1].EntityId, cable.Endpoints[1].PortName));
        Assert.Single(cable.Segments);
        Assert.Equal(spot.LayerId, cableEntity.LayerId);

        // GDTF resolution per spec.
        Assert.Equal(new[] { SyntheticMvr.LibrarySpec, SyntheticMvr.EmbeddedSpec, SyntheticMvr.StubSpec }, result.Gdtfs.Select(g => g.SpecName));
        var library = result.Gdtfs[0];
        Assert.Equal((MvrGdtfSource.Library, true), (library.Source, library.IsExactModel));
        Assert.Null(library.EmbeddedBytes);
        var embedded = result.Gdtfs[1];
        Assert.Equal((MvrGdtfSource.Embedded, false), (embedded.Source, embedded.EmbeddedIsStub));
        Assert.Equal(SyntheticMvr.EmbeddedSpec, embedded.EmbeddedFileName);
        Assert.Equal(File.ReadAllBytes(Path.Combine(TestData.RepoRoot, SyntheticMvr.EmbeddedLibraryFile)), embedded.ReadBytes());
        var stub = result.Gdtfs[2];
        Assert.Equal((MvrGdtfSource.Library, FixtureMatchKind.SameModel, true), (stub.Source, stub.MatchKind, stub.EmbeddedIsStub));
        Assert.All(result.Gdtfs, g => Assert.True(Assert.Single(g.Modes).Exact));
        Assert.Contains(result.Issues, i => i.Code == MvrIssueCodes.GdtfStub);

        // Data the scene cannot hold is listed, not lost silently.
        foreach (var what in new[] { "Classing", "hanging position", "beam alignments", "overwrites", "custom commands", "protocol", "network", "multipatch", "video mappings", "custom gobos", "inverted pan/tilt", "video sources", "projections" })
            Assert.Contains(result.Issues, i => i.Code == MvrIssueCodes.NotRepresentable && i.Message.Contains(what, StringComparison.Ordinal));
    }

    [Fact]
    public void Scene_export_round_trip_keeps_structure_patch_geometry_and_connections()
    {
        var first = MvrImporter.Import(MvrReader.Read(Archive.Value, "synthetic.mvr"), new MvrImportOptions { Library = TestData.Library });
        var options = new MvrExportOptions
        {
            GdtfProvider = name => first.Gdtfs.FirstOrDefault(g => g.FileName == name)?.ReadBytes(),
            ModelProvider = id => first.Assets.FirstOrDefault(a => a.Id == id)?.Decode(),
            ProviderVersion = "test",
        };
        var export = MvrWriter.BuildDocument(first.Scene, options);
        foreach (var i in export.Issues) _out.WriteLine($"export {i.Severity} {i.Code}: {i.Message}");
        Assert.DoesNotContain(export.Issues, i => i.Severity != IssueSeverity.Note);
        Assert.Equal(1, export.ConnectionCount);
        Assert.Equal(1, export.FocusPointCount);
        // 18 entities minus the cable (written as a connection) minus the 4 chair meshes folded back into Geometry3D entries.
        Assert.Equal(13, export.ObjectCount);

        var doc = export.Document;
        Assert.Equal(new[] { "Rig", "Set" }, doc.Layers.Select(l => l.Name));
        Assert.False(doc.Layers[1].HasMatrix); // baked into the objects
        var chairs = doc.AllObjects().OfType<MvrSceneObject>().Single(o => o.Uuid == G(SyntheticMvr.Chairs));
        Assert.Equal(4, chairs.Geometries.Count);
        Assert.Empty(chairs.Children);
        var spot = doc.AllObjects().OfType<MvrFixture>().Single(o => o.Uuid == G(SyntheticMvr.Spot));
        var connection = Assert.Single(spot.Connections);
        Assert.Equal(("Input", "Output", G(SyntheticMvr.Wash)), (connection.Own, connection.Other, connection.ToObject));
        var focus = doc.Layers[0].Objects.OfType<MvrFocusPoint>().Single();
        Assert.Equal(focus.Uuid, spot.Focus);
        Assert.Equal(new Vector3(1, 2, 0), focus.Matrix.Translation);
        // The screen and the projector come back as scene objects (Core keeps only their mesh).
        Assert.IsType<MvrSceneObject>(doc.AllObjects().Single(o => o.Uuid == G(SyntheticMvr.Screen)));
        // Files: GDTFs (3 distinct) and models (GLB box, GLB of the 3DS truss), all in archive root.
        Assert.Equal(3, doc.Files.Count(f => f.Kind == MvrFileKind.Gdtf));
        Assert.Equal(2, doc.Files.Count(f => f.Kind == MvrFileKind.Glb));
        Assert.All(doc.Files, f => Assert.DoesNotContain('/', f.Name));

        using var ms = new MemoryStream();
        MvrWriter.WriteDocument(doc, ms);
        var second = MvrImporter.Import(MvrReader.Read(ms.ToArray(), "export.mvr"), new MvrImportOptions { Library = TestData.Library });
        foreach (var i in second.Issues) _out.WriteLine($"reimport {i.Severity} {i.Code}: {i.Message}");
        Assert.Empty(second.Document.Issues);
        Assert.Equal(first.Scene.Count, second.Scene.Count);
        Assert.All(second.Gdtfs, g => Assert.Equal(MvrGdtfSource.Embedded, g.Source));

        foreach (var a in first.Scene.All())
        {
            var b = second.Scene.Find(a.Id);
            Assert.True(b is not null, $"{a.Name} ({a.Id}) is missing after the round trip");
            Assert.Equal(a.Name, b!.Name);
            Assert.Equal(a.Parent?.Id, b.Parent?.Id);
            AssertMatrixNear(a.WorldMatrix, b.WorldMatrix, 1e-4f);
            Assert.Equal(a.Has<MeshComponent>(), b.Has<MeshComponent>());
            Assert.Equal(a.Has<TrussComponent>(), b.Has<TrussComponent>());
            Assert.Equal(a.Has<RiggingPointComponent>(), b.Has<RiggingPointComponent>());
            Assert.Equal(a.Has<GroupComponent>(), b.Has<GroupComponent>());
            if (a.Get<FixtureComponent>() is { } fa)
            {
                var fb = b.Get<FixtureComponent>()!;
                Assert.Equal((fa.GdtfSpec, fa.GdtfMode, fa.FixtureId, fa.UnitNumber, fa.CustomId, fa.Color, fa.CastShadow, fa.FocusPoint, fa.MountedOn),
                    (fb.GdtfSpec, fb.GdtfMode, fb.FixtureId, fb.UnitNumber, fb.CustomId, fb.Color, fb.CastShadow, fb.FocusPoint, fb.MountedOn));
                Assert.Equal(fa.Addresses, fb.Addresses);
            }
            if (a.Get<CableComponent>() is { } ca)
            {
                var cb = b.Get<CableComponent>()!;
                Assert.Equal(ca.Endpoints.Select(e => (e.EntityId, e.PortName)), cb.Endpoints.Select(e => (e.EntityId, e.PortName)));
            }
        }
        Assert.Equal(3f, second.Scene.Find(G(SyntheticMvr.Truss))!.Get<TrussComponent>()!.Length, 4);
        var boxAgain = second.Scene.Find(G(SyntheticMvr.Hoist))!.Get<MeshComponent>()!.ModelAssetId;
        var bounds = second.Assets.Single(a => a.Id == boxAgain).Decode().ComputeBounds();
        AssertNear(Vector3.Zero, bounds.Min, 1e-5f);
        AssertNear(SyntheticMvr.BoxSize, bounds.Max, 1e-5f);
    }
}
