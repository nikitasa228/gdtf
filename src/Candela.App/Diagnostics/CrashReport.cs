using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Candela.App.Shell;
using Candela.App.Ui;
using Candela.Core.Settings;

namespace Candela.App.Diagnostics;

/// <summary>
/// Per-user folders of the editor's own diagnostics: the log of each run and the crash reports. On Windows they are in
/// <c>%LOCALAPPDATA%\Candela\logs</c> (machine-local, next to the caches; not roamed with the settings in
/// <c>%APPDATA%\Candela</c>), elsewhere in <c>logs</c> of the config folder (<c>~/.config/Candela/logs</c>). A config
/// folder given with <c>--config-dir</c> or <c>CANDELA_CONFIG_DIR</c> keeps its logs inside it, so a test or portable
/// run leaves nothing behind in the user's folders.
/// </summary>
internal static class AppFolders
{
    /// <summary>File name of the editor's log (the previous run's is <c>candela-app.previous.log</c>).</summary>
    public const string LogFileName = "candela-app.log";

    /// <summary>The folder of the log and the crash reports for this run.</summary>
    public static string LogsDirectory => LogsDirectoryFor(
        Environment.GetEnvironmentVariable("CANDELA_CONFIG_DIR"), OperatingSystem.IsWindows(),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ConfigPaths.ConfigDir);

    /// <summary>The editor's log file of this run.</summary>
    public static string LogFile => Path.Combine(LogsDirectory, LogFileName);

    /// <summary>
    /// The logs folder for a config folder override (<paramref name="configOverride"/>, null when none), the platform
    /// and the per-user folders (tests call it with explicit values).
    /// </summary>
    public static string LogsDirectoryFor(string? configOverride, bool windows, string localAppData, string configDir)
    {
        if (!string.IsNullOrWhiteSpace(configOverride))
            return Path.Combine(configOverride, "logs");
        if (windows && !string.IsNullOrWhiteSpace(localAppData))
            return Path.Combine(localAppData, "Candela", "logs");
        return Path.Combine(configDir, "logs");
    }
}

/// <summary>
/// A crash report: when the editor stops because of an error (an exception that escaped everything, a start-up
/// failure, a frame loop that kept failing) a text file <c>crash-yyyyMMdd-HHmmss.txt</c> is written into
/// <see cref="AppFolders.LogsDirectory"/> with the version, the system, the error and the last lines of the log, and the
/// user is told where it is (a message box on Windows that offers to open the folder; standard error elsewhere), so the
/// file can be attached to a problem report. Writing a report never throws.
/// </summary>
internal static class CrashReport
{
    /// <summary>Lines of the log copied into a report (the most recent ones).</summary>
    public const int LogLines = 300;

    /// <summary>File name prefix of the reports.</summary>
    public const string FilePrefix = "crash-";

    /// <summary>Most reports kept; older ones are deleted when a new one is written.</summary>
    public const int MaxReports = 20;

    /// <summary>The editor's version as shown in About ("0.1.0-preview.1") with the build's commit when known.</summary>
    public static string FullVersion =>
        typeof(CrashReport).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(CrashReport).Assembly.GetName().Version?.ToString() ?? "0.0";

    /// <summary>
    /// The text of a report: what happened, version, system, the exception (with inner exceptions and stack traces)
    /// and the last <see cref="LogLines"/> log entries of <paramref name="log"/>.
    /// </summary>
    public static string Compose(string what, Exception? exception, AppLog? log, DateTime time)
    {
        var text = new StringBuilder(16 * 1024);
        text.AppendLine(CultureInfo.InvariantCulture, $"Candela crash report — {time:yyyy-MM-dd HH:mm:ss}");
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"What:      {what}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Version:   Candela {FullVersion}");
        text.AppendLine(CultureInfo.InvariantCulture, $"System:    {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        text.AppendLine(CultureInfo.InvariantCulture, $"Runtime:   {RuntimeInformation.FrameworkDescription} ({RuntimeInformation.ProcessArchitecture})");
        text.AppendLine(CultureInfo.InvariantCulture, $"Program:   {AppContext.BaseDirectory}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Language:  {Loc.Language}; culture {CultureInfo.CurrentUICulture.Name}");
        if (exception is not null)
        {
            text.AppendLine();
            text.AppendLine("Error:");
            text.AppendLine(exception.ToString());
        }

        if (log is not null)
        {
            var entries = new List<LogEntry>();
            log.CopyTo(entries);
            int first = Math.Max(0, entries.Count - LogLines);
            text.AppendLine();
            text.AppendLine(CultureInfo.InvariantCulture, $"Log (last {entries.Count - first} of {entries.Count} lines; the whole log of the run: {log.FilePath ?? "not written"}):");
            for (int i = first; i < entries.Count; i++)
                text.AppendLine(AppLog.Format(entries[i]));
        }

        return text.ToString();
    }

    /// <summary>
    /// Writes a report into <paramref name="folder"/> (created when missing; the oldest reports beyond
    /// <see cref="MaxReports"/> are removed). Returns its path, or null when it could not be written.
    /// </summary>
    public static string? Write(string folder, string what, Exception? exception, AppLog? log, DateTime? time = null)
    {
        var now = time ?? DateTime.Now;
        try
        {
            Directory.CreateDirectory(folder);
            string name = FilePrefix + now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string path = Path.Combine(folder, name + ".txt");
            for (int i = 2; File.Exists(path) && i < 100; i++)
                path = Path.Combine(folder, string.Create(CultureInfo.InvariantCulture, $"{name}-{i}.txt"));
            File.WriteAllText(path, Compose(what, exception, log, now), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            Prune(folder);
            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException
            or System.Security.SecurityException)
        {
            log?.Warn("Crash", $"The crash report could not be written to '{folder}': {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// Tells the user that the editor stopped (<paramref name="started"/>) or could not start, and where the report is:
    /// a message box on Windows (offering to open the folder) when <paramref name="interactive"/>, always a line on
    /// standard error.
    /// </summary>
    public static void Notify(bool started, string? reportPath, bool interactive, AppLog? log)
    {
        string where = reportPath ?? log?.FilePath ?? AppFolders.LogFile;
        string title = InstallLoc.Get(started ? InstallStr.CrashTitle : InstallStr.StartFailedTitle);
        string text = InstallLoc.Format(started ? InstallStr.CrashText : InstallStr.StartFailedText, where);
        try
        {
            Console.Error.WriteLine($"{title}. {where}");
        }
        catch (IOException)
        {
            // No console attached.
        }

        if (!interactive || !NativeShell.HasMessageBoxes)
            return;
        if (NativeShell.ShowMessage(title, text + "\n\n" + InstallLoc.Get(InstallStr.CrashOpenFolder), NativeButtons.YesNo, NativeIcon.Error) == true)
            NativeShell.Reveal(where, log);
    }

    private static void Prune(string folder)
    {
        var reports = new DirectoryInfo(folder).GetFiles(FilePrefix + "*.txt");
        if (reports.Length <= MaxReports)
            return;
        Array.Sort(reports, (a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
        for (int i = MaxReports; i < reports.Length; i++)
        {
            try
            {
                reports[i].Delete();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // An old report that cannot be deleted stays.
            }
        }
    }
}
