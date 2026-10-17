using System.Numerics;
using Candela.Core.Geometry;

namespace Candela.Assets;

/// <summary>
/// Deterministic, seamlessly tileable textures for the built-in materials. A tile covers 1 m × 1 m at UV scale 1
/// (procedural meshes use UVs in metres). Albedo maps are sRGB-encoded and normalised so that their mean linear albedo
/// equals the material's reference albedo; roughness maps are glTF metallic-roughness maps (G = roughness, B = metallic
/// = 0, R = A = 255) with absolute values; normal maps are tangent space, +Y up (glTF).
/// </summary>
internal static class ProceduralTextures
{
    /// <summary>Square size of the generated tiles in pixels.</summary>
    public const int Size = 256;

    /// <summary>One generated material texture set (channels that a material does not use are null).</summary>
    public sealed record TextureSet(ImageData? Albedo, ImageData? Normal, ImageData? Roughness);

    /// <summary>Stage boards: 8 planks of 125 mm per metre running along U, one butt joint per row, grain, bevelled gaps.</summary>
    public static TextureSet WoodPlanks(Vector3 meanAlbedo, float meanRoughness)
    {
        const int n = Size, rows = 8, rowPx = n / rows;
        var variation = new float[n * n];
        var height = new float[n * n];
        var rough = new float[n * n];
        for (int y = 0; y < n; y++)
        {
            int row = y / rowPx;
            float v = (y + 0.5f) / n;
            float joint = TileableNoise.Hash01(row, 7, 11u);
            float tone = 0.8f + 0.4f * TileableNoise.Hash01(row, 3, 17u);
            int dy = System.Math.Min(y % rowPx, rowPx - 1 - y % rowPx);
            for (int x = 0; x < n; x++)
            {
                float u = (x + 0.5f) / n;
                float grain = TileableNoise.Fbm(u, v, 2, 64, 3, 0.55f, 29u + (uint)row);
                float figure = 0.5f + 0.5f * MathF.Sin(2 * MathF.PI * (v * rows * 3 + 2.5f * TileableNoise.Fbm(u, v, 4, 16, 2, 0.5f, 41u)));
                int jointPx = (int)(joint * n);
                int dxRaw = System.Math.Abs(x - jointPx);
                int dx = System.Math.Min(dxRaw, n - dxRaw);
                int edge = System.Math.Min(dy, dx);
                float bevel = edge == 0 ? 0f : edge == 1 ? 0.6f : 1f;
                int i = y * n + x;
                variation[i] = tone * (0.75f + 0.45f * grain) * (0.9f + 0.1f * figure) * (edge == 0 ? 0.35f : 1f);
                height[i] = bevel + 0.03f * grain;
                rough[i] = edge == 0 ? 0.9f : meanRoughness + 0.12f * (grain - 0.5f);
            }
        }
        return new TextureSet(Albedo(variation, meanAlbedo), TextureOps.NormalFromHeight(height, n, n, 3f, wrap: true), Roughness(rough, n));
    }

    /// <summary>Concrete: low-frequency mottling, fine aggregate grain and sparse pores.</summary>
    public static TextureSet Concrete(Vector3 meanAlbedo, float meanRoughness)
    {
        const int n = Size;
        var variation = new float[n * n];
        var height = new float[n * n];
        var rough = new float[n * n];
        for (int y = 0; y < n; y++)
        {
            float v = (y + 0.5f) / n;
            for (int x = 0; x < n; x++)
            {
                float u = (x + 0.5f) / n;
                float mottle = TileableNoise.Fbm(u, v, 4, 4, 5, 0.5f, 101u);
                float grain = TileableNoise.Value(u, v, 128, 128, 103u);
                bool pore = TileableNoise.Hash01(x, y, 107u) > 0.988f;
                int i = y * n + x;
                variation[i] = (0.75f + 0.5f * mottle + 0.2f * (grain - 0.5f)) * (pore ? 0.55f : 1f);
                height[i] = 0.5f * mottle + 0.35f * grain - (pore ? 0.6f : 0f);
                rough[i] = pore ? 0.97f : meanRoughness + 0.08f * (grain - 0.5f) + 0.05f * (mottle - 0.5f);
            }
        }
        return new TextureSet(Albedo(variation, meanAlbedo), TextureOps.NormalFromHeight(height, n, n, 2.5f, wrap: true), Roughness(rough, n));
    }

    /// <summary>Cut-pile carpet: per-pixel fibre noise over tuft-sized blotches.</summary>
    public static TextureSet Carpet(Vector3 meanAlbedo, float meanRoughness)
    {
        const int n = Size;
        var variation = new float[n * n];
        var height = new float[n * n];
        var rough = new float[n * n];
        for (int y = 0; y < n; y++)
        {
            float v = (y + 0.5f) / n;
            for (int x = 0; x < n; x++)
            {
                float u = (x + 0.5f) / n;
                float fibre = TileableNoise.Hash01(x, y, 211u);
                float tuft = TileableNoise.Value(u, v, 64, 64, 223u);
                float blotch = TileableNoise.Fbm(u, v, 8, 8, 3, 0.5f, 227u);
                int i = y * n + x;
                variation[i] = 0.7f + 0.35f * (0.5f * fibre + 0.5f * tuft) + 0.25f * (blotch - 0.5f);
                height[i] = 0.55f * fibre + 0.45f * tuft;
                rough[i] = System.Math.Clamp(meanRoughness - 0.04f * fibre, 0f, 1f);
            }
        }
        return new TextureSet(Albedo(variation, meanAlbedo), TextureOps.NormalFromHeight(height, n, n, 1.5f, wrap: true), Roughness(rough, n));
    }

    /// <summary>Worn black vinyl: roughness variation only (scuffs and polished traffic lanes).</summary>
    public static TextureSet VinylScuffs(float meanRoughness)
    {
        const int n = Size;
        var rough = new float[n * n];
        for (int y = 0; y < n; y++)
        {
            float v = (y + 0.5f) / n;
            for (int x = 0; x < n; x++)
            {
                float u = (x + 0.5f) / n;
                float scuff = TileableNoise.Fbm(u, v, 4, 4, 4, 0.55f, 307u);
                float streak = TileableNoise.Fbm(u, v, 1, 16, 2, 0.5f, 311u);
                rough[y * n + x] = System.Math.Clamp(meanRoughness + 0.16f * (scuff - 0.5f) + 0.08f * (streak - 0.5f), 0.05f, 1f);
            }
        }
        return new TextureSet(null, null, Roughness(rough, n));
    }

    /// <summary>
    /// A switched-off LED panel: 8 × 8 SMD packages on a black louvred mask per tile (64 px). With the material's UV scale
    /// of 32 a tile spans 31.25 mm, i.e. a 3.9 mm pixel pitch.
    /// </summary>
    public static TextureSet LedPanel(Vector3 meanAlbedo)
    {
        const int n = 64, cell = 8;
        var variation = new float[n * n];
        var rough = new float[n * n];
        for (int y = 0; y < n; y++)
        {
            int cy = y % cell;
            for (int x = 0; x < n; x++)
            {
                int cx = x % cell;
                bool package = cx is >= 2 and <= 5 && cy is >= 2 and <= 5;
                bool die = cx is 3 or 4 && cy is 3 or 4;
                int i = y * n + x;
                variation[i] = die ? 8f : package ? 5f : 1f;
                rough[i] = package ? 0.25f : 0.8f;
            }
        }
        return new TextureSet(Albedo(variation, meanAlbedo, n), null, Roughness(rough, n));
    }

    /// <summary>sRGB albedo whose linear mean equals <paramref name="mean"/> (per channel), pattern from <paramref name="variation"/>.</summary>
    private static ImageData Albedo(float[] variation, Vector3 mean, int size = Size)
    {
        double sum = 0;
        foreach (var v in variation) sum += v;
        float norm = sum > 0 ? (float)(variation.Length / sum) : 1f;
        var pixels = new byte[size * size * 4];
        for (int i = 0; i < variation.Length; i++)
        {
            float k = variation[i] * norm;
            pixels[i * 4] = ColorEncoding.LinearToSrgbByte(mean.X * k);
            pixels[i * 4 + 1] = ColorEncoding.LinearToSrgbByte(mean.Y * k);
            pixels[i * 4 + 2] = ColorEncoding.LinearToSrgbByte(mean.Z * k);
            pixels[i * 4 + 3] = 255;
        }
        return new ImageData { Width = size, Height = size, Format = PixelFormat.Rgba8, Pixels = pixels };
    }

    private static ImageData Roughness(float[] roughness, int size)
    {
        var pixels = new byte[size * size * 4];
        for (int i = 0; i < roughness.Length; i++)
        {
            pixels[i * 4] = 255;
            pixels[i * 4 + 1] = TextureOps.ToByte(roughness[i]);
            pixels[i * 4 + 2] = 0;
            pixels[i * 4 + 3] = 255;
        }
        return new ImageData { Width = size, Height = size, Format = PixelFormat.Rgba8, Pixels = pixels };
    }
}
