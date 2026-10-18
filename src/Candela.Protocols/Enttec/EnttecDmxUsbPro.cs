using Candela.Dmx.Input;

namespace Candela.Protocols.Enttec;

/// <summary>
/// EntTec DMX USB Pro input: opens the widget's serial port, switches it to "send every received frame", asks for its
/// parameters and serial number, and feeds received DMX (start code 0) into one universe of the
/// <see cref="DmxInputHub"/>. Change-of-state frames are applied to a held copy of the universe. Frames the widget
/// flags as overrun/overflowed are dropped as bad. An unplugged widget becomes an error status, never an exception.
/// </summary>
public sealed class EnttecDmxUsbPro : ExternalProtocolBase
{
    /// <summary>Protocol name used in <see cref="DmxSourceId.Protocol"/>.</summary>
    public const string SourceProtocol = "enttec";

    private readonly DmxInputHub _hub;
    private readonly Func<string, IEnttecTransport> _transportFactory;
    private readonly object _writeLock = new();
    private readonly byte[] _held = new byte[DmxInputHub.SlotCount];
    private readonly byte[] _writeBuffer = new byte[EnttecProFrame.MaxPayload + EnttecProFrame.Overhead];
    private volatile IEnttecTransport? _transport;
    private Thread? _thread;
    private volatile bool _running;
    private DmxSourceId _source;
    private int _universe = 1;
    private string _label = "";
    private int _heldLength;
    private EnttecWidgetParameters? _parameters;
    private long _serial = -1;

    /// <summary>Creates the protocol.</summary>
    /// <param name="hub">Hub that receives the DMX.</param>
    /// <param name="transportFactory">Opens a transport for a port name (default <see cref="SerialPortEnttecTransport"/>).</param>
    /// <param name="timeProvider">Clock for statistics.</param>
    public EnttecDmxUsbPro(DmxInputHub hub, Func<string, IEnttecTransport>? transportFactory = null, TimeProvider? timeProvider = null)
        : base(ProtocolRegistry.Get(ProtocolIds.EnttecDmxUsbPro), timeProvider)
    {
        _hub = hub ?? throw new ArgumentNullException(nameof(hub));
        _transportFactory = transportFactory ?? (name => new SerialPortEnttecTransport(name));
    }

    /// <summary>Widget parameters once the widget has answered (else null).</summary>
    public EnttecWidgetParameters? WidgetParameters => Volatile.Read(ref _parameters);

    /// <summary>Widget serial number once the widget has answered (else null).</summary>
    public uint? SerialNumber => Interlocked.Read(ref _serial) is var s and >= 0 ? (uint)s : null;

    /// <summary>Raised on the reader thread when the widget reports parameters or its serial number.</summary>
    public event EventHandler? WidgetInfoReceived;

    /// <summary>
    /// Sends DMX out of the widget (label 6, "output only send DMX"), e.g. to pass Candela's output to a real rig.
    /// Returns false when the widget is not running or the write failed.
    /// </summary>
    public bool SendDmx(ReadOnlySpan<byte> slots, byte startCode = 0)
    {
        var transport = _transport;
        if (!_running || transport is null) return false;
        lock (_writeLock)
        {
            try
            {
                int n = EnttecProFrame.EncodeSendDmx(_writeBuffer, slots.Length > 512 ? slots[..512] : slots, startCode);
                transport.Write(_writeBuffer.AsSpan(0, n));
                return true;
            }
            catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException
                                           or ObjectDisposedException or TimeoutException)
            {
                ReportFault($"EntTec widget on {transport.Name}: write failed ({e.Message}).");
                return false;
            }
        }
    }

    /// <inheritdoc />
    protected override string OnStart(ProtocolOptions options)
    {
        var portName = options.EnttecSerialPort?.Trim() ?? "";
        if (portName.Length == 0)
        {
            var ports = SerialPortEnttecTransport.GetPortNames();
            throw new ProtocolStartException("Choose the serial port of the EntTec DMX USB Pro" +
                                             (ports.Count > 0 ? $" (available: {string.Join(", ", ports)})." : " (no serial ports found)."));
        }
        if (options.EnttecUniverse < 1)
            throw new ProtocolStartException($"EntTec universe {options.EnttecUniverse} is invalid; universes start at 1.");

        IEnttecTransport? transport = null;
        try
        {
            transport = _transportFactory(portName);
            transport.Open();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            transport?.Dispose();
            throw new ProtocolStartException($"Cannot open the EntTec widget on {portName}: {e.Message} " +
                                             "(is it plugged in, and not used by another program?)", e);
        }
        catch
        {
            transport?.Dispose();
            throw;
        }

        _transport = transport;
        _universe = options.EnttecUniverse;
        _source = new DmxSourceId(SourceProtocol, transport.Name);
        _label = transport.Name;
        _heldLength = 0;
        Array.Clear(_held);
        Volatile.Write(ref _parameters, null);
        Interlocked.Exchange(ref _serial, -1);

        try
        {
            lock (_writeLock)
            {
                transport.Write(EnttecProFrame.EncodeReceiveDmxOnChange(onChangeOnly: false));
                transport.Write(EnttecProFrame.EncodeGetParameters());
                transport.Write(EnttecProFrame.EncodeGetSerialNumber());
            }
        }
        catch (IOException e)
        {
            throw new ProtocolStartException($"The EntTec widget on {portName} does not accept commands: {e.Message}", e);
        }

        _running = true;
        _thread = new Thread(() => ReadLoop(transport)) { IsBackground = true, Name = "Candela EntTec DMX USB Pro reader" };
        _thread.Start();
        return $"Receiving DMX from {transport.Name} into universe {_universe}";
    }

    /// <inheritdoc />
    protected internal override string OptionsSignature(ProtocolOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Signature(options.EnttecSerialPort, options.EnttecUniverse);
    }

    /// <inheritdoc />
    protected override void OnStop()
    {
        _running = false;
        var thread = _thread;
        _thread = null;
        if (thread is not null && thread.ManagedThreadId != Environment.CurrentManagedThreadId) thread.Join(TimeSpan.FromSeconds(2));
        var transport = _transport;
        _transport = null;
        if (transport is not null)
        {
            transport.Close();
            transport.Dispose();
            _hub.Terminate(_source, _universe);
        }
    }

    /// <summary>Handles one decoded frame (reader thread; tests).</summary>
    internal void HandleFrame(EnttecProLabel label, ReadOnlySpan<byte> payload)
    {
        var now = UtcNow;
        switch (label)
        {
            case EnttecProLabel.ReceivedDmx:
                if (!EnttecProFrame.TryDecodeReceivedDmx(payload, out var status, out var startCode, out var slots))
                {
                    CountBadPacket("received-DMX frame too short", default);
                    return;
                }
                if (status != 0)
                {
                    CountBadPacket((status & 0x02) != 0 ? "widget reported a DMX receive overrun" : "widget receive queue overflowed", default);
                    return;
                }
                if (startCode != 0)
                {
                    CountIgnoredPacket();
                    return;
                }
                slots.CopyTo(_held);
                if (slots.Length < _heldLength) _held.AsSpan(slots.Length, _heldLength - slots.Length).Clear();
                _heldLength = slots.Length;
                _hub.Submit(_source, _universe, slots, DmxInputHub.DefaultPriority, _label);
                CountPacket(now);
                CountUniversePacket(_universe, now, _label);
                break;
            case EnttecProLabel.ReceivedDmxChangeOfState:
                if (!EnttecProFrame.TryApplyChangeOfState(payload, _held, out _))
                {
                    CountBadPacket("malformed change-of-state frame", default);
                    return;
                }
                _heldLength = DmxInputHub.SlotCount;
                _hub.Submit(_source, _universe, _held, DmxInputHub.DefaultPriority, _label);
                CountPacket(now);
                CountUniversePacket(_universe, now, _label);
                break;
            case EnttecProLabel.GetWidgetParameters:
                if (EnttecProFrame.TryDecodeParameters(payload, out var parameters))
                {
                    Volatile.Write(ref _parameters, parameters);
                    WidgetInfoReceived?.Invoke(this, EventArgs.Empty);
                }
                else CountBadPacket("widget-parameters reply too short", default);
                break;
            case EnttecProLabel.GetWidgetSerialNumber:
                if (EnttecProFrame.TryDecodeSerialNumber(payload, out var serial))
                {
                    Interlocked.Exchange(ref _serial, serial);
                    WidgetInfoReceived?.Invoke(this, EventArgs.Empty);
                }
                else CountBadPacket("serial-number reply too short", default);
                break;
            default:
                CountIgnoredPacket();
                break;
        }
    }

    private void ReadLoop(IEnttecTransport transport)
    {
        var decoder = new EnttecProFrameDecoder(HandleFrame);
        var buffer = new byte[1024];
        long lastBad = 0;
        while (_running)
        {
            int n;
            try
            {
                n = transport.Read(buffer);
            }
            catch (Exception e)
            {
                // Any failure of the device read ends this loop with an error status. An exception escaping a
                // background thread would terminate the whole program.
                if (_running)
                {
                    _running = false;
                    ReportFault(e is IOException or UnauthorizedAccessException or InvalidOperationException or ObjectDisposedException
                        ? $"EntTec widget on {transport.Name} disconnected: {e.Message}"
                        : $"EntTec widget on {transport.Name} failed: {e.GetType().Name}: {e.Message}");
                }
                break;
            }
            if (n <= 0) continue;
            try
            {
                decoder.Feed(buffer.AsSpan(0, n));
            }
            catch (Exception e)
            {
                // A handler bug must not stop DMX input.
                CountBadPacket($"internal error while decoding EntTec data: {e.GetType().Name}", default);
            }
            if (decoder.BadFrames != lastBad)
            {
                for (long i = lastBad; i < decoder.BadFrames; i++) CountBadPacket("corrupted EntTec frame", default);
                lastBad = decoder.BadFrames;
            }
        }
    }
}
