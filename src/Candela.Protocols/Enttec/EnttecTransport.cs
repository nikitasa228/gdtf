using System.IO.Ports;

namespace Candela.Protocols.Enttec;

/// <summary>
/// Byte transport to an EntTec DMX USB Pro widget. The real one is a USB virtual serial port
/// (<see cref="SerialPortEnttecTransport"/>); tests use an in-memory fake.
/// </summary>
public interface IEnttecTransport : IDisposable
{
    /// <summary>Port name for messages ("COM3", "/dev/ttyUSB0").</summary>
    string Name { get; }

    /// <summary>True while open.</summary>
    bool IsOpen { get; }

    /// <summary>Opens the port; throws <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> on failure.</summary>
    void Open();

    /// <summary>
    /// Reads available bytes, waiting at most about 100 ms. Returns 0 on timeout; throws <see cref="IOException"/> when
    /// the device is gone.
    /// </summary>
    int Read(Span<byte> buffer);

    /// <summary>Writes all bytes; throws <see cref="IOException"/> when the device is gone.</summary>
    void Write(ReadOnlySpan<byte> data);

    /// <summary>Closes the port (idempotent).</summary>
    void Close();
}

/// <summary><see cref="IEnttecTransport"/> over <see cref="SerialPort"/> (the widget's FTDI virtual COM port).</summary>
public sealed class SerialPortEnttecTransport : IEnttecTransport
{
    private readonly SerialPort _port;
    private readonly byte[] _readBuffer = new byte[1024];
    private readonly byte[] _writeBuffer = new byte[EnttecProFrame.MaxPayload + EnttecProFrame.Overhead];

    /// <summary>Creates the transport for <paramref name="portName"/> (not opened yet).</summary>
    public SerialPortEnttecTransport(string portName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(portName);
        // The widget talks USB; the baud rate is ignored by the FTDI driver but must be a legal value.
        _port = new SerialPort(portName, 57600, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            ReadTimeout = 100,
            WriteTimeout = 500,
            ReadBufferSize = 16384,
        };
    }

    /// <inheritdoc />
    public string Name => _port.PortName;

    /// <inheritdoc />
    public bool IsOpen => _port.IsOpen;

    /// <summary>Serial ports present on this computer (empty when they cannot be listed).</summary>
    public static IReadOnlyList<string> GetPortNames()
    {
        try
        {
            return SerialPort.GetPortNames().Order(StringComparer.Ordinal).ToArray();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException or InvalidOperationException)
        {
            return Array.Empty<string>();
        }
    }

    /// <inheritdoc />
    public void Open() => _port.Open();

    /// <inheritdoc />
    public int Read(Span<byte> buffer)
    {
        try
        {
            int n = _port.Read(_readBuffer, 0, Math.Min(buffer.Length, _readBuffer.Length));
            _readBuffer.AsSpan(0, n).CopyTo(buffer);
            return n;
        }
        catch (TimeoutException)
        {
            return 0;
        }
        catch (InvalidOperationException e)
        {
            throw new IOException($"Serial port {Name} is closed.", e);
        }
    }

    /// <inheritdoc />
    public void Write(ReadOnlySpan<byte> data)
    {
        try
        {
            while (!data.IsEmpty)
            {
                int n = Math.Min(data.Length, _writeBuffer.Length);
                data[..n].CopyTo(_writeBuffer);
                _port.Write(_writeBuffer, 0, n);
                data = data[n..];
            }
        }
        catch (TimeoutException e)
        {
            throw new IOException($"Writing to {Name} timed out.", e);
        }
        catch (InvalidOperationException e)
        {
            throw new IOException($"Serial port {Name} is closed.", e);
        }
    }

    /// <inheritdoc />
    public void Close()
    {
        try
        {
            if (_port.IsOpen) _port.Close();
        }
        catch (IOException)
        {
            // The device may already be unplugged.
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Close();
        _port.Dispose();
    }
}
