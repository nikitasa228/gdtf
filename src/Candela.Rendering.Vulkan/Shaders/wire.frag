#version 460
#extension GL_GOOGLE_include_directive : require
// Wireframe view on devices with fillModeNonSolid: triangles rasterised as lines (VK_POLYGON_MODE_LINE) in a constant
// display-linear colour from the push constants.

#include "common.glsl"

layout(location = 0) in vec3 inWorldPosition;
layout(location = 1) in vec3 inNormal;
layout(location = 2) in vec4 inTangent;
layout(location = 3) in vec2 inUv;
layout(location = 4) flat in uint inInstance;
layout(location = 5) flat in uint inMaterial;

layout(location = 0) out vec4 outColor;

void main()
{
    outColor = draw.color;
}
