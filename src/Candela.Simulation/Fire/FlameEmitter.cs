using System.Numerics;

namespace Candela.Simulation;

/// <summary>
/// One flame device (a GENERATOR FIRE fixture such as a stage flame projector) for a single <see cref="FireSimulation"/>
/// step, built by the engine from the fixture's position, aim and DMX-evaluated flame output. The simulation keeps
/// each device's state (valve, burn timer, burning fuel) between steps under its <see cref="Id"/>.
/// </summary>
public readonly record struct FlameEmitter
{
    /// <summary>Default nominal flame height at full output, m (typical stage flame projectors: 3–8 m).</summary>
    public const float DefaultNominalHeight = 5f;

    /// <summary>Default nozzle diameter, m.</summary>
    public const float DefaultNozzleDiameter = 0.05f;

    /// <summary>Identity of the device (the fixture entity's id); state is tracked per id.</summary>
    public Guid Id { get; init; }

    /// <summary>Nozzle exit position, m.</summary>
    public Vector3 Position { get; init; }

    /// <summary>Direction the nozzle fires (normalised internally; zero or invalid = straight up, +Z).</summary>
    public Vector3 Direction { get; init; }

    /// <summary>
    /// Flame output 0..1 from DMX: the fuel valve opening. Fuel flow and heat release scale with it, the flame height
    /// with its 2/5 power (Heskestad). 0 closes the valve.
    /// </summary>
    public float Output { get; init; }

    /// <summary>Mean flame height at full output, m (the device's specified flame height).</summary>
    public float NominalHeight { get; init; }

    /// <summary>
    /// Burn time (shot length / safety time-out), s: after burning continuously for this long the valve closes until
    /// the output is brought back to 0 and raised again. 0 or ∞ = burns as long as the output is above 0.
    /// </summary>
    public float BurnTime { get; init; }

    /// <summary>Nozzle diameter, m (enters Heskestad's correlation as the source diameter).</summary>
    public float NozzleDiameter { get; init; }

    /// <summary>
    /// Heat release rate at full output, W, when the manufacturer specifies it; 0 (the default) derives it from
    /// <see cref="NominalHeight"/> with <see cref="FlameModel.HeatReleaseRateForHeight"/>.
    /// </summary>
    public float HeatReleaseRate { get; init; }

    /// <summary>A flame device with the documented defaults for nozzle and heat release.</summary>
    /// <param name="id">Device identity.</param>
    /// <param name="position">Nozzle position, m.</param>
    /// <param name="direction">Firing direction.</param>
    /// <param name="output">Flame output 0..1.</param>
    /// <param name="nominalHeight">Flame height at full output, m.</param>
    /// <param name="burnTime">Burn time, s (0 = unlimited).</param>
    public static FlameEmitter Create(Guid id, Vector3 position, Vector3 direction, float output,
        float nominalHeight = DefaultNominalHeight, float burnTime = 0f) => new()
        {
            Id = id,
            Position = position,
            Direction = direction,
            Output = output,
            NominalHeight = nominalHeight,
            BurnTime = burnTime,
            NozzleDiameter = DefaultNozzleDiameter,
        };
}
