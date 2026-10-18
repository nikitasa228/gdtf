// Shared declarations: GPU structs (scalar block layout, mirrored by GpuStructs.cs), the per-view uniform block and
// the push constants. Units: metres, candela, cd/m2; colours linear Rec.2020 unless noted.
#ifndef CANDELA_COMMON_GLSL
#define CANDELA_COMMON_GLSL

#extension GL_EXT_scalar_block_layout : require
#extension GL_EXT_nonuniform_qualifier : require

#include "generated.glsl"

const float PI = 3.14159265358979323846;
const float INV_PI = 0.31830988618379067154;
const float LN2 = 0.69314718055994530942;

// PBR metallic-roughness material (112 bytes).
struct Material
{
    vec4 baseColor;          // factor converted Rec.709 -> Rec.2020 on the CPU; a = alpha
    vec4 baseColorFactor709; // factor in Rec.709, multiplied with textures before conversion
    vec3 emissive;           // Rec.2020 colour x luminance, cd/m2
    float metallic;
    float roughness;         // perceptual
    float f0;                // dielectric reflectance at normal incidence, ((ior - 1) / (ior + 1))^2
    float alphaCutoff;
    float transmission;
    vec2 uvScale;
    uint baseColorTexture;
    uint normalTexture;
    uint metallicRoughnessTexture;
    uint occlusionTexture;
    uint emissiveTexture;
    uint flags;              // MATERIAL_*
    vec3 emissive709;        // Rec.709 colour x luminance, multiplied with the emissive texture before conversion
    float ior;
};

// A placed mesh (160 bytes). Matrices are System.Numerics matrices uploaded verbatim: M * v in GLSL = v * M in C#.
struct Instance
{
    mat4 world;
    mat4 normalMatrix;       // inverse transpose of the upper 3x3
    uint flags;              // Candela.Rendering.InstanceFlags
    uint pickId;
    uint materialSlotOffset; // first entry in materialSlots[]
    uint materialSlotCount;
    vec4 boundingSphere;     // world-space centre + radius
};

// Spot light (96 bytes), packed by LightPacker.cs. The profile parameters are those of
// Candela.Photometry.BeamDistribution; beamProfile() in photometry.glsl evaluates its Relative(theta).
struct Light
{
    vec3 position;
    float maxIntensity;      // largest channel of intensity, cd (sets the range from the exposure)
    vec3 direction;
    float cosCutoff;         // cos of the angle beyond which the intensity is exactly zero (iris included)
    vec3 intensity;          // linear Rec.2020 colour (luminance 1) x peak candela
    float invHalfBeam;       // 1 / half beam angle
    float exponent;          // p of exp(-ln2 (theta/theta_b)^p)
    float halfField;         // half field angle (start of the smoothstep window)
    float invWindow;         // 1 / (cutoff - halfField)
    float cutoff;            // half-angle with zero intensity beyond
    float irisAngle;         // half-angle of the iris aperture (4 rad when open)
    float irisSoftStart;     // the iris edge fades from here to irisAngle
    uint shape;              // SHAPE_*
    uint flags;              // bit 0: casts shadows (stage 2)
    vec3 up;                 // gobo/blade orientation (stage 2)
    float sourceRadius;      // exit lens radius, m (stage 2: soft shadows)
};

// Per-view constants (uniform block, 592 bytes).
struct FrameData
{
    mat4 view;
    mat4 projection;         // reverse-Z (infinite far plane for perspective)
    mat4 viewProjection;
    mat4 inverseView;
    mat4 inverseProjection;
    mat4 inverseViewProjection;
    vec4 cameraPosition;     // w = 1 for orthographic
    vec4 cameraForward;      // world-space view direction
    vec4 renderSize;         // width, height, 1/width, 1/height
    vec4 background;         // HDR clear colour (pre-exposed in Full, display-linear otherwise)
    vec4 gridColorMinor;     // sRGB-encoded colour + alpha
    vec4 gridColorMajor;
    vec4 gridAxisX;
    vec4 gridAxisY;
    float exposure;          // scene luminance (cd/m2) x exposure = HDR value (1 in Colorless/Wireframe)
    float nearPlane;
    float farPlane;          // far end of the cluster grid (perspective) / far plane (orthographic)
    float clusterLogScale;   // slice = log(viewDepth) * scale + bias
    float clusterLogBias;
    uint clusterCountX;
    uint clusterCountY;
    uint clusterCountZ;
    uint lightCount;
    uint viewMode;           // VIEW_*
    uint flags;              // FRAME_*
    float gridSpacing;       // metres per cell
    float gridMajorEvery;    // cells per major line
    float gridFadeDistance;  // metres; 0 = no distance fade
    float rangeEpsilon;      // exposed value at which a light's contribution counts as negligible
    uint clusterIndexCapacity;
    vec4 headlight;          // Colorless: unit direction towards the key light (world space)
};

layout(set = SET_VIEW, binding = BINDING_FRAME, scalar) uniform FrameBlock
{
    FrameData frame;
};

// Per-draw constants (32 bytes).
layout(push_constant, scalar) uniform DrawConstants
{
    uint instanceIndex;
    uint materialIndex;
    uint mode;               // DRAW_MODE_* (meshes), TONE_* (tone mapping)
    float width;             // line width in pixels
    vec4 color;              // constant colour (wireframe, edges)
} draw;

bool isOrthographic()
{
    return (frame.flags & FRAME_ORTHOGRAPHIC) != 0u;
}

#endif
