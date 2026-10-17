using System.Numerics;
using Candela.Core.Geometry;
using Candela.Photometry;

namespace Candela.Rendering.Vulkan;

/// <summary>Bindless slots of a material's textures (0 = <see cref="ShaderConstants.TextureWhite"/> etc. when absent).</summary>
internal readonly record struct MaterialTextureSlots(uint BaseColor, uint Normal, uint MetallicRoughness, uint Occlusion, uint Emissive);

/// <summary>
/// Converts <see cref="MaterialData"/> (glTF semantics, linear Rec.709 factors) into <see cref="GpuMaterial"/>
/// (linear Rec.2020 working space). Texture colours are converted in the shader: sRGB textures are sampled through
/// <c>*_SRGB</c> formats (hardware decode to linear Rec.709), multiplied by the Rec.709 factor and then converted.
/// </summary>
internal static class MaterialPacker
{
    /// <summary>Smallest perceptual roughness (a point light on a perfect mirror has no finite highlight).</summary>
    public const float MinRoughness = 0.02f;

    /// <summary>Material used for missing/invalid material slots: neutral grey, rough dielectric.</summary>
    public static GpuMaterial Default { get; } = Pack(new MaterialData { Name = "default", BaseColorFactor = new Vector4(0.6f, 0.6f, 0.6f, 1f), Roughness = 0.7f },
        new MaterialTextureSlots(ShaderConstants.TextureWhite, ShaderConstants.TextureFlatNormal, ShaderConstants.TextureWhite,
            ShaderConstants.TextureWhite, ShaderConstants.TextureWhite), 0);

    /// <summary>Packs a material; <paramref name="textureFlags"/> are the MATERIAL_*_TEXTURE bits of the textures present.</summary>
    public static GpuMaterial Pack(MaterialData m, MaterialTextureSlots slots, uint textureFlags)
    {
        var factor = m.BaseColorFactor;
        var rgb709 = new Vector3(Positive(factor.X), Positive(factor.Y), Positive(factor.Z));
        float alpha = float.IsFinite(factor.W) ? Math.Clamp(factor.W, 0f, 1f) : 1f;
        float ior = float.IsFinite(m.Ior) ? Math.Clamp(m.Ior, 1f, 4f) : 1.5f;
        float r = (ior - 1f) / (ior + 1f);
        var emissiveColor = m.EmissiveColor;
        var emissive709 = new Vector3(Positive(emissiveColor.X), Positive(emissiveColor.Y), Positive(emissiveColor.Z)) * Positive(m.EmissiveLuminance);
        float transmission = Clamp01(m.Transmission);

        uint flags = textureFlags;
        if (m.AlphaMode == AlphaMode.Mask)
            flags |= ShaderConstants.MaterialAlphaMask;
        else if (m.AlphaMode == AlphaMode.Blend)
            flags |= ShaderConstants.MaterialAlphaBlend;
        if (m.DoubleSided)
            flags |= ShaderConstants.MaterialDoubleSided;
        if (transmission > 0f)
            flags |= ShaderConstants.MaterialTransmissive;

        return new GpuMaterial
        {
            BaseColor = new Vector4(ColorSpaces.Rec709ToRec2020(rgb709), alpha),
            BaseColorFactor709 = new Vector4(rgb709, alpha),
            Emissive = ColorSpaces.Rec709ToRec2020(emissive709),
            Metallic = Clamp01(m.Metallic),
            Roughness = float.IsFinite(m.Roughness) ? Math.Clamp(m.Roughness, MinRoughness, 1f) : 0.6f,
            F0 = r * r,
            AlphaCutoff = float.IsFinite(m.AlphaCutoff) ? Math.Clamp(m.AlphaCutoff, 0f, 1f) : 0.5f,
            Transmission = transmission,
            UvScale = float.IsFinite(m.UvScale.X) && float.IsFinite(m.UvScale.Y) ? m.UvScale : Vector2.One,
            BaseColorTexture = slots.BaseColor,
            NormalTexture = slots.Normal,
            MetallicRoughnessTexture = slots.MetallicRoughness,
            OcclusionTexture = slots.Occlusion,
            EmissiveTexture = slots.Emissive,
            Flags = flags,
            Emissive709 = emissive709,
            Ior = ior,
        };
    }

    /// <summary>How a material is drawn.</summary>
    public static DrawKind KindOf(in GpuMaterial m) =>
        (m.Flags & (ShaderConstants.MaterialAlphaBlend | ShaderConstants.MaterialTransmissive)) != 0 ? DrawKind.Blend
        : (m.Flags & ShaderConstants.MaterialAlphaMask) != 0 ? DrawKind.Mask
        : DrawKind.Opaque;

    private static float Positive(float v) => float.IsFinite(v) ? MathF.Max(v, 0f) : 0f;

    private static float Clamp01(float v) => float.IsFinite(v) ? Math.Clamp(v, 0f, 1f) : 0f;
}

/// <summary>Draw category of a sub-mesh, derived from its material.</summary>
internal enum DrawKind : byte
{
    /// <summary>Depth pre-pass + EQUAL-tested shading.</summary>
    Opaque,

    /// <summary>Alpha-tested (glTF MASK): the pre-pass discards below the cut-off.</summary>
    Mask,

    /// <summary>Alpha-blended or transmissive: not in the pre-pass, sorted back to front, premultiplied blending.</summary>
    Blend,
}
