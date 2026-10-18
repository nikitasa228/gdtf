#version 460
#extension GL_GOOGLE_include_directive : require
// Full view: forward PBR shading with the physically based spot lights of the fragment's cluster.
//   L = sum over lights of BRDF(v, l) * E_light + albedo / pi * E_indirect + L_emissive
//   E_light = I0 * profile(theta) * cookie * colour * shadow * cos(incidence) / d^2
// Output: luminance (cd/m2, linear Rec.2020) x exposure, into the RGBA16F HDR image.
// Variant TRANSPARENT (alpha BLEND and transmissive materials): premultiplied output; coverage = alpha *
// (1 - transmission) scales diffuse and emission, specular reflection is added in full (a glass approximation that
// keeps highlights on clear surfaces; the tint of transmitted light is not modelled). Transparent surfaces are drawn
// after the volumetrics and apply the in-scattering and transmittance between the camera and themselves.

#include "lighting.glsl"
#include "brdf.glsl"
#include "volume.glsl"

layout(location = 0) in vec3 inWorldPosition;
layout(location = 1) in vec3 inNormal;
layout(location = 2) in vec4 inTangent;
layout(location = 3) in vec2 inUv;
layout(location = 4) flat in uint inInstance;
layout(location = 5) flat in uint inMaterial;

layout(location = 0) out vec4 outColor;

// Opacity of reference drawings (InstanceFlags.Reference): unlit and semi-transparent.
const float REFERENCE_OPACITY = 0.5;

// Joint bilateral upsampling of the reduced-resolution indirect irradiance: the four nearest low-resolution samples,
// bilinear weights times a view-depth similarity weight (samples on other surfaces do not bleed across edges).
vec3 indirectIrradiance(float viewDepth)
{
    vec2 size = vec2(textureSize(indirectTexture, 0));
    vec2 pos = gl_FragCoord.xy * frame.renderSize.zw * size - 0.5;
    vec2 base = floor(pos);
    vec2 f = pos - base;
    vec3 sum = vec3(0.0);
    float weightSum = 0.0;
    for (int j = 0; j < 2; ++j)
    {
        for (int i = 0; i < 2; ++i)
        {
            ivec2 p = ivec2(clamp(base + vec2(i, j), vec2(0.0), size - 1.0));
            vec4 s = texelFetch(indirectTexture, p, 0);
            float w = (i == 0 ? 1.0 - f.x : f.x) * (j == 0 ? 1.0 - f.y : f.y);
            w *= exp(-abs(s.a - viewDepth) / max(0.03 * viewDepth, 1e-3)) + 1e-4;
            sum += s.rgb * w;
            weightSum += w;
        }
    }

    return weightSum > 0.0 ? sum / weightSum : vec3(0.0);
}

void main()
{
    Material m = materials[inMaterial];
    vec2 uv = inUv * m.uvScale;

    vec3 albedo;
    float alpha;
    if ((m.flags & MATERIAL_BASE_COLOR_TEXTURE) != 0u)
    {
        vec4 t = sampleTexture(m.baseColorTexture, uv); // *_SRGB format: decoded to linear Rec.709 by the sampler
        albedo = rec709ToRec2020(t.rgb * m.baseColorFactor709.rgb);
        alpha = t.a * m.baseColorFactor709.a;
    }
    else
    {
        albedo = m.baseColor.rgb;
        alpha = m.baseColor.a;
    }

    // Reference drawings and unlit materials show their base colour as a display-referred value, independent of the
    // exposure and the stage lights; reference drawings are semi-transparent. The Colorless view (reference drawings
    // only) writes display-linear Rec.709, which its display transform passes through.
    bool reference = (instances[inInstance].flags & INSTANCE_FLAG_REFERENCE) != 0u;
    if ((m.flags & MATERIAL_UNLIT) != 0u || reference)
    {
#ifdef TRANSPARENT
        float a = alpha * (reference ? REFERENCE_OPACITY : 1.0);
        vec3 display = frame.viewMode == VIEW_COLORLESS ? clamp(rec2020ToRec709(albedo), 0.0, 1.0) : albedo;
        outColor = vec4(display * a, a);
#else
        outColor = vec4(albedo, 1.0);
#endif
        return;
    }

    float metallic = m.metallic;
    float roughness = m.roughness;
    if ((m.flags & MATERIAL_METALLIC_ROUGHNESS_TEXTURE) != 0u)
    {
        vec4 mr = sampleTexture(m.metallicRoughnessTexture, uv); // glTF: G = roughness, B = metallic
        roughness *= mr.g;
        metallic *= mr.b;
    }

    roughness = clamp(roughness, 0.02, 1.0);
    metallic = clamp(metallic, 0.0, 1.0);

    vec3 N = normalize(inNormal);
    if (!gl_FrontFacing)
        N = -N; // double-sided materials (single-sided ones are back-face culled)
    if ((m.flags & MATERIAL_NORMAL_TEXTURE) != 0u)
    {
        vec3 T = inTangent.xyz - N * dot(N, inTangent.xyz);
        float tl = length(T);
        if (tl > 1e-6)
        {
            T /= tl;
            vec3 B = cross(N, T) * (inTangent.w < 0.0 ? -1.0 : 1.0);
            vec3 tn = sampleTexture(m.normalTexture, uv).xyz * 2.0 - 1.0;
            N = normalize(T * tn.x + B * tn.y + N * max(tn.z, 1e-3));
        }
    }

    vec3 P = inWorldPosition;
    vec3 V = isOrthographic() ? -frame.cameraForward.xyz : normalize(frame.cameraPosition.xyz - P);
    SurfaceBrdf surface = SurfaceBrdf(albedo, metallic, roughness, m.f0);
    float viewDepth = viewDepthOf(P);
    float pixelSize = pixelWorldSize(viewDepth);
    float noise = interleavedGradientNoise(gl_FragCoord.xy);

    vec3 diffuse = vec3(0.0);
    vec3 specular = vec3(0.0);
    uvec2 cluster = frame.lightCount > 0u ? clusters[clusterIndex(gl_FragCoord.xy, viewDepth)] : uvec2(0u);
    for (uint i = 0u; i < cluster.y; ++i)
    {
        uint index = clusterLights[cluster.x + i];
        vec3 L;
        vec3 E = surfaceIlluminance(index, P, N, pixelSize, noise, L);
        if (E.r + E.g + E.b <= 0.0)
            continue;
        vec3 fd, fs;
        evaluateBrdf(surface, N, V, L, fd, fs);
        diffuse += fd * E;
        specular += fs * E;
    }

#ifndef TRANSPARENT
    if (hasFeature(FEATURE_INDIRECT_RSM) || hasFeature(FEATURE_INDIRECT_RAY_TRACED))
        diffuse += (1.0 - metallic) * albedo * INV_PI * frame.indirectIntensity * indirectIrradiance(viewDepth);
#endif

    vec3 emissive = m.emissive;
    if ((m.flags & MATERIAL_EMISSIVE_TEXTURE) != 0u)
        emissive = rec709ToRec2020(sampleTexture(m.emissiveTexture, uv).rgb * m.emissive709);

#ifdef TRANSPARENT
    float coverage = clamp(alpha * (1.0 - m.transmission), 0.0, 1.0);
    vec3 color = ((diffuse + emissive) * coverage + specular) * frame.exposure;
    if (hasFeature(FEATURE_VOLUMETRICS))
    {
        // The volume composite already fogged what lies behind; this surface is fogged by the medium in front of it
        // (the integrated volume is pre-exposed).
        vec4 fog = sampleIntegratedVolume(gl_FragCoord.xy * frame.renderSize.zw, viewDepth);
        color = color * fog.a + fog.rgb * coverage;
    }

    outColor = vec4(finiteLight(color), coverage);

#else
    outColor = vec4(finiteLight((diffuse + specular + emissive) * frame.exposure), 1.0);
#endif
}
