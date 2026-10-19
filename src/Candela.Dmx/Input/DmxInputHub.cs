namespace Candela.Dmx.Input;

/// <summary>Who sent a universe: protocol id, sender address and (for sACN) the source CID.</summary>
/// <param name="Protocol">Protocol name ("Art-Net", "sACN", …).</param>
/// <param name="Endpoint">Sender address (IP:port, device path …).</param>
/// <param name="Cid">sACN component identifier; empty for protocols without one.</param>
public readonly record struct DmxSourceId(string Protocol, string Endpoint, Guid Cid = default)
{
    /// <inheritdoc />
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

/// <summary>A timecode message with its sender and arrival time (see <see cref="DmxInputHub.SubmitTimecode(Timecode, DmxSourceId)"/>).</summary>
/// <param name="Timecode">The position.</param>
/// <param name="Source">Who sent it (protocol and sender address, e.g. <c>Art-Net 192.168.1.20:6454</c>; the source
/// <c>recording</c> for a recording being played); <see cref="DmxInputHub.UnnamedSource"/> when the submitter did not say.</param>
/// <param name="ReceivedUtc">When it arrived (the hub's clock).</param>
public sealed record TimecodeSample(Timecode Timecode, DmxSourceId Source, DateTime ReceivedUtc);

/// <summary>
/// Follows the hub's receive path (see <see cref="DmxInputHub.AddObserver"/>): told about every change of a merged
/// universe and every timecode as they happen, with the hub's timestamps — a DMX recorder captures at packet rate this
/// way. Calls come from the receiving threads while the hub is locked, one at a time: an observer must return quickly,
/// must not block and must not call back into the hub.
/// </summary>
public interface IDmxInputObserver
{
    /// <summary>
    /// The merged content of <paramref name="universe"/> after a packet, a source termination or a source timeout.
    /// <paramref name="live"/> is false when no source sends it any more (<paramref name="merged"/> is then all zero).
    /// </summary>
    /// <param name="universe">Internal 1-based universe.</param>
    /// <param name="merged">The 512 merged slots (valid during the call only).</param>
    /// <param name="live">True while at least one source sends the universe.</param>
    /// <param name="timestampUtc">When the change happened: the packet's arrival, or the moment a source timed out.</param>
    void OnUniverse(int universe, ReadOnlySpan<byte> merged, bool live, DateTime timestampUtc);

    /// <summary>A timecode arrived.</summary>
    void OnTimecode(TimecodeSample sample);
}

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
    /// <summary>Priority of sources that have none (Art-Net); sACN's default priority.</summary>
    public const int DefaultPriority = 100;

    /// <summary>Slots per universe.</summary>
    public const int SlotCount = 512;

    private readonly object _lock = new();
    private readonly Dictionary<int, UniverseState> _universes = new();
    private readonly Func<DateTime> _clock;
    private IDmxInputObserver[] _observers = Array.Empty<IDmxInputObserver>();
    private TimecodeSample? _lastTimecode;

    /// <summary>Creates the hub; <paramref name="clockUtc"/> replaces the system clock (tests, offline playback).</summary>
    public DmxInputHub(Func<DateTime>? clockUtc = null) => _clock = clockUtc ?? (() => DateTime.UtcNow);

    /// <summary>A source without packets for this long is dropped (default 3 s).</summary>
    public TimeSpan SourceTimeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>How sources of equal priority are combined (default HTP).</summary>
    public DmxMergeMode MergeMode { get; set; } = DmxMergeMode.Htp;

    /// <summary>Raised on the receiving thread when a timecode message arrives (Art-Net ArtTimeCode, MIDI/LTC bridges…).</summary>
    public event Action<Timecode>? TimecodeReceived;

    /// <summary>
    /// Raised on the receiving thread when a timecode message arrives, with its sender and arrival time (see
    /// <see cref="TimecodeReceived"/> for the position only).
    /// </summary>
    public event Action<TimecodeSample>? TimecodeSampleReceived;

    /// <summary>Last timecode received (null until the first one).</summary>
    public Timecode? LastTimecode => Volatile.Read(ref _lastTimecode)?.Timecode;

    /// <summary>The last timecode with its sender and arrival time (null until the first one); read atomically.</summary>
    public TimecodeSample? LastTimecodeSample => Volatile.Read(ref _lastTimecode);

    /// <summary>Who sent the last timecode (null until the first one).</summary>
    public DmxSourceId? LastTimecodeSource => Volatile.Read(ref _lastTimecode)?.Source;

    /// <summary>The hub's clock (UTC): the time base of packet and timecode timestamps.</summary>
    public DateTime UtcNow => _clock();

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
            if (_observers.Length > 0) Notify(universe, u, now);
        }
    }

    /// <summary>A source announced that it stopped sending (sACN Stream_Terminated, Art-Net node going away).</summary>
    public void Terminate(DmxSourceId source, int universe)
    {
        lock (_lock)
        {
            if (!_universes.TryGetValue(universe, out var u) || !u.Sources.Remove(source)) return;
            u.Dirty = true;
            if (_observers.Length > 0) Notify(universe, u, _clock());
        }
    }

    /// <summary>The source of timecodes submitted without one (empty protocol and endpoint).</summary>
    public static readonly DmxSourceId UnnamedSource = new("", "");

    /// <summary>Reports a timecode position from an unnamed source (thread-safe).</summary>
    public void SubmitTimecode(Timecode timecode) => SubmitTimecode(timecode, UnnamedSource);

    /// <summary>
    /// Reports a timecode position and its sender (thread-safe): <see cref="LastTimecodeSample"/> and
    /// <see cref="LastTimecodeSource"/> then tell where it came from (Art-Net node address, a recording …).
    /// </summary>
    public void SubmitTimecode(Timecode timecode, DmxSourceId source)
    {
        var sample = new TimecodeSample(timecode, source, _clock());
        Volatile.Write(ref _lastTimecode, sample);
        if (Volatile.Read(ref _observers).Length > 0)
        {
            lock (_lock)
                foreach (var o in _observers) o.OnTimecode(sample);
        }
        TimecodeReceived?.Invoke(timecode);
        TimecodeSampleReceived?.Invoke(sample);
    }

    /// <summary>
    /// Starts telling <paramref name="observer"/> about every change of a merged universe and every timecode (see
    /// <see cref="IDmxInputObserver"/>). With <paramref name="reportCurrentState"/>, the observer first receives the
    /// current merged content of every live universe (while the hub is locked, so nothing is missed or reported twice).
    /// </summary>
    public void AddObserver(IDmxInputObserver observer, bool reportCurrentState = true)
    {
        ArgumentNullException.ThrowIfNull(observer);
        lock (_lock)
        {
            if (Array.IndexOf(_observers, observer) >= 0) return;
            if (reportCurrentState)
            {
                var now = _clock();
                foreach (var (n, u) in _universes)
                {
                    ExpireSources(u, now, n); // the observers already registered hear about a timeout found here
                    if (u.Sources.Count == 0) continue;
                    if (u.Dirty) Merge(u);
                    observer.OnUniverse(n, u.Merged, live: true, now);
                }
            }
            Volatile.Write(ref _observers, [.. _observers, observer]);
        }
    }

    /// <summary>Stops notifying <paramref name="observer"/>; when this returns, no call to it is running any more.</summary>
    public void RemoveObserver(IDmxInputObserver observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        lock (_lock)
        {
            int i = Array.IndexOf(_observers, observer);
            if (i < 0) return;
            var list = new List<IDmxInputObserver>(_observers);
            list.RemoveAt(i);
            Volatile.Write(ref _observers, list.ToArray());
        }
    }

    /// <summary>Merges <paramref name="u"/> and reports it to the observers (lock held).</summary>
    private void Notify(int universe, UniverseState u, DateTime timestamp)
    {
        if (u.Dirty) Merge(u);
        bool live = u.Sources.Count > 0;
        foreach (var o in _observers) o.OnUniverse(universe, u.Merged, live, timestamp);
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
            ExpireSources(u, _clock(), universe);
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
                ExpireSources(u, now, n);
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
                ExpireSources(u, now, n);
                foreach (var (id, s) in u.Sources) list.Add(new DmxSourceInfo(id, n, s.Priority, s.LastSeen, s.Packets, s.Name));
            }
            return list;
        }
    }

    /// <summary>
    /// Drops the sources of <paramref name="u"/> that timed out; observers are told (at the moment the last of them timed
    /// out) when <paramref name="universe"/> is given.
    /// </summary>
    private void ExpireSources(UniverseState u, DateTime now, int universe = 0)
    {
        List<DmxSourceId>? dead = null;
        DateTime expiredAt = default;
        foreach (var (id, s) in u.Sources)
        {
            if (now - s.LastSeen <= SourceTimeout) continue;
            (dead ??= new()).Add(id);
            var at = s.LastSeen + SourceTimeout;
            if (at > expiredAt) expiredAt = at;
        }
        if (dead is null) return;
        foreach (var id in dead) u.Sources.Remove(id);
        u.Dirty = true;
        if (universe > 0 && _observers.Length > 0) Notify(universe, u, expiredAt);
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
    /// <summary>24 fps (film).</summary>
    Film24 = 0,

    /// <summary>25 fps (EBU).</summary>
    Ebu25 = 1,

    /// <summary>29.97 fps drop-frame.</summary>
    Df2997 = 2,

    /// <summary>30 fps (SMPTE).</summary>
    Smpte30 = 3,
}

/// <summary>A SMPTE timecode position.</summary>
/// <param name="Hours">Hours.</param>
/// <param name="Minutes">Minutes.</param>
/// <param name="Seconds">Seconds.</param>
/// <param name="Frames">Frames within the second.</param>
/// <param name="Type">Frame rate / type.</param>
public readonly record struct Timecode(int Hours, int Minutes, int Seconds, int Frames, TimecodeType Type)
{
    /// <summary>Actual frame rate, frames per second (29.97 for drop-frame).</summary>
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

    /// <inheritdoc />
    public override string ToString() => $"{Hours:D2}:{Minutes:D2}:{Seconds:D2}{(Type == TimecodeType.Df2997 ? ';' : ':')}{Frames:D2}";
}
