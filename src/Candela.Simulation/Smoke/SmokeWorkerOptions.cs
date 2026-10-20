namespace Candela.Simulation;

/// <summary>How a <see cref="SmokeWorker"/> relates simulated time to the frames it is given.</summary>
public enum SmokeTiming
{
    /// <summary>
    /// Every submitted frame is simulated in order with exactly its time and emitters, at the requested cell size:
    /// the results are bit-identical to calling <see cref="SmokeSimulation.Step"/> with the same sequence (offline
    /// rendering, the command-line tool, tests). A caller that wants the result of a frame waits for it
    /// (<see cref="SmokeWorker.Synchronize()"/>).
    /// </summary>
    Lockstep,

    /// <summary>
    /// The solver keeps up with wall-clock time on its own thread: frames that arrive while it is busy are merged
    /// (their time adds up, the newest emitters apply), each call simulates only what it can finish within
    /// <see cref="SmokeWorkerOptions.MaxLatency"/> and drops the rest (counted, see <see cref="SmokeWorker.IsBehindRealTime"/>),
    /// and the cell size is chosen and adapted so a step fits the time budget
    /// (<see cref="SmokeWorkerOptions.RealTimeLoad"/>). Results depend on the machine's speed (the editor).
    /// </summary>
    RealTime,
}

/// <summary>Configuration of a <see cref="SmokeWorker"/>. Times are seconds.</summary>
public sealed record SmokeWorkerOptions
{
    /// <summary>Initial timing (can be changed later with <see cref="SmokeWorker.Timing"/>).</summary>
    public SmokeTiming Timing { get; init; } = SmokeTiming.Lockstep;

    /// <summary>
    /// The real-time budget: the share of each simulated sub-step's duration the solver may spend computing it
    /// (0.6 = 20 ms of wall time for a 1/30 s sub-step). The cell size is chosen so a step fits it, leaving headroom
    /// for load spikes and other work. Clamped to 0.05 … 1.
    /// </summary>
    public float RealTimeLoad { get; init; } = 0.6f;

    /// <summary>
    /// Whether the real-time worker chooses its cell size from the time budget (and keeps adapting it to the measured
    /// step cost). Off: the requested cell size always, dropping time when the machine is too slow.
    /// </summary>
    public bool AdaptCellSize { get; init; } = true;

    /// <summary>
    /// Coarsest cell the real-time worker may use, as a multiple of the requested cell size (≥ 1). Each adaptation level
    /// multiplies the cell edge by ∛2, halving the cell count; beyond this factor (or below 8 cells along an axis) time is
    /// dropped instead.
    /// </summary>
    public float MaxCellSizeFactor { get; init; } = 4f;

    /// <summary>
    /// Longest wall-clock time a real-time step call may take, s: a call that is behind simulates only as many fixed
    /// sub-steps as fit (at least one) and drops the rest, so the smoke never lags far behind the frames that drive it.
    /// </summary>
    public double MaxLatency { get; init; } = 0.1;

    /// <summary>Frames a lockstep caller may queue before <see cref="SmokeWorker.Submit"/> waits for the solver (≥ 1).</summary>
    public int MaxQueuedFrames { get; init; } = 2;

    /// <summary>
    /// Share of real time that must be dropped (averaged over a few seconds) before <see cref="SmokeWorker.IsBehindRealTime"/>
    /// reports it; isolated hiccups below it (a garbage collection, a resize) are not worth a warning.
    /// </summary>
    public float BehindThreshold { get; init; } = 0.05f;

    /// <summary>
    /// Priority of the solver's threads. Below normal by default, so the main thread (UI, rendering) wins every contention
    /// for a core; operating systems that ignore thread priorities for unprivileged processes keep the default.
    /// </summary>
    public ThreadPriority Priority { get; init; } = ThreadPriority.BelowNormal;

    /// <summary>Where the measured cost per cell is kept (the process-wide model; tests pass their own).</summary>
    internal SmokeCostModel CostModel { get; init; } = SmokeCostModel.Shared;
}
