using System.Numerics;
using Candela.Assets;
using Candela.Core.Geometry;
using Hexa.NET.ImGui;

namespace Candela.App.Ui;

/// <summary>
/// Material previews for the properties panel: a lit sphere of a built-in material (<see cref="MaterialLibrary"/>) —
/// its albedo texture (wood planks, concrete, carpet, LED panel) mapped onto the sphere, Lambert shading under a key
/// light with a specular highlight shaped by the material's roughness and metalness — drawn as a grid of small quads,
/// and a flat swatch of the mean albedo for list entries. The sphere's colours are computed once per material and
/// cached; drawing allocates nothing.
/// </summary>
internal static class MaterialSwatch
{
    /// <summary>Cells per side of the sphere preview.</summary>
    public const int Cells = 28;

    private static readonly Dictionary<string, uint[]> s_spheres = new(StringComparer.Ordinal);

    /// <summary>The mean albedo of a material as a display colour (sRGB; the default grey for unknown ids).</summary>
    public static Vector4 FlatColor(string? materialId)
    {
        var m = MaterialLibrary.Default.Find(materialId) ?? MaterialLibrary.Default.Find(Core.SceneGraph.Components.MaterialIds.Default);
        var a = m?.Albedo ?? new Vector3(0.4f);
        return new Vector4(Encode(a.X), Encode(a.Y), Encode(a.Z), 1f);
    }

    /// <summary>Draws a flat round swatch of <paramref name="size"/> pixels at the cursor and advances it.</summary>
    public static void Flat(string? materialId, float size)
    {
        var p = ImGui.GetCursorScreenPos();
        var dl = ImGui.GetWindowDrawList();
        dl.AddCircleFilled(p + new Vector2(size * 0.5f), size * 0.45f, Theme.U32(FlatColor(materialId)), 20);
        dl.AddCircle(p + new Vector2(size * 0.5f), size * 0.45f, Theme.U32(Theme.WithAlpha(Theme.Text, 0.35f)), 20);
        ImGui.Dummy(new Vector2(size));
    }

    /// <summary>Draws the lit sphere preview of <paramref name="materialId"/> of <paramref name="size"/> pixels at the cursor.</summary>
    public static void Sphere(string? materialId, float size)
    {
        var colors = SphereColors(materialId ?? "");
        var p = ImGui.GetCursorScreenPos();
        var dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(p, p + new Vector2(size), Theme.U32(Theme.Rgb(0x15161A)), 4f);
        float cell = size / Cells;
        for (int y = 0; y < Cells; y++)
        {
            for (int x = 0; x < Cells; x++)
            {
                uint c = colors[y * Cells + x];
                if (c == 0)
                    continue;
                var min = p + new Vector2(x * cell, y * cell);
                dl.AddRectFilled(min, min + new Vector2(cell + 0.5f), c);
            }
        }

        dl.AddRect(p, p + new Vector2(size), Theme.U32(Theme.WithAlpha(Theme.Text, 0.25f)), 4f);
        ImGui.Dummy(new Vector2(size));
    }

    /// <summary>The cached sphere colours (row-major, packed ImGui colours; 0 = background).</summary>
    internal static uint[] SphereColors(string materialId)
    {
        if (s_spheres.TryGetValue(materialId, out var cached))
            return cached;
        var library = MaterialLibrary.Default;
        var material = library.Find(materialId) ?? library.Find(Core.SceneGraph.Components.MaterialIds.Default);
        var albedo = material?.Albedo ?? new Vector3(0.4f);
        float roughness = Math.Clamp(material?.Roughness ?? 0.6f, 0.03f, 1f);
        float metallic = Math.Clamp(material?.Metallic ?? 0f, 0f, 1f);
        ImageData? texture = null;
        if (material is not null && material.TextureChannels.Contains(MaterialLibrary.AlbedoChannel))
            texture = library.LoadTexture(MaterialLibrary.TextureKey(material.Id, MaterialLibrary.AlbedoChannel));

        var light = Vector3.Normalize(new Vector3(-0.5f, 0.6f, 0.65f));
        var view = Vector3.UnitZ;
        var half = Vector3.Normalize(light + view);
        float shininess = 2f / MathF.Max(roughness * roughness * roughness * roughness, 1e-4f);
        var f0 = Vector3.Lerp(new Vector3(0.04f), albedo, metallic);
        var colors = new uint[Cells * Cells];
        for (int y = 0; y < Cells; y++)
        {
            for (int x = 0; x < Cells; x++)
            {
                float nx = (x + 0.5f) / Cells * 2f - 1f, ny = 1f - (y + 0.5f) / Cells * 2f;
                float r2 = nx * nx + ny * ny;
                if (r2 > 1f)
                    continue;
                var n = new Vector3(nx, ny, MathF.Sqrt(1f - r2));
                var baseColor = albedo;
                if (texture is { Format: PixelFormat.Rgba8 } t && t.Width > 0 && t.Height > 0 && t.Pixels.Length >= t.Width * t.Height * 4)
                {
                    // Spherical mapping: one texture tile over the visible half.
                    float u = 0.5f + MathF.Atan2(n.X, n.Z) / MathF.PI * 0.5f;
                    float v = 0.5f - MathF.Asin(Math.Clamp(n.Y, -1f, 1f)) / MathF.PI;
                    int tx = Math.Clamp((int)(u * t.Width), 0, t.Width - 1), ty = Math.Clamp((int)(v * t.Height), 0, t.Height - 1);
                    int i = (ty * t.Width + tx) * 4;
                    baseColor = new Vector3(Decode(t.Pixels[i]), Decode(t.Pixels[i + 1]), Decode(t.Pixels[i + 2]));
                }

                float ndl = MathF.Max(Vector3.Dot(n, light), 0f);
                float ndh = MathF.Max(Vector3.Dot(n, half), 0f);
                float spec = MathF.Pow(ndh, shininess) * (shininess + 2f) / 8f * ndl;
                var diffuse = baseColor * (1f - metallic) * (0.08f + 0.92f * ndl);
                var specular = f0 * spec * (metallic > 0.5f ? 1f : 0.6f) + (metallic > 0.5f ? baseColor * 0.15f : Vector3.Zero);
                var c = Vector3.Clamp(diffuse + specular, Vector3.Zero, Vector3.One);
                colors[y * Cells + x] = Theme.U32(new Vector4(Encode(c.X), Encode(c.Y), Encode(c.Z), 1f)) | 0xFF000000u;
            }
        }

        s_spheres[materialId] = colors;
        return colors;
    }

    private static float Encode(float v)
    {
        v = Math.Clamp(v, 0f, 1f);
        return v <= 0.0031308f ? 12.92f * v : 1.055f * MathF.Pow(v, 1f / 2.4f) - 0.055f;
    }

    private static float Decode(byte b)
    {
        float c = b / 255f;
        return c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
    }
}
