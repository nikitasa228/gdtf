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
public readonly record struct SmokeStepStatistics(
    int SubSteps,
    int PressureIterations,
    float MaxDivergenceBefore,
    float MaxDivergenceAfter,
    int IgnoredEmitters,
    double DroppedTime);
