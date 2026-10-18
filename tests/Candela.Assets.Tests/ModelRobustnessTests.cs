using System.Text;

namespace Candela.Assets.Tests;

/// <summary>
/// Damaged model files must end in <see cref="AssetImportException"/> or a usable model — never in a crash or hang of
/// the process. Every input here crashed (segmentation fault) or hung the native Assimp reader before
/// <see cref="ModelPreflight"/>, <see cref="ObjSanitizer"/> and <see cref="PlyReader"/> were added; a regression aborts
/// the test run.
/// </summary>
public class ModelRobustnessTests
{
    private static void ImportsOrRejects(string fileName, byte[] data)
    {
        try
        {
            var result = ModelImporter.Import(fileName, data);
            foreach (var part in result.Model.Parts) part.Mesh.Validate();
        }
        catch (AssetImportException)
        {
            // Rejected with a message: fine.
        }
    }

    private static void EveryTruncationImportsOrRejects(string fileName, byte[] data, int maxCases = 400)
    {
        int step = System.Math.Max(1, data.Length / maxCases);
        for (int length = 0; length < data.Length; length += step) ImportsOrRejects(fileName, data[..length]);
    }

    private static void RandomCorruptionsImportOrReject(string fileName, byte[] data, int cases, int seed)
    {
        var rng = new Random(seed);
        for (int k = 0; k < cases; k++)
        {
            var copy = (byte[])data.Clone();
            int flips = 1 + rng.Next(8);
            for (int f = 0; f < flips; f++) copy[rng.Next(copy.Length)] = (byte)rng.Next(256);
            ImportsOrRejects(fileName, copy);
        }
    }

    [Theory]
    [InlineData("v 0 0 0\nv 1 0 0\nv 0 1 0\nf 1/")]
    [InlineData("v 0 0 0\nv 1 0 0\nv 0 1 0\nf 1/\n")]
    [InlineData("v 0 0 0\nv 1 0 0\nv 0 1 0\nf 1/1 2/\n")]
    [InlineData("v 0 0 0\nv 1 0 0\nv 0 1 0\nf 1// 2// 3//\n")]
    [InlineData("v 0 0 0\nv 1 0 0\nv 0 1 0\nf 1/1/1/1 2 3\n")]
    [InlineData("v 0 0 0\nv 1 0 0\nv 0 1 0\nf 0 1 2\n")]
    [InlineData("v 0 0 0\nv 1 0 0\nv 0 1 0\nf -9 -2 -1\n")]
    [InlineData("v nan 0 0\nv 1 0 inf\nv 0 1 0\nf 1 2 3\n")]
    [InlineData("v 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3\n")]
    [InlineData("usemtl\nmtllib\no\nv 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3\n")]
    public void MalformedObjStatementsDoNotCrash(string obj)
    {
        ImportsOrRejects("bad.obj", Encoding.ASCII.GetBytes(obj));
    }

    [Fact]
    public void TruncatedAndCorruptedObjFilesDoNotCrash()
    {
        var obj = Encoding.ASCII.GetBytes(TestFiles.Obj("box.mtl", "M"));
        EveryTruncationImportsOrRejects("box.obj", obj);
        RandomCorruptionsImportOrReject("box.obj", obj, 150, 1);
    }

    [Fact]
    public void DamagedMaterialLibrariesDoNotCrash()
    {
        // Statements before the first newmtl or after a nameless one crashed Assimp's MTL reader (no current material).
        var obj = Encoding.ASCII.GetBytes(TestFiles.Obj("box.mtl", "M"));
        foreach (var bad in new[] { "Kd 0.5 0.5 0.5\nnewmtl M\n", "newmtl\nKd 0.5 0.5 0.5\nmap_Kd tex.png\n", "newmtl \t\nNs 10\n" })
        {
            var single = ModelImporter.Import("box.obj", obj, new Dictionary<string, byte[]> { ["box.mtl"] = Encoding.ASCII.GetBytes(bad) });
            Assert.Single(single.Model.Materials);
        }
        var mtl = Encoding.ASCII.GetBytes("""
            # exported
            Kd 0.5 0.5 0.5
            newmtl M
            Ns 10.0000
            d 0.3000
            Tr 0.7000
            Tf 0.3 0.3 0.3
            Ka 0.588 0.588 0.588
            Kd 0.588 0.588 0.588
            map_Kd -s 1 1 1 -o 0 0 0 -bm 1 tex.png
            map_bump -bm 0.5 tex.png
            refl -type sphere tex.png
            """);
        var png = TestFiles.Png(4, 4, 200, 100, 50);
        int step = System.Math.Max(1, mtl.Length / 120);
        for (int length = 0; length <= mtl.Length; length += step)
        {
            var companions = new Dictionary<string, byte[]> { ["box.mtl"] = mtl[..length], ["tex.png"] = png };
            var result = ModelImporter.Import("box.obj", obj, companions);
            Assert.Single(result.Model.Materials);
        }
        var rng = new Random(7);
        for (int k = 0; k < 150; k++)
        {
            var copy = (byte[])mtl.Clone();
            for (int f = 0; f < 1 + rng.Next(6); f++) copy[rng.Next(copy.Length)] = (byte)rng.Next(256);
            ModelImporter.Import("box.obj", obj, new Dictionary<string, byte[]> { ["box.mtl"] = copy, ["tex.png"] = png });
        }
    }

    [Theory]
    [InlineData("ply\nformat ascii")]
    [InlineData("ply\nformat ascii 1.0\nelement vertex 8\nproperty float x\nproperty float y\nproperty float z\nelement face 6\npr")]
    [InlineData("ply\nformat ascii 1.0\nelement vertex 99999999999\nproperty float x\nend_header\n1 2 3\n")]
    [InlineData("ply\nformat binary_little_endian 1.0\nelement vertex 3\nproperty float x\nproperty float y\nproperty float z\nelement face 1\nproperty list uchar int vertex_indices\nend_header\n")]
    public void DamagedPlyHeadersAreRejectedQuickly(string ply)
    {
        var e = Assert.Throws<AssetImportException>(() => ModelImporter.Import("bad.ply", Encoding.ASCII.GetBytes(ply)));
        Assert.Contains("PLY", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TruncatedAndCorruptedPlyFilesDoNotCrashOrHang()
    {
        var ascii = Encoding.ASCII.GetBytes(TestFiles.AsciiPly());
        var binary = PlyTests.BinaryPly(bigEndian: false);
        EveryTruncationImportsOrRejects("box.ply", ascii);
        EveryTruncationImportsOrRejects("box.ply", binary);
        RandomCorruptionsImportOrReject("box.ply", ascii, 150, 2);
        RandomCorruptionsImportOrReject("box.ply", binary, 150, 3);
    }

    [Theory]
    [InlineData("<scene><instance_visual_scene url=\"#Scene\"/></scene>\n</COLLADA>", "<scene><instance_visual_scene url=\"#Scene\"/></scene>\n</COLLADA")]
    [InlineData("</library_visual_scenes>", "</library_vi\tual_scenes>")]
    [InlineData("count=\"24\">", "count=\"24\">nan inf ")]
    [InlineData("stride=\"3\"", "stride=\"300\"")]
    [InlineData("<p>4 5 7", "<p>-4 5 7")]
    [InlineData("count=\"24\">", "count=\"2400\">")]
    public void DamagedColladaIsRejectedBeforeAssimpSeesIt(string original, string replacement)
    {
        var dae = TestFiles.Collada(1f, "Z_UP");
        Assert.Contains(original, dae, StringComparison.Ordinal);
        var e = Assert.Throws<AssetImportException>(() =>
            ModelImporter.Import("bad.dae", Encoding.UTF8.GetBytes(dae.Replace(original, replacement, StringComparison.Ordinal))));
        Assert.Contains("COLLADA", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TruncatedAndCorruptedColladaFilesDoNotCrash()
    {
        var dae = Encoding.UTF8.GetBytes(TestFiles.Collada());
        EveryTruncationImportsOrRejects("box.dae", dae);
        RandomCorruptionsImportOrReject("box.dae", dae, 150, 4);
    }

    [Fact]
    public void TruncatedAndCorruptedGlbFilesDoNotCrash()
    {
        // Damaged GLBs used to be retried with Assimp's glTF reader, which crashed on them.
        var glb = TestFiles.Glb();
        EveryTruncationImportsOrRejects("box.glb", glb);
        RandomCorruptionsImportOrReject("box.glb", glb, 150, 5);
    }

    [Fact]
    public void TruncatedAndCorruptedFbx3dsAndStlFilesDoNotCrash()
    {
        var fbx = Encoding.ASCII.GetBytes(TestFiles.AsciiFbx(zUp: false, unitScaleFactorCm: 1));
        var threeDs = TestFiles.ThreeDs();
        var stl = Encoding.ASCII.GetBytes(TestFiles.AsciiStl());
        var binaryStl = TestFiles.BinaryStl(TestFiles.BoxTriangles(yUp: false));
        foreach (var (name, data, seed) in new[] { ("box.fbx", fbx, 6), ("box.3ds", threeDs, 7), ("box.stl", stl, 8), ("box.stl", binaryStl, 9) })
        {
            EveryTruncationImportsOrRejects(name, data, 150);
            RandomCorruptionsImportOrReject(name, data, 100, seed);
        }
    }
}
