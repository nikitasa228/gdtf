namespace Candela.Simulation;

/// <summary>
/// What one fixed sub-step of the smoke solver costs on this machine: wall-clock seconds per grid cell, by thread
/// count. Real-time <see cref="SmokeWorker"/>s record what they measure and choose the cell size of a new grid from it,
/// so the second domain of a session starts at the right resolution. Until something was measured, a prior is used
/// (the solver's cost is nearly proportional to the cell count: the multigrid pressure solve needs about the same
/// iterations at any resolution). <see cref="Shared"/> is the process-wide cache; thread-safe.
/// </summary>
internal sealed class SmokeCostModel
{
    /// <summary>
    /// Prior cost of one sub-step per cell on one thread, s (0.5–0.6 µs measured on a 2.1 GHz Xeon, see
    /// docs/simulation.md; a typical desktop is faster, which the measurement then shows).
    /// </summary>
    public const double DefaultSecondsPerCell = 0.6e-6;

    /// <summary>Assumed gain of each additional thread over one (the solver is largely memory-bound: 1.6× on 4 threads).</summary>
    private const double PriorGainPerExtraThread = 0.25;

    private const int MaxThreads = 64;
    private readonly double[] _measured = new double[MaxThreads + 1];
    private readonly object _lock = new();

    /// <summary>The model shared by every worker of the process.</summary>
    public static SmokeCostModel Shared { get; } = new();

    /// <summary>Estimated wall-clock seconds per cell and sub-step with <paramref name="threads"/> solver threads.</summary>
    public double SecondsPerCell(int threads)
    {
        int t = Math.Clamp(threads, 1, MaxThreads);
        lock (_lock)
        {
            if (_measured[t] > 0)
                return _measured[t];
        }

        return DefaultSecondsPerCell / (1 + PriorGainPerExtraThread * (t - 1));
    }

    /// <summary>Folds a measured cost (seconds per cell and sub-step) into the estimate for <paramref name="threads"/> threads.</summary>
    public void Record(int threads, double secondsPerCell)
    {
        if (!(secondsPerCell > 0) || !double.IsFinite(secondsPerCell))
            return;
        int t = Math.Clamp(threads, 1, MaxThreads);
        lock (_lock)
        {
            double s = _measured[t];
            _measured[t] = s > 0 ? s + 0.25 * (secondsPerCell - s) : secondsPerCell;
        }
    }
}
