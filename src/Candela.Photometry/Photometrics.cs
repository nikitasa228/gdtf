using System.Numerics;
using Candela.Core.Math;

namespace Candela.Photometry;

/// <summary>
/// Basic photometric relations between luminous intensity (cd), illuminance (lx), luminance (cd/m²) and flux (lm).
/// Distances are in metres, angles in radians.
/// </summary>
public static class Photometrics
{
    /// <summary>
    /// Distances below this (m) are clamped in the inverse-square law: the point-source model is meaningless closer
    /// than the size of a lens, and it keeps 1/d² finite.
    /// </summary>
    public const float MinDistance = 0.01f;

    /// <summary>
    /// Illuminance from a point source by the inverse-square cosine law, E = I(θ)·cos α / d², lx.
    /// </summary>
    /// <param name="peakCandela">On-axis luminous intensity I(0), cd.</param>
    /// <param name="distribution">Beam distribution giving I(θ)/I(0).</param>
    /// <param name="offAxisAngleRad">Angle θ between the beam axis and the direction to the point, rad.</param>
    /// <param name="distanceMetres">Distance from the source to the point, m (clamped to ≥ <see cref="MinDistance"/>).</param>
    /// <param name="cosIncidence">cos α between the surface normal and the direction to the source (negative → 0, lit from behind).</param>
    public static float Illuminance(float peakCandela, in BeamDistribution distribution, float offAxisAngleRad, float distanceMetres,
        float cosIncidence) =>
        Illuminance(peakCandela * distribution.Relative(offAxisAngleRad), distanceMetres, cosIncidence);

    /// <summary>
    /// CPU reference light meter: total illuminance (lx) at <paramref name="point"/> on a surface with normal
    /// <paramref name="normal"/> from <paramref name="lights"/>, without occlusion. Same as
    /// <see cref="LightMeter.IlluminanceAt(ReadOnlySpan{SpotLight}, Vector3, Vector3)"/>, which also offers batch, grid and colour queries.
    /// </summary>
    public static float IlluminanceAt(ReadOnlySpan<SpotLight> lights, Vector3 point, Vector3 normal) =>
        LightMeter.IlluminanceAt(lights, point, normal);

    /// <summary>Illuminance E = I·cos α / d², lx, from intensity <paramref name="intensityCandela"/> toward the point.</summary>
    public static float Illuminance(float intensityCandela, float distanceMetres, float cosIncidence)
    {
        if (!(intensityCandela > 0f) || !(cosIncidence > 0f) || !float.IsFinite(intensityCandela))
            return 0f;
        float d = float.IsFinite(distanceMetres) ? MathF.Max(distanceMetres, MinDistance) : float.PositiveInfinity;
        return intensityCandela * MathF.Min(cosIncidence, 1f) / (d * d);
    }

    /// <summary>
    /// Intensity that produces <paramref name="lux"/> at <paramref name="distanceMetres"/> on a surface facing the
    /// source: I = E·d², cd. Turns datasheet values such as "12 000 lx at 5 m" into candela.
    /// </summary>
    public static float IntensityFromIlluminance(float lux, float distanceMetres)
    {
        float d = MathF.Max(distanceMetres, MinDistance);
        return float.IsFinite(lux) && float.IsFinite(d) ? MathF.Max(lux, 0f) * d * d : 0f;
    }

    /// <summary>Luminance of a Lambertian (matte) surface, L = ρ·E/π, cd/m², for reflectance ρ (0..1) and illuminance E (lx).</summary>
    public static float LuminanceLambertian(float albedo, float illuminanceLux) =>
        Math.Clamp(albedo, 0f, 1f) * MathF.Max(illuminanceLux, 0f) / MathF.PI;

    /// <summary>Lux → foot-candles (1 fc = 1 lm/ft² = 10.7639 lx).</summary>
    public static float LuxToFootCandles(float lux) => lux / Units.LuxPerFootCandle;

    /// <summary>Foot-candles → lux.</summary>
    public static float FootCandlesToLux(float footCandles) => footCandles * Units.LuxPerFootCandle;

    /// <summary>Diameter of a beam of full angle <paramref name="fullAngleRad"/> at <paramref name="distanceMetres"/>: 2·d·tan(angle/2), m.</summary>
    public static float BeamDiameter(float fullAngleRad, float distanceMetres) =>
        2f * distanceMetres * MathF.Tan(0.5f * Math.Clamp(fullAngleRad, 0f, 0.999f * MathF.PI));

    /// <summary>
    /// Illuminance and colour of light given as spectral irradiance (W·m⁻²·nm⁻¹): E = K_m·Σ E_λ·V(λ)·Δλ, lx.
    /// </summary>
    public static (float Lux, LightColor Color) IlluminanceFromSpectralIrradiance(Spectrum spectralIrradiance)
    {
        ArgumentNullException.ThrowIfNull(spectralIrradiance);
        var xyz = Cie1931.XyzFromSpectrum(spectralIrradiance);
        return ((float)(Cie1931.Km * MathF.Max(xyz.Y, 0f)), LightColor.FromXyz(xyz));
    }
}
