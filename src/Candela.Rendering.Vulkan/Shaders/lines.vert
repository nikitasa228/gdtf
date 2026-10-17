#version 460
#extension GL_GOOGLE_include_directive : require
// Screen-space-width line segments drawn as quads: six vertices per instance, one instance per segment.
//  default:     SetLines segments; per-instance attributes = two LineVertex (position, sRGB colour + alpha, width px)
//  MESH_EDGES:  wireframe edges of a mesh on devices without fillModeNonSolid; per-instance attributes = the two
//               object-space endpoints, transformed by instances[draw.instanceIndex]; colour/width from push constants.
// The segment is clipped against the near plane in clip space, expanded by half its width plus one pixel for
// anti-aliasing (lines.frag fades the extra pixel), and nudged towards the camera so lines on surfaces stay visible.

#include "scene.glsl"

#ifdef MESH_EDGES
layout(location = 0) in vec3 inP0;
layout(location = 1) in vec3 inP1;
#else
layout(location = 0) in vec3 inP0;
layout(location = 1) in vec4 inC0;
layout(location = 2) in float inW0;
layout(location = 3) in vec3 inP1;
layout(location = 4) in vec4 inC1;
layout(location = 5) in float inW1;
#endif

layout(location = 0) out vec4 outColor;
layout(location = 1) noperspective out float outDistance;
layout(location = 2) flat out float outHalfWidth;

// Relative depth bias (reverse-Z: larger is nearer); 1e-4 of the distance, i.e. 1 mm at 10 m.
const float LINE_DEPTH_BIAS = 1e-4;

void main()
{
    // Corner of the quad: x = segment end (0/1), y = side (-1/+1).
    const vec2 corners[6] = vec2[6](vec2(0, -1), vec2(0, 1), vec2(1, 1), vec2(0, -1), vec2(1, 1), vec2(1, -1));
    vec2 corner = corners[gl_VertexIndex % 6];
    bool atEnd = corner.x > 0.5;

#ifdef MESH_EDGES
    mat4 world = instances[draw.instanceIndex].world;
    vec3 p0 = (world * vec4(inP0, 1.0)).xyz;
    vec3 p1 = (world * vec4(inP1, 1.0)).xyz;
    vec4 color = draw.color;
    float width = max(draw.width, 1.0);
#else
    vec3 p0 = inP0;
    vec3 p1 = inP1;
    vec4 color = atEnd ? inC1 : inC0;
    float width = max(atEnd ? inW1 : inW0, 1.0);
#endif

    vec4 c0 = frame.viewProjection * vec4(p0, 1.0);
    vec4 c1 = frame.viewProjection * vec4(p1, 1.0);
    // Near plane of the reverse-Z projection: depth z/w <= 1, i.e. w - z >= 0.
    float f0 = c0.w - c0.z;
    float f1 = c1.w - c1.z;
    if (f0 < 0.0 && f1 < 0.0)
    {
        gl_Position = vec4(2.0, 2.0, 2.0, 1.0); // entirely behind the near plane: outside the clip volume
        outColor = vec4(0.0);
        outDistance = 0.0;
        outHalfWidth = 0.0;
        return;
    }

    if (f0 < 0.0)
        c0 = mix(c0, c1, f0 / (f0 - f1));
    else if (f1 < 0.0)
        c1 = mix(c1, c0, f1 / (f1 - f0));

    vec2 halfSize = 0.5 * frame.renderSize.xy;
    vec2 s0 = c0.xy / c0.w * halfSize;
    vec2 s1 = c1.xy / c1.w * halfSize;
    vec2 dir = s1 - s0;
    float len = length(dir);
    dir = len > 1e-4 ? dir / len : vec2(1.0, 0.0);
    vec2 normal = vec2(-dir.y, dir.x);
    float halfWidth = 0.5 * width;
    float extent = halfWidth + 1.0;

    vec4 c = atEnd ? c1 : c0;
    vec2 offset = normal * corner.y * extent + dir * (atEnd ? extent : -extent);
    c.xy += offset / halfSize * c.w;
    c.z *= 1.0 + LINE_DEPTH_BIAS;
    gl_Position = c;
    outColor = color;
    outDistance = corner.y * extent;
    outHalfWidth = halfWidth;
}
