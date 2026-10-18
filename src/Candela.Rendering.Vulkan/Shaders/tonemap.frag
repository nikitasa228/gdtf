#version 460
#extension GL_GOOGLE_include_directive : require
// Display transform: HDR (pre-exposed linear Rec.2020) -> tone mapper (draw.mode = TONE_*) -> sRGB encoding into the
// RGBA8 display image. TONE_PASSTHROUGH (Colorless/Wireframe) only clamps the display-linear values. A +-0.5 LSB
// triangular dither from interleaved gradient noise hides 8-bit banding in the soft edges of light pools.

#include "view.glsl"
#include "tonemap.glsl"

layout(location = 0) in vec2 inUv;
layout(location = 0) out vec4 outColor;

float interleavedGradientNoise(vec2 p)
{
    return fract(52.9829189 * fract(dot(p, vec2(0.06711056, 0.00583715))));
}

void main()
{
    vec3 hdr = texelFetch(hdrTexture, ivec2(gl_FragCoord.xy), 0).rgb;
    vec3 display;
    if (draw.mode == TONE_AGX)
        display = tonemapAgx(hdr);
    else if (draw.mode == TONE_ACES)
        display = tonemapAces(hdr);
    else if (draw.mode == TONE_NEUTRAL)
        display = tonemapNeutral(hdr);
    else
        display = clamp(hdr, 0.0, 1.0);

    vec3 encoded = srgbEncode(display);
    float noise = interleavedGradientNoise(gl_FragCoord.xy) + interleavedGradientNoise(gl_FragCoord.xy + vec2(47.0, 17.0)) - 1.0;
    encoded = clamp(encoded + noise / 255.0, 0.0, 1.0);
    outColor = vec4(encoded, 1.0);
}
