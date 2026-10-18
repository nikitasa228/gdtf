using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace Candela.Assets.Tests;

/// <summary>Candela's own PLY reader (ASCII, binary little/big endian), which replaced Assimp's for robustness.</summary>
public class PlyTests
{
    /// <summary>The Z-up test box as binary PLY with float positions and int quad faces.</summary>
    public static byte[] BinaryPly(bool bigEndian)
    {
        var corners = TestFiles.BoxCorners(yUp: false);
        var header = Encoding.ASCII.GetBytes(
            $"ply\nformat {(bigEndian ? "binary_big_endian" : "binary_little_endian")} 1.0\ncomment made by a test\n"
            + "element vertex 8\nproperty float x\nproperty float y\nproperty float z\nproperty uchar red\n"
            + "element face 6\nproperty list uchar int vertex_indices\nelement edge 1\nproperty int vertex1\nproperty int vertex2\nend_header\n");
        var body = new List<byte>();
        var b4 = new byte[4];
        void F(float v)
        {
            if (bigEndian) BinaryPrimitives.WriteSingleBigEndian(b4, v);
            else BinaryPrimitives.WriteSingleLittleEndian(b4, v);
            body.AddRange(b4);
        }
        void I(int v)
        {
            if (bigEndian) BinaryPrimitives.WriteInt32BigEndian(b4, v);
            else BinaryPrimitives.WriteInt32LittleEndian(b4, v);
            body.AddRange(b4);
        }
        foreach (var c in corners)
        {
            F(c.X);
            F(c.Y);
            F(c.Z);
            body.Add(200);
        }
        foreach (var q in TestFiles.BoxQuads())
        {
            body.Add(4);
            foreach (var i in q) I(i);
        }
        I(0);
        I(1);
        return header.Concat(body).ToArray();
    }

    private static void AssertBox(ImportedModel result)
    {
        var bounds = result.Model.ComputeBounds();
        Geo.AssertSize(new Vector3(1f, 0.5f, 2f), bounds.Size);
        Assert.True(Geo.SignedVolume(result.Model) > 0.9, "faces must point outwards");
        foreach (var part in result.Model.Parts)
        {
            part.Mesh.Validate();
            Assert.True(Geo.NormalsAgreeWithWinding(part.Mesh));
        }
        Assert.Equal(12, result.TriangleCount);
        Assert.Equal(SourceUpAxis.Z, result.SourceUpAxis);
        Assert.False(result.UnitsKnown);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BinaryPlyInBothByteOrdersImportsAsZUpBox(bool bigEndian)
    {
        var result = ModelImporter.Import("box.ply", BinaryPly(bigEndian));
        AssertBox(result);
        Assert.Equal(ModelFormat.Ply, result.Format);
    }

    [Fact]
    public void AsciiPlyWithCrLfAndCommentsImports()
    {
        var text = TestFiles.AsciiPly().Replace("format ascii 1.0\n", "format ascii 1.0\ncomment element face 99\nobj_info scanner\n", StringComparison.Ordinal)
            .Replace("\n", "\r\n", StringComparison.Ordinal);
        AssertBox(ModelImporter.Import("box.ply", Encoding.ASCII.GetBytes(text)));
    }

    [Fact]
    public void NormalsAndVertexTextureCoordinatesAreRead()
    {
        // One quad facing +Z with explicit normals and s/t coordinates (OpenGL convention: t up).
        const string ply = """
            ply
            format ascii 1.0
            element vertex 4
            property float x
            property float y
            property float z
            property float nx
            property float ny
            property float nz
            property float s
            property float t
            element face 1
            property list uchar int vertex_indices
            end_header
            0 0 0 0 0 1 0 0
            1 0 0 0 0 1 1 0
            1 1 0 0 0 1 1 1
            0 1 0 0 0 1 0 1
            4 0 1 2 3
            """;
        var result = ModelImporter.Import("quad.ply", Encoding.ASCII.GetBytes(ply));
        var mesh = Assert.Single(result.Model.Parts).Mesh;
        Assert.Equal(2, mesh.TriangleCount);
        Assert.All(mesh.Normals, n => Geo.AssertNear(Vector3.UnitZ, n));
        Assert.NotNull(mesh.TexCoords);
        // (0, 0, 0) had t = 0 → V = 1 (bottom row of the image); (0, 1, 0) had t = 1 → V = 0.
        int origin = Array.FindIndex(mesh.Positions, p => p == Vector3.Zero);
        int top = Array.FindIndex(mesh.Positions, p => p == new Vector3(0, 1, 0));
        Assert.Equal(new Vector2(0, 1), mesh.TexCoords![origin]);
        Assert.Equal(new Vector2(0, 0), mesh.TexCoords[top]);
        Assert.NotNull(mesh.Tangents);
    }

    [Fact]
    public void PerCornerTexcoordListsAreHonoured()
    {
        // MeshLab writes wedge texture coordinates as a list per face.
        const string ply = """
            ply
            format ascii 1.0
            element vertex 3
            property float x
            property float y
            property float z
            element face 1
            property list uchar int vertex_indices
            property list uchar float texcoord
            end_header
            0 0 0
            1 0 0
            0 1 0
            3 0 1 2 6 0 0 1 0 0 1
            """;
        var mesh = Assert.Single(ModelImporter.Import("tri.ply", Encoding.ASCII.GetBytes(ply)).Model.Parts).Mesh;
        Assert.Equal(1, mesh.TriangleCount);
        var uv = mesh.TexCoords!;
        Assert.Contains(new Vector2(1, 1), uv); // (1, 0, 0) with texcoord (1, 0) → V flipped
        Assert.Contains(new Vector2(0, 0), uv); // (0, 1, 0) with texcoord (0, 1)
    }

    [Fact]
    public void FacesWithInvalidIndicesAreSkippedWithAWarning()
    {
        var text = TestFiles.AsciiPly().Replace("element face 6", "element face 7", StringComparison.Ordinal) + "3 0 1 99\n";
        var result = ModelImporter.Import("box.ply", Encoding.ASCII.GetBytes(text));
        Assert.Equal(12, result.TriangleCount);
        Assert.Contains(result.Warnings, w => w.Contains("invalid vertex indices", StringComparison.Ordinal));
    }

    [Fact]
    public void PointCloudIsRejectedWithAClearMessage()
    {
        const string ply = "ply\nformat ascii 1.0\nelement vertex 2\nproperty float x\nproperty float y\nproperty float z\nend_header\n0 0 0\n1 1 1\n";
        var e = Assert.Throws<AssetImportException>(() => ModelImporter.Import("cloud.ply", Encoding.ASCII.GetBytes(ply)));
        Assert.Contains("no faces", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LargeBinaryPlyImportsQuickly()
    {
        // 200 × 200 grid of quads (80 000 triangles) without normals.
        const int n = 200;
        var header = Encoding.ASCII.GetBytes($"ply\nformat binary_little_endian 1.0\nelement vertex {(n + 1) * (n + 1)}\nproperty float x\nproperty float y\nproperty float z\nelement face {n * n}\nproperty list uchar int vertex_indices\nend_header\n");
        using var ms = new MemoryStream();
        ms.Write(header);
        using (var w = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true))
        {
            for (int y = 0; y <= n; y++)
                for (int x = 0; x <= n; x++)
                {
                    w.Write(x * 0.01f);
                    w.Write(y * 0.01f);
                    w.Write(0.05f * MathF.Sin(x * 0.1f) * MathF.Cos(y * 0.1f));
                }
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    int a = y * (n + 1) + x;
                    w.Write((byte)4);
                    w.Write(a);
                    w.Write(a + 1);
                    w.Write(a + n + 2);
                    w.Write(a + n + 1);
                }
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = ModelImporter.Import("grid.ply", ms.ToArray());
        sw.Stop();
        Assert.Equal(2 * n * n, result.TriangleCount);
        Assert.True(sw.Elapsed.TotalSeconds < 10, $"import took {sw.Elapsed.TotalSeconds:0.00} s");
        // Slopes stay below 30°, so every normal points up (and the surface is smooth: no split vertices).
        Assert.All(result.Model.Parts[0].Mesh.Normals, nrm => Assert.True(nrm.Z > 0.85f, $"normal {nrm}"));
        Assert.Equal((n + 1) * (n + 1), result.Model.Parts[0].Mesh.VertexCount);
    }
}
