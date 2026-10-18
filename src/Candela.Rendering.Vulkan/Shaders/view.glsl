// Set 1: per-view resources (clustered light lists, scene images of the render target).
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

layout(set = SET_VIEW, binding = BINDING_CLUSTERS, scalar) CLUSTER_ACCESS buffer ClusterBuffer
{
    uvec2 clusters[]; // x = first index in clusterLights, y = light count
};

layout(set = SET_VIEW, binding = BINDING_CLUSTER_LIGHTS, scalar) CLUSTER_ACCESS buffer ClusterLightBuffer
{
    uint clusterLights[];
};

#ifdef CLUSTER_BUFFERS_WRITABLE
layout(set = SET_VIEW, binding = BINDING_CLUSTER_COUNTER, scalar) buffer ClusterCounterBuffer
{
    uint clusterIndexCount;      // indices requested by all clusters (may exceed the capacity)
    uint clusterOverflow;        // clusters whose list was truncated
    uint clusterPad0;
    uint clusterPad1;
};
#endif

layout(set = SET_VIEW, binding = BINDING_HDR) uniform sampler2D hdrTexture;

// Distance of a world point from the camera plane along the view direction (the cluster depth coordinate).
float viewDepthOf(vec3 worldPosition)
{
    return dot(worldPosition - frame.cameraPosition.xyz, frame.cameraForward.xyz);
}

// Cluster of a fragment: screen tile from the pixel position, logarithmic slice from the view depth.
uint clusterIndex(vec2 fragCoord, float viewDepth)
{
    uvec2 tile = uvec2(clamp(fragCoord * frame.renderSize.zw * vec2(frame.clusterCountX, frame.clusterCountY),
                             vec2(0.0), vec2(frame.clusterCountX - 1u, frame.clusterCountY - 1u)));
    float slice = log(max(viewDepth, 1e-6)) * frame.clusterLogScale + frame.clusterLogBias;
    uint z = uint(clamp(slice, 0.0, float(frame.clusterCountZ - 1u)));
    return (z * frame.clusterCountY + tile.y) * frame.clusterCountX + tile.x;
}

#endif
