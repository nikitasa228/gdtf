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
        Array.Empty<UniverseStatistics>());
}
