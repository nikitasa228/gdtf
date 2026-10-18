using System.Numerics;
using Hexa.NET.GLFW;

namespace Candela.App.Shell;

/// <summary>The editor's main OS window (GLFW). Secondary windows of torn-off panels are created by the ImGui backend.</summary>
internal sealed unsafe class MainWindow : IDisposable
{
    private GLFWwindowPtr _window;

    private MainWindow(GLFWwindowPtr window)
    {
        _window = window;
    }

    /// <summary>The GLFW window handle.</summary>
    public GLFWwindowPtr Handle => _window;

    /// <summary>The same window as the ImGui GLFW backend's handle type.</summary>
    public Hexa.NET.ImGui.Backends.GLFW.GLFWwindowPtr BackendHandle =>
        new((Hexa.NET.ImGui.Backends.GLFW.GLFWwindow*)_window.Handle);

    /// <summary>True once the native window was destroyed.</summary>
    public bool IsDisposed => _window.Handle == null;

    /// <summary>
    /// Creates the window. <paramref name="forVulkan"/> = no OpenGL context (the Vulkan path); false creates an OpenGL
    /// 3.0 context for the fallback error screen. Returns null when GLFW cannot create the window.
    /// </summary>
    public static MainWindow? Create(string title, int width, int height, bool forVulkan, bool maximized)
    {
        GLFW.DefaultWindowHints();
        if (forVulkan)
        {
            GLFW.WindowHint(GLFW.GLFW_CLIENT_API, GLFW.GLFW_NO_API);
        }
        else
        {
            GLFW.WindowHint(GLFW.GLFW_CONTEXT_VERSION_MAJOR, 3);
            GLFW.WindowHint(GLFW.GLFW_CONTEXT_VERSION_MINOR, 0);
        }

        // Window size in screen coordinates grows with the monitor's content scale (DPI awareness on Windows/X11).
        GLFW.WindowHint(GLFW.GLFW_SCALE_TO_MONITOR, GLFW.GLFW_TRUE);
        GLFW.WindowHint(GLFW.GLFW_MAXIMIZED, maximized ? GLFW.GLFW_TRUE : GLFW.GLFW_FALSE);
        var window = GLFW.CreateWindow(width, height, title, default, default);
        if (window.Handle == null)
            return null;
        GLFW.SetWindowSizeLimits(window, 640, 400, -1, -1);
        return new MainWindow(window);
    }

    /// <summary>Framebuffer size in pixels (0 × 0 while minimised).</summary>
    public (int Width, int Height) FramebufferSize
    {
        get
        {
            int w = 0, h = 0;
            if (!IsDisposed)
                GLFW.GetFramebufferSize(_window, &w, &h);
            return (w, h);
        }
    }

    /// <summary>Content scale of the monitor the window is on (DPI / 96 on Windows, Xft.dpi / 96 on X11; 1 = 100 %).</summary>
    public Vector2 ContentScale
    {
        get
        {
            float x = 1f, y = 1f;
            if (!IsDisposed)
                GLFW.GetWindowContentScale(_window, &x, &y);
            return new Vector2(SanitizeScale(x), SanitizeScale(y));
        }
    }

    /// <summary>True when the window is iconified or has an empty framebuffer.</summary>
    public bool IsMinimized
    {
        get
        {
            if (IsDisposed)
                return true;
            if (GLFW.GetWindowAttrib(_window, GLFW.GLFW_ICONIFIED) != 0)
                return true;
            var (w, h) = FramebufferSize;
            return w <= 0 || h <= 0;
        }
    }

    /// <summary>True once the user asked to close the window (title bar button, Alt+F4, File → Exit).</summary>
    public bool ShouldClose => IsDisposed || GLFW.WindowShouldClose(_window) != 0;

    /// <summary>Requests (or cancels) closing at the end of the frame.</summary>
    public void SetShouldClose(bool close)
    {
        if (!IsDisposed)
            GLFW.SetWindowShouldClose(_window, close ? GLFW.GLFW_TRUE : GLFW.GLFW_FALSE);
    }

    /// <summary>Resizes the client area (screen coordinates; the size limits of the window apply).</summary>
    public void SetSize(int width, int height)
    {
        if (!IsDisposed && width > 0 && height > 0)
            GLFW.SetWindowSize(_window, width, height);
    }

    /// <summary>Minimises the window to the task bar.</summary>
    public void Minimize()
    {
        if (!IsDisposed)
            GLFW.IconifyWindow(_window);
    }

    /// <summary>Restores a minimised or maximised window.</summary>
    public void Restore()
    {
        if (!IsDisposed)
            GLFW.RestoreWindow(_window);
    }

    /// <summary>Maximises the window.</summary>
    public void Maximize()
    {
        if (!IsDisposed)
            GLFW.MaximizeWindow(_window);
    }

    /// <summary>Sets the title bar text.</summary>
    public void SetTitle(string title)
    {
        if (!IsDisposed)
            GLFW.SetWindowTitle(_window, title);
    }

    /// <summary>Destroys the native window.</summary>
    public void Dispose()
    {
        if (IsDisposed)
            return;
        GLFW.DestroyWindow(_window);
        _window = default;
    }

    private static float SanitizeScale(float s) => float.IsFinite(s) && s > 0.25f ? Math.Min(s, 8f) : 1f;
}
