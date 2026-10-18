using System.Numerics;
using System.Text;
using Candela.Core.Geometry;

namespace Candela.Assets.Tests;

/// <summary>The OBJ/MTL clean-up that runs before Assimp: valid content must survive it unchanged in meaning.</summary>
public class ObjSanitizerTests
{
    private static string Sanitize(string obj)
    {
        var report = new ObjSanitizer.Report();
        return Encoding.ASCII.GetString(ObjSanitizer.SanitizeObj(Encoding.ASCII.GetBytes(obj), report));
    }

    private static string SanitizeMtl(string mtl) =>
        Encoding.ASCII.GetString(ObjSanitizer.SanitizeMtl(Encoding.ASCII.GetBytes(mtl), new ObjSanitizer.Report()));

    [Fact]
    public void RelativeIndicesBecomeAbsoluteAndCornersUniform()
    {
        var text = Sanitize("v 0 0 0\nv 1 0 0\nv 0 1 0\nvt 0 0\nvt 1 0\nvn 0 0 1\nf -3/-2/-1 -2/-1/-1 -1/2/1\nf 1/1 2 3\n");
        Assert.Contains("f 1/1/1 2/2/1 3/2/1\n", text, StringComparison.Ordinal);
        // The second face has a texture index on one corner only: written without texture indices.
        Assert.Contains("f 1 2 3\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidVerticesKeepNumberingAndTheirFacesAreDropped()
    {
        var report = new ObjSanitizer.Report();
        var text = Encoding.ASCII.GetString(ObjSanitizer.SanitizeObj(
            Encoding.ASCII.GetBytes("v 0 0 0\nv nan 0 0\nv 0 1 0\nv 1 1 0\nf 1 2 3\nf 1 3 4\n"), report));
        Assert.Equal(1, report.InvalidElements);
        Assert.Equal(1, report.DroppedFaces);
        Assert.Contains("v 0 0 0\n", text, StringComparison.Ordinal); // placeholder keeps vertex 2 at index 2
        Assert.Contains("f 1 3 4\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("f 1 2 3", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ContinuationLinesAndCrLfAreHandled()
    {
        var text = Sanitize("v 0 0 0\r\nv 1 0 0\r\nv 0 1 0\r\nf 1 \\\r\n 2 3\r\n");
        Assert.Contains("f 1 2 3\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", text, StringComparison.Ordinal);
    }

    [Fact]
    public void LinesPointsAndFreeFormStatementsAreRemoved()
    {
        var report = new ObjSanitizer.Report();
        var text = Encoding.ASCII.GetString(ObjSanitizer.SanitizeObj(
            Encoding.ASCII.GetBytes("v 0 0 0\nv 1 0 0\nv 0 1 0\nl 1 2\np 3\ncstype bezier\ncurv 0 1 1 2\nf 1 2 3\n"), report));
        Assert.Equal(2, report.DroppedLinesAndPoints);
        Assert.DoesNotContain("curv", text, StringComparison.Ordinal);
        Assert.Contains("f 1 2 3", text, StringComparison.Ordinal);
    }

    [Fact]
    public void NamesAreCopiedByteForByte()
    {
        // UTF-8 material and object names must still match between OBJ and MTL.
        var obj = Encoding.UTF8.GetBytes("mtllib сцена.mtl\no Стена\nv 0 0 0\nv 1 0 0\nv 0 1 0\nusemtl Бархат\nf 1 2 3\n");
        var text = Encoding.UTF8.GetString(ObjSanitizer.SanitizeObj(obj, new ObjSanitizer.Report()));
        Assert.Contains("mtllib сцена.mtl\n", text, StringComparison.Ordinal);
        Assert.Contains("usemtl Бархат\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TransparencyTrBecomesDissolveUnlessDIsPresent()
    {
        var text = SanitizeMtl("newmtl A\nTr 0.25\nnewmtl B\nd 0.3\nTr 0.7\n");
        Assert.Contains("newmtl A\nd 0.75\n", text, StringComparison.Ordinal);
        Assert.Contains("newmtl B\nd 0.3\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Tr", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TextureStatementsKeepValidOptionsAndCanonicalNames()
    {
        var text = SanitizeMtl("newmtl A\nmap_kd -s 2 2 1 -clamp on wood planks.png\nnorm normal.png\nmap_Kd -s tex.png\nmap_Ks -unknown 3 x.png\nKd spectral file.rfl\n");
        Assert.Contains("map_Kd -s 2 2 1 -clamp on wood planks.png\n", text, StringComparison.Ordinal);
        Assert.Contains("map_Kn normal.png\n", text, StringComparison.Ordinal);
        // "-s" without numbers, an unknown option and a spectral colour are dropped.
        Assert.DoesNotContain("map_Kd -s tex.png", text, StringComparison.Ordinal);
        Assert.DoesNotContain("map_Ks", text, StringComparison.Ordinal);
        Assert.DoesNotContain("spectral", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ObjWithTrOnlyMaterialImportsAsTranslucent()
    {
        var obj = Encoding.ASCII.GetBytes(TestFiles.Obj("box.mtl", "M"));
        var mtl = Encoding.ASCII.GetBytes("newmtl M\nKd 0.5 0.5 0.5\nTr 0.2\n");
        var m = Assert.Single(ModelImporter.Import("box.obj", obj, new Dictionary<string, byte[]> { ["box.mtl"] = mtl }).Model.Materials);
        Assert.Equal(AlphaMode.Blend, m.AlphaMode);
        Assert.Equal(0.8f, m.BaseColorFactor.W, 3);
    }

    [Fact]
    public void NormalMapStatementsReachTheMaterial()
    {
        var obj = Encoding.ASCII.GetBytes(TestFiles.Obj("box.mtl", "M"));
        var files = new Dictionary<string, byte[]>
        {
            ["box.mtl"] = Encoding.ASCII.GetBytes("newmtl M\nKd 0.5 0.5 0.5\nmap_kd albedo.png\nnorm normal.png\n"),
            ["albedo.png"] = TestFiles.Png(4, 4, 200, 100, 50),
            ["normal.png"] = TestFiles.Png(4, 4, 128, 128, 255),
        };
        var m = Assert.Single(ModelImporter.Import("box.obj", obj, files).Model.Materials);
        Assert.Equal("albedo.png", m.BaseColorTexture);
        Assert.Equal("normal.png", m.NormalTexture);
    }

    [Fact]
    public void SanitisedImportKeepsTheOriginalFilesForTheProject()
    {
        var original = Encoding.ASCII.GetBytes("v 0 0 0\nv 1 0 0\nv 0 1 0\nl 1 2\nf -3 -2 -1\n");
        var result = ModelImporter.Import("tri.obj", original);
        Assert.Same(original, result.Files["tri.obj"]);
        Assert.Equal(1, result.TriangleCount);
        Assert.Contains(result.Warnings, w => w.Contains("Points and lines", StringComparison.Ordinal));
        var mesh = result.Model.Parts[0].Mesh;
        Assert.Equal(3, mesh.Positions.Length);
        // Y-up → Z-up: (0, 1, 0) became (0, 0, 1).
        Assert.Contains(mesh.Positions, p => Vector3.Distance(p, Vector3.UnitZ) < 1e-6f);
    }
}
