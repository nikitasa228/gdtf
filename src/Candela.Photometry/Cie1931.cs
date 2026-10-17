using System.Numerics;

namespace Candela.Photometry;

/// <summary>
/// The CIE 1931 2° standard colorimetric observer (CIE 015:2004, ISO/CIE 11664-1): colour-matching functions
/// x̄(λ), ȳ(λ), z̄(λ) tabulated 360–830 nm at 5 nm (official values, see <see cref="CieTables"/>), tristimulus
/// integration and photopic photometry. ȳ(λ) is the photopic luminous efficiency function V(λ).
/// </summary>
public static class Cie1931
{
    /// <summary>First tabulated wavelength, nm.</summary>
    public const float StartNm = CieTables.CmfStartNm;

    /// <summary>Tabulation step, nm.</summary>
    public const float StepNm = CieTables.StepNm;

    /// <summary>Number of tabulated wavelengths (360–830 nm).</summary>
    public const int Count = 95;

    /// <summary>Last tabulated wavelength, nm.</summary>
    public const float EndNm = StartNm + StepNm * (Count - 1);

    /// <summary>
    /// Maximum luminous efficacy for photopic vision K_m = 683.002 lm/W (683 lm/W at 540×10¹² Hz by the SI definition
    /// of the candela, divided by V(555.016 nm)); converts radiometric quantities weighted by ȳ into photometric ones.
    /// </summary>
    public const double Km = 683.002;

    private static readonly Lazy<Spectrum> _xBar = new(() => new Spectrum(StartNm, StepNm, CieTables.XBar));
    private static readonly Lazy<Spectrum> _yBar = new(() => new Spectrum(StartNm, StepNm, CieTables.YBar));
    private static readonly Lazy<Spectrum> _zBar = new(() => new Spectrum(StartNm, StepNm, CieTables.ZBar));

    /// <summary>x̄(λ) at 360, 365, …, 830 nm.</summary>
    public static ReadOnlySpan<float> XBar => CieTables.XBar;

    /// <summary>ȳ(λ) = V(λ) at 360, 365, …, 830 nm.</summary>
    public static ReadOnlySpan<float> YBar => CieTables.YBar;

    /// <summary>z̄(λ) at 360, 365, …, 830 nm.</summary>
    public static ReadOnlySpan<float> ZBar => CieTables.ZBar;

    /// <summary>x̄(λ) as a spectrum (for <see cref="Spectrum.Integrate(Spectrum)"/>).</summary>
    public static Spectrum XBarSpectrum => _xBar.Value;

    /// <summary>ȳ(λ) = V(λ) as a spectrum.</summary>
    public static Spectrum YBarSpectrum => _yBar.Value;

    /// <summary>z̄(λ) as a spectrum.</summary>
    public static Spectrum ZBarSpectrum => _zBar.Value;

    /// <summary>Wavelength of table entry <paramref name="index"/>, nm.</summary>
    public static float WavelengthAt(int index) => StartNm + StepNm * index;

    /// <summary>
    /// (x̄, ȳ, z̄) at <paramref name="wavelengthNm"/>, linearly interpolated between the 5 nm entries; zero outside
    /// 360–830 nm.
    /// </summary>
    public static Vector3 ColorMatchingFunctions(float wavelengthNm)
    {
        float f = (wavelengthNm - StartNm) / StepNm;
        if (!(f >= 0f) || f > Count - 1)
            return Vector3.Zero;
        int i = Math.Min((int)f, Count - 2);
        float t = f - i;
        return Vector3.Lerp(
            new Vector3(CieTables.XBar[i], CieTables.YBar[i], CieTables.ZBar[i]),
            new Vector3(CieTables.XBar[i + 1], CieTables.YBar[i + 1], CieTables.ZBar[i + 1]),
            t);
    }

    /// <summary>
    /// Chromaticity of monochromatic light (a point on the spectral locus), e.g. a laser line. Wavelengths are clamped
    /// to 360–830 nm.
    /// </summary>
    public static Vector2 SpectralLocus(float wavelengthNm)
    {
        float w = float.IsNaN(wavelengthNm) ? 555f : Math.Clamp(wavelengthNm, StartNm, EndNm);
        var cmf = ColorMatchingFunctions(w);
        float sum = cmf.X + cmf.Y + cmf.Z;
        return sum > 0f ? new Vector2(cmf.X / sum, cmf.Y / sum) : ColorSpaces.D65WhiteXy;
    }

    /// <summary>
    /// Tristimulus values X, Y, Z = Σ S(λ)·(x̄, ȳ, z̄)(λ)·Δλ over 360–830 nm, unnormalised (unit: spectrum unit × nm;
    /// for a spectral power in W/nm the result is in W and Y·<see cref="Km"/> is the luminous flux in lm). When the
    /// spectrum is sampled more finely than 5 nm the sum runs on its grid with interpolated colour-matching functions,
    /// otherwise on the 5 nm table grid with the spectrum interpolated (CIE 015:2004 §7.1).
    /// </summary>
    public static Vector3 XyzFromSpectrum(Spectrum spectrum)
    {
        ArgumentNullException.ThrowIfNull(spectrum);
        double x = 0, y = 0, z = 0;
        if (spectrum.StepNm < StepNm)
        {
            float lo = MathF.Max(StartNm, spectrum.StartNm), hi = MathF.Min(EndNm, spectrum.EndNm);
            if (hi < lo)
                return Vector3.Zero;
            int first = Math.Max(0, (int)MathF.Ceiling((lo - spectrum.StartNm) / spectrum.StepNm - 1e-4f));
            int last = Math.Min(spectrum.Count - 1, (int)MathF.Floor((hi - spectrum.StartNm) / spectrum.StepNm + 1e-4f));
            var values = spectrum.Values;
            for (int i = first; i <= last; i++)
            {
                var cmf = ColorMatchingFunctions(spectrum.WavelengthAt(i));
                double s = values[i];
                x += s * cmf.X;
                y += s * cmf.Y;
                z += s * cmf.Z;
            }

            double d = spectrum.StepNm;
            return new Vector3((float)(x * d), (float)(y * d), (float)(z * d));
        }

        for (int i = 0; i < Count; i++)
        {
            double s = spectrum.Sample(WavelengthAt(i));
            x += s * CieTables.XBar[i];
            y += s * CieTables.YBar[i];
            z += s * CieTables.ZBar[i];
        }

        return new Vector3((float)(x * StepNm), (float)(y * StepNm), (float)(z * StepNm));
    }

    /// <summary>CIE 1931 chromaticity (x, y) of a spectrum; D65 white for a spectrum without visible content.</summary>
    public static Vector2 ChromaticityFromSpectrum(Spectrum spectrum)
    {
        var xyz = XyzFromSpectrum(spectrum);
        float sum = xyz.X + xyz.Y + xyz.Z;
        return sum > 0f && float.IsFinite(sum) ? new Vector2(xyz.X / sum, xyz.Y / sum) : ColorSpaces.D65WhiteXy;
    }

    /// <summary>
    /// Photopic luminous flux Φv = K_m·Σ P(λ)·V(λ)·Δλ, lm, of a spectral power distribution given in W/nm.
    /// The same formula turns spectral irradiance (W·m⁻²·nm⁻¹) into illuminance (lx) and spectral radiance into
    /// luminance (cd/m²).
    /// </summary>
    public static double PhotopicLuminousFlux(Spectrum spectralPowerWattsPerNm) =>
        Km * XyzFromSpectrum(spectralPowerWattsPerNm).Y;

    /// <summary>
    /// Luminous efficacy of radiation K = Φv/Φe, lm/W, of a spectral power distribution (any unit): the ratio of
    /// its luminous flux to its total radiant power over the spectrum's own range. 0 for an empty spectrum.
    /// </summary>
    public static double LuminousEfficacyOfRadiation(Spectrum spectralPower)
    {
        ArgumentNullException.ThrowIfNull(spectralPower);
        double radiant = spectralPower.Integrate();
        return radiant > 0 ? PhotopicLuminousFlux(spectralPower) / radiant : 0.0;
    }
}
