#version 460
#extension GL_GOOGLE_include_directive : require
// Anti-aliased line coverage: full inside the line width, a one-pixel linear ramp at both edges.

#include "common.glsl"

layout(location = 0) in vec4 inColor;
layout(location = 1) noperspective in float inDistance;
layout(location = 2) flat in float inHalfWidth;

layout(location = 0) out vec4 outColor;

void main()
{
    float coverage = clamp(inHalfWidth + 0.5 - abs(inDistance), 0.0, 1.0);
    if (coverage <= 0.0)
        discard;
    outColor = vec4(inColor.rgb, inColor.a * coverage);
}
