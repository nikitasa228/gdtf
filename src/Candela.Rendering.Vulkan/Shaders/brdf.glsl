// Microfacet BRDF (units: 1/sr). Luminance = BRDF x illuminance.
//   f = (1 - F) (1 - metallic) albedo / pi                         Lambert diffuse, weighted by the energy not reflected
//     + D_GGX(h) V_SmithGGXCorrelated(v, l) F_Schlick(v.h)         specular (Trowbridge-Reitz, height-correlated Smith)
// alpha = roughness^2 (perceptual roughness), F0 = mix(dielectric f0 from the IOR, albedo, metallic), F90 = sat(50 F0)
// so a material with IOR 1 (f0 = 0) has no specular at all and is exactly Lambertian (albedo / pi). The (1 - F)
// weight keeps diffuse + specular <= 1 for every direction (single scattering; the multiple-scattering energy of
// very rough metals is not compensated).
#ifndef CANDELA_BRDF_GLSL
#define CANDELA_BRDF_GLSL

#include "common.glsl"

struct SurfaceBrdf
{
    vec3 albedo;     // linear Rec.2020
    float metallic;
    float roughness; // perceptual
    float f0;        // dielectric reflectance at normal incidence
};

float distributionGgx(float NdotH, float alpha)
{
    float a2 = alpha * alpha;
    float f = (NdotH * a2 - NdotH) * NdotH + 1.0;
    return a2 / (PI * f * f);
}

float visibilitySmithGgxCorrelated(float NdotV, float NdotL, float alpha)
{
    float a2 = alpha * alpha;
    float gv = NdotL * sqrt(NdotV * NdotV * (1.0 - a2) + a2);
    float gl = NdotV * sqrt(NdotL * NdotL * (1.0 - a2) + a2);
    return 0.5 / max(gv + gl, 1e-7);
}

vec3 fresnelSchlick(vec3 f0, float f90, float VdotH)
{
    float f = pow(1.0 - VdotH, 5.0);
    return f0 + (vec3(f90) - f0) * f;
}

// Diffuse and specular parts of the BRDF separately (premultiplied blending needs them apart).
void evaluateBrdf(SurfaceBrdf s, vec3 N, vec3 V, vec3 L, out vec3 diffuse, out vec3 specular)
{
    float NdotL = clamp(dot(N, L), 0.0, 1.0);
    float NdotV = clamp(abs(dot(N, V)), 1e-4, 1.0);
    vec3 H = normalize(V + L);
    float NdotH = clamp(dot(N, H), 0.0, 1.0);
    float VdotH = clamp(dot(V, H), 0.0, 1.0);

    vec3 f0 = mix(vec3(s.f0), s.albedo, s.metallic);
    float f90 = clamp(50.0 * dot(f0, vec3(0.3333)), 0.0, 1.0);
    vec3 F = fresnelSchlick(f0, f90, VdotH);
    // alpha >= 0.002 keeps the highlight of a point light finite on mirror-like surfaces.
    float alpha = max(s.roughness * s.roughness, 0.002);
    specular = distributionGgx(NdotH, alpha) * visibilitySmithGgxCorrelated(NdotV, NdotL, alpha) * F;
    diffuse = (vec3(1.0) - F) * (1.0 - s.metallic) * s.albedo * INV_PI;
}

// Directional albedo of the BRDF under light of equal luminance from every direction above the surface (a uniform
// environment): reflected luminance = these factors x the environment's luminance. Specular: Karis' analytic fit of the
// split-sum environment BRDF for GGX ("Physically Based Shading on Mobile", 2014) with the BRDF's F0 and F90; diffuse:
// Lambert weighted by the energy the specular part leaves. With F0 = 0 (IOR 1) the specular part vanishes and a white
// Lambertian surface reflects exactly the environment's luminance (the furnace test).
void ambientReflectance(SurfaceBrdf s, vec3 N, vec3 V, out vec3 diffuse, out vec3 specular)
{
    float NdotV = clamp(abs(dot(N, V)), 1e-4, 1.0);
    vec3 f0 = mix(vec3(s.f0), s.albedo, s.metallic);
    float f90 = clamp(50.0 * dot(f0, vec3(0.3333)), 0.0, 1.0);
    const vec4 c0 = vec4(-1.0, -0.0275, -0.572, 0.022);
    const vec4 c1 = vec4(1.0, 0.0425, 1.04, -0.04);
    vec4 r = s.roughness * c0 + c1;
    float a004 = min(r.x * r.x, exp2(-9.28 * NdotV)) * r.x + r.y;
    vec2 ab = vec2(-1.04, 1.04) * a004 + r.zw;
    specular = clamp(f0 * ab.x + vec3(f90 * ab.y), 0.0, 1.0);
    diffuse = (vec3(1.0) - specular) * (1.0 - s.metallic) * s.albedo;
}

#endif
