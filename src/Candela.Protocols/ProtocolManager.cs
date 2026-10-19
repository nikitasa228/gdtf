using System.Text.Json;
using Candela.Dmx.Input;
using Candela.Protocols.Enttec;
using Candela.Protocols.Networking;

namespace Candela.Protocols;

/// <summary>
/// Owns one instance of every protocol (all 15, in display order) and starts/stops the set the user enabled with the
/// current options. Errors (port in use, interface missing, two protocols on one port) end up in the protocols'
/// statuses — no method here throws for run-time problems. Thread-safe.
/// </summary>
public sealed class ProtocolManager : IDisposable
{
    private readonly object _lock = new();
    private readonly IReadOnlyList<IExternalProtocol> _protocols;
    private readonly Dictionary<string, string> _startedWith = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    /// <summary>Creates every registered protocol, feeding <paramref name="hub"/>.</summary>
    /// <param name="hub">The DMX input hub.</param>
    /// <param name="enttecTransportFactory">Transport factory for the EntTec widget (tests pass a fake).</param>
    public ProtocolManager(DmxInputHub hub, Func<string, IEnttecTransport>? enttecTransportFactory = null)
        : this(ProtocolRegistry.CreateAll(hub ?? throw new ArgumentNullException(nameof(hub)), enttecTransportFactory))
    {
    }

    /// <summary>Manages the given protocol instances (custom sets, tests).</summary>
    public ProtocolManager(IEnumerable<IExternalProtocol> protocols)
    {
        ArgumentNullException.ThrowIfNull(protocols);
        _protocols = protocols.ToArray();
        var duplicate = _protocols.GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null) throw new ArgumentException($"Protocol '{duplicate.Key}' is listed twice.", nameof(protocols));
        foreach (var p in _protocols) p.StatusChanged += OnProtocolStatusChanged;
    }

    /// <summary>All protocols in display order.</summary>
    public IReadOnlyList<IExternalProtocol> Protocols => _protocols;

    /// <summary>The options of the last <see cref="Apply"/>.</summary>
    public ProtocolOptions Options { get; private set; } = new();

    /// <summary>Ids of the protocols that are currently running.</summary>
    public IReadOnlyList<string> RunningProtocolIds => _protocols.Where(p => p.IsRunning).Select(p => p.Id).ToArray();

    /// <summary>Raised (on any thread) when any protocol's status changes.</summary>
    public event EventHandler<ProtocolStatusEventArgs>? StatusChanged;

    /// <summary>The protocol with <paramref name="id"/>, or null.</summary>
    public IExternalProtocol? Find(string id)
    {
        foreach (var p in _protocols)
            if (string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)) return p;
        return null;
    }

    /// <summary>The first protocol of type <typeparamref name="T"/> (e.g. <c>Get&lt;ArtNetReceiver&gt;()</c>), or null.</summary>
    public T? Get<T>()
        where T : class, IExternalProtocol => _protocols.OfType<T>().FirstOrDefault();

    /// <summary>
    /// Makes exactly <paramref name="enabledIds"/> run with <paramref name="options"/>: stops the others, starts new
    /// ones and restarts running ones whose own options changed (see
    /// <see cref="ExternalProtocolBase.OptionsSignature"/>; a running protocol whose options are unchanged keeps
    /// running, so saving unrelated settings does not make DMX drop out). Unknown ids are ignored. Two UDP protocols configured on the same port are refused with an
    /// error status for the later one. Returns every protocol's status afterwards.
    /// </summary>
    public IReadOnlyList<ProtocolStatus> Apply(IEnumerable<string> enabledIds, ProtocolOptions options)
    {
        ArgumentNullException.ThrowIfNull(enabledIds);
        ArgumentNullException.ThrowIfNull(options);
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Options = options;
            var enabled = new HashSet<string>(enabledIds, StringComparer.OrdinalIgnoreCase);

            foreach (var p in _protocols)
            {
                if (!enabled.Contains(p.Id) && p.Status.State != ProtocolState.Stopped)
                {
                    p.Stop();
                    _startedWith.Remove(p.Id);
                }
            }

            var usedPorts = new Dictionary<int, string>();
            foreach (var p in _protocols)
            {
                if (!enabled.Contains(p.Id)) continue;
                int? port = UdpPortOf(p.Id, options);
                if (port is > 0 && usedPorts.TryGetValue(port.Value, out var other))
                {
                    p.Stop();
                    _startedWith.Remove(p.Id);
                    ForceError(p, ProtocolText.Create(ProtocolMessages.PortConflict, p.DisplayName, other, port.Value));
                    continue;
                }
                if (port is > 0) usedPorts[port.Value] = p.DisplayName;
                string signature = Signature(p, options);
                if (p.IsRunning && _startedWith.TryGetValue(p.Id, out var previous) && previous == signature) continue;
                bool ok = p.Start(options);
                if (ok) _startedWith[p.Id] = signature;
                else _startedWith.Remove(p.Id);
            }
            return GetStatus();
        }
    }

    /// <summary>Starts (or restarts) one protocol with the current <see cref="Options"/>. Returns false with the reason
    /// in its status when it cannot run; unknown ids return false.</summary>
    public bool Start(string id)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var p = Find(id);
            if (p is null) return false;
            bool ok = p.Start(Options);
            if (ok) _startedWith[p.Id] = Signature(p, Options);
            else _startedWith.Remove(p.Id);
            return ok;
        }
    }

    /// <summary>Stops one protocol (unknown ids are ignored).</summary>
    public void Stop(string id)
    {
        lock (_lock)
        {
            var p = Find(id);
            if (p is null) return;
            p.Stop();
            _startedWith.Remove(p.Id);
        }
    }

    /// <summary>Stops every protocol.</summary>
    public void StopAll()
    {
        lock (_lock)
        {
            foreach (var p in _protocols) p.Stop();
            _startedWith.Clear();
        }
    }

    /// <summary>Every protocol's status, in display order.</summary>
    public IReadOnlyList<ProtocolStatus> GetStatus() => _protocols.Select(p => p.Status).ToArray();

    /// <summary>IPv4 network interfaces for the settings UI (never throws).</summary>
    public static IReadOnlyList<NetworkInterfaceInfo> GetNetworkInterfaces() => NetworkInterfaceCatalog.Enumerate();

    /// <summary>Serial ports for the EntTec setting (never throws).</summary>
    public static IReadOnlyList<string> GetSerialPorts() => SerialPortEnttecTransport.GetPortNames();

    /// <summary>Stops and disposes every protocol.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var p in _protocols)
            {
                p.StatusChanged -= OnProtocolStatusChanged;
                p.Dispose();
            }
            _startedWith.Clear();
        }
    }

    /// <summary>The UDP port a protocol listens on with <paramref name="options"/>, or null for non-UDP protocols.</summary>
    internal static int? UdpPortOf(string id, ProtocolOptions options) => id switch
    {
        ProtocolIds.ArtNet => options.ArtNetPort,
        ProtocolIds.Sacn => options.SacnPort,
        ProtocolIds.Osc => options.OscPort,
        ProtocolIds.EtcEos => options.EosOscPort,
        ProtocolIds.PosiStageNet => options.PsnPort,
        _ => null,
    };

    /// <summary>The options that matter to <paramref name="protocol"/>: a change elsewhere does not restart it.</summary>
    private static string Signature(IExternalProtocol protocol, ProtocolOptions options) =>
        protocol is ExternalProtocolBase b ? b.OptionsSignature(options) : JsonSerializer.Serialize(options);

    private static void ForceError(IExternalProtocol protocol, ProtocolText message)
    {
        if (protocol is ExternalProtocolBase b) b.FailWithoutStarting(message, ProtocolErrorCode.PortConflict);
    }

    private void OnProtocolStatusChanged(object? sender, ProtocolStatusEventArgs e)
    {
        // Often called on a receive thread: one faulty subscriber must neither crash it nor starve the others.
        if (StatusChanged is not { } handler) return;
        foreach (var d in handler.GetInvocationList())
        {
            try
            {
                ((EventHandler<ProtocolStatusEventArgs>)d)(this, e);
            }
            catch (Exception)
            {
                // Contained; see ExternalProtocolBase.RaiseStatusChanged.
            }
        }
    }
}
