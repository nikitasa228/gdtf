using System.Runtime.ExceptionServices;

namespace Candela.Dmx;

/// <summary>
/// A small fork-join pool for per-frame work: <see cref="For"/> splits an index range into chunks that the calling
/// thread and a fixed set of background threads claim with an interlocked counter, and returns when every index is
/// done. Unlike <c>Parallel.For</c> it does not allocate per call (the body must be a cached delegate), which keeps the
/// frame loop free of garbage. One caller at a time; not reentrant.
/// </summary>
internal sealed class WorkerPool : IDisposable
{
    private readonly Thread[] _threads;
    private readonly SemaphoreSlim _start = new(0);
    private readonly CountdownEvent _done = new(1);
    private Action<int>? _body;
    private int _count;
    private int _chunk;
    private int _next;
    private ExceptionDispatchInfo? _error;
    private volatile bool _disposed;

    /// <summary>Starts <paramref name="workers"/> background threads (the calling thread works too).</summary>
    public WorkerPool(int workers, string name)
    {
        _threads = new Thread[Math.Max(0, workers)];
        for (int i = 0; i < _threads.Length; i++)
        {
            _threads[i] = new Thread(WorkerLoop) { IsBackground = true, Name = $"{name} {i + 1}" };
            _threads[i].Start();
        }
    }

    /// <summary>Number of threads that work on a call, including the caller.</summary>
    public int Concurrency => _threads.Length + 1;

    /// <summary>
    /// Runs <paramref name="body"/> for every index in [0, <paramref name="count"/>) in chunks of
    /// <paramref name="chunk"/> indices. The first exception thrown by the body is rethrown on the caller after all
    /// threads have stopped.
    /// </summary>
    public void For(int count, int chunk, Action<int> body)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (count <= 0) return;
        chunk = Math.Max(1, chunk);
        int helpers = Math.Min(_threads.Length, (count - 1) / chunk);
        if (helpers == 0)
        {
            for (int i = 0; i < count; i++) body(i);
            return;
        }
        _body = body;
        _count = count;
        _chunk = chunk;
        _next = 0;
        _error = null;
        _done.Reset(helpers);
        _start.Release(helpers);
        try
        {
            Run();
        }
        catch (Exception e)
        {
            Fail(e);
        }
        finally
        {
            _done.Wait();
            _body = null;
        }
        _error?.Throw();
    }

    private void Fail(Exception e)
    {
        Interlocked.CompareExchange(ref _error, ExceptionDispatchInfo.Capture(e), null);
        Interlocked.Exchange(ref _next, int.MaxValue / 2); // stop handing out work
    }

    private void Run()
    {
        var body = _body!;
        int count = _count, chunk = _chunk;
        while (true)
        {
            int start = Interlocked.Add(ref _next, chunk) - chunk;
            if (start >= count) return;
            int end = Math.Min(start + chunk, count);
            for (int i = start; i < end; i++) body(i);
        }
    }

    private void WorkerLoop()
    {
        while (true)
        {
            _start.Wait();
            if (_disposed) return;
            try
            {
                Run();
            }
            catch (Exception e)
            {
                Fail(e);
            }
            finally
            {
                _done.Signal();
            }
        }
    }

    /// <summary>Stops the background threads.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _start.Release(_threads.Length);
        foreach (var t in _threads) t.Join(TimeSpan.FromSeconds(1));
        _start.Dispose();
        _done.Dispose();
    }
}
