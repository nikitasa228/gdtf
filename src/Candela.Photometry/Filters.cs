using System.Numerics;
using Candela.Core.SceneGraph.Components;

namespace Candela.Photometry;

/// <summary>Result of filtering a light.</summary>
/// <param name="Color">Colour of the transmitted light.</param>
/// <param name="LuminousTransmission">Transmitted luminous flux ÷ incident luminous flux, 0..1.</param>
public readonly record struct FilterResult(LightColor Color, float LuminousTransmission);

/// <summary>
/// Subtractive colour: spectral filters (gels, dichroics), the CMY flag model of moving-light colour mixing,
/// colour-temperature conversion (CTO/CTB) filters, and colour-wheel slots known only by the CIE xyY of the
/// transmitted light.
/// </summary>
/// <remarks>
/// <para><b>CMY model.</b> Each flag is a dichroic filter with a logistic absorption band (10–90 % edge width
/// ≈ 4.4·<see cref="EdgeWidthNm"/> ≈ 13 nm, peak absorption <see cref="PeakAbsorption"/>): cyan absorbs above
/// <see cref="CyanEdgeNm"/> (red), magenta absorbs between <see cref="MagentaLowEdgeNm"/> and
/// <see cref="MagentaHighEdgeNm"/> (green), yellow absorbs below <see cref="YellowEdgeNm"/> (blue). The bands overlap by
/// 10 nm so that full C+M+Y is practically black (≈ 1 % luminous transmission), as on real CMY engines. A level
/// 0..1 scales the absorption depth: T(λ) = 1 − level·a_peak·band(λ), which also models a flag that covers part of
/// the beam. Flags in series multiply.</para>
/// <para><b>Conversion filters.</b> An ideal CTO/CTB filter that turns a Planckian source of T₁ into one of T₂ has
/// transmission B(λ, T₂)/B(λ, T₁), scaled to a peak of 1 over 380–780 nm (a filter cannot amplify). Applied to other
/// white sources it shifts them by the same mired value.</para>
/// <para><b>xyY filters.</b> GDTF wheel slots and gels often give only the chromaticity of the transmitted light under
/// a D65 source and the luminous transmission Y (%). Without a spectrum the filter is applied per channel in linear
/// Rec.2020 (a von Kries-like product), which reproduces the slot colour exactly for a D65 source and approximates it
/// for other sources.</para>
/// </remarks>
public static class Filters
{
    /// <summary>Half-absorption wavelength of the cyan flag (absorbs longer wavelengths), nm.</summary>
    public const float CyanEdgeNm = 580f;

    /// <summary>Short-wavelength half-absorption edge of the magenta flag's band, nm.</summary>
    public const float MagentaLowEdgeNm = 490f;

    /// <summary>Long-wavelength half-absorption edge of the magenta flag's band, nm.</summary>
    public const float MagentaHighEdgeNm = 590f;

    /// <summary>Half-absorption wavelength of the yellow flag (absorbs shorter wavelengths), nm.</summary>
    public const float YellowEdgeNm = 500f;

    /// <summary>Logistic edge scale of the dichroic bands, nm.</summary>
    public const float EdgeWidthNm = 3f;

    /// <summary>Absorption inside a band at level 1 (0.5 % residual transmission, typical for dichroics).</summary>
    public const float PeakAbsorption = 0.995f;

    /// <summary>Absorption band shape of the cyan flag at <paramref name="wavelengthNm"/> (0..1, before level and depth).</summary>
    public static float CyanBand(float wavelengthNm) => Logistic((wavelengthNm - CyanEdgeNm) / EdgeWidthNm);

    /// <summary>Absorption band shape of the magenta flag at <paramref name="wavelengthNm"/> (0..1).</summary>
    public static float MagentaBand(float wavelengthNm) =>
        Logistic((wavelengthNm - MagentaLowEdgeNm) / EdgeWidthNm) * Logistic((MagentaHighEdgeNm - wavelengthNm) / EdgeWidthNm);

    /// <summary>Absorption band shape of the yellow flag at <paramref name="wavelengthNm"/> (0..1).</summary>
    public static float YellowBand(float wavelengthNm) => Logistic((YellowEdgeNm - wavelengthNm) / EdgeWidthNm);

    /// <summary>Combined transmission of the three CMY flags at <paramref name="wavelengthNm"/> for levels 0..1.</summary>
    public static float CmyTransmission(float wavelengthNm, float cyan, float magenta, float yellow) =>
        (1f - Level(cyan) * PeakAbsorption * CyanBand(wavelengthNm)) *
        (1f - Level(magenta) * PeakAbsorption * MagentaBand(wavelengthNm)) *
        (1f - Level(yellow) * PeakAbsorption * YellowBand(wavelengthNm));

    /// <summary>Transmission spectrum of the cyan flag at <paramref name="level"/> (360–830 nm, 5 nm).</summary>
    public static Spectrum CyanTransmission(float level) => CmyTransmission(level, 0f, 0f);

    /// <summary>Transmission spectrum of the magenta flag at <paramref name="level"/> (360–830 nm, 5 nm).</summary>
    public static Spectrum MagentaTransmission(float level) => CmyTransmission(0f, level, 0f);

    /// <summary>Transmission spectrum of the yellow flag at <paramref name="level"/> (360–830 nm, 5 nm).</summary>
    public static Spectrum YellowTransmission(float level) => CmyTransmission(0f, 0f, level);

    /// <summary>Combined transmission spectrum of the three CMY flags (360–830 nm, 5 nm).</summary>
    public static Spectrum CmyTransmission(float cyan, float magenta, float yellow) =>
        Spectrum.FromFunction(w => CmyTransmission(w, cyan, magenta, yellow));

    /// <summary>
    /// Filters <paramref name="source"/> by <paramref name="transmission"/> (both spectra; the transmission is extended
    /// with its edge values outside its range) and returns the transmitted colour and luminous transmission.
    /// </summary>
    public static FilterResult Apply(Spectrum source, Spectrum transmission)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(transmission);
        var incident = Cie1931.XyzFromSpectrum(source);
        if (!(incident.Y > 0f))
            return new FilterResult(LightColor.White, 0f);
        var transmitted = Cie1931.XyzFromSpectrum(source.Multiply(transmission));
        float factor = Math.Clamp(transmitted.Y / incident.Y, 0f, 1f);
        return new FilterResult(factor > 0f ? LightColor.FromXyz(transmitted) : LightColor.FromXyz(incident), factor);
    }

    /// <summary>CMY flags in front of <paramref name="source"/>; see <see cref="SubtractiveFilterModel"/> for per-frame use.</summary>
    public static FilterResult ApplyCmy(Spectrum source, float cyan, float magenta, float yellow) =>
        new SubtractiveFilterModel(source).Evaluate(cyan, magenta, yellow);

    /// <summary>
    /// Transmission spectrum of an ideal colour-temperature conversion filter from <paramref name="sourceKelvin"/> to
    /// <paramref name="targetKelvin"/> (CTO when the target is lower, CTB when higher), peak 1 over 380–780 nm, tabulated
    /// 360–830 nm at 5 nm. Temperatures are clamped to 1000–25000 K.
    /// </summary>
    public static Spectrum ConversionFilterTransmission(float sourceKelvin, float targetKelvin)
    {
        double ts = Cct.ClampKelvin(sourceKelvin), tt = Cct.ClampKelvin(targetKelvin);
        double norm = ConversionNormalization(ts, tt);
        return Spectrum.FromFunction(w => (float)Math.Min(1.0, PlanckRatio(w, ts, tt) / norm));
    }

    /// <summary>
    /// Conversion filter from <paramref name="sourceKelvin"/> to <paramref name="targetKelvin"/> applied to the spectrum
    /// <paramref name="source"/> (whose own CCT should be about <paramref name="sourceKelvin"/>).
    /// </summary>
    public static FilterResult ApplyConversion(Spectrum source, float sourceKelvin, float targetKelvin) =>
        Apply(source, ConversionFilterTransmission(sourceKelvin, targetKelvin));

    /// <summary>
    /// Conversion filter applied to a Planckian source: the result is exactly on the locus at the target temperature,
    /// with the filter's luminous transmission. Allocation-free.
    /// </summary>
    public static FilterResult ConvertBlackbody(float sourceKelvin, float targetKelvin)
    {
        double ts = Cct.ClampKelvin(sourceKelvin), tt = Cct.ClampKelvin(targetKelvin);
        double norm = ConversionNormalization(ts, tt);
        double incident = 0, transmitted = 0;
        for (int i = 0; i < Cie1931.Count; i++)
        {
            double w = Cie1931.WavelengthAt(i);
            double source = BlackbodyRadiator.SpectralRadiance(w * 1e-9, ts) * CieTables.YBar[i];
            incident += source;
            transmitted += source * Math.Min(1.0, PlanckRatio(w, ts, tt) / norm);
        }

        float factor = incident > 0 ? (float)Math.Clamp(transmitted / incident, 0.0, 1.0) : 0f;
        return new FilterResult(LightColor.FromCct((float)tt), factor);
    }

    /// <summary>
    /// A filter known only by the CIE 1931 chromaticity <paramref name="filterXy"/> of the light it transmits under D65
    /// and its luminous transmission, applied to a light of colour <paramref name="source"/>: per-channel product in
    /// linear Rec.2020 (exact for a D65 source).
    /// </summary>
    /// <param name="source">Colour of the incident light.</param>
    /// <param name="filterXy">CIE 1931 (x, y) of the filter under D65.</param>
    /// <param name="luminousTransmission">Luminous transmission of the filter under D65, 0..1 (NaN → 1).</param>
    public static FilterResult ApplyChromaticityFilter(LightColor source, Vector2 filterXy, float luminousTransmission)
    {
        float tau = float.IsNaN(luminousTransmission) ? 1f : Math.Clamp(luminousTransmission, 0f, 1f);
        var filterColor = LightColor.FromXy(filterXy);
        var transmission = PhysicalChannelTransmission(filterColor, tau);
        var filtered = source.Rgb * transmission;
        float y = ColorSpaces.LuminanceRec2020(filtered);
        return y > 0f ? new FilterResult(LightColor.FromRec2020(filtered), Math.Min(y, 1f)) : new FilterResult(filterColor, 0f);
    }

    /// <summary>
    /// Per-channel (linear Rec.2020) transmission of a filter of colour <paramref name="filterColor"/> and luminous
    /// transmission <paramref name="luminousTransmission"/> under D65: <c>rgb · τ</c>. A channel transmission above 1 is
    /// physically impossible — it happens when a file states an unrealistically high Y for a saturated colour (e.g.
    /// 100 % for a deep blue) — so the whole vector is then scaled down until its largest channel is 1. The chromaticity
    /// is kept and the luminous transmission becomes the largest one an RGB-separable filter of that colour can have.
    /// </summary>
    public static Vector3 PhysicalChannelTransmission(LightColor filterColor, float luminousTransmission)
    {
        float tau = float.IsNaN(luminousTransmission) ? 1f : Math.Clamp(luminousTransmission, 0f, 1f);
        var t = filterColor.Rgb * tau;
        float max = MathF.Max(t.X, MathF.Max(t.Y, t.Z));
        return max > 1f ? t / max : t;
    }

    /// <summary>
    /// A GDTF colour-wheel slot (or gel) given as CIE xyY with Y = luminous transmission in percent, in front of a
    /// D65 white source. Non-finite Y counts as 100 %, values outside 0–100 % are clamped.
    /// </summary>
    public static FilterResult FromWheelSlot(float x, float y, float transmissionPercent) =>
        ApplyChromaticityFilter(LightColor.White, new Vector2(x, y), float.IsNaN(transmissionPercent) ? 1f : transmissionPercent / 100f);

    /// <summary>A GDTF/MVR CIE colour (x, y, Y %) as a wheel slot or gel; see <see cref="FromWheelSlot(float, float, float)"/>.</summary>
    public static FilterResult FromWheelSlot(CieColor color) => FromWheelSlot(color.X, color.Y, color.BigY);

    internal static float Level(float level) => float.IsNaN(level) ? 0f : Math.Clamp(level, 0f, 1f);

    internal static float Logistic(float t) => 1f / (1f + MathF.Exp(-t));

    /// <summary>B(λ, T₂)/B(λ, T₁) — the λ⁻⁵ factors cancel.</summary>
    internal static double PlanckRatio(double wavelengthNm, double sourceKelvin, double targetKelvin)
    {
        double c = PhysicalConstants.SecondRadiationConstant / (wavelengthNm * 1e-9);
        double xs = c / sourceKelvin, xt = c / targetKelvin;
        if (xt > 700.0)
            return 0.0;
        return double.ExpM1(Math.Min(xs, 700.0)) / double.ExpM1(xt);
    }

    /// <summary>Peak of the Planck ratio over 380–780 nm (the ratio is monotonic in λ, so the peak is at an end).</summary>
    internal static double ConversionNormalization(double sourceKelvin, double targetKelvin)
    {
        double norm = Math.Max(PlanckRatio(380.0, sourceKelvin, targetKelvin), PlanckRatio(780.0, sourceKelvin, targetKelvin));
        return norm > 0 && double.IsFinite(norm) ? norm : 1.0;
    }
}
