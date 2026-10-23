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

    /// <summary>
    /// Fits a newly created window to the work area of the primary monitor (where a new window opens): maximised when
    /// it is larger — 1600 × 900 grows to 2400 × 1350 pixels at the 150 % scaling of a typical Full HD laptop, which put
    /// the bottom of the editor off the screen at the first start —, centred when it sticks out, left alone otherwise.
    /// Interactive runs only (screenshots keep their exact size). Never throws.
    /// </summary>
    public void FitToWorkArea()
    {
        if (IsDisposed)
            return;
        try
        {
            var monitor = GLFW.GetPrimaryMonitor();
            if (monitor.Handle == null)
                return;
            int x = 0, y = 0, width = 0, height = 0, left = 0, top = 0, right = 0, bottom = 0;
            int areaX = 0, areaY = 0, areaWidth = 0, areaHeight = 0;
            GLFW.GetWindowPos(_window, &x, &y);
            GLFW.GetWindowSize(_window, &width, &height);
            GLFW.GetWindowFrameSize(_window, &left, &top, &right, &bottom);
            GLFW.GetMonitorWorkarea(monitor, &areaX, &areaY, &areaWidth, &areaHeight);
            var placement = WindowPlacement.Fit(x, y, width, height, new WindowFrame(left, top, right, bottom),
                new WorkArea(areaX, areaY, areaWidth, areaHeight));
            if (placement.Maximize)
                GLFW.MaximizeWindow(_window);
            else if (placement.Move)
                GLFW.SetWindowPos(_window, placement.X, placement.Y);
        }
        catch (EntryPointNotFoundException)
        {
            // An older GLFW without work areas: the window stays as created.
        }
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

/// <summary>The border of a window around its client area, in screen coordinates (title bar on top).</summary>
internal readonly record struct WindowFrame(int Left, int Top, int Right, int Bottom);

/// <summary>The part of a monitor not covered by the task bar, in screen coordinates.</summary>
internal readonly record struct WorkArea(int X, int Y, int Width, int Height);

/// <summary>What <see cref="MainWindow.FitToWorkArea"/> does with a new window.</summary>
/// <param name="Maximize">The window with its frame is larger than the work area: maximise it.</param>
/// <param name="Move">The window fits but sticks out of the work area: move its client area to (<paramref name="X"/>, <paramref name="Y"/>).</param>
/// <param name="X">New left edge of the client area (screen coordinates).</param>
/// <param name="Y">New top edge of the client area (screen coordinates).</param>
internal readonly record struct WindowPlacement(bool Maximize, bool Move, int X, int Y)
{
    /// <summary>
    /// The placement of a window whose client area is at (<paramref name="x"/>, <paramref name="y"/>) with the size
    /// <paramref name="width"/> × <paramref name="height"/> and the border <paramref name="frame"/>, for
    /// <paramref name="area"/>: maximised when it does not fit, centred when it sticks out, unchanged otherwise (also for
    /// an unknown work area).
    /// </summary>
    public static WindowPlacement Fit(int x, int y, int width, int height, WindowFrame frame, WorkArea area)
    {
        if (area.Width <= 0 || area.Height <= 0 || width <= 0 || height <= 0)
            return new WindowPlacement(false, false, x, y);
        int outerWidth = width + Math.Max(0, frame.Left) + Math.Max(0, frame.Right);
        int outerHeight = height + Math.Max(0, frame.Top) + Math.Max(0, frame.Bottom);
        if (outerWidth > area.Width || outerHeight > area.Height)
            return new WindowPlacement(true, false, x, y);
        int outerX = x - Math.Max(0, frame.Left), outerY = y - Math.Max(0, frame.Top);
        bool inside = outerX >= area.X && outerY >= area.Y
            && outerX + outerWidth <= area.X + area.Width && outerY + outerHeight <= area.Y + area.Height;
        if (inside)
            return new WindowPlacement(false, false, x, y);
        return new WindowPlacement(false, true,
            area.X + (area.Width - outerWidth) / 2 + Math.Max(0, frame.Left),
            area.Y + (area.Height - outerHeight) / 2 + Math.Max(0, frame.Top));
    }
}
