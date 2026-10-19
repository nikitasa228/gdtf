namespace Candela.App.Files;

/// <summary>
/// Whether folders exist, checked in the background and remembered for a while: the settings window marks missing
/// library folders every frame, and <c>Directory.Exists</c> on an unreachable network share can block for seconds — on
/// the UI thread that would freeze the editor for as long as the window is open. Each folder is checked at most once
/// per <see cref="MaxAgeSeconds"/>, off the UI thread; until the first answer arrives its state is unknown.
/// </summary>
internal sealed class FolderStatusCache
{
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Func<string, bool> _exists;

    /// <summary>Creates a cache; <paramref name="exists"/> defaults to <see cref="Directory.Exists(string)"/>.</summary>
    public FolderStatusCache(Func<string, bool>? exists = null, double maxAgeSeconds = 2.0)
    {
        _exists = exists ?? Directory.Exists;
        MaxAgeSeconds = maxAgeSeconds;
    }

    /// <summary>How long an answer is used before the folder is checked again, s.</summary>
    public double MaxAgeSeconds { get; }

    /// <summary>
    /// The last known state of <paramref name="folder"/> at time <paramref name="now"/> (s): true = exists, false =
    /// missing or unreachable, null = not known yet. Never blocks; starts a background check when the answer is old.
    /// </summary>
    public bool? Exists(string folder, double now)
    {
        ArgumentNullException.ThrowIfNull(folder);
        if (!_entries.TryGetValue(folder, out var entry))
        {
            entry = new Entry();
            _entries[folder] = entry;
        }

        if (!entry.Pending && (!entry.Started || now - entry.CheckedAt >= MaxAgeSeconds))
        {
            entry.Started = true;
            entry.Pending = true;
            entry.CheckedAt = now;
            _ = Task.Run(() =>
            {
                bool result;
                try
                {
                    result = _exists(folder);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    result = false;
                }

                entry.Result = result ? 1 : 0;
                entry.Pending = false;
            });
        }

        int known = entry.Result;
        return known < 0 ? null : known == 1;
    }

    /// <summary>Forgets folders no longer in <paramref name="folders"/> (after one was removed from the list).</summary>
    public void Retain(IReadOnlyCollection<string> folders)
    {
        ArgumentNullException.ThrowIfNull(folders);
        if (_entries.Count <= folders.Count)
            return;
        foreach (var key in _entries.Keys.Where(k => !folders.Contains(k)).ToList())
            _entries.Remove(key);
    }

    private sealed class Entry
    {
        private volatile int _result = -1;
        private volatile bool _pending;

        public bool Started { get; set; }

        public double CheckedAt { get; set; }

        public int Result
        {
            get => _result;
            set => _result = value;
        }

        public bool Pending
        {
            get => _pending;
            set => _pending = value;
        }
    }
}
