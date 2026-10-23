using Candela.App.Diagnostics;
using Candela.App.Shell;

namespace Candela.App;

/// <summary>Entry point of <c>candela-app</c>, the Candela editor.</summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        AppOptions options;
        try
        {
            options = AppOptions.Parse(args);
        }
        catch (AppUsageException e)
        {
            NativeShell.AttachParentConsole();
            Console.Error.WriteLine(e.Message);
            Console.Error.WriteLine();
            Console.Error.WriteLine(AppOptions.Usage);
            return ExitCodes.Usage;
        }

        if (options.Help)
        {
            NativeShell.AttachParentConsole();
            Console.WriteLine(AppOptions.Usage);
            return ExitCodes.Ok;
        }

        // A GUI program on Windows: headless runs print their progress to the console they were started from.
        if (options.IsScreenshotMode)
            NativeShell.AttachParentConsole();
        if (options.ConfigDirectory is { Length: > 0 } configDir)
            Environment.SetEnvironmentVariable("CANDELA_CONFIG_DIR", Path.GetFullPath(configDir));

        // The editor's own model imports (when the import worker is unavailable) from a program folder the ANSI code
        // page cannot spell (a per-user install under C:\Users\Иван\…).
        Candela.Engine.AssimpProgramFolder.Prepare();
        bool interactive = !options.IsScreenshotMode;
        using var log = new AppLog { ConsoleLevel = options.IsScreenshotMode ? LogLevel.Info : LogLevel.Warning };
        log.OpenFile(AppFolders.LogFile);
        log.Info("Startup", $"Candela {CrashReport.FullVersion} in '{AppContext.BaseDirectory}'.");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            // The process ends after this handler: write what is known and tell the user where it is.
            var error = e.ExceptionObject as Exception;
            log.Error("Fatal", "Unhandled exception", error ?? new InvalidOperationException(e.ExceptionObject?.ToString()));
            string? report = CrashReport.Write(AppFolders.LogsDirectory, "An unhandled exception ended the editor.", error, log);
            CrashReport.Notify(started: true, report, interactive, log);
        };

        var app = new EditorApplication(options, log);
        int exitCode;
        bool reported = false;
        try
        {
            exitCode = app.Run();
        }
        catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException))
        {
            // Start-up failures outside the frame loop (the loop itself never lets exceptions out).
            log.Error("Fatal", "The editor stopped", e);
            string? report = CrashReport.Write(AppFolders.LogsDirectory, "The editor stopped because of an exception.", e, log);
            NotifyFailure(app.Started, report, interactive, log);
            reported = true;
            exitCode = ExitCodes.Failure;
        }
        finally
        {
            try
            {
                app.Dispose();
            }
            catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException))
            {
                log.Error("Shutdown", "Cleaning up failed", e);
            }
        }

        if (exitCode == ExitCodes.Failure && !reported)
        {
            // The editor could not start (no display, no window) or its frame loop gave up: a report for the user too.
            string what = app.FailureReason ?? (app.Started ? "The editor stopped." : "The editor could not start.");
            string? report = CrashReport.Write(AppFolders.LogsDirectory, what, null, log);
            NotifyFailure(app.Started, report, interactive, log);
        }

        return exitCode;
    }

    /// <summary>
    /// Tells the user why the editor stopped: a start that failed because the Microsoft Visual C++ runtime is missing
    /// (the window libraries cannot load) names the runtime and its download; everything else gets the crash report.
    /// </summary>
    private static void NotifyFailure(bool started, string? report, bool interactive, AppLog log)
    {
        if (!started && VcRuntime.ReportIfMissing(interactive, log))
            return;
        CrashReport.Notify(started, report, interactive, log);
    }
}
