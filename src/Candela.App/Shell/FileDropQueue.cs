using System.Numerics;
using System.Runtime.InteropServices;
using Hexa.NET.GLFW;

namespace Candela.App.Shell;

/// <summary>Files dropped onto the main window: their paths and where the cursor was (main-window pixels).</summary>
/// <param name="Paths">Full paths of the dropped files (folders are passed on as well and refused later).</param>
/// <param name="Position">Cursor position in main-window pixels (top-left origin) at the drop.</param>
internal sealed record FileDrop(IReadOnlyList<string> Paths, Vector2 Position);

/// <summary>
/// Receives files dragged from the desktop or a file manager onto the main window (GLFW drop callback) and queues them
/// for the workspace, which opens or imports them at the start of the next frame. GLFW calls the callback on the main
/// thread from <c>glfwPollEvents</c>; the queue is drained by the same thread.
/// </summary>
internal sealed unsafe class FileDropQueue : IDisposable
{
    /// <summary>Most files one drop may carry (a drop of a whole folder tree is cut off with a note).</summary>
    public const int MaxFilesPerDrop = 256;

    private readonly MainWindow _window;
    private readonly GLFWdropfun _callback;
    private readonly Queue<FileDrop> _drops = new();
    private bool _disposed;

    /// <summary>Installs the drop callback on <paramref name="window"/>.</summary>
    public FileDropQueue(MainWindow window)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        // The delegate is kept in a field: GLFW holds only a native pointer to it.
        _callback = OnDrop;
        if (!window.IsDisposed)
            GLFW.SetDropCallback(window.Handle, _callback);
    }

    /// <summary>Number of drops waiting.</summary>
    public int Count => _drops.Count;

    /// <summary>Queues a drop as if it came from the OS (scripts and tests).</summary>
    public void Enqueue(FileDrop drop)
    {
        ArgumentNullException.ThrowIfNull(drop);
        _drops.Enqueue(drop);
    }

    /// <summary>Takes the oldest waiting drop.</summary>
    public bool TryDequeue(out FileDrop drop)
    {
        if (_drops.Count > 0)
        {
            drop = _drops.Dequeue();
            return true;
        }

        drop = null!;
        return false;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (!_window.IsDisposed)
            GLFW.SetDropCallback(_window.Handle, (GLFWdropfun?)null);
        _drops.Clear();
    }

    private void OnDrop(GLFWwindow* window, int count, byte** paths)
    {
        // Never let an exception unwind into native code.
        try
        {
            if (count <= 0 || paths == null)
                return;
            int n = Math.Min(count, MaxFilesPerDrop);
            var list = new List<string>(n);
            for (int i = 0; i < n; i++)
            {
                if (paths[i] == null)
                    continue;
                string? path = Marshal.PtrToStringUTF8((nint)paths[i]);
                if (!string.IsNullOrWhiteSpace(path))
                    list.Add(path);
            }

            double x = 0, y = 0;
            GLFW.GetCursorPos(window, &x, &y);
            var position = double.IsFinite(x) && double.IsFinite(y) ? new Vector2((float)x, (float)y) : Vector2.Zero;
            if (list.Count > 0)
                _drops.Enqueue(new FileDrop(list, position));
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or OutOfMemoryException)
        {
            // A malformed path list from the OS is ignored; the user can drop again.
        }
    }
}
