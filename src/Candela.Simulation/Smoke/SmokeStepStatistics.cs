namespace Candela.Simulation;

/// <summary>
/// Diagnostics of the last <see cref="SmokeSimulation.Step"/> call (or single <see cref="SmokeSimulation.StepOnce"/>).
/// Pressure figures are those of the last fixed sub-step.
/// </summary>
/// <param name="SubSteps">Fixed sub-steps run.</param>
/// <param name="PressureIterations">Conjugate-gradient iterations of the last pressure solve.</param>
/// <param name="MaxDivergenceBefore">Largest |∇·u| of a fluid cell before the last projection, 1/s.</param>
/// <param name="MaxDivergenceAfter">Largest |∇·u| of a fluid cell after the last projection, 1/s.</param>
/// <param name="IgnoredEmitters">Emitters skipped in the last sub-step (non-finite position, outside the box, inside a solid).</param>
/// <param name="DroppedTime">Simulation time discarded because the sub-step cap was reached, s.</param>
/// <param name="MassCorrection">
/// Relative correction δ the conservation fixer applied to the smoke in the last sub-step (0 when
/// <see cref="SmokeSimulationSettings.ConserveMassAndHeat"/> is off): a measure of the advection's conservation error.
/// </param>
/// <param name="HeatCorrection">The same for the heat (temperature excess).</param>
public readonly record struct SmokeStepStatistics(
    int SubSteps,
    int PressureIterations,
    float MaxDivergenceBefore,
    float MaxDivergenceAfter,
    int IgnoredEmitters,
    double DroppedTime,
    float MassCorrection,
    float HeatCorrection);

/// <summary>Wall-clock milliseconds spent in each stage of the last fixed sub-step.</summary>
/// <param name="Sources">Emitter injection.</param>
/// <param name="Forces">Buoyancy and fans.</param>
/// <param name="VelocityAdvection">Velocity self-advection and viscosity.</param>
/// <param name="Projection">Pressure solve and velocity correction.</param>
/// <param name="ScalarAdvection">Density/temperature advection, diffusion and dissipation.</param>
public readonly record struct SmokeStageTimings(float Sources, float Forces, float VelocityAdvection, float Projection,
    float ScalarAdvection)
{
    /// <summary>Sum of all stages, ms.</summary>
    public float Total => Sources + Forces + VelocityAdvection + Projection + ScalarAdvection;
}
