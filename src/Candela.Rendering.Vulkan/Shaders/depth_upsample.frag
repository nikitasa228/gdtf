#version 460
#extension GL_GOOGLE_include_directive : require
// Overlay depth at display resolution when the scene is rendered at a reduced resolution (ResolutionScale < 1):
// the nearest scene depth sample, so the grid, lines and outlines are depth-tested against the scene.

#include "view.glsl"

layout(location = 0) in vec2 inUv;

void main()
{
    gl_FragDepth = textureLod(sceneDepth, gl_FragCoord.xy * frame.displaySize.zw, 0.0).r;
}
