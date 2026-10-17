#version 460
#extension GL_GOOGLE_include_directive : require
// Colorless view: neutral grey Lambert lit by a camera headlight plus a soft sky/ground hemisphere ambient. No
// materials, no stage lights. Display-linear output (the tone-mapping pass only clamps and sRGB-encodes).

#include "common.glsl"

layout(location = 0) in vec3 inWorldPosition;
layout(location = 1) in vec3 inNormal;
layout(location = 2) in vec4 inTangent;
layout(location = 3) in vec2 inUv;
layout(location = 4) flat in uint inInstance;
layout(location = 5) flat in uint inMaterial;

layout(location = 0) out vec4 outColor;

const float ALBEDO = 0.62;
const float HEADLIGHT = 0.80;
const float SKY = 0.24;
const float GROUND = 0.10;

void main()
{
    vec3 N = normalize(inNormal);
    if (!gl_FrontFacing)
        N = -N;
    float lambert = max(dot(N, frame.headlight.xyz), 0.0);
    float ambient = mix(GROUND, SKY, 0.5 + 0.5 * N.z);
    outColor = vec4(vec3(ALBEDO * (ambient + HEADLIGHT * lambert)), 1.0);
}
