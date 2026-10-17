namespace Candela.Photometry;

/// <summary>
/// Physical constants used by the radiometric and photometric code, with the exact SI 2019 values (CODATA 2018).
/// All values are in SI base units.
/// </summary>
public static class PhysicalConstants
{
    /// <summary>Planck constant h, J·s (exact).</summary>
    public const double PlanckConstant = 6.62607015e-34;

    /// <summary>Speed of light in vacuum c, m/s (exact).</summary>
    public const double SpeedOfLight = 299_792_458.0;

    /// <summary>Boltzmann constant k, J/K (exact).</summary>
    public const double BoltzmannConstant = 1.380649e-23;

    /// <summary>
    /// Second radiation constant c₂ = h·c/k = 1.438776877…×10⁻² m·K. CIE 015:2004 recommends 1.4388×10⁻² m·K for the
    /// Planckian locus; the difference (0.002 %) is far below anything visible.
    /// </summary>
    public const double SecondRadiationConstant = PlanckConstant * SpeedOfLight / BoltzmannConstant;

    /// <summary>First radiation constant for spectral radiance c₁L = 2·h·c², W·m²·sr⁻¹.</summary>
    public const double FirstRadiationConstantRadiance = 2.0 * PlanckConstant * SpeedOfLight * SpeedOfLight;

    /// <summary>First radiation constant for spectral exitance c₁ = 2·π·h·c², W·m².</summary>
    public const double FirstRadiationConstant = System.Math.PI * FirstRadiationConstantRadiance;

    /// <summary>
    /// Stefan–Boltzmann constant σ = 2π⁵k⁴/(15h³c²), W·m⁻²·K⁻⁴ — exact in the SI 2019, quoted here to the ten
    /// significant digits of CODATA 2018.
    /// </summary>
    public const double StefanBoltzmannConstant = 5.670374419e-8;

    /// <summary>Wien displacement constant b for the wavelength of peak spectral radiance, m·K.</summary>
    public const double WienDisplacementConstant = 2.897771955e-3;
}
