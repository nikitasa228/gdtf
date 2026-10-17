// Set 1: per-view resources (clusters, scene images of the render target).
#ifndef CANDELA_VIEW_GLSL
#define CANDELA_VIEW_GLSL

#include "common.glsl"

// Only the light-culling compute shader writes the cluster buffers; graphics stages must declare storage buffers
// read-only (fragmentStoresAndAtomics / vertexPipelineStoresAndAtomics are not required).
#ifdef CLUSTER_BUFFERS_WRITABLE
#define CLUSTER_ACCESS
#else
#define CLUSTER_ACCESS readonly
#endif

layout(set = SET_VIEW, binding = BINDING_CLUSTERS, scalar) CLUSTER_ACCESS buffer ClusterBuffer
{
    uvec2 clusters[]; // x = first index in clusterLights, y = light count
};

layout(set = SET_VIEW, binding = BINDING_CLUSTER_LIGHTS, scalar) CLUSTER_ACCESS buffer ClusterLightBuffer
{
    uint clusterLights[];
};

layout(set = SET_VIEW, binding = BINDING_CLUSTER_COUNTER, scalar) CLUSTER_ACCESS buffer ClusterCounterBuffer
{
    uint clusterIndexCount;    // indices written (clamped to the capacity)
    uint clusterIndexRequired; // indices that would have been written (grows the buffer next time)
    uint clusterOverflow;      // clusters that hit MAX_LIGHTS_PER_CLUSTER
    uint clusterPad;
};

layout(set = SET_VIEW, binding = BINDING_HDR) uniform sampler2D hdrTexture;
layout(set = SET_VIEW, binding = BINDING_DEPTH) uniform sampler2D depthTexture;

// Cluster of a fragment: screen tile from the pixel position, logarithmic slice from the view-space depth.
uint clusterIndex(vec2 fragCoord, float viewDepth)
{
    uvec2 tile = uvec2(clamp(fragCoord * frame.renderSize.zw * vec2(frame.clusterCountX, frame.clusterCountY),
                             vec2(0.0), vec2(frame.clusterCountX - 1, frame.clusterCountY - 1)));
    float slice = log(max(viewDepth, 1e-6)) * frame.clusterLogScale + frame.clusterLogBias;
    uint z = uint(clamp(slice, 0.0, float(frame.clusterCountZ - 1)));
    return (z * frame.clusterCountY + tile.y) * frame.clusterCountX + tile.x;
}

#endif
