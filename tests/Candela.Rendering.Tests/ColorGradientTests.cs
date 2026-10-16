using System.Numerics;

namespace Candela.Rendering.Tests;

public class ColorGradientTests
{
    private static readonly Vector4 Red = new(1, 0, 0, 1);
    private static readonly Vector4 Blue = new(0, 0, 1, 1);

    [Fact]
    public void Values_outside_range_are_grey_and_inside_interpolate()
    {
        var g = new ColorGradient { RangeMin = 100, RangeMax = 500, Stops = { new(100, Blue), new(500, Red) } };
        Assert.Equal(g.OutOfRangeColor, g.Evaluate(50));
        Assert.Equal(g.OutOfRangeColor, g.Evaluate(600));
        Assert.Equal(Blue, g.Evaluate(100));
        var mid = g.Evaluate(300);
        Assert.Equal(0.5f, mid.X, 3);
        Assert.Equal(0.5f, mid.Z, 3);
    }

    [Fact]
    public void Logarithmic_gradient_puts_decade_midpoint_in_the_middle()
    {
        var g = new ColorGradient { RangeMin = 10, RangeMax = 1000, Logarithmic = true, Stops = { new(10, Blue), new(1000, Red) } };
        Assert.Equal(0.5f, g.Evaluate(100).X, 3);
        var lut = g.Bake(3);
        Assert.Equal(Blue, lut[0]);
        Assert.Equal(0.5f, lut[1].X, 3);
        Assert.Equal(Red, lut[2]);
    }

    [Fact]
    public void Unsorted_stops_are_handled_and_defaults_are_valid()
    {
        var g = new ColorGradient { RangeMin = 0, RangeMax = 10, Stops = { new(10, Red), new(0, Blue) } };
        Assert.Equal(Blue, g.Evaluate(0));
        Assert.Equal(Red, g.Evaluate(10));
        Assert.Equal(5, ColorGradient.DefaultLux().Stops.Count);
        Assert.Equal(64, ColorGradient.DefaultLux().Bake(64).Length);
    }

    [Fact]
    public void Look_at_camera_position_round_trips()
    {
        var cam = RenderCamera.LookAt(new Vector3(0, -10, 5), Vector3.Zero);
        Assert.True(Vector3.Distance(cam.Position, new Vector3(0, -10, 5)) < 1e-4f);
        var top = RenderCamera.LookAt(new Vector3(0, 0, 20), Vector3.Zero);
        Assert.True(float.IsFinite(top.View.M11));
    }
}
