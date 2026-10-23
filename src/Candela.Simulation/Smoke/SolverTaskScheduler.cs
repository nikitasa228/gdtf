using System.Collections.Concurrent;

namespace Candela.Simulation;

/// <summary>
/// A task scheduler with its own threads for the parallel slices of a <see cref="SmokeSimulation"/> owned by a
/// <see cref="SmokeWorker"/>. The solver then never takes threads from the shared thread pool, which the main thread
/// needs for its own parallel work (DMX evaluation), and its threads can run below normal priority so the main thread
/// (UI, render submission) wins every contention for a core. Tasks queued from any thread run on the scheduler's
/// threads; a task may also run inline on the thread that waits for it (the worker thread itself takes part in every
/// <see cref="Parallel.For(int, int, ParallelOptions, Action{int})"/>).
/// </summary>
internal sealed class SolverTaskScheduler : TaskScheduler, IDisposable
{
    private readonly BlockingCollection<Task> _tasks = new();
    private readonly Thread[] _threads;

    /// <summary>Starts <paramref name="threadCount"/> background threads (at least 1) with the given priority.</summary>
    public SolverTaskScheduler(int threadCount, ThreadPriority priority, string name)
    {
        _threads = new Thread[Math.Max(1, threadCount)];
        for (int i = 0; i < _threads.Length; i++)
        {
            var thread = new Thread(Run) { IsBackground = true, Name = $"{name} {i + 1}" };
            SmokeWorker.TrySetPriority(thread, priority);
            _threads[i] = thread;
            thread.Start();
        }
    }

    /// <inheritdoc />
    public override int MaximumConcurrencyLevel => _threads.Length + 1;

    /// <summary>Lets the threads finish the queued tasks and end (they are background threads; nothing waits for them).</summary>
    public void Dispose() => _tasks.CompleteAdding();

    /// <inheritdoc />
    protected override void QueueTask(Task task)
    {
        try
        {
            _tasks.Add(task);
        }
        catch (InvalidOperationException)
        {
            // Disposed: no solver runs any more; a straggling task (a parallel loop's spare replica) runs inline.
            TryExecuteTask(task);
        }
    }

    /// <inheritdoc />
    protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) =>
        // The thread that starts a parallel loop runs its root task inline and so takes part in the work; refusing
        // would leave it waiting for the scheduler's threads. A queued task that already ran is not run again.
        TryExecuteTask(task);

    /// <inheritdoc />
    protected override IEnumerable<Task> GetScheduledTasks() => _tasks.ToArray();

    private void Run()
    {
        foreach (var task in _tasks.GetConsumingEnumerable())
            TryExecuteTask(task);
    }
}
