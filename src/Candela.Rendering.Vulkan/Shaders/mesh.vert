#version 460
#extension GL_GOOGLE_include_directive : require
// Shared by the depth pre-pass, forward, colorless and wireframe (polygon-mode) pipelines. gl_Position is invariant
// and computed by the same code in every pipeline, so the forward pass can use an EQUAL depth test.

#include "scene.glsl"

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec3 inNormal;
layout(location = 2) in vec2 inUv;
layout(location = 3) in vec4 inTangent;

layout(location = 0) out vec3 outWorldPosition;
layout(location = 1) out vec3 outNormal;
layout(location = 2) out vec4 outTangent;
layout(location = 3) out vec2 outUv;
layout(location = 4) flat out uint outInstance;
layout(location = 5) flat out uint outMaterial;

invariant gl_Position;

void main()
{
    Instance inst = instances[draw.instanceIndex];
    vec4 world = inst.world * vec4(inPosition, 1.0);
    gl_Position = frame.viewProjection * world;
    outWorldPosition = world.xyz;
    outNormal = mat3(inst.normalMatrix) * inNormal;
    outTangent = vec4(mat3(inst.world) * inTangent.xyz, inTangent.w);
    outUv = inUv;
    outInstance = draw.instanceIndex;
    outMaterial = draw.materialIndex;
}
