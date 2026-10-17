// Colour helpers: sRGB transfer functions (IEC 61966-2-1) and Rec.709 <-> Rec.2020 (matrices from Candela.Photometry).
#ifndef CANDELA_COLOR_GLSL
#define CANDELA_COLOR_GLSL

#include "generated.glsl"

vec3 srgbEncode(vec3 linear)
{
    vec3 lo = linear * 12.92;
    vec3 hi = 1.055 * pow(max(linear, vec3(0.0)), vec3(1.0 / 2.4)) - 0.055;
    return mix(hi, lo, lessThanEqual(linear, vec3(0.0031308)));
}

vec3 srgbDecode(vec3 encoded)
{
    vec3 lo = encoded / 12.92;
    vec3 hi = pow((max(encoded, vec3(0.0)) + 0.055) / 1.055, vec3(2.4));
    return mix(hi, lo, lessThanEqual(encoded, vec3(0.04045)));
}

vec3 rec709ToRec2020(vec3 c)
{
    return REC709_TO_REC2020 * c;
}

vec3 rec2020ToRec709(vec3 c)
{
    return REC2020_TO_REC709 * c;
}

float luminanceRec2020(vec3 c)
{
    return dot(REC2020_LUMINANCE, c);
}

#endif
