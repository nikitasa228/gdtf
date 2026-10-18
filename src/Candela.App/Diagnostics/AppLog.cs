using System.Globalization;
using System.Text;

namespace Candela.App.Diagnostics;

/// <summary>Severity of an editor log message.</summary>
internal enum LogLevel
{
    /// <summary>Verbose diagnostics (not shown in the console panel by default).</summary>
    Debug,

    /// <summary>Normal progress messages ("Saved 'show.candela'").</summary>
    Info,

    /// <summary>Something went wrong but the editor keeps working normally.</summary>
    Warning,

    /// <summary>An operation failed (exception caught by the frame loop, failed save…).</summary>
    Error,
}

/// <summary>One line of the editor log.</summary>
/// <param name="Time">Local time the message was written.</param>
/// <param name="Level">Severity.</param>
/// <param name="Source">Short area name ("Vulkan", "Session", "UI"…).</param>
/// <param name="Message">The text.</param>
internal readonly record struct LogEntry(DateTime Time, LogLevel Level, string Source, string Message);

/// <summary>
/// The editor's log: a bounded in-memory ring shown by the Console panel, mirrored to standard error (warnings and
/// errors) and, when <see cref="OpenFile"/> was called, to a log file in the user config folder. Thread-safe: protocol
/// receivers and background jobs may log from any thread; the console panel reads a snapshot on the main thread.
/// </summary>
internal sealed class AppLog : IDisposable
{
    private readonly object _gate = new();
    private readonly LogEntry[] _ring;
    private int _start;
    private int _count;
    private long _version;
    private StreamWriter? _file;

    /// <summary>Creates a log keeping the last <paramref name="capacity"/> entries in memory.</summary>
    public AppLog(int capacity = 2000)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 16);
        _ring = new LogEntry[capacity];
    }

    /// <summary>Lowest level written to standard error (default: warnings).</summary>
    public LogLevel ConsoleLevel { get; set; } = LogLevel.Warning;

    /// <summary>Incremented on every message; lets views refresh only when something was added.</summary>
    public long Version => Interlocked.Read(ref _version);

    /// <summary>Number of entries currently held (at most the capacity).</summary>
    public int Count
    {
        get
        {
            lock (_gate)
                return _count;
        }
    }

    /// <summary>Path of the log file, or null when logging to memory only.</summary>
    public string? FilePath { get; private set; }

    /// <summary>
    /// Also appends messages to <paramref name="path"/> (created with its folder). Failure to open the file is not an
    /// error: the editor logs to memory and standard error only.
    /// </summary>
    public void OpenFile(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            lock (_gate)
            {
                _file?.Dispose();
                _file = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
                FilePath = path;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Write(LogLevel.Warning, "Log", $"Cannot write the log file '{path}': {e.Message}");
        }
    }

    /// <summary>Writes a message.</summary>
    public void Write(LogLevel level, string source, string message)
    {
        var entry = new LogEntry(DateTime.Now, level, source, message);
        lock (_gate)
        {
            int index = (_start + _count) % _ring.Length;
            _ring[index] = entry;
            if (_count < _ring.Length)
                _count++;
            else
                _start = (_start + 1) % _ring.Length;
            _version++;
            if (_file is not null)
            {
                try
                {
                    _file.WriteLine(Format(entry));
                }
                catch (IOException)
                {
                    // A full disk must not break the editor; the in-memory log still has the message.
                }
            }
        }

        if (level >= ConsoleLevel)
        {
            try
            {
                Console.Error.WriteLine(Format(entry));
            }
            catch (IOException)
            {
                // Closed standard error (detached GUI process) — ignore.
            }
        }
    }

    /// <summary>Writes an informational message.</summary>
    public void Info(string source, string message) => Write(LogLevel.Info, source, message);

    /// <summary>Writes a warning.</summary>
    public void Warn(string source, string message) => Write(LogLevel.Warning, source, message);

    /// <summary>Writes an error.</summary>
    public void Error(string source, string message) => Write(LogLevel.Error, source, message);

    /// <summary>Writes an error with the exception's type, message and stack trace (stack trace to the file only).</summary>
    public void Error(string source, string message, Exception exception)
    {
        Write(LogLevel.Error, source, $"{message}: {exception.GetType().Name}: {exception.Message}");
        lock (_gate)
        {
            try
            {
                _file?.WriteLine(exception.ToString());
            }
            catch (IOException)
            {
                // see Write
            }
        }
    }

    /// <summary>Copies the entries (oldest first) into <paramref name="destination"/>, which is cleared first.</summary>
    public void CopyTo(List<LogEntry> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.Clear();
        lock (_gate)
        {
            for (int i = 0; i < _count; i++)
                destination.Add(_ring[(_start + i) % _ring.Length]);
        }
    }

    /// <summary>Removes every entry from memory (the file keeps them).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _start = 0;
            _count = 0;
            _version++;
        }
    }

    /// <summary>One-line text form used for the file and standard error.</summary>
    public static string Format(LogEntry entry) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{entry.Time:yyyy-MM-dd HH:mm:ss.fff} {LevelTag(entry.Level)} [{entry.Source}] {entry.Message}");

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_gate)
        {
            _file?.Dispose();
            _file = null;
        }
    }

    private static string LevelTag(LogLevel level) => level switch
    {
        LogLevel.Debug => "DBG",
        LogLevel.Info => "INF",
        LogLevel.Warning => "WRN",
        _ => "ERR",
    };
}
