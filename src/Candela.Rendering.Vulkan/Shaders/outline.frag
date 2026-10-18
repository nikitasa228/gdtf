#version 460
#extension GL_GOOGLE_include_directive : require
// Selection outline over the display image: pixels outside a selected object's silhouette but within ~2 px of it
// get the selection colour (sRGB, alpha blended), anti-aliased by the distance to the nearest covered pixel. Where
// the nearby part of the object is hidden behind other geometry the outline is drawn dimmed, so selected objects
// stay findable through occluders.

#include "view.glsl"

layout(location = 0) in vec2 inUv;
layout(location = 0) out vec4 outColor;

const float OUTLINE_RADIUS = 2.0;   // pixels
const float OCCLUDED_ALPHA = 0.45;

void main()
{
    ivec2 p = ivec2(gl_FragCoord.xy);
    ivec2 size = ivec2(frame.displaySize.xy);
    if (texelFetch(selectionMask, p, 0).r > 0.5)
        discard;
    float cover = 0.0;
    float visible = 0.0;
    for (int dy = -3; dy <= 3; ++dy)
    {
        for (int dx = -3; dx <= 3; ++dx)
        {
            ivec2 q = p + ivec2(dx, dy);
            if (any(lessThan(q, ivec2(0))) || any(greaterThanEqual(q, size)))
                continue;
            vec4 m = texelFetch(selectionMask, q, 0);
            if (m.r < 0.5)
                continue;
            float w = clamp(OUTLINE_RADIUS + 0.5 - length(vec2(dx, dy)), 0.0, 1.0);
            cover = max(cover, w);
            visible = max(visible, m.g * w);
        }
    }

    if (cover <= 0.0)
        discard;
    float alpha = cover * mix(OCCLUDED_ALPHA, 1.0, visible >= cover - 1e-3 ? 1.0 : visible / cover);
    outColor = vec4(frame.selectionColor.rgb, frame.selectionColor.a * alpha);
}
