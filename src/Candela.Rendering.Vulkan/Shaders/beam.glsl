// Volumetric beams: geometry shared by the tile culling (beam_cull.comp) and the per-pixel single scattering
// (beam_scatter.comp). A beam is a spot light that is integrated per pixel instead of being injected into the froxel
// volume (LIGHT_VIEW_BEAM). Its volume is a truncated cone per prism facet: it starts at the lens disc (radius
// sourceRadius) and opens with the facet's bounding half-angle (cut-off, iris), so the shaft leaves the fixture
// with the width of its lens. The per-view list raymarchList[0 .. beamCount) holds the beams in score order; the
// tile masks (one bit per list entry) live in the pass buffer behind BEAM_MASK_HEADER statistics words.
// Units: metres, radians.
#ifndef CANDELA_BEAM_GLSL
#define CANDELA_BEAM_GLSL

#include "lighting.glsl"

#ifdef BEAM_MASKS_WRITABLE
#define BEAM_MASK_ACCESS
#else
#define BEAM_MASK_ACCESS readonly
#endif

// Statistics words (BEAM_MASK_HEADER, cleared every frame) followed by the masks of the tiles.
layout(set = SET_PASS, binding = BINDING_PASS_BUFFER, scalar) BEAM_MASK_ACCESS buffer BeamMaskBuffer
{
    uint beamMasks[];
};

// Half-angle of the cone that bounds one prism facet of a light (the light's bounding angle covers every facet).
float beamFacetHalfAngle(Light light)
{
    bool prism = (light.flags & LIGHT_PRISM) != 0u;
    float spread = prism ? atan(light.prismSinSpread, light.prismCosSpread) : 0.0;
    return clamp(light.boundingAngle - spread, 1e-4, 1.5);
}

// Number of facets of a light (1 without a prism).
uint beamFacetCount(Light light)
{
    return (light.flags & LIGHT_PRISM) != 0u ? max(light.prismFacets, 1u) : 1u;
}

// Rotation into the frame of prism facet k (see lightCookieProfile): rotation axis in light coordinates (right, up,
// direction) and the cosine / sine of the facet tilt.
void beamFacetRotation(Light light, uint k, uint facets, out vec3 axis, out float c, out float s)
{
    float phi = light.prismRotation + 2.0 * PI * float(k) / float(facets);
    axis = vec3(-sin(phi), cos(phi), 0.0);
    c = light.prismCosSpread;
    s = light.prismSinSpread;
}

// World-space axis of prism facet k (the light's axis without a prism): tilted by the spread towards azimuth phi.
vec3 beamFacetAxis(Light light, uint k, uint facets)
{
    if ((light.flags & LIGHT_PRISM) == 0u)
        return light.direction;
    float phi = light.prismRotation + 2.0 * PI * float(k) / float(facets);
    float c = light.prismCosSpread;
    float s = light.prismSinSpread;
    return normalize(light.right * (s * cos(phi)) + light.up * (s * sin(phi)) + light.direction * c);
}

// Light-frame direction (right, up, direction components) rotated into facet k's frame.
vec3 beamToFacet(Light light, uint k, uint facets, vec3 v)
{
    if ((light.flags & LIGHT_PRISM) == 0u)
        return v;
    vec3 u;
    float c, s;
    beamFacetRotation(light, k, facets, u, c, s);
    return v * c - cross(u, v) * s + u * (dot(u, v) * (1.0 - c));
}

// Distance (m) behind the lens of the apex of the truncated cone through the lens rim: R / tan(half-angle).
float beamApexOffset(float lensRadius, float halfAngle)
{
    return lensRadius / max(tan(halfAngle), 1e-4);
}

// Largest value of dot(x, n) over the truncated cone with the lens disc (centre S, axis a, radius lens) and the cap
// at distance h along the axis (radius lens + h tan(half-angle)): the convex hull of the two discs.
float beamConeSupport(vec3 S, vec3 a, float lens, float h, float tanHalf, vec3 n)
{
    float an = dot(a, n);
    float perp = sqrt(max(1.0 - an * an, 0.0));
    float sn = dot(S, n);
    return max(sn + lens * perp, sn + h * an + (lens + h * tanHalf) * perp);
}

// Interval [t0, t1] of the ray o + t d (unit d), t in [0, tMax], inside the solid cone with apex `apex`, unit axis w,
// cosine of the half-angle cosA, between the planes at distances hMin and hMax along the axis from the apex. The
// single-nappe cone is convex, so the result is one interval.
bool beamConeInterval(vec3 o, vec3 d, vec3 apex, vec3 w, float cosA, float hMin, float hMax, float tMax, out float t0, out float t1)
{
    vec3 co = o - apex;
    float dw = dot(d, w);
    float cw = dot(co, w);
    float lo = 0.0;
    float hi = tMax;
    t0 = 0.0;
    t1 = 0.0;
    // Slab hMin <= cw + t dw <= hMax.
    if (abs(dw) < 1e-7)
    {
        if (cw < hMin || cw > hMax)
            return false;
    }
    else
    {
        float ta = (hMin - cw) / dw;
        float tb = (hMax - cw) / dw;
        lo = max(lo, min(ta, tb));
        hi = min(hi, max(ta, tb));
    }

    if (!(lo < hi))
        return false;
    float c2 = cosA * cosA;
    float A = dw * dw - c2;
    float B = 2.0 * (dw * cw - c2 * dot(d, co));
    float C = cw * cw - c2 * dot(co, co);
    if (abs(A) < 1e-7)
    {
        if (abs(B) < 1e-12)
        {
            if (C < 0.0)
                return false;
        }
        else if (B > 0.0)
            lo = max(lo, -C / B);
        else
            hi = min(hi, -C / B);
    }
    else
    {
        float disc = B * B - 4.0 * A * C;
        if (disc < 0.0)
        {
            if (A < 0.0)
                return false;
        }
        else
        {
            float q = sqrt(disc);
            float r0 = (-B - q) / (2.0 * A);
            float r1 = (-B + q) / (2.0 * A);
            float rmin = min(r0, r1);
            float rmax = max(r0, r1);
            if (A < 0.0)
            {
                lo = max(lo, rmin);
                hi = min(hi, rmax);
            }
            else if (!(hi <= rmin || lo >= rmax))
            {
                if (lo >= rmin)
                    lo = max(lo, rmax);
                else
                    hi = min(hi, rmin);
            }
        }
    }

    t0 = lo;
    t1 = hi;
    return t1 > t0;
}

// Length along the axis over which beams are considered (the light's range, at most 2 km).
float beamRange(Light light)
{
    return min(sqrt(max(lightRangeSquared(light), 0.0)), 2000.0);
}

#endif
