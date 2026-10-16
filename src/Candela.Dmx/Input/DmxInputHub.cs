namespace Candela.Dmx.Input;

/// <summary>Who sent a universe: protocol id, sender address and (for sACN) the source CID.</summary>
public readonly record struct DmxSourceId(string Protocol, string Endpoint, Guid Cid = default)
{
    public override string ToString() => Cid == Guid.Empty ? $"{Protocol} {Endpoint}" : $"{Protocol} {Endpoint} ({Cid})";
}

/// <summary>How several sources sending the same universe are combined.</summary>
public enum DmxMergeMode
{
    /// <summary>Highest takes precedence per slot (Art-Net default when two sources send one universe).</summary>
    Htp,

    /// <summary>The most recently updated source wins entirely.</summary>
    Ltp,
}

/// <summary>Live information about one sender of one universe (for the external-control panel).</summary>
public sealed record DmxSourceInfo(DmxSourceId Source, int Universe, int Priority, DateTime LastSeenUtc, long PacketCount, string? Name);

/// <summary>
/// Collects DMX universes from every protocol receiver and merges them. Receivers call <see cref="Submit"/> from their
/// network threads; the engine reads merged universes on the main thread once per frame with <see cref="TryRead"/>.
/// Merge rules: only sources with the highest priority among the non-expired ones count (sACN priority; Art-Net
/// sources use <see cref="DefaultPriority"/>); among those, slots are merged HTP or LTP. A source expires after
/// <see cref="SourceTimeout"/> without packets (sACN uses 2.5 s; Art-Net nodes typically 4 s); a source that announces
/// stream termination is dropped immediately.
/// Universes are 1-based internal numbers (see AppSettings.ArtNetUniverseOffset for the Art-Net mapping).
/// </summary>
public sealed class DmxInputHub
{
    public const int DefaultPriority = 100;
    public const int SlotCount = 512;

    private readonly object _lock = new();
    private readonly Dictionary<int, UniverseState> _universes = new();
    private readonly Func<DateTime> _clock;

    public DmxInputHub(Func<DateTime>? clockUtc = null) => _clock = clockUtc ?? (() => DateTime.UtcNow);

    public TimeSpan SourceTimeout { get; set; } = TimeSpan.FromSeconds(3);
    public DmxMergeMode MergeMode { get; set; } = DmxMergeMode.Htp;

    /// <summary>Raised on the receiving thread when a timecode message arrives (Art-Net ArtTimeCode, MIDI/LTC bridges…).</summary>
    public event Action<Timecode>? TimecodeReceived;

    /// <summary>Last timecode received (null until the first one).</summary>
    public Timecode? LastTimecode { get; private set; }

    /// <summary>
    /// Stores the slots of one universe from one source. <paramref name="slots"/> may be shorter than 512 (missing slots
    /// are zero). Thread-safe; called from network threads.
    /// </summary>
    public void Submit(DmxSourceId source, int universe, ReadOnlySpan<byte> slots, int priority = DefaultPriority, string? sourceName = null)
    {
        if (universe < 1) throw new ArgumentOutOfRangeException(nameof(universe), universe, "Universes are 1-based.");
        var now = _clock();
        lock (_lock)
        {
            if (!_universes.TryGetValue(universe, out var u)) _universes[universe] = u = new UniverseState();
            if (!u.Sources.TryGetValue(source, out var s)) u.Sources[source] = s = new SourceState();
            int n = System.Math.Min(slots.Length, SlotCount);
            slots[..n].CopyTo(s.Slots);
            if (n < SlotCount) Array.Clear(s.Slots, n, SlotCount - n);
            s.Priority = priority;
            s.LastSeen = now;
            s.Name = sourceName ?? s.Name;
            s.Packets++;
            u.Dirty = true;
        }
    }

    /// <summary>A source announced that it stopped sending (sACN Stream_Terminated, Art-Net node going away).</summary>
    public void Terminate(DmxSourceId source, int universe)
    {
        lock (_lock)
        {
            if (_universes.TryGetValue(universe, out var u) && u.Sources.Remove(source)) u.Dirty = true;
        }
    }

    /// <summary>Reports a timecode position (thread-safe).</summary>
    public void SubmitTimecode(Timecode timecode)
    {
        LastTimecode = timecode;
        TimecodeReceived?.Invoke(timecode);
    }

    /// <summary>
    /// Copies the merged slots of <paramref name="universe"/> into <paramref name="destination"/> (512 bytes).
    /// Returns false (and zeroes the destination) when no live source sends it. <paramref name="version"/> changes
    /// whenever the merged content may have changed, so callers can skip re-evaluation.
    /// </summary>
    public bool TryRead(int universe, Span<byte> destination, out long version)
    {
        if (destination.Length < SlotCount) throw new ArgumentException("Destination must hold 512 slots.", nameof(destination));
        lock (_lock)
        {
            if (!_universes.TryGetValue(universe, out var u))
            {
                destination[..SlotCount].Clear();
                version = 0;
                return false;
            }
            ExpireSources(u, _clock());
            if (u.Dirty) Merge(u);
            u.Merged.AsSpan().CopyTo(destination);
            version = u.Version;
            return u.Sources.Count > 0;
        }
    }

    /// <summary>Universes that currently have at least one live source.</summary>
    public IReadOnlyList<int> ActiveUniverses()
    {
        lock (_lock)
        {
            var now = _clock();
            var list = new List<int>();
            foreach (var (n, u) in _universes)
            {
                ExpireSources(u, now);
                if (u.Sources.Count > 0) list.Add(n);
            }
            list.Sort();
            return list;
        }
    }

    /// <summary>Every live source of every universe (for the UI).</summary>
    public IReadOnlyList<DmxSourceInfo> Sources()
    {
        lock (_lock)
        {
            var now = _clock();
            var list = new List<DmxSourceInfo>();
            foreach (var (n, u) in _universes)
            {
                ExpireSources(u, now);
                foreach (var (id, s) in u.Sources) list.Add(new DmxSourceInfo(id, n, s.Priority, s.LastSeen, s.Packets, s.Name));
            }
            return list;
        }
    }

    private void ExpireSources(UniverseState u, DateTime now)
    {
        List<DmxSourceId>? dead = null;
        foreach (var (id, s) in u.Sources)
            if (now - s.LastSeen > SourceTimeout) (dead ??= new()).Add(id);
        if (dead is null) return;
        foreach (var id in dead) u.Sources.Remove(id);
        u.Dirty = true;
    }

    private void Merge(UniverseState u)
    {
        Array.Clear(u.Merged);
        if (u.Sources.Count > 0)
        {
            int top = int.MinValue;
            foreach (var s in u.Sources.Values) top = System.Math.Max(top, s.Priority);
            if (MergeMode == DmxMergeMode.Ltp)
            {
                SourceState? latest = null;
                foreach (var s in u.Sources.Values)
                    if (s.Priority == top && (latest is null || s.LastSeen >= latest.LastSeen)) latest = s;
                latest!.Slots.AsSpan().CopyTo(u.Merged);
            }
            else
            {
                foreach (var s in u.Sources.Values)
                {
                    if (s.Priority != top) continue;
                    for (int i = 0; i < SlotCount; i++)
                        if (s.Slots[i] > u.Merged[i]) u.Merged[i] = s.Slots[i];
                }
            }
        }
        u.Dirty = false;
        u.Version++;
    }

    private sealed class UniverseState
    {
        public readonly Dictionary<DmxSourceId, SourceState> Sources = new();
        public readonly byte[] Merged = new byte[SlotCount];
        public bool Dirty;
        public long Version;
    }

    private sealed class SourceState
    {
        public readonly byte[] Slots = new byte[SlotCount];
        public int Priority = DefaultPriority;
        public DateTime LastSeen;
        public long Packets;
        public string? Name;
    }
}

/// <summary>Frame rate / type of a timecode stream (SMPTE types as used by Art-Net ArtTimeCode).</summary>
public enum TimecodeType
{
    Film24 = 0,
    Ebu25 = 1,
    Df2997 = 2,
    Smpte30 = 3,
}

/// <summary>A SMPTE timecode position.</summary>
public readonly record struct Timecode(int Hours, int Minutes, int Seconds, int Frames, TimecodeType Type)
{
    public double FramesPerSecond => Type switch
    {
        TimecodeType.Film24 => 24,
        TimecodeType.Ebu25 => 25,
        TimecodeType.Df2997 => 30000.0 / 1001.0,
        _ => 30,
    };

    /// <summary>Position in seconds from 00:00:00:00 (drop-frame counted as nominal 30 fps labels).</summary>
    public double TotalSeconds
    {
        get
        {
            double nominal = Type switch { TimecodeType.Film24 => 24, TimecodeType.Ebu25 => 25, _ => 30 };
            long frames = ((Hours * 60L + Minutes) * 60L + Seconds) * (long)nominal + Frames;
            if (Type == TimecodeType.Df2997)
            {
                long totalMinutes = Hours * 60L + Minutes;
                frames -= 2 * (totalMinutes - totalMinutes / 10);
            }
            return frames / FramesPerSecond;
        }
    }

    public override string ToString() => $"{Hours:D2}:{Minutes:D2}:{Seconds:D2}{(Type == TimecodeType.Df2997 ? ';' : ':')}{Frames:D2}";
}
