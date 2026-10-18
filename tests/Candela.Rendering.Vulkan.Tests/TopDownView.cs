using System.Numerics;
using Candela.Core.Geometry;

namespace Candela.Rendering.Vulkan.Tests;

/// <summary>
/// An orthographic camera looking straight down at the floor (Z = 0) for the photometric tests, with helpers that map
/// floor points to pixels and back exactly as the renderer projects them.
/// </summary>
internal sealed class TopDownView
{
    public TopDownView(int width, int height, float orthoHeight, float ev100 = 7f)
    {
        Width = width;
        Height = height;
        OrthoHeight = orthoHeight;
        Camera = RenderCamera.LookAt(new Vector3(0f, 0f, 10f), Vector3.Zero);
        Camera.Projection = ProjectionKind.Orthographic;
        Camera.OrthoHeight = orthoHeight;
        Camera.Near = 0.1f;
        Camera.Far = 100f;
        Camera.Ev100 = ev100;
        ViewProjection = Camera.View * ReverseZ.Orthographic(orthoHeight, width / (float)height, Camera.Near, Camera.Far);
        Matrix4x4.Invert(ViewProjection, out var inverse);
        InverseViewProjection = inverse;
    }

    public int Width { get; }

    public int Height { get; }

    public float OrthoHeight { get; }

    public RenderCamera Camera { get; }

    public Matrix4x4 ViewProjection { get; }

    public Matrix4x4 InverseViewProjection { get; }

    /// <summary>A white Lambertian material: albedo 1, roughness 1, IOR 1 (F0 = 0, no specular).</summary>
    public static MaterialData WhiteLambertian() => new()
    {
        Name = "white lambertian",
        BaseColorFactor = Vector4.One,
        Metallic = 0f,
        Roughness = 1f,
        Ior = 1f,
    };

    /// <summary>The pixel whose centre is closest to a point of the floor, and that pixel centre on the floor.</summary>
    public (int X, int Y, Vector3 Point) NearestPixel(Vector3 point)
    {
        var clip = Vector4.Transform(new Vector4(point, 1f), ViewProjection);
        float ndcX = clip.X / clip.W, ndcY = clip.Y / clip.W;
        int x = Math.Clamp((int)MathF.Floor((ndcX * 0.5f + 0.5f) * Width), 0, Width - 1);
        int y = Math.Clamp((int)MathF.Floor((ndcY * 0.5f + 0.5f) * Height), 0, Height - 1);
        return (x, y, FloorPointOf(x, y));
    }

    /// <summary>The floor point (Z = 0) seen through the centre of pixel (x, y).</summary>
    public Vector3 FloorPointOf(int x, int y)
    {
        float nx = (x + 0.5f) / Width * 2f - 1f, ny = (y + 0.5f) / Height * 2f - 1f;
        var a = Vector4.Transform(new Vector4(nx, ny, 1f, 1f), InverseViewProjection);
        var b = Vector4.Transform(new Vector4(nx, ny, 0.5f, 1f), InverseViewProjection);
        var pa = new Vector3(a.X, a.Y, a.Z) / a.W;
        var pb = new Vector3(b.X, b.Y, b.Z) / b.W;
        float t = -pa.Z / (pb.Z - pa.Z);
        return pa + (pb - pa) * t;
    }
}
