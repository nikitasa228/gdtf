#version 460
#extension GL_GOOGLE_include_directive : require
// Analysis overlays (EQUAL depth test against the pre-pass, every visible opaque surface): one value per pixel into
// the R32F analysis image, read back by tests and mapped through the overlay gradient by the display transform.
//  OVERLAY_LUX:   illuminance E (lx, photopic) = sum over lights of I0 x profile x cookie x shadow x max(cos, 0) / d^2
//                 (no albedo) - the luminance of the summed Rec.2020 illuminance, since light colours have luminance 1.
//  OVERLAY_NOISE: free-field sound pressure level (dB) of the noise sources (or the noise field) at the surface.

#include "lighting.glsl"
#include "volume.glsl"

layout(location = 0) in vec3 inWorldPosition;
layout(location = 1) in vec3 inNormal;
layout(location = 2) in vec4 inTangent;
layout(location = 3) in vec2 inUv;
layout(location = 4) flat in uint inInstance;
layout(location = 5) flat in uint inMaterial;

layout(location = 0) out float outValue;

void main()
{
    vec3 P = inWorldPosition;
    if (frame.overlayMode == OVERLAY_NOISE)
    {
        outValue = soundLevelAt(P);
        return;
    }

    vec3 N = normalize(inNormal);
    if (!gl_FrontFacing)
        N = -N;
    float viewDepth = viewDepthOf(P);
    float pixelSize = pixelWorldSize(viewDepth);
    float noise = interleavedGradientNoise(gl_FragCoord.xy);
    float lux = 0.0;
    uvec2 cluster = frame.lightCount > 0u ? clusters[clusterIndex(gl_FragCoord.xy, viewDepth)] : uvec2(0u);
    for (uint i = 0u; i < cluster.y; ++i)
    {
        vec3 L;
        lux += luminanceRec2020(surfaceIlluminance(clusterLights[cluster.x + i], P, N, pixelSize, noise, L));
    }

    outValue = lux;
}
