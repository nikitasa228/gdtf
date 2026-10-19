using System.Numerics;
using Candela.Core.Geometry;
using Candela.Core.SceneGraph.Components;

namespace Candela.Assets.Tests;

public class ProceduralMeshTests
{
    private static double Volume(MeshData mesh) =>
        Geo.SignedVolume(new ModelData { Parts = { new ModelPart { Mesh = mesh } } });

    private static void AssertWellFormed(MeshData mesh)
    {
        mesh.Validate();
        Assert.True(mesh.TriangleCount > 0);
        Assert.NotNull(mesh.TexCoords);
        Assert.NotNull(mesh.Tangents);
        Assert.Equal(mesh.Positions.Length, mesh.Tangents!.Length);
        Assert.All(mesh.Normals, n => Assert.InRange(n.Length(), 0.999f, 1.001f));
        Assert.All(mesh.Tangents, t => Assert.True(MathF.Abs(Vector3.Dot(new Vector3(t.X, t.Y, t.Z), Vector3.UnitX)) <= 1.001f && MathF.Abs(t.W) == 1f));
        Assert.True(Geo.NormalsAgreeWithWinding(mesh), $"{mesh.Name}: normals disagree with the winding");
    }

    /// <summary>The four vertices of the face whose normal is <paramref name="normal"/>.</summary>
    private static int[] Face(MeshData mesh, Vector3 normal) =>
        Enumerable.Range(0, mesh.VertexCount).Where(i => Vector3.Dot(mesh.Normals[i], normal) > 0.999f).ToArray();

    [Fact]
    public void WallStandsOnTheOriginWithItsFrontFacingTheAudience()
    {
        var wall = new WallComponent { Kind = WallKind.Wall, Width = 4f, Height = 3f, Thickness = 0.1f, UvScale = 2f };

        var mesh = WallMesh.Create(wall);

        AssertWellFormed(mesh);
        Assert.Equal(24, mesh.VertexCount);
        Assert.Equal(12, mesh.TriangleCount);
        var b = mesh.ComputeBounds();
        Geo.AssertNear(new Vector3(-2f, 0f, 0f), b.Min);
        Geo.AssertNear(new Vector3(2f, 0.1f, 3f), b.Max);
        Assert.Equal(4.0 * 3.0 * 0.1, Volume(mesh), 4);

        // Front face: plane y = 0, normal −Y, UV in metres × 2 with (0, 0) at the top-left corner (−X, top).
        var front = Face(mesh, -Vector3.UnitY);
        Assert.Equal(4, front.Length);
        Assert.All(front, i => Assert.Equal(0f, mesh.Positions[i].Y, 5));
        var uv = mesh.TexCoords!;
        var topLeft = front.Single(i => mesh.Positions[i].X < 0 && mesh.Positions[i].Z > 1);
        var bottomRight = front.Single(i => mesh.Positions[i].X > 0 && mesh.Positions[i].Z < 1);
        Assert.Equal(Vector2.Zero, uv[topLeft]);
        Geo.AssertNear(new Vector3(8f, 6f, 0f), new Vector3(uv[bottomRight], 0f));
        // Tangent along +U (+X on the front), bitangent sign per glTF.
        Geo.AssertNear(Vector3.UnitX, new Vector3(mesh.Tangents![topLeft].X, mesh.Tangents[topLeft].Y, mesh.Tangents[topLeft].Z));
    }

    [Fact]
    public void FloorTopIsAtTheOriginAndThicknessGoesDown()
    {
        var floor = new WallComponent { Kind = WallKind.Floor, Width = 4f, Height = 6f, Thickness = 0.2f, UvScale = 1f };

        var mesh = WallMesh.Create(floor);

        AssertWellFormed(mesh);
        var b = mesh.ComputeBounds();
        Geo.AssertNear(new Vector3(-2f, -3f, -0.2f), b.Min);
        Geo.AssertNear(new Vector3(2f, 3f, 0f), b.Max);
        Assert.Equal(4.0 * 6.0 * 0.2, Volume(mesh), 4);
        var top = Face(mesh, Vector3.UnitZ);
        Assert.All(top, i => Assert.Equal(0f, mesh.Positions[i].Z, 5));
        // UVs in metres; "up" on horizontal faces is +Y, so (0, 0) is at (−X, +Y).
        var uv = mesh.TexCoords!;
        Assert.Equal(Vector2.Zero, uv[top.Single(i => mesh.Positions[i].X < 0 && mesh.Positions[i].Y > 0)]);
        Geo.AssertNear(new Vector3(4f, 6f, 0f), new Vector3(uv[top.Single(i => mesh.Positions[i].X > 0 && mesh.Positions[i].Y < 0)], 0f));
    }

    [Fact]
    public void CeilingBottomIsAtTheOriginAndThicknessGoesUp()
    {
        var ceiling = new WallComponent { Kind = WallKind.Ceiling, Width = 4f, Height = 6f, Thickness = 0.2f, UvScale = 0.5f };

        var mesh = WallMesh.Create(ceiling);

        AssertWellFormed(mesh);
        var b = mesh.ComputeBounds();
        Geo.AssertNear(new Vector3(-2f, -3f, 0f), b.Min);
        Geo.AssertNear(new Vector3(2f, 3f, 0.2f), b.Max);
        var bottom = Face(mesh, -Vector3.UnitZ);
        Assert.Equal(4, bottom.Length);
        Assert.All(bottom, i => Assert.Equal(0f, mesh.Positions[i].Z, 5));
        var uvs = bottom.Select(i => mesh.TexCoords![i]).ToArray();
        Assert.Equal(2f, uvs.Max(u => u.X), 4); // 4 m × 0.5 repeats per metre
        Assert.Equal(3f, uvs.Max(u => u.Y), 4);
    }

    [Theory]
    [InlineData(WallKind.Wall, 0f, -1f, 0f)]
    [InlineData(WallKind.Floor, 0f, 0f, 1f)]
    [InlineData(WallKind.Ceiling, 0f, 0f, -1f)]
    public void ZeroThicknessGivesOnlyTheVisibleFace(WallKind kind, float nx, float ny, float nz)
    {
        var mesh = WallMesh.Create(kind, 2f, 1f, 0f);

        AssertWellFormed(mesh);
        Assert.Equal(4, mesh.VertexCount);
        Assert.All(mesh.Normals, n => Geo.AssertNear(new Vector3(nx, ny, nz), n));
    }

    [Fact]
    public void InvalidWallSizesAreClamped()
    {
        var mesh = WallMesh.Create(WallKind.Wall, float.NaN, -3f, float.PositiveInfinity, 0f);

        mesh.Validate();
        Assert.All(mesh.Positions, p => Assert.True(float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z)));
        Assert.True(mesh.ComputeBounds().Size.X <= WallMesh.MinimumSize * 1.001f);
    }

    [Theory]
    [InlineData("F34", 3f, 0.29f, 0.29f)]
    [InlineData("F44", 2.5f, 0.40f, 0.40f)]
    [InlineData("F24", 1f, 0.22f, 0.22f)]
    [InlineData("H30V", 4f, 0.29f, 0.29f)]
    public void SquareTrussBoundsAreLengthTimesProfileSize(string profile, float length, float width, float height)
    {
        var truss = new TrussComponent { Kind = TrussKind.Truss, Length = length, Profile = profile };

        var mesh = TrussMesh.Create(truss);

        AssertWellFormed(mesh);
        var b = mesh.ComputeBounds();
        Geo.AssertNear(new Vector3(0f, -width / 2, -height / 2), b.Min, 1e-4f);
        Geo.AssertNear(new Vector3(length, width / 2, height / 2), b.Max, 1e-4f);
        Assert.Equal(TrussShape.Square, TrussProfile.Parse(profile).Shape);
    }

    [Theory]
    [InlineData("F33", 0.29f, 0.050f)]
    [InlineData("F23", 0.22f, 0.035f)]
    public void TriangleTrussBoundsFollowTheEquilateralSection(string profile, float size, float chord)
    {
        var p = TrussProfile.Parse(profile);
        var mesh = TrussMesh.Create(TrussKind.Truss, 2f, p);

        AssertWellFormed(mesh);
        Assert.Equal(TrussShape.Triangle, p.Shape);
        Assert.Equal(size, p.Size, 4);
        Assert.Equal(chord, p.ChordDiameter, 4);
        float height = (size - chord) * MathF.Sqrt(3f) / 2 + chord;
        Assert.Equal(height, TrussMesh.SectionHeight(p), 5);
        var bounds = mesh.ComputeBounds();
        Geo.AssertSize(new Vector3(2f, size, height), bounds.Size, 2e-4f);
        Assert.Equal(-height / 2, bounds.Min.Z, 3);
        // Two chords on top, one at the bottom.
        var chords = TrussMesh.ChordPositions(p.Shape, p.Size, p.ChordDiameter / 2);
        Assert.Equal(3, chords.Length);
        Assert.Equal(2, chords.Count(c => c.Y > 0));
    }

    [Fact]
    public void TrussVariantsPipeLadderTower()
    {
        var pipe = TrussMesh.Create(new TrussComponent { Kind = TrussKind.Pipe, Length = 6f, Profile = "Ø48 pipe" });
        var ladder = TrussMesh.Create(new TrussComponent { Kind = TrussKind.Ladder, Length = 2f, Profile = "F32" });
        var tower = TrussMesh.Create(new TrussComponent { Kind = TrussKind.Tower, Length = 3f, Profile = "F34" });

        AssertWellFormed(pipe);
        Geo.AssertSize(new Vector3(6f, 0.048f, 0.048f), pipe.ComputeBounds().Size, 1e-4f);
        Assert.True(Volume(pipe) > 0);
        AssertWellFormed(ladder);
        Geo.AssertSize(new Vector3(2f, 0.05f, 0.29f), ladder.ComputeBounds().Size, 2e-4f);
        AssertWellFormed(tower);
        var plate = 0.29f + 0.3f;
        Geo.AssertSize(new Vector3(3f, plate, plate), tower.ComputeBounds().Size, 2e-4f);
        Assert.Equal(0f, tower.ComputeBounds().Min.X, 5);
    }

    [Fact]
    public void LongerTrussesGetMoreDiagonals()
    {
        var p = TrussProfile.Parse("F34");
        var short1 = TrussMesh.Create(TrussKind.Truss, 1f, p);
        var long4 = TrussMesh.Create(TrussKind.Truss, 4f, p);

        // Chords are the same 4 tubes; the zig-zag count grows with length (about one diagonal per 0.25 m per face).
        Assert.True(long4.TriangleCount > 3 * short1.TriangleCount / 2, $"{short1.TriangleCount} vs {long4.TriangleCount}");
        Assert.True(long4.TriangleCount < 200_000);
    }

    [Theory]
    [InlineData("F34", TrussKind.Truss, TrussShape.Square, 0.29f, true)]
    [InlineData("f44", TrussKind.Truss, TrussShape.Square, 0.40f, true)]
    [InlineData(" X30D ", TrussKind.Truss, TrussShape.Triangle, 0.29f, true)]
    [InlineData("triangle 400 mm", TrussKind.Truss, TrussShape.Triangle, 0.40f, true)]
    [InlineData("square 0.3 m", TrussKind.Truss, TrussShape.Square, 0.30f, true)]
    [InlineData("Ø50 pipe", TrussKind.Pipe, TrussShape.Pipe, 0.05f, true)]
    [InlineData("", TrussKind.Pipe, TrussShape.Pipe, 0.0483f, true)]
    [InlineData("F34", TrussKind.Ladder, TrussShape.Ladder, 0.29f, true)]
    [InlineData("banana", TrussKind.Truss, TrussShape.Square, 0.29f, false)]
    [InlineData("F34", TrussKind.Pipe, TrussShape.Pipe, 0.0483f, false)]
    public void ProfileLabelsAreParsed(string label, TrussKind kind, TrussShape shape, float size, bool recognised)
    {
        var p = TrussProfile.Parse(label, kind);

        Assert.Equal(shape, p.Shape);
        Assert.Equal(size, p.Size, 4);
        Assert.Equal(recognised, p.IsRecognised);
        Assert.True(p.ChordDiameter > 0 && p.BraceDiameter > 0 && p.BraceDiameter <= p.ChordDiameter);
    }

    [Fact]
    public void ImagePlaneFacesMinusYWithNormalisedUvs()
    {
        var image = new ImageComponent { Width = 2f, Height = 1f };

        var mesh = ImagePlaneMesh.Create(image);

        AssertWellFormed(mesh);
        Assert.Equal(4, mesh.VertexCount);
        Assert.Equal(2, mesh.TriangleCount);
        Assert.All(mesh.Normals, n => Geo.AssertNear(-Vector3.UnitY, n));
        var b = mesh.ComputeBounds();
        Geo.AssertNear(new Vector3(-1f, 0f, -0.5f), b.Min);
        Geo.AssertNear(new Vector3(1f, 0f, 0.5f), b.Max);
        var uv = mesh.TexCoords!;
        int topLeft = Enumerable.Range(0, 4).Single(i => mesh.Positions[i].X < 0 && mesh.Positions[i].Z > 0);
        int bottomRight = Enumerable.Range(0, 4).Single(i => mesh.Positions[i].X > 0 && mesh.Positions[i].Z < 0);
        Assert.Equal(Vector2.Zero, uv[topLeft]);
        Geo.AssertNear(new Vector3(1f, 1f, 0f), new Vector3(uv[bottomRight], 0f));
        Assert.Equal(new Vector2(2f, 1.125f), ImagePlaneMesh.SizeForAspect(1920, 1080, 2f));
    }

    [Fact]
    public void StraightCableTubeVertexCountAndBounds()
    {
        var points = new[] { Vector3.Zero, new Vector3(2f, 0f, 0f) };
        var options = new CableTubeOptions { Radius = 0.01f, RadialSegments = 8 };

        var mesh = CableTubeMesh.Create(points, options);

        AssertWellFormed(mesh);
        // N path points × (segments + 1) ring vertices + 2 caps × (segments + 1).
        Assert.Equal(2 * 9 + 2 * 9, mesh.VertexCount);
        Assert.Equal(8 * 2 + 2 * 8, mesh.TriangleCount);
        Geo.AssertSize(new Vector3(2f, 0.02f, 0.02f), mesh.ComputeBounds().Size, 1e-5f);
        Assert.Equal(Math.PI * 0.01 * 0.01 * 2, Volume(mesh), 4);
        // V runs along the cable in metres.
        Assert.Equal(2f, mesh.TexCoords!.Take(18).Max(u => u.Y), 4);
    }

    [Fact]
    public void PolylineCableHasOneRingPerPoint()
    {
        var points = new[] { Vector3.Zero, new Vector3(1, 0, 0), new Vector3(1, 1, 0), new Vector3(1, 1, 1), new Vector3(2, 2, 2) };

        var mesh = CableTubeMesh.Create(points, new CableTubeOptions { Radius = 0.005f, RadialSegments = 6, CapEnds = false });

        mesh.Validate();
        Assert.True(Geo.NormalsAgreeWithWinding(mesh));
        Assert.Equal(5 * 7, mesh.VertexCount);
        var b = mesh.ComputeBounds();
        Assert.InRange(b.Max.X, 2f, 2.006f);
        Assert.InRange(b.Min.X, -0.006f, 0f);
    }

    [Fact]
    public void SlackMakesACatenarySag()
    {
        const float span = 4f, slack = 1.05f;
        var points = new[] { new Vector3(0, 0, 5), new Vector3(span, 0, 5) };

        var path = CableTubeMesh.SamplePath(points, slack, 0.05f);
        var mesh = CableTubeMesh.Create(points, new CableTubeOptions { Radius = 0.005f, SlackFactor = slack, MaxSampleSpacing = 0.05f });

        float sag = CableTubeMesh.CatenarySag(span, slack);
        Assert.InRange(sag / span, 0.13f, 0.15f); // 5 % slack ≈ 14 % sag
        double length = 0;
        for (int i = 1; i < path.Length; i++) length += Vector3.Distance(path[i - 1], path[i]);
        Assert.Equal(span * slack, length, 2);
        Assert.Equal(5f - sag, path.Min(p => p.Z), 3);
        Assert.Equal(5f - sag - 0.005f, mesh.ComputeBounds().Min.Z, 3);
        Assert.Equal(path.Length * 9 + 2 * 9, mesh.VertexCount);
        // Endpoints stay where they are; a vertical drop does not sag.
        Assert.Equal(points[0], path[0]);
        Assert.Equal(points[1], path[^1]);
        Assert.Equal(2, CableTubeMesh.SamplePath(new[] { Vector3.Zero, new Vector3(0, 0, -3) }, slack, 0.05f).Length);
    }

    [Fact]
    public void CableComponentTreeIsSweptChainByChain()
    {
        // A─B─C with a branch B─D: three chains (A–B, B–C, B–D) because B is a branch point.
        var a = new CableNode { Position = new Vector3(0, 0, 0) };
        var b = new CableNode { Position = new Vector3(1, 0, 0) };
        var c = new CableNode { Position = new Vector3(2, 0, 0) };
        var d = new CableNode { Position = new Vector3(1, 1, 0) };
        var cable = new CableComponent
        {
            Nodes = { a, b, c, d },
            Segments = { new(a.Id, b.Id), new(b.Id, c.Id), new(b.Id, d.Id), new(a.Id, Guid.NewGuid()) },
            SlackFactor = 1f,
        };
        var options = new CableTubeOptions { RadialSegments = 8, CapEnds = false };

        var tree = CableTubeMesh.Create(cable, options: options);
        var chain = CableTubeMesh.Create(new CableComponent { Nodes = { a, b, c }, Segments = { new(a.Id, b.Id), new(b.Id, c.Id) } }, options: options);

        Assert.Equal(3 * 2 * 9, tree.VertexCount);
        Assert.Equal(3 * 9, chain.VertexCount); // one continuous tube through B
        Geo.AssertSize(new Vector3(2f, 1.004f, 0.008f), tree.ComputeBounds().Size, 1e-4f);
    }

    [Fact]
    public void ASaggingSpanLiesOnTheSurfaceUnderItInsteadOfSinkingThroughIt()
    {
        // 6 m level span 2 cm above a floor at z = 0 with 20 % slack: unsupported it sags about 1.9 m
        var points = new[] { new Vector3(0, 0, 0.02f), new Vector3(6, 0, 0.02f) };
        const float radius = 0.005f;
        CableSupportHeight floor = (x, y, maxHeight) => maxHeight >= -0.02f ? 0f : float.NegativeInfinity;
        var free = CableTubeMesh.SamplePath(points, 1.2f, 0.05f);
        Assert.True(free.Min(p => p.Z) < -1.5f);

        var lying = CableTubeMesh.SamplePath(points, 1.2f, 0.05f, floor, radius);
        Assert.All(lying, p => Assert.True(p.Z >= radius - 1e-6f, $"path point at z = {p.Z}"));
        var mesh = CableTubeMesh.Create(points, new CableTubeOptions { Radius = radius, SlackFactor = 1.2f, MaxSampleSpacing = 0.05f, Support = floor });
        mesh.Validate();
        Assert.True(mesh.ComputeBounds().Min.Z >= -1e-5f, $"tube reaches z = {mesh.ComputeBounds().Min.Z}");
        Assert.Equal(points[0], lying[0]);
        Assert.Equal(points[1], lying[^1]);
    }

    [Fact]
    public void ASurfaceAboveTheSpansChordDoesNotLiftIt()
    {
        // a cable running under a platform at 1 m sags freely; one draped from 3 m over a 1 m platform lies on it
        CableSupportHeight platform = (x, y, maxHeight) => x is >= 2f and <= 4f && maxHeight >= 0.98f ? 1f : float.NegativeInfinity;
        var under = CableTubeMesh.SamplePath(new[] { new Vector3(0, 0, 0.5f), new Vector3(6, 0, 0.5f) }, 1.05f, 0.05f, platform, 0.004f);
        var reference = CableTubeMesh.SamplePath(new[] { new Vector3(0, 0, 0.5f), new Vector3(6, 0, 0.5f) }, 1.05f, 0.05f);
        Assert.Equal(reference, under);

        var over = CableTubeMesh.SamplePath(new[] { new Vector3(0, 0, 3f), new Vector3(6, 0, 3f) }, 1.5f, 0.05f, platform, 0.004f);
        Assert.All(over.Where(p => p.X is >= 2f and <= 4f), p => Assert.True(p.Z >= 1.004f - 1e-5f, $"z = {p.Z} over the platform"));
        Assert.Contains(over, p => p.X is >= 2.5f and <= 3.5f && MathF.Abs(p.Z - 1.004f) < 1e-4f); // it does touch the platform
    }

    [Fact]
    public void EachSpanOfACableTreeCanHaveItsOwnSlack()
    {
        // A─B lies along a truss (straight), B─C hangs free (sags)
        var a = new CableNode { Position = new Vector3(0, 0, 6) };
        var b = new CableNode { Position = new Vector3(4, 0, 6) };
        var c = new CableNode { Position = new Vector3(8, 0, 6) };
        var cable = new CableComponent { Nodes = { a, b, c }, Segments = { new(a.Id, b.Id), new(b.Id, c.Id) }, SlackFactor = 1.1f };
        var options = new CableTubeOptions
        {
            Radius = 0.004f,
            RadialSegments = 6,
            SlackFactor = 1.1f,
            MaxSampleSpacing = 0.1f,
            SpanSlack = (x, y) => (x == a && y == b) || (x == b && y == a) ? 1f : 1.1f,
        };
        var mesh = CableTubeMesh.Create(cable, options: options);
        mesh.Validate();
        var straightPart = mesh.Positions.Where(p => p.X < 3.9f).ToArray();
        var hangingPart = mesh.Positions.Where(p => p.X > 4.1f).ToArray();
        Assert.All(straightPart, p => Assert.InRange(p.Z, 6f - 0.0041f, 6f + 0.0041f));
        Assert.True(hangingPart.Min(p => p.Z) < 6f - 0.5f);

        // per-span slack values: missing or invalid ones draw the span straight
        var path = CableTubeMesh.SamplePath(new[] { Vector3.Zero, new Vector3(4, 0, 0), new Vector3(8, 0, 0) }, new[] { float.NaN }, 0.1f);
        Assert.Equal(3, path.Length);
    }

    [Fact]
    public void DegenerateCableInputYieldsAnEmptyMesh()
    {
        Assert.Equal(0, CableTubeMesh.Create(Array.Empty<Vector3>()).VertexCount);
        Assert.Equal(0, CableTubeMesh.Create(new[] { Vector3.One, Vector3.One }).VertexCount);
        var withNaN = CableTubeMesh.Create(new[] { Vector3.Zero, new Vector3(float.NaN, 0, 0), Vector3.UnitX });
        withNaN.Validate();
        Assert.Equal(2 * 9 + 2 * 9, withNaN.VertexCount);
    }

    [Fact]
    public void PrimitivesHaveTheDocumentedSizesAndVolumes()
    {
        var box = Primitives.Box(new Vector3(1f, 2f, 3f));
        var cylinder = Primitives.Cylinder(0.5f, 2f, 64);
        var cone = Primitives.Cone(0.5f, 1f, 64);
        var sphere = Primitives.Sphere(1f, 64, 32);
        var capsule = Primitives.Capsule(0.25f, 2f, 48, 16);
        var arrow = Primitives.Arrow(1f, 0.02f, 0.2f, 0.06f);

        foreach (var m in new[] { box, cylinder, cone, sphere, capsule, arrow }) AssertWellFormed(m);

        Geo.AssertSize(new Vector3(1f, 2f, 3f), box.ComputeBounds().Size);
        Assert.Equal(6.0, Volume(box), 4);
        Geo.AssertSize(new Vector3(1f, 1f, 2f), cylinder.ComputeBounds().Size);
        Assert.Equal(Math.PI * 0.25 * 2, Volume(cylinder), 2);
        Geo.AssertSize(new Vector3(1f, 1f, 1f), cone.ComputeBounds().Size);
        Assert.Equal(Math.PI * 0.25 / 3, Volume(cone), 2);
        Geo.AssertSize(new Vector3(2f, 2f, 2f), sphere.ComputeBounds().Size);
        Assert.Equal(4.0 / 3 * Math.PI, Volume(sphere), 1);
        Geo.AssertSize(new Vector3(0.5f, 0.5f, 2f), capsule.ComputeBounds().Size);
        Assert.Equal(Math.PI * 0.0625 * 1.5 + 4.0 / 3 * Math.PI * 0.015625, Volume(capsule), 2);
        var ab = arrow.ComputeBounds();
        Geo.AssertNear(new Vector3(-0.06f, -0.06f, 0f), ab.Min);
        Geo.AssertNear(new Vector3(0.06f, 0.06f, 1f), ab.Max);
        Assert.True(Volume(arrow) > 0);
    }

    [Fact]
    public void PrimitivesClampInvalidInput()
    {
        var box = Primitives.Box(new Vector3(float.NaN, -1f, 0f));
        var sphere = Primitives.Sphere(-2f, 1, 0);

        box.Validate();
        sphere.Validate();
        Assert.True(box.ComputeBounds().Size.X <= WallMesh.MinimumSize * 1.001f);
        Assert.True(sphere.TriangleCount > 0);
    }
}
