namespace Candela.Simulation;

/// <summary>
/// Physical constants and standard-atmosphere values shared by the smoke, fire and acoustic models. SI units.
/// </summary>
public static class SimulationConstants
{
    /// <summary>Standard acceleration of gravity, m/s² (CGPM 1901, exact). Gravity points along −Z.</summary>
    public const float Gravity = 9.80665f;

    /// <summary>Default ambient (room) temperature, K (20 °C).</summary>
    public const float AmbientTemperature = 293.15f;

    /// <summary>Density of dry air at 20 °C and 101.325 kPa, kg/m³ (ideal gas, M = 28.965 g/mol).</summary>
    public const float AirDensity = 1.204f;

    /// <summary>Specific heat capacity of air at constant pressure near room temperature, J/(kg·K).</summary>
    public const float AirSpecificHeat = 1005f;

    /// <summary>Lower heating value (net heat of combustion) of propane, J/kg (SFPE Handbook, 46.0–46.4 MJ/kg).</summary>
    public const float PropaneHeatOfCombustion = 46.3e6f;
}
