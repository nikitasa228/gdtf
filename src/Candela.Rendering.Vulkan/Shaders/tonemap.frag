#version 460
#extension GL_GOOGLE_include_directive : require
// Display transform at display resolution: HDR (pre-exposed linear Rec.2020, internal resolution; bilinear upscaling
// when ResolutionScale < 1) mixed with the bloom (energy conserving: (1 - k) scene + k bloom) -> tone mapper
// (draw.mode = TONE_*) -> analysis overlays -> sRGB encoding into the RGBA8 display image. TONE_PASSTHROUGH
// (Colorless/Wireframe) only clamps the display-linear values. A +-0.5 LSB triangular dither from interleaved gradient
// noise hides 8-bit banding in the soft edges of light pools.
//  Lux / noise overlay: surfaces show the overlay gradient colour of their analysis value (alpha blended in linear
//  display space; values outside the range use the out-of-range colour).
//  Cable overlay: everything except instances with InstanceFlags.CableHighlight is desaturated and darkened.
// draw.width = 1 / number of bloom levels (normalisation of the summed bloom pyramid).

#include "scene.glsl"
#include "volume.glsl"
#include "tonemap.glsl"

layout(location = 0) in vec2 inUv;
layout(location = 0) out vec4 outColor;

const float CABLE_DIM = 0.32;

void main()
{
    vec2 uv = gl_FragCoord.xy * frame.displaySize.zw;
    vec3 hdr = hasFeature(FEATURE_SCALED) ? textureLod(hdrTexture, uv, 0.0).rgb : texelFetch(hdrTexture, ivec2(gl_FragCoord.xy), 0).rgb;
    if (hasFeature(FEATURE_BLOOM))
        hdr = mix(hdr, textureLod(bloomTexture, uv, 0.0).rgb * draw.width, frame.bloomIntensity);

    vec3 display;
    if (draw.mode == TONE_AGX)
        display = tonemapAgx(hdr);
    else if (draw.mode == TONE_ACES)
        display = tonemapAces(hdr);
    else if (draw.mode == TONE_NEUTRAL)
        display = tonemapNeutral(hdr);
    else
        display = clamp(hdr, 0.0, 1.0);

    if (frame.overlayMode != OVERLAY_NONE)
    {
        ivec2 p = ivec2(uv * frame.renderSize.xy);
        uint instance = texelFetch(instanceIds, p, 0).r;
        if (frame.overlayMode == OVERLAY_CABLES)
        {
            bool highlighted = instance != 0u && (instances[instance - 1u].flags & INSTANCE_FLAG_CABLE_HIGHLIGHT) != 0u;
            if (!highlighted)
                display = vec3(dot(display, vec3(0.2126, 0.7152, 0.0722)) * CABLE_DIM);
        }
        else if (instance != 0u)
        {
            vec4 g = overlayGradient(texelFetch(analysisTexture, p, 0).r);
            display = mix(display, srgbDecode(g.rgb), clamp(g.a, 0.0, 1.0));
        }
    }

    vec3 encoded = srgbEncode(display);
    float noise = interleavedGradientNoise(gl_FragCoord.xy) + interleavedGradientNoise(gl_FragCoord.xy + vec2(47.0, 17.0)) - 1.0;
    encoded = clamp(encoded + noise / 255.0, 0.0, 1.0);
    outColor = vec4(encoded, 1.0);
}
