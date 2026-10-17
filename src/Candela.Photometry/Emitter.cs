using System.Numerics;
using Candela.Core.SceneGraph.Components;

namespace Candela.Photometry;

/// <summary>
/// One additive light source of a fixture (an LED colour, a white engine, a lamp): its CIE 1931 chromaticity and the
/// luminous flux it emits at full level, optionally with its spectral shape. Built from GDTF emitter data, measured
/// spectra or <see cref="StandardEmitters"/> when a fixture file has no emitter data.
/// </summary>
/// <remarks>
/// Invalid data is sanitised rather than rejected so that one bad emitter in a fixture file cannot break a fixture:
/// a non-finite chromaticity or one with y ≤ 0 becomes D65 white, coordinates are clamped to the valid triangle, and a
/// negative or non-finite flux becomes 0 (the emitter then contributes nothing).
/// </remarks>
public readonly struct Emitter
{
    private readonly string? _name;

    /// <summary>Creates an emitter.</summary>
    /// <param name="name">Display name (e.g. "Red").</param>
    /// <param name="chromaticity">CIE 1931 (x, y).</param>
    /// <param name="maxFluxLm">Luminous flux at full level, lm.</param>
    /// <param name="spectrum">Optional relative spectral power distribution (any scale); used for spectral mixing and filtering.</param>
    public Emitter(string name, Vector2 chromaticity, float maxFluxLm, Spectrum? spectrum = null)
    {
        _name = name;
        Chromaticity = SanitizeChromaticity(chromaticity);
        MaxFlux = float.IsFinite(maxFluxLm) && maxFluxLm > 0f ? maxFluxLm : 0f;
        Spectrum = spectrum;
        float x = Chromaticity.X, y = Chromaticity.Y;
        TristimulusPerLumen = new Vector3(x / y, 1f, (1f - x - y) / y);
    }

    /// <summary>Display name.</summary>
    public string Name => _name ?? "";

    /// <summary>CIE 1931 chromaticity (x, y) of the emitted light.</summary>
    public Vector2 Chromaticity { get; }

    /// <summary>Luminous flux at full level (1.0), lm.</summary>
    public float MaxFlux { get; }

    /// <summary>Relative spectral power distribution, if known.</summary>
    public Spectrum? Spectrum { get; }

    /// <summary>Tristimulus values (X, Y, Z) of one lumen of this emitter's light, with Y = 1.</summary>
    public Vector3 TristimulusPerLumen { get; }

    /// <summary>Light colour of the emitter.</summary>
    public LightColor Color => LightColor.FromXy(Chromaticity);

    /// <summary>Emitter of chromaticity (<paramref name="x"/>, <paramref name="y"/>) and full-level flux <paramref name="maxFluxLm"/>.</summary>
    public static Emitter FromXy(string name, float x, float y, float maxFluxLm) => new(name, new Vector2(x, y), maxFluxLm);

    /// <summary>
    /// Emitter from a GDTF emitter colour: chromaticity (x, y) and Y = the emitter's luminous output in percent of the
    /// beam's luminous flux <paramref name="beamFluxLm"/> (GDTF 1.2 Emitter "Color": Y relative to the LuminousFlux of
    /// the related beam). A non-finite Y counts as 100 %, a negative one as 0 %.
    /// </summary>
    public static Emitter FromXyY(string name, CieColor color, float beamFluxLm)
    {
        float share = float.IsFinite(color.BigY) ? MathF.Max(color.BigY, 0f) / 100f : 1f;
        return new Emitter(name, new Vector2(color.X, color.Y), beamFluxLm * share);
    }

    /// <summary>
    /// Emitter whose chromaticity is computed from a relative spectral distribution, with full-level flux
    /// <paramref name="maxFluxLm"/>.
    /// </summary>
    public static Emitter FromSpectrum(string name, Spectrum relativeSpectrum, float maxFluxLm)
    {
        ArgumentNullException.ThrowIfNull(relativeSpectrum);
        return new Emitter(name, Cie1931.ChromaticityFromSpectrum(relativeSpectrum), maxFluxLm, relativeSpectrum);
    }

    /// <summary>
    /// Emitter from an absolute spectral power distribution in W/nm: chromaticity from the spectrum, flux
    /// Φv = K_m·Σ P(λ)·V(λ)·Δλ.
    /// </summary>
    public static Emitter FromSpectralPower(string name, Spectrum spectralPowerWattsPerNm)
    {
        ArgumentNullException.ThrowIfNull(spectralPowerWattsPerNm);
        return new Emitter(name, Cie1931.ChromaticityFromSpectrum(spectralPowerWattsPerNm),
            (float)Cie1931.PhotopicLuminousFlux(spectralPowerWattsPerNm), spectralPowerWattsPerNm);
    }

    /// <summary>The same emitter with a different full-level flux, lm.</summary>
    public Emitter WithMaxFlux(float maxFluxLm) => new(Name, Chromaticity, maxFluxLm, Spectrum);

    /// <inheritdoc/>
    public override string ToString() => $"{Name} (x={Chromaticity.X:F4}, y={Chromaticity.Y:F4}, {MaxFlux:G4} lm)";

    internal static Vector2 SanitizeChromaticity(Vector2 xy)
    {
        if (!float.IsFinite(xy.X) || !float.IsFinite(xy.Y) || xy.Y <= 0f)
            return ColorSpaces.D65WhiteXy;
        float y = Math.Clamp(xy.Y, 1e-4f, 1f);
        float x = Math.Clamp(xy.X, 0f, 1f - y);
        return new Vector2(x, y);
    }
}
