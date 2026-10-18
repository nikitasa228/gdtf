using System.Diagnostics;
using System.Numerics;
using Candela.Core.Geometry;

namespace Candela.Assets.Tests;

public class ModelImporterTests
{
    /// <summary>The test box: 1 m wide (X), 0.5 m deep (Y), 2 m high (Z) in Candela space.</summary>
    private static readonly Vector3 s_boxSize = new(1f, 0.5f, 2f);

    private static void AssertBox(ImportedModel result, float tolerance = 1e-3f)
    {
        var bounds = result.Model.ComputeBounds();
        Geo.AssertSize(s_boxSize, bounds.Size, tolerance);
        Assert.InRange(bounds.Min.Z, -tolerance, tolerance);
        Assert.True(Geo.SignedVolume(result.Model) > 0.9, $"signed volume {Geo.SignedVolume(result.Model)} (faces must point outwards)");
        foreach (var part in result.Model.Parts)
        {
            part.Mesh.Validate();
            Assert.True(Geo.NormalsAgreeWithWinding(part.Mesh), "normals disagree with the winding");
            Assert.NotNull(part.Mesh.Tangents);
            Assert.Equal(part.Mesh.Positions.Length, part.Mesh.Tangents!.Length);
        }
    }

    [Fact]
    public void AssimpNativeLibraryLoads()
    {
        Assert.Null(AssimpNative.EnsureLoaded());
        Assert.True(AssimpNative.IsAvailable);
        Assert.False(string.IsNullOrEmpty(AssimpNative.LibraryPath));
    }

    [Fact]
    public void WindowsNativeLibraryShipsAndNeedsNoVisualCppRuntime()
    {
        // Loading can only be exercised on Windows; here the shipped win-x64 binary is checked: a 64-bit PE whose only
        // import is KERNEL32.dll (C runtime linked statically), so it loads without a Visual C++ redistributable.
        var dll = Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native", "assimp.dll");
        Assert.True(File.Exists(dll), $"{dll} is missing from the build output");
        var pe = File.ReadAllBytes(dll);
        Assert.Equal((byte)'M', pe[0]);
        Assert.Equal((byte)'Z', pe[1]);
        int header = BitConverter.ToInt32(pe, 0x3C);
        Assert.Equal(0x00004550u, BitConverter.ToUInt32(pe, header)); // "PE\0\0"
        Assert.Equal(0x8664, BitConverter.ToUInt16(pe, header + 4)); // x64
        int sections = BitConverter.ToUInt16(pe, header + 6);
        int optional = header + 24;
        int optionalSize = BitConverter.ToUInt16(pe, header + 20);
        Assert.Equal(0x20B, BitConverter.ToUInt16(pe, optional)); // PE32+
        uint importRva = BitConverter.ToUInt32(pe, optional + 112 + 8); // data directory 1
        int sectionTable = optional + optionalSize;
        int Offset(uint rva)
        {
            for (int i = 0; i < sections; i++)
            {
                int sec = sectionTable + i * 40;
                uint va = BitConverter.ToUInt32(pe, sec + 12), size = BitConverter.ToUInt32(pe, sec + 8), raw = BitConverter.ToUInt32(pe, sec + 20);
                if (rva >= va && rva < va + System.Math.Max(size, BitConverter.ToUInt32(pe, sec + 16))) return (int)(rva - va + raw);
            }
            throw new InvalidDataException($"RVA {rva:X} is outside every section");
        }
        var imports = new List<string>();
        for (int d = Offset(importRva); BitConverter.ToUInt32(pe, d + 12) != 0; d += 20)
        {
            int name = Offset(BitConverter.ToUInt32(pe, d + 12));
            int end = Array.IndexOf(pe, (byte)0, name);
            imports.Add(System.Text.Encoding.ASCII.GetString(pe, name, end - name));
        }
        Assert.Equal(new[] { "KERNEL32.DLL" }, imports.Select(i => i.ToUpperInvariant()).ToArray());
    }

    [Fact]
    public void ObjWithMtlAndPngIsConvertedToZUpWithTexture()
    {
        using var dir = new TempDir();
        dir.Write("textures/wood.png", TestFiles.Png(8, 4, 200, 100, 50));
        dir.Write("box.mtl", "newmtl Painted\nKd 0.6 0.6 0.6\nKs 0.04 0.04 0.04\nNs 10\nd 1\nmap_Kd textures/wood.png\n");
        var objPath = dir.Write("box.obj", TestFiles.Obj("box.mtl", "Painted"));

        var result = ModelImporter.Import(objPath);

        Assert.Equal(ModelFormat.Obj, result.Format);
        Assert.Equal(SourceUpAxis.Y, result.SourceUpAxis);
        Assert.Equal(1f, result.UnitScale);
        AssertBox(result);
        Assert.Contains("box.mtl", result.Files.Keys);
        Assert.Contains("textures/wood.png", result.Files.Keys);
        Assert.Empty(result.MissingFiles);

        var material = Assert.Single(result.Model.Materials);
        Assert.Equal("Painted", material.Name);
        Assert.Equal("textures/wood.png", material.BaseColorTexture);
        Assert.Equal(0f, material.Metallic);
        // Ns 10 → (2 / 12)^¼ ≈ 0.64.
        Assert.InRange(material.Roughness, 0.6f, 0.68f);
        Assert.Equal(AlphaMode.Opaque, material.AlphaMode);
        foreach (var part in result.Model.Parts) Assert.NotNull(part.Mesh.TexCoords);
    }

    [Fact]
    public void ObjTexturesResolveThroughAssetStoreKeys()
    {
        using var dir = new TempDir();
        dir.Write("wood.png", TestFiles.Png(8, 4, 200, 100, 50));
        dir.Write("box.mtl", "newmtl Painted\nKd 0.6 0.6 0.6\nmap_Kd wood.png\n");
        var objPath = dir.Write("box.obj", TestFiles.Obj("box.mtl", "Painted"));
        var store = new AssetStore();

        var entry = store.ImportFile(objPath);

        Assert.Equal(AssetKind.Model, entry.Kind);
        Assert.Equal("box.obj", entry.OriginalFileName);
        Assert.True(entry.CompanionFiles.ContainsKey("box.mtl"));
        Assert.True(entry.CompanionFiles.ContainsKey("wood.png"));
        var model = store.GetModel(entry.Id).Model;
        var key = model.Materials[0].BaseColorTexture;
        Assert.Equal(AssetKeys.ForFile(entry.Id, "wood.png"), key);
        var image = store.Load(key!);
        Assert.NotNull(image);
        Assert.Equal(8, image!.Width);
        Assert.Equal(4, image.Height);
        Assert.Equal(new byte[] { 200, 100, 50, 255 }, image.Pixels[..4]);
        Assert.DoesNotContain(store.Issues, i => i.Severity != Core.Diagnostics.IssueSeverity.Note);
    }

    [Fact]
    public void ObjWithMissingTextureStillImportsAndReportsTheFile()
    {
        using var dir = new TempDir();
        dir.Write("box.mtl", "newmtl Painted\nKd 0.5 0.2 0.1\nmap_Kd missing.png\n");
        var objPath = dir.Write("box.obj", TestFiles.Obj("box.mtl", "Painted"));

        var result = ModelImporter.Import(objPath);

        AssertBox(result);
        Assert.Contains(result.MissingFiles, f => f.EndsWith("missing.png", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, w => w.Contains("missing.png", StringComparison.Ordinal));
        Assert.Null(result.Model.Materials[0].BaseColorTexture);
        Assert.Equal(new Vector4(0.5f, 0.2f, 0.1f, 1f), result.Model.Materials[0].BaseColorFactor);
    }

    [Fact]
    public void CompanionFilesAreLimitedToWhatTheModelNeeds()
    {
        // Textures referenced above the model folder and by absolute paths are read when they are images; anything else
        // (a crafted reference to a private file, a "texture" that is no image) is not stored with the asset.
        using var dir = new TempDir();
        dir.Write("shared/stone.png", TestFiles.Png(2, 2, 90, 90, 90));
        var secret = dir.Write("private/secret.txt", "do not embed");
        dir.Write("model/notes.txt", "not an image");
        dir.Write("model/box.mtl",
            "newmtl A\nmap_Kd ../shared/stone.png\n" +
            $"newmtl B\nmap_Kd {secret}\n" +
            "newmtl C\nmap_Kd notes.txt\n");
        var obj = TestFiles.Obj("box.mtl", "A").Replace("usemtl A\n", "", StringComparison.Ordinal);
        var faces = obj.Split('\n').Where(l => l.StartsWith("f ", StringComparison.Ordinal)).ToArray();
        var head = string.Join('\n', obj.Split('\n').Where(l => !l.StartsWith("f ", StringComparison.Ordinal)));
        var path = dir.Write("model/box.obj", head + "\nusemtl A\n" + faces[0] + "\n" + faces[1] + "\nusemtl B\n" + faces[2] + "\n" + faces[3]
            + "\nusemtl C\n" + faces[4] + "\n" + faces[5] + "\n");

        var result = ModelImporter.Import(path);

        Assert.Contains("stone.png", result.Files.Keys.Select(k => Path.GetFileName(k)));
        Assert.DoesNotContain(result.Files.Keys, k => k.EndsWith("secret.txt", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Files.Keys, k => k.EndsWith("notes.txt", StringComparison.Ordinal));
        var a = result.Model.Materials.Single(m => m.Name == "A");
        Assert.NotNull(a.BaseColorTexture);
        Assert.True(result.Files.ContainsKey(a.BaseColorTexture!));
        Assert.Null(result.Model.Materials.Single(m => m.Name == "B").BaseColorTexture);
        Assert.Null(result.Model.Materials.Single(m => m.Name == "C").BaseColorTexture);
        Assert.Contains(result.Warnings, w => w.Contains("notes.txt", StringComparison.Ordinal));
    }

    [Fact]
    public void ObjWithoutNormalsGetsGeneratedNormalsAndTangents()
    {
        using var dir = new TempDir();
        var obj = TestFiles.Obj("none.mtl", "x").Replace("mtllib none.mtl\n", "", StringComparison.Ordinal).Replace("usemtl x\n", "", StringComparison.Ordinal);
        var path = dir.Write("plain.obj", obj);

        var result = ModelImporter.Import(path);

        AssertBox(result);
        var mesh = result.Model.Parts[0].Mesh;
        Assert.Equal(mesh.Positions.Length, mesh.Normals.Length);
        Assert.All(mesh.Normals, n => Assert.InRange(n.Length(), 0.99f, 1.01f));
        // A box needs split normals at its 90° edges: every normal is axis-aligned.
        Assert.All(mesh.Normals, n => Assert.InRange(MathF.Max(MathF.Abs(n.X), MathF.Max(MathF.Abs(n.Y), MathF.Abs(n.Z))), 0.999f, 1.001f));
        Assert.Single(result.Model.Materials); // default material
    }

    [Fact]
    public void GlbYUpBoxComesOutWithHeightOnZ()
    {
        var glb = TestFiles.Glb();

        var result = ModelImporter.Import("box.glb", glb);

        Assert.Equal(ModelFormat.Glb, result.Format);
        Assert.Equal(SourceUpAxis.Y, result.SourceUpAxis);
        Assert.True(result.UnitsKnown);
        AssertBox(result);
        // glTF front (+Z) faces the audience (−Y) in Candela.
        var bounds = result.Model.ComputeBounds();
        Geo.AssertNear(new Vector3(-0.5f, -0.25f, 0f), bounds.Min);
        Geo.AssertNear(new Vector3(0.5f, 0.25f, 2f), bounds.Max);
    }

    [Fact]
    public void GltfNodeTransformsAndInstancesAreConvertedToCandelaSpace()
    {
        // Two instances of the box: one lifted 1 m (glTF +Y) and moved 3 m along X, one turned 90° about the glTF up axis.
        var model = TestFiles.GltfModel(withTexture: false);
        var first = model.LogicalNodes[0];
        first.LocalMatrix = Matrix4x4.CreateTranslation(3f, 1f, 0f);
        var second = model.DefaultScene.CreateNode("turned");
        second.Mesh = first.Mesh;
        second.LocalMatrix = Matrix4x4.CreateRotationY(MathF.PI / 2) * Matrix4x4.CreateTranslation(-3f, 0f, 0f);

        var result = ModelImporter.Import("two.glb", model.WriteGLB().ToArray());

        Assert.Equal(2, result.Model.Parts.Count);
        Assert.Same(result.Model.Parts[0].Mesh, result.Model.Parts[1].Mesh); // instanced, converted once
        var lifted = result.Model.Parts.Single(p => p.Name == "box");
        var turned = result.Model.Parts.Single(p => p.Name == "turned");
        var liftedBounds = lifted.Mesh.ComputeBounds().Transform(lifted.Transform);
        var turnedBounds = turned.Mesh.ComputeBounds().Transform(turned.Transform);
        Geo.AssertNear(new Vector3(2.5f, -0.25f, 1f), liftedBounds.Min);
        Geo.AssertNear(new Vector3(3.5f, 0.25f, 3f), liftedBounds.Max);
        // Turned 90° about the vertical: 0.5 m deep along X, 1 m wide along Y, still 2 m high.
        Geo.AssertSize(new Vector3(0.5f, 1f, 2f), turnedBounds.Size);
        Assert.Equal(-3f, (turnedBounds.Min.X + turnedBounds.Max.X) / 2, 4);
        Assert.Equal(0f, turnedBounds.Min.Z, 4);
        Assert.True(Geo.SignedVolume(result.Model) > 1.9);
    }

    [Fact]
    public void GlbPbrMaterialMapsDirectlyAndEmbeddedTextureResolves()
    {
        var glb = TestFiles.Glb();
        var store = new AssetStore();

        var entry = store.Add(AssetKind.Model, "box.glb", glb);
        var imported = store.GetModel(entry.Id);

        var m = Assert.Single(imported.Model.Materials);
        Assert.Equal("Painted", m.Name);
        Geo.AssertNear(new Vector3(0.9f, 0.5f, 0.2f), new Vector3(m.BaseColorFactor.X, m.BaseColorFactor.Y, m.BaseColorFactor.Z));
        Assert.Equal(0f, m.Metallic);
        Assert.Equal(0.4f, m.Roughness, 3);
        Assert.NotNull(m.BaseColorTexture);
        Assert.StartsWith(AssetKeys.TexturePrefix(entry.Id) + "embedded/", m.BaseColorTexture, StringComparison.Ordinal);
        var image = store.Load(m.BaseColorTexture!);
        Assert.NotNull(image);
        Assert.Equal(4, image!.Width);
        Assert.Equal(new byte[] { 0, 200, 0, 255 }, image.Pixels[..4]);
    }

    [Fact]
    public void GltfWithExternalBufferAndTextureReadsCompanions()
    {
        // The same box as .gltf + .bin + .png, imported from disk.
        using var dir = new TempDir();
        var settings = new SharpGLTF.Schema2.WriteSettings { ImageWriting = SharpGLTF.Schema2.ResourceWriteMode.SatelliteFile, JsonIndented = true };
        TestFiles.GltfModel().SaveGLTF(dir.File("box.gltf"), settings);
        Assert.Single(Directory.GetFiles(dir.Path, "*.bin"));
        Assert.Single(Directory.GetFiles(dir.Path, "*.png"));

        var result = ModelImporter.Import(dir.File("box.gltf"));

        Assert.Equal(ModelFormat.Gltf, result.Format);
        AssertBox(result);
        Assert.True(result.Files.Keys.Count(k => k.EndsWith(".bin", StringComparison.Ordinal)) == 1);
        var texture = result.Model.Materials[0].BaseColorTexture;
        Assert.NotNull(texture);
        Assert.True(result.Files.ContainsKey(texture!), $"texture {texture} should be a companion file");
    }

    [Fact]
    public void ColladaInCentimetresIsScaledToMetres()
    {
        var dae = System.Text.Encoding.UTF8.GetBytes(TestFiles.Collada(meter: 0.01f));

        var result = ModelImporter.Import("box.dae", dae);

        Assert.Equal(ModelFormat.Collada, result.Format);
        Assert.Equal(0.01f, result.UnitScale, 6);
        Assert.True(result.UnitsKnown);
        Assert.Equal(SourceUpAxis.Z, result.SourceUpAxis);
        AssertBox(result);
        var m = Assert.Single(result.Model.Materials);
        Assert.Equal(0f, m.Metallic);
        Geo.AssertNear(new Vector3(0.8f, 0.1f, 0.1f), new Vector3(m.BaseColorFactor.X, m.BaseColorFactor.Y, m.BaseColorFactor.Z), 0.01f);
        Assert.InRange(m.Roughness, 0.3f, 0.5f);
    }

    [Fact]
    public void ColladaYUpIsRotated()
    {
        var dae = System.Text.Encoding.UTF8.GetBytes(TestFiles.Collada(meter: 1f, upAxis: "Y_UP"));

        var result = ModelImporter.Import("box.dae", dae);

        Assert.Equal(SourceUpAxis.Y, result.SourceUpAxis);
        AssertBox(result);
    }

    [Fact]
    public void ColladaFromDiskThroughAssetStoreKeepsScale()
    {
        using var dir = new TempDir();
        var path = dir.Write("set piece.dae", TestFiles.Collada(meter: 0.01f));
        var store = new AssetStore();

        var entry = store.ImportFile(path);
        var reloaded = new AssetStore();
        var copy = reloaded.Add(entry.Kind, entry.OriginalFileName, entry.Data, entry.CompanionFiles, id: entry.Id, importOptions: entry.ImportOptions);

        AssertBox(store.GetModel(entry.Id));
        AssertBox(reloaded.GetModel(copy.Id));
        Assert.Equal(entry.ContentHash, copy.ContentHash);
    }

    [Fact]
    public void AsciiStlIsZUpMetres()
    {
        var stl = System.Text.Encoding.ASCII.GetBytes(TestFiles.AsciiStl());

        var result = ModelImporter.Import("box.stl", stl);

        Assert.Equal(ModelFormat.Stl, result.Format);
        Assert.Equal(SourceUpAxis.Z, result.SourceUpAxis);
        Assert.False(result.UnitsKnown);
        Assert.Equal(1f, result.UnitScale);
        AssertBox(result);
        Assert.Single(result.Model.Materials);
    }

    [Fact]
    public void BinaryStlIsZUpMetres()
    {
        var stl = TestFiles.BinaryStl(TestFiles.BoxTriangles(yUp: false));

        var result = ModelImporter.Import("box.stl", stl);

        AssertBox(result);
        Assert.Equal(12, result.TriangleCount);
    }

    [Fact]
    public void LargeUnitlessModelIsReadAsMillimetresWithAWarning()
    {
        var stl = TestFiles.BinaryStl(TestFiles.BoxTriangles(yUp: false, scale: 1000f));

        var result = ModelImporter.Import("box_mm.stl", stl);

        Assert.Equal(0.001f, result.UnitScale, 6);
        Assert.False(result.UnitsKnown);
        Assert.Contains(result.Warnings, w => w.Contains("millimetres", StringComparison.Ordinal));
        AssertBox(result);
    }

    [Fact]
    public void ExplicitOptionsOverrideAxisAndUnits()
    {
        // The Z-up STL read as if it were Y-up and in centimetres.
        var stl = TestFiles.BinaryStl(TestFiles.BoxTriangles(yUp: true, scale: 100f));

        var auto = ModelImporter.Import("box.stl", stl, options: new ImportOptions { GuessUnits = false });
        var overridden = ModelImporter.Import("box.stl", stl, options: new ImportOptions { UpAxis = SourceUpAxis.Y, UnitScale = 0.01f });

        Geo.AssertSize(new Vector3(100f, 200f, 50f), auto.Model.ComputeBounds().Size);
        Assert.Equal(SourceUpAxis.Y, overridden.SourceUpAxis);
        Assert.True(overridden.UnitsKnown);
        AssertBox(overridden);
    }

    [Fact]
    public void AsciiFbxHonoursAxisSystemAndUnitScale()
    {
        // Maya-style (Y up) in metres (UnitScaleFactor 100 cm) and 3ds Max-style (Z up) in millimetres (0.1 cm).
        var yUp = System.Text.Encoding.ASCII.GetBytes(TestFiles.AsciiFbx(zUp: false, unitScaleFactorCm: 100));
        var zUp = System.Text.Encoding.ASCII.GetBytes(TestFiles.AsciiFbx(zUp: true, unitScaleFactorCm: 0.1));

        var a = ModelImporter.Import("maya.fbx", yUp);
        var b = ModelImporter.Import("max.fbx", zUp);

        Assert.Equal(ModelFormat.Fbx, a.Format);
        Assert.Equal(1f, a.UnitScale, 5);
        Assert.Equal(SourceUpAxis.Y, a.SourceUpAxis);
        AssertBox(a);
        Assert.Equal(0.001f, b.UnitScale, 6);
        Assert.Equal(SourceUpAxis.Z, b.SourceUpAxis);
        AssertBox(b);
        // Dark diffuse with a strong white highlight and a metal name → metal; shininess 200 → glossy.
        var chrome = a.Model.Materials.Single(m => m.Name.Contains("Chrome", StringComparison.Ordinal));
        Assert.Equal(1f, chrome.Metallic);
        Assert.InRange(chrome.Roughness, 0.2f, 0.4f);
    }

    [Fact]
    public void MirroredNodesAreBakedSoPartTransformsNeverReflect()
    {
        // Symmetric props are often exported as one mesh instanced with a negative scale. A part transform with a
        // negative determinant would turn the instance inside out for a renderer that culls by winding.
        var model = TestFiles.GltfModel(withTexture: false);
        var original = model.LogicalNodes[0];
        original.LocalMatrix = Matrix4x4.CreateTranslation(2f, 0f, 0f);
        var mirror = model.DefaultScene.CreateNode("mirror");
        mirror.Mesh = original.Mesh;
        mirror.LocalMatrix = Matrix4x4.CreateScale(-1f, 1f, 1f) * Matrix4x4.CreateTranslation(-2f, 0f, 0f);

        var result = ModelImporter.Import("pair.glb", model.WriteGLB().ToArray());

        Assert.Equal(2, result.Model.Parts.Count);
        foreach (var part in result.Model.Parts)
        {
            Assert.True(MeshTools.Determinant3(part.Transform) > 0, $"part '{part.Name}' still mirrors");
            Assert.True(Geo.NormalsAgreeWithWinding(part.Mesh));
            part.Mesh.Validate();
        }
        Assert.NotSame(result.Model.Parts[0].Mesh, result.Model.Parts[1].Mesh);
        // Both boxes are closed and outward-facing in world space.
        Assert.True(Geo.SignedVolume(result.Model) > 1.9, $"signed volume {Geo.SignedVolume(result.Model)}");
        var mirrored = result.Model.Parts.Single(p => p.Name == "mirror");
        var bounds = mirrored.Mesh.ComputeBounds().Transform(mirrored.Transform);
        Geo.AssertNear(new Vector3(-2.5f, -0.25f, 0f), bounds.Min);
        Geo.AssertNear(new Vector3(-1.5f, 0.25f, 2f), bounds.Max);
        // A mirrored world normal still points away from the box: the +X face of the source is the −X face now.
        var mesh = mirrored.Mesh;
        var center = (bounds.Min + bounds.Max) / 2;
        for (int i = 0; i < mesh.Positions.Length; i++)
        {
            var p = Vector3.Transform(mesh.Positions[i], mirrored.Transform);
            var n = Vector3.Normalize(Vector3.TransformNormal(mesh.Normals[i], mirrored.Transform));
            Assert.True(Vector3.Dot(n, p - center) > 0, "normal points into the box");
        }
    }

    [Fact]
    public void MirroringColladaNodeIsBakedToo()
    {
        var dae = TestFiles.Collada(1f, "Z_UP").Replace("<node id=\"box\" name=\"box\">", "<node id=\"box\" name=\"box\"><scale>-1 1 1</scale>", StringComparison.Ordinal);
        var result = ModelImporter.Import("mirror.dae", System.Text.Encoding.UTF8.GetBytes(dae));
        var part = Assert.Single(result.Model.Parts);
        Assert.True(MeshTools.Determinant3(part.Transform) > 0);
        AssertBox(result);
    }

    [Fact]
    public void PlyAndThreeDsImport()
    {
        var ply = ModelImporter.Import("box.ply", System.Text.Encoding.ASCII.GetBytes(TestFiles.AsciiPly()));
        var threeDs = ModelImporter.Import("box.3ds", TestFiles.ThreeDs());

        AssertBox(ply);
        Assert.Equal(ModelFormat.Ply, ply.Format);
        AssertBox(threeDs);
        Assert.Equal(ModelFormat.ThreeDs, threeDs.Format);
    }

    [Fact]
    public void SketchUpIsRejectedWithAClearMessage()
    {
        using var dir = new TempDir();
        var path = dir.Write("stage.skp", TestFiles.SketchUpHeader());
        var store = new AssetStore();

        var byExtension = Assert.Throws<AssetImportException>(() => store.ImportFile(path));
        var byPath = Assert.Throws<AssetImportException>(() => ModelImporter.Import(path));
        // A SketchUp file renamed to another model extension is recognised by its header.
        var byHeader = Assert.Throws<AssetImportException>(() => ModelImporter.Import("stage.dae", TestFiles.SketchUpHeader()));
        var byAdd = Assert.Throws<AssetImportException>(() => store.Add(AssetKind.Model, "stage.obj", TestFiles.SketchUpHeader()));

        const string expected = "SketchUp files cannot be read; export from SketchUp as .dae, .fbx or .glb";
        Assert.Equal(expected, AssetFormats.SketchUpMessage);
        Assert.Equal(expected, byExtension.Message);
        Assert.Equal(expected, byPath.Message);
        Assert.Equal(expected, byHeader.Message);
        Assert.Equal(expected, byAdd.Message);
        Assert.Equal(0, store.Count);
    }

    [Theory]
    [InlineData("scene.blend", "Blender")]
    [InlineData("scene.max", "3ds Max")]
    [InlineData("drawing.dwg", "DWG")]
    [InlineData("print.3mf", "3MF files are not supported")]
    [InlineData("scene.x3d", "X3D files are not supported")]
    [InlineData("mesh.x", "DirectX .x files are not supported")]
    [InlineData("notes.txt", "not a supported 3D model")]
    public void ForeignFormatsAreRejectedWithAdvice(string fileName, string fragment)
    {
        var e = Assert.Throws<AssetImportException>(() => ModelImporter.Import(fileName, new byte[] { 1, 2, 3 }));
        Assert.Contains(fragment, e.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("broken.obj")]
    [InlineData("broken.fbx")]
    [InlineData("broken.dae")]
    [InlineData("broken.stl")]
    [InlineData("broken.glb")]
    [InlineData("broken.3ds")]
    public void CorruptFilesThrowImportExceptionInsteadOfCrashing(string fileName)
    {
        var garbage = new byte[512];
        new Random(42).NextBytes(garbage);
        Assert.Throws<AssetImportException>(() => ModelImporter.Import(fileName, garbage));
    }

    [Fact]
    public void EmptyFileIsRejected()
    {
        var e = Assert.Throws<AssetImportException>(() => ModelImporter.Import("empty.obj", Array.Empty<byte>()));
        Assert.Contains("empty", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LargeModelImportsInReasonableTime()
    {
        // 2 × 230² = 105 800 triangles.
        var stl = TestFiles.BinaryStl(TestFiles.Grid(230));
        var sw = Stopwatch.StartNew();

        var result = ModelImporter.Import("terrain.stl", stl);

        sw.Stop();
        Assert.Equal(2 * 230 * 230, result.TriangleCount);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), $"import took {sw.Elapsed}");
        var mesh = result.Model.Parts[0].Mesh;
        // Identical vertices are joined (STL stores three per facet).
        Assert.True(mesh.VertexCount < 2 * result.TriangleCount, $"{mesh.VertexCount} vertices");
        Geo.AssertSize(new Vector3(10f, 10f, mesh.ComputeBounds().Size.Z), mesh.ComputeBounds().Size);
    }

    [Fact]
    public void LargeObjWithoutNormalsImportsInReasonableTime()
    {
        // 2 × 240² = 115 200 triangles as indexed OBJ text without normals: exercises parsing, vertex joining and the
        // angle-aware normal generator.
        const int cells = 240;
        var sb = new System.Text.StringBuilder(8 << 20);
        for (int y = 0; y <= cells; y++)
            for (int x = 0; x <= cells; x++)
                sb.Append(FormattableString.Invariant($"v {x * 0.05f} {MathF.Sin(x * 0.2f) * 0.2f} {-y * 0.05f}\n"));
        for (int y = 0; y < cells; y++)
            for (int x = 0; x < cells; x++)
            {
                int a = y * (cells + 1) + x + 1, b = a + 1, c = a + cells + 1, d = c + 1;
                sb.Append(FormattableString.Invariant($"f {a} {b} {d}\nf {a} {d} {c}\n"));
            }
        var obj = System.Text.Encoding.ASCII.GetBytes(sb.ToString());
        var sw = Stopwatch.StartNew();

        var result = ModelImporter.Import("terrain.obj", obj);

        sw.Stop();
        Assert.Equal(2 * cells * cells, result.TriangleCount);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), $"import took {sw.Elapsed}");
        var mesh = result.Model.Parts[0].Mesh;
        // The wavy surface is smooth (dihedral angles far below 66°): no vertex is split.
        Assert.Equal((cells + 1) * (cells + 1), mesh.VertexCount);
        Assert.All(mesh.Normals, n => Assert.True(n.Z > 0.5f));
    }

    [Fact]
    public void ConcurrentImportsAreIndependent()
    {
        var dae = System.Text.Encoding.UTF8.GetBytes(TestFiles.Collada(meter: 0.01f));
        var stl = TestFiles.BinaryStl(TestFiles.BoxTriangles(yUp: false));
        var glb = TestFiles.Glb();
        var results = new ImportedModel[24];

        Parallel.For(0, results.Length, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
        {
            results[i] = (i % 3) switch
            {
                0 => ModelImporter.Import("box.dae", dae),
                1 => ModelImporter.Import("box.stl", stl),
                _ => ModelImporter.Import("box.glb", glb),
            };
        });

        Assert.All(results, r => AssertBox(r));
    }

    [Fact]
    public void ManyObjectsAreFlattenedIntoOnePartWithASubMeshPerMaterial()
    {
        // Three OBJ objects (two materials); with MaxParts = 1 they are merged into one part.
        var sb = new System.Text.StringBuilder("mtllib two.mtl\n");
        var corners = TestFiles.BoxCorners(yUp: true);
        for (int o = 0; o < 3; o++)
        {
            sb.Append("o Box").Append(o).Append('\n');
            foreach (var c in corners) sb.Append(FormattableString.Invariant($"v {c.X + 2 * o} {c.Y} {c.Z}\n"));
            sb.Append("usemtl ").Append(o == 1 ? "Blue" : "Red").Append('\n');
            foreach (var q in TestFiles.BoxQuads()) sb.Append(FormattableString.Invariant($"f {-8 + q[0]} {-8 + q[1]} {-8 + q[2]} {-8 + q[3]}\n"));
        }
        var files = new Dictionary<string, byte[]>
        {
            ["two.mtl"] = System.Text.Encoding.ASCII.GetBytes("newmtl Red\nKd 0.8 0 0\nnewmtl Blue\nKd 0 0 0.8\nnewmtl Unused\nKd 1 1 1\n"),
        };
        var obj = System.Text.Encoding.ASCII.GetBytes(sb.ToString());

        var separate = ModelImporter.Import("three.obj", obj, files);
        var merged = ModelImporter.Import("three.obj", obj, files, new ImportOptions { MaxParts = 1 });

        Assert.Equal(3, separate.Model.Parts.Count);
        Assert.Equal(2, separate.Model.Materials.Count); // unused and default materials are dropped
        var part = Assert.Single(merged.Model.Parts);
        Assert.Equal(36, merged.TriangleCount);
        Assert.Equal(2, part.Mesh.SubMeshes.Count);
        Assert.Equal(2, merged.Model.Materials.Count);
        Assert.Contains(merged.Warnings, w => w.Contains("merged", StringComparison.Ordinal));
        Assert.Equal(separate.Model.ComputeBounds().Size, merged.Model.ComputeBounds().Size);
        Geo.AssertSize(new Vector3(5f, 0.5f, 2f), merged.Model.ComputeBounds().Size);
        foreach (var sm in part.Mesh.SubMeshes)
        {
            var color = merged.Model.Materials[sm.MaterialSlot].BaseColorFactor;
            Assert.Equal(sm.IndexCount == 24 * 3 ? 0.8f : 0f, color.X, 3);
        }
    }
}
