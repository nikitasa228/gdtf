// Path tracer: set 2 declarations shared by its passes (accumulation images, parameters, the exposure histogram),
// random numbers and sampling helpers. Units: metres, candela, cd/m2; colours linear Rec.2020.
#ifndef CANDELA_PT_COMMON_GLSL
#define CANDELA_PT_COMMON_GLSL

#include "common.glsl"
#include "color.glsl"

// Path tracer parameters (uniform block, 112 bytes), mirrored by GpuPtParams.
struct PtParams
{
    vec4 mediaMin;        // xyz: box the haze fills (scene bounds grown by the beam reach)
    vec4 mediaMax;        // xyz: box; w = haze scattering coefficient inside it (1/m)
    vec4 majorants;       // x = smoke scattering majorant (1/m), y = flame absorption (1/m), z = Henyey-Greenstein g, w = 1 / exposure
    vec4 background;      // rgb = background luminance (Rec.2020 cd/m2), w = firefly clamp (cd/m2; 0 = off)
    vec4 geometry;        // x = lens clearance of shadow rays (m), y = disc offset in front of the lens (m), z = polygon radius factor, w = unused
    uint lightCount;
    uint maxBounces;
    uint maxVolumeBounces;
    uint flags;           // PT_FLAG_*
    uint seed;
    uint width;
    uint height;
    float ambient;        // luminance of the uniform surroundings lighting the surfaces (cd/m2): rays leaving a surface
                          // that reach nothing add it on top of the background, unattenuated by the media (not seen
                          // directly, not lighting the media)
};

layout(set = SET_PASS, binding = BINDING_PT_PARAMS, scalar) uniform PtParamsBlock
{
    PtParams params;
};

// Luminance histogram of the auto exposure (same bins as histogram.comp).
layout(set = SET_PASS, binding = BINDING_PT_HISTOGRAM, scalar) buffer PtHistogramBuffer
{
    uint ptHistogram[HISTOGRAM_BINS];
};

// ------------------------------------------------------------------------------------------------ random numbers

uint pcgHash(uint v)
{
    uint state = v * 747796405u + 2891336453u;
    uint word = ((state >> ((state >> 28u) + 4u)) ^ state) * 277803737u;
    return (word >> 22u) ^ word;
}

// Seed of the stream of one pixel and sample: a hash of both and of the render's seed (deterministic).
uint rngSeed(uvec2 pixel, uint sampleIndex, uint seed)
{
    return pcgHash(pixel.x + pcgHash(pixel.y + pcgHash(sampleIndex + pcgHash(seed))));
}

// Next uniform number in [0, 1) of a PCG stream.
float rand(inout uint state)
{
    state = state * 747796405u + 2891336453u;
    uint word = ((state >> ((state >> 28u) + 4u)) ^ state) * 277803737u;
    word = (word >> 22u) ^ word;
    return float(word >> 8u) * (1.0 / 16777216.0);
}

// ------------------------------------------------------------------------------------------------ geometry helpers

// Orthonormal basis around a unit vector (Duff et al. 2017).
void basis(vec3 n, out vec3 t, out vec3 b)
{
    float s = n.z >= 0.0 ? 1.0 : -1.0;
    float a = -1.0 / (s + n.z);
    float c = n.x * n.y * a;
    t = vec3(1.0 + s * n.x * n.x * a, s * c, -s * n.x);
    b = vec3(c, s + n.y * n.y * a, -n.y);
}

// Ray origin offset off a surface along its geometric normal n (Waechter and Binder, Ray Tracing Gems ch. 6):
// integer ulp steps far from the origin, a small float step near it. Robust at any scene scale.
vec3 offsetRay(vec3 p, vec3 n)
{
    const float originRange = 1.0 / 32.0;
    const float floatScale = 1.0 / 65536.0;
    const float intScale = 256.0;
    ivec3 ofi = ivec3(intScale * n);
    vec3 pi = vec3(intBitsToFloat(floatBitsToInt(p.x) + (p.x < 0.0 ? -ofi.x : ofi.x)),
                   intBitsToFloat(floatBitsToInt(p.y) + (p.y < 0.0 ? -ofi.y : ofi.y)),
                   intBitsToFloat(floatBitsToInt(p.z) + (p.z < 0.0 ? -ofi.z : ofi.z)));
    return vec3(abs(p.x) < originRange ? p.x + floatScale * n.x : pi.x,
                abs(p.y) < originRange ? p.y + floatScale * n.y : pi.y,
                abs(p.z) < originRange ? p.z + floatScale * n.z : pi.z);
}

// Parametric interval [t0, t1] where the ray o + t d is inside the box; empty when t0 > t1.
vec2 rayBox(vec3 o, vec3 d, vec3 boxMin, vec3 boxMax)
{
    vec3 inv = 1.0 / d;
    vec3 a = (boxMin - o) * inv;
    vec3 b = (boxMax - o) * inv;
    vec3 lo = min(a, b);
    vec3 hi = max(a, b);
    // NaN (0 * inf, a ray in a box face) is ignored by max/min of the other axes
    float t0 = max(max(lo.x, lo.y), lo.z);
    float t1 = min(min(hi.x, hi.y), hi.z);
    return vec2(t0, t1);
}

// Cosine-weighted direction around n.
vec3 sampleCosine(vec3 n, vec2 u)
{
    float r = sqrt(u.x);
    float phi = 2.0 * PI * u.y;
    vec3 t, b;
    basis(n, t, b);
    return normalize(t * (r * cos(phi)) + b * (r * sin(phi)) + n * sqrt(max(1.0 - u.x, 0.0)));
}

// Henyey-Greenstein: a direction around `forward` with pdf HG(g, dot(forward, w)).
vec3 sampleHenyeyGreenstein(vec3 forward, float g, vec2 u)
{
    float cosTheta;
    if (abs(g) < 1e-3)
    {
        cosTheta = 1.0 - 2.0 * u.x;
    }
    else
    {
        float s = (1.0 - g * g) / (1.0 - g + 2.0 * g * u.x);
        cosTheta = clamp((1.0 + g * g - s * s) / (2.0 * g), -1.0, 1.0);
    }

    float sinTheta = sqrt(max(1.0 - cosTheta * cosTheta, 0.0));
    float phi = 2.0 * PI * u.y;
    vec3 t, b;
    basis(forward, t, b);
    return normalize(t * (sinTheta * cos(phi)) + b * (sinTheta * sin(phi)) + forward * cosTheta);
}

// Tested on the bits (common.glsl: isnan / isinf may be folded away by a compiler that assumes finite math).
bool isFiniteVec(vec3 v)
{
    return allFinite(vec4(v, 0.0));
}

#endif
