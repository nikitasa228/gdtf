namespace Candela.Protocols;

/// <summary>Receive statistics of one internal (1-based) universe, for the external-control panel.</summary>
/// <param name="Universe">Internal 1-based universe number (after the Art-Net offset).</param>
/// <param name="Packets">Accepted packets for this universe since the protocol started.</param>
/// <param name="SequenceErrors">Packets dropped because they arrived out of order (sACN/Art-Net sequence rule).</param>
/// <param name="LastPacketUtc">When the last accepted packet arrived (UTC).</param>
/// <param name="PacketsPerSecond">Measured packet rate over the last second (0 when nothing arrived for 2 s).</param>
/// <param name="LastSource">Who sent the last packet (sender address, source name).</param>
public sealed record UniverseStatistics(
    int Universe,
    long Packets,
    long SequenceErrors,
    DateTime LastPacketUtc,
    double PacketsPerSecond,
    string? LastSource);

/// <summary>A snapshot of one protocol's state and counters (immutable; take a new one to refresh).</summary>
/// <param name="ProtocolId">Id of the protocol (see <see cref="ProtocolIds"/>).</param>
/// <param name="State">Run state.</param>
/// <param name="Message">Human summary, e.g. "Listening on 0.0.0.0:6454 (eth0 192.168.1.20)".</param>
/// <param name="LastError">The most recent error message, or null. Kept after a stop so the user can read it.</param>
/// <param name="Warnings">Non-fatal problems since the last start (multicast join failed, …).</param>
/// <param name="Packets">Accepted packets since the last start.</param>
/// <param name="BadPackets">Malformed packets (wrong header, impossible lengths, …) since the last start.</param>
/// <param name="IgnoredPackets">Well-formed packets that were not for Candela (unsubscribed universe, other opcode,
/// non-zero start code, out-of-order sequence, preview data).</param>
/// <param name="LastPacketUtc">When the last accepted packet arrived (UTC), or null.</param>
/// <param name="LastBadPacket">Reason and sender of the most recent malformed packet, or null.</param>
/// <param name="Universes">Per-universe statistics, sorted by universe.</param>
public sealed record ProtocolStatus(
    string ProtocolId,
    ProtocolState State,
    string Message,
    string? LastError,
    IReadOnlyList<string> Warnings,
    long Packets,
    long BadPackets,
    long IgnoredPackets,
    DateTime? LastPacketUtc,
    string? LastBadPacket,
    IReadOnlyList<UniverseStatistics> Universes)
{
    private readonly ProtocolText? _text;

    /// <summary>
    /// <see cref="Message"/> as a localisable text: a stable key (<see cref="ProtocolMessages"/>) with its arguments,
    /// and the English text (equal to <see cref="Message"/>).
    /// </summary>
    public ProtocolText Text
    {
        get => _text ?? ProtocolText.Literal(Message);
        init => _text = value;
    }

    /// <summary><see cref="LastError"/> as a localisable text, or null when there is no error.</summary>
    public ProtocolText? ErrorText { get; init; }

    /// <summary>Why the protocol is in <see cref="ProtocolState.Error"/> or <see cref="ProtocolState.Unavailable"/>
    /// (<see cref="ProtocolErrorCode.None"/> otherwise); kept with <see cref="LastError"/> after a stop.</summary>
    public ProtocolErrorCode ErrorCode { get; init; }

    /// <summary><see cref="Warnings"/> as localisable texts (same order).</summary>
    public IReadOnlyList<ProtocolText> WarningTexts { get; init; } = Array.Empty<ProtocolText>();

    /// <summary>
    /// Where the protocol receives while running: the bound UDP endpoint (<c>0.0.0.0:6454</c>) or the device (serial
    /// port name); null while stopped.
    /// </summary>
    public string? BoundEndpoint { get; init; }

    /// <summary>The selected network interface (<c>eth0 192.168.1.20</c>), or null for all interfaces / not a network protocol.</summary>
    public string? NetworkInterface { get; init; }

    /// <summary>
    /// Universes the protocol serves while running: announced (Art-Net), subscribed (sACN), mapped by its patterns (OSC),
    /// 1 for a DMX widget, 0 for tracking protocols.
    /// </summary>
    public int UniverseCount { get; init; }

    /// <summary>The status of a protocol that has never been started.</summary>
    public static ProtocolStatus NotStarted(string protocolId) => new(
        protocolId,
        ProtocolState.Stopped,
        "Stopped",
        null,
        Array.Empty<string>(),
        0,
        0,
        0,
        null,
        null,
        Array.Empty<UniverseStatistics>())
    {
        Text = ProtocolText.Create(ProtocolMessages.Stopped),
    };
}

/// <summary>Why a protocol could not start or stopped working (stable values; the editor shows a localised hint).</summary>
public enum ProtocolErrorCode
{
    /// <summary>No error.</summary>
    None,

    /// <summary>The protocol is planned or needs a vendor SDK, so it cannot be started.</summary>
    Unavailable,

    /// <summary>A setting is invalid (port number, multicast group, universe, no serial port chosen).</summary>
    InvalidSettings,

    /// <summary>Another protocol of Candela is set to the same UDP port.</summary>
    PortConflict,

    /// <summary>The UDP port is used by another program (or the system denied it).</summary>
    PortInUse,

    /// <summary>The address to bind to is not assigned to this computer.</summary>
    AddressNotAvailable,

    /// <summary>The selected network interface does not exist (cable unplugged, adapter disabled).</summary>
    InterfaceNotFound,

    /// <summary>Another socket error while opening the port.</summary>
    SocketError,

    /// <summary>The socket failed while receiving.</summary>
    SocketFailed,

    /// <summary>The device (serial port) could not be opened.</summary>
    DeviceNotFound,

    /// <summary>The device was opened but does not answer.</summary>
    DeviceNotResponding,

    /// <summary>The device was unplugged or failed while running.</summary>
    DeviceDisconnected,

    /// <summary>An unexpected failure while starting.</summary>
    StartFailed,
}

/// <summary>
/// A status text the editor can translate: <see cref="Key"/> selects a template (<see cref="ProtocolMessages"/>; the
/// English templates use <c>{0}</c>, <c>{1}</c> … for <see cref="Arguments"/>), <see cref="English"/> is the ready
/// English text. Arguments are language-neutral values: numbers, addresses, ports, names and system messages.
/// </summary>
/// <param name="Key">Stable message key, e.g. <c>artnet.listening</c>.</param>
/// <param name="Arguments">Values of the placeholders, formatted with the invariant culture.</param>
/// <param name="English">The text in English.</param>
public sealed record ProtocolText(string Key, IReadOnlyList<string> Arguments, string English)
{
    /// <summary>A text from the template of <paramref name="key"/> (see <see cref="ProtocolMessages.Template"/>).</summary>
    public static ProtocolText Create(string key, params object?[] arguments)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(arguments);
        var args = new string[arguments.Length];
        for (int i = 0; i < args.Length; i++) args[i] = Convert.ToString(arguments[i], System.Globalization.CultureInfo.InvariantCulture) ?? "";
        return new ProtocolText(key, args, ProtocolMessages.Format(key, args));
    }

    /// <summary>A text without a template of its own (<see cref="ProtocolMessages.Literal"/>): its only argument is the English text.</summary>
    public static ProtocolText Literal(string english) => new(ProtocolMessages.Literal, new[] { english ?? "" }, english ?? "");

    /// <summary>Equal when key, arguments and English text are equal.</summary>
    public bool Equals(ProtocolText? other) =>
        other is not null && Key == other.Key && English == other.English && Arguments.SequenceEqual(other.Arguments);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Key, English);

    /// <inheritdoc />
    public override string ToString() => English;
}

/// <summary>
/// The keys of the protocols' status texts with their English templates (<c>{0}</c>, <c>{1}</c> … are the
/// <see cref="ProtocolText.Arguments"/>). Keys ending in <c>.all</c> are the variants for "all interfaces"; the others
/// name the interface as <c>{name} {address}</c>. Keys are stable: the editor's translations refer to them.
/// </summary>
public static class ProtocolMessages
{
    /// <summary>A text without a template of its own; <c>{0}</c> is the English text (fallback, untranslatable).</summary>
    public const string Literal = "protocol.text";

    /// <summary>"Stopped".</summary>
    public const string Stopped = "protocol.stopped";

    /// <summary>"{0} is not available: {1}" — protocol name, support reason.</summary>
    public const string Unavailable = "protocol.unavailable";

    /// <summary>"{0} failed to start: {1}" — protocol name, system message.</summary>
    public const string StartFailed = "protocol.start-failed";

    /// <summary>"{0} and {1} are both set to UDP port {2}; choose different ports."</summary>
    public const string PortConflict = "protocol.port-conflict";

    /// <summary>"{0} port {1} is not a valid UDP port (0–65535)." — protocol label, port.</summary>
    public const string InvalidPort = "udp.invalid-port";

    /// <summary>The UDP port is used by another program — protocol label, port.</summary>
    public const string PortInUse = "udp.port-in-use";

    /// <summary>"{0}: the address {1} is not assigned to any network interface of this computer."</summary>
    public const string AddressNotAvailable = "udp.address-not-available";

    /// <summary>"{0}: cannot open UDP {1}:{2} ({3}: {4})." — protocol label, address, port, socket error, system message.</summary>
    public const string BindFailed = "udp.bind-failed";

    /// <summary>"{0} socket failed: {1}" — protocol label, system message.</summary>
    public const string SocketFailed = "udp.socket-failed";

    /// <summary>The selected network interface was not found — selector, list of available interfaces.</summary>
    public const string InterfaceNotFound = "network.interface-not-found";

    /// <summary>The selected network interface was not found and the computer has none — selector.</summary>
    public const string InterfaceNotFoundNone = "network.interface-not-found.none";

    /// <summary>"Listening on UDP {0} ({1} {2})" — port, interface name, address (OSC, Eos, PosiStageNet).</summary>
    public const string Listening = "udp.listening";

    /// <summary>"Listening on UDP {0} (all interfaces)".</summary>
    public const string ListeningAll = "udp.listening.all";

    /// <summary>Art-Net running — port, interface name, address, announced universes, node name.</summary>
    public const string ArtNetListening = "artnet.listening";

    /// <summary>Art-Net running on all interfaces — port, announced universes, node name.</summary>
    public const string ArtNetListeningAll = "artnet.listening.all";

    /// <summary>Subscribed universes that cannot be expressed as Art-Net port-addresses — count, offset.</summary>
    public const string ArtNetUnaddressable = "artnet.unaddressable-universes";

    /// <summary>More than 1020 subscribed universes — count.</summary>
    public const string ArtNetTooManyUniverses = "artnet.too-many-universes";

    /// <summary>Art-Net stream table full — limit.</summary>
    public const string ArtNetStreamTableFull = "artnet.stream-table-full";

    /// <summary>sACN running with multicast — port, interface name, address, joined, subscribed.</summary>
    public const string SacnListening = "sacn.listening";

    /// <summary>sACN running with multicast on all interfaces — port, joined, subscribed.</summary>
    public const string SacnListeningAll = "sacn.listening.all";

    /// <summary>sACN running unicast only — port, interface name, address.</summary>
    public const string SacnUnicast = "sacn.listening-unicast";

    /// <summary>sACN running unicast only on all interfaces — port.</summary>
    public const string SacnUnicastAll = "sacn.listening-unicast.all";

    /// <summary>More universes than multicast joins — subscribed, limit.</summary>
    public const string SacnMulticastLimit = "sacn.multicast-limit";

    /// <summary>Multicast joins failed — count, universes, system message.</summary>
    public const string SacnMulticastFailed = "sacn.multicast-failed";

    /// <summary>The synchronisation universe could not be joined — universe, system message.</summary>
    public const string SacnSyncJoinFailed = "sacn.sync-join-failed";

    /// <summary>sACN stream table full — limit.</summary>
    public const string SacnStreamTableFull = "sacn.stream-table-full";

    /// <summary>"OSC pattern ignored: {0}." — parser message.</summary>
    public const string OscPatternIgnored = "osc.pattern-ignored";

    /// <summary>"OSC keep-alive failed: {0}" — system message.</summary>
    public const string OscKeepAliveFailed = "osc.keepalive-failed";

    /// <summary>OSC held-universe table full — limit.</summary>
    public const string OscHeldTableFull = "osc.held-table-full";

    /// <summary>PosiStageNet running with multicast — port, interface name, address, group.</summary>
    public const string PsnListening = "psn.listening";

    /// <summary>PosiStageNet running with multicast on all interfaces — port, group.</summary>
    public const string PsnListeningAll = "psn.listening.all";

    /// <summary>"Listening on UDP {0}; {1} tracker(s)" — port, trackers.</summary>
    public const string PsnTrackers = "psn.trackers";

    /// <summary>"PosiStageNet group '{0}' is not an IPv4 multicast address."</summary>
    public const string PsnInvalidGroup = "psn.invalid-group";

    /// <summary>The PosiStageNet multicast group could not be joined — group, system message.</summary>
    public const string PsnMulticastFailed = "psn.multicast-failed";

    /// <summary>"Receiving DMX from {0} into universe {1}" — serial port, universe.</summary>
    public const string EnttecReceiving = "enttec.receiving";

    /// <summary>No serial port chosen — available ports.</summary>
    public const string EnttecNoPort = "enttec.no-port";

    /// <summary>No serial port chosen and none found.</summary>
    public const string EnttecNoPortNone = "enttec.no-port.none";

    /// <summary>"EntTec universe {0} is invalid; universes start at 1."</summary>
    public const string EnttecInvalidUniverse = "enttec.invalid-universe";

    /// <summary>The widget could not be opened — port, system message.</summary>
    public const string EnttecOpenFailed = "enttec.open-failed";

    /// <summary>"The EntTec widget on {0} does not accept commands: {1}".</summary>
    public const string EnttecNotResponding = "enttec.not-responding";

    /// <summary>"EntTec widget on {0}: write failed ({1})."</summary>
    public const string EnttecWriteFailed = "enttec.write-failed";

    /// <summary>"EntTec widget on {0} disconnected: {1}".</summary>
    public const string EnttecDisconnected = "enttec.disconnected";

    /// <summary>"EntTec widget on {0} failed: {1}: {2}" — port, exception type, system message.</summary>
    public const string EnttecFailed = "enttec.failed";

    private static readonly Dictionary<string, string> Templates = new(StringComparer.Ordinal)
    {
        [Literal] = "{0}",
        [Stopped] = "Stopped",
        [Unavailable] = "{0} is not available: {1}",
        [StartFailed] = "{0} failed to start: {1}",
        [PortConflict] = "{0} and {1} are both set to UDP port {2}; choose different ports.",
        [InvalidPort] = "{0} port {1} is not a valid UDP port (0–65535).",
        [PortInUse] = "{0}: UDP port {1} is already in use by another program on this computer (another visualiser, " +
                      "console offline software or node software). Close it, or select a specific network interface.",
        [AddressNotAvailable] = "{0}: the address {1} is not assigned to any network interface of this computer.",
        [BindFailed] = "{0}: cannot open UDP {1}:{2} ({3}: {4}).",
        [SocketFailed] = "{0} socket failed: {1}",
        [InterfaceNotFound] = "Network interface '{0}' was not found (cable unplugged or adapter disabled?). Available: {1}.",
        [InterfaceNotFoundNone] = "Network interface '{0}' was not found (cable unplugged or adapter disabled?). Available: none found.",
        [Listening] = "Listening on UDP {0} ({1} {2})",
        [ListeningAll] = "Listening on UDP {0} (all interfaces)",
        [ArtNetListening] = "Listening on UDP {0} ({1} {2}); announcing {3} universe(s) as node '{4}'",
        [ArtNetListeningAll] = "Listening on UDP {0} (all interfaces); announcing {1} universe(s) as node '{2}'",
        [ArtNetUnaddressable] = "{0} subscribed universe(s) cannot be expressed as an Art-Net port-address with offset {1} and are not announced.",
        [ArtNetTooManyUniverses] = "Only the first 1020 of {0} subscribed universes fit into 255 ArtPollReply binds.",
        [ArtNetStreamTableFull] = "More than {0} Art-Net streams (sender, port, universe) are active; new ones are ignored.",
        [SacnListening] = "Listening on UDP {0} ({1} {2}); multicast joined for {3} of {4} universe(s)",
        [SacnListeningAll] = "Listening on UDP {0} (all interfaces); multicast joined for {1} of {2} universe(s)",
        [SacnUnicast] = "Listening on UDP {0} ({1} {2}); unicast only",
        [SacnUnicastAll] = "Listening on UDP {0} (all interfaces); unicast only",
        [SacnMulticastLimit] = "{0} universes subscribed; multicast is joined for the first {1} only. Send the others by unicast.",
        [SacnMulticastFailed] = "Could not join sACN multicast for {0} universe(s) ({1}): {2}. Unicast sACN still works — set the console " +
                                "to unicast to this computer's IP, or check the network interface.",
        [SacnSyncJoinFailed] = "Could not join the sACN synchronisation universe {0}: {1}. Synchronised data is output unsynchronised.",
        [SacnStreamTableFull] = "More than {0} sACN streams (source, universe) are active; new ones are ignored.",
        [OscPatternIgnored] = "OSC pattern ignored: {0}.",
        [OscKeepAliveFailed] = "OSC keep-alive failed: {0}",
        [OscHeldTableFull] = "OSC already holds {0} universes; messages for further universes are ignored.",
        [PsnListening] = "Listening on UDP {0} ({1} {2}), multicast {3}",
        [PsnListeningAll] = "Listening on UDP {0} (all interfaces), multicast {1}",
        [PsnTrackers] = "Listening on UDP {0}; {1} tracker(s)",
        [PsnInvalidGroup] = "PosiStageNet group '{0}' is not an IPv4 multicast address.",
        [PsnMulticastFailed] = "Could not join PosiStageNet multicast {0}: {1}. Unicast PSN to this computer still works.",
        [EnttecReceiving] = "Receiving DMX from {0} into universe {1}",
        [EnttecNoPort] = "Choose the serial port of the EntTec DMX USB Pro (available: {0}).",
        [EnttecNoPortNone] = "Choose the serial port of the EntTec DMX USB Pro (no serial ports found).",
        [EnttecInvalidUniverse] = "EntTec universe {0} is invalid; universes start at 1.",
        [EnttecOpenFailed] = "Cannot open the EntTec widget on {0}: {1} (is it plugged in, and not used by another program?)",
        [EnttecNotResponding] = "The EntTec widget on {0} does not accept commands: {1}",
        [EnttecWriteFailed] = "EntTec widget on {0}: write failed ({1}).",
        [EnttecDisconnected] = "EntTec widget on {0} disconnected: {1}",
        [EnttecFailed] = "EntTec widget on {0} failed: {1}: {2}",
    };

    /// <summary>Every key with its English template (for translators and tests).</summary>
    public static IReadOnlyDictionary<string, string> All => Templates;

    /// <summary>The English template of <paramref name="key"/>, or null for an unknown key.</summary>
    public static string? Template(string key) => Templates.TryGetValue(key, out var t) ? t : null;

    /// <summary>
    /// Fills the English template of <paramref name="key"/> with <paramref name="arguments"/>; an unknown key (or a
    /// template that does not fit the arguments) gives the arguments joined, never an exception.
    /// </summary>
    public static string Format(string key, IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (Template(key) is { } template)
        {
            try
            {
                return string.Format(System.Globalization.CultureInfo.InvariantCulture, template, arguments.Cast<object>().ToArray());
            }
            catch (FormatException)
            {
                // fall through: a template that does not fit its arguments must not break a status report
            }
        }
        return string.Join(" ", arguments);
    }
}
