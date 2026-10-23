using System.Runtime.InteropServices;
using Candela.App.Diagnostics;
using Candela.App.Ui;

namespace Candela.App.Shell;

/// <summary>
/// The Microsoft Visual C++ 2015-2022 runtime the native user interface libraries need on Windows (GLFW, cimgui,
/// cimguizmo and the Dear ImGui backends import <c>vcruntime140.dll</c> and <c>vcruntime140_1.dll</c>; see
/// <c>packaging/windows/winpack.py check</c>). Setup installs it when it is missing; when that did not happen — the user
/// declined the administrator prompt, the runtime's setup failed, the program folder was copied by hand — the editor
/// cannot load its window libraries, and instead of a generic "could not start" report the user is told exactly what to
/// install. Never throws.
/// </summary>
internal static class VcRuntime
{
    /// <summary>Microsoft's permanent download link of the latest x64 redistributable.</summary>
    public const string DownloadUrl = "https://aka.ms/vs/17/release/vc_redist.x64.exe";

    /// <summary>The runtime libraries the native UI libraries import.</summary>
    public static readonly string[] Libraries = { "vcruntime140.dll", "vcruntime140_1.dll" };

    /// <summary>
    /// The libraries of <see cref="Libraries"/> that <paramref name="canLoad"/> cannot load (default: the system's
    /// loader on Windows; nothing is missing elsewhere).
    /// </summary>
    public static IReadOnlyList<string> Missing(Func<string, bool>? canLoad = null)
    {
        if (canLoad is null && !OperatingSystem.IsWindows())
            return Array.Empty<string>();
        canLoad ??= TryLoad;
        var missing = new List<string>();
        foreach (var name in Libraries)
        {
            bool present;
            try
            {
                present = canLoad(name);
            }
            catch (Exception e) when (e is BadImageFormatException or DllNotFoundException or ArgumentException)
            {
                present = false;
            }

            if (!present)
                missing.Add(name);
        }

        return missing;
    }

    /// <summary>The message for <paramref name="missing"/> in the UI language (the log file is named for support).</summary>
    public static string Message(IReadOnlyList<string> missing, string logFile)
    {
        ArgumentNullException.ThrowIfNull(missing);
        return InstallLoc.Format(InstallStr.VcRuntimeText, string.Join(", ", missing), DownloadUrl, logFile);
    }

    /// <summary>
    /// When the editor could not start and the runtime is missing: logs it and, in an interactive run on Windows, shows
    /// the message box (Yes opens the download). True when the runtime is missing (the caller then shows no generic
    /// report).
    /// </summary>
    public static bool ReportIfMissing(bool interactive, AppLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        var missing = Missing();
        if (missing.Count == 0)
            return false;
        string message = Message(missing, log.FilePath ?? AppFolders.LogFile);
        log.Error("Startup", $"The Microsoft Visual C++ runtime is missing ({string.Join(", ", missing)}); install {DownloadUrl}.");
        try
        {
            Console.Error.WriteLine(message);
        }
        catch (IOException)
        {
            // No console attached.
        }

        if (interactive && NativeShell.HasMessageBoxes
            && NativeShell.ShowMessage(InstallLoc.Get(InstallStr.VcRuntimeTitle), message + "\n\n" + InstallLoc.Get(InstallStr.VcRuntimeOpenPage),
                NativeButtons.YesNo, NativeIcon.Error) == true)
            NativeShell.Open(DownloadUrl, log);
        return true;
    }

    private static bool TryLoad(string name)
    {
        if (!NativeLibrary.TryLoad(name, out nint handle))
            return false;
        NativeLibrary.Free(handle);
        return true;
    }
}
