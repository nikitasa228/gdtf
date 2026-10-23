using System.Diagnostics;
using System.Runtime.InteropServices;
using Candela.App.Diagnostics;

namespace Candela.App.Shell;

/// <summary>Buttons of a native message box.</summary>
internal enum NativeButtons
{
    /// <summary>One OK button.</summary>
    Ok,

    /// <summary>Yes and No.</summary>
    YesNo,
}

/// <summary>Icon of a native message box.</summary>
internal enum NativeIcon
{
    /// <summary>Information.</summary>
    Information,

    /// <summary>Warning.</summary>
    Warning,

    /// <summary>Error.</summary>
    Error,
}

/// <summary>
/// What the operating system itself provides, for the moments the editor's own UI cannot be used: a message box of the
/// OS when the editor cannot start (no Vulkan driver — then the Dear ImGui backends cannot even load, since they link
/// <c>vulkan-1.dll</c>) or stopped because of an error, the console the editor was started from (it is a GUI program on
/// Windows), and opening web pages, files and folders with the user's default programs. Message boxes exist on Windows
/// only; elsewhere <see cref="ShowMessage"/> returns <see langword="null"/> and the caller prints the text instead.
/// Every method is safe to call at any time and never throws.
/// </summary>
internal static partial class NativeShell
{
    private const uint MbOk = 0x0, MbYesNo = 0x4, MbIconError = 0x10, MbIconWarning = 0x30, MbIconInformation = 0x40;
    private const uint MbSetForeground = 0x10000, MbTopMost = 0x40000;
    private const int IdYes = 6;
    private const int AttachParentProcess = -1;

    /// <summary>True when <see cref="ShowMessage"/> can show a box (Windows).</summary>
    public static bool HasMessageBoxes => OperatingSystem.IsWindows();

    /// <summary>
    /// Shows a modal message box of the operating system and returns true for Yes / OK, false for No; null when there is
    /// no message box on this platform or it could not be shown.
    /// </summary>
    public static bool? ShowMessage(string title, string text, NativeButtons buttons, NativeIcon icon)
    {
        if (!OperatingSystem.IsWindows())
            return null;
        uint type = (buttons == NativeButtons.YesNo ? MbYesNo : MbOk) | MbSetForeground | MbTopMost | icon switch
        {
            NativeIcon.Error => MbIconError,
            NativeIcon.Warning => MbIconWarning,
            _ => MbIconInformation,
        };
        try
        {
            int answer = MessageBoxW(0, text, title, type);
            if (answer == 0)
                return null;
            return buttons == NativeButtons.Ok || answer == IdYes;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// Connects the standard streams to the console of the process that started the editor (a GUI program on Windows has
    /// none), so <c>candela-app --help</c> prints in that console. Does nothing elsewhere or without a parent console.
    /// </summary>
    public static void AttachParentConsole()
    {
        if (!OperatingSystem.IsWindows())
            return;
        try
        {
            if (AttachConsole(AttachParentProcess) == 0)
                return;
            // The streams were bound to "no console" when the runtime started; bind them to the attached one.
            var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            var stderr = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
            Console.SetOut(stdout);
            Console.SetError(stderr);
            Console.WriteLine();
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or IOException or UnauthorizedAccessException)
        {
            // Without a console the output is in the log file.
        }
    }

    /// <summary>Opens a web page, a file or a folder with the user's default program; false (logged) when it fails.</summary>
    public static bool Open(string target, AppLog? log = null)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            return true;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException
            or FileNotFoundException or ArgumentException)
        {
            log?.Info("Shell", $"'{target}' could not be opened: {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// Shows a file in the file manager (selected in Explorer on Windows; its folder elsewhere); false when it fails.
    /// </summary>
    public static bool Reveal(string path, AppLog? log = null)
    {
        if (OperatingSystem.IsWindows() && File.Exists(path))
        {
            try
            {
                // Explorer's own syntax: /select,"C:\folder with spaces\file" (Windows paths cannot contain quotes).
                var start = new ProcessStartInfo("explorer.exe", "/select,\"" + Path.GetFullPath(path) + "\"") { UseShellExecute = false };
                using var process = Process.Start(start);
                return true;
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                log?.Info("Shell", $"Explorer could not show '{path}': {e.Message}");
            }
        }

        string? folder = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        return folder is { Length: > 0 } && Open(folder, log);
    }

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBoxW(nint owner, string text, string caption, uint type);

    [LibraryImport("kernel32.dll")]
    private static partial int AttachConsole(int processId);
}
