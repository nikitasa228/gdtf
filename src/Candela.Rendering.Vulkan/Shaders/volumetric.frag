#version 460
#extension GL_GOOGLE_include_directive : require
// Volumetric composite over the opaque scene (internal resolution, blending dst = src.rgb + src.a x dst):
//  - the integrated froxel volume at the pixel's depth (background: the far end of the volume) gives the in-scattered
//    luminance between the camera and the surface and the transmittance that dims the surface;
//  - narrow beams (field angle below ~15 degrees, the brightest MaxRaymarchedBeams) are ray marched here at full
//    resolution instead of being injected into the froxels, so sharp gobo patterns stay sharp in the haze: the view
//    ray is clipped to the beam's bounding cone (and the light's range and the scene depth) and sampled with
//    RAYMARCH_STEPS jittered steps of sigma_s x HG x I(theta) x cookie x shadow / d^2 x T(camera -> sample).
// Output is pre-exposed (cd/m2 x exposure).

#include "lighting.glsl"
#include "volume.glsl"

layout(location = 0) in vec2 inUv;
layout(location = 0) out vec4 outColor;

const int RAYMARCH_STEPS = 48;

// Interval [t0, t1] of the ray o + t d (unit d), t in [0, tMax], inside the solid cone with apex a, unit axis w,
// cos of the half-angle cosA and length h along the axis. The single-nappe cone is convex, so the result is one
// interval.
bool coneInterval(vec3 o, vec3 d, vec3 a, vec3 w, float cosA, float h, float tMax, out float t0, out float t1)
{
    vec3 co = o - a;
    float dw = dot(d, w);
    float cw = dot(co, w);
    float lo = 0.0;
    float hi = tMax;
    // Front nappe and cap: 0 <= cw + t dw <= h.
    if (abs(dw) < 1e-7)
    {
        if (cw < 0.0 || cw > h)
            return false;
    }
    else
    {
        float ta = -cw / dw;
        float tb = (h - cw) / dw;
        lo = max(lo, min(ta, tb));
        hi = min(hi, max(ta, tb));
    }

    if (lo >= hi)
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

// View-path transmittance from the camera to view depth z (froxel volume, or analytic uniform haze).
float viewTransmittance(vec2 uv, float viewDepth, float distanceAlongRay)
{
    if (hasFeature(FEATURE_VOLUMETRICS))
        return sampleIntegratedVolume(uv, viewDepth).a;
    return hasFeature(FEATURE_HAZE) ? exp(-frame.media.x * distanceAlongRay) : 1.0;
}

void main()
{
    vec2 uv = gl_FragCoord.xy * frame.renderSize.zw;
    float depth = texelFetch(sceneDepth, ivec2(gl_FragCoord.xy), 0).r;
    vec3 origin, direction;
    viewRay(uv, origin, direction);
    float stretch = 1.0 / max(dot(direction, frame.cameraForward.xyz), 1e-4);
    float originDepth = viewDepthOf(origin);
    float viewDepth;
    float tMax;
    if (depth > 0.0)
    {
        vec3 P = worldFromDepth(uv, depth);
        viewDepth = viewDepthOf(P);
        tMax = distance(origin, P);
    }
    else
    {
        viewDepth = frame.volumeDepth.y;
        tMax = (viewDepth - originDepth) * stretch;
    }

    vec4 fog = hasFeature(FEATURE_VOLUMETRICS) ? sampleIntegratedVolume(uv, viewDepth) : vec4(0.0, 0.0, 0.0, 1.0);
    vec3 inscatter = vec3(0.0);
    float jitter = interleavedGradientNoise(gl_FragCoord.xy + vec2(float(frame.frameIndex % 64u) * 5.588238, 0.0));
    float g = frame.media.y;
    for (uint b = 0u; b < frame.raymarchCount; ++b)
    {
        uint index = raymarchList[b];
        Light light = lights[index];
        uint slot = lightViews[index].shadowSlot;
        float range = min(sqrt(lightRangeSquared(light)), frame.volumeDepth.y * 2.0 + distance(light.position, frame.cameraPosition.xyz));
        float t0, t1;
        if (!coneInterval(origin, direction, light.position, light.direction, light.cosCutoff, range, tMax, t0, t1))
            continue;
        float dt = (t1 - t0) / float(RAYMARCH_STEPS);
        vec3 sum = vec3(0.0);
        for (int i = 0; i < RAYMARCH_STEPS; ++i)
        {
            float t = t0 + (float(i) + jitter) * dt;
            vec3 X = origin + direction * t;
            float sigmaS = scatteringAt(X);
            if (sigmaS <= 0.0)
                continue;
            float z = originDepth + t / stretch;
            vec3 L;
            float d2;
            vec3 I = lightIntensityToward(light, X, pixelWorldSize(z), L, d2);
            if (I.r + I.g + I.b <= 0.0)
                continue;
            float shadow = shadowHard(slot, X, vec3(0.0));
            float phase = henyeyGreenstein(dot(-L, -direction), g);
            sum += I * (sigmaS * phase * shadow / d2 * viewTransmittance(uv, z, t));
        }

        inscatter += sum * dt;
    }

    outColor = vec4(finiteLight(fog.rgb + inscatter * frame.exposure), fog.a);
}
