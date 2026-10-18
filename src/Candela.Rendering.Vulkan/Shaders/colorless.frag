#version 460
#extension GL_GOOGLE_include_directive : require
// Colorless view: neutral grey Lambert, no materials and no stage lights. A key light from above and behind the
// camera's left shoulder (frame.headlight, fixed relative to the camera) separates floors, fronts and sides; a weaker
// headlight along the view direction keeps faces towards the camera readable; a sky/ground hemisphere ambient fills
// the rest. Display-linear output (the tone-mapping pass only clamps and sRGB-encodes).

#include "common.glsl"

layout(location = 0) in vec3 inWorldPosition;
layout(location = 1) in vec3 inNormal;
layout(location = 2) in vec4 inTangent;
layout(location = 3) in vec2 inUv;
layout(location = 4) flat in uint inInstance;
layout(location = 5) flat in uint inMaterial;

layout(location = 0) out vec4 outColor;

const float ALBEDO = 0.62;
const float KEY = 0.75;
const float HEADLIGHT = 0.22;
const float SKY = 0.25;
const float GROUND = 0.08;

void main()
{
    vec3 N = normalize(inNormal);
    if (!gl_FrontFacing)
        N = -N;
    vec3 toCamera = isOrthographic() ? -frame.cameraForward.xyz : normalize(frame.cameraPosition.xyz - inWorldPosition);
    float key = max(dot(N, frame.headlight.xyz), 0.0);
    float head = max(dot(N, toCamera), 0.0);
    float ambient = mix(GROUND, SKY, 0.5 + 0.5 * N.z);
    outColor = vec4(vec3(ALBEDO * (ambient + KEY * key + HEADLIGHT * head)), 1.0);
}
