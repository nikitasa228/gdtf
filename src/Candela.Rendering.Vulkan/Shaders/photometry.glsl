// Photometric spot lights. Units: intensity in candela, illuminance in lux, luminance in cd/m2.
// beamProfile() is the GPU twin of Candela.Photometry.BeamDistribution.Relative (same parameters, same formulas).
#ifndef CANDELA_PHOTOMETRY_GLSL
#define CANDELA_PHOTOMETRY_GLSL

#include "common.glsl"

// Minimum source distance (Candela.Photometry.Photometrics.MinDistance = 1 cm).
const float MIN_LIGHT_DISTANCE = 0.01;

// Relative intensity I(theta)/I(0) at angle theta (rad) from the beam axis.
//  generalised Gaussian: exp(-ln2 (theta/theta_b)^p), times a smoothstep window from the field angle to the cut-off;
//  Lambertian: cos(theta); uniform cone: 1 inside the half beam angle.
// The iris narrows the beam with a soft-edged aperture at irisAngle (a gate stop: the centre stays as bright).
float beamProfile(Light light, float theta)
{
    float g;
    if (light.shape == SHAPE_LAMBERTIAN)
    {
        g = theta < 0.5 * PI ? cos(theta) : 0.0;
    }
    else if (light.shape == SHAPE_UNIFORM_CONE)
    {
        g = theta * light.invHalfBeam <= 1.0 ? 1.0 : 0.0;
    }
    else
    {
        if (theta >= light.cutoff)
            return 0.0;
        float r = theta * light.invHalfBeam;
        g = r > 0.0 ? exp(-LN2 * exp(light.exponent * log(r))) : 1.0;
        if (theta > light.halfField)
        {
            float s = (theta - light.halfField) * light.invWindow;
            g *= 1.0 - s * s * (3.0 - 2.0 * s);
        }
    }

    if (theta > light.irisSoftStart)
        g *= 1.0 - smoothstep(light.irisSoftStart, light.irisAngle, theta);
    return g;
}

// Smooth cut where the contribution is negligible: 1 - (d/range)^4, squared. At d = range / 3 the factor is
// 0.975, so the inverse-square law is untouched wherever the light matters; the range comes from the exposure.
float rangeWindow(float distanceSquared, Light light)
{
    float x = distanceSquared * light.invRange2;
    float w = clamp(1.0 - x * x, 0.0, 1.0);
    return w * w;
}

// Illuminance (lux, per Rec.2020 channel) that a spot light produces on a surface at P with normal N, and the unit
// direction towards the light. E = I(theta) * cos(incidence) / d^2 — inverse square, no artificial attenuation.
vec3 spotIlluminance(Light light, vec3 P, vec3 N, out vec3 L)
{
    vec3 toLight = light.position - P;
    float d2 = max(dot(toLight, toLight), MIN_LIGHT_DISTANCE * MIN_LIGHT_DISTANCE);
    L = toLight * inversesqrt(d2);
    float cosIncidence = dot(N, L);
    if (cosIncidence <= 0.0)
        return vec3(0.0);
    vec3 fromLight = -L;
    float cosTheta = dot(fromLight, light.direction);
    if (cosTheta < light.cosCutoff)
        return vec3(0.0);
    // atan2 of |sin| and cos keeps full precision near the axis (acos does not).
    float theta = atan(length(cross(fromLight, light.direction)), cosTheta);
    float profile = beamProfile(light, theta);
    return light.intensity * (profile * rangeWindow(d2, light) * cosIncidence / d2);
}

#endif
