using System.Numerics;

namespace Candela.Rendering.Vulkan;

/// <summary>
/// Vulkan projection matrices with reverse-Z depth (near plane → 1, far → 0, compare GREATER) in the
/// System.Numerics row-vector convention (clip = v · P). View space looks down −Z with +Y up; clip-space Y is
/// flipped for Vulkan's downward framebuffer Y, so images come out upright with a plain viewport.
/// </summary>
public static class ReverseZ
{
    /// <summary>
    /// Perspective projection with an infinite far plane: depth = near / (−z_view), exactly 1 at the near plane and
    /// approaching 0 at infinity. With a 32-bit float depth buffer the relative precision is nearly constant over
    /// distance (≈ 10⁻⁷), so no far plane is needed.
    /// </summary>
    /// <param name="fovY">Vertical field of view, rad.</param>
    /// <param name="aspect">Width / height.</param>
    /// <param name="near">Near plane distance, m (&gt; 0).</param>
    public static Matrix4x4 Perspective(float fovY, float aspect, float near)
    {
        float f = 1f / MathF.Tan(0.5f * fovY);
        return new Matrix4x4(
            f / aspect, 0f, 0f, 0f,
            0f, -f, 0f, 0f,
            0f, 0f, 0f, -1f,
            0f, 0f, near, 0f);
    }

    /// <summary>
    /// Orthographic projection: depth = (z_view + far) / (far − near), 1 at the near plane and 0 at the far plane.
    /// </summary>
    /// <param name="height">Visible height, m.</param>
    /// <param name="aspect">Width / height.</param>
    /// <param name="near">Near plane distance, m (may be negative to see behind the camera position).</param>
    /// <param name="far">Far plane distance, m (&gt; near).</param>
    public static Matrix4x4 Orthographic(float height, float aspect, float near, float far)
    {
        float width = height * aspect;
        float range = far - near;
        return new Matrix4x4(
            2f / width, 0f, 0f, 0f,
            0f, -2f / height, 0f, 0f,
            0f, 0f, 1f / range, 0f,
            0f, 0f, far / range, 1f);
    }
}
