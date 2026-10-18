using System.Diagnostics;

namespace Candela.App.Shell;

/// <summary>
/// Frame timing for the viewport readout: delta time of the last frame (clamped so a stall never makes simulations
/// jump), and frames per second / frame time averaged over a sliding window of recent frames. No allocations.
/// </summary>
internal sealed class FrameTimer
{
    /// <summary>Longest delta time handed to the engine (s); longer stalls (debugger, window drag) are cut.</summary>
    public const double MaxDeltaTime = 0.25;

    private readonly double[] _window = new double[90];
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _last;
    private double _sum;
    private int _index;
    private int _count;

    /// <summary>Seconds since the previous <see cref="Tick"/> (clamped to <see cref="MaxDeltaTime"/>).</summary>
    public double DeltaTime { get; private set; } = 1.0 / 60.0;

    /// <summary>Average frames per second over the recent window.</summary>
    public double FramesPerSecond => _sum > 0 ? _count / _sum : 0;

    /// <summary>Average frame time in milliseconds over the recent window.</summary>
    public double FrameMilliseconds => _count > 0 ? _sum / _count * 1000.0 : 0;

    /// <summary>Total frames ticked.</summary>
    public long FrameCount { get; private set; }

    /// <summary>Seconds since the timer was created.</summary>
    public double Elapsed => _clock.Elapsed.TotalSeconds;

    /// <summary>Marks the start of a frame and updates the statistics.</summary>
    public void Tick()
    {
        double now = _clock.Elapsed.TotalSeconds;
        double dt = FrameCount == 0 ? 1.0 / 60.0 : now - _last;
        _last = now;
        FrameCount++;
        Record(dt);
        DeltaTime = Math.Clamp(dt, 1e-4, MaxDeltaTime);
    }

    /// <summary>Adds a measured frame duration (tests and <see cref="Tick"/>).</summary>
    internal void Record(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds <= 0)
            return;
        if (_count == _window.Length)
            _sum -= _window[_index];
        else
            _count++;
        _window[_index] = seconds;
        _sum += seconds;
        _index = (_index + 1) % _window.Length;
    }
}
