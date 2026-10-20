using System.Numerics;
using Candela.Core.Diagnostics;
using Candela.Core.Geometry;
using Candela.Core.Math;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;
using Candela.Gdtf;
using ModelRoot = SharpGLTF.Schema2.ModelRoot;

namespace Candela.Mvr.Tests;

/// <summary>
/// What a scene export must keep: the fixture types and DMX modes the scene asks for (not the stand-ins a library without
/// the model draws them with), the textures of models, and the layer of every object, also of children on another layer
/// than their parent.
/// </summary>
public sealed class ExportFidelityTests
{
    // ------------------------------------------------------------------ stand-ins

    /// <summary>
    /// Фокина imported against the repository's library, with what an engine records about it: the source spec and mode
    /// of every stand-in fixture, and the GDTF the file embedded for every model the library lacks.
    /// </summary>
    private sealed record Fokina(MvrImportResult Result, Dictionary<Guid, MvrStandIn> StandIns, Dictionary<string, byte[]> RequestedGdtfs)
    {
        public static Fokina Import()
        {
            var result = MvrImporter.ImportFile(TestData.FokinaMvr, new MvrImportOptions { Library = TestData.Library });
            var substitutes = result.Gdtfs.Where(g => g.MatchKind == FixtureMatchKind.Substitute).ToDictionary(g => g.SpecName, StringComparer.OrdinalIgnoreCase);
            var standIns = new Dictionary<Guid, MvrStandIn>();
            foreach (var obj in result.Document.AllObjects().OfType<MvrDeviceObject>())
            {
                if (obj.GdtfSpec is not { } spec || !substitutes.ContainsKey(spec.Trim())) continue;
                standIns[result.FindEntity(obj.Uuid)!.Id] = new MvrStandIn(spec.Trim(), obj.GdtfMode);
            }
            var requested = substitutes.Values.ToDictionary(g => g.SpecName, g => g.EmbeddedBytes!, StringComparer.OrdinalIgnoreCase);
            return new Fokina(result, standIns, requested);
        }

        public MvrExportOptions Options(bool writeStandIns = false, bool withRequestedGdtfs = true)
        {
            var library = MvrExportOptions.GdtfFromLibrary(TestData.Library);
            return new MvrExportOptions
            {
                GdtfProvider = spec => library(spec) ?? (withRequestedGdtfs ? RequestedGdtfs.GetValueOrDefault(spec) : null),
                StandInOf = id => StandIns.GetValueOrDefault(id),
                WriteStandIns = writeStandIns,
                ProviderVersion = "test",
            };
        }
    }

    private static (MvrExportResult Export, MvrDocument Document, byte[] Bytes) Export(Scene scene, MvrExportOptions options)
    {
        using var ms = new MemoryStream();
        var export = MvrWriter.Write(scene, ms, options);
        var bytes = ms.ToArray();
        return (export, MvrReader.Read(bytes, "export.mvr"), bytes);
    }

    [Fact]
    public void Stand_ins_are_exported_as_the_models_and_modes_the_source_asks_for()
    {
        var fokina = Fokina.Import();
        Assert.Equal(87, fokina.StandIns.Count);
        var (export, document, bytes) = Export(fokina.Result.Scene, fokina.Options());

        Assert.Equal(87, export.StandInCount);
        Assert.DoesNotContain(export.Issues, i => i.Code is MvrIssueCodes.SubstituteExported or MvrIssueCodes.ExportResourceMissing);
        var source = fokina.Result.Document.AllObjects().OfType<MvrFixture>().ToDictionary(f => f.Uuid);
        var written = document.AllObjects().OfType<MvrFixture>().ToList();
        Assert.Equal(91, written.Count);
        foreach (var f in written)
        {
            var original = source[f.Uuid];
            Assert.Equal(original.GdtfMode, f.GdtfMode);
            if (fokina.StandIns.ContainsKey(f.Uuid))
            {
                // the requested model, with the GDTF the source embedded for it (byte for byte)
                Assert.Equal(original.GdtfSpec, f.GdtfSpec);
                Assert.Equal(fokina.Result.Document.FindFile(original.GdtfSpec)!.Bytes, document.FindFile(f.GdtfSpec)!.Bytes);
            }
            else
            {
                // a stub the library has the exact model of: the complete GDTF of that model under its library name
                var match = TestData.Library.FindBySpec(original.GdtfSpec!)!;
                Assert.True(match.IsExactModel);
                Assert.Equal(match.Entry.FileName, f.GdtfSpec);
                Assert.Equal(File.ReadAllBytes(match.Entry.FilePath), document.FindFile(f.GdtfSpec)!.Bytes);
            }
        }

        // opening the export again resolves every fixture exactly as opening the source did
        var again = MvrImporter.Import(document, new MvrImportOptions { Library = TestData.Library });
        foreach (var (entity, fixture) in fokina.Result.Scene.Query<FixtureComponent>())
        {
            var other = again.Scene.Find(entity.Id)!.Get<FixtureComponent>()!;
            Assert.Equal((fixture.GdtfSpec, fixture.GdtfMode), (other.GdtfSpec, other.GdtfMode));
            Assert.Equal(fixture.Addresses, other.Addresses);
        }
        Assert.Equal(4, again.Gdtfs.Count(g => g.MatchKind == FixtureMatchKind.Substitute));

        // deterministic
        Assert.Equal(bytes, Export(fokina.Result.Scene, fokina.Options()).Bytes);
    }

    [Fact]
    public void Writing_stand_ins_is_an_option_that_reports_every_stand_in_fixture()
    {
        var fokina = Fokina.Import();
        Assert.False(new MvrExportOptions().WriteStandIns); // off by default
        var (export, document, _) = Export(fokina.Result.Scene, fokina.Options(writeStandIns: true));

        var warnings = export.Issues.Where(i => i.Code == MvrIssueCodes.SubstituteExported).ToList();
        Assert.Equal(87, warnings.Count);
        Assert.All(warnings, w => Assert.Equal(IssueSeverity.Warning, w.Severity));
        Assert.Equal(fokina.StandIns.Keys.Order(), warnings.Select(w => Assert.Single(w.Entities)).Order());
        var probe = warnings.First();
        var standIn = fokina.StandIns[probe.Entities[0]];
        Assert.Contains(standIn.RequestedSpec, probe.Message, StringComparison.Ordinal);
        Assert.Contains(fokina.Result.Scene.Find(probe.Entities[0])!.Get<FixtureComponent>()!.GdtfSpec, probe.Message, StringComparison.Ordinal);

        // the file then holds the stand-ins as they are drawn
        foreach (var (entity, fixture) in fokina.Result.Scene.Query<FixtureComponent>())
        {
            var written = (MvrFixture)document.AllObjects().Single(o => o.Uuid == entity.Id);
            Assert.Equal((fixture.GdtfSpec, fixture.GdtfMode), (written.GdtfSpec, written.GdtfMode));
        }
    }

    [Fact]
    public void A_requested_model_without_a_gdtf_is_named_but_not_embedded_and_reported()
    {
        var fokina = Fokina.Import();
        var (export, document, _) = Export(fokina.Result.Scene, fokina.Options(withRequestedGdtfs: false));

        var missing = export.Issues.Where(i => i.Code == MvrIssueCodes.ExportResourceMissing).ToList();
        Assert.Equal(4, missing.Count); // one per requested model
        Assert.Contains(missing, i => i.Message.Contains("'Ayrton@MagicBlade R.gdtf'", StringComparison.Ordinal)
                                      && i.Message.Contains("Ayrton@MagicPanel_602@V2.5_Add_Mode.gdtf", StringComparison.Ordinal));
        var blade = document.AllObjects().OfType<MvrFixture>().First(f => f.GdtfSpec == "Ayrton@MagicBlade R.gdtf");
        Assert.Equal("Extended", blade.GdtfMode);
        Assert.Null(document.FindFile(blade.GdtfSpec));
    }

    [Fact]
    public void A_failing_stand_in_lookup_keeps_the_drawn_gdtf_and_is_reported()
    {
        var scene = new Scene("Lookup");
        var e = scene.Add(new Entity("Spot"));
        e.Add(new FixtureComponent { GdtfSpec = "Some@Spot.gdtf", GdtfMode = "Mode A" });
        var (export, document, _) = Export(scene, new MvrExportOptions
        {
            GdtfProvider = _ => ConformanceTests.ModellessGdtf("Some", "Spot", 1),
            StandInOf = _ => throw new InvalidOperationException("broken"),
        });
        Assert.Equal("Some@Spot.gdtf", ((MvrFixture)document.AllObjects().Single()).GdtfSpec);
        Assert.Contains(export.Issues, i => i.Code == MvrIssueCodes.ExportResourceMissing && i.Message.Contains("broken", StringComparison.Ordinal));
        Assert.Equal(0, export.StandInCount);
    }

    // ------------------------------------------------------------------ layers

    /// <summary>
    /// Layers "Venue" (default) and "Light": a venue group with children on both layers, a nested group with a child on
    /// the other layer, and a child on "Light" that has a child on "Venue" again.
    /// </summary>
    private static Scene LayeredScene(out Layer venue, out Layer light)
    {
        var scene = new Scene("Layers");
        venue = scene.Layers[0];
        venue.Name = "Venue";
        light = new Layer { Name = "Light" };
        scene.Layers.Add(light);

        var rig = scene.Add(new Entity("Rig") { LayerId = venue.Id, Local = new Transform(new Vector3(1, 2, 3), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.5f), Vector3.One) });
        rig.Add(new GroupComponent());
        var truss = scene.Add(new Entity("Truss") { LayerId = venue.Id, Local = new Transform(new Vector3(0, 0, 6)) }, rig);
        truss.Add(new TrussComponent { Kind = TrussKind.Truss, Length = 6 });
        var spot = scene.Add(new Entity("Spot on truss") { LayerId = light.Id, Local = new Transform(new Vector3(1, 0, -0.3f)) }, truss);
        spot.Add(new FixtureComponent { GdtfSpec = "Some@Spot.gdtf", GdtfMode = "Mode A", MountedOn = truss.Id });
        var wash = scene.Add(new Entity("Wash") { LayerId = light.Id, Local = new Transform(new Vector3(2, 0, 4)) }, rig);
        wash.Add(new FixtureComponent { GdtfSpec = "Some@Spot.gdtf", GdtfMode = "Mode A" });
        var marker = scene.Add(new Entity("Marker") { LayerId = venue.Id, Local = new Transform(new Vector3(0, 0.5f, 0)) }, wash);
        marker.Add(new GroupComponent());
        scene.Add(new Entity("Stage") { LayerId = venue.Id, Local = new Transform(new Vector3(0, 3, 0)) }, rig).Add(new GroupComponent());
        return scene;
    }

    private static MvrExportOptions PlainOptions() => new()
    {
        GdtfProvider = _ => ConformanceTests.ModellessGdtf("Some", "Spot", 1),
        ProviderVersion = "test",
    };

    [Fact]
    public void Children_on_another_layer_keep_their_layer_through_export_and_import()
    {
        var scene = LayeredScene(out var venue, out var light);
        var (export, document, _) = Export(scene, PlainOptions());

        // other programs: every object on its own layer, under a group of its layer that repeats the parent
        Assert.Equal(new[] { "Venue", "Light" }, document.Layers.Select(l => l.Name));
        var lightLayer = document.Layers.Single(l => l.Uuid == light.Id);
        var venueLayer = document.Layers.Single(l => l.Uuid == venue.Id);
        var rigPart = Assert.IsType<MvrGroupObject>(Assert.Single(lightLayer.Objects));
        Assert.Equal("Rig", rigPart.Name);
        Assert.NotEqual(scene.Roots[0].Id, rigPart.Uuid);
        var rigObject = venueLayer.Objects.Single(o => o.Uuid == scene.Roots[0].Id);
        Assert.Equal(rigObject.Matrix, rigPart.Matrix);
        Assert.Contains(rigPart.Children, o => o.Name == "Wash");
        var trussPart = Assert.IsType<MvrGroupObject>(rigPart.Children.Single(o => o.Name == "Truss"));
        Assert.Equal("Spot on truss", Assert.Single(trussPart.Children).Name);
        // the marker (venue) below the wash (light) goes back to the venue layer, under a part of the wash in the real rig
        var rigChildren = ((MvrGroupObject)rigObject).Children;
        var washPart = Assert.IsType<MvrGroupObject>(rigChildren.Single(o => o.Name == "Wash"));
        Assert.Equal("Marker", Assert.Single(washPart.Children).Name);
        Assert.Equal(3, export.LayerPartCount);
        var split = Assert.Single(export.Issues, i => i.Code == MvrIssueCodes.LayerSplit);
        Assert.Equal(IssueSeverity.Note, split.Severity);
        Assert.DoesNotContain(export.Issues, i => i.Code == MvrIssueCodes.NotRepresentable && i.Message.Contains("layer", StringComparison.Ordinal));

        // Candela: the parts are joined again; every entity has its layer, parent and world place
        var again = MvrImporter.Import(document);
        Assert.Equal(scene.Count, again.Scene.Count);
        foreach (var e in scene.All())
        {
            var other = again.Scene.Find(e.Id);
            Assert.NotNull(other);
            Assert.Equal(e.LayerId, other!.LayerId);
            Assert.Equal(e.Parent?.Id, other.Parent?.Id);
            Assert.True(Vector3.Distance(e.WorldPosition, other.WorldPosition) < 1e-4f, $"{e.Name}: {e.WorldPosition} vs {other.WorldPosition}");
        }
        Assert.Equal(again.Scene.Find(scene.All().Single(e => e.Name == "Truss").Id)!.Id,
            again.Scene.All().Single(e => e.Name == "Spot on truss").Get<FixtureComponent>()!.MountedOn);
        Assert.DoesNotContain(again.Issues, i => i.Code is MvrIssueCodes.LayerSplit or MvrIssueCodes.ValueInvalid or MvrIssueCodes.ObjectFailed or MvrIssueCodes.IdRemapped);
        Assert.Equal(again.Scene.Find(scene.Roots[0].Id), again.FindEntity(rigPart.Uuid));

        // the same order of children as in the scene (the joined children are put back among their siblings), so a second
        // round trip writes the same objects in the same places and the same Candela data
        Assert.Equal(scene.All().Select(e => e.Id), again.Scene.All().Select(e => e.Id));
        var second = Export(again.Scene, PlainOptions()).Document;
        Assert.Equal(Outline(document), Outline(second));
        Assert.Equal(document.UserData.Single().Element!.ToString(), second.UserData.Single().Element!.ToString());
    }

    /// <summary>Every object of a document as (layer, depth, kind, uuid, name), depth first in file order.</summary>
    private static List<(Guid Layer, int Depth, MvrObjectKind Kind, Guid Uuid, string Name)> Outline(MvrDocument document)
    {
        var result = new List<(Guid, int, MvrObjectKind, Guid, string)>();
        foreach (var layer in document.Layers)
            foreach (var root in layer.Objects) Visit(layer.Uuid, root, 0);
        return result;

        void Visit(Guid layer, MvrObject obj, int depth)
        {
            result.Add((layer, depth, obj.Kind, obj.Uuid, obj.Name));
            foreach (var c in obj.ChildObjects) Visit(layer, c, depth + 1);
        }
    }

    [Fact]
    public void Layer_parts_are_joined_whatever_the_layer_order_and_survive_another_program_moving_them()
    {
        var scene = LayeredScene(out _, out var light);
        var (_, document, _) = Export(scene, PlainOptions());
        // another program saved the file with the layers swapped and the part of the rig moved by 2 m
        document.Layers.Reverse();
        var lightLayer = document.Layers.Single(l => l.Uuid == light.Id);
        var rigPart = lightLayer.Objects[0];
        rigPart.Matrix *= Matrix4x4.CreateTranslation(2, 0, 0);
        var again = MvrImporter.Import(MvrReader.Read(MvrWriter.WriteDocument(document), "moved.mvr"));

        Assert.Equal(scene.Count, again.Scene.Count);
        var wash = again.Scene.All().Single(e => e.Name == "Wash");
        Assert.Equal(scene.Roots[0].Id, wash.Parent!.Id);
        var expected = scene.All().Single(e => e.Name == "Wash").WorldPosition + new Vector3(2, 0, 0);
        Assert.True(Vector3.Distance(expected, wash.WorldPosition) < 1e-4f, $"{wash.WorldPosition} vs {expected}");
    }

    [Fact]
    public void Layer_parts_that_no_longer_match_are_imported_as_plain_groups_and_reported()
    {
        var scene = LayeredScene(out var venue, out var light);
        var (_, document, _) = Export(scene, PlainOptions());

        // another program deleted the rig on the venue layer: its part on the light layer stays a group with the fixtures
        document.Layers.Single(l => l.Uuid == venue.Id).Objects.Clear();
        var orphan = MvrImporter.Import(MvrReader.Read(MvrWriter.WriteDocument(document), "orphan.mvr"));
        Assert.Contains(orphan.Scene.All(), e => e.Name == "Wash" && e.Parent is { Name: "Rig" } p && p.Has<GroupComponent>());
        Assert.Contains(orphan.Scene.All(), e => e.Name == "Spot on truss" && e.Parent is { Name: "Truss" });
        Assert.Contains(orphan.Issues, i => i.Code == MvrIssueCodes.LayerSplit && i.Message.Contains("plain groups", StringComparison.Ordinal));

        // damaged records are reported once and their groups imported as groups
        var (_, damagedDoc, _) = Export(LayeredScene(out _, out _), PlainOptions());
        var block = damagedDoc.UserData.Single(u => u.Provider == CandelaUserData.Provider).Element!;
        foreach (var part in block.Elements("LayerPart")) part.SetAttributeValue("original", "not a uuid");
        var damaged = MvrImporter.Import(MvrReader.Read(MvrWriter.WriteDocument(damagedDoc), "damaged.mvr"));
        Assert.Single(damaged.Issues, i => i.Code == MvrIssueCodes.ValueInvalid && i.Message.Contains("layer part", StringComparison.Ordinal));
        Assert.Equal(scene.Count + 3, damaged.Scene.Count);
    }

    [Fact]
    public void A_layer_part_that_contains_its_own_object_is_imported_without_looping()
    {
        // hostile: a part group whose record names an object inside the part itself
        var groupId = Guid.NewGuid();
        var innerId = Guid.NewGuid();
        var inner = new MvrGroupObject { Uuid = innerId, Name = "Inner" };
        var part = new MvrGroupObject { Uuid = groupId, Name = "Part", Children = { inner } };
        var doc = new MvrDocument { VerMajor = 1, VerMinor = 6 };
        var layer = new MvrLayer { Uuid = Guid.NewGuid(), Name = "Layer" };
        layer.Objects.Add(part);
        doc.Layers.Add(layer);
        doc.UserData.Add(CandelaUserData.Create(Array.Empty<CandelaUserData.WallRecord>(), Array.Empty<CandelaUserData.DeviceRecord>(),
            new[] { new CandelaUserData.LayerPartRecord(groupId, innerId) }));

        var result = MvrImporter.Import(MvrReader.Read(MvrWriter.WriteDocument(doc), "cycle.mvr"));
        Assert.Equal(2, result.Scene.Count);
        Assert.Equal("Part", result.Scene.All().Single(e => e.Name == "Inner").Parent!.Name);
        Assert.Contains(result.Issues, i => i.Code == MvrIssueCodes.LayerSplit);
    }

    [Fact]
    public void Fokina_merged_under_a_group_keeps_its_layer_counts()
    {
        // a venue scene with Фокина merged under a group on the default layer: the group's children span both layers
        var venue = new Scene("Venue");
        var merged = MvrImporter.Merge(venue, MvrReader.ReadFile(TestData.FokinaMvr), new MvrImportOptions { Library = TestData.Library });
        var counts = LayerCounts(venue);
        Assert.Equal(new Dictionary<string, int> { ["Default"] = 79, ["Light"] = 13 }, counts);

        var (export, document, _) = Export(venue, new MvrExportOptions { GdtfProvider = MvrExportOptions.GdtfFromLibrary(TestData.Library) });
        Assert.Equal(1, export.LayerPartCount);
        var again = MvrImporter.Import(document, new MvrImportOptions { Library = TestData.Library });
        Assert.Equal(counts, LayerCounts(again.Scene));
        // the same entities under the same parents, in the same order
        Assert.Equal(venue.All().Select(e => (e.Id, e.Parent?.Id, e.LayerId)), again.Scene.All().Select(e => (e.Id, e.Parent?.Id, e.LayerId)));
        Assert.Equal(13, merged.Group!.Children.Count(c => venue.FindLayer(c.LayerId)!.Name == "Light"));
    }

    private static Dictionary<string, int> LayerCounts(Scene scene) =>
        scene.All().GroupBy(e => scene.FindLayer(e.LayerId)!.Name).ToDictionary(g => g.Key, g => g.Count());

    // ------------------------------------------------------------------ textures

    /// <summary>A 1 × 1 red PNG.</summary>
    internal static readonly byte[] RedPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    /// <summary>A textured quad: base colour, normal and occlusion maps, UVs, a UV scale of 2.</summary>
    private static ModelData TexturedQuad(string name, string texture)
    {
        var mesh = new MeshData
        {
            Name = name,
            Positions = [new(0, 0, 0), new(1, 0, 0), new(1, 1, 0), new(0, 1, 0)],
            Normals = [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ],
            TexCoords = [new(0, 1), new(1, 1), new(1, 0), new(0, 0)],
            Indices = [0, 1, 2, 0, 2, 3],
        };
        var model = new ModelData { Name = name };
        model.Parts.Add(new ModelPart { Name = name, Mesh = mesh });
        model.Materials.Add(new MaterialData
        {
            Name = "Painted",
            BaseColorTexture = texture,
            NormalTexture = "asset:normal.png",
            OcclusionTexture = "asset:missing.png",
            UvScale = new Vector2(2, 2),
        });
        return model;
    }

    private static Scene TexturedScene(out Guid crate, out Guid poster)
    {
        var scene = new Scene("Textured");
        crate = Guid.NewGuid();
        poster = Guid.NewGuid();
        scene.Add(new Entity("Crate")).Add(new MeshComponent { ModelAssetId = crate });
        scene.Add(new Entity("Poster") { Local = new Transform(new Vector3(3, 0, 0)) }).Add(new MeshComponent { ModelAssetId = poster });
        return scene;
    }

    [Fact]
    public void Model_textures_are_embedded_in_the_exported_glb()
    {
        var scene = TexturedScene(out var crate, out var poster);
        var requests = new List<string>();
        var options = new MvrExportOptions
        {
            ModelProvider = id => id == crate ? TexturedQuad("Crate", "asset:checker.png") : TexturedQuad("Poster", "asset:checker.png"),
            TextureProvider = key =>
            {
                requests.Add(key);
                return key switch
                {
                    "asset:checker.png" or "asset:normal.png" => RedPng,
                    _ => null,
                };
            },
            ProviderVersion = "test",
        };
        var (export, document, bytes) = Export(scene, options);

        // each texture is asked for once, even though both models use it
        Assert.Equal(new[] { "asset:checker.png", "asset:normal.png", "asset:missing.png" }, requests);
        Assert.Equal(2, export.TextureCount);
        var missing = Assert.Single(export.Issues, i => i.Code == MvrIssueCodes.ExportResourceMissing);
        Assert.Contains("asset:missing.png", missing.Message, StringComparison.Ordinal);

        var glbs = document.Files.Where(f => f.Name.EndsWith(".glb", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, glbs.Count);
        foreach (var file in glbs)
        {
            var root = ModelRoot.ParseGLB(file.Bytes!);
            Assert.Single(root.LogicalImages); // the checker and the normal map are the same bytes: one image
            Assert.Equal(RedPng, root.LogicalImages[0].Content.Content.ToArray());
            var material = Assert.Single(root.LogicalMaterials);
            var baseColor = material.FindChannel("BaseColor")!.Value;
            Assert.NotNull(baseColor.Texture);
            Assert.Equal(new Vector2(2, 2), baseColor.TextureTransform!.Scale);
            Assert.NotNull(material.FindChannel("Normal")!.Value.Texture);
            Assert.Null(material.FindChannel("Occlusion")?.Texture);
            Assert.NotNull(root.LogicalMeshes[0].Primitives[0].GetVertexAccessor("TEXCOORD_0"));
        }
        Assert.Equal(bytes, Export(scene, options).Bytes); // deterministic
    }

    [Fact]
    public void Textures_that_cannot_be_embedded_are_reported()
    {
        var scene = TexturedScene(out var crate, out _);
        ModelData? Model(Guid id) => id == crate ? TexturedQuad("Crate", "asset:photo.tif") : null;

        // no texture source at all
        var (none, noneDoc, _) = Export(scene, new MvrExportOptions { ModelProvider = Model });
        Assert.Contains(none.Issues, i => i.Code == MvrIssueCodes.ExportResourceMissing && i.Message.Contains("No texture source", StringComparison.Ordinal));
        Assert.Empty(ModelRoot.ParseGLB(noneDoc.Files.First(f => f.Name.StartsWith("Crate", StringComparison.Ordinal)).Bytes!).LogicalImages);

        // a source that returns something else than PNG or JPEG, or throws
        var (wrong, _, _) = Export(scene, new MvrExportOptions
        {
            ModelProvider = Model,
            TextureProvider = key => key == "asset:photo.tif" ? "II*\0not a png"u8.ToArray() : throw new IOException("disk gone"),
        });
        Assert.Contains(wrong.Issues, i => i.Code == MvrIssueCodes.ExportResourceMissing && i.Message.Contains("PNG or JPEG", StringComparison.Ordinal));
        Assert.Contains(wrong.Issues, i => i.Code == MvrIssueCodes.ExportResourceMissing && i.Message.Contains("disk gone", StringComparison.Ordinal));
        Assert.Equal(0, wrong.TextureCount);
    }
}
