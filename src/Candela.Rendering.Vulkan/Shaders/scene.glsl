// Set 0: the retained scene (materials, instances, instance material slots, lights, bindless textures).
#ifndef CANDELA_SCENE_GLSL
#define CANDELA_SCENE_GLSL

#include "common.glsl"

layout(set = SET_SCENE, binding = BINDING_MATERIALS, scalar) readonly buffer MaterialBuffer
{
    Material materials[];
};

layout(set = SET_SCENE, binding = BINDING_INSTANCES, scalar) readonly buffer InstanceBuffer
{
    Instance instances[];
};

// Material index per instance slot: materials[materialSlots[instance.materialSlotOffset + slot]].
layout(set = SET_SCENE, binding = BINDING_MATERIAL_SLOTS, scalar) readonly buffer MaterialSlotBuffer
{
    uint materialSlots[];
};

layout(set = SET_SCENE, binding = BINDING_LIGHTS, scalar) readonly buffer LightBuffer
{
    Light lights[];
};

// Bindless textures: variable count, update-after-bind, partially bound. Indices are dynamically non-uniform.
layout(set = SET_SCENE, binding = BINDING_TEXTURES) uniform sampler2D textures[];

vec4 sampleTexture(uint index, vec2 uv)
{
    return texture(textures[nonuniformEXT(index)], uv);
}

#endif
