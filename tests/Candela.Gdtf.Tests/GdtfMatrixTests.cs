using System.Numerics;

namespace Candela.Gdtf.Tests;

public sealed class GdtfMatrixTests
{
    private const float Eps = 1e-5f;

    [Fact]
    public void Translation_in_the_fourth_column_becomes_the_System_Numerics_translation_row()
    {
        Assert.True(GdtfMatrix.TryParse("{1,0,0,0.1}{0,1,0,0.2}{0,0,1,-0.3}{0,0,0,1}", out var m));
        Assert.Equal(new Vector3(0.1f, 0.2f, -0.3f), m.Translation);
        Assert.Equal(new Vector3(1.1f, 0.2f, -0.3f), Vector3.Transform(Vector3.UnitX, m));
    }

    [Fact]
    public void Rotation_is_transposed_so_points_rotate_the_same_way_as_with_column_vectors()
    {
        // Column-vector matrix of +90° about Z (x → y): rows {0,-1,0}{1,0,0}{0,0,1}.
        Assert.True(GdtfMatrix.TryParse("{0,-1,0,0}{1,0,0,0}{0,0,1,0}{0,0,0,1}", out var m));
        var p = Vector3.Transform(Vector3.UnitX, m);
        Assert.Equal(0f, p.X, Eps);
        Assert.Equal(1f, p.Y, Eps);
        var expected = Matrix4x4.CreateRotationZ(MathF.PI / 2);
        AssertClose(expected, m);
    }

    [Fact]
    public void Composition_reads_child_times_parent()
    {
        Assert.True(GdtfMatrix.TryParse("{0,-1,0,0}{1,0,0,0}{0,0,1,0}{0,0,0,1}", out var parent)); // rotate +90° about Z
        Assert.True(GdtfMatrix.TryParse("{1,0,0,1}{0,1,0,0}{0,0,1,0}{0,0,0,1}", out var child)); // 1 m along the parent's X
        var world = child * parent;
        var origin = Vector3.Transform(Vector3.Zero, world);
        Assert.Equal(0f, origin.X, Eps);
        Assert.Equal(1f, origin.Y, Eps);
    }

    [Fact]
    public void Accepts_three_row_and_MVR_style_forms_and_round_trips_through_text()
    {
        Assert.True(GdtfMatrix.TryParse("{1,0,0,2}{0,1,0,3}{0,0,1,4}", out var three));
        Assert.Equal(new Vector3(2, 3, 4), three.Translation);
        Assert.True(GdtfMatrix.TryParse("{1,0,0}{0,1,0}{0,0,1}{5,6,7}", out var mvr));
        Assert.Equal(new Vector3(5, 6, 7), mvr.Translation);

        var original = Matrix4x4.CreateRotationX(0.3f) * Matrix4x4.CreateTranslation(0.5f, -0.25f, 1f);
        Assert.True(GdtfMatrix.TryParse(GdtfMatrix.Format(original), out var back));
        AssertClose(original, back);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{1,0,0}{0,1,0}")]
    [InlineData("{1,0,0,0}{0,1,0,0}{0,0,1,0}{0,0,0,1}{0,0,0,1}")]
    [InlineData("{1,0,0,a}{0,1,0,0}{0,0,1,0}{0,0,0,1}")]
    [InlineData("1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1")]
    [InlineData("{NaN,0,0,0}{0,1,0,0}{0,0,1,0}{0,0,0,1}")]
    public void Rejects_malformed_matrices(string text) => Assert.False(GdtfMatrix.TryParse(text, out _));

    [Fact]
    public void Malformed_position_in_a_file_becomes_identity_with_a_warning()
    {
        var ft = TestData.ReadXml("""
            <GDTF DataVersion="1.2"><FixtureType Name="T" Manufacturer="M">
              <Geometries><Geometry Name="Body" Position="{1,0,0}{oops}"/></Geometries>
            </FixtureType></GDTF>
            """);
        Assert.Equal(Matrix4x4.Identity, ft.Geometries[0].Position);
        Assert.Contains(ft.Warnings, w => w.Message.Contains("not a GDTF matrix", StringComparison.Ordinal));
    }

    /// <summary>
    /// GDTF draws fixtures hanging: in the Robe MegaPointe the yoke hangs below the base and the head below the yoke,
    /// with the beam at the lowest point pointing along −Z. A wrong (untransposed) conversion would lose the offsets.
    /// </summary>
    [Fact]
    public void Real_moving_head_parts_are_stacked_along_Z()
    {
        var ft = GdtfReader.ReadFile(TestData.Fixture("Robe_Lighting@Robin_MegaPointe@"));
        var baseGeometry = ft.FindGeometry("Base")!;
        var yoke = ft.FindGeometry("Yoke")!;
        var head = ft.FindGeometry("Head")!;
        var beam = ft.AllGeometries().OfType<BeamGeometry>().First();
        Assert.IsType<AxisGeometry>(yoke);
        Assert.IsType<AxisGeometry>(head);
        Assert.Same(baseGeometry, yoke.Parent);
        Assert.Same(yoke, head.Parent);

        var yokeZ = yoke.TransformToTopLevel().Translation;
        var headZ = head.TransformToTopLevel().Translation;
        var beamZ = beam.TransformToTopLevel().Translation;
        Assert.Equal(-0.087f, yokeZ.Z, 3);
        Assert.Equal(0f, yokeZ.X, 4);
        Assert.Equal(0f, yokeZ.Y, 4);
        Assert.True(headZ.Z < yokeZ.Z - 0.2f, $"head {headZ} should hang well below the yoke {yokeZ}");
        Assert.True(beamZ.Z < headZ.Z, $"beam {beamZ} should be below the tilt axis {headZ}");
        Assert.Equal(0f, headZ.X, 3);
        Assert.Equal(0f, headZ.Y, 3);
    }

    private static void AssertClose(Matrix4x4 expected, Matrix4x4 actual)
    {
        for (int r = 0; r < 4; r++)
            for (int c = 0; c < 4; c++)
                Assert.Equal(expected[r, c], actual[r, c], 5);
    }
}
