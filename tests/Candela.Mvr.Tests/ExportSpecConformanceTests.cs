using System.IO.Compression;
using System.Numerics;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Candela.Core.Geometry;
using Candela.Core.Math;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;
using SharpGLTF.Schema2;
using Scene = Candela.Core.SceneGraph.Scene;

namespace Candela.Mvr.Tests;

/// <summary>
/// What other MVR consumers receive from an export: binary glTF that the Khronos glTF validator accepts (every textured
/// primitive has texture coordinates, finite accessors), materials whose emission, transmission and index of refraction
/// survive, numbers in the forms DIN SPEC 15801's XML schema accepts, and objects of a layer split at their world place
/// for a reader that knows nothing of Candela's user data.
/// </summary>
public sealed class ExportSpecConformanceTests
{
    private static ModelData Quad(string name, bool uvs, MaterialData material, Vector3 offset = default)
    {
        var model = new ModelData { Name = name };
        model.Materials.Add(material);
        model.Parts.Add(new ModelPart
        {
            Name = name,
            Transform = Matrix4x4.CreateTranslation(offset),
            Mesh = new MeshData
            {
                Name = name,
                Positions = [new(0, 0, 0), new(1, 0, 0), new(1, 1, 0), new(0, 1, 0)],
                Normals = [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ],
                TexCoords = uvs ? [new(0, 1), new(1, 1), new(1, 0), new(0, 0)] : null,
                Indices = [0, 1, 2, 0, 2, 3],
            },
        });
        return model;
    }

    [Fact]
    public void Every_primitive_whose_material_has_a_texture_has_texture_coordinates()
    {
        // glTF validator error MESH_PRIMITIVE_TOO_FEW_TEXCOORDS: a texture binding needs TEXCOORD_0 (an OBJ with map_Kd
        // but without vt lines is such a model)
        var material = new MaterialData { Name = "Painted", BaseColorTexture = "asset:checker.png", NormalTexture = "asset:checker.png" };
        var model = Quad("No UVs", uvs: false, material);
        var withUvs = Quad("UVs", uvs: true, material, new Vector3(2, 0, 0)).Parts[0];
        withUvs.Mesh.TexCoords![1] = new Vector2(float.NaN, 0); // damaged coordinates must not reach the file either
        model.Parts.Add(withUvs);
        model.Parts[0].Mesh.Positions[3] = new Vector3(float.PositiveInfinity, 0, 0);

        var root = ModelRoot.ParseGLB(GlbWriter.Write(model, _ => ExportFidelityTests.RedPng));
        var primitives = root.LogicalMeshes.SelectMany(m => m.Primitives).ToList();
        Assert.Equal(2, primitives.Count);
        foreach (var p in primitives)
        {
            Assert.NotNull(p.Material.FindChannel("BaseColor")!.Value.Texture);
            var uv = p.GetVertexAccessor("TEXCOORD_0");
            Assert.NotNull(uv);
            Assert.All(uv!.AsVector2Array(), t => Assert.True(float.IsFinite(t.X) && float.IsFinite(t.Y)));
            Assert.All(p.GetVertexAccessor("POSITION")!.AsVector3Array(), v => Assert.True(float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z)));
        }
        // the mesh without coordinates samples the texture at (0, 0), as Candela's renderer draws it
        Assert.All(primitives[0].GetVertexAccessor("TEXCOORD_0")!.AsVector2Array(), t => Assert.Equal(Vector2.Zero, t));
        Assert.Equal(new Vector2(0, 1), primitives[1].GetVertexAccessor("TEXCOORD_0")!.AsVector2Array()[0]);

        // an untextured material on a mesh without coordinates stays without them
        var plain = ModelRoot.ParseGLB(GlbWriter.Write(Quad("Plain", uvs: false, new MaterialData { Name = "Plain" }), _ => null));
        Assert.Null(plain.LogicalMeshes[0].Primitives[0].GetVertexAccessor("TEXCOORD_0"));
    }

    [Fact]
    public void Emission_transmission_and_index_of_refraction_survive_as_gltf_extensions()
    {
        var model = new ModelData { Name = "Materials" };
        var screen = new MaterialData { Name = "Screen", EmissiveColor = new Vector3(1f, 0.5f, 0.25f), EmissiveLuminance = 450f };
        var bright = new MaterialData { Name = "Bright", EmissiveColor = new Vector3(2f, 1f, 0f), EmissiveLuminance = 100f };
        var glass = new MaterialData { Name = "Glass", Transmission = 0.8f, Ior = 1.45f };
        var water = new MaterialData { Name = "Water", Ior = 1.33f };
        var paint = new MaterialData { Name = "Paint" };
        model.Materials.AddRange([screen, bright, glass, water, paint]);
        for (int i = 0; i < model.Materials.Count; i++)
        {
            var part = Quad("P" + i, uvs: true, paint, new Vector3(2 * i, 0, 0)).Parts[0];
            part.Mesh.SubMeshes.Add(new SubMesh(0, 6, i));
            model.Parts.Add(part);
        }

        byte[] glb = GlbWriter.Write(model, _ => null);
        var root = ModelRoot.ParseGLB(glb);
        Material M(string name) => root.LogicalMaterials.Single(m => m.Name == name);
        float Factor(Material m, string channel, string parameter) =>
            (float)m.FindChannel(channel)!.Value.Parameters.Single(p => p.Name == parameter).Value;
        Vector3 Rgb(Material m) => (Vector3)m.FindChannel("Emissive")!.Value.Parameters.Single(p => p.Name == "RGB").Value;

        // colour × strength × reference luminance = the material's emitted colour × luminance
        Assert.Equal(new Vector3(1f, 0.5f, 0.25f), Rgb(M("Screen")));
        Assert.Equal(450f / GlbWriter.EmissiveReferenceLuminance, Factor(M("Screen"), "Emissive", "EmissiveStrength"), 4);
        Assert.Equal(new Vector3(1f, 0.5f, 0f), Rgb(M("Bright")));
        Assert.Equal(2f, Factor(M("Bright"), "Emissive", "EmissiveStrength"), 4);
        Assert.Equal(0.8f, Factor(M("Glass"), "Transmission", "TransmissionFactor"), 4);
        Assert.Equal(1.45f, M("Glass").IndexOfRefraction, 4);
        Assert.Equal(1.33f, M("Water").IndexOfRefraction, 4);
        Assert.Null(M("Water").FindChannel("Transmission"));
        Assert.Equal(1.5f, M("Paint").IndexOfRefraction, 4);
        Assert.Equal(Vector3.Zero, Rgb(M("Paint")));

        // optional extensions only: readers without them still read core glTF
        Assert.Empty(root.ExtensionsRequired);
        Assert.Equal(new[] { "KHR_materials_emissive_strength", "KHR_materials_ior", "KHR_materials_transmission" }, root.ExtensionsUsed.Order());
    }

    /// <summary>The number forms of the MVR 1.6 XML schema (mvr.xsd, <c>matrixtype</c> and <c>ciecolortype</c>).</summary>
    private static readonly Regex SchemaMatrix = new(@"^(\{-?[0-9]+\.?[0-9]*(e-?[0-9]*)?(,-?[0-9]+\.?[0-9]*(e-?[0-9]*)?){2}\}){4}$");

    private static readonly Regex SchemaCieColor = new(@"^-?[0-9]+\.?[0-9]*(,-?[0-9]+\.?[0-9]*){2}$");

    [Fact]
    public void Matrices_and_colours_use_the_number_forms_of_the_mvr_schema_and_read_back_exactly()
    {
        var scene = new Scene("Numbers");
        // rotations with components near zero (shortest round-trip text has an exponent), far and tiny translations
        var tilted = scene.Add(new Entity("Tilted")
        {
            Local = new Transform(new Vector3(14.88713f, 1e-9f, 3.653431f), Quaternion.CreateFromYawPitchRoll(1e-7f, 3.1415925f, 2e-8f), Vector3.One),
        });
        tilted.Add(new FixtureComponent { GdtfSpec = "Some@Spot.gdtf", GdtfMode = "Mode A", Color = new CieColor(0.3127f, 0.329f, 1e-7f) });
        scene.Add(new Entity("Huge") { Local = new Transform(new Vector3(3e20f, -2e-20f, 0), Quaternion.Identity, new Vector3(1e-30f, 1, 1)) })
            .Add(new GroupComponent());

        using var ms = new MemoryStream();
        MvrWriter.Write(scene, ms, new MvrExportOptions { GdtfProvider = _ => ConformanceTests.ModellessGdtf("Some", "Spot", 1), ProviderVersion = "test" });
        var document = MvrReader.Read(ms.ToArray(), "numbers.mvr");
        XDocument xml;
        using (var zip = new ZipArchive(new MemoryStream(ms.ToArray()), ZipArchiveMode.Read))
        using (var description = zip.GetEntry(MvrReader.SceneDescriptionEntry)!.Open())
            xml = XDocument.Load(description);

        var matrices = xml.Descendants("Matrix").Select(m => m.Value).ToList();
        Assert.NotEmpty(matrices);
        Assert.All(matrices, m => Assert.Matches(SchemaMatrix, m));
        var colour = Assert.Single(xml.Descendants("Color")).Value;
        Assert.Matches(SchemaCieColor, colour);

        // the plain decimals give back the same floats
        foreach (var e in scene.All())
        {
            var obj = document.AllObjects().Single(o => o.Uuid == e.Id);
            var expected = e.Local.ToMatrix();
            Assert.Equal(expected.M11, obj.Matrix.M11);
            Assert.Equal(expected.M12, obj.Matrix.M12);
            Assert.Equal(expected.M13, obj.Matrix.M13);
            Assert.Equal(expected.M23, obj.Matrix.M23);
            Assert.Equal(expected.M32, obj.Matrix.M32);
            Assert.Equal(expected.M41, obj.Matrix.M41);
        }
        Assert.Equal(1e-7f, ((MvrFixture)document.AllObjects().Single(o => o.Name == "Tilted")).Color!.Value.BigY);
        Assert.Equal("0", MvrMatrix.FormatFloat(float.NaN));
        Assert.Equal("-0.000000087253284", MvrMatrix.FormatFloat(-8.7253284e-08f));
        // beyond what decimals hold: a lower-case exponent without a plus sign, still exact
        foreach (float v in new[] { 1e-40f, -3e38f, 1e30f })
        {
            string text = MvrMatrix.FormatFloat(v);
            Assert.Matches(@"^-?[0-9]+\.?[0-9]*(e-?[0-9]+)?$", text);
            Assert.Equal(v, float.Parse(text, System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    [Fact]
    public void A_reader_without_candela_data_sees_every_object_of_a_layer_split_at_its_world_place()
    {
        var scene = new Scene("Split");
        var venue = scene.Layers[0];
        var light = new Layer { Name = "Light" };
        scene.Layers.Add(light);
        var rig = scene.Add(new Entity("Rig") { LayerId = venue.Id, Local = new Transform(new Vector3(4, -2, 1), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.7f), new Vector3(1, 1, 1)) });
        rig.Add(new GroupComponent());
        var truss = scene.Add(new Entity("Truss") { LayerId = venue.Id, Local = new Transform(new Vector3(0, 1, 7), Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.3f), Vector3.One) }, rig);
        truss.Add(new TrussComponent { Kind = TrussKind.Truss, Length = 8 });
        for (int i = 0; i < 4; i++)
        {
            var spot = scene.Add(new Entity("Spot " + i) { LayerId = i % 2 == 0 ? light.Id : venue.Id, Local = new Transform(new Vector3(i, 0, -0.4f)) }, truss);
            spot.Add(new FixtureComponent { GdtfSpec = "Some@Spot.gdtf", GdtfMode = "Mode A", MountedOn = truss.Id });
            var marker = scene.Add(new Entity("Marker " + i) { LayerId = venue.Id, Local = new Transform(new Vector3(0, 0.25f, 0)) }, spot);
            marker.Add(new GroupComponent());
        }

        using var ms = new MemoryStream();
        var export = MvrWriter.Write(scene, ms, new MvrExportOptions { GdtfProvider = _ => ConformanceTests.ModellessGdtf("Some", "Spot", 1), ProviderVersion = "test" });
        Assert.True(export.LayerPartCount > 0);
        var document = MvrReader.Read(ms.ToArray(), "split.mvr");
        document.UserData.Clear(); // another program: no Candela data

        // world matrices as any MVR reader composes them (local × parent), from the file alone
        var worlds = new Dictionary<Guid, (Matrix4x4 World, Guid Layer)>();
        foreach (var layer in document.Layers)
            foreach (var root in layer.Objects) Visit(root, Matrix4x4.Identity, layer.Uuid);
        foreach (var e in scene.All())
        {
            var (world, layerId) = worlds[e.Id];
            Assert.Equal(e.LayerId, layerId);
            Assert.True(Vector3.Distance(e.WorldPosition, world.Translation) < 1e-4f, $"{e.Name}: {e.WorldPosition} vs {world.Translation}");
            Assert.True(Vector3.Distance(Vector3.TransformNormal(Vector3.UnitX, e.WorldMatrix), Vector3.TransformNormal(Vector3.UnitX, world)) < 1e-4f, e.Name);
        }

        // and Candela, without its data, still imports every object at its place (the parts are plain groups)
        var plain = MvrImporter.Import(document);
        foreach (var e in scene.All())
        {
            var other = plain.Scene.Find(e.Id)!;
            Assert.True(Vector3.Distance(e.WorldPosition, other.WorldPosition) < 1e-4f, e.Name);
            Assert.Equal(e.LayerId, other.LayerId);
        }

        void Visit(MvrObject obj, Matrix4x4 parent, Guid layer)
        {
            var world = obj.Matrix * parent;
            worlds.TryAdd(obj.Uuid, (world, layer));
            foreach (var c in obj.ChildObjects) Visit(c, world, layer);
        }
    }

    [Fact]
    public void A_split_export_merged_into_the_scene_it_came_from_is_joined_again()
    {
        // the same file merged twice into its own scene: every uuid is taken, so the importer remaps them and must still
        // join the parts by the file's uuids
        var scene = new Scene("Again");
        var venue = scene.Layers[0];
        var light = new Layer { Name = "Light" };
        scene.Layers.Add(light);
        var rig = scene.Add(new Entity("Rig") { LayerId = venue.Id, Local = new Transform(new Vector3(1, 2, 3)) });
        rig.Add(new GroupComponent());
        var truss = scene.Add(new Entity("Truss") { LayerId = venue.Id, Local = new Transform(new Vector3(0, 0, 6)) }, rig);
        truss.Add(new TrussComponent { Kind = TrussKind.Truss, Length = 6 });
        for (int i = 0; i < 3; i++)
        {
            scene.Add(new Entity("Spot " + i) { LayerId = light.Id, Local = new Transform(new Vector3(i, 0, -0.3f)) }, truss)
                .Add(new FixtureComponent { GdtfSpec = "Some@Spot.gdtf", GdtfMode = "Mode A", MountedOn = truss.Id });
        }
        scene.Add(new Entity("Marker") { LayerId = venue.Id }, truss).Add(new GroupComponent());

        using var ms = new MemoryStream();
        MvrWriter.Write(scene, ms, new MvrExportOptions { GdtfProvider = _ => ConformanceTests.ModellessGdtf("Some", "Spot", 1), ProviderVersion = "test" });
        var bytes = ms.ToArray();
        var originals = scene.All().ToList();
        for (int round = 0; round < 2; round++)
        {
            var merged = MvrImporter.Merge(scene, MvrReader.Read(bytes, "again.mvr"));
            var group = merged.Group!;
            var copies = group.SelfAndDescendants().Skip(1).ToList();
            Assert.Equal(originals.Count, copies.Count);
            var spots = copies.Where(e => e.Has<FixtureComponent>()).ToList();
            Assert.Equal(3, spots.Count);
            var trussCopy = Assert.Single(copies, e => e.Name == "Truss");
            Assert.All(spots, s =>
            {
                Assert.Same(trussCopy, s.Parent);
                Assert.Equal(light.Id, s.LayerId);
                Assert.Equal(trussCopy.Id, s.Get<FixtureComponent>()!.MountedOn);
            });
            Assert.Equal(new[] { "Spot 0", "Spot 1", "Spot 2", "Marker" }, trussCopy.Children.Select(c => c.Name));
            Assert.DoesNotContain(copies, e => e.Name == "Rig" && e.Parent != group);
            foreach (var e in originals)
            {
                var copy = Assert.Single(copies, c => c.Name == e.Name);
                Assert.True(Vector3.Distance(e.WorldPosition, copy.WorldPosition) < 1e-4f, e.Name);
            }
            Assert.DoesNotContain(merged.Issues, i => i.Code == MvrIssueCodes.LayerSplit);
        }
    }
}
