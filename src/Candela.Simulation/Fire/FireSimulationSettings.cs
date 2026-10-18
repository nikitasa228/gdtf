namespace Candela.Simulation;

/// <summary>
/// Configuration of a <see cref="FireSimulation"/> (SI units). Invalid values are replaced by the defaults when the
/// simulation is created and reported in <see cref="FireSimulation.Issues"/>.
/// </summary>
public sealed record FireSimulationSettings
{
    /// <summary>Fixed internal time step, s (120 Hz resolves the ≈ 0.2 s ignition and flicker well).</summary>
    public float TimeStep { get; init; } = 1f / 120f;

    /// <summary>Maximum fixed sub-steps per <see cref="FireSimulation.Step"/> call; the rest of a long frame is dropped.</summary>
    public int MaxSubStepsPerCall { get; init; } = 16;

    /// <summary>
    /// Time the burning fuel takes from the nozzle to the flame tip, s: the flame grows to full height over this time
    /// after ignition and has burnt out this long after the valve closes.
    /// </summary>
    public float IgnitionTime { get; init; } = 0.2f;

    /// <summary>Time constant of the fuel valve (solenoid), s.</summary>
    public float ValveResponseTime { get; init; } = 0.03f;

    /// <summary>Fraction of the heat release that is radiated, χ_r.</summary>
    public float RadiantFraction { get; init; } = FlameModel.DefaultRadiantFraction;

    /// <summary>Soot radiation temperature of the luminous zone, K (sets colour and luminous efficacy).</summary>
    public float RadiationTemperature { get; init; } = FlameModel.DefaultRadiationTemperature;

    /// <summary>Soot produced per kg of fuel burnt, kg/kg; injected into the smoke simulation at the flame tip.</summary>
    public float SootYield { get; init; } = FlameModel.DefaultSootYield;

    /// <summary>Visible flame column diameter relative to its height (jet flame columns: ≈ 0.15–0.2).</summary>
    public float WidthToHeightRatio { get; init; } = 0.18f;

    /// <summary>Fuel parcels (particles) released per sub-step while the valve is open.</summary>
    public int ParticlesPerSubStep { get; init; } = 8;

    /// <summary>Point-light samples per flame (along its axis).</summary>
    public int LightSamplesPerFlame { get; init; } = 4;

    /// <summary>Preferred cell size of the per-flame temperature volumes, m (enlarged when a flame exceeds the cell budget).</summary>
    public float CellSize { get; init; } = 0.1f;

    /// <summary>Upper limit of cells along any axis of one flame's temperature volume.</summary>
    public int MaxCellsPerAxis { get; init; } = 48;

    /// <summary>Seed of the deterministic turbulence (different seeds give differently flickering but equally valid flames).</summary>
    public uint Seed { get; init; } = 0x5EED_F1A3u;
}
