using Candela.App.Diagnostics;
using Hexa.NET.GLFW;
using StbImageSharp;

namespace Candela.App.Shell;

/// <summary>
/// The Candela icon on the editor's windows (title bar, taskbar, Alt+Tab): the 256 px picture embedded in the assembly
/// (<c>packaging/windows/icon/candela-256.png</c>, the same drawing as the executable's <c>candela.ico</c>), reduced to
/// 16, 32 and 48 px with an area filter so the system finds a sharp image for each place. Windows and X11 only (Wayland
/// and macOS take the icon from the application bundle). Decoded once per process; failures are logged, never thrown.
/// </summary>
internal static unsafe class WindowIcon
{
    /// <summary>Name of the embedded picture.</summary>
    public const string ResourceName = "Icons/candela-256.png";

    /// <summary>Sizes handed to the window, in pixels.</summary>
    public static readonly int[] Sizes = { 16, 32, 48, 256 };

    private static IReadOnlyList<(int Size, byte[] Rgba)>? s_images;

    /// <summary>The icon images (RGBA, row-major, top row first), largest last; empty when the picture is missing.</summary>
    public static IReadOnlyList<(int Size, byte[] Rgba)> Images => s_images ??= Load();

    /// <summary>Sets the icon of <paramref name="window"/>.</summary>
    public static void Apply(MainWindow window, AppLog log)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(log);
        if (window.IsDisposed || !(OperatingSystem.IsWindows() || (OperatingSystem.IsLinux() && IsX11())))
            return;
        var images = Images;
        if (images.Count == 0)
        {
            log.Warn("UI", "The window icon is missing from the program.");
            return;
        }

        var handles = new System.Runtime.InteropServices.GCHandle[images.Count];
        var native = stackalloc GLFWimage[images.Count];
        try
        {
            for (int i = 0; i < images.Count; i++)
            {
                handles[i] = System.Runtime.InteropServices.GCHandle.Alloc(images[i].Rgba, System.Runtime.InteropServices.GCHandleType.Pinned);
                native[i] = new GLFWimage
                {
                    Width = images[i].Size,
                    Height = images[i].Size,
                    Pixels = (byte*)handles[i].AddrOfPinnedObject(),
                };
            }

            // GLFW copies the pixels before it returns.
            GLFW.SetWindowIcon(window.Handle, images.Count, new GLFWimagePtr(native));
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            log.Warn("UI", $"The window icon could not be set: {e.Message}");
        }
        finally
        {
            foreach (var handle in handles)
                if (handle.IsAllocated)
                    handle.Free();
        }
    }

    /// <summary>
    /// Reduces a square RGBA image to <paramref name="size"/> × <paramref name="size"/> by averaging the source pixels each
    /// target pixel covers (alpha-weighted, so transparent edges do not darken).
    /// </summary>
    public static byte[] Downscale(byte[] rgba, int sourceSize, int size)
    {
        ArgumentNullException.ThrowIfNull(rgba);
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);
        if (rgba.Length != sourceSize * sourceSize * 4)
            throw new ArgumentException("The image is not square RGBA of the given size.", nameof(rgba));
        if (size == sourceSize)
            return (byte[])rgba.Clone();
        var result = new byte[size * size * 4];
        double scale = (double)sourceSize / size;
        for (int y = 0; y < size; y++)
        {
            int y0 = (int)(y * scale), y1 = Math.Max(y0 + 1, (int)((y + 1) * scale));
            for (int x = 0; x < size; x++)
            {
                int x0 = (int)(x * scale), x1 = Math.Max(x0 + 1, (int)((x + 1) * scale));
                double r = 0, g = 0, b = 0, a = 0;
                int n = 0;
                for (int sy = y0; sy < y1 && sy < sourceSize; sy++)
                {
                    for (int sx = x0; sx < x1 && sx < sourceSize; sx++)
                    {
                        int i = (sy * sourceSize + sx) * 4;
                        double alpha = rgba[i + 3];
                        r += rgba[i] * alpha;
                        g += rgba[i + 1] * alpha;
                        b += rgba[i + 2] * alpha;
                        a += alpha;
                        n++;
                    }
                }

                int o = (y * size + x) * 4;
                if (a > 0)
                {
                    result[o] = (byte)Math.Clamp(Math.Round(r / a), 0, 255);
                    result[o + 1] = (byte)Math.Clamp(Math.Round(g / a), 0, 255);
                    result[o + 2] = (byte)Math.Clamp(Math.Round(b / a), 0, 255);
                }

                result[o + 3] = (byte)Math.Clamp(Math.Round(a / Math.Max(1, n)), 0, 255);
            }
        }

        return result;
    }

    private static IReadOnlyList<(int Size, byte[] Rgba)> Load()
    {
        try
        {
            using var stream = typeof(WindowIcon).Assembly.GetManifestResourceStream(ResourceName);
            if (stream is null)
                return Array.Empty<(int, byte[])>();
            var image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
            if (image.Width != image.Height || image.Width < 16)
                return Array.Empty<(int, byte[])>();
            return Sizes.Where(s => s <= image.Width).Select(s => (s, Downscale(image.Data, image.Width, s))).ToList();
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or ArgumentException)
        {
            return Array.Empty<(int, byte[])>();
        }
    }

    private static bool IsX11() =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))
        && !string.Equals(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), "wayland", StringComparison.OrdinalIgnoreCase);
}
