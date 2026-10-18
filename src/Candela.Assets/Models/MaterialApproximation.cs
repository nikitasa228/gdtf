using System.Numerics;
using Candela.Core.Geometry;

namespace Candela.Assets;

/// <summary>
/// A classic (Phong / Blinn / Lambert) material as found in OBJ/MTL, FBX, COLLADA and 3DS files. Colours are linear
/// 0 … 1 values as written in the file.
/// </summary>
public sealed record PhongMaterial
{
    /// <summary>Material name (also used for the metal/glass name hints).</summary>
    public string Name { get; init; } = "";

    /// <summary>Diffuse colour (OBJ <c>Kd</c>).</summary>
    public Vector3 Diffuse { get; init; } = new(0.8f);

    /// <summary>Specular colour (OBJ <c>Ks</c>); black means "no highlight".</summary>
    public Vector3 Specular { get; init; } = Vector3.Zero;

    /// <summary>Multiplier of <see cref="Specular"/> (FBX/3DS shininess strength); 1 when the format has none.</summary>
    public float SpecularStrength { get; init; } = 1f;

    /// <summary>Phong exponent (OBJ <c>Ns</c>, 0 … 1000); null when the file does not say.</summary>
    public float? Shininess { get; init; }

    /// <summary>Opacity (OBJ <c>d</c>, 1 = opaque).</summary>
    public float Opacity { get; init; } = 1f;

    /// <summary>Emissive colour (OBJ <c>Ke</c>).</summary>
    public Vector3 Emissive { get; init; }

    /// <summary>Render both faces.</summary>
    public bool TwoSided { get; init; }
}

/// <summary>
/// Approximates PBR metallic-roughness parameters for classic materials (documented in docs/assets.md):
/// <list type="bullet">
/// <item>Roughness from the Phong exponent n via the Blinn-Phong ↔ Beckmann correspondence α = √(2 / (n + 2))
/// (Walter et al. 2007) and glTF's perceptual roughness r = √α. n = 0 → 1, n = 10 → 0.64, n = 100 → 0.37,
/// n = 1000 → 0.21. Without a specular highlight (specular ≈ black) roughness is at least 0.9; without an exponent 0.8.</item>
/// <item>Metallic = 1 when the specular colour dominates a dark diffuse colour (max Ks &gt; 0.5 and &gt; 2 × max Kd), when the
/// specular colour is clearly tinted (saturation &gt; 0.25 at max Ks &gt; 0.25 — dielectrics have white highlights), or
/// when the name says metal (chrome, steel, aluminium, gold, …) and there is a highlight (max Ks ≥ 0.2). A metal's base
/// colour is its specular colour (= F0).</item>
/// <item>Opacity &lt; 1: clear materials (named glass/window, or opacity ≤ 0.6 with roughness ≤ 0.3) become transmissive
/// glass (transmission = 1 − opacity, IOR 1.5); everything else alpha-blends.</item>
/// <item>Emission: the emissive colour normalised to its maximum, luminance = max × <see cref="EmissiveReferenceLuminance"/>.</item>
/// </list>
/// </summary>
public static class MaterialApproximation
{
    /// <summary>
    /// Luminance in cd/m² given to an emissive colour of 1 from a file without physical emission units (roughly the white
    /// of an sRGB display; glTF emissive factors and Phong <c>Ke</c> are relative).
    /// </summary>
    public const float EmissiveReferenceLuminance = 100f;

    /// <summary>Lowest roughness produced (perfect mirrors are not representable with GGX).</summary>
    public const float MinimumRoughness = 0.03f;

    private static readonly string[] s_metalWords =
    {
        "metal", "chrome", "chrom", "steel", "alumin", "alu_", "gold", "silver", "copper", "brass", "bronze", "iron", "nickel",
        "zinc", "titan", "металл", "хром", "сталь", "алюмин", "золот", "серебр", "медь", "латун",
    };

    private static readonly string[] s_glassWords = { "glass", "window", "стекл", "окно" };

    /// <summary>Perceptual (glTF) roughness for a Phong exponent: (2 / (n + 2))^¼, clamped to [<see cref="MinimumRoughness"/>, 1].</summary>
    public static float RoughnessFromShininess(float exponent)
    {
        if (!float.IsFinite(exponent) || exponent <= 0) return 1f;
        float r = MathF.Pow(2f / (exponent + 2f), 0.25f);
        return System.Math.Clamp(r, MinimumRoughness, 1f);
    }

    /// <summary>True when the material name suggests a metal (see the class remarks).</summary>
    public static bool IsMetalName(string? name) => ContainsAny(name, s_metalWords);

    /// <summary>True when the material name suggests glass.</summary>
    public static bool IsGlassName(string? name) => ContainsAny(name, s_glassWords);

    /// <summary>Converts a classic material to PBR metallic-roughness (textures are assigned by the caller).</summary>
    public static MaterialData FromPhong(PhongMaterial phong)
    {
        ArgumentNullException.ThrowIfNull(phong);
        var kd = Clamp01(phong.Diffuse);
        float strength = float.IsFinite(phong.SpecularStrength) && phong.SpecularStrength >= 0 ? phong.SpecularStrength : 1f;
        var ksColour = Clamp01(phong.Specular);
        var ks = Clamp01(phong.Specular * strength);
        float s = Max(ks), d = Max(kd);

        float roughness = phong.Shininess is { } n && n > 0 ? RoughnessFromShininess(n) : 0.8f;
        if (s < 0.02f) roughness = MathF.Max(roughness, 0.9f);

        bool dominant = s > 0.5f && s > 2f * d;
        bool tinted = s > 0.25f && Saturation(ks) > 0.25f;
        bool named = IsMetalName(phong.Name) && s >= 0.2f;
        bool metal = dominant || tinted || named;

        var baseColor = metal && Max(ksColour) > 0.05f ? ksColour : kd;
        float opacity = float.IsFinite(phong.Opacity) ? System.Math.Clamp(phong.Opacity, 0f, 1f) : 1f;

        var m = new MaterialData
        {
            Name = phong.Name,
            Metallic = metal ? 1f : 0f,
            Roughness = roughness,
            DoubleSided = phong.TwoSided,
            BaseColorFactor = new Vector4(baseColor, 1f),
        };

        if (opacity < 0.999f)
        {
            bool clear = IsGlassName(phong.Name) || (opacity <= 0.6f && roughness <= 0.3f);
            if (clear && !metal)
            {
                m.Transmission = 1f - opacity;
                m.Ior = 1.5f;
                m.Roughness = MathF.Min(m.Roughness, 0.1f);
                m.DoubleSided = true;
            }
            else
            {
                m.AlphaMode = AlphaMode.Blend;
                m.BaseColorFactor = new Vector4(baseColor, opacity);
            }
        }

        var ke = phong.Emissive;
        float e = Max(new Vector3(MathF.Max(ke.X, 0), MathF.Max(ke.Y, 0), MathF.Max(ke.Z, 0)));
        if (float.IsFinite(e) && e > 1e-3f)
        {
            m.EmissiveColor = Vector3.Max(ke, Vector3.Zero) / e;
            m.EmissiveLuminance = MathF.Min(e, 1000f) * EmissiveReferenceLuminance;
        }
        return m;
    }

    private static bool ContainsAny(string? name, string[] words)
    {
        if (string.IsNullOrEmpty(name)) return false;
        foreach (var w in words)
            if (name.Contains(w, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static Vector3 Clamp01(Vector3 v) =>
        new(Clamp01(v.X), Clamp01(v.Y), Clamp01(v.Z));

    private static float Clamp01(float v) => float.IsFinite(v) ? System.Math.Clamp(v, 0f, 1f) : 0f;

    private static float Max(Vector3 v) => MathF.Max(v.X, MathF.Max(v.Y, v.Z));

    private static float Saturation(Vector3 v)
    {
        float max = Max(v), min = MathF.Min(v.X, MathF.Min(v.Y, v.Z));
        return max > 1e-6f ? (max - min) / max : 0f;
    }
}
