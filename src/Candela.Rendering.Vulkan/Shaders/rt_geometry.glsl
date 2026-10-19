// Hit attributes of ray-traced instances (RayTracingScene): the geometry table and the vertex/index buffers it points
// at through buffer device addresses. The including shader enables GL_EXT_buffer_reference and
// GL_EXT_buffer_reference_uvec2 and defines RT_GEOMETRY_SET and RT_GEOMETRY_BINDING (where the table is bound).
// Table layout: per instance slot (8 uints at slot * 8): vertex address (2), index address (2), first sub-mesh entry,
// sub-mesh count, 2 unused; sub-mesh entries (2 uints): first index, material slot.
#ifndef CANDELA_RT_GEOMETRY_GLSL
#define CANDELA_RT_GEOMETRY_GLSL

#include "scene.glsl"

layout(set = RT_GEOMETRY_SET, binding = RT_GEOMETRY_BINDING, scalar) readonly buffer RtGeometryBuffer
{
    uint rtData[];
};

struct RtVertex
{
    vec3 position;
    vec3 normal;
    vec2 uv;
    vec4 tangent;
};

layout(buffer_reference, scalar, buffer_reference_align = 4) readonly buffer RtVertices
{
    RtVertex v[];
};

layout(buffer_reference, scalar, buffer_reference_align = 4) readonly buffer RtIndices
{
    uint i[];
};

// A surface point of a hit, world space. normal and tangent are interpolated (not yet normalised against each other);
// geometricNormal is the triangle's unit normal (not faced towards the ray).
struct RtSurface
{
    vec3 position;
    vec3 geometricNormal;
    vec3 normal;
    vec4 tangent;            // xyz world tangent, w = bitangent sign
    vec2 uv;                 // mesh uv (before the material's uv scale)
    uint material;           // index into materials[]
};

// Material index of sub-mesh `geometry` of instance slot `instance` (without fetching vertices).
uint rtMaterialIndex(uint instance, uint geometry)
{
    uint subMesh = rtData[instance * 8u + 4u] + 2u * geometry;
    uint slot = rtData[subMesh + 1u];
    uint count = instances[instance].materialSlotCount;
    return slot < count ? materialSlots[instances[instance].materialSlotOffset + slot] : 0u;
}

// Fetches the surface of triangle `primitive` of sub-mesh `geometry` of instance slot `instance` at barycentrics `bary`.
RtSurface rtFetchSurface(uint instance, uint geometry, uint primitive, vec2 bary)
{
    uint header = instance * 8u;
    uvec2 vaddr = uvec2(rtData[header], rtData[header + 1u]);
    uvec2 iaddr = uvec2(rtData[header + 2u], rtData[header + 3u]);
    uint subMesh = rtData[header + 4u] + 2u * geometry;
    uint firstIndex = rtData[subMesh];
    uint slot = rtData[subMesh + 1u];
    RtIndices indices = RtIndices(iaddr);
    RtVertices vertices = RtVertices(vaddr);
    uint i0 = indices.i[firstIndex + 3u * primitive];
    uint i1 = indices.i[firstIndex + 3u * primitive + 1u];
    uint i2 = indices.i[firstIndex + 3u * primitive + 2u];
    RtVertex a = vertices.v[i0];
    RtVertex b = vertices.v[i1];
    RtVertex c = vertices.v[i2];
    vec3 w = vec3(1.0 - bary.x - bary.y, bary.x, bary.y);
    Instance inst = instances[instance];
    RtSurface s;
    vec3 pa = (inst.world * vec4(a.position, 1.0)).xyz;
    vec3 pb = (inst.world * vec4(b.position, 1.0)).xyz;
    vec3 pc = (inst.world * vec4(c.position, 1.0)).xyz;
    s.position = pa * w.x + pb * w.y + pc * w.z;
    vec3 g = cross(pb - pa, pc - pa);
    float gl = length(g);
    s.normal = mat3(inst.normalMatrix) * (a.normal * w.x + b.normal * w.y + c.normal * w.z);
    float nl = length(s.normal);
    s.normal = nl > 1e-20 ? s.normal / nl : vec3(0.0, 0.0, 1.0);
    s.geometricNormal = gl > 1e-30 ? g / gl : s.normal;
    vec4 t = a.tangent * w.x + b.tangent * w.y + c.tangent * w.z;
    s.tangent = vec4(mat3(inst.world) * t.xyz, a.tangent.w);
    s.uv = a.uv * w.x + b.uv * w.y + c.uv * w.z;
    s.material = slot < inst.materialSlotCount ? materialSlots[inst.materialSlotOffset + slot] : 0u;
    return s;
}

#endif
