// Shared declarations: GPU structs (scalar block layout, mirrored by GpuStructs.cs), descriptor sets, push constants.
#ifndef CANDELA_COMMON_GLSL
#define CANDELA_COMMON_GLSL

#extension GL_EXT_scalar_block_layout : require
#extension GL_EXT_nonuniform_qualifier : require

#include "generated.glsl"

const float PI = 3.14159265358979323846;
const float INV_PI = 0.31830988618379067154;
const float LN2 = 0.69314718055994530942;

// Interleaved vertex of the shared geometry buffer (48 bytes).
struct Vertex
{
    vec3 position;
    vec3 normal;
    vec2 uv;
    vec4 tangent; // xyz tangent, w bitangent sign
};

// PBR metallic-roughness material (112 bytes). Colours in linear Rec.2020 unless noted.
struct Material
{
    vec4 baseColor;          // factor converted Rec.709 -> Rec.2020 on the CPU; a = alpha
    vec4 baseColorFactor709; // factor in Rec.709, multiplied with textures before conversion
    vec3 emissive;           // Rec.2020 colour x luminance, cd/m2
    float metallic;
    float roughness;
    float f0;                // dielectric reflectance at normal incidence, ((ior - 1) / (ior + 1))^2
    float alphaCutoff;
    float transmission;
    vec2 uvScale;
    uint baseColorTexture;
    uint normalTexture;
    uint metallicRoughnessTexture;
    uint occlusionTexture;
    uint emissiveTexture;
    uint flags;
    vec3 emissive709;        // Rec.709 colour x luminance, multiplied with the emissive texture before conversion
    float ior;
};

// A placed mesh (160 bytes). Matrices are System.Numerics matrices uploaded verbatim: M * v in GLSL = v * M in C#.
struct Instance
{
    mat4 world;
    mat4 normalMatrix;      // inverse transpose of the upper 3x3
    uint flags;
    uint pickId;
    uint materialSlotOffset;
    uint materialSlotCount;
    vec4 boundingSphere;    // world-space centre + radius
};

// Spot light (96 bytes), see LightPacker.cs. The profile parameters reproduce Candela.Photometry.BeamDistribution.
struct Light
{
    vec3 position;
    float range;             // distance beyond which the contribution is negligible (smooth cut)
    vec3 direction;
    float cosCutoff;         // cos of the angle beyond which the intensity is exactly zero
    vec3 intensity;          // linear Rec.2020 colour (luminance 1) x peak candela
    float invHalfBeam;       // 1 / half beam angle
    float exponent;          // p of exp(-ln2 (theta/theta_b)^p)
    float halfField;         // half field angle (start of the smoothstep window)
    float invWindow;         // 1 / (cutoff - halfField)
    float cutoff;            // half-angle with zero intensity beyond
    float irisAngle;         // half-angle of the iris aperture (>= cutoff when open)
    float irisSoftStart;     // the iris edge fades from here to irisAngle
    uint shape;              // SHAPE_*
    float invRange2;         // 1 / range^2 for the range window
    vec3 up;
    float sourceRadius;
};

// SetLines vertex (32 bytes, identical to Candela.Rendering.LineVertex).
struct LineVertex
{
    vec3 position;
    vec4 color;
    float width;
};

// Per-view constants (UBO, 544 bytes).
struct FrameData
{
    mat4 view;
    mat4 projection;
    mat4 viewProjection;
    mat4 inverseView;
    mat4 inverseProjection;
    mat4 inverseViewProjection;
    vec4 cameraPosition;     // w = 1 for orthographic
    vec4 renderSize;         // width, height, 1/width, 1/height of the scene images
    vec4 outputSize;         // the same for the display image
    vec4 background;         // rgb: pre-exposed (Full) or display-linear (other modes)
    vec4 cameraForward;      // world-space view direction, w = near plane
    float exposure;          // scene luminance (cd/m2) x exposure = HDR value
    float nearPlane;
    float farPlane;
    float clusterLogScale;   // slice = log(viewDepth) * scale + bias
    float clusterLogBias;
    uint clusterCountX;
    uint clusterCountY;
    uint clusterCountZ;
    uint lightCount;
    uint viewMode;
    uint flags;              // FRAME_*
    float gridSpacing;
    vec4 gridParams;         // major line every N cells, fade start, fade end, axis width (pixels)
    float time;
    float clusterIndexCapacity;
    float wireframeLineWidth;
    float pad0;
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
    uint firstEdge;
    uint mode;
    vec4 color;
} draw;

#endif
