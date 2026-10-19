using System.Numerics;

namespace Candela.Rendering.Vulkan;

/// <summary>
/// The matrices of a <see cref="RenderCamera"/> as the renderer uses them (System.Numerics row-vector convention):
/// sanitised near/far planes and field of view, reverse-Z projection (infinite far plane for perspective), inverses,
/// eye position and view directions. Shared by the real-time views and the path tracer, so both see the same picture.
/// </summary>
internal readonly struct CameraFrame
{
    public Matrix4x4 View { get; init; }

    public Matrix4x4 Projection { get; init; }

    public Matrix4x4 ViewProjection { get; init; }

    public Matrix4x4 InverseView { get; init; }

    public Matrix4x4 InverseProjection { get; init; }

    public Matrix4x4 InverseViewProjection { get; init; }

    /// <summary>Camera position, m.</summary>
    public Vector3 Eye { get; init; }

    /// <summary>World direction of the image's up.</summary>
    public Vector3 Up { get; init; }

    /// <summary>World view direction.</summary>
    public Vector3 Forward { get; init; }

    /// <summary>Near plane (view depth, m).</summary>
    public float Near { get; init; }

    /// <summary>Far plane (view depth, m; the projection ignores it for perspective).</summary>
    public float Far { get; init; }

    public bool Orthographic { get; init; }

    /// <summary>Visible height of an orthographic camera, m.</summary>
    public float OrthoHeight { get; init; }

    /// <summary>The camera's matrices for an image of the given aspect ratio (width / height).</summary>
    public static CameraFrame Compute(RenderCamera camera, float aspect)
    {
        ArgumentNullException.ThrowIfNull(camera);
        if (!float.IsFinite(aspect) || aspect <= 0f)
            aspect = 1f;
        bool ortho = camera.Projection == ProjectionKind.Orthographic;
        float near = float.IsFinite(camera.Near) && camera.Near > 1e-4f ? camera.Near : 1e-4f;
        float far = float.IsFinite(camera.Far) && camera.Far > near * 1.001f ? camera.Far : near * 1000f;
        var viewMatrix = camera.View;
        if (!Matrix4x4.Invert(viewMatrix, out var inverseView))
        {
            viewMatrix = Matrix4x4.Identity;
            inverseView = Matrix4x4.Identity;
        }

        Matrix4x4 projection;
        float orthoHeight = float.IsFinite(camera.OrthoHeight) && camera.OrthoHeight > 1e-4f ? camera.OrthoHeight : 10f;
        if (ortho)
        {
            projection = ReverseZ.Orthographic(orthoHeight, aspect, near, far);
        }
        else
        {
            float fov = float.IsFinite(camera.FovY) ? Math.Clamp(camera.FovY, 0.01f, 3.1f) : 50f * MathF.PI / 180f;
            projection = ReverseZ.Perspective(fov, aspect, near);
        }

        var viewProjection = viewMatrix * projection;
        Matrix4x4.Invert(projection, out var inverseProjection);
        Matrix4x4.Invert(viewProjection, out var inverseViewProjection);
        return new CameraFrame
        {
            View = viewMatrix,
            Projection = projection,
            ViewProjection = viewProjection,
            InverseView = inverseView,
            InverseProjection = inverseProjection,
            InverseViewProjection = inverseViewProjection,
            Eye = inverseView.Translation,
            Up = Vector3.Normalize(new Vector3(inverseView.M21, inverseView.M22, inverseView.M23)),
            Forward = -Vector3.Normalize(new Vector3(inverseView.M31, inverseView.M32, inverseView.M33)),
            Near = near,
            Far = far,
            Orthographic = ortho,
            OrthoHeight = orthoHeight,
        };
    }
}
