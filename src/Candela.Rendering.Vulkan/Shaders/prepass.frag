#version 460
#extension GL_GOOGLE_include_directive : require
// Depth pre-pass: writes depth (fixed function), the instance pick id, the instance index + 1 (overlays) and the
// octahedral-encoded surface normal (indirect light, bilateral filters). Variant ALPHA_MASK discards fragments below
// the material's alpha cut-off (glTF MASK).

#include "scene.glsl"

layout(location = 0) in vec3 inWorldPosition;
layout(location = 1) in vec3 inNormal;
layout(location = 2) in vec4 inTangent;
layout(location = 3) in vec2 inUv;
layout(location = 4) flat in uint inInstance;
layout(location = 5) flat in uint inMaterial;

layout(location = 0) out uint outPickId;
layout(location = 1) out uint outInstance;
layout(location = 2) out vec2 outNormal;

void main()
{
#ifdef ALPHA_MASK
    Material m = materials[inMaterial];
    float alpha = m.baseColor.a;
    if ((m.flags & MATERIAL_BASE_COLOR_TEXTURE) != 0u)
        alpha *= sampleTexture(m.baseColorTexture, inUv * m.uvScale).a;
    if (alpha < m.alphaCutoff)
        discard;
#endif
    Instance inst = instances[inInstance];
    outPickId = (inst.flags & INSTANCE_FLAG_NOT_PICKABLE) != 0u ? 0u : inst.pickId;
    outInstance = inInstance + 1u;
    vec3 n = normalize(inNormal);
    outNormal = encodeNormal(gl_FrontFacing ? n : -n);
}
