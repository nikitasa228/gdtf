using System.Numerics;

namespace Candela.Gdtf.Tests;

/// <summary>
/// GDTF 1.1 moved the origin of the Base, Scanner and Conventional meshes to the mounting plate ("The origin of the
/// meshes is now the mounting plate", spec revision notes); the 1.1 primitive types hang below their origin. They were
/// centred like the 1.0 types, so the Beam of a Conventional1_1 body (placed at Z ≈ −Height by the files) floated
/// half a body length below the drawn fixture.
/// </summary>
public sealed class PrimitiveOriginTests
{
    [Theory]
    [InlineData(GdtfPrimitiveType.Base1_1, true)]
    [InlineData(GdtfPrimitiveType.Scanner1_1, true)]
    [InlineData(GdtfPrimitiveType.Conventional1_1, true)]
    [InlineData(GdtfPrimitiveType.Base, false)]
    [InlineData(GdtfPrimitiveType.Scanner, false)]
    [InlineData(GdtfPrimitiveType.Conventional, false)]
    [InlineData(GdtfPrimitiveType.Yoke, false)]
    [InlineData(GdtfPrimitiveType.Head, false)]
    [InlineData(GdtfPrimitiveType.Cube, false)]
    public void Primitives_of_gdtf_1_1_hang_from_their_mounting_plate(GdtfPrimitiveType type, bool hangs)
    {
        var size = new Vector3(0.3f, 0.2f, 0.5f);
        var bounds = PrimitiveMeshes.Create(type, size)!.ComputeBounds();
        Assert.Equal(hangs, PrimitiveMeshes.HangsFromOrigin(type));
        Assert.Equal(hangs ? 0f : 0.25f, bounds.Max.Z, 1e-5f);
        Assert.Equal(hangs ? -0.5f : -0.25f, bounds.Min.Z, 1e-5f);
        Assert.Equal(0f, bounds.Center.X, 1e-5f);
        Assert.Equal(0f, bounds.Center.Y, 1e-5f);
    }

    /// <summary>
    /// Real files without model files: the Beam of a Conventional1_1 body sits at its bottom, the Beam of a 1.0
    /// Conventional body at its centre, and a Base1_1 base ends where its (centred) Yoke primitive begins.
    /// </summary>
    [Theory]
    [InlineData("BOS_Lighting@P-7_HEX@", "Body", "Beam")]
    [InlineData("Briteq@BT-THEATRE_250EZ_Mk2@", "Body", "Beam")]
    [InlineData("Briteq@BT-THEATRE_400TW@", "Body", "Beam")]
    [InlineData("ACME@ACME_BW-545@", "Body", "Beam1")]
    public void Beams_of_primitive_bodies_lie_inside_the_body(string fixture, string body, string beam)
    {
        using var archive = GdtfArchive.Open(TestData.Fixture(fixture));
        var ft = GdtfReader.Read(archive);
        var geometry = new FixtureGeometryBuilder(ft, new GdtfModelLoader(archive)).Build();
        var world = new Matrix4x4[geometry.Parts.Count];
        geometry.ComputeWorldTransforms(world, Matrix4x4.Identity);
        int bodyIndex = geometry.Parts.Single(p => p.Name == body).Index;
        int beamIndex = geometry.Parts.Single(p => p.Name == beam).Index;
        var part = geometry.Parts[bodyIndex];
        Assert.Equal(ModelSourceKind.Primitive, part.LoadedModel!.Info.Source);
        var bounds = part.Model!.ComputeBounds().Transform(world[bodyIndex]);
        float z = world[beamIndex].Translation.Z;
        Assert.InRange(z, bounds.Min.Z - 1e-3f, bounds.Max.Z + 1e-3f);
    }

    [Theory]
    [InlineData("ACME@ACME_(FACE_600)@")]
    [InlineData("BeamZ_Pro@Ignite_300_BSW@")]
    public void A_base_1_1_ends_where_its_yoke_begins(string fixture)
    {
        using var archive = GdtfArchive.Open(TestData.Fixture(fixture));
        var ft = GdtfReader.Read(archive);
        var geometry = new FixtureGeometryBuilder(ft, new GdtfModelLoader(archive)).Build();
        var world = new Matrix4x4[geometry.Parts.Count];
        geometry.ComputeWorldTransforms(world, Matrix4x4.Identity);
        var baseBounds = Bounds(geometry, world, "Base");
        var yokeBounds = Bounds(geometry, world, "Yoke");
        Assert.Equal(0f, baseBounds.Max.Z, 1e-4f); // hangs from the mounting plate
        Assert.InRange(yokeBounds.Max.Z - baseBounds.Min.Z, -0.02f, 0.02f);
    }

    private static Candela.Core.Math.Aabb Bounds(FixtureGeometry geometry, Matrix4x4[] world, string name)
    {
        var part = geometry.Parts.Single(p => p.Name == name);
        Assert.Equal(ModelSourceKind.Primitive, part.LoadedModel!.Info.Source);
        return part.Model!.ComputeBounds().Transform(world[part.Index]);
    }
}
