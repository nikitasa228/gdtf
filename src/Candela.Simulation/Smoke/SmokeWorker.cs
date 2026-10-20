using System.Diagnostics;
using System.Numerics;
using Candela.Core.Diagnostics;
using Candela.Core.Math;

namespace Candela.Simulation;

/// <summary>
/// Runs a <see cref="SmokeSimulation"/> on its own thread so the main thread never waits for the solver. The main
/// thread hands over each frame's time and emitters (<see cref="Submit"/>) and commands (obstacles, reset, puffs), which
/// the worker applies in order; finished states are published as <see cref="SmokeFrame"/>s, triple-buffered, and taken
/// by the main thread with <see cref="AcquireLatest"/> without blocking — a step that takes 60 ms costs the main thread
/// a few microseconds.
/// </summary>
/// <remarks>
/// <para><b>Timing.</b> In <see cref="SmokeTiming.Lockstep"/> every frame is simulated exactly as
/// <see cref="SmokeSimulation.Step"/> would (bit-identical results at the requested cell size); a caller that needs the
/// result waits with <see cref="Synchronize()"/>. In <see cref="SmokeTiming.RealTime"/> the worker keeps up with wall-clock
/// time: frames that arrive while it is busy merge, a call that is behind simulates what fits
/// <see cref="SmokeWorkerOptions.MaxLatency"/> and drops the rest (<see cref="IsBehindRealTime"/>), and the cell size is
/// chosen from the time budget (<see cref="SmokeWorkerOptions.RealTimeLoad"/>) and adapted to the measured step cost:
/// coarsened (the cell edge × ∛2 per level, half the cells) when a step exceeds the budget, refined again when a finer
/// grid would fit comfortably. The state moves to the new grid with its smoke mass and heat conserved.</para>
/// <para><b>Threads.</b> The solver runs on the worker thread plus <see cref="Parallelism"/> − 1 dedicated threads, all
/// at <see cref="SmokeWorkerOptions.Priority"/> (below normal), never on the shared thread pool. In real time it uses half
/// the cores by default, leaving the others to the main thread, rendering, DMX evaluation and network input. The public
/// members are for one controlling thread (the engine's main thread); the worker synchronises with it internally.</para>
/// <para><b>Robustness.</b> Invalid settings are replaced and reported (<see cref="Issues"/>); an exception inside the
/// solver stops the simulation with an error issue instead of crashing the process (<see cref="Fault"/>), and
/// <see cref="Reconfigure"/> starts a fresh one.</para>
/// </remarks>
public sealed class SmokeWorker : IDisposable
{
    /// <summary>Fewest cells along any axis an adapted grid may have.</summary>
    private const int MinCellsPerAxis = 8;

    /// <summary>Sub-steps on a new grid that are not measured (just-in-time compilation, cold caches, first projection).</summary>
    private const int WarmUpSteps = 2;

    /// <summary>Measured sub-steps on a grid before the cell size may change again.</summary>
    private const int MeasuredStepsBeforeAdapting = 12;

    /// <summary>
    /// Measured sub-steps before coarsening a grid that cannot keep up at all (a step takes longer than
    /// <see cref="RescueFactor"/> × the budget: with the default budget, about the sub-step's own duration).
    /// </summary>
    private const int MeasuredStepsBeforeRescue = 2;

    /// <summary>Step cost, in budgets, above which the grid is coarsened at once (time is being dropped).</summary>
    private const double RescueFactor = 1.5;

    /// <summary>Wall-clock time the dropped share is at least averaged over, s (a hiccup in the first second is not 100 %).</summary>
    private const double MinDroppedWindow = 1.0;

    /// <summary>Averaging time of the dropped share of real time, s.</summary>
    private const double DroppedTimeConstant = 3.0;

    /// <summary>Submitted frame time still waiting for the solver above which it counts as behind real time, s.</summary>
    private const double MaxLag = 0.5;

    private readonly object _gate = new();
    private readonly SmokeWorkerOptions _options;
    private readonly SolverTaskScheduler? _scheduler;
    private readonly Thread _thread;
    private readonly int _parallelism;
    private bool _disposed;

    // ---- shared state, guarded by _gate
    private WorkItem[] _items = new WorkItem[8];
    private int _head, _count, _queuedFrames;
    private readonly Stack<SmokeEmitter[]> _emitterPool = new();
    private bool _busy, _stopping, _exited;
    private SmokeFrame _ready;
    private bool _readyFresh;
    private long _version;
    private Issue[] _issues;
    private Exception? _fault;

    // ---- main-thread state
    private SmokeFrame _front;
    private SmokeTiming _timing;
    private SmokeSimulationSettings _settings;
    private double _submittedTime;

    // ---- worker-thread state
    private SmokeFrame _back;
    private SmokeSimulation? _simulation;
    private int _generation;
    private SmokeTiming _workerTiming;
    private SmokeSimulationSettings _workerSettings;
    private SmokeSimulationSettings _rawSettings;
    private IReadOnlyList<Issue> _settingsIssues;
    private ObstacleBox[] _obstacles = Array.Empty<ObstacleBox>();
    private readonly List<float> _levelCellSize = new();
    private readonly List<int> _levelCells = new();
    private int _level;
    private double _consumed, _dropped;
    private double _droppedWeighted, _wallWeighted;
    private double _stepSeconds;
    private int _stepsOnGrid;
    private long _lastFrameTimestamp, _gridTimestamp;
    private bool _trimNextFrame;
    private Issue? _adaptIssue, _faultIssue;

    /// <summary>
    /// Starts the worker thread and the simulation for <paramref name="settings"/> (created on the worker thread; until
    /// it publishes, <see cref="Current"/> is an empty frame of the requested grid).
    /// </summary>
    public SmokeWorker(SmokeSimulationSettings settings, SmokeWorkerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _options = options ?? new SmokeWorkerOptions();
        _timing = _workerTiming = Enum.IsDefined(_options.Timing) ? _options.Timing : SmokeTiming.Lockstep;
        var issues = new List<Issue>();
        _rawSettings = settings;
        _settings = _workerSettings = SmokeSimulation.Sanitize(settings, issues);
        _front = NewFrame(_settings, issues);
        _ready = NewFrame(_settings, null);
        _back = NewFrame(_settings, null);
        _issues = issues.ToArray();
        _settingsIssues = _issues;

        int requested = _settings.MaxDegreeOfParallelism;
        int cores = Environment.ProcessorCount;
        _parallelism = requested > 0 ? requested : _timing == SmokeTiming.RealTime ? Math.Max(1, cores / 2) : Math.Max(1, cores);
        if (_parallelism > 1)
            _scheduler = new SolverTaskScheduler(_parallelism - 1, _options.Priority, "Candela smoke solver");
        Enqueue(new WorkItem { Kind = WorkKind.Configure, Settings = settings });
        _thread = new Thread(Run) { IsBackground = true, Name = "Candela smoke solver" };
        TrySetPriority(_thread, _options.Priority);
        _thread.Start();
    }

    /// <summary>The requested settings (after sanitising) of the last <see cref="Reconfigure"/> or of the constructor.</summary>
    public SmokeSimulationSettings Settings => _settings;

    /// <summary>Threads the solver uses (the worker thread and its helpers).</summary>
    public int Parallelism => _parallelism;

    /// <summary>
    /// Lockstep or real time. Switching to <see cref="SmokeTiming.Lockstep"/> returns to the requested cell size (the
    /// state moves to that grid); frames already submitted keep the timing they were submitted with.
    /// </summary>
    public SmokeTiming Timing
    {
        get => _timing;
        set
        {
            if (!Enum.IsDefined(value) || value == _timing)
                return;
            ObjectDisposedException.ThrowIf(_disposed, this);
            _timing = value;
            lock (_gate)
            {
                Enqueue(new WorkItem { Kind = WorkKind.Timing, Timing = value });
                Monitor.PulseAll(_gate);
            }
        }
    }

    /// <summary>
    /// The frame taken by the last <see cref="AcquireLatest"/> or <see cref="Synchronize()"/> (an empty frame of the
    /// requested grid before the first publication). It does not change until the next of those calls.
    /// </summary>
    public SmokeFrame Current => _front;

    /// <summary>Problems of the settings, obstacles and the worker itself (a snapshot; safe to keep).</summary>
    public IReadOnlyList<Issue> Issues => Volatile.Read(ref _issues);

    /// <summary>The exception that stopped the solver, or null while it runs.</summary>
    public Exception? Fault
    {
        get
        {
            lock (_gate)
                return _fault;
        }
    }

    /// <summary>Frame time submitted so far, s.</summary>
    public double SubmittedTime => _submittedTime;

    /// <summary>
    /// Submitted frame time the solver has not taken in yet at <see cref="Current"/>, s: what is queued or being
    /// simulated (a fraction of a sub-step when the worker keeps up).
    /// </summary>
    public double Lag => Math.Max(0, _submittedTime - _front.ConsumedTime);

    /// <summary>Simulation time dropped so far because the solver could not keep up (as of <see cref="Current"/>), s.</summary>
    public double DroppedTime => _front.DroppedTime;

    /// <summary>
    /// Share of real time the solver could not simulate over the last few seconds (0 … 1), decaying while nothing more
    /// is dropped; 0 in lockstep.
    /// </summary>
    public double DroppedFraction
    {
        get
        {
            if (_timing != SmokeTiming.RealTime)
                return 0;
            var frame = _front;
            if (frame.PublishedTimestamp == 0)
                return 0;
            double age = Stopwatch.GetElapsedTime(frame.PublishedTimestamp).TotalSeconds;
            return frame.DroppedFraction * Math.Exp(-Math.Max(0, age) / DroppedTimeConstant);
        }
    }

    /// <summary>
    /// True in real time while the simulated smoke does not keep up with wall-clock time: a noticeable share of real time
    /// was dropped recently (<see cref="SmokeWorkerOptions.BehindThreshold"/>) or more than half a second of submitted
    /// frames still waits for the solver. The editor shows a warning then.
    /// </summary>
    public bool IsBehindRealTime =>
        _timing == SmokeTiming.RealTime && (DroppedFraction >= _options.BehindThreshold || Lag >= MaxLag || Fault is not null);

    /// <summary>
    /// Hands one frame to the solver: <paramref name="deltaTime"/> seconds with <paramref name="emitters"/> active (the
    /// emitters are copied). Lockstep queues it (waiting while <see cref="SmokeWorkerOptions.MaxQueuedFrames"/> are
    /// queued); real time merges it into a frame still waiting. Non-positive or non-finite times are ignored. Does not
    /// allocate in steady state.
    /// </summary>
    public void Submit(double deltaTime, ReadOnlySpan<SmokeEmitter> emitters)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!double.IsFinite(deltaTime) || deltaTime <= 0)
            return;
        lock (_gate)
        {
            if (_exited)
                return;
            _submittedTime += deltaTime;
            if (_timing == SmokeTiming.RealTime && _count > 0)
            {
                ref var tail = ref Tail();
                if (tail.Kind == WorkKind.Frame)
                {
                    tail.DeltaTime += deltaTime;
                    if (tail.Emitters!.Length < emitters.Length)
                    {
                        _emitterPool.Push(tail.Emitters);
                        tail.Emitters = RentEmitters(emitters.Length);
                    }

                    emitters.CopyTo(tail.Emitters);
                    tail.EmitterCount = emitters.Length;
                    return;
                }
            }

            if (_timing == SmokeTiming.Lockstep)
            {
                int limit = Math.Max(1, _options.MaxQueuedFrames);
                while (_queuedFrames >= limit && !_exited)
                    Monitor.Wait(_gate);
                if (_exited)
                    return;
            }

            var buffer = RentEmitters(emitters.Length);
            emitters.CopyTo(buffer);
            Enqueue(new WorkItem { Kind = WorkKind.Frame, DeltaTime = deltaTime, Emitters = buffer, EmitterCount = emitters.Length });
            _queuedFrames++;
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>
    /// Takes the newest published frame as <see cref="Current"/> if there is one (never waits). Returns true when
    /// <see cref="Current"/> changed.
    /// </summary>
    public bool AcquireLatest()
    {
        lock (_gate)
        {
            if (!_readyFresh)
                return false;
            (_front, _ready) = (_ready, _front);
            _readyFresh = false;
            return true;
        }
    }

    /// <summary>Waits until every submitted frame and command has been processed, then takes the result as <see cref="Current"/>.</summary>
    public void Synchronize() => Synchronize(Timeout.InfiniteTimeSpan);

    /// <summary>
    /// <see cref="Synchronize()"/> with a time limit; returns false when the worker was still busy after
    /// <paramref name="timeout"/> (<see cref="Current"/> is then the newest frame published so far).
    /// </summary>
    public bool Synchronize(TimeSpan timeout)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        bool idle;
        lock (_gate)
            idle = WaitIdleLocked(timeout);
        AcquireLatest();
        return idle;
    }

    /// <summary>
    /// Replaces the solid obstacles (queued; the boxes are copied). See <see cref="SmokeSimulation.SetObstacles"/>.
    /// In real time a replacement still waiting is overwritten, so dragging a wall does not queue every intermediate
    /// position.
    /// </summary>
    public void SetObstacles(ReadOnlySpan<ObstacleBox> boxes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var copy = boxes.ToArray();
        lock (_gate)
        {
            if (_timing == SmokeTiming.RealTime && _count > 0 && Tail().Kind == WorkKind.Obstacles)
                Tail().Obstacles = copy;
            else
                Enqueue(new WorkItem { Kind = WorkKind.Obstacles, Obstacles = copy });
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>
    /// Replaces the simulation with a fresh one for <paramref name="settings"/> (queued; the smoke is discarded, the
    /// obstacles are kept). Used when the atmosphere volume changes.
    /// </summary>
    public void Reconfigure(SmokeSimulationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ObjectDisposedException.ThrowIf(_disposed, this);
        _settings = SmokeSimulation.Sanitize(settings, new List<Issue>());
        lock (_gate)
        {
            if (_count > 0 && Tail().Kind == WorkKind.Configure)
                Tail().Settings = settings;
            else
                Enqueue(new WorkItem { Kind = WorkKind.Configure, Settings = settings });
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>Clears velocity, smoke, temperature and time (queued). Obstacles are kept. See <see cref="SmokeSimulation.Reset"/>.</summary>
    public void Reset() => EnqueueCommand(new WorkItem { Kind = WorkKind.Reset });

    /// <summary>Adds a smooth puff of smoke (queued). See <see cref="SmokeSimulation.AddSmoke"/>.</summary>
    public void AddSmoke(Vector3 center, float radius, float densityIncrement, float temperatureExcess = 0f) =>
        EnqueueCommand(new WorkItem
        {
            Kind = WorkKind.AddSmoke,
            Point = center,
            Radius = radius,
            Value = densityIncrement,
            Temperature = temperatureExcess,
        });

    /// <summary>Sets the density of every fluid cell (queued). See <see cref="SmokeSimulation.Fill"/>.</summary>
    public void Fill(float density) => EnqueueCommand(new WorkItem { Kind = WorkKind.Fill, Value = density });

    /// <summary>
    /// Waits for the queued work and copies the complete state. Throws <see cref="InvalidOperationException"/> when the
    /// solver stopped after a fault.
    /// </summary>
    public SmokeSimulationSnapshot CaptureSnapshot()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            WaitIdleLocked(Timeout.InfiniteTimeSpan);
            return RunningSimulation().CaptureSnapshot();
        }
    }

    /// <summary>
    /// Waits for the queued work and restores a state captured from this worker on the same grid (in real time the grid
    /// may have been adapted since: then an <see cref="ArgumentException"/> is thrown and nothing changes). The restored
    /// state becomes <see cref="Current"/>.
    /// </summary>
    public void RestoreSnapshot(SmokeSimulationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            WaitIdleLocked(Timeout.InfiniteTimeSpan);
            RunningSimulation().RestoreSnapshot(snapshot);
            // The worker is idle and cannot take work while the lock is held, so its back frame is free to fill here.
            Publish();
        }

        AcquireLatest();
    }

    /// <summary>
    /// Stops the worker after the item it is working on (it does not wait for it). The threads end on their own and the
    /// simulation's memory is released with them.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        lock (_gate)
        {
            _stopping = true;
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>Makes the worker fail on its thread as an internal error would (tests of the fault handling).</summary>
    internal void FailForTesting() => EnqueueCommand(new WorkItem { Kind = WorkKind.Fail });

    /// <summary>Sets a thread's priority where the operating system allows it (ignored elsewhere).</summary>
    internal static void TrySetPriority(Thread thread, ThreadPriority priority)
    {
        try
        {
            thread.Priority = priority;
        }
        catch (Exception e) when (e is ThreadStateException or PlatformNotSupportedException or NotSupportedException
                                       or ArgumentException or UnauthorizedAccessException)
        {
            // Not supported or not permitted: the thread keeps normal priority.
        }
    }

    // ------------------------------------------------------------------ queue (callers hold _gate)

    private enum WorkKind : byte
    {
        Frame,
        Configure,
        Obstacles,
        Reset,
        AddSmoke,
        Fill,
        Timing,
        Fail,
    }

    private struct WorkItem
    {
        public WorkKind Kind;
        public double DeltaTime;
        public SmokeEmitter[]? Emitters;
        public int EmitterCount;
        public SmokeSimulationSettings? Settings;
        public ObstacleBox[]? Obstacles;
        public Vector3 Point;
        public float Radius, Value, Temperature;
        public SmokeTiming Timing;
    }

    private void EnqueueCommand(WorkItem item)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            Enqueue(item);
            Monitor.PulseAll(_gate);
        }
    }

    private void Enqueue(WorkItem item)
    {
        if (_count == _items.Length)
        {
            var grown = new WorkItem[_items.Length * 2];
            for (int n = 0; n < _count; n++)
                grown[n] = _items[(_head + n) % _items.Length];
            _items = grown;
            _head = 0;
        }

        _items[(_head + _count) % _items.Length] = item;
        _count++;
    }

    private WorkItem Dequeue()
    {
        var item = _items[_head];
        _items[_head] = default;
        _head = (_head + 1) % _items.Length;
        _count--;
        return item;
    }

    private ref WorkItem Tail() => ref _items[(_head + _count - 1) % _items.Length];

    private SmokeEmitter[] RentEmitters(int count)
    {
        while (_emitterPool.TryPop(out var buffer))
        {
            if (buffer.Length >= count)
                return buffer;
        }

        return new SmokeEmitter[Math.Max(16, count)];
    }

    /// <summary>Waits (holding the lock between waits) until the queue is empty and the worker idle; false on timeout.</summary>
    private bool WaitIdleLocked(TimeSpan timeout)
    {
        long deadline = timeout == Timeout.InfiniteTimeSpan ? long.MaxValue : Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while ((_count > 0 || _busy) && !_exited)
        {
            if (deadline == long.MaxValue)
            {
                Monitor.Wait(_gate);
                continue;
            }

            long remaining = deadline - Stopwatch.GetTimestamp();
            if (remaining <= 0)
                return false;
            Monitor.Wait(_gate, TimeSpan.FromSeconds((double)remaining / Stopwatch.Frequency));
        }

        return true;
    }

    private SmokeSimulation RunningSimulation() =>
        _simulation ?? throw new InvalidOperationException("The smoke simulation is not running (it stopped after an error; see Issues).");

    // ------------------------------------------------------------------ worker thread

    private void Run()
    {
        SmokeEmitter[]? returned = null;
        try
        {
            while (true)
            {
                WorkItem item;
                lock (_gate)
                {
                    if (returned is not null)
                    {
                        _emitterPool.Push(returned);
                        returned = null;
                    }

                    _busy = false;
                    Monitor.PulseAll(_gate);
                    while (_count == 0 && !_stopping)
                        Monitor.Wait(_gate);
                    if (_stopping)
                        return;
                    item = Dequeue();
                    if (item.Kind == WorkKind.Frame)
                        _queuedFrames--;
                    _busy = true;
                    Monitor.PulseAll(_gate);
                }

                try
                {
                    Process(in item);
                }
                catch (Exception e)
                {
                    Stop(e);
                }

                returned = item.Emitters;
            }
        }
        finally
        {
            lock (_gate)
            {
                _busy = false;
                _exited = true;
                Monitor.PulseAll(_gate);
            }

            _simulation = null;
            _scheduler?.Dispose();
        }
    }

    private void Process(in WorkItem item)
    {
        switch (item.Kind)
        {
            case WorkKind.Frame:
                RunFrame(item.DeltaTime, item.Emitters.AsSpan(0, item.EmitterCount));
                break;
            case WorkKind.Configure:
                Configure(item.Settings!);
                break;
            case WorkKind.Obstacles:
                _obstacles = item.Obstacles!;
                if (_simulation is not null)
                {
                    _simulation.SetObstacles(_obstacles);
                    PublishIssues();
                    Publish();
                }

                break;
            case WorkKind.Reset:
                if (_simulation is not null)
                {
                    _simulation.Reset();
                    Publish();
                }

                break;
            case WorkKind.AddSmoke:
                if (_simulation is not null)
                {
                    _simulation.AddSmoke(item.Point, item.Radius, item.Value, item.Temperature);
                    Publish();
                }

                break;
            case WorkKind.Fill:
                if (_simulation is not null)
                {
                    _simulation.Fill(item.Value);
                    Publish();
                }

                break;
            case WorkKind.Timing:
                SetWorkerTiming(item.Timing);
                break;
            case WorkKind.Fail:
                throw new InvalidOperationException("Failure requested by a test.");
        }
    }

    /// <summary>A fresh simulation for <paramref name="settings"/>, at the level the time budget allows in real time.</summary>
    private void Configure(SmokeSimulationSettings settings)
    {
        var issues = new List<Issue>();
        _rawSettings = settings;
        _workerSettings = SmokeSimulation.Sanitize(settings, issues);
        SmokeSimulation.LayoutGrid(_workerSettings, issues);
        _settingsIssues = issues;
        _faultIssue = null;
        lock (_gate)
            _fault = null;
        ComputeLevels();
        int level = Adapting ? InitialLevel() : 0;
        CreateSimulation(level, transferFrom: null);
        // The frames that arrived while the grid was being built are dropped, but not counted against the solver.
        _trimNextFrame = true;
    }

    private bool Adapting => _workerTiming == SmokeTiming.RealTime && _options.AdaptCellSize;

    private double Budget => Math.Clamp(_options.RealTimeLoad, 0.05f, 1f) * _workerSettings.TimeStep;

    /// <summary>Cell sizes and counts of the adaptation levels (level 0 = the requested grid).</summary>
    private void ComputeLevels()
    {
        var ignored = new List<Issue>();
        var basis = SmokeSimulation.LayoutGrid(_workerSettings, ignored);
        _levelCellSize.Clear();
        _levelCells.Clear();
        _levelCellSize.Add(_workerSettings.CellSize);
        _levelCells.Add(basis.Nx * basis.Ny * basis.Nz);
        float factor = float.IsFinite(_options.MaxCellSizeFactor) ? Math.Clamp(_options.MaxCellSizeFactor, 1f, 64f) : 4f;
        int maxLevel = (int)Math.Floor(3 * Math.Log2(factor) + 1e-6);
        for (int level = 1; level <= maxLevel; level++)
        {
            float h = basis.H * MathF.Pow(2f, level / 3f);
            var grid = SmokeSimulation.LayoutGrid(_workerSettings with { CellSize = h }, ignored);
            if (Math.Min(grid.Nx, Math.Min(grid.Ny, grid.Nz)) < MinCellsPerAxis)
                break;
            _levelCellSize.Add(grid.H);
            _levelCells.Add(grid.Nx * grid.Ny * grid.Nz);
        }
    }

    private int MaxLevel => _levelCells.Count - 1;

    /// <summary>The finest level whose estimated step cost fits 80 % of the budget.</summary>
    private int InitialLevel()
    {
        double perCell = _options.CostModel.SecondsPerCell(_parallelism);
        double target = 0.8 * Budget;
        for (int level = 0; level < MaxLevel; level++)
        {
            if (_levelCells[level] * perCell <= target)
                return level;
        }

        return MaxLevel;
    }

    private void CreateSimulation(int level, SmokeSimulation? transferFrom)
    {
        // The settings as given (so the simulation reports invalid values itself, as it would without the worker), at
        // the level's cell size, on the worker's threads.
        var settings = _rawSettings with
        {
            CellSize = level == 0 ? _rawSettings.CellSize : _levelCellSize[level],
            MaxDegreeOfParallelism = _parallelism,
        };
        var next = new SmokeSimulation(settings, _scheduler);
        if (_obstacles.Length > 0)
            next.SetObstacles(_obstacles);
        if (transferFrom is not null)
            next.TransferStateFrom(transferFrom);
        _simulation = next;
        _generation++;
        _level = level;
        _stepsOnGrid = 0;
        _stepSeconds = 0;
        _gridTimestamp = Stopwatch.GetTimestamp();
        UpdateAdaptIssue(estimate: level > 0 ? _levelCells[level] * _options.CostModel.SecondsPerCell(_parallelism) : 0);
        PublishIssues();
        Publish();
    }

    private void RunFrame(double deltaTime, ReadOnlySpan<SmokeEmitter> emitters)
    {
        _consumed += deltaTime;
        var simulation = _simulation;
        if (simulation is null)
        {
            // Stopped after a fault: the time is lost, and says so.
            _dropped += deltaTime;
            return;
        }

        if (_workerTiming == SmokeTiming.Lockstep)
        {
            _trimNextFrame = false;
            int run = simulation.Step(deltaTime, emitters);
            _dropped += simulation.LastStatistics.DroppedTime;
            if (run > 0)
                Publish();
            return;
        }

        long now = Stopwatch.GetTimestamp();
        if (_trimNextFrame)
        {
            // Time that passed while a new simulation was set up: beyond one sub-step it is lost (and counted), but it
            // does not mean the solver cannot keep up.
            _trimNextFrame = false;
            double setup = Math.Max(0, deltaTime - simulation.TimeStep);
            _dropped += setup;
            deltaTime -= setup;
        }

        int steps = simulation.StepCapped(deltaTime, emitters, RealTimeCap(simulation));
        double elapsed = Stopwatch.GetElapsedTime(now).TotalSeconds;
        double dropped = simulation.LastStatistics.DroppedTime;
        _dropped += dropped;
        TrackDropped(dropped, now);
        if (steps == 0)
            return;
        Measure(simulation, elapsed, steps);
        Publish();
        Adapt();
    }

    /// <summary>Sub-steps a real-time call may run: what fits <see cref="SmokeWorkerOptions.MaxLatency"/> (1 … the settings' cap).</summary>
    private int RealTimeCap(SmokeSimulation simulation)
    {
        double perStep = _stepSeconds > 0 ? _stepSeconds : simulation.CellCount * _options.CostModel.SecondsPerCell(_parallelism);
        double latency = double.IsFinite(_options.MaxLatency) && _options.MaxLatency > 0 ? _options.MaxLatency : 0.1;
        double fit = Math.Floor(latency / Math.Max(perStep, 1e-6));
        return (int)Math.Clamp(fit, 1, _workerSettings.MaxSubStepsPerCall);
    }

    /// <summary>
    /// Folds a frame into the dropped share of real time: Σ dropped / Σ wall time, both weighted by
    /// e^(−age / <see cref="DroppedTimeConstant"/>), with at least <see cref="MinDroppedWindow"/> of wall time in the
    /// denominator (so a single hiccup right after the start does not count as all of real time).
    /// </summary>
    private void TrackDropped(double dropped, long now)
    {
        double wall = _lastFrameTimestamp == 0 ? 0 : (now - _lastFrameTimestamp) / (double)Stopwatch.Frequency;
        _lastFrameTimestamp = now;
        double decay = Math.Exp(-Math.Max(0, wall) / DroppedTimeConstant);
        _droppedWeighted = _droppedWeighted * decay + dropped;
        _wallWeighted = _wallWeighted * decay + wall;
    }

    private float DroppedShare() =>
        (float)Math.Clamp(_droppedWeighted / Math.Max(_wallWeighted, MinDroppedWindow), 0, 1);

    private void Measure(SmokeSimulation simulation, double elapsed, int steps)
    {
        int before = _stepsOnGrid;
        _stepsOnGrid += steps;
        if (before < WarmUpSteps || !(elapsed > 0))
            return;
        double perStep = elapsed / steps;
        double alpha = 1 - Math.Exp(-steps / 8.0);
        _stepSeconds = _stepSeconds > 0 ? _stepSeconds + alpha * (perStep - _stepSeconds) : perStep;
        _options.CostModel.Record(_parallelism, perStep / simulation.CellCount);
    }

    /// <summary>
    /// Coarsens when the measured step exceeds the budget (to the first level predicted within 80 % of it; after only a
    /// couple of steps when it takes more than <see cref="RescueFactor"/> budgets, so time is dropped only briefly),
    /// refines one level when the finer grid is predicted within 75 % of it. Cost is proportional to the cell count.
    /// </summary>
    private void Adapt()
    {
        if (!Adapting || _simulation is null || !(_stepSeconds > 0))
            return;
        double budget = Budget;
        double cost = _stepSeconds;
        int measured = _stepsOnGrid - WarmUpSteps;
        bool severe = cost > RescueFactor * budget;
        if (measured < (severe ? MeasuredStepsBeforeRescue : MeasuredStepsBeforeAdapting))
            return;
        double onGrid = Stopwatch.GetElapsedTime(_gridTimestamp).TotalSeconds;
        if (cost > budget && _level < MaxLevel && (severe || onGrid >= _options.CoarsenInterval))
        {
            int level = _level + 1;
            while (level < MaxLevel && cost * _levelCells[level] / _levelCells[_level] > 0.8 * budget)
                level++;
            CreateSimulation(level, transferFrom: _simulation);
        }
        else if (_level > 0 && onGrid >= _options.RefineInterval && cost * _levelCells[_level - 1] / _levelCells[_level] < 0.75 * budget)
        {
            CreateSimulation(_level - 1, transferFrom: _simulation);
        }
    }

    private void SetWorkerTiming(SmokeTiming timing)
    {
        if (timing == _workerTiming)
            return;
        _workerTiming = timing;
        _lastFrameTimestamp = 0;
        _droppedWeighted = 0;
        _wallWeighted = 0;
        _gridTimestamp = Stopwatch.GetTimestamp();
        _stepsOnGrid = 0;
        _stepSeconds = 0;
        if (timing == SmokeTiming.Lockstep && _level != 0 && _simulation is not null)
            CreateSimulation(0, transferFrom: _simulation);
        else if (_simulation is not null)
            Publish();
    }

    private void UpdateAdaptIssue(double estimate)
    {
        if (_level == 0)
        {
            _adaptIssue = null;
            return;
        }

        double perStep = _stepSeconds > 0 ? _stepSeconds : estimate;
        _adaptIssue = new Issue(IssueSeverity.Note, IssueCategory.Scene, "simulation.cell-size-adapted",
            $"The smoke simulation uses {_levelCellSize[_level]:0.###} m cells instead of {_levelCellSize[0]:0.###} m to keep up " +
            $"with real time on this computer (a {_workerSettings.TimeStep * 1000:0} ms step takes about {perStep * 1000:0} ms " +
            $"at the requested size × {(double)_levelCells[0] / Math.Max(1, _levelCells[_level]):0.#} cells).");
    }

    /// <summary>Copies the current state into the back frame and makes it the ready one (worker thread, or idle worker).</summary>
    private void Publish()
    {
        var simulation = _simulation;
        if (simulation is null)
            return;
        var frame = _back;
        frame.CopyFrom(simulation, _generation);
        frame.ConsumedTime = _consumed;
        frame.DroppedTime = _dropped;
        frame.DroppedFraction = _workerTiming == SmokeTiming.RealTime ? DroppedShare() : 0f;
        frame.MillisecondsPerStep = (float)(_stepSeconds * 1000);
        frame.PublishedTimestamp = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            frame.Version = ++_version;
            _back = _ready;
            _ready = frame;
            _readyFresh = true;
        }
    }

    private void PublishIssues()
    {
        var all = new List<Issue>(_simulation?.Issues ?? _settingsIssues);
        if (_adaptIssue is not null)
            all.Add(_adaptIssue);
        if (_faultIssue is not null)
            all.Add(_faultIssue);
        Volatile.Write(ref _issues, all.ToArray());
    }

    /// <summary>An exception escaped the solver: stop simulating (frames are dropped) and report it.</summary>
    private void Stop(Exception e)
    {
        _simulation = null;
        _faultIssue = new Issue(IssueSeverity.Error, IssueCategory.Program, "simulation.smoke-failed",
            $"The smoke simulation stopped after an internal error ({e.GetType().Name}: {e.Message}); changing the atmosphere " +
            "volume starts it again.");
        lock (_gate)
            _fault = e;
        PublishIssues();
    }

    private static SmokeFrame NewFrame(SmokeSimulationSettings settings, List<Issue>? issues)
    {
        var grid = SmokeSimulation.LayoutGrid(settings, issues ?? new List<Issue>());
        return new SmokeFrame(grid.Bounds, grid.Nx, grid.Ny, grid.Nz, grid.H,
            settings.ScatteringAtFullDensity / settings.MassScatteringCoefficient);
    }
}
