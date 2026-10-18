#version 460
#extension GL_GOOGLE_include_directive : require
// Full view: forward PBR shading with the physically based spot lights of the fragment's cluster.
//   L = sum over lights of BRDF(v, l) * E_light + L_emissive,     E = I0 * profile(theta) * colour * cos(incidence) / d^2
// Output: luminance (cd/m2, linear Rec.2020) x exposure, into the RGBA16F HDR image.
// Variant TRANSPARENT (alpha BLEND and transmissive materials): premultiplied output; coverage = alpha *
// (1 - transmission) scales diffuse and emission, specular reflection is added in full (a glass approximation that
// keeps highlights on clear surfaces; the tint of transmitted light is not modelled in stage 1).

#include "scene.glsl"
#include "view.glsl"
#include "color.glsl"
#include "brdf.glsl"
#include "photometry.glsl"

layout(location = 0) in vec3 inWorldPosition;
layout(location = 1) in vec3 inNormal;
layout(location = 2) in vec4 inTangent;
layout(location = 3) in vec2 inUv;
layout(location = 4) flat in uint inInstance;
layout(location = 5) flat in uint inMaterial;

layout(location = 0) out vec4 outColor;

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

    // Reference drawings (InstanceFlags.Reference) and unlit materials show their base colour as a display-referred
    // value, independent of the exposure and the stage lights.
    if ((m.flags & MATERIAL_UNLIT) != 0u || (instances[inInstance].flags & INSTANCE_FLAG_REFERENCE) != 0u)
    {
#ifdef TRANSPARENT
        outColor = vec4(albedo * alpha, alpha);
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

    vec3 diffuse = vec3(0.0);
    vec3 specular = vec3(0.0);
    uvec2 cluster = frame.lightCount > 0u ? clusters[clusterIndex(gl_FragCoord.xy, viewDepthOf(P))] : uvec2(0u);
    for (uint i = 0u; i < cluster.y; ++i)
    {
        Light light = lights[clusterLights[cluster.x + i]];
        vec3 L;
        vec3 E = spotIlluminance(light, P, N, L);
        if (E.r + E.g + E.b <= 0.0)
            continue;
        vec3 fd, fs;
        evaluateBrdf(surface, N, V, L, fd, fs);
        diffuse += fd * E;
        specular += fs * E;
    }

    vec3 emissive = m.emissive;
    if ((m.flags & MATERIAL_EMISSIVE_TEXTURE) != 0u)
        emissive = rec709ToRec2020(sampleTexture(m.emissiveTexture, uv).rgb * m.emissive709);

#ifdef TRANSPARENT
    float coverage = clamp(alpha * (1.0 - m.transmission), 0.0, 1.0);
    outColor = vec4(((diffuse + emissive) * coverage + specular) * frame.exposure, coverage);
#else
    outColor = vec4((diffuse + specular + emissive) * frame.exposure, 1.0);
#endif
}
