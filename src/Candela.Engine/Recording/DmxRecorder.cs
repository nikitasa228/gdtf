using Candela.Dmx.Input;

namespace Candela.Engine;

/// <summary>
/// Records the live DMX input: once per engine frame (<see cref="Capture"/>, called by <see cref="StageEngine.Tick"/>)
/// every universe the <see cref="DmxInputHub"/> receives is read, and the slots that changed since the previous frame
/// are stored with the frame time and the last timecode received (ArtTimeCode …). Universes that stop arriving are
/// recorded as lost. The result is a <see cref="DmxRecording"/> to save and to play back for offline rendering.
/// </summary>
/// <remarks>
/// The input is sampled at the engine's frame rate; DMX frames arriving faster than that (44 Hz consoles in a 30 fps
/// editor) are recorded at the frame times, which is what the renderer would have shown. Main thread only.
/// </remarks>
public sealed class DmxRecorder
{
    /// <summary>How often the set of received universes is re-read from the hub, s of recording time.</summary>
    public const double UniverseScanInterval = 0.25;

    private readonly DmxInputHub _hub;
    private readonly Dictionary<int, UniverseTrack> _tracks = new();
    private readonly List<DmxRecordedFrame> _frames = new();
    private readonly byte[] _buffer = new byte[DmxInputHub.SlotCount];
    private readonly List<int> _lost = new();
    private IReadOnlyList<int> _universes = Array.Empty<int>();
    private double _start;
    private double _nextScan;
    private Timecode? _lastTimecode;
    private string _name = "Recording";
    private DateTime _startedUtc;

    /// <summary>Creates a recorder reading <paramref name="hub"/>.</summary>
    public DmxRecorder(DmxInputHub hub) => _hub = hub ?? throw new ArgumentNullException(nameof(hub));

    /// <summary>True between <see cref="Start"/> and <see cref="Stop"/>.</summary>
    public bool IsRecording { get; private set; }

    /// <summary>Frames recorded so far.</summary>
    public int FrameCount => _frames.Count;

    /// <summary>Recording time of the last frame, s.</summary>
    public double Duration => _frames.Count > 0 ? _frames[^1].Time : 0;

    /// <summary>
    /// Starts a new recording at engine time <paramref name="engineTime"/> (frames are stored relative to it). A
    /// recording in progress is discarded.
    /// </summary>
    public void Start(double engineTime, string name = "Recording")
    {
        _tracks.Clear();
        _frames.Clear();
        _universes = Array.Empty<int>();
        _start = double.IsFinite(engineTime) ? engineTime : 0;
        _nextScan = 0;
        _lastTimecode = null;
        _name = string.IsNullOrWhiteSpace(name) ? "Recording" : name;
        _startedUtc = DateTime.UtcNow;
        IsRecording = true;
    }

    /// <summary>Stores the input as it is at engine time <paramref name="engineTime"/> (no-op when not recording).</summary>
    public void Capture(double engineTime)
    {
        if (!IsRecording || !double.IsFinite(engineTime)) return;
        double t = Math.Max(0, engineTime - _start);
        if (t >= _nextScan || _frames.Count == 0)
        {
            _universes = _hub.ActiveUniverses();
            _nextScan = t + UniverseScanInterval;
        }

        List<DmxUniverseChange>? changes = null;
        foreach (int universe in _universes)
        {
            bool live = _hub.TryRead(universe, _buffer, out long version);
            if (!_tracks.TryGetValue(universe, out var track))
            {
                if (!live) continue;
                _tracks[universe] = track = new UniverseTrack();
            }
            if (!live)
            {
                if (track.Live) (changes ??= new()).Add(new DmxUniverseChange(universe, DmxUniverseChangeKind.Lost, Array.Empty<DmxSlotRun>()));
                track.Live = false;
                continue;
            }
            if (!track.Live)
            {
                (changes ??= new()).Add(new DmxUniverseChange(universe, DmxUniverseChangeKind.Full,
                    new[] { new DmxSlotRun(0, (byte[])_buffer.Clone()) }));
                _buffer.CopyTo(track.Slots, 0);
                track.Live = true;
                track.Version = version;
                continue;
            }
            if (version == track.Version) continue;
            track.Version = version;
            var runs = DmxRecording.Diff(track.Slots, _buffer);
            if (runs.Count == 0) continue;
            _buffer.CopyTo(track.Slots, 0);
            (changes ??= new()).Add(new DmxUniverseChange(universe, DmxUniverseChangeKind.Delta, runs));
        }

        // universes that disappeared from the hub's list entirely
        _lost.Clear();
        foreach (var (universe, track) in _tracks)
            if (track.Live && !Contains(_universes, universe)) _lost.Add(universe);
        foreach (int universe in _lost)
        {
            _tracks[universe].Live = false;
            (changes ??= new()).Add(new DmxUniverseChange(universe, DmxUniverseChangeKind.Lost, Array.Empty<DmxSlotRun>()));
        }

        var timecode = _hub.LastTimecode;
        bool timecodeChanged = timecode != _lastTimecode;
        if (changes is null && !timecodeChanged && _frames.Count > 0) return;
        _lastTimecode = timecode;
        _frames.Add(new DmxRecordedFrame(t, timecode, (IReadOnlyList<DmxUniverseChange>?)changes ?? Array.Empty<DmxUniverseChange>()));
    }

    /// <summary>Ends the recording and returns it (null when nothing was being recorded).</summary>
    public DmxRecording? Stop()
    {
        if (!IsRecording) return null;
        IsRecording = false;
        var recording = new DmxRecording(_name, _frames.ToList(), _startedUtc);
        _frames.Clear();
        _tracks.Clear();
        return recording;
    }

    private static bool Contains(IReadOnlyList<int> list, int value)
    {
        for (int i = 0; i < list.Count; i++)
            if (list[i] == value) return true;
        return false;
    }

    private sealed class UniverseTrack
    {
        public readonly byte[] Slots = new byte[DmxInputHub.SlotCount];
        public bool Live;
        public long Version = -1;
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
        if (_timecode is { } tc && tc != _hub.LastTimecode) _hub.SubmitTimecode(tc);
    }
}
