using System.Diagnostics;
using System.Runtime.InteropServices;
using Candela.App.Diagnostics;
using Hexa.NET.GLFW;

namespace Candela.App.Shell;

/// <summary>
/// Process-wide GLFW state: initialisation, the error callback, the Vulkan instance extensions GLFW needs and window
/// surfaces. Hexa.NET.GLFW does not bind the Vulkan entry points of GLFW (<c>glfwCreateWindowSurface</c>), so they are
/// resolved from the very GLFW library instance the bindings (and the Dear ImGui GLFW backend) already loaded — loading
/// a second copy would give it separate global state. Main thread only.
/// </summary>
internal static unsafe class GlfwPlatform
{
    private static delegate* unmanaged[Cdecl]<nint, nint, nint, ulong*, int> s_createWindowSurface;
    private static GLFWerrorfun? s_errorCallback;
    private static AppLog? s_log;

    /// <summary>True between a successful <see cref="Initialize"/> and <see cref="Terminate"/>.</summary>
    public static bool IsInitialized { get; private set; }

    /// <summary>Path of the GLFW library in use (for diagnostics).</summary>
    public static string? LibraryPath { get; private set; }

    /// <summary>Initialises GLFW. Returns false (with a reason) when no display is available.</summary>
    public static bool Initialize(AppLog log, out string error)
    {
        ArgumentNullException.ThrowIfNull(log);
        error = "";
        if (IsInitialized)
            return true;
        s_log = log;
        try
        {
            s_errorCallback = OnGlfwError;
            GLFW.SetErrorCallback(s_errorCallback);
            if (GLFW.Init() == 0)
            {
                error = "GLFW could not be initialised (no display? On Linux set DISPLAY or run under Xvfb).";
                return false;
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            error = $"The GLFW library could not be loaded: {e.Message}";
            return false;
        }

        IsInitialized = true;
        ResolveVulkanEntryPoints(log);
        return true;
    }

    /// <summary>Whether GLFW found a Vulkan loader (<c>glfwVulkanSupported</c>).</summary>
    public static bool VulkanSupported => IsInitialized && GLFW.VulkanSupported() != 0;

    /// <summary>Instance extensions needed to present to GLFW windows (includes <c>VK_KHR_surface</c>).</summary>
    public static IReadOnlyList<string> RequiredVulkanExtensions()
    {
        if (!IsInitialized)
            return Array.Empty<string>();
        uint count = 0;
        byte** names = GLFW.GetRequiredInstanceExtensions(&count);
        if (names == null || count == 0)
            return Array.Empty<string>();
        var result = new string[count];
        for (int i = 0; i < count; i++)
            result[i] = Marshal.PtrToStringUTF8((nint)names[i]) ?? "";
        return result;
    }

    /// <summary>
    /// Creates a <c>VkSurfaceKHR</c> for <paramref name="window"/> on <paramref name="instance"/> (a <c>VkInstance</c>
    /// handle). Returns 0 and logs when GLFW cannot create one.
    /// </summary>
    public static ulong CreateSurface(nint instance, GLFWwindowPtr window)
    {
        if (s_createWindowSurface == null)
        {
            s_log?.Error("GLFW", "glfwCreateWindowSurface is not available in the loaded GLFW library.");
            return 0;
        }

        ulong surface = 0;
        int result = s_createWindowSurface(instance, (nint)window.Handle, 0, &surface);
        if (result != 0)
        {
            s_log?.Error("GLFW", $"glfwCreateWindowSurface failed (VkResult {result}).");
            return 0;
        }

        return surface;
    }

    /// <summary>Terminates GLFW (all windows must have been destroyed).</summary>
    public static void Terminate()
    {
        if (!IsInitialized)
            return;
        GLFW.Terminate();
        IsInitialized = false;
        s_createWindowSurface = null;
    }

    private static void ResolveVulkanEntryPoints(AppLog log)
    {
        foreach (var path in CandidateLibraryPaths())
        {
            if (!NativeLibrary.TryLoad(path, out nint handle))
                continue;
            if (NativeLibrary.TryGetExport(handle, "glfwCreateWindowSurface", out nint fn))
            {
                s_createWindowSurface = (delegate* unmanaged[Cdecl]<nint, nint, nint, ulong*, int>)fn;
                LibraryPath = path;
                log.Write(LogLevel.Debug, "GLFW", $"Using {path}");
                return;
            }
        }

        log.Warn("GLFW", "glfwCreateWindowSurface was not found; Vulkan presentation is unavailable.");
    }

    /// <summary>
    /// The GLFW library the bindings loaded comes first (found among the process's modules, so the same instance is
    /// used), then the copies shipped next to the program.
    /// </summary>
    private static IEnumerable<string> CandidateLibraryPaths()
    {
        var loaded = new List<string>();
        try
        {
            foreach (ProcessModule module in Process.GetCurrentProcess().Modules)
            {
                string name = Path.GetFileName(module.FileName ?? "");
                if (IsGlfwLibraryName(name))
                    loaded.Add(module.FileName!);
            }
        }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            // Module enumeration is not available everywhere; fall back to the shipped copies.
        }

        foreach (var p in loaded)
            yield return p;

        string baseDir = AppContext.BaseDirectory;
        string rid = RuntimeInformation.RuntimeIdentifier;
        foreach (var name in new[] { "libglfw.so", "libglfw.so.3", "glfw3.dll", "glfw.dll", "libglfw.3.dylib", "libglfw.dylib" })
        {
            foreach (var dir in new[] { Path.Combine(baseDir, "runtimes", rid, "native"), PortableNativeFolder(baseDir), baseDir })
            {
                string candidate = Path.Combine(dir, name);
                if (File.Exists(candidate))
                    yield return candidate;
            }
        }
    }

    private static string PortableNativeFolder(string baseDir)
    {
        string os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        string arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            _ => "x64",
        };
        return Path.Combine(baseDir, "runtimes", $"{os}-{arch}", "native");
    }

    private static bool IsGlfwLibraryName(string fileName) =>
        fileName.StartsWith("libglfw", StringComparison.OrdinalIgnoreCase) ||
        fileName.Equals("glfw3.dll", StringComparison.OrdinalIgnoreCase) ||
        fileName.Equals("glfw.dll", StringComparison.OrdinalIgnoreCase);

    private static void OnGlfwError(int code, byte* description)
    {
        string text = description == null ? "" : Marshal.PtrToStringUTF8((nint)description) ?? "";
        s_log?.Warn("GLFW", $"error 0x{code:X5}: {text}");
    }
}
