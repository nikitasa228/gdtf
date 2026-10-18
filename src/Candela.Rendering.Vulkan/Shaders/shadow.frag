#version 460
#extension GL_GOOGLE_include_directive : require
// Shadow atlas tiles. Depth only (fixed function); variant ALPHA_MASK discards below the material's cut-off;
// variant RSM also writes the reflective shadow map: diffuse reflectance (1 - metallic) x base colour in linear
// Rec.2020 and the octahedral surface normal facing the light. The light's intensity, colour and cookie are applied
// when the virtual point lights are generated, so the tile stays valid when only those change.

#include "scene.glsl"
#include "color.glsl"

layout(location = 0) in vec3 inNormal;
layout(location = 1) in vec2 inUv;
layout(location = 2) flat in uint inMaterial;

#ifdef RSM
layout(location = 0) out vec4 outAlbedo;
layout(location = 1) out vec2 outNormal;
#endif

void main()
{
    Material m = materials[inMaterial];
#if defined(ALPHA_MASK) || defined(RSM)
    vec2 uv = inUv * m.uvScale;
    vec4 base = (m.flags & MATERIAL_BASE_COLOR_TEXTURE) != 0u ? sampleTexture(m.baseColorTexture, uv) : vec4(1.0);
#endif
#ifdef ALPHA_MASK
    if (base.a * m.baseColorFactor709.a < m.alphaCutoff)
        discard;
#endif
#ifdef RSM
    vec3 albedo = (m.flags & MATERIAL_BASE_COLOR_TEXTURE) != 0u ? rec709ToRec2020(base.rgb * m.baseColorFactor709.rgb) : m.baseColor.rgb;
    outAlbedo = vec4(albedo * (1.0 - clamp(m.metallic, 0.0, 1.0)), 1.0);
    vec3 n = normalize(inNormal);
    outNormal = encodeNormal(gl_FrontFacing ? n : -n);
#endif
}
