using System.Numerics;
using Candela.Core.Geometry;
using Xunit.Abstractions;

namespace Candela.Gdtf.Tests;

public sealed class ModelLoadingTests
{
    private readonly ITestOutputHelper _output;

    public ModelLoadingTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// glTF models in GDTF files are Y-up (as glTF requires): after rotating them to Z-up, the file's own extents equal
    /// the declared Length (X), Width (Y) and Height (Z) within a few percent, with no unit or axis scaling.
    /// </summary>
    [Theory]
    [InlineData("Robe_Lighting@Robin_MegaPointe@", "Base", "Yoke", "Head")]
    [InlineData("Robe_Lighting@Robin_T1_Profile@", "Base", "Yoke", "Head")]
    [InlineData("Chauvet_Professional@Maverick_Storm_2_Profile@", "Base", "Yoke", "Head")]
    [InlineData("Clay_Paky@Sharpy@", "Yoke", "Body")]
    public void Gltf_models_are_y_up_metres_and_match_the_declared_size(string fixture, params string[] models)
    {
        using var archive = GdtfArchive.Open(TestData.Fixture(fixture));
        var ft = GdtfReader.Read(archive);
        var loader = new GdtfModelLoader(archive);
        foreach (var name in models)
        {
            var model = ft.FindModel(name)!;
            var loaded = loader.Load(model)!;
            Assert.Equal(ModelSourceKind.Gltf, loaded.Info.Source);
            Assert.True(loaded.Info.ConvertedFromYUp);
            Assert.Equal(1f, loaded.Info.UnitScale);
            AssertClose(model.Size, loaded.Info.FileSize, 0.05f, $"{fixture} {name} file extents after Y-up → Z-up");
            AssertMeshValid(loaded.Model, model.Size, 0.025f);
            _output.WriteLine($"{fixture} {name}: declared {model.Size}, file {loaded.Info.FileSize}");
        }
        Assert.Empty(loader.Warnings);
    }

    /// <summary>Without the axis conversion the head's height would lie along Y: the raw glTF is Y-up.</summary>
    [Fact]
    public void Raw_gltf_head_has_its_height_along_Y()
    {
        using var archive = GdtfArchive.Open(TestData.Fixture("Robe_Lighting@Robin_MegaPointe@"));
        var ft = GdtfReader.Read(archive);
        var head = ft.FindModel("Head")!;
        var raw = GltfModelReader.Read(archive.ReadBytes("models/gltf/head.glb"), "models/gltf/head.glb");
        var size = raw.ComputeBounds().Size;
        Assert.Equal(head.Height, size.Y, 0.01f);
        Assert.Equal(head.Width, size.Z, 0.01f);
        Assert.True(System.Math.Abs(head.Height - size.Z) > 0.05f);
    }

    /// <summary>3DS models are Z-up like GDTF but written in millimetres in most files; the loader detects the unit.</summary>
    [Theory]
    [InlineData("Martin_Professional@MAC_Aura@", "Base", "Yoke", "Head")]
    [InlineData("GLP@JDC_1@", "Base Yoke M1", "Head M1")]
    [InlineData("GSARC@DOT_S_FW@", "Body")]
    public void ThreeDs_models_are_z_up_millimetres_and_match_the_declared_size(string fixture, params string[] models)
    {
        using var archive = GdtfArchive.Open(TestData.Fixture(fixture));
        var ft = GdtfReader.Read(archive);
        var loader = new GdtfModelLoader(archive);
        foreach (var name in models)
        {
            var model = ft.FindModel(name)!;
            var loaded = loader.Load(model)!;
            Assert.Equal(ModelSourceKind.ThreeDs, loaded.Info.Source);
            Assert.False(loaded.Info.ConvertedFromYUp);
            Assert.Equal(0.001f, loaded.Info.UnitScale);
            AssertClose(model.Size, loaded.Info.FileSize * loaded.Info.UnitScale, 0.02f, $"{fixture} {name} 3DS extents in mm");
            AssertMeshValid(loaded.Model, model.Size, 0.02f);
            Assert.NotEmpty(loaded.Model.Materials);
        }
        Assert.Empty(loader.Warnings);
    }

    [Fact]
    public void Files_that_differ_from_the_declared_size_are_scaled_to_it()
    {
        // Magmatic Sidewinder: the 3DS body is 376 × 193 × 413 mm, the model declares 338 × 160 × 400 mm.
        using var archive = GdtfArchive.Open(TestData.Fixture("Magmatic@SIDEWINDER@"));
        var ft = GdtfReader.Read(archive);
        var model = ft.FindModel("Base")!;
        var loaded = new GdtfModelLoader(archive).Load(model)!;
        Assert.NotEqual(Vector3.One, loaded.Info.AxisScale);
        AssertMeshValid(loaded.Model, model.Size, 0.01f);

        var unscaled = new GdtfModelLoader(archive, new GdtfModelLoadOptions { FitToDeclaredSize = false }).Load(model)!;
        Assert.Equal(Vector3.One, unscaled.Info.AxisScale);
        AssertClose(new Vector3(0.376f, 0.193f, 0.413f), unscaled.Info.FinalSize, 0.01f, "unscaled Sidewinder");
    }

    [Fact]
    public void Gltf_textures_get_gdtf_keys_that_the_image_source_decodes()
    {
        string path = TestData.Fixture("BeamZ@RB_40_RGBW@");
        using var archive = GdtfArchive.Open(path);
        var ft = GdtfReader.Read(archive);
        var loader = new GdtfModelLoader(archive);
        var textured = ft.Models.Select(loader.Load).Where(m => m is not null)
            .SelectMany(m => m!.Model.Materials).FirstOrDefault(m => m.BaseColorTexture is not null);
        Assert.NotNull(textured);
        string key = textured!.BaseColorTexture!;
        Assert.StartsWith($"gdtf:{Path.GetFileName(path)}#models/gltf/", key);
        Assert.True(GdtfImageSource.TryParseKey(key, out var file, out var entry));
        Assert.Equal(Path.GetFileName(path), file);
        Assert.Contains(".glb", entry);

        var image = new GdtfImageSource(archive).Load(key);
        Assert.NotNull(image);
        Assert.True(image!.Width > 0 && image.Height > 0);
        Assert.Equal(image.Width * image.Height * 4, image.Pixels.Length);
    }

    [Fact]
    public void Wheel_gobo_images_are_found_and_decoded()
    {
        string path = TestData.Fixture("Robe_Lighting@Robin_MegaPointe@");
        using var archive = GdtfArchive.Open(path);
        var ft = GdtfReader.Read(archive);
        var slot = ft.Wheels.SelectMany(w => w.Slots).First(s => s.MediaPath is not null);
        Assert.StartsWith("wheels/", slot.MediaPath, StringComparison.OrdinalIgnoreCase);
        var image = new GdtfImageSource(archive).Load(GdtfImageSource.MakeKey(Path.GetFileName(path), slot.MediaPath!));
        Assert.NotNull(image);
        Assert.True(image!.Width >= 16);
        Assert.Null(new GdtfImageSource(archive).Load(GdtfImageSource.MakeKey("other.gdtf", slot.MediaPath!)));
        Assert.Null(new GdtfImageSource(archive).Load("asset:whatever"));
    }

    [Fact]
    public void Real_moving_head_geometry_has_meshes_for_base_yoke_and_head_and_animatable_axes()
    {
        using var archive = GdtfArchive.Open(TestData.Fixture("Robe_Lighting@Robin_MegaPointe@"));
        var ft = GdtfReader.Read(archive);
        var geometry = new FixtureGeometryBuilder(ft, new GdtfModelLoader(archive)).Build(ft.DmxModes[0]);
        Assert.Equal(new[] { "Base", "Base/Yoke", "Base/Yoke/Head" }, geometry.Parts.Take(3).Select(p => p.Path));
        Assert.Equal(2, geometry.Parts.Count(p => p.Type == GeometryType.Axis));
        Assert.All(geometry.Parts.Take(3), p => Assert.Equal(ModelSourceKind.Gltf, p.LoadedModel!.Info.Source));
        Assert.Contains(geometry.Parts, p => p.Type == GeometryType.Beam && p.Model is not null);
        Assert.Contains(geometry.Parts, p => p.Type == GeometryType.WiringObject && p.Model is null);

        var world = new Matrix4x4[geometry.Parts.Count];
        geometry.ComputeWorldTransforms(world, Matrix4x4.Identity);
        var rest = geometry.ComputeBounds(world);
        Assert.InRange(rest.Size.Z, 0.6f, 0.8f); // hanging head: base on top, lens at the bottom
        Assert.InRange(rest.Size.X, 0.3f, 0.5f);

        // Tilting the head by 90° about its X axis swings the lens forward: the fixture gets shorter.
        var animation = new Matrix4x4[geometry.Parts.Count];
        Array.Fill(animation, Matrix4x4.Identity);
        animation[geometry.FindPartByPath("Base/Yoke/Head")] = Matrix4x4.CreateRotationX(MathF.PI / 2);
        geometry.ComputeWorldTransforms(world, Matrix4x4.Identity, animation);
        Assert.True(geometry.ComputeBounds(world).Size.Z < rest.Size.Z - 0.1f);
        Assert.Empty(geometry.Warnings);
    }

    [Fact]
    public void Multi_pixel_bar_geometry_is_expanded_per_reference()
    {
        using var archive = GdtfArchive.Open(TestData.Fixture("Infinity@Furion_FX402_Bar@"));
        var ft = GdtfReader.Read(archive);
        var mode = ft.FindMode("37+160Ch")!;
        Assert.Equal(197, mode.Footprint);
        var geometry = new FixtureGeometryBuilder(ft).Build(mode);
        var instanced = geometry.Parts.Where(p => p.InstancedBy is not null).ToList();
        Assert.True(instanced.Count >= 20, $"{instanced.Count} instanced parts");
        Assert.Equal(instanced.Count, instanced.Select(p => p.Path).Distinct().Count());
        int mapped = mode.ChannelInstances.Count(i => i.References.Count > 0 && geometry.FindPart(i) >= 0);
        Assert.Equal(mode.ChannelInstances.Count(i => i.References.Count > 0), mapped);
    }

    [Theory]
    [InlineData(GdtfPrimitiveType.Cube)]
    [InlineData(GdtfPrimitiveType.Cylinder)]
    [InlineData(GdtfPrimitiveType.Sphere)]
    [InlineData(GdtfPrimitiveType.Base)]
    [InlineData(GdtfPrimitiveType.Yoke)]
    [InlineData(GdtfPrimitiveType.Head)]
    [InlineData(GdtfPrimitiveType.Scanner)]
    [InlineData(GdtfPrimitiveType.Conventional)]
    [InlineData(GdtfPrimitiveType.Pigtail)]
    [InlineData(GdtfPrimitiveType.Base1_1)]
    [InlineData(GdtfPrimitiveType.Scanner1_1)]
    [InlineData(GdtfPrimitiveType.Conventional1_1)]
    [InlineData(GdtfPrimitiveType.Undefined)]
    public void Primitive_meshes_fill_the_declared_size(GdtfPrimitiveType type)
    {
        var size = new Vector3(0.3f, 0.2f, 0.5f);
        var model = PrimitiveMeshes.Create(type, size, type.ToString())!;
        AssertMeshValid(model, size, 1e-3f);
        foreach (var part in model.Parts) AssertOutwardFacing(part.Mesh);
    }

    [Fact]
    public void Primitive_without_size_is_nothing_and_flat_sizes_stay_renderable()
    {
        Assert.Null(PrimitiveMeshes.Create(GdtfPrimitiveType.Cube, Vector3.Zero));
        var flat = PrimitiveMeshes.Create(GdtfPrimitiveType.Cylinder, new Vector3(0.1f, 0.1f, 0))!;
        Assert.Equal(PrimitiveMeshes.MinimumExtent, flat.ComputeBounds().Size.Z, 1e-6f);
    }

    [Fact]
    public void Model_with_a_missing_file_falls_back_to_its_primitive_or_a_placeholder()
    {
        var xml = """
            <GDTF DataVersion="1.2"><FixtureType Name="T" Manufacturer="M">
              <Models>
                <Model Name="A" Length="0.2" Width="0.2" Height="0.1" PrimitiveType="Cylinder" File="nothere"/>
                <Model Name="B" Length="0.2" Width="0.3" Height="0.4" PrimitiveType="Undefined" File="nothere"/>
                <Model Name="C" Length="0.2" Width="0.3" Height="0.4" PrimitiveType="Undefined"/>
              </Models>
            </FixtureType></GDTF>
            """;
        using var archive = GdtfArchive.FromBytes(TestData.GdtfWithDescription(xml), "t.gdtf");
        var ft = GdtfReader.Read(archive);
        var loader = new GdtfModelLoader(archive);
        Assert.Equal(ModelSourceKind.Primitive, loader.Load(ft.FindModel("A")!)!.Info.Source);
        var placeholder = loader.Load(ft.FindModel("B")!)!;
        Assert.Equal(ModelSourceKind.Placeholder, placeholder.Info.Source);
        AssertClose(new Vector3(0.2f, 0.3f, 0.4f), placeholder.Model.ComputeBounds().Size, 1e-3f, "placeholder");
        Assert.Null(loader.Load(ft.FindModel("C")!));
        Assert.Same(loader.Load(ft.FindModel("A")!), loader.Load(ft.FindModel("A")!));
        Assert.Contains(loader.Warnings, w => w.Message.Contains("not in the archive", StringComparison.Ordinal));
    }

    [Fact]
    public void Corrupt_model_files_become_warnings()
    {
        var xml = """
            <GDTF DataVersion="1.2"><FixtureType Name="T" Manufacturer="M">
              <Models><Model Name="A" Length="0.2" Width="0.2" Height="0.1" PrimitiveType="Cube" File="broken"/></Models>
            </FixtureType></GDTF>
            """;
        var bytes = TestData.Zip(
            ("description.xml", System.Text.Encoding.UTF8.GetBytes(xml)),
            ("models/gltf/broken.glb", new byte[] { 0x67, 0x6C, 0x54, 0x46, 2, 0, 0, 0, 99, 0, 0, 0, 1, 2, 3 }),
            ("models/3ds/broken.3ds", new byte[] { 0x4D, 0x4D, 0xFF, 0xFF, 0xFF, 0x7F, 0x3D, 0x3D }));
        using var archive = GdtfArchive.FromBytes(bytes, "t.gdtf");
        var ft = GdtfReader.Read(archive);
        var loader = new GdtfModelLoader(archive);
        var loaded = loader.Load(ft.Models[0])!;
        Assert.Equal(ModelSourceKind.Primitive, loaded.Info.Source);
        Assert.NotEmpty(loader.Warnings);
    }

    [Fact]
    public void ThreeDs_reader_rejects_non_3ds_data()
    {
        Assert.Throws<InvalidDataException>(() => ThreeDsReader.Read(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
        Assert.Throws<InvalidDataException>(() => ThreeDsReader.Read(new byte[] { 0x4D }));
        Assert.Throws<InvalidDataException>(() => GltfModelReader.Read(new byte[] { 1, 2, 3 }, "x.glb"));
    }

    private static void AssertMeshValid(ModelData model, Vector3 expectedSize, float relativeTolerance)
    {
        Assert.NotEmpty(model.Parts);
        int triangles = 0;
        foreach (var part in model.Parts)
        {
            var mesh = part.Mesh;
            mesh.Validate();
            Assert.Equal(Matrix4x4.Identity, part.Transform);
            triangles += mesh.TriangleCount;
            foreach (var n in mesh.Normals) Assert.Equal(1f, n.Length(), 1e-3f);
            foreach (var p in mesh.Positions) Assert.True(float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z));
            foreach (var s in mesh.SubMeshes) Assert.InRange(s.MaterialSlot, 0, model.Materials.Count - 1);
        }
        Assert.True(triangles > 0);
        AssertClose(Vector3.Max(expectedSize, new Vector3(PrimitiveMeshes.MinimumExtent)), model.ComputeBounds().Size, relativeTolerance, model.Name);
    }

    /// <summary>Counter-clockwise front faces: the face normal points the same way as the vertex normals.</summary>
    private static void AssertOutwardFacing(MeshData mesh)
    {
        for (int i = 0; i < mesh.Indices.Length; i += 3)
        {
            uint a = mesh.Indices[i], b = mesh.Indices[i + 1], c = mesh.Indices[i + 2];
            var face = Vector3.Cross(mesh.Positions[b] - mesh.Positions[a], mesh.Positions[c] - mesh.Positions[a]);
            var vertexNormals = mesh.Normals[a] + mesh.Normals[b] + mesh.Normals[c];
            Assert.True(Vector3.Dot(face, vertexNormals) > 0, $"triangle {i / 3} of '{mesh.Name}' faces inwards");
        }
    }

    private static void AssertClose(Vector3 expected, Vector3 actual, float relativeTolerance, string what)
    {
        for (int a = 0; a < 3; a++)
        {
            float e = expected[a], v = actual[a];
            float tolerance = System.Math.Max(relativeTolerance * System.Math.Abs(e), 2e-3f);
            Assert.True(System.Math.Abs(e - v) <= tolerance, $"{what}: axis {a} expected {e}, got {v} (all {expected} vs {actual})");
        }
    }
}
