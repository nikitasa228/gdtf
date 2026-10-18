using System.Numerics;
using Candela.Core.Math;
using Candela.Core.SceneGraph.Components;

namespace Candela.Simulation;

/// <summary>Scheme used to advect smoke density and temperature.</summary>
public enum ScalarAdvectionScheme
{
    /// <summary>First-order semi-Lagrangian (Stam 1999): unconditionally stable, noticeably diffusive.</summary>
    SemiLagrangian,

    /// <summary>
    /// Semi-Lagrangian MacCormack (Selle et al. 2008) with the min/max limiter: second-order, keeps plumes sharp,
    /// falls back to semi-Lagrangian wherever the correction would create a new extremum, so it stays stable.
    /// </summary>
    MacCormack,
}

/// <summary>Preconditioner of the pressure conjugate-gradient solve.</summary>
public enum PressurePreconditioner
{
    /// <summary>Geometric multigrid V-cycle (MGPCG): about 5–10 iterations whatever the grid size.</summary>
    Multigrid,

    /// <summary>Diagonal (Jacobi): the simplest, iterations grow with the grid size (≈ 40 at 64³ with a warm start).</summary>
    Jacobi,
}

/// <summary>
/// Configuration of a <see cref="SmokeSimulation"/>. All values are SI (m, s, K, kg). Invalid values (NaN, negative
/// sizes, …) are replaced by the documented defaults when the simulation is created and reported as issues, so a
/// broken project file can never stop the simulation from starting.
/// </summary>
public sealed record SmokeSimulationSettings
{
    /// <summary>World-space box to simulate, m. The grid covers it with cubic cells, rounded up to whole cells.</summary>
    public Aabb Bounds { get; init; } = new(new Vector3(-10f, -10f, 0f), new Vector3(10f, 10f, 10f));

    /// <summary>Requested cell edge length, m. Increased automatically when the grid would exceed the cell budget.</summary>
    public float CellSize { get; init; } = 0.25f;

    /// <summary>Upper limit of cells along any axis (budget).</summary>
    public int MaxCellsPerAxis { get; init; } = 128;

    /// <summary>Upper limit of the total cell count (budget; 1 M cells ≈ 70 MB of solver state).</summary>
    public int MaxCellCount { get; init; } = 128 * 128 * 64;

    /// <summary>Fixed internal time step, s. <see cref="SmokeSimulation.Step"/> runs as many of these as fit the frame time.</summary>
    public float TimeStep { get; init; } = 1f / 30f;

    /// <summary>
    /// Maximum fixed sub-steps one <see cref="SmokeSimulation.Step"/> call may run. Time beyond that is dropped (and counted
    /// in <see cref="SmokeStepStatistics.DroppedTime"/>) so a slow frame cannot snowball into ever slower frames.
    /// </summary>
    public int MaxSubStepsPerCall { get; init; } = 4;

    /// <summary>Faces of the box through which air and smoke can leave or enter; the rest are free-slip walls.</summary>
    public DomainFaces OpenFaces { get; init; } = DomainFaces.AllButFloor;

    /// <summary>Ambient air temperature, K. Temperatures are simulated as the excess over this value.</summary>
    public float AmbientTemperature { get; init; } = SimulationConstants.AmbientTemperature;

    /// <summary>Fraction of the smoke lost per second (evaporation, ventilation): density decays as (1 − f)^t.</summary>
    public float DissipationPerSecond { get; init; } = 0.01f;

    /// <summary>
    /// Rate (1/s) at which warm or chilled air returns to ambient temperature (lumped heat exchange with surfaces and
    /// radiation, which the grid does not resolve). 0.05 /s = 20 s time constant.
    /// </summary>
    public float TemperatureRelaxationRate { get; init; } = 0.05f;

    /// <summary>Scattering coefficient at smoke density 1, 1/m (the renderer's σₛ = density × this value).</summary>
    public float ScatteringAtFullDensity { get; init; } = 0.5f;

    /// <summary>
    /// Mass scattering coefficient of the aerosol, m²/kg: scattering per unit mass concentration. Converts emitter
    /// output (kg/s of fluid aerosol) into density. 3000 m²/kg (3 m²/g) is Mie theory for glycol/water and mineral-oil
    /// droplets of about 1 µm (Q_ext ≈ 2.5, ρ ≈ 1050 kg/m³: k = 3Q/(2ρd)).
    /// </summary>
    public float MassScatteringCoefficient { get; init; } = 3000f;

    /// <summary>Constant ventilation airflow, m/s. Drives the domain-mean velocity (only possible through open faces).</summary>
    public Vector3 Airflow { get; init; }

    /// <summary>Time constant with which the mean flow follows <see cref="Airflow"/>, s.</summary>
    public float AirflowResponseTime { get; init; } = 2f;

    /// <summary>How density and temperature are advected.</summary>
    public ScalarAdvectionScheme ScalarAdvection { get; init; } = ScalarAdvectionScheme.MacCormack;

    /// <summary>
    /// Whether the advected smoke is rescaled so its total mass changes only by what flows out through open faces
    /// (global mass fixer; see <c>docs/simulation.md</c>). Off = plain semi-Lagrangian/MacCormack, which gains or loses
    /// mass in recirculating flows.
    /// </summary>
    public bool ConserveMass { get; init; } = true;

    /// <summary>Molecular/turbulent diffusivity of smoke and heat, m²/s (0 = off; numerical diffusion already acts).</summary>
    public float Diffusivity { get; init; }

    /// <summary>Kinematic (eddy) viscosity, m²/s (0 = off). Air's molecular value 1.5×10⁻⁵ m²/s is negligible at grid scale.</summary>
    public float Viscosity { get; init; }

    /// <summary>Jacobi iterations of the implicit diffusion and viscosity solves.</summary>
    public int DiffusionIterations { get; init; } = 8;

    /// <summary>
    /// Pressure solve tolerance: the largest remaining cell divergence relative to the largest divergence before the
    /// projection.
    /// </summary>
    public float PressureTolerance { get; init; } = 1e-4f;

    /// <summary>Preconditioner of the pressure solve; results agree to the tolerance, multigrid is faster on large grids.</summary>
    public PressurePreconditioner PressurePreconditioner { get; init; } = PressurePreconditioner.Multigrid;

    /// <summary>Iteration cap of the pressure solver (preconditioned conjugate gradient).</summary>
    public int MaxPressureIterations { get; init; } = 200;

    /// <summary>Safety limit for any velocity component, m/s.</summary>
    public float MaxSpeed { get; init; } = 60f;

    /// <summary>Safety limit for the temperature excess (positive or negative), K.</summary>
    public float MaxTemperatureExcess { get; init; } = 1000f;

    /// <summary>Threads used by the solver (≤ 0 = all cores). Results do not depend on it.</summary>
    public int MaxDegreeOfParallelism { get; init; } = -1;

    /// <summary>
    /// Settings for the simulation box of an <see cref="AtmosphereVolumeComponent"/> whose entity sits at
    /// <paramref name="worldCentre"/> (the component's box is centred on the entity and axis-aligned in world space).
    /// </summary>
    /// <param name="component">The atmosphere volume.</param>
    /// <param name="worldCentre">World position of the entity, m.</param>
    public static SmokeSimulationSettings FromComponent(AtmosphereVolumeComponent component, Vector3 worldCentre)
    {
        ArgumentNullException.ThrowIfNull(component);
        var half = component.Size * 0.5f;
        return new SmokeSimulationSettings
        {
            Bounds = new Aabb(worldCentre - half, worldCentre + half),
            CellSize = component.CellSize,
            DissipationPerSecond = component.Dissipation,
            ScatteringAtFullDensity = component.ScatteringAtFullDensity,
            Airflow = component.Airflow,
        };
    }
}
