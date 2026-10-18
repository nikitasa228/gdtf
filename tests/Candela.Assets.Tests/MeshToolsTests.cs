using System.Diagnostics;
using System.Numerics;
using Candela.Core.Geometry;

namespace Candela.Assets.Tests;

public class MeshToolsTests
{
    /// <summary>A disc of <paramref name="segments"/> triangles around one centre vertex (a classic high-valence fan).</summary>
    private static MeshData Fan(int segments, float height)
    {
        var pos = new Vector3[segments + 1];
        var idx = new uint[segments * 3];
        pos[0] = new Vector3(0, 0, height);
        for (int i = 0; i < segments; i++)
        {
            float a = i * 2 * MathF.PI / segments;
            pos[i + 1] = new Vector3(MathF.Cos(a), MathF.Sin(a), 0f);
            idx[3 * i] = 0;
            idx[3 * i + 1] = (uint)(i + 1);
            idx[3 * i + 2] = (uint)((i + 1) % segments + 1);
        }
        return new MeshData { Positions = pos, Indices = idx };
    }

    [Fact]
    public void NormalGenerationStaysFastAroundHighValenceVertices()
    {
        // 50 000 triangles share the centre vertex: comparing every pair of faces there used to take minutes.
        var disc = Fan(50_000, 0f);
        var sw = Stopwatch.StartNew();
        MeshTools.GenerateNormals(disc, 66f);
        sw.Stop();
        Assert.True(sw.Elapsed.TotalSeconds < 5, $"took {sw.Elapsed.TotalSeconds:0.00} s");
        disc.Validate();
        Assert.All(disc.Normals, n => Assert.True(n.Z > 0.999f, $"flat disc normal {n}"));
        // A flat disc needs no split vertices.
        Assert.Equal(50_001, disc.VertexCount);
    }

    [Fact]
    public void HighValenceConeApexIsSplitByTheSmoothingAngle()
    {
        // A shallow cone (slope 45°): around the apex faces differ by up to 90°, so the apex gets several normals, but
        // each is within the smoothing angle of the faces that use it.
        var cone = Fan(20_000, 1f);
        MeshTools.GenerateNormals(cone, 66f);
        cone.Validate();
        Assert.True(Geo.NormalsAgreeWithWinding(cone));
        for (int i = 0; i + 2 < cone.Indices.Length; i += 3)
        {
            var a = cone.Positions[cone.Indices[i]];
            var b = cone.Positions[cone.Indices[i + 1]];
            var c = cone.Positions[cone.Indices[i + 2]];
            var face = Vector3.Normalize(Vector3.Cross(b - a, c - a));
            foreach (var k in new[] { cone.Indices[i], cone.Indices[i + 1], cone.Indices[i + 2] })
                Assert.True(Vector3.Dot(face, cone.Normals[k]) >= MathF.Cos(66f * MathF.PI / 180f) - 1e-3f);
        }
    }

    [Fact]
    public void LowValenceMeshesKeepExactSmoothing()
    {
        var box = Primitives.Box(Vector3.One);
        box.Normals = Array.Empty<Vector3>();
        MeshTools.GenerateNormals(box, 66f);
        // A box keeps flat faces: 24 vertices, normals along the axes.
        Assert.Equal(24, box.VertexCount);
        Assert.All(box.Normals, n => Assert.Equal(1f, MathF.Max(MathF.Abs(n.X), MathF.Max(MathF.Abs(n.Y), MathF.Abs(n.Z))), 4));
    }

    [Fact]
    public void CloneIsDeep()
    {
        var box = Primitives.Box(Vector3.One);
        var copy = MeshTools.Clone(box);
        MeshTools.FlipWinding(copy);
        copy.Positions[0] = new Vector3(9f);
        Assert.NotEqual(box.Indices[1], copy.Indices[1]);
        Assert.NotEqual(box.Positions[0], copy.Positions[0]);
    }
}
