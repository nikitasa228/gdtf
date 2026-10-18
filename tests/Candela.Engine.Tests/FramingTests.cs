using System.Numerics;
using Candela.Core.Math;
using Candela.Rendering;

namespace Candela.Engine.Tests;

/// <summary>Camera framing of the CLI presets: every point inside the picture, the content filling it.</summary>
public sealed class FramingTests
{
    /// <summary>Normalised image coordinates (−1 … 1) of a world point, and its depth in front of the camera.</summary>
    private static (float X, float Y, float Depth) Project(RenderCamera camera, float aspect, Vector3 p)
    {
        var v = Vector3.Transform(p, camera.View); // camera looks along −Z in view space
        float depth = -v.Z;
        float t = MathF.Tan(camera.FovY / 2f);
        return (v.X / (depth * t * aspect), v.Y / (depth * t), depth);
    }

    public static TheoryData<int, float, float> Cases => new()
    {
        { (int)ViewPreset.Auto, 45f, 16f / 9f },
        { (int)ViewPreset.Front, 45f, 16f / 9f },
        { (int)ViewPreset.Top, 30f, 1f },
        { (int)ViewPreset.Side, 60f, 0.5f },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Points_are_framed_tightly_inside_the_image(int preset, float fov, float aspect)
    {
        var random = new Random(7 + preset);
        var points = new List<Vector3>();
        for (int i = 0; i < 300; i++)
            points.Add(new Vector3(3f + 20f * random.NextSingle(), -4f + 9f * random.NextSingle(), 7f * random.NextSingle() * random.NextSingle()));
        points.Add(new Vector3(float.NaN, 0, 0)); // ignored
        const float margin = 1.1f;
        var camera = ViewFraming.Frame(points.ToArray(), ViewFraming.Direction((ViewPreset)preset), fov, aspect, margin);

        float maxX = 0, maxY = 0;
        foreach (var p in points.Where(p => float.IsFinite(p.X)))
        {
            var (x, y, depth) = Project(camera, aspect, p);
            Assert.True(depth > camera.Near, $"{p} is behind the near plane");
            Assert.InRange(MathF.Abs(x), 0f, 1f / margin + 1e-3f);
            Assert.InRange(MathF.Abs(y), 0f, 1f / margin + 1e-3f);
            maxX = MathF.Max(maxX, MathF.Abs(x));
            maxY = MathF.Max(maxY, MathF.Abs(y));
        }
        // tight: the content touches the margin on at least one axis
        Assert.True(MathF.Max(maxX, maxY) > 1f / margin - 0.01f, $"loose framing: {maxX:0.000} × {maxY:0.000}");
    }

    [Fact]
    public void Content_framing_is_tighter_than_the_bounding_box_and_handles_degenerate_input()
    {
        // an L-shaped rig: the box contains a large empty volume
        var points = new List<Vector3>();
        for (int i = 0; i <= 20; i++)
        {
            points.Add(new Vector3(i, 0, 0));
            points.Add(new Vector3(0, i, 6));
        }
        var box = new Aabb(new Vector3(0, 0, 0), new Vector3(20, 20, 6));
        var direction = ViewFraming.Direction(ViewPreset.Auto);
        var byBox = ViewFraming.Frame(box, direction, 45f, 16f / 9f);
        var byPoints = ViewFraming.Frame(points.ToArray(), direction, 45f, 16f / 9f);
        float boxDistance = Vector3.Distance(byBox.Position, box.Center);
        float pointDistance = Vector3.Distance(byPoints.Position, box.Center);
        Assert.True(pointDistance < boxDistance, $"{pointDistance:0.0} m vs {boxDistance:0.0} m");

        // one point, no point, non-finite points: a usable camera
        foreach (var set in new[] { new[] { new Vector3(1, 2, 3) }, Array.Empty<Vector3>(), new[] { new Vector3(float.PositiveInfinity) } })
        {
            var camera = ViewFraming.Frame(set, direction, 45f, 1.5f);
            Assert.True(float.IsFinite(camera.Position.X) && float.IsFinite(camera.Position.Z));
            Assert.True(camera.Near > 0f && camera.Far > camera.Near);
        }
    }
}
