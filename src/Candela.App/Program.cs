using Candela.App.Diagnostics;
using Candela.App.Shell;
using Candela.Core.Settings;

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
            Console.Error.WriteLine(e.Message);
            Console.Error.WriteLine();
            Console.Error.WriteLine(AppOptions.Usage);
            return ExitCodes.Usage;
        }

        if (options.Help)
        {
            Console.WriteLine(AppOptions.Usage);
            return ExitCodes.Ok;
        }

        if (options.ConfigDirectory is { Length: > 0 } configDir)
            Environment.SetEnvironmentVariable("CANDELA_CONFIG_DIR", Path.GetFullPath(configDir));

        using var log = new AppLog { ConsoleLevel = options.IsScreenshotMode ? LogLevel.Info : LogLevel.Warning };
        log.OpenFile(Path.Combine(ConfigPaths.ConfigDir, "logs", "candela-app.log"));
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
                log.Error("Fatal", "Unhandled exception", ex);
        };

        var app = new EditorApplication(options, log);
        try
        {
            return app.Run();
        }
        catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException))
        {
            // Start-up failures outside the frame loop (the loop itself never lets exceptions out).
            log.Error("Fatal", "The editor stopped", e);
            return ExitCodes.Failure;
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
    }
}
