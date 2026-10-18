namespace Candela.Simulation;

/// <summary>
/// A complete copy of a <see cref="FireSimulation"/>'s state — every device's valve, burn timer and burning fuel
/// parcels, plus time — for scrubbing a timecode show (see <see cref="FireSimulation.RestoreSnapshot"/>).
/// </summary>
public sealed class FireSimulationSnapshot
{
    internal FireSimulationSnapshot(IReadOnlyList<FireSimulation.FlameState> states, double time, double accumulator, long stepCount,
        FireSimulationSettings settings)
    {
        States = states;
        Time = time;
        Accumulator = accumulator;
        StepCount = stepCount;
        Settings = settings;
    }

    /// <summary>Simulated time at capture, s.</summary>
    public double Time { get; }

    /// <summary>Fixed sub-steps run before capture.</summary>
    public long StepCount { get; }

    /// <summary>Number of devices with state (burning, burning out or waiting for re-arming).</summary>
    public int FlameCount => States.Count;

    internal IReadOnlyList<FireSimulation.FlameState> States { get; }

    internal double Accumulator { get; }

    internal FireSimulationSettings Settings { get; }
}
