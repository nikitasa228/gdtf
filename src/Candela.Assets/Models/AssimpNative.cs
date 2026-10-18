using System.Reflection;
using System.Runtime.InteropServices;
using Assimp.Unmanaged;

namespace Candela.Assets;

/// <summary>
/// Loads the native Assimp library shipped with the AssimpNet package (runtimes/{linux-x64, win-x64, win-x86,
/// osx-x64}/native). Two problems of the managed binding are handled here:
/// <list type="bullet">
/// <item>On Linux AssimpNet calls <c>dlopen</c> through <c>[DllImport("libdl.so")]</c>. glibc 2.34+ distributions ship
/// only <c>libdl.so.2</c> (the unversioned name belongs to the -dev package), so the binding fails with
/// <see cref="DllNotFoundException"/>. A DllImport resolver maps the name to <c>libdl.so.2</c> or <c>libc.so.6</c>.</item>
/// <item>The library is looked up in <c>runtimes/{rid}/native</c> and flat next to the application, next to this
/// assembly and next to AssimpNet.dll, so it is found both in portable builds (RID folders) and in RID-specific
/// publishes (flattened), and when Candela.Assets is loaded by a host whose base directory differs (test runners).</item>
/// </list>
/// The first call does the work; later calls return the cached outcome. Thread-safe.
/// </summary>
public static class AssimpNative
{
    private static readonly object s_gate = new();
    private static bool s_attempted;
    private static string? s_error;

    /// <summary>True when the native library is loaded (attempts the load on first use).</summary>
    public static bool IsAvailable => EnsureLoaded() is null;

    /// <summary>
    /// Loads the native library if needed. Returns null on success, otherwise a user-facing description of why the
    /// native library is unavailable (the same message on every call).
    /// </summary>
    public static string? EnsureLoaded()
    {
        lock (s_gate)
        {
            if (s_attempted) return s_error;
            s_attempted = true;
            try
            {
                InstallResolver();
                var library = AssimpLibrary.Instance;
                if (!library.IsLibraryLoaded)
                {
                    var probing = ProbingPaths();
                    if (probing.Length > 0) library.Resolver.SetProbingPaths(probing);
                    library.ThrowOnLoadFailure = true;
                    library.LoadLibrary();
                }
                s_error = library.IsLibraryLoaded ? null : "The Assimp native library could not be loaded.";
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                s_error = $"The 3D import library (Assimp) is not available on this system ({RuntimeInformation.RuntimeIdentifier}): "
                    + e.GetBaseException().Message;
            }
            return s_error;
        }
    }

    /// <summary>Path of the loaded native library, or null when it is not loaded.</summary>
    public static string? LibraryPath => IsAvailable ? AssimpLibrary.Instance.LibraryPath : null;

    private static void InstallResolver()
    {
        if (!OperatingSystem.IsLinux()) return;
        try
        {
            NativeLibrary.SetDllImportResolver(typeof(AssimpLibrary).Assembly, Resolve);
        }
        catch (InvalidOperationException)
        {
            // Another component already installed a resolver for AssimpNet; trust it.
        }
    }

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(name, "libdl.so", StringComparison.Ordinal)) return IntPtr.Zero;
        foreach (var candidate in new[] { "libdl.so.2", "libc.so.6", "libdl.so" })
        {
            if (NativeLibrary.TryLoad(candidate, out var handle)) return handle;
        }
        return IntPtr.Zero;
    }

    private static string[] ProbingPaths()
    {
        string rid = RuntimeId();
        var paths = new List<string>();
        void Add(string? dir)
        {
            if (string.IsNullOrEmpty(dir)) return;
            foreach (var p in new[] { Path.Combine(dir, "runtimes", rid, "native"), dir })
                if (Directory.Exists(p) && !paths.Contains(p, StringComparer.Ordinal)) paths.Add(p);
        }
        Add(AppContext.BaseDirectory);
        Add(Path.GetDirectoryName(typeof(AssimpNative).Assembly.Location));
        Add(Path.GetDirectoryName(typeof(AssimpLibrary).Assembly.Location));
        return paths.ToArray();
    }

    private static string RuntimeId()
    {
        string os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        string arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X86 => "x86",
            Architecture.Arm64 => "arm64",
            Architecture.Arm => "arm",
            _ => "x64",
        };
        return os + "-" + arch;
    }
}
