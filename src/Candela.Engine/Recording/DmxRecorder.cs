using Candela.Dmx.Input;

namespace Candela.Engine;

/// <summary>
/// Records the live DMX input at packet rate: the recorder follows the <see cref="DmxInputHub"/>'s receive path
/// (<see cref="IDmxInputObserver"/>) and stores every change of a merged universe and every new timecode the moment the
/// hub reports it — not once per engine frame — so a 44 Hz console recorded by a 30 fps editor keeps all of its frames
/// and timecode seeks land between engine frames. Universes that stop (a source terminates or times out) are recorded
/// as lost. The result is a <see cref="DmxRecording"/> (<see cref="DmxCaptureMode.Packets"/>) to save and to play back
/// for offline rendering.
/// </summary>
/// <remarks>
/// <para><b>Timestamps.</b> Recording time is the hub's clock at arrival minus the hub's clock at <see cref="Start(string)"/>
/// (packet precision). <see cref="StageEngine.Tick"/> reports the engine clock through <see cref="Capture"/>; when the
/// engine clock runs ahead of real time (offline ticking, tests, a render job), the engine time of the last frame is the
/// earliest time a packet can get, so the recording stays on the show's timeline. Live, the engine follows real time and
/// the arrival times win. Changes of different universes arriving within <see cref="CoalesceWindow"/> share one recorded
/// frame (a console's burst of universes).</para>
/// <para><b>Threading.</b> <see cref="Start(string)"/>, <see cref="Capture"/> and <see cref="Stop"/> are called on the
/// main thread; the capture itself runs on the protocol receive threads (inside the hub's lock, without blocking).
/// Unchanged packets cost a 512-byte comparison and allocate nothing.</para>
/// </remarks>
public sealed class DmxRecorder
{
    /// <summary>How often <see cref="Capture"/> asks the hub to expire silent sources, s of engine time.</summary>
    public const double UniverseScanInterval = 0.25;

    /// <summary>Changes of different universes closer together than this share one recorded frame, s.</summary>
    public const double CoalesceWindow = 0.001;

    private readonly DmxInputHub _hub;
    private readonly Observer _observer;
    private readonly object _gate = new();
    private readonly Dictionary<int, UniverseTrack> _tracks = new();
    private readonly List<DmxRecordedFrame> _frames = new();
    private List<DmxUniverseChange>? _pendingChanges;
    private double _pendingTime;
    private Timecode? _pendingTimecode;
    private bool _pendingTimecodeReceived;
    private bool _hasPending;
    private Timecode? _lastTimecode;
    private DateTime _startUtc;
    private double _startEngine;
    private double _engineFloor;
    private double _nextScan;
    private double _lastTime;
    private string _name = "Recording";
    private volatile bool _recording;

    /// <summary>Creates a recorder reading <paramref name="hub"/>.</summary>
    public DmxRecorder(DmxInputHub hub)
    {
        _hub = hub ?? throw new ArgumentNullException(nameof(hub));
        _observer = new Observer(this);
    }

    /// <summary>True between <see cref="Start(string)"/> and <see cref="Stop"/>.</summary>
    public bool IsRecording => _recording;

    /// <summary>Frames recorded so far.</summary>
    public int FrameCount
    {
        get
        {
            lock (_gate) return _frames.Count + (_hasPending ? 1 : 0);
        }
    }

    /// <summary>Recording time of the last frame, s.</summary>
    public double Duration
    {
        get
        {
            lock (_gate) return _hasPending ? _pendingTime : _frames.Count > 0 ? _frames[^1].Time : 0;
        }
    }

    /// <summary>
    /// Starts a new recording now (the hub's clock); the universes being received are stored in full as the first
    /// frame. A recording in progress is discarded.
    /// </summary>
    public void Start(string name = "Recording") => Start(0, name);

    /// <summary>
    /// Starts a new recording now (see <see cref="Start(string)"/>); <paramref name="engineTime"/> is the engine clock at
    /// the start, against which later <see cref="Capture"/> calls are measured.
    /// </summary>
    public void Start(double engineTime, string name = "Recording")
    {
        _hub.RemoveObserver(_observer);
        lock (_gate)
        {
            _tracks.Clear();
            _frames.Clear();
            _pendingChanges = null;
            _hasPending = false;
            _lastTimecode = null;
            _name = string.IsNullOrWhiteSpace(name) ? "Recording" : name;
            _startUtc = _hub.UtcNow;
            _startEngine = double.IsFinite(engineTime) ? engineTime : 0;
            _engineFloor = 0;
            _nextScan = UniverseScanInterval;
            _lastTime = 0;
            _recording = true;
        }
        // registered outside the recorder's lock: the hub calls back with its own lock held (lock order hub → recorder)
        _hub.AddObserver(_observer, reportCurrentState: true);
    }

    /// <summary>
    /// Reports the engine clock (called by <see cref="StageEngine.Tick"/>; no-op when not recording): packets arriving
    /// from now on are not stamped earlier than this frame, and silent sources are expired every
    /// <see cref="UniverseScanInterval"/> so lost universes are recorded even when nothing reads them.
    /// </summary>
    public void Capture(double engineTime)
    {
        if (!_recording || !double.IsFinite(engineTime)) return;
        double t;
        lock (_gate) t = Math.Max(0, engineTime - _startEngine);
        if (t >= _nextScan)
        {
            // before the floor moves: a source that timed out since the last scan is stamped when it timed out
            _nextScan = t + UniverseScanInterval;
            _ = _hub.ActiveUniverses();
        }
        lock (_gate)
            if (t > _engineFloor) _engineFloor = t;
    }

    /// <summary>Ends the recording and returns it (null when nothing was being recorded).</summary>
    public DmxRecording? Stop()
    {
        if (!_recording) return null;
        _hub.RemoveObserver(_observer); // no capture runs after this returns
        lock (_gate)
        {
            _recording = false;
            FlushPending();
            var recording = new DmxRecording(_name, _frames.ToList(), _startUtc) { Capture = DmxCaptureMode.Packets };
            _frames.Clear();
            _tracks.Clear();
            return recording;
        }
    }

    /// <summary>Recording time of an event the hub stamped <paramref name="timestampUtc"/> (lock held).</summary>
    private double TimeOf(DateTime timestampUtc)
    {
        double t = Math.Max((timestampUtc - _startUtc).TotalSeconds, _engineFloor);
        if (!double.IsFinite(t)) t = _engineFloor;
        // monotonic even when the system clock steps back
        if (t < _lastTime) t = _lastTime;
        _lastTime = t;
        return t;
    }

    private void OnUniverse(int universe, ReadOnlySpan<byte> merged, bool live, DateTime timestampUtc)
    {
        lock (_gate)
        {
            if (!_recording) return;
            if (!_tracks.TryGetValue(universe, out var track))
            {
                if (!live) return;
                _tracks[universe] = track = new UniverseTrack();
            }
            DmxUniverseChange change;
            if (!live)
            {
                if (!track.Live) return;
                track.Live = false;
                change = new DmxUniverseChange(universe, DmxUniverseChangeKind.Lost, Array.Empty<DmxSlotRun>());
            }
            else if (!track.Live)
            {
                track.Live = true;
                merged[..DmxInputHub.SlotCount].CopyTo(track.Slots);
                change = new DmxUniverseChange(universe, DmxUniverseChangeKind.Full, new[] { new DmxSlotRun(0, track.Slots.ToArray()) });
            }
            else
            {
                if (merged[..DmxInputHub.SlotCount].SequenceEqual(track.Slots)) return;
                var runs = DmxRecording.Diff(track.Slots, merged);
                merged[..DmxInputHub.SlotCount].CopyTo(track.Slots);
                change = new DmxUniverseChange(universe, DmxUniverseChangeKind.Delta, runs);
            }
            double t = TimeOf(timestampUtc);
            if (!_hasPending || t - _pendingTime > CoalesceWindow || ContainsUniverse(_pendingChanges, universe)) BeginFrame(t);
            (_pendingChanges ??= new List<DmxUniverseChange>(4)).Add(change);
        }
    }

    private void OnTimecode(TimecodeSample sample)
    {
        lock (_gate)
        {
            if (!_recording || sample.Timecode == _lastTimecode) return;
            _lastTimecode = sample.Timecode;
            double t = TimeOf(sample.ReceivedUtc);
            if (!_hasPending || t - _pendingTime > CoalesceWindow || _pendingTimecodeReceived) BeginFrame(t);
            _pendingTimecode = sample.Timecode;
            _pendingTimecodeReceived = true;
        }
    }

    /// <summary>Closes the frame being collected and opens one at <paramref name="time"/> (lock held).</summary>
    private void BeginFrame(double time)
    {
        FlushPending();
        _hasPending = true;
        _pendingTime = time;
        _pendingChanges = null;
        _pendingTimecode = _lastTimecode;
        _pendingTimecodeReceived = false;
    }

    private void FlushPending()
    {
        if (!_hasPending) return;
        _hasPending = false;
        _frames.Add(new DmxRecordedFrame(_pendingTime, _pendingTimecode,
            (IReadOnlyList<DmxUniverseChange>?)_pendingChanges ?? Array.Empty<DmxUniverseChange>())
        {
            TimecodeReceived = _pendingTimecodeReceived,
        });
        _pendingChanges = null;
    }

    private static bool ContainsUniverse(List<DmxUniverseChange>? changes, int universe)
    {
        if (changes is null) return false;
        foreach (var c in changes)
            if (c.Universe == universe) return true;
        return false;
    }

    private sealed class UniverseTrack
    {
        public readonly byte[] Slots = new byte[DmxInputHub.SlotCount];
        public bool Live;
    }

    /// <summary>The hub's view of the recorder (kept private so the capture entry points are not public API).</summary>
    private sealed class Observer(DmxRecorder recorder) : IDmxInputObserver
    {
        public void OnUniverse(int universe, ReadOnlySpan<byte> merged, bool live, DateTime timestampUtc) =>
            recorder.OnUniverse(universe, merged, live, timestampUtc);

        public void OnTimecode(TimecodeSample sample) => recorder.OnTimecode(sample);
    }
}

/// <summary>
/// Plays a <see cref="DmxRecording"/> into a <see cref="DmxInputHub"/> as the source <c>recording</c>, following the
/// engine clock (<see cref="StageEngine.Play"/>). Seeking (by time or by timecode) rebuilds the universe state from the
/// recording's nearest keyframe. Universes are re-sent every frame so they never time out in the hub; the timecode of
/// the current frame is forwarded too.
/// </summary>
public sealed class DmxPlayer : IDisposable
{
    /// <summary>Protocol name of the hub source.</summary>
    public const string SourceProtocol = "recording";

    private readonly DmxInputHub _hub;
    private readonly Dictionary<int, byte[]> _state = new();
    private readonly List<int> _removed = new();
    private readonly HashSet<int> _sent = new();
    private readonly DmxSourceId _source;
    private double _offset;
    private int _next;
    private double _position = double.NegativeInfinity;
    private Timecode? _timecode;
    private bool _disposed;

    /// <summary>
    /// Creates a player: recording time = engine time − <paramref name="offset"/> (so an offset equal to the current
    /// engine time starts the recording now).
    /// </summary>
    public DmxPlayer(DmxRecording recording, DmxInputHub hub, double offset = 0)
    {
        Recording = recording ?? throw new ArgumentNullException(nameof(recording));
        _hub = hub ?? throw new ArgumentNullException(nameof(hub));
        _offset = double.IsFinite(offset) ? offset : 0;
        _source = new DmxSourceId(SourceProtocol, recording.Name);
    }

    /// <summary>The recording being played.</summary>
    public DmxRecording Recording { get; }

    /// <summary>Current recording time, s.</summary>
    public double Position => double.IsFinite(_position) ? _position : 0;

    /// <summary>True once the position passed the last frame.</summary>
    public bool IsAtEnd => _next >= Recording.Frames.Count && Recording.Frames.Count > 0;

    /// <summary>
    /// Moves to engine time <paramref name="engineTime"/>: applies the frames up to the corresponding recording time (or
    /// seeks backwards) and sends every live universe to the hub.
    /// </summary>
    public void PlayTo(double engineTime)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!double.IsFinite(engineTime)) return;
        double t = engineTime - _offset;
        if (t < _position) Rebuild(t);
        var frames = Recording.Frames;
        while (_next < frames.Count && frames[_next].Time <= t + DmxRecording.TimeTolerance)
        {
            DmxRecording.Apply(frames[_next], _state);
            if (frames[_next].Timecode is { } tc) _timecode = tc;
            _next++;
        }
        _position = t;
        Send();
    }

    /// <summary>Makes recording time <paramref name="recordingTime"/> correspond to engine time <paramref name="engineTime"/> and plays there.</summary>
    public void Seek(double recordingTime, double engineTime)
    {
        if (!double.IsFinite(recordingTime) || !double.IsFinite(engineTime)) return;
        _offset = engineTime - recordingTime;
        Rebuild(recordingTime);
        PlayTo(engineTime);
    }

    /// <summary>
    /// Seeks to the moment <paramref name="timecode"/> was received; false (nothing changes) when the recording carries no
    /// such timecode.
    /// </summary>
    public bool SeekTimecode(Timecode timecode, double engineTime)
    {
        if (Recording.FindTimecode(timecode) is not { } t) return false;
        Seek(t, engineTime);
        return true;
    }

    /// <summary>Withdraws the recording's universes from the hub.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (int u in _sent) _hub.Terminate(_source, u);
        _sent.Clear();
    }

    private void Rebuild(double t)
    {
        Recording.StateAt(t, _state);
        _next = Recording.FirstFrameAfter(t);
        _timecode = null;
        for (int i = _next - 1; i >= 0; i--)
        {
            if (Recording.Frames[i].Timecode is { } tc)
            {
                _timecode = tc;
                break;
            }
        }
        _position = t;
    }

    private void Send()
    {
        foreach (var (u, slots) in _state)
        {
            _hub.Submit(_source, u, slots, DmxInputHub.DefaultPriority, Recording.Name);
            _sent.Add(u);
        }
        _removed.Clear();
        foreach (int u in _sent)
            if (!_state.ContainsKey(u)) _removed.Add(u);
        foreach (int u in _removed)
        {
            _hub.Terminate(_source, u);
            _sent.Remove(u);
        }
        if (_timecode is { } tc && tc != _hub.LastTimecode) _hub.SubmitTimecode(tc, _source);
    }
}
