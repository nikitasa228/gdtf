using System.IO.Compression;
using System.Text;
using Candela.Core.Diagnostics;
using Candela.Dmx.Input;

namespace Candela.Engine;

/// <summary>How one universe changed in a recorded frame.</summary>
public enum DmxUniverseChangeKind : byte
{
    /// <summary>Some slots changed: <see cref="DmxUniverseChange.Runs"/> hold the new values.</summary>
    Delta = 0,

    /// <summary>The universe appeared (all 512 slots in one run).</summary>
    Full = 1,

    /// <summary>The universe stopped being received.</summary>
    Lost = 2,
}

/// <summary>A run of consecutive slots with their new values.</summary>
/// <param name="Start">First slot, 0-based.</param>
/// <param name="Values">New values (Start + Length ≤ 512).</param>
public readonly record struct DmxSlotRun(int Start, byte[] Values);

/// <summary>A change of one universe in one recorded frame.</summary>
/// <param name="Universe">Internal universe number (1-based).</param>
/// <param name="Kind">What happened.</param>
/// <param name="Runs">Changed slots (empty for <see cref="DmxUniverseChangeKind.Lost"/>).</param>
public sealed record DmxUniverseChange(int Universe, DmxUniverseChangeKind Kind, IReadOnlyList<DmxSlotRun> Runs);

/// <summary>One recorded frame: when it was captured, the timecode received by then, and the universes that changed.</summary>
/// <param name="Time">Seconds since the start of the recording.</param>
/// <param name="Timecode">Timecode received by then (ArtTimeCode …), or null.</param>
/// <param name="Changes">Universes that changed since the previous frame.</param>
public sealed record DmxRecordedFrame(double Time, Timecode? Timecode, IReadOnlyList<DmxUniverseChange> Changes);

/// <summary>
/// Recorded DMX input: timestamped frames of universe changes (delta-encoded) with the timecode that arrived with them.
/// Written by <see cref="DmxRecorder"/>, played by <see cref="DmxPlayer"/> — e.g. to render a timecode range offline
/// exactly as the console played it.
/// </summary>
/// <remarks>
/// <para><b>File</b> (<c>.cdmx</c>): <c>CDMX</c>, format version (int32), creation time (UTC ticks, int64), name, then a
/// zlib stream of frames: tag 0xF1, time (float64 s), flags (bit 0: timecode follows as h, m, s, f, type bytes), number
/// of changed universes (varint), and per universe its number (varint), the change kind (byte) and, for deltas, the runs
/// (varint count; per run varint start, varint length, the bytes). A file cut short (a crash while recording) loads the
/// complete frames before the cut and reports <see cref="EngineIssueCodes.RecordingDamaged"/>.</para>
/// <para><b>Seeking</b>: the full state of every universe is kept every <see cref="KeyframeInterval"/> seconds, so
/// <see cref="StateAt"/> replays at most that much.</para>
/// </remarks>
public sealed class DmxRecording
{
    /// <summary>File extension of recordings.</summary>
    public const string Extension = ".cdmx";

    /// <summary>Current file format version.</summary>
    public const int FormatVersion = 1;

    /// <summary>
    /// Frames up to this much after a requested time count as reached, s (absorbs the rounding of times computed as
    /// differences of engine clocks).
    /// </summary>
    public const double TimeTolerance = 1e-6;

    /// <summary>Recording time between kept full states (seek points), s.</summary>
    public const double KeyframeInterval = 2.0;

    private const byte FrameTag = 0xF1;
    private static readonly byte[] Magic = "CDMX"u8.ToArray();
    private readonly List<Keyframe> _keyframes = new();

    /// <summary>Creates a recording from frames (sorted by time; ties keep their order).</summary>
    public DmxRecording(string name, IEnumerable<DmxRecordedFrame> frames, DateTime createdUtc = default)
    {
        ArgumentNullException.ThrowIfNull(frames);
        Name = string.IsNullOrWhiteSpace(name) ? "Recording" : name;
        CreatedUtc = createdUtc == default ? DateTime.UtcNow : createdUtc;
        Frames = frames.Where(f => double.IsFinite(f.Time)).OrderBy(f => f.Time).ToList();
        BuildKeyframes();
    }

    /// <summary>Name of the recording (also the source name in the input hub during playback).</summary>
    public string Name { get; }

    /// <summary>When the recording started (UTC).</summary>
    public DateTime CreatedUtc { get; }

    /// <summary>Frames in time order.</summary>
    public IReadOnlyList<DmxRecordedFrame> Frames { get; }

    /// <summary>Problems found while loading (damaged file); empty for a recording made in this session.</summary>
    public IReadOnlyList<Issue> Issues { get; private init; } = Array.Empty<Issue>();

    /// <summary>Time of the last frame, s.</summary>
    public double Duration => Frames.Count > 0 ? Frames[^1].Time : 0;

    /// <summary>Every universe that occurs in the recording, ascending.</summary>
    public IReadOnlyList<int> Universes => Frames.SelectMany(f => f.Changes).Select(c => c.Universe).Distinct().Order().ToList();

    /// <summary>Timecode of the first frame that carries one, or null.</summary>
    public Timecode? FirstTimecode => Frames.FirstOrDefault(f => f.Timecode is not null)?.Timecode;

    /// <summary>
    /// The recording time at which <paramref name="timecode"/> was received: the first frame whose timecode is at or after
    /// it (timecode running forwards), or null when the recording carries no such timecode.
    /// </summary>
    public double? FindTimecode(Timecode timecode)
    {
        double target = timecode.TotalSeconds;
        DmxRecordedFrame? previous = null;
        foreach (var f in Frames)
        {
            if (f.Timecode is not { } tc) continue;
            double s = tc.TotalSeconds;
            if (s >= target - 1e-6)
            {
                // interpolate within one timecode frame of the previous sample so seeks land between captures precisely
                if (previous?.Timecode is { } p && s - p.TotalSeconds is > 0 and < 1.0)
                {
                    double k = Math.Clamp((target - p.TotalSeconds) / (s - p.TotalSeconds), 0, 1);
                    return previous.Time + k * (f.Time - previous.Time);
                }
                return f.Time;
            }
            previous = f;
        }
        return null;
    }

    /// <summary>
    /// The state of every universe at recording time <paramref name="time"/> (after all frames at or before it), written
    /// into <paramref name="state"/> (universe → 512 slots; universes lost by then are removed).
    /// </summary>
    public void StateAt(double time, IDictionary<int, byte[]> state)
    {
        ArgumentNullException.ThrowIfNull(state);
        state.Clear();
        int start = 0;
        time += TimeTolerance;
        foreach (var k in _keyframes)
        {
            if (k.Time > time) break;
            start = k.FrameIndex;
            state.Clear();
            foreach (var (u, slots) in k.State) state[u] = (byte[])slots.Clone();
        }
        for (int i = start; i < Frames.Count && Frames[i].Time <= time; i++) Apply(Frames[i], state);
    }

    /// <summary>Index of the first frame after <paramref name="time"/> (binary search).</summary>
    internal int FirstFrameAfter(double time)
    {
        time += TimeTolerance;
        int lo = 0, hi = Frames.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (Frames[mid].Time <= time) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    /// <summary>Applies one frame's changes to a universe state map.</summary>
    internal static void Apply(DmxRecordedFrame frame, IDictionary<int, byte[]> state)
    {
        foreach (var c in frame.Changes)
        {
            if (c.Kind == DmxUniverseChangeKind.Lost)
            {
                state.Remove(c.Universe);
                continue;
            }
            if (!state.TryGetValue(c.Universe, out var slots))
                state[c.Universe] = slots = new byte[DmxInputHub.SlotCount];
            else if (c.Kind == DmxUniverseChangeKind.Full)
                Array.Clear(slots);
            foreach (var run in c.Runs)
            {
                int n = Math.Min(run.Values.Length, DmxInputHub.SlotCount - run.Start);
                if (run.Start >= 0 && n > 0) run.Values.AsSpan(0, n).CopyTo(slots.AsSpan(run.Start));
            }
        }
    }

    /// <summary>Writes the recording to a stream.</summary>
    public void Save(Stream output)
    {
        ArgumentNullException.ThrowIfNull(output);
        using (var header = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true))
        {
            header.Write(Magic);
            header.Write(FormatVersion);
            header.Write(CreatedUtc.Ticks);
            header.Write(Name);
        }
        using var z = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true);
        using var w = new BinaryWriter(z, Encoding.UTF8, leaveOpen: true);
        foreach (var f in Frames)
        {
            w.Write(FrameTag);
            w.Write(f.Time);
            if (f.Timecode is { } tc)
            {
                w.Write((byte)1);
                w.Write((byte)Math.Clamp(tc.Hours, 0, 255));
                w.Write((byte)Math.Clamp(tc.Minutes, 0, 255));
                w.Write((byte)Math.Clamp(tc.Seconds, 0, 255));
                w.Write((byte)Math.Clamp(tc.Frames, 0, 255));
                w.Write((byte)tc.Type);
            }
            else
            {
                w.Write((byte)0);
            }
            w.Write7BitEncodedInt(f.Changes.Count);
            foreach (var c in f.Changes)
            {
                w.Write7BitEncodedInt(c.Universe);
                w.Write((byte)c.Kind);
                if (c.Kind == DmxUniverseChangeKind.Lost) continue;
                w.Write7BitEncodedInt(c.Runs.Count);
                foreach (var r in c.Runs)
                {
                    w.Write7BitEncodedInt(r.Start);
                    w.Write7BitEncodedInt(r.Values.Length);
                    w.Write(r.Values);
                }
            }
        }
    }

    /// <summary>Writes the recording to a file atomically (temporary file + rename).</summary>
    public void Save(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                Save(fs);
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    /// <summary>
    /// Reads a recording. Throws <see cref="InvalidDataException"/> when the stream is not a recording at all; a damaged
    /// or truncated frame stream keeps the frames before the damage and lists the problem in <see cref="Issues"/>.
    /// </summary>
    public static DmxRecording Load(Stream input)
    {
        ArgumentNullException.ThrowIfNull(input);
        string name;
        DateTime created;
        try
        {
            using var header = new BinaryReader(input, Encoding.UTF8, leaveOpen: true);
            if (!header.ReadBytes(4).AsSpan().SequenceEqual(Magic)) throw new InvalidDataException("Not a Candela DMX recording.");
            int version = header.ReadInt32();
            if (version != FormatVersion) throw new InvalidDataException($"Unsupported DMX recording version {version}.");
            long ticks = header.ReadInt64();
            created = ticks >= DateTime.MinValue.Ticks && ticks <= DateTime.MaxValue.Ticks ? new DateTime(ticks, DateTimeKind.Utc) : DateTime.UnixEpoch;
            name = ReadName(header);
        }
        catch (EndOfStreamException e)
        {
            throw new InvalidDataException("The DMX recording is truncated.", e);
        }
        catch (FormatException e)
        {
            // a malformed length (7-bit encoded integer) in the header
            throw new InvalidDataException($"The DMX recording's header is damaged: {e.Message}", e);
        }

        var frames = new List<DmxRecordedFrame>();
        string? problem = null;
        try
        {
            using var z = new ZLibStream(input, CompressionMode.Decompress, leaveOpen: true);
            using var r = new BinaryReader(z, Encoding.UTF8, leaveOpen: true);
            while (true)
            {
                int tag = ReadTagOrEnd(r);
                if (tag < 0) break;
                if (tag != FrameTag)
                {
                    problem = "an unknown record was found";
                    break;
                }
                frames.Add(ReadFrame(r));
            }
        }
        catch (Exception e) when (e is EndOfStreamException or InvalidDataException or IOException or FormatException or ArgumentException)
        {
            problem = e is EndOfStreamException ? "the file ends in the middle of a frame" : e.Message;
        }
        var issues = problem is null
            ? Array.Empty<Issue>()
            : new[]
            {
                new Issue(IssueSeverity.Warning, IssueCategory.Program, EngineIssueCodes.RecordingDamaged,
                    $"The DMX recording '{name}' is damaged ({problem}); the first {frames.Count} frames are used."),
            };
        return new DmxRecording(name, frames, created) { Issues = issues };
    }

    /// <summary>Reads a recording file (see <see cref="Load(Stream)"/>).</summary>
    public static DmxRecording Load(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Load(fs);
    }

    private static string ReadName(BinaryReader r)
    {
        int length = r.Read7BitEncodedInt();
        if (length < 0 || length > 4096) throw new InvalidDataException("The recording name is too long.");
        var bytes = r.ReadBytes(length);
        if (bytes.Length != length) throw new EndOfStreamException();
        return Encoding.UTF8.GetString(bytes);
    }

    private static int ReadTagOrEnd(BinaryReader r)
    {
        try
        {
            return r.ReadByte();
        }
        catch (EndOfStreamException)
        {
            return -1;
        }
    }

    private static DmxRecordedFrame ReadFrame(BinaryReader r)
    {
        double time = r.ReadDouble();
        if (!double.IsFinite(time)) throw new InvalidDataException("A frame has an invalid time.");
        byte flags = r.ReadByte();
        Timecode? tc = null;
        if ((flags & 1) != 0)
        {
            int h = r.ReadByte(), m = r.ReadByte(), s = r.ReadByte(), f = r.ReadByte();
            var type = (TimecodeType)r.ReadByte();
            if (!Enum.IsDefined(type)) throw new InvalidDataException("A frame has an unknown timecode type.");
            tc = new Timecode(h, m, s, f, type);
        }
        int count = r.Read7BitEncodedInt();
        if (count < 0 || count > 65536) throw new InvalidDataException("A frame lists too many universes.");
        var changes = new List<DmxUniverseChange>(Math.Min(count, 64));
        for (int i = 0; i < count; i++)
        {
            int universe = r.Read7BitEncodedInt();
            var kind = (DmxUniverseChangeKind)r.ReadByte();
            if (universe < 1 || !Enum.IsDefined(kind)) throw new InvalidDataException("A frame has an invalid universe record.");
            if (kind == DmxUniverseChangeKind.Lost)
            {
                changes.Add(new DmxUniverseChange(universe, kind, Array.Empty<DmxSlotRun>()));
                continue;
            }
            int runs = r.Read7BitEncodedInt();
            if (runs < 0 || runs > DmxInputHub.SlotCount) throw new InvalidDataException("A universe record has too many runs.");
            var list = new DmxSlotRun[runs];
            for (int k = 0; k < runs; k++)
            {
                int start = r.Read7BitEncodedInt(), length = r.Read7BitEncodedInt();
                if (start < 0 || length < 0 || start + length > DmxInputHub.SlotCount) throw new InvalidDataException("A slot run is out of range.");
                var values = r.ReadBytes(length);
                if (values.Length != length) throw new EndOfStreamException();
                list[k] = new DmxSlotRun(start, values);
            }
            changes.Add(new DmxUniverseChange(universe, kind, list));
        }
        return new DmxRecordedFrame(time, tc, changes);
    }

    private void BuildKeyframes()
    {
        var state = new Dictionary<int, byte[]>();
        double next = 0;
        for (int i = 0; i < Frames.Count; i++)
        {
            if (Frames[i].Time >= next)
            {
                _keyframes.Add(new Keyframe(Frames[i].Time, i, state.ToDictionary(kv => kv.Key, kv => (byte[])kv.Value.Clone())));
                next = Frames[i].Time + KeyframeInterval;
            }
            Apply(Frames[i], state);
        }
    }

    /// <summary>The state before frame <see cref="FrameIndex"/> (captured at <see cref="Time"/>).</summary>
    private sealed record Keyframe(double Time, int FrameIndex, Dictionary<int, byte[]> State);

    /// <summary>Computes the runs of slots that differ between two universe states (gaps up to 4 equal slots are bridged).</summary>
    internal static List<DmxSlotRun> Diff(ReadOnlySpan<byte> previous, ReadOnlySpan<byte> current)
    {
        var runs = new List<DmxSlotRun>();
        int n = Math.Min(previous.Length, current.Length);
        int i = 0;
        while (i < n)
        {
            if (previous[i] == current[i])
            {
                i++;
                continue;
            }
            int start = i, end = i, equal = 0;
            for (int k = i + 1; k < n; k++)
            {
                if (previous[k] != current[k])
                {
                    end = k;
                    equal = 0;
                }
                else if (++equal > 4)
                {
                    break;
                }
            }
            runs.Add(new DmxSlotRun(start, current[start..(end + 1)].ToArray()));
            i = end + 1;
        }
        return runs;
    }
}
