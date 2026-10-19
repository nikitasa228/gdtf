// Set 1: per-view resources (clustered light lists, per-view light data, shadow atlas, volumes, scene images of the
// render target). Every pipeline shares this layout; a shader only touches what it declares use of.
#ifndef CANDELA_VIEW_GLSL
#define CANDELA_VIEW_GLSL

#include "common.glsl"

// Only the light-assignment compute shader writes the cluster buffers; graphics stages declare them read-only
// (fragmentStoresAndAtomics is not required).
#ifdef CLUSTER_BUFFERS_WRITABLE
#define CLUSTER_ACCESS
#else
#define CLUSTER_ACCESS readonly
#endif

#ifdef VPLS_WRITABLE
#define VPL_ACCESS
#else
#define VPL_ACCESS readonly
#endif

// Clustered light lists: one bit per light (light i = bit i % 32 of mask word i / 32) for every cluster, so a list
// holds every one of the frame.clusterListedLights listed lights that reaches the cluster. Each cluster's record in
// clusterMasks[] is clusterSummaryWords() summary words (bit w set = mask word w is not zero, so sparse lists skip
// the empty words) followed by the mask words; lights are visited in ascending index order (deterministic sums).
layout(set = SET_VIEW, binding = BINDING_CLUSTERS, scalar) CLUSTER_ACCESS buffer ClusterBuffer
{
    uvec2 clusters[]; // x = lights in the cluster's list, y = 1 when an unlisted light (beyond the limit) reaches it
};

layout(set = SET_VIEW, binding = BINDING_CLUSTER_MASKS, scalar) CLUSTER_ACCESS buffer ClusterMaskBuffer
{
    uint clusterMasks[];
};

#ifdef CLUSTER_BUFFERS_WRITABLE
// Statistics of the light assignment (cleared every frame, read back by the CPU a frame or two later).
layout(set = SET_VIEW, binding = BINDING_CLUSTER_COUNTER, scalar) buffer ClusterCounterBuffer
{
    uint clusterLightPairs;      // sum over the clusters of the lights in their lists
    uint clusterOverflow;        // clusters reached by an unlisted light (beyond the limit; missing there)
    uint clusterMaxLights;       // most lights in one cluster
    uint clusterPad0;
};
#endif

// Pre-exposed HDR image of the target (internal resolution), linear filtering.
layout(set = SET_VIEW, binding = BINDING_HDR) uniform sampler2D hdrTexture;

layout(set = SET_VIEW, binding = BINDING_LIGHT_VIEWS, scalar) readonly buffer LightViewBuffer
{
    LightView lightViews[];
};

layout(set = SET_VIEW, binding = BINDING_SHADOW_SLOTS, scalar) readonly buffer ShadowSlotBuffer
{
    ShadowSlot shadowSlots[];
};

// Shadow atlas: hardware comparison (reverse-Z, GREATER_OR_EQUAL, bilinear PCF) and raw depth (blocker search).
layout(set = SET_VIEW, binding = BINDING_SHADOW_MAP) uniform sampler2DShadow shadowMap;
layout(set = SET_VIEW, binding = BINDING_SHADOW_DEPTH) uniform sampler2D shadowDepth;

// Reflective shadow maps (same atlas layout): diffuse reflectance (Rec.2020) and octahedral normal.
layout(set = SET_VIEW, binding = BINDING_RSM_ALBEDO) uniform sampler2D rsmAlbedo;
layout(set = SET_VIEW, binding = BINDING_RSM_NORMAL) uniform sampler2D rsmNormal;

layout(set = SET_VIEW, binding = BINDING_VPLS, scalar) VPL_ACCESS buffer VplBuffer
{
    Vpl vpls[];
};

// Indirect irradiance (lx, Rec.2020) at reduced resolution; alpha = linear view depth of the sample.
layout(set = SET_VIEW, binding = BINDING_INDIRECT) uniform sampler2D indirectTexture;

// Integrated froxel volume: rgb = in-scattered luminance (cd/m2) from the camera to the far side of each froxel,
// a = transmittance over the same path.
layout(set = SET_VIEW, binding = BINDING_VOLUME) uniform sampler3D volumeTexture;

// Participating media of the world (dummy 1^3 volumes when absent).
layout(set = SET_VIEW, binding = BINDING_SMOKE) uniform sampler3D smokeTexture;
layout(set = SET_VIEW, binding = BINDING_FLAME) uniform sampler3D flameTexture;
layout(set = SET_VIEW, binding = BINDING_NOISE_FIELD) uniform sampler3D noiseFieldTexture;

// Blackbody luminance table: per entry vec4(Rec.2020 colour normalised to luminance 1, luminance cd/m2); after its
// frame.flameLut.z entries, four entries per flame grid (box min, box max, atlas offset, size in texels).
layout(set = SET_VIEW, binding = BINDING_FLAME_LUT, scalar) readonly buffer FlameLutBuffer
{
    vec4 flameLutEntries[];
};

// Noise sources: xyz = position (m), w = level at 1 m, dB(A).
layout(set = SET_VIEW, binding = BINDING_NOISE_SOURCES, scalar) readonly buffer NoiseSourceBuffer
{
    vec4 noiseSources[];
};

// Overlay gradient (sRGB colour + alpha) over [RangeMin, RangeMax] (log10 or linear).
layout(set = SET_VIEW, binding = BINDING_GRADIENT_LUT) uniform sampler2D gradientLut;

// Scene images of the target (internal resolution).
layout(set = SET_VIEW, binding = BINDING_SCENE_DEPTH) uniform sampler2D sceneDepth;
layout(set = SET_VIEW, binding = BINDING_SCENE_NORMALS) uniform sampler2D sceneNormals;
layout(set = SET_VIEW, binding = BINDING_INSTANCE_IDS) uniform usampler2D instanceIds;
layout(set = SET_VIEW, binding = BINDING_BLOOM) uniform sampler2D bloomTexture;
layout(set = SET_VIEW, binding = BINDING_ANALYSIS) uniform sampler2D analysisTexture;
layout(set = SET_VIEW, binding = BINDING_SELECTION_MASK) uniform sampler2D selectionMask;

// Per-view light lists: the volumetric beams (frame.beamCount, score order), then the lights of the indirect light.
layout(set = SET_VIEW, binding = BINDING_RAYMARCH_LIST, scalar) readonly buffer RaymarchListBuffer
{
    uint raymarchList[];
};

// Distance of a world point from the camera plane along the view direction (the cluster depth coordinate).
float viewDepthOf(vec3 worldPosition)
{
    return dot(worldPosition - frame.cameraPosition.xyz, frame.cameraForward.xyz);
}

// Cluster of a point: screen tile from the normalised screen position, logarithmic slice from the view depth.
uint clusterIndexUv(vec2 uv, float viewDepth)
{
    uvec2 tile = uvec2(clamp(uv * vec2(frame.clusterCountX, frame.clusterCountY),
                             vec2(0.0), vec2(frame.clusterCountX - 1u, frame.clusterCountY - 1u)));
    float slice = log(max(viewDepth, 1e-6)) * frame.clusterLogScale + frame.clusterLogBias;
    uint z = uint(clamp(slice, 0.0, float(frame.clusterCountZ - 1u)));
    return (z * frame.clusterCountY + tile.y) * frame.clusterCountX + tile.x;
}

// Cluster of a fragment of the scene images (gl_FragCoord at internal resolution).
uint clusterIndex(vec2 fragCoord, float viewDepth)
{
    return clusterIndexUv(fragCoord * frame.renderSize.zw, viewDepth);
}

// Summary words of a cluster record (one bit per mask word).
uint clusterSummaryWords()
{
    return (frame.clusterMaskWords + 31u) / 32u;
}

// Words of one cluster record in clusterMasks[] (summary words, then mask words).
uint clusterRecordWords()
{
    return clusterSummaryWords() + frame.clusterMaskWords;
}

// Iteration over the lights of a cluster's list in ascending index order:
//     ClusterCursor c = clusterCursor(cluster); uint index;
//     while (nextClusterLight(c, index)) { ... lights[index] ... }
struct ClusterCursor
{
    uint base;          // first word of the cluster's record
    uint summaryWords;
    uint summaryIndex;  // summary word being consumed
    uint summary;       // its bits not visited yet
    uint word;          // mask word being consumed
    uint bits;          // its bits not visited yet
};

ClusterCursor clusterCursor(uint cluster)
{
    ClusterCursor c;
    c.summaryWords = frame.lightCount > 0u ? clusterSummaryWords() : 0u;
    c.base = cluster * clusterRecordWords();
    c.summaryIndex = 0u;
    c.summary = c.summaryWords > 0u ? clusterMasks[c.base] : 0u;
    c.word = 0u;
    c.bits = 0u;
    return c;
}

bool nextClusterLight(inout ClusterCursor c, out uint index)
{
    while (c.bits == 0u)
    {
        while (c.summary == 0u)
        {
            if (++c.summaryIndex >= c.summaryWords)
            {
                index = 0u;
                return false;
            }
            c.summary = clusterMasks[c.base + c.summaryIndex];
        }
        c.word = c.summaryIndex * 32u + uint(findLSB(c.summary));
        c.summary &= c.summary - 1u;
        c.bits = c.word < frame.clusterMaskWords ? clusterMasks[c.base + c.summaryWords + c.word] : 0u;
    }
    index = c.word * 32u + uint(findLSB(c.bits));
    c.bits &= c.bits - 1u;
    return true;
}

// World position of a normalised screen position (uv, origin top-left) at reverse-Z depth d.
vec3 worldFromDepth(vec2 uv, float d)
{
    vec4 h = frame.inverseViewProjection * vec4(uv * 2.0 - 1.0, d, 1.0);
    return h.xyz / h.w;
}

// Unit view ray through a normalised screen position (towards the scene) and its origin.
void viewRay(vec2 uv, out vec3 origin, out vec3 direction)
{
    vec3 a = worldFromDepth(uv, 1.0);
    vec3 b = worldFromDepth(uv, 0.5);
    origin = isOrthographic() ? a : frame.cameraPosition.xyz;
    direction = normalize(b - a);
}

#endif
