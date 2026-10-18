using Candela.Photometry;

namespace Candela.Simulation;

/// <summary>
/// Engineering correlations behind the fire simulation (SI units; heat release rates in watts). They turn a flame
/// machine's nominal flame height into a heat release rate, the heat release rate into radiated power and luminous
/// flux, and give the velocity of the hot gases in the flame. See <c>docs/simulation.md</c> for the derivation and the
/// magnitudes they give for 3–8 m propane flame columns.
/// </summary>
public static class FlameModel
{
    /// <summary>
    /// Fraction of the heat release radiated by a luminous propane flame (SFPE Handbook, Tewarson: 0.25–0.30 for propane
    /// diffusion flames; momentum-driven jet flames sit at the lower end).
    /// </summary>
    public const float DefaultRadiantFraction = 0.25f;

    /// <summary>
    /// Effective radiation temperature of the soot in the luminous zone, K. Hydrocarbon diffusion flames show a colour
    /// temperature of about 1800–2000 K (a candle ≈ 1850 K); with <see cref="DefaultRadiantFraction"/> this value
    /// reproduces a candle's ≈ 1 cd from its ≈ 80 W heat release (see <see cref="LuminousFlux"/>).
    /// </summary>
    public const float DefaultRadiationTemperature = 1800f;

    /// <summary>Soot yield of propane, kg of soot per kg of fuel burnt (SFPE Handbook, Tewarson: 0.024).</summary>
    public const float DefaultSootYield = 0.024f;

    /// <summary>
    /// Mean flame height of a turbulent diffusion flame by Heskestad's correlation, L = 0.235·Q^(2/5) − 1.02·D (Q in
    /// kW, L and D in m), m; 0 for no fire. Valid for buoyancy- to moderately momentum-dominated fires, which is how
    /// stage flame projectors are usually characterised.
    /// </summary>
    /// <param name="heatReleaseRate">Heat release rate Q, W.</param>
    /// <param name="sourceDiameter">Diameter of the fuel source (nozzle), m.</param>
    public static float FlameHeight(float heatReleaseRate, float sourceDiameter)
    {
        if (!(heatReleaseRate > 0f) || !float.IsFinite(heatReleaseRate))
            return 0f;
        float d = float.IsFinite(sourceDiameter) ? MathF.Max(sourceDiameter, 0f) : 0f;
        return MathF.Max(0f, 0.235f * MathF.Pow(heatReleaseRate * 1e-3f, 0.4f) - 1.02f * d);
    }

    /// <summary>
    /// Heat release rate that gives a mean flame height <paramref name="flameHeight"/> (Heskestad's correlation solved
    /// for Q): Q = ((L + 1.02·D)/0.235)^(5/2) kW, returned in W. About 0.6 MW for 3 m, 2.1 MW for 5 m and 6.9 MW for 8 m.
    /// </summary>
    /// <param name="flameHeight">Mean flame height L, m.</param>
    /// <param name="sourceDiameter">Diameter of the fuel source (nozzle), m.</param>
    public static float HeatReleaseRateForHeight(float flameHeight, float sourceDiameter)
    {
        if (!(flameHeight > 0f) || !float.IsFinite(flameHeight))
            return 0f;
        float d = float.IsFinite(sourceDiameter) ? MathF.Max(sourceDiameter, 0f) : 0f;
        return MathF.Pow((flameHeight + 1.02f * d) / 0.235f, 2.5f) * 1e3f;
    }

    /// <summary>Fuel mass burnt per second for a heat release rate, kg/s (propane, lower heating value 46.3 MJ/kg).</summary>
    public static float FuelMassRate(float heatReleaseRate) =>
        float.IsFinite(heatReleaseRate) && heatReleaseRate > 0f ? heatReleaseRate / SimulationConstants.PropaneHeatOfCombustion : 0f;

    /// <summary>
    /// Luminous flux of a flame, lm: Φ = χ_r·Q·K(T_r) — the radiated power χ_r·Q times the luminous efficacy K of a
    /// grey body at the soot radiation temperature T_r (equal to the black-body efficacy, a constant emissivity does not
    /// change it). 0.62 lm/W at 1800 K, so a flame turns about 0.15 lm of every watt of heat into light.
    /// </summary>
    /// <param name="heatReleaseRate">Heat release rate Q, W.</param>
    /// <param name="radiantFraction">Radiated fraction χ_r of the heat release.</param>
    /// <param name="radiationTemperature">Soot radiation temperature T_r, K.</param>
    public static float LuminousFlux(float heatReleaseRate, float radiantFraction = DefaultRadiantFraction,
        float radiationTemperature = DefaultRadiationTemperature)
    {
        if (!(heatReleaseRate > 0f) || !float.IsFinite(heatReleaseRate) || !(radiantFraction > 0f))
            return 0f;
        return heatReleaseRate * MathF.Min(radiantFraction, 1f) * BlackbodyTable.Efficacy(radiationTemperature);
    }

    /// <summary>
    /// Average luminous intensity of a flame seen from all directions, cd: I = Φ/(4π). Seen from the side, a tall
    /// column is about 25 % brighter than this average (a Lambertian cylinder: I⊥ = Φ/π²); seen end-on, much dimmer.
    /// </summary>
    public static float MeanIntensity(float luminousFlux) => float.IsFinite(luminousFlux) && luminousFlux > 0f
        ? luminousFlux / (4f * MathF.PI)
        : 0f;

    /// <summary>
    /// Mean centre-line gas velocity at the flame tip, m/s: McCaffrey's intermittent-region value u = 1.93·Q_c^(1/5)
    /// (Q_c, the convective heat release, in kW) — about 8 m/s for a 2 MW flame.
    /// </summary>
    /// <param name="convectiveHeatRelease">Convective part (1 − χ_r)·Q of the heat release, W.</param>
    public static float FlameTipVelocity(float convectiveHeatRelease) =>
        float.IsFinite(convectiveHeatRelease) && convectiveHeatRelease > 0f
            ? 1.93f * MathF.Pow(convectiveHeatRelease * 1e-3f, 0.2f)
            : 0f;

    /// <summary>
    /// Pulsation (puffing) frequency of a buoyant flame of diameter D, Hz: f ≈ 1.5/√D (Cetegen &amp; Ahmed 1993) —
    /// about 1.5 Hz for a 1 m wide flame column.
    /// </summary>
    public static float PulsationFrequency(float flameDiameter) =>
        float.IsFinite(flameDiameter) && flameDiameter > 1e-3f ? 1.5f / MathF.Sqrt(flameDiameter) : 0f;
}

/// <summary>
/// Black-body quantities on a fine temperature table for per-particle use: luminous efficacy (lm/W), luminance
/// relative to <see cref="FlameModel.DefaultRadiationTemperature"/> and CIE 1931 chromaticity, all from
/// <see cref="BlackbodyRadiator"/> (Planck's law integrated against the CIE observer). 500–3000 K in 5 K steps, linear
/// interpolation, built once (≈ 1 ms).
/// </summary>
internal static class BlackbodyTable
{
    public const float MinKelvin = 500f;
    public const float MaxKelvin = 3000f;
    private const float Step = 5f;
    private const int Count = (int)((MaxKelvin - MinKelvin) / Step) + 1;

    private static readonly Lazy<Table> Data = new(Build, LazyThreadSafetyMode.ExecutionAndPublication);

    private sealed class Table
    {
        public readonly float[] Efficacy = new float[Count];
        public readonly float[] Luminance = new float[Count];
        public readonly float[] X = new float[Count];
        public readonly float[] Y = new float[Count];
    }

    private static Table Build()
    {
        var t = new Table();
        for (int n = 0; n < Count; n++)
        {
            float kelvin = MinKelvin + n * Step;
            t.Efficacy[n] = (float)BlackbodyRadiator.LuminousEfficacy(kelvin);
            t.Luminance[n] = (float)BlackbodyRadiator.Luminance(kelvin);
            var xy = BlackbodyRadiator.Chromaticity(kelvin);
            t.X[n] = xy.X;
            t.Y[n] = xy.Y;
        }

        return t;
    }

    /// <summary>Luminous efficacy of black-body radiation, lm/W (0 below <see cref="MinKelvin"/>).</summary>
    public static float Efficacy(float kelvin) => Lookup(Data.Value.Efficacy, kelvin);

    /// <summary>Black-body luminance, cd/m² (0 below <see cref="MinKelvin"/>).</summary>
    public static float Luminance(float kelvin) => Lookup(Data.Value.Luminance, kelvin);

    /// <summary>CIE 1931 chromaticity of a black body (clamped to the table range).</summary>
    public static (float X, float Y) Chromaticity(float kelvin)
    {
        var t = Data.Value;
        float k = float.IsFinite(kelvin) ? Math.Clamp(kelvin, MinKelvin, MaxKelvin) : MinKelvin;
        float f = (k - MinKelvin) / Step;
        int i = Math.Min((int)f, Count - 2);
        float w = f - i;
        return (t.X[i] + (t.X[i + 1] - t.X[i]) * w, t.Y[i] + (t.Y[i + 1] - t.Y[i]) * w);
    }

    private static float Lookup(float[] table, float kelvin)
    {
        if (!(kelvin > MinKelvin))
            return 0f;
        float f = (MathF.Min(kelvin, MaxKelvin) - MinKelvin) / Step;
        int i = Math.Min((int)f, Count - 2);
        float w = f - i;
        return table[i] + (table[i + 1] - table[i]) * w;
    }
}
