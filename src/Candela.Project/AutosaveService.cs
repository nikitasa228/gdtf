using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Candela.Core.Diagnostics;
using Candela.Core.Serialization;
using Candela.Core.Settings;
using Candela.Project.Internal;

namespace Candela.Project;

/// <summary>Options of an <see cref="AutosaveService"/>.</summary>
public sealed class AutosaveOptions
{
    /// <summary>Shared default options.</summary>
    public static AutosaveOptions Default { get; } = new();

    /// <summary>Folder of the autosave files (default <see cref="AutosaveService.DefaultDirectory"/>).</summary>
    public string? Directory { get; init; }

    /// <summary>Time between autosaves (default 5 minutes; the app setting <c>AutosaveMinutes</c>).</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Number of autosave files kept per session, newest first (default 3, at least 1).</summary>
    public int KeepCount { get; init; } = 3;

    /// <summary>Compression of autosaves (default <see cref="CompressionLevel.Fastest"/>: autosaves must be cheap).</summary>
    public CompressionLevel CompressionLevel { get; init; } = CompressionLevel.Fastest;

    /// <summary>Clock for the interval and the recorded times (tests inject a manual clock).</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>
    /// The recovered session this service continues (<see cref="RecoverableSession.Load"/> was used to open the project):
    /// its autosave files become this session's, are rotated by new autosaves and deleted by
    /// <see cref="AutosaveService.CompleteSession"/>. Its folder is used instead of <see cref="Directory"/>. Without it a
    /// new session never writes to, rotates or deletes autosave files that an ended session left behind, so crash data
    /// stays recoverable until the user recovers or discards it (<see cref="RecoverableSession.Delete"/>).
    /// </summary>
    public RecoverableSession? Resume { get; init; }
}

/// <summary>A finished autosave (reported through <see cref="AutosaveService.Saved"/> on the main thread).</summary>
/// <param name="Path">The autosave file that was written.</param>
/// <param name="SavedUtc">When it was written.</param>
/// <param name="BytesWritten">Size of the file in bytes.</param>
/// <param name="Duration">Time the background write took.</param>
/// <param name="Issues">Problems of the save (for example GDTF files that could not be stored).</param>
public sealed record AutosaveResult(string Path, DateTime SavedUtc, long BytesWritten, TimeSpan Duration, IReadOnlyList<Issue> Issues);

/// <summary>
/// Periodic autosave of the open project to <c>{CacheDir}/autosave/{project id}.candela</c> and crash recovery.
/// <para>
/// <b>Threading.</b> The service has no timer of its own: the main loop calls <see cref="Update"/> (cheap; it compares
/// one timestamp and allocates nothing while no save is due). When the interval has elapsed, <see cref="Update"/> calls
/// the snapshot callback on the main thread — the only step that touches the live scene — and writes the captured
/// <see cref="ProjectSnapshot"/> on a background task. Results are handed back through <see cref="Saved"/> and
/// <see cref="Failed"/>, raised by a later <see cref="Update"/> on the main thread.
/// </para>
/// <para>
/// <b>Files.</b> The newest autosave is <c>{key}.candela</c>, older ones <c>{key}.1.candela</c> …
/// (<see cref="AutosaveOptions.KeepCount"/> in total); <c>{key}.session.json</c> records the scene name and the file the
/// project belongs to; <c>{key}.lock</c> is held open while the session runs, so another running instance never offers a
/// live session for recovery. The key is the project id; <c>{id}~2</c>, … are used when that project is already open in
/// another instance, or when an ended session of it left autosave files that were neither recovered nor discarded — a new
/// session never overwrites, rotates away or deletes another session's crash data. A session opened with
/// <see cref="AutosaveOptions.Resume"/> continues the recovered session's files instead. Every file is written atomically.
/// </para>
/// <para>
/// <b>Lifecycle.</b> On start-up call <see cref="FindRecoverable"/> to list sessions that did not end cleanly; after opening
/// one with <see cref="RecoverableSession.Load"/>, start the new service with <see cref="AutosaveOptions.Resume"/> so it
/// carries on with (and finally cleans up) those files. On a clean exit (or when the project is closed normally) call
/// <see cref="CompleteSession"/>, which deletes the session's files.
/// <see cref="Dispose"/> only stops the service and keeps the files, so an exception unwinding through a <c>using</c>
/// never destroys the recovery data it exists for.
/// </para>
/// </summary>
public sealed class AutosaveService : IDisposable
{
    private const string SessionSuffix = ".session.json";
    private const string LockSuffix = ".lock";
    private const int MaxInstances = 256;

    private readonly Func<bool, ProjectSnapshot?> _capture;
    private readonly AutosaveOptions _options;
    private readonly TimeProvider _clock;
    private readonly ConcurrentQueue<object> _completed = new();
    private readonly FileStream _lock;
    private readonly ChangeTracker? _tracker;
    private readonly int _keep;
    private readonly DateTime _startedUtc;
    private Task _pending = Task.CompletedTask;
    private long _lastCaptureTimestamp;
    private TimeSpan _interval;
    private bool _disposed;

    /// <summary>
    /// Starts an autosave session for <paramref name="projectId"/>. <paramref name="capture"/> is called on the main thread
    /// (from <see cref="Update"/> or <see cref="SaveNowAsync"/>) and returns the snapshot to write, or null when there is
    /// nothing new to save.
    /// </summary>
    /// <exception cref="IOException">The autosave folder cannot be created or locked.</exception>
    public AutosaveService(Guid projectId, Func<ProjectSnapshot?> capture, AutosaveOptions? options = null)
        : this(projectId, WrapCapture(capture), options, null)
    {
    }

    private AutosaveService(Guid projectId, Func<bool, ProjectSnapshot?> capture, AutosaveOptions? options, ChangeTracker? tracker)
    {
        _capture = capture;
        _options = options ?? AutosaveOptions.Default;
        _clock = _options.TimeProvider;
        _keep = System.Math.Clamp(_options.KeepCount, 1, 100);
        Interval = _options.Interval;
        ProjectId = projectId;
        var resume = _options.Resume;
        if (resume is not null && resume.ProjectId != Guid.Empty && resume.ProjectId != projectId)
            throw new ArgumentException($"The recovered session belongs to project {resume.ProjectId:D}, not {projectId:D}.", nameof(options));
        Directory = Path.GetFullPath(resume?.Directory ?? _options.Directory ?? DefaultDirectory);
        System.IO.Directory.CreateDirectory(Directory);
        (SessionKey, _lock) = AcquireSession(Directory, projectId, resume?.SessionKey);
        _tracker = tracker;
        _startedUtc = _clock.GetUtcNow().UtcDateTime;
        _lastCaptureTimestamp = _clock.GetTimestamp();
    }

    /// <summary>Default autosave folder: <c>{ConfigPaths.CacheDir}/autosave</c>.</summary>
    public static string DefaultDirectory => Path.Combine(ConfigPaths.CacheDir, "autosave");

    /// <summary>The project this session autosaves.</summary>
    public Guid ProjectId { get; }

    /// <summary>Full path of the autosave folder.</summary>
    public string Directory { get; }

    /// <summary>Base name of this session's files (the project id, or <c>{id}~n</c> when the project is open twice).</summary>
    public string SessionKey { get; }

    /// <summary>Path of the newest autosave file of this session (it exists once the first autosave finished).</summary>
    public string FilePath => FileFor(Directory, SessionKey, 0);

    /// <summary>Time between autosaves; must be positive (use <see cref="Enabled"/> to switch autosave off).</summary>
    public TimeSpan Interval
    {
        get => _interval;
        set
        {
            if (value <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(value), "The autosave interval must be positive.");
            _interval = value;
        }
    }

    /// <summary>When false, <see cref="Update"/> does not start autosaves (explicit <see cref="SaveNowAsync"/> still works).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>True while an autosave is being written in the background.</summary>
    public bool IsWriting => !_pending.IsCompleted;

    /// <summary>When the last autosave of this session finished (UTC), or null before the first one.</summary>
    public DateTime? LastSavedUtc { get; private set; }

    /// <summary>The error of the last failed autosave, cleared by the next successful one.</summary>
    public Exception? LastError { get; private set; }

    /// <summary>Raised on the main thread (by <see cref="Update"/>) after an autosave was written.</summary>
    public event Action<AutosaveResult>? Saved;

    /// <summary>Raised on the main thread (by <see cref="Update"/>) after an autosave failed; the project itself is unaffected.</summary>
    public event Action<Exception>? Failed;

    /// <summary>
    /// An autosave service that captures <paramref name="project"/> whenever its scene, settings or assets changed since
    /// the last capture (nothing is written for an unchanged project). Call <see cref="ProjectSnapshot.Capture"/> semantics
    /// apply: <paramref name="saveOptions"/> supplies the GDTF provider, which is then called on the background thread.
    /// </summary>
    /// <param name="project">The open project (main thread).</param>
    /// <param name="saveOptions">Options of the captured snapshots.</param>
    /// <param name="options">Autosave options.</param>
    /// <param name="startDirty">Treat the project as changed from the start (a new, never saved project with content).</param>
    public static AutosaveService ForProject(ProjectData project, ProjectSaveOptions? saveOptions = null, AutosaveOptions? options = null,
        bool startDirty = false)
    {
        ArgumentNullException.ThrowIfNull(project);
        var tracker = new ChangeTracker(project, startDirty);
        try
        {
            return new AutosaveService(project.Id,
                force => tracker.TakeChanges(force) ? ProjectSnapshot.Capture(project, saveOptions) : null,
                options, tracker);
        }
        catch
        {
            tracker.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Call from the main loop (every frame is fine). Raises <see cref="Saved"/> / <see cref="Failed"/> for finished writes,
    /// and when <see cref="Interval"/> has elapsed and no write is running, captures a snapshot and starts writing it.
    /// Returns true when an autosave was started.
    /// </summary>
    public bool Update()
    {
        if (_disposed) return false;
        DrainCompleted();
        if (!Enabled || !_pending.IsCompleted) return false;
        long now = _clock.GetTimestamp();
        if (_clock.GetElapsedTime(_lastCaptureTimestamp, now) < _interval) return false;
        _lastCaptureTimestamp = now;
        return Start(force: false) is not null;
    }

    /// <summary>
    /// Captures a snapshot now (main thread) and writes it after any running write; the interval restarts. Returns the
    /// write task (completed immediately when the callback had nothing to save). The task never faults: failures are
    /// reported through <see cref="Failed"/> and <see cref="LastError"/>.
    /// </summary>
    public Task SaveNowAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        DrainCompleted();
        _lastCaptureTimestamp = _clock.GetTimestamp();
        return Start(force: true) ?? Task.CompletedTask;
    }

    /// <summary>A task that completes when the running write (if any) has finished. Never faults.</summary>
    public Task FlushAsync() => _pending;

    /// <summary>Existing autosave files of this session, newest first.</summary>
    public IReadOnlyList<string> Files => ListFiles(Directory, SessionKey);

    /// <summary>
    /// Ends the session cleanly: waits for a running write, deletes this session's autosave files and releases the lock.
    /// Call on a clean exit or when the project is closed normally (after it was saved or the user discarded changes).
    /// </summary>
    public void CompleteSession()
    {
        if (_disposed) return;
        Stop();
        DeleteSessionFiles(Directory, SessionKey);
        ReleaseLock(_lock);
    }

    /// <summary>
    /// Stops the service, waits for a running write and releases the lock. The autosave files are kept: the session can be
    /// recovered later (use <see cref="CompleteSession"/> on a clean exit).
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        ReleaseLock(_lock);
    }

    /// <summary>
    /// Sessions in <paramref name="directory"/> (default <see cref="DefaultDirectory"/>) that left autosave files and are
    /// not running, newest first. Call on start-up and offer them for recovery. Leftover temporary files of ended sessions
    /// are deleted.
    /// </summary>
    public static IReadOnlyList<RecoverableSession> FindRecoverable(string? directory = null)
    {
        var dir = Path.GetFullPath(directory ?? DefaultDirectory);
        if (!System.IO.Directory.Exists(dir)) return Array.Empty<RecoverableSession>();
        string[] names;
        try
        {
            names = System.IO.Directory.GetFiles(dir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<RecoverableSession>();
        }

        var keys = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var path in names)
            if (ParseAutosaveName(Path.GetFileName(path)) is { } parsed) keys.Add(parsed.Key);

        var sessions = new List<RecoverableSession>();
        foreach (var key in keys)
        {
            if (IsLive(dir, key)) continue;
            var files = ListFiles(dir, key);
            if (files.Count == 0) continue;
            DeleteStaleTemporaries(dir, key);
            var info = ReadSessionInfo(dir, key);
            Guid projectId = info?.ProjectId is { } pid && pid != Guid.Empty ? pid : ParseProjectId(key);
            DateTime saved = info?.LastSavedUtc is { } t && t != default ? t : SafeLastWrite(files[0]);
            sessions.Add(new RecoverableSession(dir, key, projectId, info?.SceneName, info?.OriginalPath, info?.AppVersion, saved, files));
        }
        sessions.Sort((a, b) => b.LastSavedUtc.CompareTo(a.LastSavedUtc));
        return sessions;
    }

    // ----- writing --------------------------------------------------------------------------------------------------

    private Task? Start(bool force)
    {
        ProjectSnapshot? snapshot;
        try
        {
            snapshot = _capture(force);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            ReportFailure(e);
            return null;
        }
        if (snapshot is null) return null;
        var previous = _pending;
        _pending = previous.ContinueWith(_ => Write(snapshot), CancellationToken.None, TaskContinuationOptions.DenyChildAttach, TaskScheduler.Default);
        return _pending;
    }

    /// <summary>Runs on a thread-pool thread; never throws (results go to the queue drained on the main thread).</summary>
    private void Write(ProjectSnapshot snapshot)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            var path = FilePath;
            var result = ProjectFile.Save(path, snapshot, _options.CompressionLevel, Rotate);
            DeleteBeyondKeep();
            WriteSessionInfo(snapshot, result.Manifest.ModifiedUtc);
            _completed.Enqueue(new AutosaveResult(path, result.SavedUtc, result.BytesWritten, watch.Elapsed, result.Issues));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _completed.Enqueue(e);
        }
    }

    /// <summary>Moves <c>{key}.candela</c> → <c>{key}.1.candela</c> → … just before the new file takes its place.</summary>
    private void Rotate()
    {
        for (int k = _keep - 1; k >= 1; k--)
        {
            var source = FileFor(Directory, SessionKey, k - 1);
            if (!File.Exists(source)) continue;
            try
            {
                File.Move(source, FileFor(Directory, SessionKey, k), overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Keeping fewer old copies is acceptable; the new autosave still replaces the newest one.
            }
        }
    }

    private void DeleteBeyondKeep()
    {
        foreach (var path in ListFiles(Directory, SessionKey))
        {
            var parsed = ParseAutosaveName(Path.GetFileName(path));
            if (parsed is { } p && p.Index >= _keep) AtomicFile.TryDelete(path);
        }
    }

    private void WriteSessionInfo(ProjectSnapshot snapshot, DateTime savedUtc)
    {
        var info = new SessionInfo
        {
            ProjectId = snapshot.ProjectId,
            SessionKey = SessionKey,
            SceneName = snapshot.SceneName,
            OriginalPath = snapshot.FilePath,
            AppVersion = snapshot.Options.AppVersion ?? ProjectFile.DefaultAppVersion,
            StartedUtc = _startedUtc,
            LastSavedUtc = savedUtc,
            ProcessId = Environment.ProcessId,
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(info, CandelaJson.Options);
        AtomicFile.Write(Path.Combine(Directory, SessionKey + SessionSuffix), s => s.Write(bytes));
    }

    private void DrainCompleted()
    {
        while (_completed.TryDequeue(out var item))
        {
            if (item is AutosaveResult result)
            {
                LastSavedUtc = result.SavedUtc;
                LastError = null;
                Saved?.Invoke(result);
            }
            else if (item is Exception e)
            {
                ReportFailure(e);
            }
        }
    }

    private void ReportFailure(Exception e)
    {
        LastError = e;
        _tracker?.MarkChanged();
        Failed?.Invoke(e);
    }

    private void Stop()
    {
        _disposed = true;
        try
        {
            _pending.Wait();
        }
        catch (AggregateException)
        {
            // Write never throws; a continuation fault would only come from the runtime and must not block shutdown.
        }
        while (_completed.TryDequeue(out var item))
            if (item is AutosaveResult r) LastSavedUtc = r.SavedUtc;
        _tracker?.Dispose();
    }

    private static Func<bool, ProjectSnapshot?> WrapCapture(Func<ProjectSnapshot?> capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        return _ => capture();
    }

    // ----- files and sessions ---------------------------------------------------------------------------------------

    /// <summary>
    /// Takes the lock of a session key: <paramref name="resumeKey"/> when given and not running elsewhere, otherwise the
    /// first of <c>{id}</c>, <c>{id}~2</c>, … that no running session holds and that has no autosave files left by an
    /// ended session (those belong to a crash the user has not recovered or discarded yet and must not be overwritten).
    /// </summary>
    private static (string Key, FileStream Lock) AcquireSession(string directory, Guid projectId, string? resumeKey)
    {
        if (!string.IsNullOrEmpty(resumeKey))
        {
            try
            {
                return (resumeKey, OpenLock(Path.Combine(directory, resumeKey + LockSuffix), FileMode.OpenOrCreate));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The recovered session is running in another instance after all; start a session of our own.
            }
        }

        Exception? last = null;
        for (int n = 1; n <= MaxInstances; n++)
        {
            var key = n == 1 ? projectId.ToString("D") : string.Create(CultureInfo.InvariantCulture, $"{projectId:D}~{n}");
            FileStream stream;
            try
            {
                stream = OpenLock(Path.Combine(directory, key + LockSuffix), FileMode.OpenOrCreate);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                last = e;
                continue;
            }
            if (ListFiles(directory, key).Count == 0) return (key, stream);
            ReleaseLock(stream);
            last = new IOException($"The autosave files of session '{key}' have not been recovered or discarded yet.");
        }
        throw new IOException($"No autosave session could be started for project {projectId:D}: {last?.Message}", last);
    }

    /// <summary>
    /// Opens a lock file exclusively (<see cref="FileShare.None"/>: a sharing lock on Windows, <c>flock</c> on Unix). On
    /// Windows the file disappears when the handle closes; on Unix <see cref="ReleaseLock"/> unlinks it while the lock is
    /// still held, so another instance can never lose a lock file it has just taken.
    /// </summary>
    private static FileStream OpenLock(string path, FileMode mode) =>
        new(path, mode, FileAccess.ReadWrite, FileShare.None, 1, OperatingSystem.IsWindows() ? FileOptions.DeleteOnClose : FileOptions.None);

    private static void ReleaseLock(FileStream stream)
    {
        if (!OperatingSystem.IsWindows()) AtomicFile.TryDelete(stream.Name);
        stream.Dispose();
    }

    /// <summary>True while another service (in this or another process) holds the session's lock file open.</summary>
    private static bool IsLive(string directory, string key)
    {
        var path = Path.Combine(directory, key + LockSuffix);
        if (!File.Exists(path)) return false;
        try
        {
            // The lock of an ended session is free: take it and remove the stale file.
            ReleaseLock(OpenLock(path, FileMode.Open));
            return false;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    internal static string FileFor(string directory, string key, int index) =>
        Path.Combine(directory, index == 0 ? key + ProjectFile.Extension : string.Create(CultureInfo.InvariantCulture, $"{key}.{index}{ProjectFile.Extension}"));

    /// <summary>Autosave files of a session ordered newest first (index 0, 1, …).</summary>
    internal static IReadOnlyList<string> ListFiles(string directory, string key)
    {
        var found = new List<(int Index, string Path)>();
        string[] candidates;
        try
        {
            candidates = System.IO.Directory.GetFiles(directory, key + "*" + ProjectFile.Extension);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
        foreach (var path in candidates)
        {
            if (ParseAutosaveName(Path.GetFileName(path)) is { } parsed && parsed.Key == key) found.Add((parsed.Index, path));
        }
        found.Sort((a, b) => a.Index.CompareTo(b.Index));
        return found.Select(f => f.Path).ToArray();
    }

    internal static void DeleteSessionFiles(string directory, string key)
    {
        foreach (var path in ListFiles(directory, key)) AtomicFile.TryDelete(path);
        AtomicFile.TryDelete(Path.Combine(directory, key + SessionSuffix));
        DeleteStaleTemporaries(directory, key);
    }

    /// <summary>Hidden temporary files (<c>.{key}.candela.{random}.tmp</c>) left by a write that was interrupted.</summary>
    private static void DeleteStaleTemporaries(string directory, string key)
    {
        try
        {
            foreach (var path in System.IO.Directory.GetFiles(directory, "." + key + ".*.tmp"))
                AtomicFile.TryDelete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Leftover temporaries are harmless.
        }
    }

    /// <summary>"{key}.candela" → (key, 0); "{key}.{n}.candela" → (key, n); anything else → null.</summary>
    internal static (string Key, int Index)? ParseAutosaveName(string fileName)
    {
        if (fileName.StartsWith('.') || !fileName.EndsWith(ProjectFile.Extension, StringComparison.Ordinal)) return null;
        var stem = fileName[..^ProjectFile.Extension.Length];
        if (stem.Length == 0) return null;
        int dot = stem.LastIndexOf('.');
        if (dot > 0 && int.TryParse(stem.AsSpan(dot + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int index) && index > 0)
            return (stem[..dot], index);
        return (stem, 0);
    }

    private static Guid ParseProjectId(string key)
    {
        int tilde = key.IndexOf('~');
        return Guid.TryParse(tilde > 0 ? key[..tilde] : key, out var id) ? id : Guid.Empty;
    }

    private static SessionInfo? ReadSessionInfo(string directory, string key)
    {
        try
        {
            var path = Path.Combine(directory, key + SessionSuffix);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<SessionInfo>(File.ReadAllBytes(path), CandelaJson.Options);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    private static DateTime SafeLastWrite(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return default;
        }
    }

    /// <summary><c>{key}.session.json</c>.</summary>
    internal sealed class SessionInfo
    {
        public Guid ProjectId { get; set; }

        public string SessionKey { get; set; } = "";

        public string? SceneName { get; set; }

        public string? OriginalPath { get; set; }

        public string? AppVersion { get; set; }

        public DateTime StartedUtc { get; set; }

        public DateTime LastSavedUtc { get; set; }

        public int ProcessId { get; set; }
    }

    /// <summary>Tracks changes of a project (scene, settings, assets) between captures.</summary>
    private sealed class ChangeTracker : IDisposable
    {
        private readonly ProjectData _project;
        private int _changed;

        public ChangeTracker(ProjectData project, bool startChanged)
        {
            _project = project;
            _changed = startChanged ? 1 : 0;
            project.Scene.Changed += OnSceneChanged;
            project.Settings.Changed += OnSettingsChanged;
            project.Assets.AssetChanged += OnAssetChanged;
        }

        /// <summary>True (and resets the flag) when something changed since the last call, or when forced.</summary>
        public bool TakeChanges(bool force) => Interlocked.Exchange(ref _changed, 0) == 1 || force;

        public void MarkChanged() => Volatile.Write(ref _changed, 1);

        public void Dispose()
        {
            _project.Scene.Changed -= OnSceneChanged;
            _project.Settings.Changed -= OnSettingsChanged;
            _project.Assets.AssetChanged -= OnAssetChanged;
        }

        private void OnSceneChanged(Core.SceneGraph.SceneChange change) => MarkChanged();

        private void OnSettingsChanged(string? section) => MarkChanged();

        private void OnAssetChanged(Guid id) => MarkChanged();
    }
}

/// <summary>
/// Autosave files of a session that did not end cleanly (<see cref="AutosaveService.FindRecoverable"/>). Offer it to the
/// user: <see cref="Load"/> opens the newest readable autosave, <see cref="Delete"/> discards the session.
/// </summary>
public sealed class RecoverableSession
{
    internal RecoverableSession(string directory, string key, Guid projectId, string? sceneName, string? originalPath, string? appVersion,
        DateTime lastSavedUtc, IReadOnlyList<string> files)
    {
        Directory = directory;
        SessionKey = key;
        ProjectId = projectId;
        SceneName = sceneName;
        OriginalPath = originalPath;
        AppVersion = appVersion;
        LastSavedUtc = lastSavedUtc;
        Files = files;
    }

    /// <summary>The autosave folder.</summary>
    public string Directory { get; }

    /// <summary>Base name of the session's files.</summary>
    public string SessionKey { get; }

    /// <summary>The autosaved project's id (empty when it could not be determined).</summary>
    public Guid ProjectId { get; }

    /// <summary>Scene name at the last autosave, when recorded.</summary>
    public string? SceneName { get; }

    /// <summary>The project file the session was working on, or null for a project that was never saved.</summary>
    public string? OriginalPath { get; }

    /// <summary>Version of the application that wrote the autosave, when recorded.</summary>
    public string? AppVersion { get; }

    /// <summary>When the newest autosave was written (UTC).</summary>
    public DateTime LastSavedUtc { get; }

    /// <summary>Autosave files, newest first.</summary>
    public IReadOnlyList<string> Files { get; }

    /// <summary>
    /// Opens the newest autosave that can be read; when it is damaged an older one is used and a
    /// <see cref="ProjectIssueCodes.RecoveredOlder"/> issue says so. <see cref="ProjectData.FilePath"/> is set to
    /// <see cref="OriginalPath"/> (null for a never-saved project), so "Save" writes back to the user's file. May run on a
    /// background thread.
    /// </summary>
    /// <exception cref="ProjectFormatException">No autosave of the session can be read (the first error is reported).</exception>
    public ProjectData Load(ProjectLoadOptions? options = null)
    {
        Exception? first = null;
        for (int i = 0; i < Files.Count; i++)
        {
            try
            {
                var project = ProjectFile.Load(Files[i], options);
                project.FilePath = OriginalPath;
                if (i > 0)
                {
                    var issues = new List<Issue>(project.LoadIssues.Count + 1)
                    {
                        new(IssueSeverity.Warning, IssueCategory.Program, ProjectIssueCodes.RecoveredOlder,
                            $"The newest autosave could not be opened ({first?.Message}); an older autosave was recovered ({Path.GetFileName(Files[i])})."),
                    };
                    issues.AddRange(project.LoadIssues);
                    project.LoadIssues = issues;
                }
                return project;
            }
            catch (Exception e) when (e is ProjectFormatException or IOException or UnauthorizedAccessException)
            {
                first ??= e;
            }
        }
        throw new ProjectFormatException($"The autosaved session '{SceneName ?? SessionKey}' cannot be recovered: {first?.Message}", first);
    }

    /// <summary>Deletes the session's autosave files (the user discarded the recovery, or recovered and saved it).</summary>
    public void Delete() => AutosaveService.DeleteSessionFiles(Directory, SessionKey);
}
