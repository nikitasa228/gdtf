using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Candela.Protocols.Networking;

namespace Candela.Protocols;

/// <summary>
/// Shared plumbing of every protocol: start/stop life cycle that never throws for run-time problems, thread-safe
/// packet counters, per-universe statistics and <see cref="IExternalProtocol.StatusChanged"/> notifications.
/// Derived classes implement <see cref="OnStart"/> / <see cref="OnStop"/> and call the <c>Count…</c> methods from
/// their receive thread; counting does not allocate after a universe has been seen once.
/// </summary>
public abstract class ExternalProtocolBase : IExternalProtocol
{
    private static readonly long RateWindowTicks = TimeSpan.TicksPerSecond;
    private static readonly long RateStaleTicks = 2 * TimeSpan.TicksPerSecond;

    private readonly object _lifecycleLock = new();
    private readonly object _statusLock = new();
    private readonly Dictionary<int, UniverseCounter> _universes = new();
    private readonly List<string> _warnings = new();
    private long _packets;
    private long _badPackets;
    private long _ignoredPackets;
    private long _lastPacketTicks;
    private string? _lastBadReason;
    private IPv4Endpoint _lastBadSender;
    private ProtocolState _state = ProtocolState.Stopped;
    private string _message = "Stopped";
    private string? _lastError;
    private bool _disposed;

    /// <summary>Creates the protocol from its registry descriptor.</summary>
    /// <param name="descriptor">Identity and support level.</param>
    /// <param name="timeProvider">Clock for statistics and timeouts (tests inject a fake one).</param>
    protected ExternalProtocolBase(ProtocolDescriptor descriptor, TimeProvider? timeProvider = null)
    {
        Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
        Time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Identity and support level of this protocol.</summary>
    public ProtocolDescriptor Descriptor { get; }

    /// <inheritdoc />
    public string Id => Descriptor.Id;

    /// <inheritdoc />
    public string DisplayName => Descriptor.DisplayName;

    /// <inheritdoc />
    public string Description => Descriptor.Description;

    /// <inheritdoc />
    public ProtocolSupportLevel SupportLevel => Descriptor.SupportLevel;

    /// <inheritdoc />
    public string SupportReason => Descriptor.SupportReason;

    /// <summary>The options of the current (or last successful) start, or null.</summary>
    public ProtocolOptions? Options { get; private set; }

    /// <inheritdoc />
    public bool IsRunning
    {
        get
        {
            lock (_statusLock) return _state is ProtocolState.Running or ProtocolState.Warning;
        }
    }

    /// <summary>Clock used for statistics and protocol timeouts.</summary>
    protected TimeProvider Time { get; }

    /// <summary>Current UTC time from <see cref="Time"/>.</summary>
    protected DateTime UtcNow => Time.GetUtcNow().UtcDateTime;

    /// <inheritdoc />
    public event EventHandler<ProtocolStatusEventArgs>? StatusChanged;

    /// <inheritdoc />
    public ProtocolStatus Status
    {
        get
        {
            long nowTicks = UtcNow.Ticks;
            lock (_statusLock)
            {
                var universes = new List<UniverseStatistics>(_universes.Count);
                foreach (var (universe, c) in _universes)
                {
                    double rate = nowTicks - c.LastTicks > RateStaleTicks ? 0 : c.Rate;
                    if (rate == 0 && c.WindowCount > 0 && nowTicks - c.LastTicks <= RateStaleTicks)
                        rate = c.WindowCount / System.Math.Max(1e-3, (nowTicks - c.WindowStartTicks) / (double)TimeSpan.TicksPerSecond);
                    universes.Add(new UniverseStatistics(
                        universe,
                        c.Packets,
                        c.SequenceErrors,
                        new DateTime(c.LastTicks, DateTimeKind.Utc),
                        rate,
                        c.LastSource));
                }
                universes.Sort(static (a, b) => a.Universe.CompareTo(b.Universe));
                long last = Interlocked.Read(ref _lastPacketTicks);
                return new ProtocolStatus(
                    Id,
                    _state,
                    _message,
                    _lastError,
                    _warnings.ToArray(),
                    Interlocked.Read(ref _packets),
                    Interlocked.Read(ref _badPackets),
                    Interlocked.Read(ref _ignoredPackets),
                    last == 0 ? null : new DateTime(last, DateTimeKind.Utc),
                    _lastBadReason is null ? null
                    : _lastBadSender == default ? _lastBadReason
                    : $"{_lastBadReason} (from {_lastBadSender.AddressString})",
                    universes);
            }
        }
    }

    /// <inheritdoc />
    public bool Start(ProtocolOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_lifecycleLock)
        {
            if (IsRunning || CurrentState == ProtocolState.Error) StopCore();
            if (!Descriptor.CanStart)
            {
                SetState(ProtocolState.Unavailable, $"{DisplayName} is not available: {SupportReason}", error: null, keepError: false);
                return false;
            }
            ResetStatistics();
            try
            {
                string summary = OnStart(options);
                Options = options;
                bool warn;
                lock (_statusLock) warn = _warnings.Count > 0;
                SetState(warn ? ProtocolState.Warning : ProtocolState.Running, summary, error: null, keepError: false);
                return true;
            }
            catch (Exception e)
            {
                SafeStop();
                string message = e is ProtocolStartException ? e.Message : $"{DisplayName} failed to start: {e.Message}";
                SetState(ProtocolState.Error, message, message, keepError: false);
                return false;
            }
        }
    }

    /// <inheritdoc />
    public void Stop()
    {
        lock (_lifecycleLock)
        {
            if (CurrentState == ProtocolState.Stopped) return;
            StopCore();
            SetState(ProtocolState.Stopped, "Stopped", error: null, keepError: true);
        }
    }

    /// <summary>Stops the protocol and releases its sockets/ports.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Opens sockets/devices and starts the receive thread. Throw <see cref="ProtocolStartException"/> with a
    /// user-facing message for problems the user can fix; any exception makes the start fail (after
    /// <see cref="OnStop"/> has cleaned up). Call <see cref="AddWarning"/> for degraded but working setups.
    /// </summary>
    /// <returns>A one-line summary for the status ("Listening on 0.0.0.0:6454 …").</returns>
    protected abstract string OnStart(ProtocolOptions options);

    /// <summary>Stops the receive thread, closes sockets/devices and withdraws hub sources. Must tolerate a partly
    /// completed <see cref="OnStart"/> and repeated calls.</summary>
    protected abstract void OnStop();

    /// <summary>Puts the protocol into <see cref="ProtocolState.Error"/> without starting it (configuration conflicts
    /// detected by <see cref="ProtocolManager"/>).</summary>
    internal void FailWithoutStarting(string message)
    {
        lock (_lifecycleLock)
        {
            if (IsRunning) StopCore();
            SetState(ProtocolState.Error, message, message, keepError: false);
        }
    }

    /// <summary>Records a non-fatal problem; a running protocol moves to <see cref="ProtocolState.Warning"/>.</summary>
    protected void AddWarning(string warning)
    {
        lock (_statusLock)
        {
            if (_warnings.Contains(warning)) return;
            _warnings.Add(warning);
            if (_state == ProtocolState.Running) _state = ProtocolState.Warning;
        }
        RaiseStatusChanged();
    }

    /// <summary>
    /// The part of <paramref name="options"/> this protocol uses. <see cref="ProtocolManager.Apply"/> restarts a running
    /// protocol only when this signature changes, so saving unrelated settings (an OSC pattern, the EntTec port) does
    /// not interrupt Art-Net or sACN reception. The default covers every option; overrides list the fields that
    /// <see cref="OnStart"/> reads.
    /// </summary>
    protected internal virtual string OptionsSignature(ProtocolOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return JsonSerializer.Serialize(options);
    }

    /// <summary>Joins option values into a signature string for <see cref="OptionsSignature"/> (lists element by
    /// element, numbers in the invariant culture).</summary>
    protected static string Signature(params object?[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var sb = new StringBuilder();
        foreach (var value in values)
        {
            switch (value)
            {
                case string text:
                    sb.Append(text.Length).Append(':').Append(text);
                    break;
                case IEnumerable list:
                    sb.Append('[');
                    foreach (var item in list)
                    {
                        string itemText = Convert.ToString(item, CultureInfo.InvariantCulture) ?? "";
                        sb.Append(itemText.Length).Append(':').Append(itemText).Append(',');
                    }
                    sb.Append(']');
                    break;
                default:
                    sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                    break;
            }
            sb.Append('\u001F');
        }
        return sb.ToString();
    }

    /// <summary>
    /// Reports a failure detected on a background thread (socket closed by the OS, serial device unplugged). The state
    /// becomes <see cref="ProtocolState.Error"/>; the caller is expected to end its receive loop.
    /// </summary>
    protected void ReportFault(string message) => SetState(ProtocolState.Error, message, message, keepError: false);

    /// <summary>Counts an accepted packet (thread-safe, allocation-free).</summary>
    protected void CountPacket(DateTime nowUtc)
    {
        Interlocked.Increment(ref _packets);
        Interlocked.Exchange(ref _lastPacketTicks, nowUtc.Ticks);
    }

    /// <summary>Counts a malformed packet. <paramref name="reason"/> should be a constant string (no formatting per
    /// packet); the sender is formatted only when a status snapshot is taken.</summary>
    protected void CountBadPacket(string reason, IPv4Endpoint sender)
    {
        Interlocked.Increment(ref _badPackets);
        lock (_statusLock)
        {
            _lastBadReason = reason;
            _lastBadSender = sender;
        }
    }

    /// <summary>Counts a well-formed packet that Candela does not use.</summary>
    protected void CountIgnoredPacket() => Interlocked.Increment(ref _ignoredPackets);

    /// <summary>Updates the statistics of one internal universe (thread-safe; allocates only for a new universe).</summary>
    /// <param name="universe">Internal 1-based universe.</param>
    /// <param name="nowUtc">Arrival time.</param>
    /// <param name="source">Sender label (keep it a cached string).</param>
    /// <param name="sequenceError">True when the packet was dropped as out of order (not counted as accepted).</param>
    protected void CountUniversePacket(int universe, DateTime nowUtc, string? source, bool sequenceError = false)
    {
        long now = nowUtc.Ticks;
        lock (_statusLock)
        {
            if (!_universes.TryGetValue(universe, out var c))
            {
                c = new UniverseCounter { WindowStartTicks = now };
                _universes[universe] = c;
            }
            if (sequenceError)
            {
                c.SequenceErrors++;
                return;
            }
            c.Packets++;
            c.LastTicks = now;
            c.LastSource = source;
            long elapsed = now - c.WindowStartTicks;
            if (elapsed >= RateWindowTicks)
            {
                c.Rate = c.WindowCount / (elapsed / (double)TimeSpan.TicksPerSecond);
                c.WindowStartTicks = now;
                c.WindowCount = 0;
            }
            c.WindowCount++;
        }
    }

    /// <summary>When the last accepted packet of <paramref name="universe"/> arrived, or null.</summary>
    protected DateTime? LastPacketOf(int universe)
    {
        lock (_statusLock)
        {
            return _universes.TryGetValue(universe, out var c) && c.Packets > 0 ? new DateTime(c.LastTicks, DateTimeKind.Utc) : null;
        }
    }

    /// <summary>Updates the one-line status message without changing the state (e.g. tracker counts).</summary>
    protected void SetMessage(string message)
    {
        lock (_statusLock) _message = message;
    }

    private ProtocolState CurrentState
    {
        get
        {
            lock (_statusLock) return _state;
        }
    }

    private void StopCore()
    {
        SafeStop();
        Options = null;
    }

    private void SafeStop()
    {
        try
        {
            OnStop();
        }
        catch (Exception e)
        {
            // Releasing resources must not fail a stop; remember why for the panel.
            lock (_statusLock) _lastError = $"{DisplayName} did not stop cleanly: {e.Message}";
        }
    }

    private void ResetStatistics()
    {
        lock (_statusLock)
        {
            _universes.Clear();
            _warnings.Clear();
            _lastBadReason = null;
            _lastBadSender = default;
        }
        Interlocked.Exchange(ref _packets, 0);
        Interlocked.Exchange(ref _badPackets, 0);
        Interlocked.Exchange(ref _ignoredPackets, 0);
        Interlocked.Exchange(ref _lastPacketTicks, 0);
    }

    private void SetState(ProtocolState state, string message, string? error, bool keepError)
    {
        bool changed;
        lock (_statusLock)
        {
            changed = _state != state || _message != message || (!keepError && _lastError != error);
            _state = state;
            _message = message;
            if (!keepError) _lastError = error;
        }
        if (changed) RaiseStatusChanged();
    }

    /// <summary>
    /// Raises <see cref="IExternalProtocol.StatusChanged"/>. Status changes are often reported from receive threads,
    /// timers and serial reader threads, where an exception thrown by a subscriber (a UI handler) would terminate the
    /// process; such exceptions are therefore contained here, each subscriber being called on its own.
    /// </summary>
    private void RaiseStatusChanged()
    {
        if (StatusChanged is not { } handler) return;
        var args = new ProtocolStatusEventArgs(this, Status);
        foreach (var d in handler.GetInvocationList())
        {
            try
            {
                ((EventHandler<ProtocolStatusEventArgs>)d)(this, args);
            }
            catch (Exception)
            {
                // A faulty subscriber must not stop reception or make Start/Stop throw.
            }
        }
    }

    private sealed class UniverseCounter
    {
        public long Packets;
        public long SequenceErrors;
        public long LastTicks;
        public string? LastSource;
        public long WindowStartTicks;
        public int WindowCount;
        public double Rate;
    }
}
