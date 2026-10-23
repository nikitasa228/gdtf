#version 460
#extension GL_GOOGLE_include_directive : require
// Display transform at display resolution: HDR (pre-exposed linear Rec.2020, internal resolution; bilinear upscaling
// when ResolutionScale < 1) mixed with the bloom (energy conserving: (1 - k) scene + k bloom) -> tone mapper
// (draw.mode = TONE_*) -> analysis overlays -> sRGB encoding into the RGBA8 display image. TONE_PASSTHROUGH
// (Colorless/Wireframe) only clamps the display-linear values. The HDR alpha is the share of scene light: the rest
// (reference drawings, unlit materials, written after the exposure) is shown as it is, not tone mapped
// (displayTransformShared). A +-0.5 LSB triangular dither from interleaved gradient
// noise hides 8-bit banding in the soft edges of light pools.
//  Lux / noise overlay: surfaces show the overlay gradient colour of their analysis value (alpha blended in linear
//  display space; values outside the range use the out-of-range colour).
//  Noise overlay fog (FEATURE_NOISE_FOG): the air is tinted with the gradient colour of its sound level - a
//  display-referred fog of density overlayRange.w x (position of the level in the gradient range) x gradient alpha
//  per metre, integrated front to back along the view ray up to the surface (or NOISE_FOG_RANGE over the background)
//  with NOISE_FOG_STEPS jittered, quadratically spaced samples. Being display-referred it is not tone mapped, so the
//  fog shows the gradient colours exactly and does not depend on exposure or on the scene's lighting.
//  Smoke overlay (FEATURE_SMOKE_FOG): the scene is desaturated and darkened like the cable overlay, surfaces show the
//  gradient colour of the scattering coefficient of the air next to them (analysis value, 1/m; the default gradient is
//  partly transparent, so the greyed scene shows through) and the air is tinted with the gradient colour of its own
//  scattering coefficient — the same display-referred fog as the noise overlay's, of density overlayRange.w x
//  (position of the value in the gradient range, log scale when the gradient is)^2 x gradient alpha per metre. Haze is
//  uniform, so it tints the whole view as a veil; smoke shows as coloured clouds where the smoke grid holds it.
//  Cable overlay: everything except instances with InstanceFlags.CableHighlight is desaturated and darkened.
// draw.width = 1 / number of bloom levels (normalisation of the summed bloom pyramid).

#include "scene.glsl"
#include "volume.glsl"
#include "tonemap.glsl"

layout(location = 0) in vec2 inUv;
layout(location = 0) out vec4 outColor;

const float CABLE_DIM = 0.32;
const int NOISE_FOG_STEPS = 24;
const float NOISE_FOG_RANGE = 60.0;

// Overlay fog along the ray origin + t direction, t in [0, tMax]: (display colour, transmittance). The value along the
// ray is the sound level (noise overlay) or the air's scattering coefficient (smoke overlay, `smoke` true); values
// below the gradient range leave the air clear, values above it count as its top.
vec4 overlayFog(vec3 origin, vec3 direction, float tMax, float jitter, bool smoke)
{
    float hi = frame.overlayRange.y;
    vec3 colour = vec3(0.0);
    float transmittance = 1.0;
    float previous = 0.0;
    for (int i = 0; i < NOISE_FOG_STEPS; ++i)
    {
        float u = float(i + 1) / float(NOISE_FOG_STEPS);
        float next = tMax * u * u;
        float dt = next - previous;
        vec3 P = origin + direction * (previous + jitter * dt);
        float value = smoke ? smokeOverlayAt(P) : soundLevelAt(P);
        previous = next;
        // The noise fog grows linearly through its range (as it always has); the smoke fog follows the gradient's scale.
        float position = overlayRangePosition(value, smoke);
        if (position < 0.0)
            continue;
        vec4 g = overlayGradient(min(value, hi));
        // The smoke fog weighs the position quadratically: haze, which fills the whole view, stays a veil while dense
        // clouds stand out.
        float density = frame.overlayRange.w * (smoke ? position * position : position) * clamp(g.a, 0.0, 1.0);
        float opacity = 1.0 - exp(-density * dt);
        colour += transmittance * opacity * srgbDecode(g.rgb);
        transmittance *= 1.0 - opacity;
    }

    return vec4(colour, transmittance);
}

void main()
{
    vec2 uv = gl_FragCoord.xy * frame.displaySize.zw;
    vec4 texel = hasFeature(FEATURE_SCALED) ? textureLod(hdrTexture, uv, 0.0) : texelFetch(hdrTexture, ivec2(gl_FragCoord.xy), 0);
    // An overflowed blend (+inf) must not reach the tone mapper as NaN: a scaled image is filtered from checked
    // texels (common.glsl), never by the texture unit, which would turn an infinity into NaN. The alpha (the share of
    // scene light, < 1 on reference drawings) is at most 1 and only needs the non-finite case: scene light.
    vec3 hdr = hasFeature(FEATURE_SCALED) ? sampleLightBilinear(hdrTexture, uv) : finiteLight(texel.rgb);
    texel.a = isNonFinite(texel.a) ? 1.0 : texel.a;
    if (hasFeature(FEATURE_BLOOM))
        hdr = mix(hdr, finiteLight(textureLod(bloomTexture, uv, 0.0).rgb) * draw.width, frame.bloomIntensity);

    vec3 display = displayTransformShared(hdr, draw.mode, texel.a);

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
        else
        {
            if (frame.overlayMode == OVERLAY_SMOKE)
                display = vec3(dot(display, vec3(0.2126, 0.7152, 0.0722)) * CABLE_DIM);
            if (instance != 0u)
            {
                vec4 g = overlayGradient(texelFetch(analysisTexture, p, 0).r);
                display = mix(display, srgbDecode(g.rgb), clamp(g.a, 0.0, 1.0));
            }
        }

        if (hasFeature(FEATURE_NOISE_FOG) || hasFeature(FEATURE_SMOKE_FOG))
        {
            vec2 renderUv = (vec2(p) + 0.5) * frame.renderSize.zw;
            float depth = texelFetch(sceneDepth, p, 0).r;
            vec3 origin, direction;
            viewRay(renderUv, origin, direction);
            float tMax = depth > 0.0 ? distance(origin, worldFromDepth(renderUv, depth)) : NOISE_FOG_RANGE;
            vec4 fog = overlayFog(origin, direction, tMax, interleavedGradientNoise(gl_FragCoord.xy + vec2(13.0, 71.0)),
                hasFeature(FEATURE_SMOKE_FOG));
            display = display * fog.a + fog.rgb;
        }
    }

    vec3 encoded = srgbEncode(display);
    float noise = interleavedGradientNoise(gl_FragCoord.xy) + interleavedGradientNoise(gl_FragCoord.xy + vec2(47.0, 17.0)) - 1.0;
    encoded = clamp(encoded + noise / 255.0, 0.0, 1.0);
    outColor = vec4(encoded, 1.0);
}
