using System.Numerics;
using System.Runtime.InteropServices;
using Candela.App.Editing;
using Candela.App.Session;
using Candela.Core.Math;
using Candela.Rendering;

namespace Candela.App.Viewport;

/// <summary>Where things land in this viewport: the surface under a screen point, and framing a newly opened scene.</summary>
internal sealed partial class ViewportPanel
{
    /// <summary>True when <paramref name="screen"/> (ImGui screen coordinates) lies on this viewport's image (last frame).</summary>
    public bool ContainsScreenPoint(Vector2 screen) =>
        _imageSize.X > 0 && _imageSize.Y > 0 &&
        screen.X >= _imageMin.X && screen.Y >= _imageMin.Y && screen.X < _imageMin.X + _imageSize.X && screen.Y < _imageMin.Y + _imageSize.Y;

    /// <summary>
    /// The surface under <paramref name="screen"/>: the geometry the renderer drew there in the last frame (pick buffer:
    /// position and normal), else the ground plane, else a point on the plane through the camera's target. Must run
    /// outside a GPU frame (the pick reads the last frame back).
    /// </summary>
    public SurfaceHit SurfaceAt(Vector2 screen, IRenderer renderer)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        if (_target is not null && ContainsScreenPoint(screen))
        {
            var (x, y) = PixelOf(screen, _imageMin, _framebufferScale, _target.Width, _target.Height);
            PickResult? hit = null;
            try
            {
                hit = renderer.Pick(_target, x, y);
            }
            catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException)
            {
                // The target was resized or released since the last frame: fall back to the ground below.
            }

            if (hit is { } h && Finite(h.WorldPosition) && Finite(h.Normal) && h.Normal.LengthSquared() > 1e-6f)
                return new SurfaceHit(h.WorldPosition, Vector3.Normalize(h.Normal), SurfaceKind.Geometry, h.PickId);
        }

        return GroundAt(screen);
    }

    /// <summary>The screen point (Dear ImGui coordinates) of a world point in this viewport, or null when it is not on the image.</summary>
    public Vector2? ScreenOf(Vector3 world)
    {
        if (_imageSize.X <= 0 || !Camera.WorldToScreen(world, _imageSize, out var s))
            return null;
        var p = s + _imageMin;
        return ContainsScreenPoint(p) ? p : null;
    }

    /// <summary>The ground point under <paramref name="screen"/> (or the view plane through the camera's target).</summary>
    public SurfaceHit GroundAt(Vector2 screen)
    {
        var ray = Camera.RayThrough(screen - _imageMin, _imageSize);
        if (Placement.GroundUnder(ray.Origin, ray.Direction) is { } ground && Vector3.Distance(ground, ray.Origin) < ViewportCamera.MaxDistance)
            return SurfaceHit.Ground(ground);
        // Looking at the horizon or above: the plane through the camera's target, facing the camera.
        var forward = Camera.Forward;
        float denom = Vector3.Dot(ray.Direction, forward);
        var point = MathF.Abs(denom) > 1e-5f ? ray.At(Vector3.Dot(Camera.Target - ray.Origin, forward) / denom) : Camera.Target;
        return new SurfaceHit(Finite(point) ? point : Camera.Target, -forward, SurfaceKind.ViewPlane);
    }

    /// <summary>
    /// Frames everything shown (after a scene was opened): orthographic axis views keep their direction, perspective
    /// views keep their angle. An empty scene puts the camera back to its start position over the stage origin.
    /// </summary>
    public void FrameScene()
    {
        _framingPoints.Clear();
        _ctx.Session.Content?.CollectFramingPoints(_framingPoints);
        float aspect = _imageSize.Y > 0 ? _imageSize.X / _imageSize.Y : 16f / 9f;
        if (!Camera.FramePoints(CollectionsMarshal.AsSpan(_framingPoints), aspect))
        {
            var home = new ViewportCamera();
            Camera.Target = home.Target;
            Camera.Distance = home.Distance;
            Camera.OrthoHeight = home.OrthoHeight;
        }

        _framedCamera = CameraState();
    }

    /// <summary>
    /// Shows a project's stored view instead of framing it (opening a project brings back the camera it was saved with).
    /// False when the stored camera is unusable; the caller frames the scene instead.
    /// </summary>
    public bool ShowSavedCamera(Candela.Engine.SavedCamera saved)
    {
        ArgumentNullException.ThrowIfNull(saved);
        if (!ViewSettingsEdits.Apply(saved, Camera))
            return false;
        _framedCamera = CameraState();
        return true;
    }

    /// <summary>True when the camera was moved since <see cref="FrameScene"/> last set it.</summary>
    public bool CameraMovedSinceFraming => _framedCamera != CameraState();

    private (Vector3, float, float, float, float, ProjectionKind) CameraState() =>
        (Camera.Target, Camera.Distance, Camera.OrthoHeight, Camera.Yaw, Camera.Pitch, Camera.Projection);

    private (Vector3, float, float, float, float, ProjectionKind)? _framedCamera;

    private readonly List<Vector3> _framingPoints = new();

    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
