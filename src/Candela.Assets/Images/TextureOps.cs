using System.Buffers.Binary;
using System.Numerics;
using Candela.Core.Geometry;

namespace Candela.Assets;

/// <summary>
/// Pixel operations used to bring classic material maps into glTF form (bump → normal map, separate metal/rough maps →
/// packed metallic-roughness, separate opacity map → albedo alpha) and by the procedural material textures.
/// Normal maps follow glTF: tangent space, +X right (increasing U), +Y up (decreasing V, i.e. towards the top row).
/// </summary>
internal static class TextureOps
{
    /// <summary>Linear 0 … 1 grey value of a pixel (average of RGB; float images clamped).</summary>
    public static float Gray(ImageData image, int x, int y)
    {
        int i = y * image.Width + x;
        if (image.Format == PixelFormat.Rgba8)
        {
            var p = image.Pixels;
            return (p[i * 4] + p[i * 4 + 1] + p[i * 4 + 2]) / (3f * 255f);
        }
        var s = image.Pixels.AsSpan(i * 16, 12);
        float r = BinaryPrimitives.ReadSingleLittleEndian(s), g = BinaryPrimitives.ReadSingleLittleEndian(s[4..]), b = BinaryPrimitives.ReadSingleLittleEndian(s[8..]);
        float v = (r + g + b) / 3f;
        return float.IsFinite(v) ? System.Math.Clamp(v, 0f, 1f) : 0f;
    }

    /// <summary>Grey values of a whole image, row-major.</summary>
    public static float[] GrayPlane(ImageData image)
    {
        var plane = new float[image.Width * image.Height];
        for (int y = 0; y < image.Height; y++)
            for (int x = 0; x < image.Width; x++) plane[y * image.Width + x] = Gray(image, x, y);
        return plane;
    }

    /// <summary>
    /// True when an 8-bit image looks like a tangent-space normal map (mean colour near (0.5, 0.5, ≥ 0.7)) rather than a
    /// grey height/bump map. Samples at most 64 × 64 pixels.
    /// </summary>
    public static bool LooksLikeNormalMap(ImageData image)
    {
        if (image.Format != PixelFormat.Rgba8 || image.Width <= 0 || image.Height <= 0) return false;
        int sx = System.Math.Max(1, image.Width / 64), sy = System.Math.Max(1, image.Height / 64);
        double r = 0, g = 0, b = 0, spread = 0;
        int n = 0;
        var p = image.Pixels;
        for (int y = 0; y < image.Height; y += sy)
        {
            for (int x = 0; x < image.Width; x += sx)
            {
                int i = (y * image.Width + x) * 4;
                r += p[i];
                g += p[i + 1];
                b += p[i + 2];
                spread += System.Math.Abs(p[i] - p[i + 2]) + System.Math.Abs(p[i + 1] - p[i + 2]);
                n++;
            }
        }
        r /= n * 255.0;
        g /= n * 255.0;
        b /= n * 255.0;
        spread /= n * 255.0;
        return b > 0.7 && System.Math.Abs(r - 0.5) < 0.15 && System.Math.Abs(g - 0.5) < 0.15 && spread > 0.2;
    }

    /// <summary>
    /// Normal map from a height field (0 … 1) by central differences. <paramref name="strength"/> is the height range
    /// expressed in texels-per-unit-height (larger = bumpier); <paramref name="wrap"/> makes the result tileable.
    /// </summary>
    public static ImageData NormalFromHeight(ReadOnlySpan<float> height, int width, int heightPx, float strength, bool wrap)
    {
        var pixels = new byte[width * heightPx * 4];
        for (int y = 0; y < heightPx; y++)
        {
            for (int x = 0; x < width; x++)
            {
                float left = Sample(height, width, heightPx, x - 1, y, wrap), right = Sample(height, width, heightPx, x + 1, y, wrap);
                float up = Sample(height, width, heightPx, x, y - 1, wrap), down = Sample(height, width, heightPx, x, y + 1, wrap);
                // +Y of the normal map points to the top row, so the "up" slope is (up − down).
                var n = Vector3.Normalize(new Vector3(-(right - left) * 0.5f * strength, -(up - down) * 0.5f * strength, 1f));
                int i = (y * width + x) * 4;
                pixels[i] = (byte)MathF.Round((n.X * 0.5f + 0.5f) * 255f);
                pixels[i + 1] = (byte)MathF.Round((n.Y * 0.5f + 0.5f) * 255f);
                pixels[i + 2] = (byte)MathF.Round((n.Z * 0.5f + 0.5f) * 255f);
                pixels[i + 3] = 255;
            }
        }
        return new ImageData { Width = width, Height = heightPx, Format = PixelFormat.Rgba8, Pixels = pixels };
    }

    /// <summary>Normal map from a grey bump image (see <see cref="NormalFromHeight(ReadOnlySpan{float}, int, int, float, bool)"/>).</summary>
    public static ImageData NormalFromHeight(ImageData bump, float strength) =>
        NormalFromHeight(GrayPlane(bump), bump.Width, bump.Height, strength, wrap: true);

    /// <summary>
    /// glTF metallic-roughness texture (G = roughness, B = metallic, R = A = 255) from optional grey maps; a missing map
    /// contributes its constant value. The result has the larger of the two sizes (nearest-neighbour resampling).
    /// </summary>
    public static ImageData PackMetallicRoughness(ImageData? metal, float metalValue, ImageData? rough, float roughValue)
    {
        int w = System.Math.Max(metal?.Width ?? 1, rough?.Width ?? 1);
        int h = System.Math.Max(metal?.Height ?? 1, rough?.Height ?? 1);
        byte mConst = ToByte(metalValue), rConst = ToByte(roughValue);
        var pixels = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                pixels[i] = 255;
                pixels[i + 1] = rough is null ? rConst : ToByte(Gray(rough, x * rough.Width / w, y * rough.Height / h));
                pixels[i + 2] = metal is null ? mConst : ToByte(Gray(metal, x * metal.Width / w, y * metal.Height / h));
                pixels[i + 3] = 255;
            }
        }
        return new ImageData { Width = w, Height = h, Format = PixelFormat.Rgba8, Pixels = pixels };
    }

    /// <summary>
    /// Albedo with the grey value of <paramref name="opacity"/> in alpha. Without an albedo image the colour is white
    /// (the material's base colour factor tints it). Size: the larger of both (nearest-neighbour resampling).
    /// </summary>
    public static ImageData MergeOpacity(ImageData? albedo, ImageData opacity)
    {
        int w = System.Math.Max(albedo?.Width ?? 1, opacity.Width);
        int h = System.Math.Max(albedo?.Height ?? 1, opacity.Height);
        var src = albedo is null ? null : ImageImporter.ToRgba8(albedo);
        var pixels = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                if (src is not null && albedo is not null)
                {
                    int j = ((y * albedo.Height / h) * albedo.Width + x * albedo.Width / w) * 4;
                    pixels[i] = src[j];
                    pixels[i + 1] = src[j + 1];
                    pixels[i + 2] = src[j + 2];
                }
                else pixels[i] = pixels[i + 1] = pixels[i + 2] = 255;
                pixels[i + 3] = ToByte(Gray(opacity, x * opacity.Width / w, y * opacity.Height / h));
            }
        }
        return new ImageData { Width = w, Height = h, Format = PixelFormat.Rgba8, Pixels = pixels };
    }

    /// <summary>0 … 1 → byte with rounding and clamping.</summary>
    public static byte ToByte(float v) => (byte)MathF.Round(System.Math.Clamp(float.IsFinite(v) ? v : 0f, 0f, 1f) * 255f);

    private static float Sample(ReadOnlySpan<float> h, int w, int hh, int x, int y, bool wrap)
    {
        if (wrap)
        {
            x = ((x % w) + w) % w;
            y = ((y % hh) + hh) % hh;
        }
        else
        {
            x = System.Math.Clamp(x, 0, w - 1);
            y = System.Math.Clamp(y, 0, hh - 1);
        }
        return h[y * w + x];
    }
}
