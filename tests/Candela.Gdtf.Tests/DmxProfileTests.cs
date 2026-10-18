namespace Candela.Gdtf.Tests;

/// <summary>
/// DMX profiles follow the spec formula <c>Output(x) = CFC3·(x − P)³ + CFC2·(x − P)² + CFC1·(x − P) + CFC0</c> with the
/// point at or below x. The curve used to rescale each segment to 0 … 1, which changes every multi-segment profile,
/// returned CFC0 of the first point below it instead of 0, and switched GDTF Builder's "Linear" profile off at 100 %.
/// </summary>
public sealed class DmxProfileTests
{
    private static DmxProfile Profile(params DmxProfilePoint[] points)
    {
        var p = new DmxProfile { Name = "P" };
        p.Points.AddRange(points);
        return p;
    }

    [Fact]
    public void Segments_use_the_distance_from_their_point_without_rescaling()
    {
        // f(x) = x up to 75 %, then g(x) = 0.75 + 2·(x − 0.75).
        var p = Profile(new DmxProfilePoint(0, 0, 1, 0, 0), new DmxProfilePoint(75, 0.75f, 2, 0, 0));
        Assert.Equal(0.5f, p.Evaluate(50), 5);
        Assert.Equal(0.75f, p.Evaluate(75), 5);
        Assert.Equal(1.0f, p.Evaluate(87.5f), 5);
    }

    [Fact]
    public void Below_the_first_point_the_output_is_zero() =>
        Assert.Equal(0f, Profile(new DmxProfilePoint(20, 0.5f, 0, 0, 0)).Evaluate(10));

    [Fact]
    public void The_linear_profile_of_the_library_is_the_identity_up_to_full()
    {
        var ft = GdtfReader.ReadFile(TestData.Fixture("MARK@MOVILED_BEAM_150@"));
        var linear = ft.PhysicalDescriptions.DmxProfiles.Single(p => p.Name == "Linear");
        Assert.Equal(2, linear.Points.Count); // 0 % and a closing point at 100 % with CFC0 = 0
        foreach (float x in new[] { 0f, 25f, 50f, 99f, 100f }) Assert.Equal(x / 100f, linear.Evaluate(x), 5);
    }
}
