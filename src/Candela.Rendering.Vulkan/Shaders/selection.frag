#version 460
#extension GL_GOOGLE_include_directive : require
// Selection mask (display resolution, MAX blending, no depth test): every fragment of a selected instance writes
// coverage = 1 into R and, when it is not hidden behind other geometry (its depth is not farther than the scene depth
// at that pixel), visible = 1 into G. outline.frag turns the mask into the outline.

#include "view.glsl"

layout(location = 0) in vec3 inWorldPosition;
layout(location = 1) in vec3 inNormal;
layout(location = 2) in vec4 inTangent;
layout(location = 3) in vec2 inUv;
layout(location = 4) flat in uint inInstance;
layout(location = 5) flat in uint inMaterial;

layout(location = 0) out vec4 outMask;

void main()
{
    vec2 uv = gl_FragCoord.xy * frame.displaySize.zw;
    float scene = textureLod(sceneDepth, uv, 0.0).r;
    // reverse-Z: larger is nearer; a relative tolerance covers the lower-resolution scene depth when scaled.
    bool visible = gl_FragCoord.z >= scene * 0.998 - 1e-7;
    outMask = vec4(1.0, visible ? 1.0 : 0.0, 0.0, 1.0);
}
