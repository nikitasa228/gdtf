using Candela.Dmx.Input;
using Candela.Protocols.Networking;

namespace Candela.Protocols.Osc;

/// <summary>
/// Generic OSC receiver (UDP, default port 8000). Messages that match one of the configured
/// <see cref="OscDmxPattern"/>s (default <c>/dmx/{universe}/{channel}</c> with int 0..255 or float 0..1, and
/// <c>/dmx/{universe}</c> with a blob or value list) set DMX slots; each sender keeps its own universe buffers, which
/// are submitted to the <see cref="DmxInputHub"/> after every datagram and re-submitted once a second (OSC senders only
/// send changes, while the hub expires silent sources). All messages are also exposed raw through
/// <see cref="OscReceiverBase.MessageReceived"/> and <see cref="OscReceiverBase.RecentMessages"/>.
/// </summary>
public sealed class OscReceiver : OscReceiverBase
{
    /// <summary>Protocol name used in <see cref="DmxSourceId.Protocol"/>.</summary>
    public const string SourceProtocol = "osc";

    /// <summary>How often held universes are re-submitted to keep them alive in the hub.</summary>
    public static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(1);

    /// <summary>Most (sender, universe) buffers held at once; messages for further ones are ignored (with a warning).</summary>
    public const int MaxHeldUniverses = 1024;

    private static readonly ProtocolText HeldTableFullWarning = ProtocolText.Create(ProtocolMessages.OscHeldTableFull, MaxHeldUniverses);

    private readonly DmxInputHub _hub;
    private readonly object _gate = new();
    private readonly Dictionary<(uint Sender, int Universe), Held> _held = new();
    private readonly List<Held> _dirty = new();
    private readonly byte[] _scratch = new byte[512];
    private OscDmxPattern[] _patterns = Array.Empty<OscDmxPattern>();
    private ITimer? _keepAlive;
    private bool _open;

    /// <summary>Creates the receiver feeding <paramref name="hub"/>.</summary>
    public OscReceiver(DmxInputHub hub, TimeProvider? timeProvider = null)
        : base(ProtocolRegistry.Get(ProtocolIds.Osc), timeProvider)
    {
        _hub = hub ?? throw new ArgumentNullException(nameof(hub));
    }

    /// <summary>The active mapping patterns.</summary>
    public IReadOnlyList<OscDmxPattern> Patterns => _patterns;

    /// <inheritdoc />
    protected override string ProtocolLabel => "OSC";

    /// <inheritdoc />
    protected override int PortFrom(ProtocolOptions options) => options.OscPort;

    /// <inheritdoc />
    protected internal override string OptionsSignature(ProtocolOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Signature(options.NetworkInterface, options.OscPort, options.OscPatterns);
    }

    /// <inheritdoc />
    protected override void OnConfigure(ProtocolOptions options)
    {
        var patterns = new List<OscDmxPattern>();
        foreach (var text in options.OscPatterns)
        {
            if (OscDmxPattern.TryParse(text, out var p, out var error)) patterns.Add(p!);
            else AddWarning(ProtocolText.Create(ProtocolMessages.OscPatternIgnored, error));
        }
        lock (_gate)
        {
            _patterns = patterns.ToArray();
            _held.Clear();
            _dirty.Clear();
            _open = true;
        }
        _keepAlive?.Dispose();
        _keepAlive = Time.CreateTimer(_ => KeepAlive(), null, KeepAliveInterval, KeepAliveInterval);
    }

    /// <summary>
    /// The universes the patterns write to when none of them takes the universe from the message (<c>{universe}</c>);
    /// with such a pattern any universe can be written and the count is that of the universes held so far.
    /// </summary>
    protected override int UniverseCountOf(ProtocolOptions options)
    {
        lock (_gate)
        {
            if (_patterns.Any(p => p.HasUniverse)) return _held.Count;
            return _patterns.Select(p => p.DefaultUniverse).Distinct().Count();
        }
    }

    /// <inheritdoc />
    protected override void OnClosed()
    {
        _keepAlive?.Dispose();
        _keepAlive = null;
        List<Held> held;
        lock (_gate)
        {
            _open = false;
            held = _held.Values.ToList();
            _held.Clear();
            _dirty.Clear();
        }
        foreach (var h in held) _hub.Terminate(h.Source, h.Universe);
    }

    /// <inheritdoc />
    protected override void OnMessages(IReadOnlyList<OscMessage> messages, IPv4Endpoint sender, string senderText, DateTime nowUtc)
    {
        var patterns = _patterns;
        if (patterns.Length == 0) return;
        bool full = false;
        lock (_gate)
        {
            if (!_open) return;
            for (int m = 0; m < messages.Count; m++)
            {
                var message = messages[m];
                bool mapped = false;
                foreach (var pattern in patterns)
                {
                    if (!pattern.TryMatch(message.Address, out int universe, out int channel)) continue;
                    int count = pattern.WriteValues(message, channel, _scratch);
                    if (count == 0) continue;
                    var held = GetHeld(sender, universe, senderText);
                    if (held is null)
                    {
                        full = true;
                        break;
                    }
                    _scratch.AsSpan(channel - 1, count).CopyTo(held.Slots.AsSpan(channel - 1));
                    if (!held.Dirty)
                    {
                        held.Dirty = true;
                        _dirty.Add(held);
                    }
                    CountUniversePacket(universe, nowUtc, senderText);
                    mapped = true;
                    break;
                }
                if (!mapped) CountIgnoredPacket();
            }
            foreach (var h in _dirty)
            {
                _hub.Submit(h.Source, h.Universe, h.Slots);
                h.Dirty = false;
            }
            _dirty.Clear();
        }
        if (full) AddWarning(HeldTableFullWarning);
    }

    private Held? GetHeld(IPv4Endpoint sender, int universe, string senderText)
    {
        var key = (sender.Address, universe);
        if (!_held.TryGetValue(key, out var held))
        {
            // Every held universe is re-submitted each second for as long as OSC runs: bound the set.
            if (_held.Count >= MaxHeldUniverses) return null;
            held = new Held(new DmxSourceId(SourceProtocol, senderText[..senderText.LastIndexOf(':')]), universe);
            _held[key] = held;
        }
        return held;
    }

    private void KeepAlive()
    {
        try
        {
            lock (_gate)
            {
                if (!_open) return;
                foreach (var h in _held.Values) _hub.Submit(h.Source, h.Universe, h.Slots);
            }
        }
        catch (Exception e)
        {
            // A timer callback must never crash the process.
            AddWarning(ProtocolText.Create(ProtocolMessages.OscKeepAliveFailed, e.Message));
        }
    }

    private sealed class Held
    {
        public Held(DmxSourceId source, int universe)
        {
            Source = source;
            Universe = universe;
        }

        public DmxSourceId Source { get; }
        public int Universe { get; }
        public byte[] Slots { get; } = new byte[DmxInputHub.SlotCount];
        public bool Dirty { get; set; }
    }
}
