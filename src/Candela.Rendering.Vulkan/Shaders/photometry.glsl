// Photometric spot lights. Units: intensity in candela, illuminance in lux, luminance in cd/m2.
// beamProfile() is the GPU twin of Candela.Photometry.BeamDistribution.Relative (same parameters, same formulas).
#ifndef CANDELA_PHOTOMETRY_GLSL
#define CANDELA_PHOTOMETRY_GLSL

#include "common.glsl"

// Minimum source distance (Candela.Photometry.Photometrics clamps distances to 1 cm).
const float MIN_LIGHT_DISTANCE = 0.01;

// Relative intensity I(theta)/I(0) at angle theta (rad) from the beam axis.
//  generalised Gaussian: exp(-ln2 (theta/theta_b)^p), times a smoothstep window from the field angle to the cut-off;
//  Lambertian: cos(theta); uniform cone: 1 inside the half beam angle.
// The iris clips the beam with a soft-edged aperture at irisAngle (a gate stop: the centre stays as bright).
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

// Squared distance beyond which a light's contribution is negligible for this view. Full view: where a white
// Lambertian surface lit on axis would reach the display threshold after exposure, exposure I / (pi d^2) = epsilon.
// Lux overlay: where the illuminance falls far below the gradient's range. The renderer sets lightRangeScale.
float lightRangeSquared(Light light)
{
    return light.maxIntensity * frame.lightRangeScale;
}

// Smooth cut at the range: (1 - (d/range)^4)^2. Where a light contributes 100x the threshold the factor is
// 0.9998, so the inverse-square law is untouched wherever the light is visible; the cut only gives lights the
// finite support that clustered culling needs.
float rangeWindow(float distanceSquared, float rangeSquared)
{
    float x = distanceSquared / max(rangeSquared, 1e-30);
    float w = clamp(1.0 - x * x, 0.0, 1.0);
    return w * w;
}

// Henyey-Greenstein phase function (1/sr) for the cosine between the light's propagation direction and the
// direction towards the viewer.
float henyeyGreenstein(float cosTheta, float g)
{
    float g2 = g * g;
    float denom = max(1.0 + g2 - 2.0 * g * cosTheta, 1e-6);
    return (1.0 - g2) / (4.0 * PI * denom * sqrt(denom));
}

#endif
