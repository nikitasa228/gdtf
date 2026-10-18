using System.Numerics;
using Candela.Core.Math;
using Candela.Rendering;

namespace Candela.App.Viewport;

/// <summary>Fixed camera directions of the view cube and the View menu (orthographic snaps).</summary>
internal enum ViewDirection
{
    /// <summary>Looking down −Z (plan view); screen up is +Y (into the stage).</summary>
    Top,

    /// <summary>Looking up +Z from below; screen up is −Y.</summary>
    Bottom,

    /// <summary>From the audience (−Y) towards the stage, looking along +Y.</summary>
    Front,

    /// <summary>From behind the stage (+Y) looking along −Y.</summary>
    Back,

    /// <summary>From stage left (−X) looking along +X.</summary>
    Left,

    /// <summary>From stage right (+X) looking along −X.</summary>
    Right,
}

/// <summary>
/// An editor camera: orbit around a target point (yaw about world Z, pitch above the horizon, distance), pan, zoom,
/// fly (WASD + mouse look), framing, perspective / orthographic projection and snapping to the six axis views.
/// World convention: right-handed, Z up, X right, Y into the stage; units metres, angles radians.
/// <para>The view direction is <c>D = (sin yaw·cos pitch, cos yaw·cos pitch, −sin pitch)</c>, the screen right axis
/// <c>R = (cos yaw, −sin yaw, 0)</c> and the screen up axis <c>U = R × D</c>, so yaw 0 / pitch 0 is the front view and
/// pitch 90° the top view without a singularity.</para>
/// </summary>
internal sealed class ViewportCamera
{
    /// <summary>Largest pitch magnitude (exactly straight down/up is allowed: the up axis is explicit).</summary>
    public const float MaxPitch = MathF.PI / 2f;

    /// <summary>Distance range of the orbit camera, m.</summary>
    public const float MinDistance = 0.05f, MaxDistance = 20000f;

    /// <summary>Visible height range of the orthographic camera, m.</summary>
    public const float MinOrthoHeight = 0.02f, MaxOrthoHeight = 40000f;

    /// <summary>Distance of the eye behind the target in orthographic views (the depth range covers the scene both ways).</summary>
    public const float OrthoEyeDistance = 2000f;

    private Vector3 _target = new(0f, 1.5f, 1.0f);
    private float _yaw;
    // Defaults frame the stage like the render-test camera (eye (0, −9, 3.8) looking at (0, 1.5, 1)).
    private float _pitch = 0.26f;
    private float _distance = 10.9f;
    private float _orthoHeight = 10f;
    private float _fovY = 50f * MathF.PI / 180f;

    /// <summary>Point the camera orbits around, m.</summary>
    public Vector3 Target
    {
        get => _target;
        set => _target = Finite(value) ? value : _target;
    }

    /// <summary>Rotation about world Z, radians (0 = looking along +Y).</summary>
    public float Yaw
    {
        get => _yaw;
        set => _yaw = float.IsFinite(value) ? WrapAngle(value) : _yaw;
    }

    /// <summary>Angle below the horizon, radians (positive = looking down), clamped to ±90°.</summary>
    public float Pitch
    {
        get => _pitch;
        set => _pitch = float.IsFinite(value) ? Math.Clamp(value, -MaxPitch, MaxPitch) : _pitch;
    }

    /// <summary>Distance from the eye to the target in perspective, m.</summary>
    public float Distance
    {
        get => _distance;
        set => _distance = float.IsFinite(value) ? Math.Clamp(value, MinDistance, MaxDistance) : _distance;
    }

    /// <summary>Visible height of the orthographic view, m.</summary>
    public float OrthoHeight
    {
        get => _orthoHeight;
        set => _orthoHeight = float.IsFinite(value) ? Math.Clamp(value, MinOrthoHeight, MaxOrthoHeight) : _orthoHeight;
    }

    /// <summary>Vertical field of view of the perspective projection, radians (10°–120°).</summary>
    public float FovY
    {
        get => _fovY;
        set => _fovY = float.IsFinite(value) ? Math.Clamp(value, 10f * MathF.PI / 180f, 120f * MathF.PI / 180f) : _fovY;
    }

    /// <summary>Perspective or orthographic.</summary>
    public ProjectionKind Projection { get; set; } = ProjectionKind.Perspective;

    /// <summary>The axis view the camera was snapped to, or null after free rotation.</summary>
    public ViewDirection? SnappedView { get; private set; }

    /// <summary>Exposure at ISO 100 of the physical camera.</summary>
    public float Ev100 { get; set; } = 8f;

    /// <summary>Unit vector from the eye towards the target.</summary>
    public Vector3 Forward => DirectionOf(_yaw, _pitch);

    /// <summary>Screen right axis in world space.</summary>
    public Vector3 Right => new(MathF.Cos(_yaw), -MathF.Sin(_yaw), 0f);

    /// <summary>Screen up axis in world space.</summary>
    public Vector3 Up => Vector3.Normalize(Vector3.Cross(Right, Forward));

    /// <summary>Eye position, m (orthographic views place it far behind the target).</summary>
    public Vector3 Eye => _target - Forward * (Projection == ProjectionKind.Orthographic ? OrthoEyeDistance : _distance);

    /// <summary>World → view matrix (looking down −Z, +Y up; System.Numerics row-vector convention).</summary>
    public Matrix4x4 ViewMatrix => Matrix4x4.CreateLookAt(Eye, _target, Up);

    /// <summary>Near plane distance, m (scaled with the orbit distance so close-ups keep precision).</summary>
    public float Near => Projection == ProjectionKind.Orthographic ? 0.1f : Math.Clamp(_distance * 0.002f, 0.01f, 0.5f);

    /// <summary>Far plane distance, m.</summary>
    public float Far => Projection == ProjectionKind.Orthographic ? OrthoEyeDistance * 2f + 2000f : 5000f;

    /// <summary>Direction from the eye for given yaw/pitch.</summary>
    public static Vector3 DirectionOf(float yaw, float pitch)
    {
        float cp = MathF.Cos(pitch);
        return new Vector3(MathF.Sin(yaw) * cp, MathF.Cos(yaw) * cp, -MathF.Sin(pitch));
    }

    /// <summary>Yaw and pitch of an axis view.</summary>
    public static (float Yaw, float Pitch) AnglesOf(ViewDirection view) => view switch
    {
        ViewDirection.Top => (0f, MaxPitch),
        ViewDirection.Bottom => (0f, -MaxPitch),
        ViewDirection.Front => (0f, 0f),
        ViewDirection.Back => (MathF.PI, 0f),
        ViewDirection.Left => (MathF.PI / 2f, 0f),
        ViewDirection.Right => (-MathF.PI / 2f, 0f),
        _ => (0f, 0f),
    };

    /// <summary>The camera for the renderer (projection and exposure included).</summary>
    public RenderCamera ToRenderCamera()
    {
        var camera = new RenderCamera();
        UpdateRenderCamera(camera);
        return camera;
    }

    /// <summary>Writes this camera into an existing <see cref="RenderCamera"/> (no allocation per frame).</summary>
    public void UpdateRenderCamera(RenderCamera camera)
    {
        ArgumentNullException.ThrowIfNull(camera);
        camera.View = ViewMatrix;
        camera.Projection = Projection;
        camera.FovY = _fovY;
        camera.OrthoHeight = _orthoHeight;
        camera.Near = Near;
        camera.Far = Far;
        camera.Ev100 = Ev100;
    }

    /// <summary>
    /// A conventional right-handed projection (clip depth 0..1, +Y up) for screen-space math in the editor (ImGuizmo,
    /// projecting points, picking rays). The renderer builds its own reverse-Z Vulkan projection.
    /// </summary>
    public Matrix4x4 ProjectionMatrix(float aspect)
    {
        aspect = float.IsFinite(aspect) && aspect > 1e-4f ? aspect : 1f;
        return Projection == ProjectionKind.Orthographic
            ? Matrix4x4.CreateOrthographic(_orthoHeight * aspect, _orthoHeight, Near, Far)
            : Matrix4x4.CreatePerspectiveFieldOfView(_fovY, aspect, Near, Far);
    }

    /// <summary>Metres per screen pixel at the target depth (pan speed, snapping).</summary>
    public float MetersPerPixel(float viewportHeightPixels)
    {
        float h = MathF.Max(viewportHeightPixels, 1f);
        return Projection == ProjectionKind.Orthographic ? _orthoHeight / h : 2f * _distance * MathF.Tan(_fovY * 0.5f) / h;
    }

    /// <summary>Rotates around the target by pixel deltas (0.3° per pixel × speed). Leaves an axis-view snap.</summary>
    public void Orbit(float dxPixels, float dyPixels, float speed = 1f)
    {
        const float radiansPerPixel = 0.3f * MathF.PI / 180f;
        Yaw += dxPixels * radiansPerPixel * speed;
        Pitch += dyPixels * radiansPerPixel * speed;
        SnappedView = null;
    }

    /// <summary>Moves target and eye parallel to the view plane so the scene follows the mouse.</summary>
    public void Pan(float dxPixels, float dyPixels, float viewportHeightPixels)
    {
        float m = MetersPerPixel(viewportHeightPixels);
        Target += (-Right * dxPixels + Up * dyPixels) * m;
    }

    /// <summary>
    /// Zooms by wheel <paramref name="steps"/> (positive = closer), each step 12 % × speed. Perspective dollies the
    /// distance; orthographic scales the visible height. With a <paramref name="towards"/> point (the point under the
    /// cursor) the target moves towards it by the same fraction, so the point stays under the cursor.
    /// </summary>
    public void Zoom(float steps, float speed = 1f, Vector3? towards = null)
    {
        if (!float.IsFinite(steps) || steps == 0f)
            return;
        float factor = MathF.Pow(0.88f, steps * Math.Clamp(speed, 0.1f, 5f));
        if (Projection == ProjectionKind.Orthographic)
        {
            float old = _orthoHeight;
            OrthoHeight = old * factor;
            if (towards is { } p && Finite(p))
                Target += (ProjectOntoViewPlane(p) - _target) * (1f - _orthoHeight / old);
        }
        else
        {
            float old = _distance;
            Distance = old * factor;
            if (towards is { } p && Finite(p))
            {
                float k = 1f - _distance / old;
                Target += (p - _target) * k;
            }
        }
    }

    /// <summary>Changes the orbit distance by a vertical drag (zoom drag): down = away.</summary>
    public void ZoomDrag(float dyPixels, float speed = 1f) => Zoom(-dyPixels / 40f, speed);

    /// <summary>Mouse look from the eye position (fly mode): the eye stays, the target moves.</summary>
    public void Look(float dxPixels, float dyPixels, float speed = 1f)
    {
        var eye = Eye;
        if (Projection == ProjectionKind.Orthographic)
        {
            Projection = ProjectionKind.Perspective;
            eye = _target - Forward * _distance;
        }

        const float radiansPerPixel = 0.2f * MathF.PI / 180f;
        Yaw += dxPixels * radiansPerPixel * speed;
        Pitch += dyPixels * radiansPerPixel * speed;
        _target = eye + Forward * _distance;
        SnappedView = null;
    }

    /// <summary>
    /// Moves eye and target in fly mode: <paramref name="move"/> = (right, forward, up) in −1…1, metres per second scaled
    /// with the orbit distance.
    /// </summary>
    public void Fly(Vector3 move, float deltaSeconds, float speed = 1f)
    {
        if (move == Vector3.Zero || !float.IsFinite(deltaSeconds) || deltaSeconds <= 0f)
            return;
        float metersPerSecond = Math.Clamp(_distance * 0.8f, 1f, 200f) * Math.Clamp(speed, 0.1f, 10f);
        var forward = Forward;
        var delta = (Right * move.X + forward * move.Y + Vector3.UnitZ * move.Z) * metersPerSecond * deltaSeconds;
        Target += delta;
        SnappedView = null;
    }

    /// <summary>Frames a box (frame selection, F): centres it and sets the distance / height so it fills ~85 % of the view.</summary>
    public void Frame(Aabb bounds, float aspect)
    {
        if (bounds.IsEmpty || !Finite(bounds.Min) || !Finite(bounds.Max))
            return;
        aspect = float.IsFinite(aspect) && aspect > 1e-3f ? aspect : 1f;
        float radius = MathF.Max(bounds.Size.Length() * 0.5f, 0.05f);
        Target = bounds.Center;
        float halfFov = _fovY * 0.5f;
        float halfFovX = MathF.Atan(MathF.Tan(halfFov) * aspect);
        float limiting = MathF.Min(halfFov, halfFovX);
        Distance = radius / MathF.Sin(limiting) / 0.85f;
        OrthoHeight = 2f * radius / MathF.Min(1f, aspect) / 0.85f;
    }

    /// <summary>Moves the target to a point (focus on double-click) keeping the direction and distance.</summary>
    public void FocusOn(Vector3 point)
    {
        if (Finite(point))
            Target = point;
    }

    /// <summary>Snaps to an axis view with an orthographic projection keeping the apparent size.</summary>
    public void SnapTo(ViewDirection view)
    {
        if (Projection == ProjectionKind.Perspective)
            OrthoHeight = 2f * _distance * MathF.Tan(_fovY * 0.5f);
        (Yaw, Pitch) = AnglesOf(view);
        Projection = ProjectionKind.Orthographic;
        SnappedView = view;
    }

    /// <summary>Switches between perspective and orthographic keeping the apparent size at the target.</summary>
    public void ToggleProjection()
    {
        if (Projection == ProjectionKind.Perspective)
        {
            OrthoHeight = 2f * _distance * MathF.Tan(_fovY * 0.5f);
            Projection = ProjectionKind.Orthographic;
        }
        else
        {
            Distance = _orthoHeight / (2f * MathF.Tan(_fovY * 0.5f));
            Projection = ProjectionKind.Perspective;
            SnappedView = null;
        }
    }

    /// <summary>
    /// The world-space ray through a viewport pixel (<paramref name="pixel"/> from the top-left of a viewport of
    /// <paramref name="size"/> pixels).
    /// </summary>
    public Ray RayThrough(Vector2 pixel, Vector2 size)
    {
        size = Vector2.Max(size, Vector2.One);
        float nx = pixel.X / size.X * 2f - 1f;
        float ny = 1f - pixel.Y / size.Y * 2f;
        float aspect = size.X / size.Y;
        var forward = Forward;
        if (Projection == ProjectionKind.Orthographic)
        {
            var origin = Eye + Right * (nx * _orthoHeight * 0.5f * aspect) + Up * (ny * _orthoHeight * 0.5f);
            return new Ray(origin, forward);
        }

        float t = MathF.Tan(_fovY * 0.5f);
        var dir = Vector3.Normalize(forward + Right * (nx * t * aspect) + Up * (ny * t));
        return new Ray(Eye, dir);
    }

    /// <summary>
    /// Projects a world point to viewport pixels (top-left origin). Returns false when the point is behind the camera.
    /// </summary>
    public bool WorldToScreen(Vector3 world, Vector2 size, out Vector2 pixel)
    {
        size = Vector2.Max(size, Vector2.One);
        var clip = Vector4.Transform(new Vector4(world, 1f), ViewMatrix * ProjectionMatrix(size.X / size.Y));
        pixel = default;
        if (clip.W <= 1e-6f)
            return false;
        float nx = clip.X / clip.W, ny = clip.Y / clip.W;
        pixel = new Vector2((nx * 0.5f + 0.5f) * size.X, (0.5f - ny * 0.5f) * size.Y);
        return float.IsFinite(pixel.X) && float.IsFinite(pixel.Y);
    }

    /// <summary>Intersection of a ray with the ground plane Z = <paramref name="height"/> (null when parallel or behind).</summary>
    public static Vector3? IntersectGround(Ray ray, float height = 0f)
    {
        if (MathF.Abs(ray.Direction.Z) < 1e-6f)
            return null;
        float t = (height - ray.Origin.Z) / ray.Direction.Z;
        return t > 0f ? ray.At(t) : null;
    }

    /// <summary>Copies all state from another camera.</summary>
    public void CopyFrom(ViewportCamera other)
    {
        ArgumentNullException.ThrowIfNull(other);
        _target = other._target;
        _yaw = other._yaw;
        _pitch = other._pitch;
        _distance = other._distance;
        _orthoHeight = other._orthoHeight;
        _fovY = other._fovY;
        Projection = other.Projection;
        SnappedView = other.SnappedView;
        Ev100 = other.Ev100;
    }

    private Vector3 ProjectOntoViewPlane(Vector3 p)
    {
        var f = Forward;
        return p - f * Vector3.Dot(p - _target, f);
    }

    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    private static float WrapAngle(float a)
    {
        a %= 2f * MathF.PI;
        if (a > MathF.PI)
            a -= 2f * MathF.PI;
        else if (a <= -MathF.PI)
            a += 2f * MathF.PI;
        return a;
    }
}
