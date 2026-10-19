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
/// errors) and, when <see cref="OpenFile"/> was called, to a log file in the user config folder. The file holds the
/// current run (the previous run is kept next to it as <c>*.previous.log</c>, e.g. after a crash) and stops growing at
/// <see cref="MaxFileBytes"/>, so an error repeated every frame cannot fill the disk. Thread-safe: protocol
/// receivers and background jobs may log from any thread; the console panel reads a snapshot on the main thread.
/// </summary>
internal sealed class AppLog : IDisposable
{
    /// <summary>Largest log file written in one run (bytes, approximately; UTF-16 characters are counted).</summary>
    public const long DefaultMaxFileBytes = 16L * 1024 * 1024;

    private readonly object _gate = new();
    private readonly LogEntry[] _ring;
    private int _start;
    private int _count;
    private long _version;
    private StreamWriter? _file;
    private long _fileChars;
    private bool _fileFull;

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

    /// <summary>Size limit of the log file of this run (bytes, approximately).</summary>
    public long MaxFileBytes { get; init; } = DefaultMaxFileBytes;

    /// <summary>The file that keeps the previous run's log next to <paramref name="path"/> (<c>name.previous.log</c>).</summary>
    public static string PreviousPathOf(string path) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, Path.GetFileNameWithoutExtension(path) + ".previous" + Path.GetExtension(path));

    /// <summary>
    /// Also writes messages to <paramref name="path"/> (created with its folder). An existing file — the previous run —
    /// is renamed to <see cref="PreviousPathOf"/> first; when that fails (another editor is still writing it) the new
    /// messages are appended instead. Failure to open the file is not an error: the editor logs to memory and standard
    /// error only.
    /// </summary>
    public void OpenFile(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            try
            {
                if (File.Exists(path))
                    File.Move(path, PreviousPathOf(path), overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // In use by another editor instance: append to it.
            }

            var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            lock (_gate)
            {
                _file?.Dispose();
                _file = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
                _fileChars = stream.Length;
                _fileFull = false;
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
            WriteFileLine(Format(entry));
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
            WriteFileLine(exception.ToString());
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

    /// <summary>Appends a line to the file while it is below its size limit (caller holds the lock).</summary>
    private void WriteFileLine(string line)
    {
        if (_file is null || _fileFull)
            return;
        try
        {
            if (_fileChars + line.Length > MaxFileBytes)
            {
                _fileFull = true;
                _file.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} WRN [Log] The log file reached {MaxFileBytes / (1024 * 1024)} MB; further messages are kept in memory only."));
                return;
            }

            _file.WriteLine(line);
            _fileChars += line.Length + Environment.NewLine.Length;
        }
        catch (IOException)
        {
            // A full disk must not break the editor; the in-memory log still has the message.
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
