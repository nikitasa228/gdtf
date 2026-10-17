// Display transforms: exposed scene-referred linear Rec.2020 -> display-referred linear Rec.709 in [0, 1].
#ifndef CANDELA_TONEMAP_GLSL
#define CANDELA_TONEMAP_GLSL

#include "color.glsl"

// AgX (Troy Sobotka), as in Blender 4 / Filament / three.js: the Rec.2020 input is pulled towards the achromatic
// axis by the inset matrix, log2-encoded over [-12.47, +4.03] stops around middle grey 0.18 (so 0.18 maps to the
// middle of the curve), shaped by a 6th-order polynomial fit of the AgX sigmoid, pushed back out by the outset
// matrix, linearised with gamma 2.2 and converted to Rec.709. Saturated stage colours desaturate towards white
// instead of skewing hue when they get very bright.
vec3 agxContrast(vec3 x)
{
    vec3 x2 = x * x;
    vec3 x4 = x2 * x2;
    return 15.5 * x4 * x2 - 40.14 * x4 * x + 31.96 * x4 - 6.868 * x2 * x + 0.4298 * x2 + 0.1191 * x - 0.00232;
}

vec3 tonemapAgx(vec3 rec2020)
{
    const mat3 inset = mat3(
        vec3(0.856627153315983, 0.137318972929847, 0.11189821299995),
        vec3(0.0951212405381588, 0.761241990602591, 0.0767994186031903),
        vec3(0.0482516061458583, 0.101439036467562, 0.811302368396859));
    const mat3 outset = mat3(
        vec3(1.1271005818144368, -0.1413297634984383, -0.14132976349843826),
        vec3(-0.11060664309660323, 1.157823702216272, -0.11060664309660294),
        vec3(-0.016493938717834573, -0.016493938717834257, 1.2519364065950405));
    const float minEv = -12.47393; // log2(2^-10 * 0.18)
    const float maxEv = 4.026069;  // log2(2^6.5 * 0.18)
    vec3 c = inset * max(rec2020, vec3(0.0));
    c = clamp((log2(max(c, vec3(1e-10))) - minEv) / (maxEv - minEv), 0.0, 1.0);
    c = agxContrast(c);
    c = outset * c;
    c = pow(max(c, vec3(0.0)), vec3(2.2));
    return clamp(rec2020ToRec709(c), 0.0, 1.0);
}

// ACES filmic (Stephen Hill's fit of the RRT + sRGB ODT): sRGB -> AP1 (with the RRT saturation), rational
// curve (a(a + 0.0245786) - 0.000090537) / (a(0.983729a + 0.4329510) + 0.238081), AP1 -> sRGB.
// Colours outside Rec.709 are clipped to it first.
vec3 tonemapAces(vec3 rec2020)
{
    const mat3 inputMatrix = mat3(
        vec3(0.59719, 0.07600, 0.02840),
        vec3(0.35458, 0.90834, 0.13383),
        vec3(0.04823, 0.01566, 0.83777));
    const mat3 outputMatrix = mat3(
        vec3(1.60475, -0.10208, -0.00327),
        vec3(-0.53108, 1.10813, -0.07276),
        vec3(-0.07367, -0.00605, 1.07602));
    vec3 c = inputMatrix * max(rec2020ToRec709(rec2020), vec3(0.0));
    vec3 a = c * (c + 0.0245786) - 0.000090537;
    vec3 b = c * (0.983729 * c + 0.4329510) + 0.238081;
    c = outputMatrix * (a / b);
    return clamp(c, 0.0, 1.0);
}

// Khronos PBR Neutral (2024): identity below 0.76 (after a small toe offset), then a smooth highlight
// compression newPeak = 1 - d^2 / (peak + d - start) with d = 0.24, desaturating by 0.15 as values compress.
// Keeps base colours exactly (product shots); colours outside Rec.709 are clipped to it first.
vec3 tonemapNeutral(vec3 rec2020)
{
    vec3 color = max(rec2020ToRec709(rec2020), vec3(0.0));
    const float startCompression = 0.8 - 0.04;
    const float desaturation = 0.15;
    float x = min(color.r, min(color.g, color.b));
    float offset = x < 0.08 ? x - 6.25 * x * x : 0.04;
    color -= offset;
    float peak = max(color.r, max(color.g, color.b));
    if (peak < startCompression)
        return clamp(color, 0.0, 1.0);
    const float d = 1.0 - startCompression;
    float newPeak = 1.0 - d * d / (peak + d - startCompression);
    color *= newPeak / peak;
    float g = 1.0 - 1.0 / (desaturation * (peak - newPeak) + 1.0);
    return clamp(mix(color, vec3(newPeak), g), 0.0, 1.0);
}

#endif
