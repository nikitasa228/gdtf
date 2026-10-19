#version 460
#extension GL_GOOGLE_include_directive : require
// Volumetric composite over the opaque scene (internal resolution, blending dst = src.rgb + src.a x dst):
//  - the integrated froxel volume at the pixel's depth (background: the far end of the volume) gives the in-scattered
//    luminance of the lights that are not beams, the flame emission, and the transmittance that dims the surface;
//  - the per-pixel single scattering of the beams (beam_scatter.comp / beam_temporal.comp, half resolution) is
//    upsampled with joint bilateral weights: the four nearest half-resolution samples, bilinear weights times a
//    view-depth similarity, so beams stop exactly at the silhouettes of the geometry in front of them. The depth
//    tolerance grows with the surface's own depth slope (one-sided differences, so silhouettes do not widen it):
//    a floor seen at a grazing angle changes depth by several percent per pixel and must not fall apart into the
//    2 x 2 blocks of the half-resolution samples. A sample whose beams all ended in front of its surface (negative
//    validity depth, see beam_scatter.comp) is exact for every surface behind that point.
// Output is pre-exposed (cd/m2 x exposure).

#include "volume.glsl"

layout(location = 0) in vec2 inUv;
layout(location = 0) out vec4 outColor;

// rgb = resolved beam light (pre-exposed), a = validity depth of the half-resolution sample (beam_scatter.comp).
layout(set = SET_PASS, binding = BINDING_PASS_SAMPLED0) uniform sampler2D beamTexture;

float viewDepthAt(ivec2 p)
{
    p = clamp(p, ivec2(0), ivec2(frame.renderSize.xy) - 1);
    float depth = texelFetch(sceneDepth, p, 0).r;
    return depth > 0.0 ? viewDepthOf(worldFromDepth((vec2(p) + 0.5) * frame.renderSize.zw, depth)) : BEAM_BACKGROUND_DEPTH;
}

// Smaller of the two one-sided view-depth differences along one axis: the slope of the surface, not of an edge.
float depthSlope(ivec2 p, ivec2 axis, float viewDepth)
{
    return min(abs(viewDepthAt(p + axis) - viewDepth), abs(viewDepth - viewDepthAt(p - axis)));
}

vec3 beamLight(float viewDepth)
{
    // A half-resolution sample lies up to 1.5 pixels away and represents the nearest or farthest depth of its
    // 2 x 2 block: on a sloped surface it differs from this pixel's depth by up to about 2.5 slopes per axis.
    ivec2 pixel = ivec2(gl_FragCoord.xy);
    float slope = depthSlope(pixel, ivec2(1, 0), viewDepth) + depthSlope(pixel, ivec2(0, 1), viewDepth);
    float tolerance = max(0.02 * viewDepth + 2.5 * slope, 1e-3);
    vec2 size = vec2(textureSize(beamTexture, 0));
    vec2 pos = gl_FragCoord.xy * frame.renderSize.zw * size - 0.5;
    vec2 base = floor(pos);
    vec2 f = pos - base;
    vec3 sum = vec3(0.0);
    float weightSum = 0.0;
    float bestDifference = 1e30;
    vec3 best = vec3(0.0);
    for (int j = 0; j < 2; ++j)
    {
        for (int i = 0; i < 2; ++i)
        {
            ivec2 p = ivec2(clamp(base + vec2(i, j), vec2(0.0), size - 1.0));
            vec4 s = texelFetch(beamTexture, p, 0);
            float difference = s.a < 0.0 ? max(-s.a - viewDepth, 0.0) : abs(s.a - viewDepth);
            if (difference < bestDifference)
            {
                bestDifference = difference;
                best = s.rgb;
            }

            float w = (i == 0 ? 1.0 - f.x : f.x) * (j == 0 ? 1.0 - f.y : f.y);
            w *= exp(-difference / tolerance);
            sum += s.rgb * w;
            weightSum += w;
        }
    }

    // No sample on this surface (thin geometry between them): the one with the closest depth.
    return weightSum > 1e-3 ? sum / weightSum : best;
}

void main()
{
    vec2 uv = gl_FragCoord.xy * frame.renderSize.zw;
    float depth = texelFetch(sceneDepth, ivec2(gl_FragCoord.xy), 0).r;
    float viewDepth = depth > 0.0 ? viewDepthOf(worldFromDepth(uv, depth)) : frame.volumeDepth.y;
    vec4 fog = sampleIntegratedVolume(uv, viewDepth);
    // Beams over the background reach their own range (the froxel volume ends at its far depth).
    vec3 beams = frame.beamCount > 0u ? beamLight(depth > 0.0 ? viewDepth : BEAM_BACKGROUND_DEPTH) : vec3(0.0);
    outColor = vec4(finiteLight(fog.rgb + beams), fog.a);
}
