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

// A placed mesh (192 bytes). Matrices are System.Numerics matrices uploaded verbatim: M * v in GLSL = v * M in C#.
struct Instance
{
    mat4 world;
    mat4 normalMatrix;       // inverse transpose of the upper 3x3
    uint flags;              // Candela.Rendering.InstanceFlags
    uint pickId;
    uint materialSlotOffset; // first entry in materialSlots[]
    uint materialSlotCount;
    vec4 boundingSphere;     // world-space centre + radius
    vec3 emission;           // additive emitted luminance, Rec.2020 cd/m2 (IRenderWorld.SetInstanceEmission: lens glow)
    float emissionSharpness; // 1 / half-angle^2 of the directional falloff 2^-(theta^2 x sharpness), 0 = uniform
    vec3 emissionAxis;       // world direction the directional emission is seen from (along which the light leaves)
    float pad0;
};

// Additive emission of an instance seen from direction V (unit, surface to eye).
vec3 instanceEmission(Instance inst, vec3 V)
{
    if (inst.emissionSharpness <= 0.0)
        return inst.emission;
    float theta = acos(clamp(dot(V, inst.emissionAxis), -1.0, 1.0));
    return inst.emission * exp2(-inst.emissionSharpness * theta * theta);
}

// Spot light (224 bytes), packed by LightPacker.cs. The profile parameters are those of
// Candela.Photometry.BeamDistribution; beamProfile() in photometry.glsl evaluates its Relative(theta). With a prism
// every facet is a copy of this profile around its own (tilted) axis carrying `intensity` (= peak / facets).
// Gate coordinates: the direction from the light in the light frame (right, up, direction) projected onto the plane
// at unit distance and scaled by gateScale, so the field-angle circle (before frost) is the unit circle.
struct Light
{
    vec3 position;
    float maxIntensity;      // largest channel of the on-axis intensity of the whole fixture, cd (sets the range)
    vec3 direction;
    float cosCutoff;         // cos of the bounding cone (cut-off, iris and prism spread): zero intensity outside
    vec3 intensity;          // linear Rec.2020 colour (luminance 1) x peak candela of one prism facet
    float invHalfBeam;       // 1 / half beam angle
    float exponent;          // p of exp(-ln2 (theta/theta_b)^p)
    float halfField;         // half field angle (start of the smoothstep window)
    float invWindow;         // 1 / (cutoff - halfField)
    float cutoff;            // half-angle with zero intensity beyond (per facet)
    float irisAngle;         // half-angle of the iris aperture (4 rad when open)
    float irisSoftStart;     // the iris edge fades from here to irisAngle
    uint shape;              // SHAPE_*
    uint flags;              // LIGHT_*
    vec3 up;                 // gobo/blade orientation (perpendicular to direction)
    float sourceRadius;      // exit lens radius, m (soft shadows)
    vec3 right;              // cross(direction, up)
    float gateScale;         // 1 / tan(half field angle before frost)
    uint goboTexture;        // bindless slot (LIGHT_GOBO)
    float goboBlur;          // defocus radius as a fraction of the gobo image
    float edgeSoftness;      // half width of the blade edges in gate units
    uint prismFacets;        // 0 = no prism
    vec2 goboRotation;       // cos, sin of the gobo rotation
    float prismCosSpread;    // cos/sin of the facet tilt
    float prismSinSpread;
    float prismRotation;     // azimuth of the first facet, rad
    float boundingAngle;     // acos(cosCutoff)
    float solidAngle;        // effective solid angle of one facet's profile, sr (flux = intensity x solidAngle x facets)
    float beamLayer;         // layer coordinate of the beam profile in the beam scattering table (family x exponent)
    vec4 blades[4];          // A..D: xy = edge normal in gate coordinates, z = offset (blocked where dot(p, xy) > z), w = 1 when inserted
};

// Per-view data of a light (16 bytes).
struct LightView
{
    uint shadowSlot;         // shadow atlas tile or NO_SHADOW
    uint flags;              // LIGHT_VIEW_*
    uint vplOffset;          // reflective shadow maps: first virtual point light
    uint vplCount;
};

// A shadow atlas tile (192 bytes).
struct ShadowSlot
{
    mat4 viewProjection;     // world -> light clip (reverse-Z, infinite far plane: depth = near / z)
    mat4 inverseViewProjection;
    vec4 atlasRect;          // atlas uv offset (xy) and size (zw) of the tile
    vec3 position;
    float near;
    vec3 axis;
    float tanHalfAngle;
    float texelScale;        // world size of one texel per metre of depth: 2 tan(half angle) / resolution
    float resolution;
    float atlasTexel;        // 1 / atlas size
    uint flags;              // bit 0: reflective shadow map present
};

// A virtual point light (48 bytes): a surface element lit by a spot light that re-emits its reflected flux
// diffusely (intensity flux / pi x cos theta, cd).
struct Vpl
{
    vec3 position;
    float pad0;
    vec3 normal;
    float pad1;
    vec3 flux;
    float pad2;
};

// Per-view constants (uniform block, 992 bytes).
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
    vec4 renderSize;         // scene images (internal resolution): width, height, 1/width, 1/height
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
    uint clusterMaskWords;   // light mask words per cluster (one bit per listed light), see view.glsl
    vec4 headlight;          // Colorless: unit direction towards the key light (world space)
    mat4 previousViewProjection; // the target's previous frame (temporal reprojection)
    vec4 displaySize;        // display image: width, height, 1/width, 1/height
    vec4 volumeGrid;         // froxel counts x, y, z and 1/z
    vec4 volumeDepth;        // froxel near, far (view depth, m), slices / log(far / near), log(near)
    vec4 volumeJitter;       // sub-froxel jitter of this frame (x, y, z in froxel units, -0.5..0.5)
    vec4 media;              // haze sigma_s (1/m), Henyey-Greenstein g, smoke sigma_s at density 1 (1/m), flame absorption (1/m)
    vec4 smokeMin;           // world box of the smoke grid
    vec4 smokeMax;
    vec4 flameMin;           // union box of the flame grids (w = 1 when present)
    vec4 flameMax;           // w = number of flame grids (records after the blackbody table)
    vec4 noiseMin;           // world box of the noise field
    vec4 noiseMax;
    vec4 flameLut;           // blackbody LUT: first temperature (K), entries per kelvin, entries, last temperature
    vec4 overlayRange;       // overlay gradient: RangeMin, RangeMax, logarithmic (0/1), noise fog strength
    vec4 overlayOutOfRange;  // colour outside the range (sRGB + alpha)
    vec4 selectionColor;     // selection outline colour (sRGB + alpha)
    vec4 pixelFootprint;     // world size of a pixel: x = per metre of view depth (perspective), y = constant (orthographic)
    float lightRangeScale;   // squared light range = maxIntensity x lightRangeScale
    uint beamCount;          // volumetric beams integrated per pixel (raymarchList[0 .. beamCount))
    uint features;           // FEATURE_*
    uint overlayMode;        // OVERLAY_*
    uint vplCount;
    uint frameIndex;         // frames rendered into this target (temporal jitter)
    uint noiseSourceCount;
    float bloomIntensity;
    float indirectIntensity;
    float renderScale;       // internal / display resolution
    uint shadowTileCount;
    float volumeHistoryWeight; // weight of the reprojected froxel history (0 = none)
    uvec2 beamTiles;         // beam culling tiles (BEAM_TILE_SIZE half-resolution pixels each), x and y
    uint beamWordsPerTile;   // 32-bit mask words per tile (one bit per beam of raymarchList)
    uint beamFlags;          // BEAM_FLAG_*
    float beamHistoryWeight; // weight of the accumulated beam modulation ratio (0 = none)
    float beamQuality;       // 0.25 .. 1: scales the segments and cookie samples per beam crossing (load shedding)
    float beamSigmaRef;      // scattering coefficient (1/m) of the beam envelope image (temporal ratio reference)
    uint clusterListedLights; // lights the cluster masks list (lights[0 .. clusterListedLights)); the rest are not drawn
    float ambientLuminance;  // Full view: luminance (cd/m2) of the uniform surroundings lighting every surface; 0 = none
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
    uint mode;               // DRAW_MODE_* (meshes), TONE_* (tone mapping), tile (shadows), level (bloom)
    float width;             // line width in pixels
    vec4 color;              // constant colour (wireframe, edges)
} draw;

// Largest value written to the half-float light images (HDR, froxel volumes: pre-exposed; indirect irradiance: lx).
// Half floats overflow at 65504 and the conversion may give +infinity (implementation-defined), which would poison
// blending, the bloom pyramid and temporal histories with inf/NaN. Pre-exposed 30000 is 15 stops above display
// white (the tone mapper saturates long before), so the sum of two clamped values in a blend still fits.
const float HALF_SAFE_MAX = 30000.0;

// View depth (m) the volumetric beams use for the background: beyond the range of every beam (at most 2 km), so a
// beam into the sky or an open venue is drawn to its end, not cut at the far end of the froxel volume.
const float BEAM_BACKGROUND_DEPTH = 100000.0;

// NaN -> 0, then clamped to [0, HALF_SAFE_MAX] (+inf -> HALF_SAFE_MAX).
vec3 finiteLight(vec3 c)
{
    c = mix(c, vec3(0.0), isnan(c));
    return clamp(c, vec3(0.0), vec3(HALF_SAFE_MAX));
}

bool isOrthographic()
{
    return (frame.flags & FRAME_ORTHOGRAPHIC) != 0u;
}

bool hasFeature(uint feature)
{
    return (frame.features & feature) != 0u;
}

// World size of one pixel of the scene images at a view depth (gobo filtering, froxel footprints).
float pixelWorldSize(float viewDepth)
{
    return isOrthographic() ? frame.pixelFootprint.y : frame.pixelFootprint.x * max(viewDepth, 0.0);
}

// Interleaved gradient noise in [0, 1) (Jimenez 2014): cheap per-pixel dither.
float interleavedGradientNoise(vec2 p)
{
    return fract(52.9829189 * fract(dot(p, vec2(0.06711056, 0.00583715))));
}

// Integer hash (PCG-style) mapped to [0, 1).
float hashToUnit(uint x)
{
    x ^= x >> 16;
    x *= 0x7feb352du;
    x ^= x >> 15;
    x *= 0x846ca68bu;
    x ^= x >> 16;
    return float(x >> 8) * (1.0 / 16777216.0);
}

// Octahedral encoding of a unit normal into [0, 1]^2 (and back).
vec2 encodeNormal(vec3 n)
{
    n /= abs(n.x) + abs(n.y) + abs(n.z);
    vec2 e = n.z >= 0.0 ? n.xy : (1.0 - abs(n.yx)) * vec2(n.x >= 0.0 ? 1.0 : -1.0, n.y >= 0.0 ? 1.0 : -1.0);
    return e * 0.5 + 0.5;
}

vec3 decodeNormal(vec2 e)
{
    e = e * 2.0 - 1.0;
    vec3 n = vec3(e, 1.0 - abs(e.x) - abs(e.y));
    float t = clamp(-n.z, 0.0, 1.0);
    n.x += n.x >= 0.0 ? -t : t;
    n.y += n.y >= 0.0 ? -t : t;
    return normalize(n);
}

#endif
