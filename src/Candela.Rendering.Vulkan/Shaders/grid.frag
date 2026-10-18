#version 460
#extension GL_GOOGLE_include_directive : require
// Infinite ground grid on the plane Z = 0, drawn as a full-screen pass over the display image. Each pixel's view ray
// is intersected with the plane analytically; the hit point's depth is written to gl_FragDepth (with a tiny bias
// towards the camera so the grid stays visible on a floor at Z = 0) and depth-tested against the scene. Lines are
// anti-aliased in screen space from the derivatives of the plane coordinates, fade out where cells get smaller than a
// few pixels (no moire towards the horizon) and with distance; a major line every frame.gridMajorEvery cells; the
// X axis (Y = 0) is red, the Y axis (X = 0) green. Colours are sRGB-encoded with alpha (blended over the display).

#include "common.glsl"

layout(location = 0) in vec2 inUv;
layout(location = 0) out vec4 outColor;

const float GRID_DEPTH_BIAS = 1e-4;

// Coverage of grid lines of the given pixel width at integer values of coord.
float gridCoverage(vec2 coord, float widthPx)
{
    vec2 deriv = max(fwidth(coord), vec2(1e-7));
    vec2 distPx = abs(fract(coord - 0.5) - 0.5) / deriv;
    float d = min(distPx.x, distPx.y);
    float coverage = clamp(0.5 * widthPx + 0.5 - d, 0.0, 1.0);
    float cellsPerPixel = max(deriv.x, deriv.y);
    return coverage * (1.0 - smoothstep(0.12, 0.35, cellsPerPixel));
}

// Coverage of the line where coordinate c is 0.
float axisCoverage(float c, float widthPx)
{
    float d = abs(c) / max(fwidth(c), 1e-7);
    return clamp(0.5 * widthPx + 0.5 - d, 0.0, 1.0);
}

vec4 over(vec4 dst, vec4 src)
{
    float a = src.a + dst.a * (1.0 - src.a);
    vec3 rgb = a > 0.0 ? (src.rgb * src.a + dst.rgb * dst.a * (1.0 - src.a)) / a : vec3(0.0);
    return vec4(rgb, a);
}

void main()
{
    vec2 ndc = gl_FragCoord.xy * frame.displaySize.zw * 2.0 - 1.0;
    vec4 a = frame.inverseViewProjection * vec4(ndc, 1.0, 1.0); // on the near plane (reverse-Z depth 1)
    vec4 b = frame.inverseViewProjection * vec4(ndc, 0.5, 1.0);
    vec3 origin = a.xyz / a.w;
    vec3 dir = b.xyz / b.w - origin;
    float t = abs(dir.z) > 1e-12 ? -origin.z / dir.z : -1.0;
    vec3 P = origin + dir * max(t, 0.0);

    vec2 coord = P.xy / frame.gridSpacing;
    float minor = gridCoverage(coord, 1.0);
    float major = gridCoverage(coord / frame.gridMajorEvery, 1.5);
    float axisX = axisCoverage(P.y, 2.0); // the X axis is the line Y = 0
    float axisY = axisCoverage(P.x, 2.0);

    vec4 color = vec4(frame.gridColorMinor.rgb, frame.gridColorMinor.a * minor);
    color = over(color, vec4(frame.gridColorMajor.rgb, frame.gridColorMajor.a * major));
    color = over(color, vec4(frame.gridAxisY.rgb, frame.gridAxisY.a * axisY));
    color = over(color, vec4(frame.gridAxisX.rgb, frame.gridAxisX.a * axisX));

    if (frame.gridFadeDistance > 0.0)
    {
        float distance = length(P - frame.cameraPosition.xyz);
        color.a *= 1.0 - smoothstep(0.35 * frame.gridFadeDistance, frame.gridFadeDistance, distance);
    }

    if (t <= 0.0 || color.a <= 0.002)
        discard; // after all derivatives were taken
    vec4 clip = frame.viewProjection * vec4(P, 1.0);
    gl_FragDepth = clamp(clip.z / clip.w * (1.0 + GRID_DEPTH_BIAS), 0.0, 1.0);
    outColor = color;
}
