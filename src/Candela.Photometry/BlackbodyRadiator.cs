using System.Numerics;

namespace Candela.Photometry;

/// <summary>
/// Physics of an ideal black-body (Planckian) radiator — used for incandescent sources, fire and flames (soot
/// glows close to a black body) and as the reference for colour temperature. Planck's law, the Stefan–Boltzmann law,
/// photometric output and colour. Temperatures are in kelvin; inputs are clamped to
/// [<see cref="MinKelvin"/>, <see cref="MaxKelvin"/>] and NaN is treated as 6500 K, so a bad DMX or file value
/// never produces NaN.
/// </summary>
public static class BlackbodyRadiator
{
    /// <summary>Lowest supported temperature, K (below this visible emission is utterly negligible).</summary>
    public const float MinKelvin = 300f;

    /// <summary>Highest supported temperature, K.</summary>
    public const float MaxKelvin = 1_000_000f;

    /// <summary>
    /// Spectral radiance by Planck's law, L_λ = c₁L·λ⁻⁵ / (e^(c₂/λT) − 1), in W·sr⁻¹·m⁻² per metre of wavelength
    /// (multiply by 10⁻⁹ for "per nm").
    /// </summary>
    /// <param name="wavelengthMetres">Wavelength, m.</param>
    /// <param name="kelvin">Temperature, K.</param>
    public static double SpectralRadiance(double wavelengthMetres, double kelvin)
    {
        if (!(wavelengthMetres > 0))
            return 0.0;
        double t = ClampKelvin(kelvin);
        double x = PhysicalConstants.SecondRadiationConstant / (wavelengthMetres * t);
        if (x > 700.0)
            return 0.0;
        double l5 = wavelengthMetres * wavelengthMetres;
        l5 = l5 * l5 * wavelengthMetres;
        return PhysicalConstants.FirstRadiationConstantRadiance / (l5 * double.ExpM1(x));
    }

    /// <summary>Spectral radiant exitance M_λ = π·L_λ of a black-body surface, W·m⁻²·nm⁻¹.</summary>
    /// <param name="wavelengthNm">Wavelength, nm.</param>
    /// <param name="kelvin">Temperature, K.</param>
    public static double SpectralExitancePerNm(double wavelengthNm, double kelvin) =>
        Math.PI * SpectralRadiance(wavelengthNm * 1e-9, kelvin) * 1e-9;

    /// <summary>Total radiant exitance by the Stefan–Boltzmann law, M = σ·T⁴, W/m².</summary>
    public static double RadiantExitance(double kelvin)
    {
        double t = ClampKelvin(kelvin);
        double t2 = t * t;
        return PhysicalConstants.StefanBoltzmannConstant * t2 * t2;
    }

    /// <summary>Luminous exitance M_v = K_m·Σ M_λ·V(λ)·Δλ of a black-body surface, lm/m².</summary>
    public static double LuminousExitance(double kelvin)
    {
        double t = ClampKelvin(kelvin);
        double sum = 0;
        for (int i = 0; i < Cie1931.Count; i++)
            sum += SpectralExitancePerNm(Cie1931.WavelengthAt(i), t) * CieTables.YBar[i];
        return Cie1931.Km * sum * Cie1931.StepNm;
    }

    /// <summary>Luminance of a black-body surface, L_v = M_v/π, cd/m² (a black body is a Lambertian emitter).</summary>
    public static double Luminance(double kelvin) => LuminousExitance(kelvin) / Math.PI;

    /// <summary>
    /// Luminous efficacy of black-body radiation, lm per watt of total radiated power: K = M_v / (σT⁴). About
    /// 16 lm/W at 2856 K, 55 lm/W at 4000 K, peaking near 95 lm/W around 6600 K; below 0.1 lm/W for a 1500 K flame.
    /// </summary>
    public static double LuminousEfficacy(double kelvin) => LuminousExitance(kelvin) / RadiantExitance(kelvin);

    /// <summary>
    /// Wavelength of peak spectral radiance by Wien's displacement law, λ_max = b/T, nm.
    /// </summary>
    public static double PeakWavelengthNm(double kelvin) => PhysicalConstants.WienDisplacementConstant / ClampKelvin(kelvin) * 1e9;

    /// <summary>
    /// CIE 1931 chromaticity of a black body at any temperature in the supported range, by direct integration of
    /// Planck's law against the colour-matching functions (not limited to the 1000–25000 K CCT table, so fire at
    /// 1200 K works).
    /// </summary>
    public static Vector2 Chromaticity(float kelvin)
    {
        PlanckianLocus.ComputeXyz(ClampKelvin(kelvin), out double x, out double y, out double z);
        double sum = x + y + z;
        return sum > 0 ? new Vector2((float)(x / sum), (float)(y / sum)) : Cie1931.SpectralLocus(Cie1931.EndNm);
    }

    /// <summary>Light colour of a black body (fire, incandescent filament) at <paramref name="kelvin"/>.</summary>
    public static LightColor Color(float kelvin) => LightColor.FromXy(Chromaticity(kelvin));

    internal static double ClampKelvin(double kelvin) => double.IsNaN(kelvin) ? 6500.0 : Math.Clamp(kelvin, MinKelvin, MaxKelvin);
}
