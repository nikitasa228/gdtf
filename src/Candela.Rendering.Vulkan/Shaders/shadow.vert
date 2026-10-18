#version 460
#extension GL_GOOGLE_include_directive : require
// Shadow atlas tiles: casters transformed by the tile's light view-projection (shadowSlots[draw.mode], reverse-Z,
// infinite far plane). The viewport/scissor select the tile in the atlas.

#include "scene.glsl"
#include "view.glsl"

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec3 inNormal;
layout(location = 2) in vec2 inUv;
layout(location = 3) in vec4 inTangent;

layout(location = 0) out vec3 outNormal;
layout(location = 1) out vec2 outUv;
layout(location = 2) flat out uint outMaterial;

void main()
{
    Instance inst = instances[draw.instanceIndex];
    vec4 world = inst.world * vec4(inPosition, 1.0);
    gl_Position = shadowSlots[draw.mode].viewProjection * world;
    outNormal = mat3(inst.normalMatrix) * inNormal;
    outUv = inUv;
    outMaterial = draw.materialIndex;
}
