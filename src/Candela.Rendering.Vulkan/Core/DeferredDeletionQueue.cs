namespace Candela.Rendering.Vulkan;

/// <summary>
/// Destroys GPU objects once no frame in flight can use them. An object retired while frame <c>N</c> is the last
/// begun frame may still be used by frames <c>N</c> and <c>N + 1</c> (resources replaced between frames are
/// referenced by descriptor sets of both frame slots until each slot is rewritten), so it is destroyed when frame
/// <c>N + FramesInFlight + 1</c> begins — after the fence of frame <c>N + 1</c> was waited for. Main thread only.
/// </summary>
internal sealed class DeferredDeletionQueue
{
    private readonly Queue<(ulong Frame, IDisposable Item)> _queue = new();

    public int Count => _queue.Count;

    public void Enqueue(IDisposable item, ulong currentFrame) => _queue.Enqueue((currentFrame, item));

    /// <summary>Destroys everything retired at least <c>FramesInFlight + 1</c> frames before <paramref name="beginningFrame"/>.</summary>
    public void Collect(ulong beginningFrame)
    {
        while (_queue.Count > 0 && _queue.Peek().Frame + VulkanContext.FramesInFlight + 1 <= beginningFrame)
            _queue.Dequeue().Item.Dispose();
    }

    /// <summary>Destroys everything (the device must be idle).</summary>
    public void DrainAll()
    {
        while (_queue.Count > 0)
            _queue.Dequeue().Item.Dispose();
    }
}

/// <summary>Adapts a callback to <see cref="IDisposable"/> for the deletion queue.</summary>
internal sealed class DisposeAction : IDisposable
{
    private Action? _action;

    public DisposeAction(Action action) => _action = action;

    public void Dispose()
    {
        var a = _action;
        _action = null;
        a?.Invoke();
    }
}
