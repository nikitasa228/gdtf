// Microfacet BRDF (units: 1/sr). Luminance = BRDF x illuminance.
//   f = (1 - F) (1 - metallic) albedo / pi                         Lambert diffuse, weighted by the energy not reflected
//     + D_GGX(h) V_SmithGGXCorrelated(v, l) F_Schlick(v.h)         specular (Trowbridge-Reitz, height-correlated Smith)
// alpha = roughness^2 (perceptual roughness), F0 = mix(dielectric f0 from the IOR, albedo, metallic), F90 = sat(50 F0)
// so a material with IOR 1 (f0 = 0) has no specular at all and is exactly Lambertian. The (1 - F) weight keeps
// diffuse + specular <= 1 for every direction (single scattering; the multiple-scattering energy of very rough
// metals is not compensated).
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

vec3 evaluateBrdf(SurfaceBrdf s, vec3 N, vec3 V, vec3 L)
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
    vec3 specular = distributionGgx(NdotH, alpha) * visibilitySmithGgxCorrelated(NdotV, NdotL, alpha) * F;
    vec3 diffuse = (vec3(1.0) - F) * (1.0 - s.metallic) * s.albedo * INV_PI;
    return diffuse + specular;
}

#endif
