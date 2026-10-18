using System.Diagnostics;
using System.Net;
using Candela.Dmx.Input;

namespace Candela.Protocols.ArtNet;

/// <summary>
/// A built-in "console" that plays an <see cref="ArtNetTestPattern"/> over Art-Net: per frame one ArtDmx per universe,
/// then ArtSync (optional) and ArtTimeCode (optional) — so Candela (or any Art-Net node) can be tested without a real
/// console. <see cref="SendFrame"/> sends one deterministic frame; <see cref="Start"/> runs a paced background loop.
/// </summary>
public sealed class ArtNetTestConsole : IDisposable
{
    private readonly ArtNetSender _sender;
    private readonly bool _ownsSender;
    private readonly object _lock = new();
    private Thread? _thread;
    private volatile bool _running;
    private long _frames;

    /// <summary>Creates the console.</summary>
    /// <param name="pattern">The show to play.</param>
    /// <param name="target">Destination: a node's IP (unicast), a directed broadcast (e.g. 2.255.255.255) or
    /// 127.0.0.1, port 6454 normally.</param>
    /// <param name="universeOffset">Art-Net port-address = internal universe − offset (1 = consoles' default).</param>
    /// <param name="sender">Sender to use (default: a new one owned by this console).</param>
    public ArtNetTestConsole(ArtNetTestPattern pattern, IPEndPoint target, int universeOffset = 1, ArtNetSender? sender = null)
    {
        Pattern = pattern ?? throw new ArgumentNullException(nameof(pattern));
        Target = target ?? throw new ArgumentNullException(nameof(target));
        UniverseOffset = universeOffset;
        _ownsSender = sender is null;
        _sender = sender ?? new ArtNetSender();
    }

    /// <summary>The show being played.</summary>
    public ArtNetTestPattern Pattern { get; }

    /// <summary>Where packets go.</summary>
    public IPEndPoint Target { get; }

    /// <summary>Art-Net port-address = internal universe − this offset.</summary>
    public int UniverseOffset { get; }

    /// <summary>DMX frames per second of the background loop (1–44; Art-Net consoles typically send 30–44).</summary>
    public double FrameRate { get; init; } = 40;

    /// <summary>Timecode frame-rate type sent with every frame.</summary>
    public TimecodeType TimecodeType { get; init; } = TimecodeType.Ebu25;

    /// <summary>Send ArtTimeCode with every frame.</summary>
    public bool SendTimecode { get; init; } = true;

    /// <summary>Send ArtSync after the ArtDmx packets of a frame.</summary>
    public bool SendSync { get; init; } = true;

    /// <summary>Frames sent so far.</summary>
    public long FramesSent => Interlocked.Read(ref _frames);

    /// <summary>True while the background loop runs.</summary>
    public bool IsRunning => _running;

    /// <summary>Renders the show at <paramref name="timeSeconds"/> and sends it (thread-safe).</summary>
    public void SendFrame(double timeSeconds)
    {
        lock (_lock)
        {
            Pattern.Render(timeSeconds);
            var universes = Pattern.Universes;
            for (int i = 0; i < universes.Count; i++)
            {
                int universe = universes[i];
                int portAddress = universe - UniverseOffset;
                if (portAddress is < 0 or > ArtNetProtocol.MaxPortAddress) continue;
                _sender.SendDmx(Target, portAddress, Pattern.GetUniverse(universe));
            }
            if (SendSync) _sender.SendSync(Target);
            if (SendTimecode) _sender.SendTimeCode(Target, TimecodeAt(timeSeconds, TimecodeType));
            Interlocked.Increment(ref _frames);
        }
    }

    /// <summary>Starts sending at <see cref="FrameRate"/> from time 0 on a background thread.</summary>
    public void Start()
    {
        lock (_lock)
        {
            if (_running) return;
            _running = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "Candela Art-Net test console" };
            _thread.Start();
        }
    }

    /// <summary>Stops the background loop.</summary>
    public void Stop()
    {
        Thread? t;
        lock (_lock)
        {
            _running = false;
            t = _thread;
            _thread = null;
        }
        if (t is not null && t.ManagedThreadId != Environment.CurrentManagedThreadId) t.Join(TimeSpan.FromSeconds(2));
    }

    /// <summary>Stops and releases the sender if owned.</summary>
    public void Dispose()
    {
        Stop();
        if (_ownsSender) _sender.Dispose();
    }

    /// <summary>
    /// The SMPTE/EBU timecode at <paramref name="seconds"/> of real time (drop-frame labels for
    /// <see cref="TimecodeType.Df2997"/>). Hours wrap at 24.
    /// </summary>
    public static Timecode TimecodeAt(double seconds, TimecodeType type)
    {
        if (double.IsNaN(seconds) || seconds < 0) seconds = 0;
        if (type == TimecodeType.Df2997)
        {
            long fn = (long)Math.Floor(seconds * 30000.0 / 1001.0);
            const long FramesPer10Minutes = 17982, FramesPerMinute = 1798;
            long d = fn / FramesPer10Minutes, m = fn % FramesPer10Minutes;
            fn += 18 * d + (m < 2 ? 0 : 2 * ((m - 2) / FramesPerMinute));
            return new Timecode((int)(fn / 108000 % 24), (int)(fn / 1800 % 60), (int)(fn / 30 % 60), (int)(fn % 30), type);
        }
        int fps = type switch { TimecodeType.Film24 => 24, TimecodeType.Ebu25 => 25, _ => 30 };
        long frames = (long)Math.Floor(seconds * fps + 1e-9);
        return new Timecode((int)(frames / (fps * 3600L) % 24), (int)(frames / (fps * 60L) % 60), (int)(frames / fps % 60), (int)(frames % fps), type);
    }

    private void Loop()
    {
        var clock = Stopwatch.StartNew();
        double period = 1.0 / Math.Clamp(FrameRate, 1, 44);
        double next = 0;
        while (_running)
        {
            double now = clock.Elapsed.TotalSeconds;
            if (now < next)
            {
                Thread.Sleep(TimeSpan.FromSeconds(Math.Min(next - now, 0.05)));
                continue;
            }
            try
            {
                SendFrame(now);
            }
            catch (System.Net.Sockets.SocketException)
            {
                // Network temporarily unavailable (cable unplugged): keep trying at the frame rate.
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            next = Math.Max(next + period, now - period);
        }
    }
}
