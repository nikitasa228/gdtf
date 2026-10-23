// Participating media and the froxel volume: froxel <-> view-depth mapping (exponential slices), the scattering and
// extinction coefficients of haze + smoke, flame emission (blackbody LUT) and lookups into the integrated volume.
// Units: coefficients 1/m, luminance cd/m2.
#ifndef CANDELA_VOLUME_GLSL
#define CANDELA_VOLUME_GLSL

#include "view.glsl"
#include "color.glsl"

// View depth (m) at continuous slice coordinate s in [0, slices]: near * (far / near)^(s / slices).
float froxelDepth(float s)
{
    return exp(s / frame.volumeDepth.z + frame.volumeDepth.w);
}

// Continuous slice coordinate of a view depth.
float froxelSlice(float viewDepth)
{
    return (log(max(viewDepth, frame.volumeDepth.x)) - frame.volumeDepth.w) * frame.volumeDepth.z;
}

// Integrated in-scattering (rgb, cd/m2) and transmittance (a) from the camera to view depth z along the ray through
// normalised screen position uv. Texel k of the integrated volume holds the value at the far boundary of slice k,
// so slice coordinate s maps to texel coordinate s - 0.5; inside the first slice the value is interpolated from 0.
vec4 sampleIntegratedVolume(vec2 uv, float viewDepth)
{
    float s = finiteClamp(froxelSlice(viewDepth), 0.0, frame.volumeGrid.z);
    vec4 v = textureLod(volumeTexture, vec3(uv, (s - 0.5) * frame.volumeGrid.w), 0.0);
    // What the filter returns is checked on its bits: no non-finite light or transmittance reaches a blend (NaN alpha
    // would turn the whole pixel and every later blend of it into NaN).
    v = vec4(finiteLight(v.rgb), finiteClamp(v.a, 0.0, 1.0));
    if (s < 1.0)
        v = mix(vec4(0.0, 0.0, 0.0, 1.0), v, s);
    return v;
}

// Normalised coordinates of P in a world box, false when outside.
bool boxCoordinates(vec3 P, vec4 boxMin, vec4 boxMax, out vec3 uvw)
{
    uvw = (P - boxMin.xyz) / max(boxMax.xyz - boxMin.xyz, vec3(1e-6));
    return all(greaterThanEqual(uvw, vec3(0.0))) && all(lessThanEqual(uvw, vec3(1.0)));
}

// Scattering coefficient (1/m) at P: uniform haze + smoke density (trilinear) x scattering at full density.
float scatteringAt(vec3 P)
{
    float sigma = hasFeature(FEATURE_HAZE) ? frame.media.x : 0.0;
    vec3 uvw;
    if (hasFeature(FEATURE_SMOKE) && boxCoordinates(P, frame.smokeMin, frame.smokeMax, uvw))
        sigma += max(textureLod(smokeTexture, uvw, 0.0).r, 0.0) * frame.media.z;
    return sigma;
}

// Blackbody luminance (rgb = Rec.2020 colour x cd/m2) at temperature T (K), linear interpolation in the LUT.
vec3 blackbodyLuminance(float kelvin)
{
    if (kelvin <= frame.flameLut.x)
        return vec3(0.0);
    float x = min((kelvin - frame.flameLut.x) * frame.flameLut.y, frame.flameLut.z - 1.0);
    int i = int(floor(x));
    int j = min(i + 1, int(frame.flameLut.z) - 1);
    float f = x - float(i);
    vec4 a = flameLutEntries[i];
    vec4 b = flameLutEntries[j];
    // interpolate the luminance logarithmically (it spans decades between neighbouring kelvins)
    float la = log(max(a.w, 1e-30));
    float lb = log(max(b.w, 1e-30));
    float luminance = a.w > 0.0 && b.w > 0.0 ? exp(mix(la, lb, f)) : mix(a.w, b.w, f);
    return mix(a.rgb, b.rgb, f) * luminance;
}

// Flame temperature (K) at P: the hottest of the flame grids whose box contains P. Every grid is a block of the flame
// atlas; its record (after the blackbody table) holds the box, the block's offset and its size in texels, and the
// lookup is clamped to the block's own texels so neighbouring grids never bleed into each other.
float flameTemperatureAt(vec3 P)
{
    vec3 uvw;
    if (!boxCoordinates(P, frame.flameMin, frame.flameMax, uvw))
        return 0.0;
    uint count = uint(frame.flameMax.w);
    uint first = uint(frame.flameLut.z);
    vec3 atlas = vec3(textureSize(flameTexture, 0));
    float kelvin = 0.0;
    for (uint i = 0u; i < count; ++i)
    {
        uint r = first + 4u * i;
        vec4 size = flameLutEntries[r + 3u];
        if (!boxCoordinates(P, flameLutEntries[r], flameLutEntries[r + 1u], uvw))
            continue;
        vec3 texel = clamp(uvw * size.xyz, vec3(0.5), size.xyz - 0.5) + flameLutEntries[r + 2u].xyz;
        kelvin = max(kelvin, textureLod(flameTexture, texel / atlas, 0.0).r);
    }

    return kelvin;
}

// Flame emission coefficient (cd/m2 per metre) and absorption (1/m) at P: a flame of temperature T emits like a
// black body with absorption coefficient kappa (Kirchhoff): source = kappa x L_bb(T).
vec3 flameEmissionAt(vec3 P, out float absorption)
{
    absorption = 0.0;
    if (!hasFeature(FEATURE_FLAME))
        return vec3(0.0);
    float kelvin = flameTemperatureAt(P);
    if (kelvin <= frame.flameLut.x)
        return vec3(0.0);
    absorption = frame.media.w;
    return absorption * blackbodyLuminance(kelvin);
}

// Free-field sound pressure level (dB) at P: energetic sum of L1m - 20 log10(max(d, 0.1)) over the noise sources
// (Candela.Photometry.Acoustics), or the noise field when one is given. -1e9 = silence.
float soundLevelAt(vec3 P)
{
    vec3 uvw;
    if (hasFeature(FEATURE_NOISE_FIELD))
        return boxCoordinates(P, frame.noiseMin, frame.noiseMax, uvw) ? textureLod(noiseFieldTexture, uvw, 0.0).r : -1e9;
    float maxLevel = -1e9;
    float sum = 0.0;
    for (uint i = 0u; i < frame.noiseSourceCount; ++i)
    {
        vec4 s = noiseSources[i];
        float level = s.w - 20.0 * log(max(distance(P, s.xyz), 0.1)) / log(10.0);
        if (level > maxLevel)
        {
            sum = sum * exp2((maxLevel - level) * (log2(10.0) / 10.0)) + 1.0;
            maxLevel = level;
        }
        else
        {
            sum += exp2((level - maxLevel) * (log2(10.0) / 10.0));
        }
    }

    return maxLevel <= -1e8 ? -1e9 : maxLevel + 10.0 * log(sum) / log(10.0);
}

// Scattering coefficient (1/m) of the air at P for the smoke overlay: the uniform haze plus the smoke grid's density x
// scattering at full density, in every view mode (unlike scatteringAt, which follows the Full view's media features).
// frame.media.x / .z are 0 without haze / smoke; frame.smokeMin.w marks a smoke grid.
float smokeOverlayAt(vec3 P)
{
    float sigma = max(frame.media.x, 0.0);
    vec3 uvw;
    if (frame.smokeMin.w > 0.5 && boxCoordinates(P, frame.smokeMin, frame.smokeMax, uvw))
        sigma += finiteClamp(textureLod(smokeTexture, uvw, 0.0).r, 0.0, 1e6) * max(frame.media.z, 0.0);
    return sigma;
}

// Position of a value in the overlay gradient's range, 0 ... 1 (on the gradient's log scale when overlayRange.z is
// set and `followScale` is true, else linear), or -1 below the range or when the range is empty.
float overlayRangePosition(float value, bool followScale)
{
    float lo = frame.overlayRange.x;
    float hi = frame.overlayRange.y;
    if (!(value >= lo) || !(hi > lo))
        return -1.0;
    if (followScale && frame.overlayRange.z > 0.5)
    {
        float a = log(max(lo, 1e-6));
        float b = log(max(hi, 1e-6));
        return b > a ? clamp((log(max(value, 1e-6)) - a) / (b - a), 0.0, 1.0) : 0.0;
    }

    return clamp((value - lo) / (hi - lo), 0.0, 1.0);
}

// Overlay gradient lookup for a value in the overlay's unit: sRGB colour + alpha, or the out-of-range colour.
vec4 overlayGradient(float value)
{
    float lo = frame.overlayRange.x;
    float hi = frame.overlayRange.y;
    if (!(value >= lo && value <= hi) || hi <= lo)
        return frame.overlayOutOfRange;
    float t;
    if (frame.overlayRange.z > 0.5)
    {
        float a = log(max(lo, 1e-6));
        float b = log(max(hi, 1e-6));
        t = b > a ? (log(max(value, 1e-6)) - a) / (b - a) : 0.0;
    }
    else
    {
        t = (value - lo) / (hi - lo);
    }

    float n = float(textureSize(gradientLut, 0).x);
    return textureLod(gradientLut, vec2((clamp(t, 0.0, 1.0) * (n - 1.0) + 0.5) / n, 0.5), 0.0);
}

#endif
