// Spot-light evaluation shared by surface shading, the lux overlay, the froxel volume, the narrow-beam ray marcher
// and indirect light: beam profile per prism facet, the light's cookie (gobo, framing blades; the iris is part of
// the profile) and shadows from the shadow atlas (PCSS soft shadows for surfaces, a bilinear PCF tap for volumes).
// Units: intensity cd, illuminance lx, distances m.
#ifndef CANDELA_LIGHTING_GLSL
#define CANDELA_LIGHTING_GLSL

#include "scene.glsl"
#include "view.glsl"
#include "photometry.glsl"
#include "color.glsl"

// Receiver-side shadow bias in shadow texels: along the surface normal (scaled by the slope) and towards the light.
const float SHADOW_NORMAL_OFFSET = 1.5;
const float SHADOW_LIGHT_OFFSET = 0.75;
const int SHADOW_BLOCKER_SAMPLES = 12;
const int SHADOW_PCF_SAMPLES = 16;
const float SHADOW_MAX_FILTER_TEXELS = 24.0;

// ------------------------------------------------------------------------------------------------- cookie

// Framing blades: product of four soft half-plane masks in gate coordinates (1 = open).
float bladeTransmission(Light light, vec2 p, float softness)
{
    float t = 1.0;
    for (int i = 0; i < 4; ++i)
    {
        vec4 b = light.blades[i];
        if (b.w <= 0.0)
            continue;
        t *= 1.0 - smoothstep(-softness, softness, dot(p, b.xy) - b.z);
    }

    return t;
}

// Gobo transmission (Rec.2020 per channel) at gate coordinates p. The gobo image fills the gate square whose
// inscribed circle is the field angle; outside it the gate blocks the light. footprint = filter radius in gate units
// (pixel footprint), light.goboBlur adds the defocus; both select the mip level (focus blur = LOD bias).
vec3 goboTransmission(Light light, vec2 p, float footprint)
{
    vec2 r = vec2(light.goboRotation.x * p.x - light.goboRotation.y * p.y, light.goboRotation.y * p.x + light.goboRotation.x * p.y);
    vec2 uv = vec2(0.5 + 0.5 * r.x, 0.5 - 0.5 * r.y);
    vec2 outside = abs(uv - 0.5) - 0.5;
    float edge = max(outside.x, outside.y);
    float w = max(0.5 * footprint + light.goboBlur, 1e-4);
    float inside = 1.0 - smoothstep(-w, w, edge);
    if (inside <= 0.0)
        return vec3(0.0);
    vec2 size = vec2(textureSize(textures[nonuniformEXT(light.goboTexture)], 0));
    float radius = length(vec2(0.5 * footprint, light.goboBlur)); // uv units (the gate is 2 gate units = 1 uv wide)
    float lod = log2(max(radius * max(size.x, size.y), 1.0));
    vec4 t = textureLod(textures[nonuniformEXT(light.goboTexture)], clamp(uv, vec2(0.0), vec2(1.0)), lod);
    return rec709ToRec2020(t.rgb) * (t.a * inside);
}

// Relative intensity of the whole fixture (sum over prism facets of profile x cookie) towards the unit direction
// `dir` leaving the light; multiply by light.intensity for candela. footprint: filter radius in gate units.
vec3 lightCookieProfile(Light light, vec3 dir, float footprint)
{
    vec3 v = vec3(dot(dir, light.right), dot(dir, light.up), dot(dir, light.direction));
    bool prism = (light.flags & LIGHT_PRISM) != 0u;
    uint facets = prism ? light.prismFacets : 1u;
    float softness = light.edgeSoftness + footprint;
    vec3 sum = vec3(0.0);
    for (uint k = 0u; k < facets; ++k)
    {
        vec3 vk = v;
        if (prism)
        {
            // Into the frame of facet k: its axis is tilted by the spread towards azimuth phi (right towards up).
            float phi = light.prismRotation + 2.0 * PI * float(k) / float(facets);
            vec3 u = vec3(-sin(phi), cos(phi), 0.0);
            float c = light.prismCosSpread;
            float s = light.prismSinSpread;
            vk = v * c - cross(u, v) * s + u * (dot(u, v) * (1.0 - c));
        }

        float theta = atan(length(vk.xy), vk.z);
        float g = beamProfile(light, theta);
        if (g <= 0.0 || vk.z <= 1e-6)
            continue;
        vec2 p = vk.xy * (light.gateScale / vk.z);
        if ((light.flags & LIGHT_BLADES) != 0u)
            g *= bladeTransmission(light, p, softness);
        if (g <= 0.0)
            continue;
        vec3 c3 = vec3(g);
        if ((light.flags & LIGHT_GOBO) != 0u)
            c3 *= goboTransmission(light, p, footprint);
        sum += c3;
    }

    return sum;
}

// Transmittance of the uniform haze along a light path of length d (sigma_t = sigma_s; the scattered light leaves
// the path). Smoke is not included (it would need a march through the density volume per light). Shaders compiled
// with CLEAR_AIR (the lux overlay: a light meter's reading) ignore the atmosphere.
float lightPathTransmittance(float d)
{
#ifdef CLEAR_AIR
    return 1.0;
#else
    return hasFeature(FEATURE_HAZE) ? exp(-frame.media.x * d) : 1.0;
#endif
}

// Luminous intensity (cd per Rec.2020 channel) of a light towards P, cookie and prism included, no shadow, times
// the haze transmittance between the light and P. L = unit direction from P to the light, d2 = squared distance
// (clamped to 1 cm). pixelSize = world size of the receiving pixel/froxel (filters gobos and blade edges).
vec3 lightIntensityToward(Light light, vec3 P, float pixelSize, out vec3 L, out float d2)
{
    vec3 toLight = light.position - P;
    d2 = max(dot(toLight, toLight), MIN_LIGHT_DISTANCE * MIN_LIGHT_DISTANCE);
    float invD = inversesqrt(d2);
    L = toLight * invD;
    if (dot(-L, light.direction) < light.cosCutoff)
        return vec3(0.0);
    float footprint = pixelSize * invD * light.gateScale;
    return light.intensity * (lightCookieProfile(light, -L, footprint) * rangeWindow(d2, lightRangeSquared(light)) *
                              lightPathTransmittance(d2 * invD));
}

// ------------------------------------------------------------------------------------------------- shadows

// Shadow-map coordinates of P for a tile: atlas uv, reference depth (reverse-Z) and the depth along the light axis.
// The receiver is offset along its normal (more at grazing angles) and towards the light by a fraction of the
// texel size at its depth, on top of the slope-scaled depth bias used when the tile was rendered. False when P is
// outside the tile's frustum (the light is unshadowed there).
bool shadowCoordinates(ShadowSlot s, vec3 P, vec3 N, out vec3 coord, out float z)
{
    vec3 toLight = s.position - P;
    z = -dot(toLight, s.axis);
    if (z <= s.near)
        return false;
    float texel = s.texelScale * z;
    vec3 L = normalize(toLight);
    float cosT = clamp(dot(N, L), 0.0, 1.0);
    float sinT = sqrt(1.0 - cosT * cosT);
    vec3 biased = P + N * (texel * SHADOW_NORMAL_OFFSET * max(sinT, 0.25)) + L * (texel * SHADOW_LIGHT_OFFSET);
    vec4 clip = s.viewProjection * vec4(biased, 1.0);
    if (clip.w <= 0.0)
        return false;
    vec3 ndc = clip.xyz / clip.w;
    vec2 uv = ndc.xy * 0.5 + 0.5;
    if (any(lessThan(uv, vec2(0.0))) || any(greaterThan(uv, vec2(1.0))))
        return false;
    coord = vec3(s.atlasRect.xy + uv * s.atlasRect.zw, ndc.z);
    return true;
}

vec2 clampToTile(ShadowSlot s, vec2 uv)
{
    vec2 lo = s.atlasRect.xy + vec2(0.5 * s.atlasTexel);
    vec2 hi = s.atlasRect.xy + s.atlasRect.zw - vec2(0.5 * s.atlasTexel);
    return clamp(uv, lo, hi);
}

// Vogel disk point i of n (unit disk), rotated by phi.
vec2 vogelDisk(int i, int n, float phi)
{
    const float goldenAngle = 2.39996323;
    float r = sqrt((float(i) + 0.5) / float(n));
    float a = float(i) * goldenAngle + phi;
    return r * vec2(cos(a), sin(a));
}

// Hard shadow (one bilinear PCF tap): volumes, ray marching, indirect light. 1 = lit.
float shadowHard(uint slot, vec3 P, vec3 N)
{
    if (slot == NO_SHADOW || !hasFeature(FEATURE_SHADOWS))
        return 1.0;
    ShadowSlot s = shadowSlots[slot];
    vec3 coord;
    float z;
    if (!shadowCoordinates(s, P, N, coord, z))
        return 1.0;
    return texture(shadowMap, vec3(clampToTile(s, coord.xy), coord.z));
}

// Soft shadow (PCSS): blocker search over the region from which the lens disk (radius sourceRadius) can be hidden,
// penumbra radius w = R (z - z_blocker) / z_blocker on the receiver, then a PCF disk of that radius with hardware
// bilinear comparison. noise rotates the sample pattern per pixel. 1 = lit.
float shadowSoft(uint slot, vec3 P, vec3 N, float sourceRadius, float noise)
{
    if (slot == NO_SHADOW || !hasFeature(FEATURE_SHADOWS))
        return 1.0;
    ShadowSlot s = shadowSlots[slot];
    vec3 coord;
    float z;
    if (!shadowCoordinates(s, P, N, coord, z))
        return 1.0;

    float uvPerMetre = s.atlasRect.z / (2.0 * s.tanHalfAngle * z); // atlas uv per metre on the receiver plane
    float texel = s.atlasTexel;
    float phi = noise * 2.0 * PI;
    float searchRadius = clamp(sourceRadius * uvPerMetre, 1.5 * texel, SHADOW_MAX_FILTER_TEXELS * texel);
    float blockerSum = 0.0;
    int blockers = 0;
    for (int i = 0; i < SHADOW_BLOCKER_SAMPLES; ++i)
    {
        vec2 uv = clampToTile(s, coord.xy + vogelDisk(i, SHADOW_BLOCKER_SAMPLES, phi) * searchRadius);
        float d = textureLod(shadowDepth, uv, 0.0).r;
        if (d > coord.z)
        {
            blockerSum += s.near / max(d, 1e-12); // linear depth of the blocker
            ++blockers;
        }
    }

    if (blockers == 0)
        return 1.0;
    float zBlocker = blockerSum / float(blockers);
    float penumbra = sourceRadius * max(z - zBlocker, 0.0) / max(zBlocker, 1e-4) * uvPerMetre;
    float radius = clamp(penumbra, 0.75 * texel, SHADOW_MAX_FILTER_TEXELS * texel);
    float lit = 0.0;
    for (int i = 0; i < SHADOW_PCF_SAMPLES; ++i)
    {
        vec2 uv = clampToTile(s, coord.xy + vogelDisk(i, SHADOW_PCF_SAMPLES, phi) * radius);
        lit += texture(shadowMap, vec3(uv, coord.z));
    }

    return lit / float(SHADOW_PCF_SAMPLES);
}

// Illuminance (lx per Rec.2020 channel) of light `index` on a surface at P with normal N, shadow included.
// L = unit direction to the light.
vec3 surfaceIlluminance(uint index, vec3 P, vec3 N, float pixelSize, float noise, out vec3 L)
{
    Light light = lights[index];
    float d2;
    vec3 I = lightIntensityToward(light, P, pixelSize, L, d2);
    float cosIncidence = dot(N, L);
    if (cosIncidence <= 0.0 || I.r + I.g + I.b <= 0.0)
        return vec3(0.0);
    float shadow = shadowSoft(lightViews[index].shadowSlot, P, N, light.sourceRadius, noise);
    return I * (cosIncidence * shadow / d2);
}

#endif
